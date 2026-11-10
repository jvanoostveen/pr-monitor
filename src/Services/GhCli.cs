using System.Diagnostics;
using System.Text;

namespace PrMonitor.Services;

/// <summary>
/// Starts the <c>gh</c> CLI the one way this app does it: arguments through
/// <see cref="ProcessStartInfo.ArgumentList"/>, UTF-8 output, a hard timeout and a
/// process-tree kill, so a hung <c>gh</c> can never outlive its caller's wait.
/// Logging of non-zero exits is left to the caller, which knows how bad a failure is.
/// </summary>
internal static class GhCli
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Runs <c>gh</c> and returns its output. stdout is returned on a non-zero exit too:
    /// <c>gh api graphql</c> exits 1 whenever the body has an <c>errors</c> array, even when it
    /// also carries usable partial <c>data</c>. Returns <c>(null, null, -1)</c> when gh cannot be
    /// started, times out, or <paramref name="ct"/> is cancelled.
    /// </summary>
    internal static async Task<(string? Output, string? Stderr, int ExitCode)> RunAsync(
        DiagnosticsLogger logger, string logPrefix, CancellationToken ct, params string[] arguments)
    {
        try
        {
            using var process = Start(arguments);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return (await outputTask, await stderrTask, process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(process, logger, logPrefix, arguments);
                return (null, null, -1);
            }
        }
        catch (Exception ex)
        {
            // gh CLI not installed or not on PATH
            logger.Error($"{logPrefix} failed to start gh process.", ex);
            return (null, null, -1);
        }
    }

    /// <summary>Starts <c>gh</c> with redirected UTF-8 stdout/stderr. The caller must drain both streams.</summary>
    internal static Process Start(params string[] arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "gh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };
        foreach (var arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);

        try
        {
            process.Start();
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal static void KillProcessTree(Process process, DiagnosticsLogger logger, string logPrefix, string[] arguments)
    {
        try
        {
            if (!process.HasExited)
            {
                logger.Warn($"{logPrefix}: killing unresponsive 'gh {string.Join(" ", arguments)}'.");
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"{logPrefix}: failed to kill gh process: {DiagnosticsLogger.SummarizeException(ex)}");
        }
    }
}
