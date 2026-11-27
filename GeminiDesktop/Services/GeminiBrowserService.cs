using System.Diagnostics;
using System.Net.NetworkInformation;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GeminiDesktop.Services;

/// <summary>
/// High-level Google Gemini workspace coordinator managing connectivity detection,
/// authentication error recovery, and observable BrowserState transitions.
/// </summary>
public interface IGeminiBrowserService : IDisposable
{
    BrowserState State { get; }

    event EventHandler<BrowserState>? StateChanged;
    event EventHandler<string>? MicrophonePermissionBlocked;

    bool CheckWebView2Availability();
    Task InitializeWorkspaceAsync(IWebView2 control);
    void NavigateHome();
    void Reload();
    void GoBack();
    void GoForward();
    Task ClearCacheAsync();
    Task ResetWorkspaceAsync();
    Task ResetWorkspaceAsync(IWebView2 control);
    void OpenWebView2InstallGuide();
}

public sealed class GeminiBrowserService : IGeminiBrowserService
{
    private readonly IWebViewService _webViewService;
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;

    private BrowserState _state = new();
    private bool _disposed;

    public GeminiBrowserService(
        IWebViewService webViewService,
        ISettingsService settingsService,
        ILoggingService loggingService)
    {
        _webViewService = webViewService;
        _settingsService = settingsService;
        _loggingService = loggingService;

        _webViewService.NavigationStarted += OnNavigationStarted;
        _webViewService.NavigationCompleted += OnNavigationCompleted;
        _webViewService.DocumentTitleChanged += OnDocumentTitleChanged;
        _webViewService.HistoryChanged += OnHistoryChanged;
        _webViewService.ExternalLinkOpened += OnExternalLinkOpened;
        _webViewService.UnsafeNavigationBlocked += OnUnsafeNavigationBlocked;
        _webViewService.RendererProcessFailed += OnRendererProcessFailed;
        _webViewService.MicrophonePermissionDenied += OnMicrophonePermissionDenied;

        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        var isOnline = CheckNetworkAvailable();
        UpdateState(s => new BrowserState
        {
            IsWebView2RuntimeAvailable = true,
            CurrentUrl = _settingsService.CurrentSettings.GeminiHomepageUrl,
            IsOffline = !isOnline,
            StatusText = isOnline ? "Ready" : "Offline — Waiting for internet connection"
        });
    }

    public BrowserState State => _state;

    public event EventHandler<BrowserState>? StateChanged;
    public event EventHandler<string>? MicrophonePermissionBlocked;

    public bool CheckWebView2Availability()
    {
        var available = _webViewService.IsWebView2RuntimeInstalled(out var version);
        if (_state.IsWebView2RuntimeAvailable == available &&
            string.Equals(_state.WebView2RuntimeVersion, version, StringComparison.Ordinal))
        {
            return available;
        }

        UpdateState(s => new BrowserState
        {
            IsWebView2RuntimeAvailable = available,
            WebView2RuntimeVersion = version,
            IsInitializing = s.IsInitializing,
            IsInitialized = s.IsInitialized,
            IsLoading = s.IsLoading,
            CanGoBack = s.CanGoBack,
            CanGoForward = s.CanGoForward,
            CurrentUrl = s.CurrentUrl,
            PageTitle = s.PageTitle,
            IsOffline = s.IsOffline,
            HasError = !available || s.HasError,
            ErrorTitle = available ? s.ErrorTitle : "Microsoft Edge WebView2 Runtime Missing",
            ErrorMessage = available
                ? s.ErrorMessage
                : "MyLaptop requires the Microsoft Edge WebView2 Runtime to display the integrated web workspace. Install the Evergreen Runtime from Microsoft and restart or retry initialization.",
            ErrorCode = available ? s.ErrorCode : "WEBVIEW2_RUNTIME_MISSING",
            IsGoogleSignInFlow = s.IsGoogleSignInFlow,
            StatusText = available ? $"WebView2 {version}" : "WebView2 Runtime not detected"
        });

        return available;
    }

