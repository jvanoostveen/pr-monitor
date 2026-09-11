using System.Text.Json;
using PrMonitor.Models;
using PrMonitor.Services;
using Xunit;

namespace PrMonitor.Tests.Services;

public class GitHubServiceCheckParsingTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string FullResponse = """
        {
          "data": {
            "repository": {
              "pullRequest": {
                "commits": {
                  "nodes": [
                    {
                      "commit": {
                        "oid": "abc123",
                        "statusCheckRollup": {
                          "state": "FAILURE",
                          "contexts": {
                            "nodes": [
                              {
                                "__typename": "CheckRun",
                                "name": "Compile",
                                "status": "COMPLETED",
                                "conclusion": "SUCCESS",
                                "startedAt": "2026-09-11T10:00:00Z",
                                "completedAt": "2026-09-11T10:01:24Z",
                                "detailsUrl": "https://github.com/o/r/actions/runs/1/job/11",
                                "checkSuite": {
                                  "workflowRun": {
                                    "databaseId": 1,
                                    "workflow": { "name": "CI" }
                                  }
                                }
                              },
                              {
                                "__typename": "CheckRun",
                                "name": "Test",
                                "status": "IN_PROGRESS",
                                "conclusion": null,
                                "startedAt": "2026-09-11T10:00:00Z",
                                "completedAt": null,
                                "detailsUrl": "https://github.com/o/r/actions/runs/1/job/12",
                                "checkSuite": {
                                  "workflowRun": {
                                    "databaseId": 1,
                                    "workflow": { "name": "CI" }
                                  }
                                }
                              },
                              {
                                "__typename": "CheckRun",
                                "name": "Release Components",
                                "status": "COMPLETED",
                                "conclusion": "SKIPPED",
                                "startedAt": null,
                                "completedAt": null,
                                "detailsUrl": "https://github.com/o/r/actions/runs/1/job/13",
                                "checkSuite": { "workflowRun": null }
                              },
                              {
                                "__typename": "StatusContext",
                                "context": "legacy/build",
                                "state": "FAILURE",
                                "createdAt": "2026-09-11T10:00:00Z",
                                "targetUrl": "https://ci.example.com/build/7"
                              }
                            ]
                          }
                        }
                      }
                    }
                  ]
                }
              }
            }
          }
        }
        """;

    [Fact]
    public void ParsePrChecks_ReadsEveryContextNode()
    {
        var checks = GitHubService.ParsePrChecks(Parse(FullResponse));

        Assert.Equal(4, checks.Count);
        Assert.Equal(["Compile", "Test", "Release Components", "legacy/build"], checks.Select(c => c.Name));
    }

    [Fact]
    public void ParsePrChecks_MapsCheckRunStatesAndMetadata()
    {
        var checks = GitHubService.ParsePrChecks(Parse(FullResponse));

        var compile = checks[0];
        Assert.Equal(CheckRunState.Success, compile.State);
        Assert.Equal("CI", compile.WorkflowName);
        Assert.Equal(1, compile.WorkflowRunId);
        Assert.Equal("https://github.com/o/r/actions/runs/1/job/11", compile.Url);
        Assert.Equal(TimeSpan.FromSeconds(84), compile.Duration);

        Assert.Equal(CheckRunState.Running, checks[1].State);
        Assert.True(checks[1].IsInProgress);

        Assert.Equal(CheckRunState.Skipped, checks[2].State);
        Assert.True(checks[2].IsSkipped);
        Assert.Null(checks[2].Duration);
        Assert.Equal("", checks[2].WorkflowName);
    }

    [Fact]
    public void ParsePrChecks_MapsLegacyStatusContext()
    {
        var legacy = GitHubService.ParsePrChecks(Parse(FullResponse))[3];

        Assert.Equal("legacy/build", legacy.Name);
        Assert.Equal(CheckRunState.Failure, legacy.State);
        Assert.Equal("https://ci.example.com/build/7", legacy.Url);
        Assert.Equal(0, legacy.WorkflowRunId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "data": { "repository": null } }""")]
    [InlineData("""{ "data": { "repository": { "pullRequest": null } } }""")]
    [InlineData("""{ "data": { "repository": { "pullRequest": { "commits": { "nodes": [] } } } } }""")]
    public void ParsePrChecks_MissingOrEmptyData_ReturnsEmptyList(string json)
    {
        Assert.Empty(GitHubService.ParsePrChecks(Parse(json)));
    }

    [Fact]
    public void ParsePrChecks_CommitWithoutRollup_ReturnsEmptyList()
    {
        const string json = """
            {
              "data": {
                "repository": {
                  "pullRequest": {
                    "commits": { "nodes": [ { "commit": { "oid": "abc", "statusCheckRollup": null } } ] }
                  }
                }
              }
            }
            """;

        Assert.Empty(GitHubService.ParsePrChecks(Parse(json)));
    }

    [Fact]
    public void ParsePrChecks_NamelessNodes_AreSkipped()
    {
        const string json = """
            {
              "data": {
                "repository": {
                  "pullRequest": {
                    "commits": { "nodes": [ { "commit": { "statusCheckRollup": { "contexts": { "nodes": [
                      { "__typename": "CheckRun", "status": "COMPLETED", "conclusion": "SUCCESS" },
                      { "__typename": "StatusContext", "state": "SUCCESS" },
                      { "__typename": "CheckRun", "name": "Real", "status": "QUEUED" }
                    ] } } } } ] }
                  }
                }
              }
            }
            """;

        var checks = GitHubService.ParsePrChecks(Parse(json));

        Assert.Single(checks);
        Assert.Equal("Real", checks[0].Name);
        Assert.Equal(CheckRunState.Queued, checks[0].State);
    }

    [Theory]
    [InlineData("COMPLETED", "SUCCESS", CheckRunState.Success)]
    [InlineData("COMPLETED", "FAILURE", CheckRunState.Failure)]
    [InlineData("COMPLETED", "TIMED_OUT", CheckRunState.Failure)]
    [InlineData("COMPLETED", "STARTUP_FAILURE", CheckRunState.Failure)]
    [InlineData("COMPLETED", "CANCELLED", CheckRunState.Cancelled)]
    [InlineData("COMPLETED", "SKIPPED", CheckRunState.Skipped)]
    [InlineData("COMPLETED", "NEUTRAL", CheckRunState.Neutral)]
    [InlineData("COMPLETED", "ACTION_REQUIRED", CheckRunState.Neutral)]
    [InlineData("COMPLETED", null, CheckRunState.Unknown)]
    [InlineData("IN_PROGRESS", null, CheckRunState.Running)]
    [InlineData("QUEUED", null, CheckRunState.Queued)]
    [InlineData("WAITING", null, CheckRunState.Queued)]
    [InlineData("PENDING", null, CheckRunState.Queued)]
    // A conclusion left over from a previous attempt must not win over a re-queued status.
    [InlineData("IN_PROGRESS", "FAILURE", CheckRunState.Running)]
    [InlineData(null, null, CheckRunState.Unknown)]
    public void FromCheckRun_MapsStatusAndConclusion(string? status, string? conclusion, CheckRunState expected)
    {
        Assert.Equal(expected, CheckRunInfo.FromCheckRun(status, conclusion));
    }

    [Theory]
    [InlineData("SUCCESS", CheckRunState.Success)]
    [InlineData("FAILURE", CheckRunState.Failure)]
    [InlineData("ERROR", CheckRunState.Failure)]
    [InlineData("PENDING", CheckRunState.Running)]
    [InlineData("EXPECTED", CheckRunState.Queued)]
    [InlineData(null, CheckRunState.Unknown)]
    public void FromStatusContext_MapsLegacyStates(string? state, CheckRunState expected)
    {
        Assert.Equal(expected, CheckRunInfo.FromStatusContext(state));
    }

    [Fact]
    public void Duration_RunningCheck_UsesElapsedTime()
    {
        var check = new CheckRunInfo
        {
            Name = "Test",
            State = CheckRunState.Running,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
        };

        Assert.NotNull(check.Duration);
        Assert.InRange(check.Duration!.Value.TotalMinutes, 2.9, 3.2);
    }

    [Fact]
    public void SortRank_OrdersFailuresBeforeRunningBeforeSuccessBeforeSkipped()
    {
        CheckRunInfo Check(CheckRunState state) => new() { Name = state.ToString(), State = state };

        var ordered = new[]
        {
            Check(CheckRunState.Skipped),
            Check(CheckRunState.Success),
            Check(CheckRunState.Running),
            Check(CheckRunState.Failure),
        }.OrderBy(c => c.SortRank).Select(c => c.State);

        Assert.Equal(
            [CheckRunState.Failure, CheckRunState.Running, CheckRunState.Success, CheckRunState.Skipped],
            ordered);
    }
}
