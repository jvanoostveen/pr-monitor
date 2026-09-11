using PrMonitor.Models;
using PrMonitor.ViewModels;
using Xunit;

namespace PrMonitor.Tests.ViewModels;

/// <summary>
/// A job name on its own is not identifiable — two workflows having a "Test" job is normal, and
/// a workflow triggered by pull_request_review gets a fresh check suite per review, so the same
/// skipped job can come back nine times. These tests pin down both behaviours.
/// </summary>
public class ChecksCollapseTests
{
    private static CheckRunInfo Check(
        string workflow,
        string name,
        CheckRunState state = CheckRunState.Success,
        long runId = 0,
        DateTimeOffset? startedAt = null) =>
        new()
        {
            Name = name,
            WorkflowName = workflow,
            State = state,
            WorkflowRunId = runId,
            StartedAt = startedAt,
        };

    [Fact]
    public void Collapse_IdenticalRuns_BecomeOneRowWithACount()
    {
        var checks = Enumerable.Range(0, 9)
            .Select(i => Check("Claude Code", "claude", CheckRunState.Skipped, runId: 100 + i))
            .ToList();

        var rows = ChecksViewModel.Collapse(checks);

        Assert.Single(rows);
        Assert.Equal(9, rows[0].Count);
        Assert.Equal("claude", rows[0].Check.Name);
    }

    [Fact]
    public void Collapse_KeepsTheMostRecentRunAsTheRepresentative()
    {
        var checks = new List<CheckRunInfo>
        {
            Check("Claude Code", "claude", CheckRunState.Skipped, runId: 100),
            Check("Claude Code", "claude", CheckRunState.Skipped, runId: 305),
            Check("Claude Code", "claude", CheckRunState.Skipped, runId: 204),
        };

        var rows = ChecksViewModel.Collapse(checks);

        Assert.Equal(305, rows[0].Check.WorkflowRunId);
    }

    [Fact]
    public void Collapse_WithoutRunIds_FallsBackToTheLatestStartTime()
    {
        var baseTime = new DateTimeOffset(2026, 9, 11, 14, 0, 0, TimeSpan.Zero);
        var checks = new List<CheckRunInfo>
        {
            Check("CI", "Test", CheckRunState.Success, startedAt: baseTime),
            Check("CI", "Test", CheckRunState.Success, startedAt: baseTime.AddMinutes(20)),
        };

        var rows = ChecksViewModel.Collapse(checks);

        Assert.Single(rows);
        Assert.Equal(baseTime.AddMinutes(20), rows[0].Check.StartedAt);
    }