    public async Task InitializeWorkspaceAsync(IWebView2 control)
    {
        if (!CheckWebView2Availability())
        {
            return;
        }

        var homepage = _settingsService.CurrentSettings.GeminiHomepageUrl;
        UpdateState(s => CopyWith(s, isInitializing: true, isLoading: true, hasError: false, errorTitle: null, errorMessage: null, errorCode: null,
            statusText: "Initializing MyLaptop Workspace..."));

        try
        {
            await _webViewService.InitializeAsync(control, homepage).ConfigureAwait(true);
            UpdateState(s => CopyWith(s, isInitializing: false, isInitialized: true, currentUrl: _webViewService.CurrentUrl,
                statusText: "Loading Workspace..."));
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            _loggingService.Error(ex, "WebView2 Runtime missing during initialization.");
            UpdateState(s => CopyWith(s,
                isWebView2Available: false,
                isInitializing: false,
                isInitialized: false,
                isLoading: false,
                hasError: true,
                errorTitle: "Microsoft Edge WebView2 Runtime Missing",
                errorMessage: "Please install the official Microsoft Edge WebView2 Evergreen Runtime to run MyLaptop.",
                errorCode: "WEBVIEW2_RUNTIME_MISSING",
                statusText: "WebView2 Runtime required"));
        }
        catch (Exception ex)
        {
            _loggingService.Error(ex, "WebView2 initialization failed.");
            UpdateState(s => CopyWith(s,
                isInitializing: false,
                isInitialized: false,
                isLoading: false,
                hasError: true,
                errorTitle: "Browser Initialization Failed",
                errorMessage: $"Unable to start the embedded browser engine: {ex.Message}",
                errorCode: "WEBVIEW2_INIT_FAILED",
                statusText: "Browser initialization failed"));
        }
    }

    public void NavigateHome()
    {
        var homepage = _settingsService.CurrentSettings.GeminiHomepageUrl;
        if (!ConfigurationHelper.TryValidateGeminiHomepageUrl(homepage, out var validated, out _))
        {
            validated = AppSettings.DefaultGeminiUrl;
        }

        UpdateState(s => CopyWith(s, hasError: false, errorTitle: null, errorMessage: null, errorCode: null, statusText: "Navigating to Gemini..."));
        _webViewService.Navigate(validated);
    }

    public void Reload()
    {
        var isOnline = CheckNetworkAvailable();
        if (!isOnline)
        {
            UpdateState(s => CopyWith(s,
                isOffline: true,
                isLoading: false,
                hasError: true,
                errorTitle: "No Internet Connection",
                errorMessage: "Your computer appears to be offline. Reconnect to the internet and click Reload to access Google Gemini.",
                errorCode: "NETWORK_OFFLINE",
                statusText: "Offline"));
            return;
        }

        UpdateState(s => CopyWith(s, isOffline: false, hasError: false, errorTitle: null, errorMessage: null, errorCode: null,
            statusText: "Reloading page..."));

        if (_webViewService.IsInitialized)
        {
            _webViewService.Reload();
        }
    }

    public void GoBack() => _webViewService.GoBack();

    public void GoForward() => _webViewService.GoForward();

    public async Task ClearCacheAsync()
    {
        await _webViewService.ClearCacheAsync().ConfigureAwait(true);
        UpdateState(s => CopyWith(s, statusText: "Browser cache cleared"));
    }

