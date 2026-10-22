using Vigo.Core.Models;

namespace Vigo.Core.Tests;

public sealed class ReferenceLinksTests
{
    [Fact]
    public void Azure_DevOps_links_work_items_and_pull_requests_but_not_people()
    {
        var links = new HostRepository(HostingProvider.AzureDevOps, "dev.azure.com", "contoso/Atlas", "api").ReferenceLinks;

        Assert.Equal("https://dev.azure.com/contoso/Atlas/_workitems/edit/{0}", links.Issue);
        Assert.Equal("https://dev.azure.com/contoso/Atlas/_git/api/pullrequest/{0}", links.Pull);
        Assert.Null(links.User);
    }

    [Fact]
    public void GitHub_links_issues_pull_requests_and_people()
    {
        var links = new HostRepository(HostingProvider.GitHub, "github.com", "atlas", "api").ReferenceLinks;

        Assert.Equal("https://github.com/atlas/api/issues/{0}", links.Issue);
        Assert.Equal("https://github.com/atlas/api/pull/{0}", links.Pull);
        Assert.Equal("https://github.com/{0}", links.User);
    }
}