    [Fact]
    public void Collapse_SameJobNameInDifferentWorkflows_StaysTwoRows()
    {
        var checks = new List<CheckRunInfo>
        {
            Check("Components", "Test", CheckRunState.Failure),
            Check("Main PR", "Test", CheckRunState.Running),
        };

        var rows = ChecksViewModel.Collapse(checks);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(1, r.Count));
        Assert.Equal(["Components", "Main PR"], rows.Select(r => r.Check.WorkflowName));
    }

    [Fact]
    public void Collapse_SameJobInDifferentStates_IsNotHidden()
    {
        // A job that failed and passed on a rerun must stay visible as both.
        var checks = new List<CheckRunInfo>
        {
            Check("CI", "Test", CheckRunState.Failure, runId: 1),
            Check("CI", "Test", CheckRunState.Success, runId: 2),
        };

        var rows = ChecksViewModel.Collapse(checks);

        Assert.Equal(2, rows.Count);
        Assert.Equal(CheckRunState.Failure, rows[0].Check.State);
        Assert.Equal(CheckRunState.Success, rows[1].Check.State);
    }

    [Fact]
    public void Collapse_OrdersByAttentionThenWorkflowThenJob()
    {
        var checks = new List<CheckRunInfo>
        {
            Check("Zeta", "z-job", CheckRunState.Success),
            Check("Alpha", "a-job", CheckRunState.Success),
            Check("Skipper", "s-job", CheckRunState.Skipped),
            Check("Runner", "r-job", CheckRunState.Running),
            Check("Breaker", "b-job", CheckRunState.Failure),
        };

        var rows = ChecksViewModel.Collapse(checks).Select(r => r.Check.WorkflowName);

        Assert.Equal(["Breaker", "Runner", "Alpha", "Zeta", "Skipper"], rows);
    }

    [Fact]
    public void Collapse_EmptyInput_ProducesNoRows()
    {
        Assert.Empty(ChecksViewModel.Collapse([]));
    }

    [Fact]
    public void Collapse_RealWorldShape_ShrinksThirtyOneRunsToSevenRows()
    {
        // Mirrors an actual PR: two same-named Test jobs from different workflows, two passing
        // checks, and three jobs repeated nine times each by pull_request_review triggers.
        var checks = new List<CheckRunInfo>
        {
            Check("Components", "Test", CheckRunState.Failure, runId: 1),
            Check("Main PR", "Test", CheckRunState.Running, runId: 2),
            Check("Controleer koppeling", "Connect", CheckRunState.Success, runId: 3),
            Check("Components", "Compile", CheckRunState.Success, runId: 4),
        };
        foreach (var (workflow, job) in new[]
                 {
                     ("Auto-approve PR na Copilot review", "Auto-approve if Copilot review is clean"),
                     ("Claude Code", "claude"),
                     ("Post Copilot Summary", "call-reusable / post-summary"),
                 })
        {
            for (int i = 0; i < 9; i++)
                checks.Add(Check(workflow, job, CheckRunState.Skipped, runId: 500 + i));
        }

        var rows = ChecksViewModel.Collapse(checks);

        Assert.Equal(7, rows.Count);
        Assert.Equal(31, checks.Count);
        Assert.Equal(3, rows.Count(r => r.Count == 9));
        // The failing job leads, and both Test jobs remain distinguishable by workflow.
        Assert.Equal(CheckRunState.Failure, rows[0].Check.State);
        Assert.Equal("Components", rows[0].Check.WorkflowName);
        Assert.Equal(2, rows.Count(r => r.Check.Name == "Test"));
    }

    [Fact]
    public void Summary_CountsCollapsedRows_NotRawRuns()
    {
        var checks = new List<CheckRunInfo> { Check("CI", "Test", CheckRunState.Running) };
        for (int i = 0; i < 9; i++)
            checks.Add(Check("Claude Code", "claude", CheckRunState.Skipped, runId: i));
        checks.Add(Check("CI", "Compile", CheckRunState.Success));

        var rows = ChecksViewModel.Collapse(checks);
        var (label, _, count) = ChecksViewModel.BuildSummary([.. rows.Select(r => r.Check)]);

        Assert.Equal("CHECKS RUNNING", label);
        Assert.Equal("1/2", count);
    }

    // ── Row rendering ──────────────────────────────────────────────────

    [Fact]
    public void WorkflowPrefix_IsRenderedLikeGitHubWritesIt()
    {
        var row = new CheckItemViewModel(Check("Components", "Test", CheckRunState.Failure));

        Assert.Equal("Components / ", row.WorkflowPrefix);
    }

    [Fact]
    public void WorkflowPrefix_LegacyStatusContext_HasNoPrefix()
    {
        var row = new CheckItemViewModel(Check("", "legacy/build", CheckRunState.Success));

        Assert.Equal("", row.WorkflowPrefix);
    }

    [Fact]
    public void DuplicateBadge_ShownOnlyWhenRunsWereCollapsed()
    {
        var single = new CheckItemViewModel(Check("CI", "Test"));
        var collapsed = new CheckItemViewModel(Check("CI", "Test"), duplicateCount: 9);

        Assert.False(single.HasDuplicates);
        Assert.Equal("", single.DuplicateBadge);
        Assert.True(collapsed.HasDuplicates);
        Assert.Equal("×9", collapsed.DuplicateBadge);
    }

    [Fact]
    public void RowTooltip_NamesTheWorkflowTheTriggerAndTheCollapsedCount()
    {
        var check = new CheckRunInfo
        {
            Name = "claude",
            WorkflowName = "Claude Code",
            Event = "pull_request_review",
            State = CheckRunState.Skipped,
            Url = "https://github.com/o/r/actions/runs/1/job/2",
        };

        var tooltip = new CheckItemViewModel(check, duplicateCount: 9).RowTooltip;

        Assert.Contains("Workflow: Claude Code", tooltip);
        Assert.Contains("Job: claude", tooltip);
        Assert.Contains("Triggered by: pull_request_review", tooltip);
        Assert.Contains("9 identical runs", tooltip);
        Assert.Contains("Click to open the job log", tooltip);
    }

    [Fact]
    public void RowTooltip_WithoutWorkflowOrEvent_OmitsThoseLines()
    {
        var tooltip = new CheckItemViewModel(Check("", "legacy/build")).RowTooltip;

        Assert.DoesNotContain("Workflow:", tooltip);
        Assert.DoesNotContain("Triggered by:", tooltip);
        Assert.Contains("Job: legacy/build", tooltip);
    }
}
