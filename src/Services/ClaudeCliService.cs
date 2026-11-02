using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PrMonitor.Models;

namespace PrMonitor.Services;

/// <summary>
/// Analyzes CI failure logs for flakiness with Claude (haiku) through the Claude Code CLI
/// ('claude -p'), using the user's own Claude Code login.
/// </summary>
public sealed class ClaudeCliService : IFlakinessAnalyzer
{
    private static readonly TimeSpan AnalyzeTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private const int FileNotFound = 2; // Win32 ERROR_FILE_NOT_FOUND from Process.Start

    // One claude process at a time: each is a full Node runtime, and one poll can report
    // several CI failures at once.
    private static readonly SemaphoreSlim _gate = new(1, 1);

    // An empty folder to run in, so no project CLAUDE.md or project settings are picked up.
    private static readonly string WorkingDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pr-monitor", "claude-cwd");

    private readonly DiagnosticsLogger _logger;

    public ClaudeCliService(DiagnosticsLogger logger)
    {
        _logger = logger;
    }

    public string Name => "Claude";

    public async Task<FlakinessAnalysisResult> AnalyzeFlakiness(FailureContext context, string? customHints = null)
    {
        try
        {
            var (_, stdout, stderr) = await RunAsync(
                FlakinessPrompt.BuildSystemPrompt(customHints), FlakinessPrompt.BuildUserMessage(context), AnalyzeTimeout);
            var result = ParseCliOutput(stdout);
            if (result.IsIndeterminate)
                _logger.Warn($"ClaudeCliService: {result.Rationale} {FirstLine(stderr)}".TrimEnd());
            return result;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == FileNotFound)
        {
            _logger.Warn("ClaudeCliService: 'claude' was not found on PATH.");
            return FlakinessPrompt.Indeterminate("Claude Code CLI not found on PATH.");
        }
        catch (TimeoutException ex)
        {
            _logger.Warn($"ClaudeCliService: {ex.Message}");
            return FlakinessPrompt.Indeterminate("Claude Code did not answer in time.");
        }
        catch (Exception ex)
        {
            _logger.Error("ClaudeCliService.AnalyzeFlakiness failed.", ex);
            return FlakinessPrompt.Indeterminate("Analysis failed due to an exception.");
        }
    }

    public async Task<AnalyzerCheckResult> CheckAsync()
    {
        try
        {
            var (exitCode, stdout, stderr) = await RunAsync(systemPrompt: null, "Reply with OK", CheckTimeout);
            var envelope = ReadEnvelope(stdout);
            if (exitCode == 0 && envelope is { IsError: false })
                return new(true, "Ready — Claude Code (haiku)");

            var reason = FirstLine(envelope?.Text) ?? FirstLine(stderr) ?? $"claude exited with code {exitCode}.";
            return new(false, reason);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == FileNotFound)
        {
            return new(false, "Claude Code CLI not found on PATH (restart PR Monitor after installing it).");
        }
        catch (TimeoutException)
        {
            return new(false, "Claude Code did not answer in time.");
        }
        catch (Exception ex)
        {
            _logger.Error("ClaudeCliService.CheckAsync failed.", ex);
            return new(false, "Check failed — see the log.");
        }
    }

    /// <summary>Parses the JSON that 'claude -p --output-format json' prints.</summary>
    internal FlakinessAnalysisResult ParseCliOutput(string stdout)
    {
        try
        {
            if (ReadEnvelope(stdout) is not { } envelope)
                throw new JsonException("Claude Code output has no 'result'.");
            if (envelope.IsError)
                return FlakinessPrompt.Indeterminate($"Claude Code returned an error: {envelope.Text}");
            return FlakinessPrompt.ParseVerdict(envelope.Text);
        }
        catch (Exception ex)
        {
            _logger.Error("ClaudeCliService: failed to parse Claude Code output.", ex);
            return FlakinessPrompt.Indeterminate("Failed to parse Claude Code output.");
        }
    }

    private static (bool IsError, string Text)? ReadEnvelope(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.String)
                return null;
            var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            return (isError, result.GetString()!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string? systemPrompt, string input, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("claude")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = WorkingDirectory,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        // --setting-sources does not stop the user's ~/.claude/CLAUDE.md from loading; this does.
        psi.Environment["CLAUDE_CODE_DISABLE_CLAUDE_MDS"] = "1";
        // No tools, MCP servers, user settings (hooks) or saved session: a single text-only answer.
        foreach (var arg in new[] { "-p", "--model", "haiku", "--output-format", "json", "--tools", "",
                     "--no-session-persistence", "--strict-mcp-config", "--setting-sources", "project" })
            psi.ArgumentList.Add(arg);
        if (systemPrompt is not null)
        {
            psi.ArgumentList.Add("--system-prompt");
            psi.ArgumentList.Add(systemPrompt);
        }

        Directory.CreateDirectory(WorkingDirectory);
        if (!await _gate.WaitAsync(timeout))
            throw new TimeoutException("Another claude run is still busy.");
        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start claude.");
            using var cts = new CancellationTokenSource(timeout);
            // Drain both pipes while writing stdin: the log excerpt is larger than a pipe buffer.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                try
                {
                    await proc.StandardInput.WriteAsync(input.AsMemory(), cts.Token);
                    proc.StandardInput.Close();
                }
                catch (IOException) { /* claude exited before reading stdin; its output says why */ }

                await proc.WaitForExitAsync(cts.Token);
                return (proc.ExitCode, await stdoutTask, await stderrTask);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
                throw new TimeoutException($"claude did not exit within {timeout.TotalSeconds:0} s.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? FirstLine(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim().Split('\n', 2)[0].Trim();
}
