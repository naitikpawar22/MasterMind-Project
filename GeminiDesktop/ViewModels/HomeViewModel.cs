using System.Windows.Input;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using GeminiDesktop.Services;

namespace GeminiDesktop.ViewModels;

/// <summary>
/// Dashboard ViewModel for the Home view, summarizing readiness, privacy state, and quick actions.
/// </summary>
public sealed class HomeViewModel : ViewModelBase
{
    private readonly IGeminiBrowserService _browserService;
    private readonly IPrivacyService _privacyService;
    private readonly ISettingsService _settingsService;

    private string _geminiTargetUrl = AppSettings.DefaultGeminiUrl;
    private string _webView2Status = "Checking runtime...";
    private string _networkStatus = "Online";
    private string _privacySummary = "Standard Visibility";
    private string _sessionStorageLocation = string.Empty;

    public HomeViewModel(
        IGeminiBrowserService browserService,
        IPrivacyService privacyService,
        ISettingsService settingsService)
    {
        _browserService = browserService;
        _privacyService = privacyService;
        _settingsService = settingsService;

        OpenWorkspaceCommand = new RelayCommand(() => OpenWorkspaceRequested?.Invoke(this, EventArgs.Empty));
        OpenPrivacyCommand = new RelayCommand(() => OpenPrivacyRequested?.Invoke(this, EventArgs.Empty));
        OpenSettingsCommand = new RelayCommand(() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty));

        _browserService.StateChanged += (_, _) => ApplyCurrentSnapshot();
        _privacyService.PrivacyStatusChanged += (_, _) => ApplyCurrentSnapshot();
        _settingsService.SettingsChanged += (_, _) => ApplyCurrentSnapshot();

        RefreshOverview();
    }

    public event EventHandler? OpenWorkspaceRequested;
    public event EventHandler? OpenPrivacyRequested;
    public event EventHandler? OpenSettingsRequested;

    public ICommand OpenWorkspaceCommand { get; }
    public ICommand OpenPrivacyCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    public string GeminiTargetUrl
    {
        get => _geminiTargetUrl;
        private set => SetProperty(ref _geminiTargetUrl, value);
    }

    public string WebView2Status
    {
        get => _webView2Status;
        private set => SetProperty(ref _webView2Status, value);
    }

    public string NetworkStatus
    {
        get => _networkStatus;
        private set => SetProperty(ref _networkStatus, value);
    }

    public string PrivacySummary
    {
        get => _privacySummary;
        private set => SetProperty(ref _privacySummary, value);
    }

    public string SessionStorageLocation
    {
        get => _sessionStorageLocation;
        private set => SetProperty(ref _sessionStorageLocation, value);
    }

    public void RefreshOverview()
    {
        _browserService.CheckWebView2Availability();
        ApplyCurrentSnapshot();
    }

    private void ApplyCurrentSnapshot()
    {
        var browser = _browserService.State;
        var privacy = _privacyService.CurrentStatus;

        GeminiTargetUrl = _settingsService.CurrentSettings.GeminiHomepageUrl;
        WebView2Status = browser.IsWebView2RuntimeAvailable
            ? $"Edge WebView2 {browser.WebView2RuntimeVersion ?? "Ready"}"
            : "WebView2 Runtime Missing";
        NetworkStatus = browser.IsOffline ? "Offline" : "Connected";
        PrivacySummary = privacy.StatusSummary;
        SessionStorageLocation = ConfigurationHelper.GetWebView2UserDataDirectory();
    }
}
