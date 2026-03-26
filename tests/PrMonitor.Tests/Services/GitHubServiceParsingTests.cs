using System.Text.Json;
using PrMonitor.Models;
using PrMonitor.Services;
using Xunit;

namespace PrMonitor.Tests.Services;

public class GitHubServiceParsingTests
{
    [Theory]
    [InlineData("SUCCESS",  CIState.Success)]
    [InlineData("FAILURE",  CIState.Failure)]
    [InlineData("PENDING",  CIState.Pending)]
    [InlineData("ERROR",    CIState.Error)]
    [InlineData("EXPECTED", CIState.Success)]
    [InlineData("success",  CIState.Success)]
    [InlineData("Unknown",  CIState.Unknown)]
    [InlineData("",         CIState.Unknown)]
    [InlineData(null,       CIState.Unknown)]
    public void ParseCIState_AllMappings_AreCorrect(string? input, CIState expected)
    {
        var result = GitHubService.ParseCIState(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("hello",           "hello")]
    [InlineData("he\"llo",         "he\\\"llo")]
    [InlineData("back\\slash",     "back\\\\slash")]
    [InlineData("both\\and\"here", "both\\\\and\\\"here")]
    [InlineData("",                "")]
    public void EscapeForShell_EscapesSpecialCharacters(string input, string expected)
    {
        var result = GitHubService.EscapeForShell(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildSearchQueries_NoOrgs_ReturnsBaseQueryUnchanged()
    {
        var result = GitHubService.BuildSearchQueries("is:open author:me", []);
        Assert.Single(result);
        Assert.Equal("is:open author:me", result[0]);
    }

    [Fact]
    public void BuildSearchQueries_SingleOrg_ReturnsSingleQueryWithOrgQualifier()
    {
        var result = GitHubService.BuildSearchQueries("is:open", ["my-org"]);
        Assert.Single(result);
        Assert.Equal("is:open org:my-org", result[0]);
    }

    [Fact]
    public void BuildSearchQueries_MultipleOrgs_ReturnsOneQueryPerOrg()
    {
        var result = GitHubService.BuildSearchQueries("is:open", ["org-a", "org-b", "org-c"]);
        Assert.Equal(3, result.Count);
        Assert.Contains("is:open org:org-a", result);
        Assert.Contains("is:open org:org-b", result);
        Assert.Contains("is:open org:org-c", result);
    }

    [Fact]
    public void ParseUnresolvedReviewCommentCount_NoReviewThreadsProperty_ReturnsZero()
    {
        using var doc = JsonDocument.Parse("{}");
        var result = GitHubService.ParseUnresolvedReviewCommentCount(doc.RootElement);
        Assert.Equal(0, result);
    }

    [Fact]
    public void ParseUnresolvedReviewCommentCount_AllResolved_ReturnsZero()
    {
        var json = """{"reviewThreads":{"nodes":[{"isResolved":true,"comments":{"totalCount":3}}]}}""";
        using var doc = JsonDocument.Parse(json);
        var result = GitHubService.ParseUnresolvedReviewCommentCount(doc.RootElement);
        Assert.Equal(0, result);
    }

    [Fact]
    public void ParseUnresolvedReviewCommentCount_TwoUnresolvedThreads_SumsCommentCounts()
    {
        var json = """{"reviewThreads":{"nodes":[{"isResolved":false,"comments":{"totalCount":2}},{"isResolved":false,"comments":{"totalCount":3}},{"isResolved":true,"comments":{"totalCount":5}}]}}""";
        using var doc = JsonDocument.Parse(json);
        var result = GitHubService.ParseUnresolvedReviewCommentCount(doc.RootElement);
        Assert.Equal(5, result);
    }

    [Fact]
    public void GetAuthorLogin_MissingAuthorProperty_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("{}");
        Assert.Equal("", GitHubService.GetAuthorLogin(doc.RootElement));
    }

    [Fact]
    public void GetAuthorLogin_NullAuthor_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""{"author":null}""");
        Assert.Equal("", GitHubService.GetAuthorLogin(doc.RootElement));
    }

    [Fact]
    public void GetAuthorLogin_WithLogin_ReturnsLoginString()
    {
        using var doc = JsonDocument.Parse("""{"author":{"login":"bob"}}""");
        Assert.Equal("bob", GitHubService.GetAuthorLogin(doc.RootElement));
    }

    [Fact]
    public void GetCommitOid_MissingCommitsProperty_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("{}");
        Assert.Equal("", GitHubService.GetCommitOid(doc.RootElement));
    }

    [Fact]
    public void GetCommitOid_WithCommit_ReturnsOid()
    {
        var json = """{"commits":{"nodes":[{"commit":{"oid":"deadbeef"}}]}}""";
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("deadbeef", GitHubService.GetCommitOid(doc.RootElement));
    }

    [Fact]
    public void ParseMyPrs_EmptyResponse_ReturnsEmptyList()
    {
        using var doc = JsonDocument.Parse("{}");
        var result = GitHubService.ParseMyPrs(doc.RootElement);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseMyPrs_ArchivedRepo_SkipsPR()
    {
        var json = BuildMyPrsJson(BuildPrNode(number: 1, isArchived: true));
        using var doc = JsonDocument.Parse(json);
        var result = GitHubService.ParseMyPrs(doc.RootElement);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseMyPrs_NormalPr_ParsesAllFields()
    {
        var json = BuildMyPrsJson(BuildPrNode(
            number: 42, title: "My feature", ciState: "SUCCESS",
            hasAutoMerge: false, isDraft: false, reviewDecision: "APPROVED",
            headSha: "abc123", headRef: "feature/foo"));
        using var doc = JsonDocument.Parse(json);
        var result = GitHubService.ParseMyPrs(doc.RootElement);

        Assert.Single(result);
        var pr = result[0];
        Assert.Equal(42, pr.Number);
        Assert.Equal("My feature", pr.Title);
        Assert.Equal(CIState.Success, pr.CIState);
        Assert.False(pr.HasAutoMerge);
        Assert.False(pr.IsDraft);
        Assert.True(pr.IsApproved);
        Assert.Equal("abc123", pr.HeadCommitSha);
        Assert.Equal("feature/foo", pr.HeadRefName);
    }

    [Fact]
    public void ParseMyPrs_AutoMergePr_SetsHasAutoMerge()
    {
        var json = BuildMyPrsJson(BuildPrNode(number: 5, hasAutoMerge: true));
        using var doc = JsonDocument.Parse(json);
        var result = GitHubService.ParseMyPrs(doc.RootElement);

        Assert.Single(result);
        Assert.True(result[0].HasAutoMerge);
    }

    [Fact]
    public void ParseMyPrs_ConflictingMergeable_OverridesCIStateWithFailure()
    {
        var json = BuildMyPrsJson(BuildPrNode(number: 7, ciState: "SUCCESS", mergeable: "CONFLICTING"));
        using var doc = JsonDocument.Parse(json);
        var result = GitHubService.ParseMyPrs(doc.RootElement);

        Assert.Equal(CIState.Failure, result[0].CIState);
    }

    [Fact]
    public void ParseMyPrs_DraftPr_SetsIsDraft()
    {
        var json = BuildMyPrsJson(BuildPrNode(number: 8, isDraft: true));
        using var doc = JsonDocument.Parse(json);
        var result = GitHubService.ParseMyPrs(doc.RootElement);

        Assert.True(result[0].IsDraft);
    }

    [Fact]
    public void ParseMyPrs_WithNonCopilotReviewer_PopulatesReviewerLogins()
    {
        var json = BuildMyPrsJson(BuildPrNode(number: 9, reviewers: [("alice", "User")]));
        using var doc = JsonDocument.Parse(json);
        var result = GitHubService.ParseMyPrs(doc.RootElement);

        Assert.Single(result[0].ReviewerLogins);
        Assert.Equal("alice", result[0].ReviewerLogins[0]);
    }

    [Fact]
    public void ParseMyPrs_WithCopilotReviewer_FiltersCopilotFromReviewerLogins()
    {
        var json = BuildMyPrsJson(BuildPrNode(number: 10, reviewers: [("copilot", "User"), ("bob", "User")]));
        using var doc = JsonDocument.Parse(json);
        var result = GitHubService.ParseMyPrs(doc.RootElement);

        Assert.Single(result[0].ReviewerLogins);
        Assert.Equal("bob", result[0].ReviewerLogins[0]);
    }

    [Fact]
    public void ParseMyPrs_WithOnlyCopilotReviewer_ReturnsEmptyReviewerLogins()
    {
        var json = BuildMyPrsJson(BuildPrNode(number: 11, reviewers: [("Copilot", "User")]));
        using var doc = JsonDocument.Parse(json);
        var result = GitHubService.ParseMyPrs(doc.RootElement);

        Assert.Empty(result[0].ReviewerLogins);
    }

    [Fact]
    public void ParseMyPrs_WithTeamReviewer_IncludesTeamSlug()
    {
        var json = BuildMyPrsJson(BuildPrNode(number: 12, reviewers: [("platform-team", "Team")]));
        using var doc = JsonDocument.Parse(json);
        var result = GitHubService.ParseMyPrs(doc.RootElement);

        Assert.Single(result[0].ReviewerLogins);
        Assert.Equal("platform-team", result[0].ReviewerLogins[0]);
    }

    [Fact]
    public void ParseReviewerLogins_NoReviewRequestsProperty_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("{}");
        var result = GitHubService.ParseReviewerLogins(doc.RootElement);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseReviewPrs_DirectUserReviewRequest_IsNotTeamOnly()
    {
        var json = BuildReviewPrsJson(BuildReviewPrNode(number: 10, reviewerName: "alice", reviewerType: "User"));
        using var doc = JsonDocument.Parse(json);
        var result = GitHubService.ParseReviewPrs(doc.RootElement, "alice");

        Assert.Single(result);
        Assert.False(result[0].IsTeamReviewRequested);
    }

    [Fact]
    public void ParseReviewPrs_TeamReviewRequest_IsTeamOnly()
    {
        var json = BuildReviewPrsJson(BuildReviewPrNode(number: 11, reviewerName: "my-team", reviewerType: "Team"));
        using var doc = JsonDocument.Parse(json);
        var result = GitHubService.ParseReviewPrs(doc.RootElement, "alice");

        Assert.Single(result);
        Assert.True(result[0].IsTeamReviewRequested);
    }

    [Fact]
    public void ParseReviewPrs_DifferentUserReview_IsTeamOnly()
    {
        var json = BuildReviewPrsJson(BuildReviewPrNode(number: 12, reviewerName: "charlie", reviewerType: "User"));
        using var doc = JsonDocument.Parse(json);
        var result = GitHubService.ParseReviewPrs(doc.RootElement, "alice");

        Assert.Single(result);
        Assert.True(result[0].IsTeamReviewRequested);
    }

    private static string BuildMyPrsJson(string prNode) =>
        "{\"data\":{\"search\":{\"nodes\":[" + prNode + "]}}}";

    private static string BuildReviewPrsJson(string prNode) =>
        "{\"data\":{\"search\":{\"nodes\":[" + prNode + "]}}}";

    private static string BuildPrNode(
        int number = 1,
        string title = "Test PR",
        string? ciState = null,
        bool hasAutoMerge = false,
        bool isDraft = false,
        string? reviewDecision = null,
        string headSha = "sha1",
        string headRef = "feature/test",
        bool isArchived = false,
        string mergeable = "MERGEABLE",
        IEnumerable<(string name, string type)>? reviewers = null)
    {
        var autoMerge = hasAutoMerge ? "{\"enabledAt\":\"2026-01-01T00:00:00Z\"}" : "null";
        var ciNode = ciState != null
            ? "{\"commit\":{\"oid\":\"" + headSha + "\",\"statusCheckRollup\":{\"state\":\"" + ciState + "\"}}}"
            : "{\"commit\":{\"oid\":\"" + headSha + "\",\"statusCheckRollup\":null}}";
        var rd = reviewDecision != null ? "\"" + reviewDecision + "\"" : "null";
        var reviewRequestNodes = reviewers != null
            ? string.Join(",", reviewers.Select(r =>
                r.type == "Team"
                    ? $"{{\"requestedReviewer\":{{\"__typename\":\"Team\",\"slug\":\"{r.name}\"}}}}"
                    : $"{{\"requestedReviewer\":{{\"__typename\":\"User\",\"login\":\"{r.name}\"}}}}"  ))
            : "";
        return "{\"number\":" + number
            + ",\"title\":\"" + title + "\""
            + ",\"url\":\"https://github.com/org/repo/pull/" + number + "\""
            + ",\"repository\":{\"nameWithOwner\":\"org/repo\",\"isArchived\":" + isArchived.ToString().ToLower() + "}"
            + ",\"author\":{\"login\":\"testuser\"}"
            + ",\"createdAt\":\"2026-01-15T10:00:00Z\""
            + ",\"autoMergeRequest\":" + autoMerge
            + ",\"isDraft\":" + isDraft.ToString().ToLower()
            + ",\"reviewDecision\":" + rd
            + ",\"mergeable\":\"" + mergeable + "\""
            + ",\"headRefName\":\"" + headRef + "\""
            + ",\"commits\":{\"nodes\":[" + ciNode + "]}"
            + ",\"reviewRequests\":{\"nodes\":[" + reviewRequestNodes + "]}"
            + ",\"reviewThreads\":{\"nodes\":[]}}";
    }

    private static string BuildReviewPrNode(
        int number = 1,
        string reviewerName = "alice",
        string reviewerType = "User")
    {
        var reviewerJson = "{\"requestedReviewer\":{\"__typename\":\"" + reviewerType + "\",\"login\":\"" + reviewerName + "\"}}";
        return "{\"number\":" + number
            + ",\"title\":\"Review PR " + number + "\""
            + ",\"url\":\"https://github.com/org/repo/pull/" + number + "\""
            + ",\"repository\":{\"nameWithOwner\":\"org/repo\",\"isArchived\":false}"
            + ",\"author\":{\"login\":\"someone\"}"
            + ",\"createdAt\":\"2026-01-15T10:00:00Z\""
            + ",\"autoMergeRequest\":null"
            + ",\"isDraft\":false"
            + ",\"reviewDecision\":null"
            + ",\"mergeable\":\"MERGEABLE\""
            + ",\"headRefName\":\"feature/test\""
            + ",\"baseRefName\":\"main\""
            + ",\"commits\":{\"nodes\":[{\"commit\":{\"oid\":\"sha1\",\"statusCheckRollup\":null}}]}"
            + ",\"reviewThreads\":{\"nodes\":[]}"
            + ",\"reviewRequests\":{\"nodes\":[" + reviewerJson + "]}}";
    }
}