using System.Text;
using System.IO;

namespace PrMonitor.Services;

/// <summary>
/// Minimal file logger for local diagnostics.
/// </summary>
public sealed class DiagnosticsLogger
{
    private const long MaxLogFileBytes = 1 * 1024 * 1024;
    private const int MaxArchivedLogFiles = 3;

    private readonly object _sync = new();
    private readonly string? _logFilePath;

    /// <summary>
    /// A no-op logger that discards all output. Use in unit tests to avoid writing to the real log file.
    /// </summary>
    public static readonly DiagnosticsLogger Null = new(logFilePath: null);

    public DiagnosticsLogger()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var logsDirectory = System.IO.Path.Combine(appData, "pr-monitor", "logs");
        System.IO.Directory.CreateDirectory(logsDirectory);
        _logFilePath = System.IO.Path.Combine(logsDirectory, "pr-monitor.log");
    }

    private DiagnosticsLogger(string? logFilePath)
    {
        _logFilePath = logFilePath;
    }

    public string? LogFilePath => _logFilePath;

    /// <summary>
    /// When false, <see cref="Info"/> calls are silently dropped. Warn/Error are always written.
    /// </summary>
    public bool VerboseLogging { get; set; } = false;

    public void Info(string message) { if (VerboseLogging) Write("INFO", message); }

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    public void Error(string message, Exception ex) => Write("ERROR", $"{message} | {SummarizeException(ex)}");

    public static string SummarizeException(Exception ex)
    {
        var builder = new StringBuilder();
        builder.Append(ex.GetType().Name);
        builder.Append(": ");
        builder.Append(ex.Message);

        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            var stackLines = ex.StackTrace
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Take(4);
            builder.Append(" | Stack: ");
            builder.Append(string.Join(" <= ", stackLines));
        }

        return builder.ToString();
    }

    private void Write(string level, string message)
    {
        if (_logFilePath is null)
            return;
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}";
            lock (_sync)
            {
                RotateIfNeeded();
                System.IO.File.AppendAllText(_logFilePath, line);
            }
        }
        catch
        {
            // Diagnostics must never affect app behavior.
        }
    }

    private void RotateIfNeeded()
    {
        if (_logFilePath is null || !System.IO.File.Exists(_logFilePath))
        {
            return;
        }

        var fileInfo = new FileInfo(_logFilePath);
        if (fileInfo.Length < MaxLogFileBytes)
        {
            return;
        }

        for (var index = MaxArchivedLogFiles; index >= 1; index--)
        {
            var sourcePath = index == 1 ? _logFilePath : $"{_logFilePath}.{index - 1}";
            var destinationPath = $"{_logFilePath}.{index}";

            if (!System.IO.File.Exists(sourcePath))
            {
                continue;
            }

            if (System.IO.File.Exists(destinationPath))
            {
                System.IO.File.Delete(destinationPath);
            }

            System.IO.File.Move(sourcePath, destinationPath);
        }
    }
}