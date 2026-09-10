using PrMonitor.Services;
using Xunit;

namespace PrMonitor.Tests.Services;

public class MemoryDiagnosticsTests
{
    [Fact]
    public void Capture_ReturnsPlausibleProcessCounters()
    {
        var snapshot = MemoryDiagnostics.Capture();

        Assert.True(snapshot.ManagedHeapBytes > 0);
        Assert.True(snapshot.WorkingSetBytes > 0);
        Assert.True(snapshot.HandleCount > 0);
        Assert.True(snapshot.ThreadCount > 0);
        Assert.True(snapshot.Gen0Collections >= 0);
    }

    [Fact]
    public void ToString_ContainsTheKeyCountersInMegabytes()
    {
        var text = MemoryDiagnostics.Capture().ToString();

        Assert.Contains("heap=", text);
        Assert.Contains("workingSet=", text);
        Assert.Contains("gdi=", text);
        Assert.Contains("MB", text);
    }

    [Fact]
    public void ExceedsWarnThreshold_ForANormallySizedProcess_IsFalse()
    {
        Assert.False(MemoryDiagnostics.Capture().ExceedsWarnThreshold);
    }

    [Fact]
    public void TrimMemory_DoesNotThrow()
    {
        MemoryDiagnostics.TrimMemory(DiagnosticsLogger.Null, "test");
    }

    [Fact]
    public void CaptureNativeBreakdown_AccountsForTheProcessAddressSpace()
    {
        var breakdown = MemoryDiagnostics.CaptureNativeBreakdown();

        Assert.True(breakdown.RegionCount > 0);
        Assert.True(breakdown.PrivateCommittedBytes > 0);
        Assert.True(breakdown.ImageCommittedBytes > 0);
        Assert.True(breakdown.LargestPrivateAllocationBytes > 0);
        Assert.True(breakdown.LargestPrivateAllocationBytes <= breakdown.PrivateCommittedBytes);
    }

    [Fact]
    public void CaptureNativeBreakdown_PrivateCommitIsInTheSameOrderAsProcessPrivateBytes()
    {
        var breakdown = MemoryDiagnostics.CaptureNativeBreakdown();
        var snapshot = MemoryDiagnostics.Capture();

        // Private bytes counts committed private pages, so the walk must not be wildly off.
        Assert.True(breakdown.PrivateCommittedBytes <= snapshot.PrivateBytes * 4);
    }
}
