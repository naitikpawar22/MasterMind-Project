using System.Diagnostics;
using System.IO;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GeminiDesktop.Services;

/// <summary>
/// Navigation completion details emitted by WebViewService.
/// </summary>
public sealed class WebViewNavigationCompletedInfo
{
    public bool IsSuccess { get; init; }
    public CoreWebView2WebErrorStatus WebErrorStatus { get; init; }
    public string CurrentUrl { get; init; } = string.Empty;
    public int HttpStatusCode { get; init; }
}

/// <summary>
/// Low-level wrapper managing WebView2 runtime detection, environment initialization,
/// secure navigation filtering, session persistence, and browsing data maintenance.
/// </summary>
public interface IWebViewService : IDisposable
{
    bool IsInitialized { get; }
    bool CanGoBack { get; }
    bool CanGoForward { get; }
    bool IsDocumentPlayingAudio { get; }
    bool IsBrowserAudioMuted { get; }
    string CurrentUrl { get; }
    string DocumentTitle { get; }
    string UserDataDirectory { get; }

    event EventHandler? NavigationStarted;
    event EventHandler<WebViewNavigationCompletedInfo>? NavigationCompleted;
    event EventHandler<string>? DocumentTitleChanged;
    event EventHandler? HistoryChanged;
    event EventHandler<string>? ExternalLinkOpened;
    event EventHandler<string>? UnsafeNavigationBlocked;
    event EventHandler<string>? RendererProcessFailed;
    event EventHandler<string>? MicrophonePermissionDenied;
    event EventHandler<bool>? DocumentPlayingAudioChanged;
    event EventHandler<bool>? BrowserAudioMutedChanged;

    bool IsWebView2RuntimeInstalled(out string? version);
    Task InitializeAsync(IWebView2 control, string initialUrl);
    void Navigate(string url);
    void Reload();
    void Stop();
    void GoBack();
    void GoForward();
    void SetBrowserAudioMuted(bool muted);
    Task ClearCacheAsync();
    Task ResetWorkspaceAsync(string initialUrl);
    Task ResetWorkspaceAsync(IWebView2 control, string initialUrl);
}

public sealed class WebViewService : IWebViewService
{
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    private readonly IMicrophoneService _microphoneService;

    private IWebView2? _webView;
    private CoreWebView2Environment? _environment;
    private string? _lastValidatedTargetUrl;
    private int _lastHttpStatusCode;
    private bool _desiredMuted;
    private bool _disposed;

    public WebViewService(
        ILoggingService loggingService,
        ISettingsService settingsService,
        IMicrophoneService microphoneService)
    {
        _loggingService = loggingService;
        _settingsService = settingsService;
        _microphoneService = microphoneService;
        UserDataDirectory = ConfigurationHelper.GetWebView2UserDataDirectory();
    }

    public bool IsInitialized => _webView?.CoreWebView2 is not null;
    public bool CanGoBack => _webView?.CoreWebView2?.CanGoBack ?? false;
    public bool CanGoForward => _webView?.CoreWebView2?.CanGoForward ?? false;
    public bool IsDocumentPlayingAudio => _webView?.CoreWebView2?.IsDocumentPlayingAudio ?? false;
    public bool IsBrowserAudioMuted => _webView?.CoreWebView2?.IsMuted ?? _desiredMuted;
    public string CurrentUrl => _webView?.CoreWebView2?.Source ?? _lastValidatedTargetUrl ?? AppSettings.DefaultGeminiUrl;
    public string DocumentTitle => _webView?.CoreWebView2?.DocumentTitle ?? "Google Gemini";
    public string UserDataDirectory { get; }

    public event EventHandler? NavigationStarted;
    public event EventHandler<WebViewNavigationCompletedInfo>? NavigationCompleted;
    public event EventHandler<string>? DocumentTitleChanged;
    public event EventHandler? HistoryChanged;
    public event EventHandler<string>? ExternalLinkOpened;
    public event EventHandler<string>? UnsafeNavigationBlocked;
    public event EventHandler<string>? RendererProcessFailed;
    public event EventHandler<string>? MicrophonePermissionDenied;
    public event EventHandler<bool>? DocumentPlayingAudioChanged;
    public event EventHandler<bool>? BrowserAudioMutedChanged;

