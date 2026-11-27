namespace GeminiDesktop.Models;

/// <summary>
/// Snapshot of the embedded WebView2 browser session state.
/// </summary>
public sealed class BrowserState
{
    public bool IsWebView2RuntimeAvailable { get; init; } = true;

    public string? WebView2RuntimeVersion { get; init; }

    public bool IsInitializing { get; init; }

    public bool IsInitialized { get; init; }

    public bool IsLoading { get; init; }

    public bool CanGoBack { get; init; }

    public bool CanGoForward { get; init; }

    public string CurrentUrl { get; init; } = AppSettings.DefaultGeminiUrl;

    public string PageTitle { get; init; } = "Google Gemini";

    public bool IsOffline { get; init; }

    public bool HasError { get; init; }

    public string? ErrorTitle { get; init; }

    public string? ErrorMessage { get; init; }

    public string? ErrorCode { get; init; }

    public bool IsGoogleSignInFlow { get; init; }

    public string StatusText { get; init; } = "Ready";
}
