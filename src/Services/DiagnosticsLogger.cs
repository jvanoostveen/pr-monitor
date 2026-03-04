using System.Text;
using System.IO;

namespace PrMonitor.Services;

/// <summary>
/// Minimal file logger for local diagnostics.
/// </summary>
public sealed class DiagnosticsLogger
{
    private readonly object _sync = new();
    private readonly string _logFilePath;

    public DiagnosticsLogger()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var logsDirectory = System.IO.Path.Combine(appData, "pr-monitor", "logs");
        System.IO.Directory.CreateDirectory(logsDirectory);
        _logFilePath = System.IO.Path.Combine(logsDirectory, "pr-monitor.log");
    }

    public string LogFilePath => _logFilePath;

    public void Info(string message) => Write("INFO", message);

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
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}";
            lock (_sync)
            {
                System.IO.File.AppendAllText(_logFilePath, line);
            }
        }
        catch
        {
            // Diagnostics must never affect app behavior.
        }
    }
}