using PrMonitor.Models;
using PrMonitor.Services;
using PrMonitor.Settings;
using Xunit;

namespace PrMonitor.Tests.Services;

public class NotificationServiceLogicTests
{
    [Fact]
    public void GetHeader_CIFailure_ReturnsCIFailedHeader()
    {
        var e = MakeEvent(PrChangeKind.CIStatusChanged, CIState.Failure);
        Assert.Equal("❌ CI Failed", NotificationService.GetHeader(e));
    }

    [Fact]
    public void GetHeader_CIRecoveryFromFailure_ReturnsCIPassedHeader()
    {
        var e = MakeEvent(PrChangeKind.CIStatusChanged, CIState.Success, previousCI: CIState.Failure);
        Assert.Equal("✅ CI Passed", NotificationService.GetHeader(e));
    }

    [Fact]
    public void GetHeader_CIError_ReturnsCIErrorHeader()
    {
        var e = MakeEvent(PrChangeKind.CIStatusChanged, CIState.Error);
        Assert.Equal("⚠️ CI Error", NotificationService.GetHeader(e));
    }

    [Fact]
    public void GetHeader_NewReviewRequested_ReturnsReviewHeader()
    {
        var e = MakeEvent(PrChangeKind.NewReviewRequested, CIState.Unknown);
        Assert.Equal("👀 Review Requested", NotificationService.GetHeader(e));
    }

    [Fact]
    public void GetHeader_RemovedAutoMergePr_ReturnsMergedHeader()
    {
        var e = MakeEvent(PrChangeKind.RemovedAutoMergePr, CIState.Unknown);
        Assert.Equal("🔀 PR Merged / Closed", NotificationService.GetHeader(e));
    }

    [Fact]
    public void GetHeader_CISuccessFromNonFailure_ReturnsNull()
    {
        var e = MakeEvent(PrChangeKind.CIStatusChanged, CIState.Success, previousCI: CIState.Pending);
        Assert.Null(NotificationService.GetHeader(e));
    }

    [Fact]
    public void GetHeader_ReviewRequestRemoved_ReturnsNull()
    {
        var e = MakeEvent(PrChangeKind.ReviewRequestRemoved, CIState.Unknown);
        Assert.Null(NotificationService.GetHeader(e));
    }

    [Fact]
    public void IsNotificationEnabled_ModeNever_AlwaysReturnsFalse()
    {
        var settings = new AppSettings { NotificationMode = NotificationMode.Never };
        var svc = new NotificationService(settings);

        Assert.False(svc.IsNotificationEnabled("❌ CI Failed"));
    }

    [Fact]
    public void IsNotificationEnabled_CIFailedToggleOff_ReturnsFalse()
    {
        var settings = new AppSettings
        {
            NotificationMode = NotificationMode.Always,
            NotifyCiFailed = false,
        };
        var svc = new NotificationService(settings);

        Assert.False(svc.IsNotificationEnabled("❌ CI Failed"));
    }

    [Fact]
    public void IsNotificationEnabled_AllTogglesOn_ReturnsTrue()
    {
        var settings = new AppSettings
        {
            NotificationMode = NotificationMode.Always,
            NotifyCiFailed = true,
            NotifyCiPassed = true,
            NotifyReviewRequested = true,
        };
        var svc = new NotificationService(settings);

        Assert.True(svc.IsNotificationEnabled("❌ CI Failed"));
        Assert.True(svc.IsNotificationEnabled("✅ CI Passed"));
        Assert.True(svc.IsNotificationEnabled("👀 Review Requested"));
    }

    [Fact]
    public void IsNotificationEnabled_WhenWindowClosed_WindowVisible_ReturnsFalse()
    {
        var settings = new AppSettings
        {
            NotificationMode = NotificationMode.WhenWindowClosed,
            MainWindowVisible = true,
            NotifyCiFailed = true,
        };
        var svc = new NotificationService(settings);

        Assert.False(svc.IsNotificationEnabled("❌ CI Failed"));
    }

    [Fact]
    public void IsNotificationEnabled_WhenWindowClosed_WindowHidden_ReturnsTrue()
    {
        var settings = new AppSettings
        {
            NotificationMode = NotificationMode.WhenWindowClosed,
            MainWindowVisible = false,
            NotifyCiFailed = true,
        };
        var svc = new NotificationService(settings);

        Assert.True(svc.IsNotificationEnabled("❌ CI Failed"));
    }

    private static PrChangeEventArgs MakeEvent(
        PrChangeKind kind, CIState ciState, CIState previousCI = CIState.Unknown) =>
        new()
        {
            Kind = kind,
            PreviousCIState = previousCI,
            PullRequest = new PullRequestInfo
            {
                Number = 1,
                Title = "Test PR",
                Url = "https://github.com/org/repo/pull/1",
                Repository = "org/repo",
                Author = "alice",
                CIState = ciState,
            },
        };
}