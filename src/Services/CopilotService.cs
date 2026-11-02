using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PrMonitor.Models;

namespace PrMonitor.Services;

/// <summary>
/// Analyzes CI failure logs for flakiness using the GitHub Models API (gpt-4o-mini).
/// Uses the current user's GitHub token via 'gh auth token'.
/// </summary>
public sealed class CopilotService : IFlakinessAnalyzer
{
    private readonly DiagnosticsLogger _logger;
    private static readonly HttpClient _http = new(new System.Net.Http.SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
        MaxResponseContentBufferSize = 1024 * 1024, // 1 MB cap
    };

    private const string ModelsEndpoint = "https://models.github.ai/inference/chat/completions";
    private const string Model = "openai/gpt-4o-mini";

    public CopilotService(DiagnosticsLogger logger)
    {
        _logger = logger;
    }

    public string Name => "GitHub Models";

    /// <summary>
    /// Analyzes the failure context and returns whether the failure is likely flaky,
    /// a short rationale, and any suggested regex rules to detect the same flakiness in future.
    /// </summary>
    public async Task<FlakinessAnalysisResult> AnalyzeFlakiness(FailureContext context, string? customHints = null)
    {
        try
        {
            var token = await GetBearerTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.Warn("CopilotService: could not retrieve GitHub token via 'gh auth token'.");
                return FlakinessPrompt.Indeterminate("Could not retrieve GitHub token.");
            }

            var requestBody = new
            {
                model = Model,
                messages = new[]
                {
                    new { role = "system", content = FlakinessPrompt.BuildSystemPrompt(customHints) },
                    new { role = "user", content = FlakinessPrompt.BuildUserMessage(context) }
                },
                temperature = 0.1,
                max_tokens = 512
            };

            var (response, responseBody) = await SendChatCompletionAsync(token, requestBody);

            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn($"CopilotService: GitHub Models API returned {(int)response.StatusCode}: {responseBody.Trim()}");
                if (IsContentFilterBlock(responseBody))
                {
                    _logger.Warn("CopilotService: jailbreak content filter triggered by CI log — skipping analysis.");
                    return FlakinessPrompt.Indeterminate("CI log content triggered the content filter.");
                }
                return FlakinessPrompt.Indeterminate($"API returned {(int)response.StatusCode}.");
            }

            return ParseResponse(responseBody);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Transient connectivity problem — not actionable, so keep it out of the error log.
            _logger.Warn($"CopilotService.AnalyzeFlakiness: could not reach the model API — {ex.Message}");
            return FlakinessPrompt.Indeterminate("Could not reach the model API.");
        }
        catch (Exception ex)
        {
            _logger.Error("CopilotService.AnalyzeFlakiness failed.", ex);
            return FlakinessPrompt.Indeterminate("Analysis failed due to an exception.");
        }
    }

    public async Task<AnalyzerCheckResult> CheckAsync()
    {
        try
        {
            var token = await GetBearerTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
                return new(false, "No GitHub token — run 'gh auth login'.");

            var requestBody = new
            {
                model = Model,
                messages = new[] { new { role = "user", content = "Reply with OK" } },
                max_tokens = 5
            };

            var (response, _) = await SendChatCompletionAsync(token, requestBody);
            return response.StatusCode switch
            {
                _ when response.IsSuccessStatusCode => new(true, "Ready — GitHub Models (gpt-4o-mini, free tier)"),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new(false, $"GitHub Models refused your token ({(int)response.StatusCode})."),
                HttpStatusCode.TooManyRequests => new(false, "GitHub Models rate limit reached — try again later."),
                _ => new(false, $"GitHub Models returned {(int)response.StatusCode}."),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, "Could not reach GitHub Models.");
        }
        catch (Exception ex)
        {
            _logger.Error("CopilotService.CheckAsync failed.", ex);
            return new(false, "Check failed — see the log.");
        }
    }

    private static async Task<(HttpResponseMessage Response, string Body)> SendChatCompletionAsync(string token, object requestBody)
    {
        var json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, ModelsEndpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.Dispose();
        return (response, body);
    }

    internal FlakinessAnalysisResult ParseResponse(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            return FlakinessPrompt.ParseVerdict(content);
        }
        catch (Exception ex)
        {
            _logger.Error("CopilotService: failed to parse model response.", ex);
            return FlakinessPrompt.Indeterminate("Failed to parse model response.");
        }
    }

    private async Task<string?> GetBearerTokenAsync()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch (Exception ex)
        {
            _logger.Error("CopilotService.GetBearerTokenAsync failed.", ex);
            return null;
        }
    }

    /// <summary>
    /// Returns true when a 400 response is caused by Azure OpenAI's content management
    /// filter (e.g. jailbreak detection triggered by raw CI log content).
    /// </summary>
    private static bool IsContentFilterBlock(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("code", out var code)
                && code.GetString() == "content_filter")
                return true;
        }
        catch { /* not JSON or unexpected shape — fall through */ }
        return false;
    }
}
