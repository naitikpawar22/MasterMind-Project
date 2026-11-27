using GeminiDesktop.Helpers;
using Xunit;

namespace GeminiDesktop.Tests;

public sealed class ConfigurationHelperTests
{
    [Theory]
    [InlineData("https://gemini.google.com/", true, "https://gemini.google.com/")]
    [InlineData("https://gemini.google.com/app", true, "https://gemini.google.com/app")]
    [InlineData("  https://gemini.google.com/u/1/app  ", true, "https://gemini.google.com/u/1/app")]
    [InlineData("http://gemini.google.com/", false, "")]
    [InlineData("https://gemini.google.com:8443/app", false, "")]
    [InlineData("https://evil.example.com/", false, "")]
    [InlineData("https://gemini.google.com.evil.com/", false, "")]
    [InlineData("javascript:alert(1)", false, "")]
    [InlineData("", false, "")]
    public void TryValidateGeminiHomepageUrl_EnforcesHttpsAndOfficialDomain(
        string input, bool expectedValid, string expectedNormalized)
    {
        var ok = ConfigurationHelper.TryValidateGeminiHomepageUrl(input, out var normalized, out var error);

        Assert.Equal(expectedValid, ok);
        if (expectedValid)
        {
            Assert.Equal(expectedNormalized, normalized);
            Assert.Equal(string.Empty, error);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Theory]
    [InlineData("https://gemini.google.com/app", NavigationTargetKind.InAppGemini)]
    [InlineData("https://accounts.google.com/v3/signin/identifier", NavigationTargetKind.InAppGoogleAuth)]
    [InlineData("https://myaccount.google.com/", NavigationTargetKind.InAppGoogleAuth)]
    [InlineData("https://www.google.com/accounts/ServiceLogin", NavigationTargetKind.InAppGoogleAuth)]
    [InlineData("https://www.google.com/search?q=test", NavigationTargetKind.ExternalHttps)]
    [InlineData("https://learn.microsoft.com/en-us/", NavigationTargetKind.ExternalHttps)]
    [InlineData("http://example.com/", NavigationTargetKind.BlockedUnsafe)]
    [InlineData("file:///C:/Windows/System32/drivers/etc/hosts", NavigationTargetKind.BlockedUnsafe)]
    [InlineData("javascript:alert(1)", NavigationTargetKind.BlockedUnsafe)]
    [InlineData("data:text/html,<h1>x</h1>", NavigationTargetKind.BlockedUnsafe)]
    public void ClassifyNavigationUri_RoutesAndBlocksAccurately(string rawUri, NavigationTargetKind expected)
    {
        var uri = new Uri(rawUri, UriKind.Absolute);
        Assert.Equal(expected, ConfigurationHelper.ClassifyNavigationUri(uri));
    }

    [Fact]
    public void SanitizeUriForLogging_StripsQueryAndFragmentSecrets()
    {
        var uri = new Uri("https://accounts.google.com/o/oauth2/v2/auth?code=SECRET_TOKEN&state=123#frag");
        var sanitized = ConfigurationHelper.SanitizeUriForLogging(uri);

        Assert.Equal("https://accounts.google.com/o/oauth2/v2/auth", sanitized);
        Assert.DoesNotContain("SECRET_TOKEN", sanitized);
        Assert.DoesNotContain("frag", sanitized);
    }
}
