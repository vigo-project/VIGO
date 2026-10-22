using Vigo.Core.Services;

namespace Vigo.Core.Tests;

public sealed class AzureDevOpsOrganizationTests
{
    [Theory]
    [InlineData("contoso", "contoso")]
    [InlineData("  contoso  ", "contoso")]
    [InlineData("https://dev.azure.com/contoso", "contoso")]
    [InlineData("https://dev.azure.com/contoso/Atlas%20Project/_git/atlas-api", "contoso")]
    [InlineData("https://contoso@dev.azure.com/contoso/Atlas/_git/api", "contoso")]
    [InlineData("dev.azure.com/contoso", "contoso")]
    [InlineData("https://contoso.visualstudio.com/DefaultCollection/Atlas", "contoso")]
    public void Reads_the_organization(string input, string expected)
    {
        Assert.Equal(expected, AzureDevOpsOrganization.Parse(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://github.com/contoso/repo")]
    [InlineData("https://dev.azure.com/")]
    [InlineData("http://dev.azure.com/contoso")]
    [InlineData("not an org")]
    public void Rejects_what_is_not_an_organization(string? input)
    {
        Assert.Null(AzureDevOpsOrganization.Parse(input));
    }

    [Fact]
    public void Builds_the_token_page_url()
    {
        Assert.Equal("https://dev.azure.com/contoso/_usersSettings/tokens", AzureDevOpsOrganization.TokenPageUrl("contoso"));
        Assert.Equal("https://dev.azure.com/", AzureDevOpsOrganization.TokenPageUrl(null));
    }
}
