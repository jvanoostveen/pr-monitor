using PrMonitor.Services;
using Xunit;

namespace PrMonitor.Tests.Services;

public class GhCliTests
{
    [Fact]
    public void DescribeArguments_ShortensQueryTextAndKeepsOtherArguments()
    {
        var description = GhCli.DescribeArguments(["api", "graphql", "-f", "query=query { viewer { login } }", "-f", "q=is:pr"]);

        Assert.Equal("api graphql -f query=<26 chars> -f q=is:pr", description);
    }
}