    public async Task ResetWorkspaceAsync()
    {
        UpdateState(s => CopyWith(s, isInitializing: true, isLoading: true, hasError: false, errorTitle: null, errorMessage: null, errorCode: null,
            statusText: "Resetting browser workspace..."));

        try
        {
            await _webViewService.ResetWorkspaceAsync(_settingsService.CurrentSettings.GeminiHomepageUrl)
                .ConfigureAwait(true);
            UpdateState(s => CopyWith(s,
                isInitializing: false,
                isInitialized: _webViewService.IsInitialized,
                isLoading: _webViewService.IsInitialized,
                currentUrl: _settingsService.CurrentSettings.GeminiHomepageUrl,
                statusText: "Workspace reset complete"));
        }
        catch (Exception ex)
        {
            _loggingService.Error(ex, "Workspace reset failed.");
            UpdateState(s => CopyWith(s,
                isInitializing: false,
                isLoading: false,
                hasError: true,
                errorTitle: "Workspace Reset Failed",
                errorMessage: ex.Message,
                errorCode: "WORKSPACE_RESET_FAILED",
                statusText: "Reset failed"));
        }
    }

    public async Task ResetWorkspaceAsync(IWebView2 control)
    {
        UpdateState(s => CopyWith(s, isInitializing: true, isLoading: true, hasError: false, errorTitle: null, errorMessage: null, errorCode: null,
            statusText: "Resetting browser workspace..."));

        try
        {
            await _webViewService.ResetWorkspaceAsync(control, _settingsService.CurrentSettings.GeminiHomepageUrl)
                .ConfigureAwait(true);
            UpdateState(s => CopyWith(s, isInitializing: false, isInitialized: true, statusText: "Workspace reset complete"));
        }
        catch (Exception ex)
        {
            _loggingService.Error(ex, "Workspace reset failed.");
            UpdateState(s => CopyWith(s,
                isInitializing: false,
                isLoading: false,
                hasError: true,
                errorTitle: "Workspace Reset Failed",
                errorMessage: ex.Message,
                errorCode: "WORKSPACE_RESET_FAILED",
                statusText: "Reset failed"));
        }
    }

