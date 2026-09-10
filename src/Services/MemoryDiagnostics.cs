using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace PrMonitor.Services;

/// <summary>
/// Captures managed-heap, process and GDI/USER handle metrics so memory growth can be
/// diagnosed from the log file on machines we cannot profile directly.
/// </summary>
public static class MemoryDiagnostics
{
    /// <summary>Working set above which a snapshot is logged as a warning regardless of verbose logging.</summary>
    private const long WorkingSetWarnBytes = 500L * 1024 * 1024;

    /// <summary>GDI object count above which a snapshot is logged as a warning (Windows limit is 10 000).</summary>
    private const int GdiObjectWarnCount = 2000;

    // uiFlags: 0 = GDI objects, 1 = USER objects
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualQuery(IntPtr lpAddress, out MemoryBasicInformation lpBuffer, IntPtr dwLength);

    // x64 layout: the two __alignment fields are the compiler padding around RegionSize/Type.
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public uint __alignment1;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint __alignment2;
    }

    private const uint MemCommit = 0x1000;
    private const uint MemPrivate = 0x20000;
    private const uint MemMapped = 0x40000;
    private const uint MemImage = 0x1000000;

    /// <summary>
    /// Where the process's committed address space actually lives. The managed heap is only a
    /// fraction of it, so this is what distinguishes a GC problem from a native/unmanaged leak.
    /// </summary>
    public readonly record struct NativeBreakdown(
        long PrivateCommittedBytes,
        long MappedCommittedBytes,
        long ImageCommittedBytes,
        long ReservedBytes,
        int RegionCount,
        long LargestPrivateAllocationBytes,
        int PrivateAllocationCount)
    {
        public override string ToString()
        {
            static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):F1}MB";

            return $"privateCommit={Mb(PrivateCommittedBytes)} mapped={Mb(MappedCommittedBytes)} "
                 + $"image={Mb(ImageCommittedBytes)} reserved={Mb(ReservedBytes)} regions={RegionCount} "
                 + $"privateAllocs={PrivateAllocationCount} largestPrivateAlloc={Mb(LargestPrivateAllocationBytes)}";
        }
    }

    /// <summary>Walks the process address space with VirtualQuery and buckets committed pages by region type.</summary>
    public static NativeBreakdown CaptureNativeBreakdown()
    {
        long privateCommitted = 0, mappedCommitted = 0, imageCommitted = 0, reserved = 0;
        int regions = 0;

        // Committed bytes per allocation base, so one runaway allocator stands out from normal churn.
        var privateAllocations = new Dictionary<IntPtr, long>();

        var address = IntPtr.Zero;
        var infoSize = (IntPtr)Marshal.SizeOf<MemoryBasicInformation>();

        while (VirtualQuery(address, out var info, infoSize) != IntPtr.Zero)
        {
            regions++;
            var size = (long)info.RegionSize;
            if (size <= 0) break;

            if (info.State == MemCommit)
            {
                switch (info.Type)
                {
                    case MemPrivate:
                        privateCommitted += size;
                        privateAllocations.TryGetValue(info.AllocationBase, out var current);
                        privateAllocations[info.AllocationBase] = current + size;
                        break;
                    case MemMapped:
                        mappedCommitted += size;
                        break;
                    case MemImage:
                        imageCommitted += size;
                        break;
                }
            }
            else if (info.State != 0x10000) // not MEM_FREE
            {
                reserved += size;
            }

            var next = (long)address + size;
            if (next <= (long)address) break;
            address = (IntPtr)next;
        }

        long largest = 0;
        foreach (var bytes in privateAllocations.Values)
            if (bytes > largest) largest = bytes;

        return new NativeBreakdown(privateCommitted, mappedCommitted, imageCommitted, reserved,
            regions, largest, privateAllocations.Count);
    }

    public readonly record struct Snapshot(
        long ManagedHeapBytes,
        long CommittedBytes,
        long FragmentedBytes,
        long LohBytes,
        long PohBytes,
        long WorkingSetBytes,
        long PrivateBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        int HandleCount,
        int ThreadCount,
        uint GdiObjects,
        uint UserObjects)
    {
        public bool ExceedsWarnThreshold => WorkingSetBytes > WorkingSetWarnBytes || GdiObjects > GdiObjectWarnCount;

        public override string ToString()
        {
            static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):F1}MB";

            return $"heap={Mb(ManagedHeapBytes)} committed={Mb(CommittedBytes)} fragmented={Mb(FragmentedBytes)} "
                 + $"loh={Mb(LohBytes)} poh={Mb(PohBytes)} workingSet={Mb(WorkingSetBytes)} private={Mb(PrivateBytes)} "
                 + $"gc=[{Gen0Collections}/{Gen1Collections}/{Gen2Collections}] handles={HandleCount} threads={ThreadCount} "
                 + $"gdi={GdiObjects} user={UserObjects}";
        }
    }

    public static Snapshot Capture()
    {
        var info = GC.GetGCMemoryInfo();

        // GenerationInfo is indexed gen0, gen1, gen2, LOH, POH.
        long loh = info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : 0;
        long poh = info.GenerationInfo.Length > 4 ? info.GenerationInfo[4].SizeAfterBytes : 0;

        using var process = Process.GetCurrentProcess();
        var handle = process.Handle;

        return new Snapshot(
            ManagedHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
            CommittedBytes: info.TotalCommittedBytes,
            FragmentedBytes: info.FragmentedBytes,
            LohBytes: loh,
            PohBytes: poh,
            WorkingSetBytes: process.WorkingSet64,
            PrivateBytes: process.PrivateMemorySize64,
            Gen0Collections: GC.CollectionCount(0),
            Gen1Collections: GC.CollectionCount(1),
            Gen2Collections: GC.CollectionCount(2),
            HandleCount: process.HandleCount,
            ThreadCount: process.Threads.Count,
            GdiObjects: GetGuiResources(handle, 0),
            UserObjects: GetGuiResources(handle, 1));
    }

    /// <summary>
    /// Logs a snapshot at INFO level, or at WARN when it exceeds the alarm thresholds
    /// (so problematic installations report themselves without verbose logging enabled).
    /// </summary>
    public static void Log(DiagnosticsLogger logger, string context)
    {
        try
        {
            var snapshot = Capture();
            var line = $"MemoryDiagnostics [{context}] {snapshot} | {CaptureNativeBreakdown()}";
            if (snapshot.ExceedsWarnThreshold)
                logger.Warn(line);
            else
                logger.Info(line);
        }
        catch (Exception ex)
        {
            logger.Warn($"MemoryDiagnostics capture failed: {DiagnosticsLogger.SummarizeException(ex)}");
        }
    }

    /// <summary>
    /// Compacts the large object heap, runs a full collection and releases the trimmed pages
    /// back to the OS. Intended for idle moments only — it is a blocking gen2 collection.
    /// </summary>
    public static void TrimMemory(DiagnosticsLogger logger, string context)
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            using var process = Process.GetCurrentProcess();
            EmptyWorkingSet(process.Handle);

            Log(logger, $"{context}:after-trim");
        }
        catch (Exception ex)
        {
            logger.Warn($"MemoryDiagnostics trim failed: {DiagnosticsLogger.SummarizeException(ex)}");
        }
    }
}
