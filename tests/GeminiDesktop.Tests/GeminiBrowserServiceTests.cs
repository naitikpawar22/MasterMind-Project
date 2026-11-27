using GeminiDesktop.Services;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace GeminiDesktop.Tests;

public sealed class GeminiBrowserServiceTests
{
    [Fact]
    public void MapNavigationFailure_OfflineOrDisconnected_ReturnsOfflineGuidance()
    {
        var result = GeminiBrowserService.MapNavigationFailure(
            CoreWebView2WebErrorStatus.Disconnected,
            "https://gemini.google.com/",
            networkAvailable: false);

        Assert.True(result.IsOffline);
        Assert.Equal("ERR_INTERNET_DISCONNECTED", result.Code);
        Assert.Contains("Reload", result.Message);
    }

    [Fact]
    public void MapNavigationFailure_GoogleSignInFailure_ReturnsAuthSpecificGuidance()
    {
        var result = GeminiBrowserService.MapNavigationFailure(
            CoreWebView2WebErrorStatus.Timeout,
            "https://accounts.google.com/v3/signin",
            networkAvailable: true);

        Assert.False(result.IsOffline);
        Assert.Contains("Sign-In", result.Title);
        Assert.Equal("ERR_TIMED_OUT", result.Code);
    }

    [Fact]
    public void MapNavigationFailure_CertificateError_ReturnsSecurityWarning()
    {
        var result = GeminiBrowserService.MapNavigationFailure(
            CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect,
            "https://gemini.google.com/",
            networkAvailable: true);

        Assert.Contains("Certificate", result.Title);
    }
}