    public void OpenWebView2InstallGuide()
    {
        if (Uri.TryCreate(ConfigurationHelper.WebView2DownloadPageUrl, UriKind.Absolute, out var uri) &&
            ConfigurationHelper.IsSafeExternalLaunchUri(uri))
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    /// <summary>
    /// Maps WebView2 error statuses into clear, actionable messages for offline, Gemini outage, and Google login errors.
    /// </summary>
    public static (string Title, string Message, string Code, bool IsOffline) MapNavigationFailure(
        CoreWebView2WebErrorStatus status,
        string? url,
        bool networkAvailable)
    {
        var isGoogleAuth = Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                           ConfigurationHelper.ClassifyNavigationUri(uri) == NavigationTargetKind.InAppGoogleAuth;

        if (!networkAvailable || status == CoreWebView2WebErrorStatus.Disconnected)
        {
            return (
                "Internet Connection Unavailable",
                "Gemini Desktop cannot reach the network. Verify your Wi-Fi or Ethernet connection, then click Reload.",
                "ERR_INTERNET_DISCONNECTED",
                true);
        }

        if (status is CoreWebView2WebErrorStatus.HostNameNotResolved or CoreWebView2WebErrorStatus.CannotConnect)
        {
            return isGoogleAuth
                ? ("Google Sign-In Unavailable",
                   "Could not connect to Google Account authentication servers. Check your internet connection, firewall, or proxy settings and try again.",
                   status.ToString(),
                   false)
                : ("Google Gemini Unavailable",
                   "Could not reach gemini.google.com. The service may be temporarily unavailable or blocked by a network firewall or VPN.",
                   status.ToString(),
                   false);
        }

        if (status == CoreWebView2WebErrorStatus.Timeout)
        {
            return isGoogleAuth
                ? ("Google Sign-In Timed Out",
                   "The Google Account sign-in page took too long to respond. Click Reload to retry signing in.",
                   "ERR_TIMED_OUT",
                   false)
                : ("Connection Timed Out",
                   "Google Gemini took too long to respond. Check your connection stability and click Reload.",
                   "ERR_TIMED_OUT",
                   false);
        }

        if (status is CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect
            or CoreWebView2WebErrorStatus.CertificateExpired
            or CoreWebView2WebErrorStatus.CertificateRevoked
            or CoreWebView2WebErrorStatus.CertificateIsInvalid)
        {
            return (
                "Security Certificate Verification Failed",
                "A secure HTTPS connection to Google could not be verified. Check your system clock and corporate proxy/antivirus HTTPS settings.",
                status.ToString(),
                false);
        }

        if (isGoogleAuth)
        {
            return (
                "Google Sign-In Interrupted",
                $"Authentication could not be completed ({status}). You can reload the sign-in page or return to the Gemini homepage.",
                status.ToString(),
                false);
        }

        return (
            "Unable to Load Google Gemini",
            $"The page encountered a browser navigation error ({status}). Click Reload to try again.",
            status.ToString(),
            false);
    }

    private void OnNavigationStarted(object? sender, EventArgs e)
    {
        var currentUrl = _webViewService.CurrentUrl;
        var isAuth = Uri.TryCreate(currentUrl, UriKind.Absolute, out var u) &&
                     ConfigurationHelper.ClassifyNavigationUri(u) == NavigationTargetKind.InAppGoogleAuth;

        UpdateState(s => CopyWith(s,
            isLoading: true,
            hasError: false,
            errorTitle: null,
            errorMessage: null,
            errorCode: null,
            currentUrl: currentUrl,
            canGoBack: _webViewService.CanGoBack,
            canGoForward: _webViewService.CanGoForward,
            isGoogleSignInFlow: isAuth,
            statusText: isAuth ? "Signing in with Google Account..." : "Loading Google Gemini..."));
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedInfo e)
    {
        var isAuth = Uri.TryCreate(e.CurrentUrl, UriKind.Absolute, out var u) &&
                     ConfigurationHelper.ClassifyNavigationUri(u) == NavigationTargetKind.InAppGoogleAuth;

        if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            var (title, msg, code, offline) = MapNavigationFailure(e.WebErrorStatus, e.CurrentUrl, CheckNetworkAvailable());
            UpdateState(s => CopyWith(s,
                isLoading: false,
                isOffline: offline,
                hasError: true,
                errorTitle: title,
                errorMessage: msg,
                errorCode: code,
                currentUrl: e.CurrentUrl,
                canGoBack: _webViewService.CanGoBack,
                canGoForward: _webViewService.CanGoForward,
                isGoogleSignInFlow: isAuth,
                statusText: title));
            return;
        }

        if (e.HttpStatusCode >= 500)
        {
            UpdateState(s => CopyWith(s,
                isLoading: false,
                hasError: true,
                errorTitle: "Google Gemini Temporarily Unavailable",
                errorMessage: $"Google Gemini returned HTTP status {e.HttpStatusCode}. Wait a moment and click Reload.",
                errorCode: $"HTTP_{e.HttpStatusCode}",
                currentUrl: e.CurrentUrl,
                canGoBack: _webViewService.CanGoBack,
                canGoForward: _webViewService.CanGoForward,
                isGoogleSignInFlow: isAuth,
                statusText: $"HTTP {e.HttpStatusCode}"));
            return;
        }

        UpdateState(s => CopyWith(s,
            isLoading: false,
            isOffline: !CheckNetworkAvailable(),
            hasError: false,
            errorTitle: null,
            errorMessage: null,
            errorCode: null,
            currentUrl: e.CurrentUrl,
            pageTitle: _webViewService.DocumentTitle,
            canGoBack: _webViewService.CanGoBack,
            canGoForward: _webViewService.CanGoForward,
            isGoogleSignInFlow: isAuth,
            statusText: isAuth ? "Google Account Sign-In" : "Connected to Google Gemini"));
    }

    private void OnDocumentTitleChanged(object? sender, string title)
    {
        UpdateState(s => CopyWith(s, pageTitle: string.IsNullOrWhiteSpace(title) ? "Google Gemini" : title));
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        UpdateState(s => CopyWith(s, canGoBack: _webViewService.CanGoBack, canGoForward: _webViewService.CanGoForward));
    }