    public bool IsWebView2RuntimeInstalled(out string? version)
    {
        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrWhiteSpace(version);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            version = null;
            return false;
        }
        catch (Exception ex)
        {
            _loggingService.Warning("WebView2 runtime detection returned an error: {Message}", ex.Message);
            version = null;
            return false;
        }
    }

    public async Task InitializeAsync(IWebView2 control, string initialUrl)
    {
        ArgumentNullException.ThrowIfNull(control);

        if (_webView is not null && ReferenceEquals(_webView, control) && control.CoreWebView2 is not null)
        {
            return;
        }

        DetachEvents();
        _webView = control;

        ConfigurationHelper.EnsureDirectoryExists(UserDataDirectory);
        _environment ??= await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: UserDataDirectory).ConfigureAwait(true);

        await control.EnsureCoreWebView2Async(_environment).ConfigureAwait(true);

        var core = control.CoreWebView2 ?? throw new InvalidOperationException("CoreWebView2 failed to initialize.");
        ConfigureSecuritySettings(core.Settings);
        AttachEvents(core);

        _loggingService.Information("WebView2 initialized cleanly. UserDataDirectory={Path}", UserDataDirectory);
        Navigate(initialUrl);
    }

    public void Navigate(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            _loggingService.Warning("Rejected malformed navigation URL.");
            return;
        }

        var kind = ConfigurationHelper.ClassifyNavigationUri(uri);
        if (kind == NavigationTargetKind.BlockedUnsafe)
        {
            var sanitized = ConfigurationHelper.SanitizeUriForLogging(uri);
            _loggingService.Warning("Blocked unsafe navigation attempt to {Uri}.", sanitized);
            UnsafeNavigationBlocked?.Invoke(this, "Unsafe URL scheme blocked by security policy.");
            return;
        }

        if (kind == NavigationTargetKind.ExternalHttps)
        {
            LaunchExternalUri(uri);
            return;
        }

        _lastValidatedTargetUrl = uri.AbsoluteUri;
        _lastHttpStatusCode = 0;
        _webView?.CoreWebView2?.Navigate(uri.AbsoluteUri);
    }

    public void Reload()
    {
        if (_webView?.CoreWebView2 is null)
        {
            return;
        }

        _lastHttpStatusCode = 0;
        _webView.CoreWebView2.Reload();
    }

    public void Stop() => _webView?.CoreWebView2?.Stop();

    public void GoBack()
    {
        if (_webView?.CoreWebView2?.CanGoBack == true)
        {
            _webView.CoreWebView2.GoBack();
        }
    }

    public void GoForward()
    {
        if (_webView?.CoreWebView2?.CanGoForward == true)
        {
            _webView.CoreWebView2.GoForward();
        }
    }

    public void SetBrowserAudioMuted(bool muted)
    {
        _desiredMuted = muted;
        var core = _webView?.CoreWebView2;
        if (core is not null)
        {
            core.IsMuted = muted;
        }
        else
        {
            BrowserAudioMutedChanged?.Invoke(this, muted);
        }

        _loggingService.Information("WebView2 Gemini audio mute set to {Muted}.", muted);
    }

    public async Task ClearCacheAsync()
    {
        if (_webView?.CoreWebView2?.Profile is null)
        {
            return;
        }

        var kinds = CoreWebView2BrowsingDataKinds.CacheStorage | CoreWebView2BrowsingDataKinds.DiskCache;
        await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync(kinds).ConfigureAwait(true);
        _loggingService.Information("Cleared WebView2 disk and cache storage while preserving user session cookies.");
    }

    public async Task ResetWorkspaceAsync(string initialUrl)
    {
        if (_webView is not null)
        {
            await ResetWorkspaceAsync(_webView, initialUrl).ConfigureAwait(true);
            return;
        }

        if (Directory.Exists(UserDataDirectory))
        {
            Directory.Delete(UserDataDirectory, recursive: true);
        }

        ConfigurationHelper.EnsureDirectoryExists(UserDataDirectory);
        _loggingService.Information("Cleared unattached WebView2 user data directory.");
    }

    public async Task ResetWorkspaceAsync(IWebView2 control, string initialUrl)
    {
        ArgumentNullException.ThrowIfNull(control);

        if (control.CoreWebView2?.Profile is not null)
        {
            await control.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile)
                .ConfigureAwait(true);
        }

        DetachEvents();
        if (control is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _environment = null;
        _webView = null;

        if (Directory.Exists(UserDataDirectory))
        {
            try
            {
                Directory.Delete(UserDataDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                _loggingService.Warning("Partial cleanup on WebView2UserData directory during workspace reset: {Message}", ex.Message);
            }
        }

        _loggingService.Information("Browser workspace reset completed; reinitializing clean WebView2 session.");
        await InitializeAsync(control, initialUrl).ConfigureAwait(true);
    }

    private static void ConfigureSecuritySettings(CoreWebView2Settings settings)
    {
        // Never save raw Google account passwords in browser autofill storage.
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = true;
        settings.AreDefaultContextMenusEnabled = true;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = true;
        settings.AreBrowserAcceleratorKeysEnabled = true;
        settings.IsBuiltInErrorPageEnabled = false; // We display rich native WPF recovery UI instead.
#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
#endif
    }

    private void AttachEvents(CoreWebView2 core)
    {
        core.IsMuted = _desiredMuted;
        core.NavigationStarting += OnCoreNavigationStarting;
        core.NavigationCompleted += OnCoreNavigationCompleted;
        core.NewWindowRequested += OnCoreNewWindowRequested;
        core.DocumentTitleChanged += OnCoreDocumentTitleChanged;
        core.HistoryChanged += OnCoreHistoryChanged;
        core.ProcessFailed += OnCoreProcessFailed;
        core.PermissionRequested += OnCorePermissionRequested;
        core.WebResourceResponseReceived += OnWebResourceResponseReceived;
        core.IsDocumentPlayingAudioChanged += OnCoreIsDocumentPlayingAudioChanged;
        core.IsMutedChanged += OnCoreIsMutedChanged;
        DocumentPlayingAudioChanged?.Invoke(this, core.IsDocumentPlayingAudio);
        BrowserAudioMutedChanged?.Invoke(this, core.IsMuted);
    }

    private void DetachEvents()
    {
        var core = _webView?.CoreWebView2;
        if (core is null)
        {
            return;
        }

        core.NavigationStarting -= OnCoreNavigationStarting;
        core.NavigationCompleted -= OnCoreNavigationCompleted;
        core.NewWindowRequested -= OnCoreNewWindowRequested;
        core.DocumentTitleChanged -= OnCoreDocumentTitleChanged;
        core.HistoryChanged -= OnCoreHistoryChanged;
        core.ProcessFailed -= OnCoreProcessFailed;
        core.PermissionRequested -= OnCorePermissionRequested;
        core.WebResourceResponseReceived -= OnWebResourceResponseReceived;
        core.IsDocumentPlayingAudioChanged -= OnCoreIsDocumentPlayingAudioChanged;
        core.IsMutedChanged -= OnCoreIsMutedChanged;
    }

    private void OnCoreNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            e.Cancel = true;
            return;
        }

        var kind = ConfigurationHelper.ClassifyNavigationUri(uri);
        var sanitized = ConfigurationHelper.SanitizeUriForLogging(uri);

        switch (kind)
        {
            case NavigationTargetKind.InAppGemini:
            case NavigationTargetKind.InAppGoogleAuth:
                _lastHttpStatusCode = 0;
                _loggingService.Debug("Navigating in-app to {Url}", sanitized);
                NavigationStarted?.Invoke(this, EventArgs.Empty);
                break;

            case NavigationTargetKind.ExternalHttps:
                e.Cancel = true;
                _loggingService.Information("External HTTPS navigation intercepted ({Url}); delegating to system browser.", sanitized);
                LaunchExternalUri(uri);
                break;

            default:
                e.Cancel = true;
                _loggingService.Warning("Blocked unsafe navigation URI: {Url}", sanitized);
                UnsafeNavigationBlocked?.Invoke(this, $"Navigation to untrusted scheme '{uri.Scheme}' was blocked.");
                break;
        }
    }

    private void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (e.Request?.Uri is null || _webView?.CoreWebView2 is null)
        {
            return;
        }

        if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var reqUri) &&
            Uri.TryCreate(_webView.CoreWebView2.Source, UriKind.Absolute, out var activeUri) &&
            string.Equals(reqUri.Host, activeUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            _lastHttpStatusCode = e.Response.StatusCode;
        }
    }

    private void OnCoreNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var currentUrl = _webView?.CoreWebView2?.Source ?? _lastValidatedTargetUrl ?? AppSettings.DefaultGeminiUrl;
        if (!e.IsSuccess)
        {
            _loggingService.Warning("Navigation completed with error status {Status} at {Url}",
                e.WebErrorStatus, ConfigurationHelper.SanitizeUriForLogging(Uri.TryCreate(currentUrl, UriKind.Absolute, out var u) ? u : null));
        }

        NavigationCompleted?.Invoke(this, new WebViewNavigationCompletedInfo
        {
            IsSuccess = e.IsSuccess,
            WebErrorStatus = e.WebErrorStatus,
            CurrentUrl = currentUrl,
            HttpStatusCode = _lastHttpStatusCode
        });
    }

    private void OnCoreNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            return;
        }

        var kind = ConfigurationHelper.ClassifyNavigationUri(uri);
        if (kind is NavigationTargetKind.InAppGemini or NavigationTargetKind.InAppGoogleAuth)
        {
            _webView?.CoreWebView2?.Navigate(uri.AbsoluteUri);
            return;
        }

        if (kind == NavigationTargetKind.ExternalHttps)
        {
            LaunchExternalUri(uri);
            return;
        }

        UnsafeNavigationBlocked?.Invoke(this, $"Blocked unsafe popup navigation ({uri.Scheme}).");
    }

    private void OnCoreDocumentTitleChanged(object? sender, object e)
    {
        if (_webView?.CoreWebView2 is not null)
        {
            DocumentTitleChanged?.Invoke(this, _webView.CoreWebView2.DocumentTitle);
        }
    }

    private void OnCoreHistoryChanged(object? sender, object e) =>
        HistoryChanged?.Invoke(this, EventArgs.Empty);

    private void OnCoreIsDocumentPlayingAudioChanged(object? sender, object e)
    {
        if (_webView?.CoreWebView2 is not null)
        {
            DocumentPlayingAudioChanged?.Invoke(this, _webView.CoreWebView2.IsDocumentPlayingAudio);
        }
    }

    private void OnCoreIsMutedChanged(object? sender, object e)
    {
        if (_webView?.CoreWebView2 is not null)
        {
            BrowserAudioMutedChanged?.Invoke(this, _webView.CoreWebView2.IsMuted);
        }
    }

    private void OnCoreProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _loggingService.Error(null, "WebView2 process failure: Kind={Kind}, Reason={Reason}",
            e.ProcessFailedKind, e.Reason);
        RendererProcessFailed?.Invoke(this, $"Browser renderer process failed ({e.ProcessFailedKind}).");
    }

    private void OnCorePermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
            ConfigurationHelper.ClassifyNavigationUri(uri) != NavigationTargetKind.InAppGemini)
        {
            e.State = CoreWebView2PermissionState.Deny;
            return;
        }

        if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
        {
            if (!_settingsService.CurrentSettings.AllowMicrophoneAccess)
            {
                e.State = CoreWebView2PermissionState.Deny;
                _loggingService.Information("Denied WebView2 microphone permission per MyLaptop workspace setting.");
                MicrophonePermissionDenied?.Invoke(this, "Microphone access is disabled in MyLaptop Settings.");
                return;
            }

            if (_microphoneService.CurrentState.PermissionState == MicrophonePermissionState.DeniedByWindows)
            {
                e.State = CoreWebView2PermissionState.Deny;
                _loggingService.Warning("Denied WebView2 microphone permission because Windows Microphone privacy is off.");
                MicrophonePermissionDenied?.Invoke(this, "Windows Microphone privacy permission is turned off.");
                return;
            }

            e.State = CoreWebView2PermissionState.Allow;
            _loggingService.Information("Granted WebView2 microphone permission to Gemini workspace.");
            return;
        }

        if (e.PermissionKind is CoreWebView2PermissionKind.ClipboardRead
            or CoreWebView2PermissionKind.Notifications)
        {
            e.State = CoreWebView2PermissionState.Allow;
            return;
        }

        e.State = CoreWebView2PermissionState.Deny;
    }

    private void LaunchExternalUri(Uri uri)
    {
        if (!_settingsService.CurrentSettings.OpenExternalLinksInSystemBrowser)
        {
            ExternalLinkOpened?.Invoke(this, $"External link suppressed by policy: {uri.Host}");
            return;
        }

        if (!ConfigurationHelper.IsSafeExternalLaunchUri(uri))
        {
            UnsafeNavigationBlocked?.Invoke(this, "Only HTTPS external links can be launched.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            ExternalLinkOpened?.Invoke(this, $"Opened external link in default browser: {uri.Host}");
        }
        catch (Exception ex)
        {
            _loggingService.Error(ex, "Failed to launch external browser for {Host}", uri.Host);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachEvents();
        _webView = null;
        _environment = null;
    }
}
