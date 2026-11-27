using System.IO;
using GeminiDesktop.Models;

namespace GeminiDesktop.Helpers;

/// <summary>
/// Classification result for URL navigation validation.
/// </summary>
public enum NavigationTargetKind
{
    InAppGemini = 0,
    InAppGoogleAuth = 1,
    ExternalHttps = 2,
    BlockedUnsafe = 3
}

/// <summary>
/// Provides validated local storage paths, URL security classification, and configuration helpers.
/// </summary>
public static class ConfigurationHelper
{
    public const string ApplicationName = "MyLaptop";
    public const string StartupRegistryRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupRegistryValueName = "MyLaptop";
    public const string WebView2DownloadPageUrl = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/";
    private const string PrivacyUnlockPasscode = "12345";

    private static readonly string[] GeminiHosts =
    [
        "gemini.google.com",
        "aistudio.google.com"
    ];

    private static readonly string[] GoogleAuthAndSupportHosts =
    [
        "accounts.google.com",
        "accounts.youtube.com",
        "myaccount.google.com",
        "consent.google.com",
        "gds.google.com",
        "ogs.google.com",
        "notifications.google.com",
        "policies.google.com",
        "support.google.com",
        "workspace.google.com"
    ];

    public static string GetAppDataDirectory(string? customRoot = null)
    {
        var baseDir = string.IsNullOrWhiteSpace(customRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : customRoot;

        return Path.Combine(baseDir, "MyLaptop");
    }

    public static bool VerifyPrivacyUnlockPassword(string? password) =>
        string.Equals(password, PrivacyUnlockPasscode, StringComparison.Ordinal);

    public static string GetWebView2UserDataDirectory(string? customRoot = null) =>
        Path.Combine(GetAppDataDirectory(customRoot), "WebView2UserData");

    public static string GetSettingsFilePath(string? customRoot = null) =>
        Path.Combine(GetAppDataDirectory(customRoot), "settings.json");

    public static string GetWindowSettingsFilePath(string? customRoot = null) =>
        Path.Combine(GetAppDataDirectory(customRoot), "window-settings.json");

    public static string GetLogsDirectory(string? customRoot = null) =>
        Path.Combine(GetAppDataDirectory(customRoot), "Logs");

    public static string EnsureDirectoryExists(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        return directoryPath;
    }

    /// <summary>
    /// Validates and normalizes the configured Gemini homepage URL.
    /// Enforces HTTPS and restricts host to official Google Gemini domains.
    /// </summary>
    public static bool TryValidateGeminiHomepageUrl(string? rawUrl, out string normalizedUrl, out string errorMessage)
    {
        normalizedUrl = AppSettings.DefaultGeminiUrl;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            errorMessage = "Homepage URL cannot be empty.";
            return false;
        }

        var trimmed = rawUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            errorMessage = "Enter a valid absolute URL (for example, https://gemini.google.com/).";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "Only secure HTTPS URLs are permitted.";
            return false;
        }

        if (!uri.IsDefaultPort)
        {
            errorMessage = "Custom ports are not permitted for the Gemini workspace URL.";
            return false;
        }

        if (!MatchesAnyHostOrSubdomain(uri.Host, GeminiHosts))
        {
            errorMessage = "Homepage URL must point to the official gemini.google.com domain.";
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        return true;
    }

    /// <summary>
    /// Classifies any navigation URI inside WebView2 to separate in-app Google Gemini/Auth flows,
    /// safe external HTTPS links (opened in default browser), and unsafe schemes.
    /// </summary>
    public static NavigationTargetKind ClassifyNavigationUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return NavigationTargetKind.BlockedUnsafe;
        }

        // Allow internal browser blank/edge schemes needed during WebView initialization or OAuth popups.
        if (string.Equals(uri.Scheme, "about", StringComparison.OrdinalIgnoreCase))
        {
            return NavigationTargetKind.InAppGemini;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return NavigationTargetKind.BlockedUnsafe;
        }

        if (MatchesAnyHostOrSubdomain(uri.Host, GeminiHosts))
        {
            return NavigationTargetKind.InAppGemini;
        }

        if (MatchesAnyHostOrSubdomain(uri.Host, GoogleAuthAndSupportHosts))
        {
            return NavigationTargetKind.InAppGoogleAuth;
        }

        // Google sign-in / OAuth redirects sometimes pass through www.google.com/accounts or /signin.
        if (string.Equals(uri.Host, "www.google.com", StringComparison.OrdinalIgnoreCase) &&
            (uri.AbsolutePath.StartsWith("/accounts", StringComparison.OrdinalIgnoreCase) ||
             uri.AbsolutePath.StartsWith("/signin", StringComparison.OrdinalIgnoreCase) ||
             uri.AbsolutePath.StartsWith("/intl/", StringComparison.OrdinalIgnoreCase)))
        {
            return NavigationTargetKind.InAppGoogleAuth;
        }

        return NavigationTargetKind.ExternalHttps;
    }

    /// <summary>
    /// Validates whether a URI is safe to open externally via ShellExecute.
    /// </summary>
    public static bool IsSafeExternalLaunchUri(Uri? uri) =>
        uri is not null &&
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(uri.Host);

    /// <summary>
    /// Strips query strings and fragments before logging URLs so OAuth tokens or prompt parameters are never persisted.
    /// </summary>
    public static string SanitizeUriForLogging(Uri? uri)
    {
        if (uri is null)
        {
            return "<null>";
        }

        if (!uri.IsAbsoluteUri)
        {
            return "<relative-uri>";
        }

        if (string.Equals(uri.Scheme, "about", StringComparison.OrdinalIgnoreCase))
        {
            return uri.OriginalString;
        }

        return uri.GetLeftPart(UriPartial.Path);
    }

    private static bool MatchesAnyHostOrSubdomain(string host, IEnumerable<string> allowedHosts)
    {
        foreach (var allowed in allowedHosts)
        {
            if (string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