    private void OnExternalLinkOpened(object? sender, string info)
    {
        UpdateState(s => CopyWith(s, statusText: info));
    }

    private void OnUnsafeNavigationBlocked(object? sender, string reason)
    {
        UpdateState(s => CopyWith(s, statusText: reason));
    }

    private void OnMicrophonePermissionDenied(object? sender, string reason)
    {
        UpdateState(s => CopyWith(s, statusText: reason));
        MicrophonePermissionBlocked?.Invoke(this, reason);
    }

    private void OnRendererProcessFailed(object? sender, string reason)
    {
        UpdateState(s => CopyWith(s,
            isLoading: false,
            hasError: true,
            errorTitle: "Browser Process Interrupted",
            errorMessage: $"{reason} Click Reload to restart the Gemini workspace view.",
            errorCode: "RENDERER_PROCESS_FAILED",
            statusText: "Browser process interrupted"));
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        UpdateState(s => CopyWith(s,
            isOffline: !e.IsAvailable,
            statusText: e.IsAvailable ? "Network connection restored" : "Internet connection unavailable"));
    }

    private static bool CheckNetworkAvailable()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch
        {
            return true;
        }
    }

    private void UpdateState(Func<BrowserState, BrowserState> updater)
    {
        _state = updater(_state);
        StateChanged?.Invoke(this, _state);
    }

    private static BrowserState CopyWith(
        BrowserState source,
        bool? isWebView2Available = null,
        string? webView2Version = null,
        bool? isInitializing = null,
        bool? isInitialized = null,
        bool? isLoading = null,
        bool? canGoBack = null,
        bool? canGoForward = null,
        string? currentUrl = null,
        string? pageTitle = null,
        bool? isOffline = null,
        bool? hasError = null,
        string? errorTitle = null,
        string? errorMessage = null,
        string? errorCode = null,
        bool? isGoogleSignInFlow = null,
        string? statusText = null)
    {
        return new BrowserState
        {
            IsWebView2RuntimeAvailable = isWebView2Available ?? source.IsWebView2RuntimeAvailable,
            WebView2RuntimeVersion = webView2Version ?? source.WebView2RuntimeVersion,
            IsInitializing = isInitializing ?? source.IsInitializing,
            IsInitialized = isInitialized ?? source.IsInitialized,
            IsLoading = isLoading ?? source.IsLoading,
            CanGoBack = canGoBack ?? source.CanGoBack,
            CanGoForward = canGoForward ?? source.CanGoForward,
            CurrentUrl = currentUrl ?? source.CurrentUrl,
            PageTitle = pageTitle ?? source.PageTitle,
            IsOffline = isOffline ?? source.IsOffline,
            HasError = hasError ?? source.HasError,
            ErrorTitle = hasError == false ? null : (errorTitle ?? source.ErrorTitle),
            ErrorMessage = hasError == false ? null : (errorMessage ?? source.ErrorMessage),
            ErrorCode = hasError == false ? null : (errorCode ?? source.ErrorCode),
            IsGoogleSignInFlow = isGoogleSignInFlow ?? source.IsGoogleSignInFlow,
            StatusText = statusText ?? source.StatusText
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;

        _webViewService.NavigationStarted -= OnNavigationStarted;
        _webViewService.NavigationCompleted -= OnNavigationCompleted;
        _webViewService.DocumentTitleChanged -= OnDocumentTitleChanged;
        _webViewService.HistoryChanged -= OnHistoryChanged;
        _webViewService.ExternalLinkOpened -= OnExternalLinkOpened;
        _webViewService.UnsafeNavigationBlocked -= OnUnsafeNavigationBlocked;
        _webViewService.RendererProcessFailed -= OnRendererProcessFailed;
        _webViewService.MicrophonePermissionDenied -= OnMicrophonePermissionDenied;
    }
}
