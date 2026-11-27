using System.Reflection;
using System.Windows;
using System.Windows.Input;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using GeminiDesktop.Services;

namespace GeminiDesktop.ViewModels;

/// <summary>
/// ViewModel for General, Appearance, Browser, Dual Audio & Speech Transcription, Privacy Summary, and About sections in Settings.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IWindowManagementService _windowService;
    private readonly IGeminiBrowserService _browserService;
    private readonly IPrivacyService _privacyService;
    private readonly IMicrophoneService _microphoneService;
    private readonly ISystemAudioService _systemAudioService;
    private readonly ISpeechTranscriptionService _transcriptionService;
    private readonly IDialogService _dialogService;

    private MicrophoneState _microphoneState;
    private SystemAudioState _systemAudioState;
    private TranscriptionState _transcriptionState;

    private LaunchDestination _launchBehavior;
    private bool _launchOnWindowsStartup;
    private bool _startMinimized;
    private bool _showMiniPopupWhenMinimized;
    private bool _rememberLastWindowPosition;
    private DefaultWindowSizePreset _windowSizePreset;
    private double _defaultWindowWidth;
    private double _defaultWindowHeight;
    private AppThemeMode _selectedTheme;
    private string _geminiHomepageUrl = AppSettings.DefaultGeminiUrl;
    private bool _openExternalLinksInSystemBrowser;
    private bool _enableCaptureExclusionByDefault;
    private double _windowOpacity = 1.0;

    private string _urlValidationError = string.Empty;
    private string _feedbackMessage = string.Empty;

    private string _privacyCapabilitySummary = string.Empty;
    private string _privacyCurrentStatus = string.Empty;
    private string _privacyLimitationSummary = string.Empty;

    public SettingsViewModel(
        ISettingsService settingsService,
        IThemeService themeService,
        IWindowManagementService windowService,
        IGeminiBrowserService browserService,
        IPrivacyService privacyService,
        IMicrophoneService microphoneService,
        ISystemAudioService systemAudioService,
        ISpeechTranscriptionService transcriptionService,
        IDialogService dialogService)
    {
        _settingsService = settingsService;
        _themeService = themeService;
        _windowService = windowService;
        _browserService = browserService;
        _privacyService = privacyService;
        _microphoneService = microphoneService;
        _systemAudioService = systemAudioService;
        _transcriptionService = transcriptionService;
        _dialogService = dialogService;

        _microphoneState = _microphoneService.CurrentState;
        _systemAudioState = _systemAudioService.CurrentState;
        _transcriptionState = _transcriptionService.CurrentState;

        SaveSettingsCommand = new RelayCommand(SaveSettings);
        ResetToDefaultsCommand = new RelayCommand(ResetAllSettingsWithConfirmation);
        ApplyDefaultWindowSizeCommand = new RelayCommand(ApplyConfiguredWindowSizeNow);
        ResetHomepageUrlCommand = new RelayCommand(ResetHomepageUrl);
        RefreshBrowserSessionCommand = new RelayCommand(RefreshBrowserSession);
        ClearBrowserCacheCommand = new AsyncRelayCommand(ClearBrowserCacheAsync);
        ResetBrowserWorkspaceCommand = new AsyncRelayCommand(ResetBrowserWorkspaceAsync);
        OpenPrivacyModuleCommand = new RelayCommand(() => NavigateToPrivacyRequested?.Invoke(this, EventArgs.Empty));
        OpenAboutViewCommand = new RelayCommand(() => NavigateToAboutRequested?.Invoke(this, EventArgs.Empty));
        SetOpacityPresetCommand = new RelayCommand<string>(ApplyOpacityPreset);

        StartMicrophoneCommand = new RelayCommand(
            () => _microphoneService.StartListening(),
            () => !IsMicrophoneListening && IsMicrophoneDeviceAvailable && !IsMicrophonePermissionDenied);
        StopMicrophoneCommand = new RelayCommand(
            () => _microphoneService.StopListening(),
            () => IsMicrophoneListening);
        RefreshMicrophonesCommand = new RelayCommand(() => _microphoneService.RefreshDevices());
        ToggleMicrophoneMuteCommand = new RelayCommand(() => _microphoneService.ToggleMute(), () => _microphoneState.IsDeviceAvailable);
        OpenWindowsMicPrivacyCommand = new RelayCommand(() => _microphoneService.OpenWindowsMicrophonePrivacySettings());

        StartSystemAudioCommand = new RelayCommand(
            () => _systemAudioService.StartListening(),
            () => !IsSystemAudioListening && IsSystemAudioDeviceAvailable);
        StopSystemAudioCommand = new RelayCommand(
            () => _systemAudioService.StopListening(),
            () => IsSystemAudioListening);
        RefreshSystemAudioCommand = new RelayCommand(() => _systemAudioService.RefreshDevice());
        ToggleSystemAudioMuteCommand = new RelayCommand(() => _systemAudioService.ToggleMute(), () => _systemAudioState.IsDeviceAvailable);
        OpenWindowsSoundSettingsCommand = new RelayCommand(() => _systemAudioService.OpenWindowsSoundSettings());

        _privacyService.PrivacyStatusChanged += (_, _) => RefreshPrivacyAndAboutSummary();
        _microphoneService.StateChanged += (_, micState) => RunOnUi(() => ApplyMicrophoneState(micState));
        _systemAudioService.StateChanged += (_, sysState) => RunOnUi(() => ApplySystemAudioState(sysState));
        _transcriptionService.StateChanged += (_, txState) => RunOnUi(() => ApplyTranscriptionState(txState));
        _windowService.WindowOpacityChanged += (_, opacity) =>
        {
            if (Math.Abs(_windowOpacity - opacity) > 0.001)
            {
                _windowOpacity = opacity;
                OnPropertyChanged(nameof(WindowOpacity));
                OnPropertyChanged(nameof(WindowOpacityPercent));
                OnPropertyChanged(nameof(BackgroundVisibilityPercent));
            }
        };

        LoadFromSettings(_settingsService.CurrentSettings);
        RefreshPrivacyAndAboutSummary();
    }

    public event EventHandler? NavigateToPrivacyRequested;
    public event EventHandler? NavigateToAboutRequested;

    public ICommand SaveSettingsCommand { get; }
    public ICommand ResetToDefaultsCommand { get; }
    public ICommand ApplyDefaultWindowSizeCommand { get; }
    public ICommand ResetHomepageUrlCommand { get; }
    public ICommand RefreshBrowserSessionCommand { get; }
    public ICommand ClearBrowserCacheCommand { get; }
    public ICommand ResetBrowserWorkspaceCommand { get; }
    public ICommand OpenPrivacyModuleCommand { get; }
    public ICommand OpenAboutViewCommand { get; }
    public ICommand SetOpacityPresetCommand { get; }

    public RelayCommand StartMicrophoneCommand { get; }
    public RelayCommand StopMicrophoneCommand { get; }
    public ICommand RefreshMicrophonesCommand { get; }
    public RelayCommand ToggleMicrophoneMuteCommand { get; }
    public ICommand OpenWindowsMicPrivacyCommand { get; }

    public RelayCommand StartSystemAudioCommand { get; }
    public RelayCommand StopSystemAudioCommand { get; }
    public ICommand RefreshSystemAudioCommand { get; }
    public RelayCommand ToggleSystemAudioMuteCommand { get; }
    public ICommand OpenWindowsSoundSettingsCommand { get; }

    // Microphone Input Controls
    public IReadOnlyList<MicrophoneDevice> MicrophoneDevices => _microphoneState.Devices;

    public string SelectedMicrophoneId
    {
        get => _microphoneState.SelectedDeviceId;
        set
        {
            if (!string.Equals(_microphoneState.SelectedDeviceId, value ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                _microphoneService.SelectDevice(value ?? string.Empty);
            }
        }
    }

    public bool AllowMicrophoneAccess
    {
        get => _microphoneState.IsWorkspaceMicrophoneAllowed;
        set
        {
            if (_microphoneState.IsWorkspaceMicrophoneAllowed != value)
            {
                _microphoneService.SetWorkspaceMicrophoneAllowed(value);
                OnPropertyChanged();
            }
        }
    }

    public bool EnableMicrophoneLevelMonitoring
    {
        get => _microphoneState.IsMonitoring || _settingsService.CurrentSettings.EnableMicrophoneLevelMonitoring;
        set
        {
            if (_settingsService.CurrentSettings.EnableMicrophoneLevelMonitoring != value)
            {
                _microphoneService.SetMonitoringEnabled(value);
                OnPropertyChanged();
            }
        }
    }

    public bool IsMicrophoneListening => _microphoneState.IsMonitoring;
    public bool IsMicrophonePermissionDenied => _microphoneState.PermissionState == MicrophonePermissionState.DeniedByWindows;
    public int MicrophoneLevelPercent => _microphoneState.InputLevelPercent;
    public bool IsMicrophoneMuted => _microphoneState.IsMuted;
    public bool IsMicrophoneDeviceAvailable => _microphoneState.IsDeviceAvailable;
    public string MicrophoneMuteButtonText => _microphoneState.IsMuted ? "Unmute Microphone" : "Mute Microphone";
    public string MicrophoneStatusText => _microphoneState.StatusText;
    public string MicrophoneErrorMessage => _microphoneState.ErrorMessage ?? string.Empty;
    public string ActiveMicrophoneName => _microphoneState.ActiveDeviceName;

    // System Audio Loopback Controls
    public bool EnableSystemAudioMonitoring
    {
        get => _systemAudioState.IsListening || _settingsService.CurrentSettings.EnableSystemAudioMonitoring;
        set
        {
            if (_settingsService.CurrentSettings.EnableSystemAudioMonitoring != value)
            {
                if (value) _systemAudioService.StartListening();
                else _systemAudioService.StopListening();
                OnPropertyChanged();
            }
        }
    }

    public bool IsSystemAudioListening => _systemAudioState.IsListening;
    public int SystemAudioLevelPercent => _systemAudioState.OutputLevelPercent;
    public bool IsSystemAudioMuted => _systemAudioState.IsMuted;
    public bool IsSystemAudioDeviceAvailable => _systemAudioState.IsDeviceAvailable;
    public string SystemAudioMuteButtonText => _systemAudioState.IsMuted ? "Unmute System Audio" : "Mute System Audio";
    public string SystemAudioStatusText => _systemAudioState.StatusText;
    public string SystemAudioErrorMessage => _systemAudioState.ErrorMessage ?? string.Empty;
    public string ActiveSystemAudioDeviceName => _systemAudioState.ActiveDeviceName;

    // Speech Transcription Settings
    public bool EnableLiveSpeechTranscription
    {
        get => _transcriptionState.IsEnabled;
        set
        {
            if (_transcriptionState.IsEnabled != value)
            {
                _transcriptionService.SetEnabled(value);
                OnPropertyChanged();
            }
        }
    }

    public string TranscriptionStatusText => _transcriptionState.StatusText;
    public string RecognizerDisplayName => _transcriptionState.RecognizerDisplayName;
    public string TranscriptionErrorMessage => _transcriptionState.ErrorMessage ?? string.Empty;

    public double WindowOpacity
    {
        get => _windowOpacity;
        set
        {
            var clamped = AppSettings.ClampOpacity(value);
            if (SetProperty(ref _windowOpacity, clamped))
            {
                _windowService.SetWindowOpacity(clamped, persist: false);
                OnPropertyChanged(nameof(WindowOpacityPercent));
                OnPropertyChanged(nameof(BackgroundVisibilityPercent));
            }
        }
    }

    public int WindowOpacityPercent => (int)Math.Round(WindowOpacity * 100);
    public int BackgroundVisibilityPercent => Math.Max(0, 100 - WindowOpacityPercent);

    public LaunchDestination LaunchBehavior
    {
        get => _launchBehavior;
        set => SetProperty(ref _launchBehavior, value);
    }

    public bool LaunchOnWindowsStartup
    {
        get => _launchOnWindowsStartup;
        set => SetProperty(ref _launchOnWindowsStartup, value);
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set => SetProperty(ref _startMinimized, value);
    }

    public bool ShowMiniPopupWhenMinimized
    {
        get => _showMiniPopupWhenMinimized;
        set => SetProperty(ref _showMiniPopupWhenMinimized, value);
    }

    public bool RememberLastWindowPosition
    {
        get => _rememberLastWindowPosition;
        set => SetProperty(ref _rememberLastWindowPosition, value);
    }

    public DefaultWindowSizePreset WindowSizePreset
    {
        get => _windowSizePreset;
        set
        {
            if (SetProperty(ref _windowSizePreset, value))
            {
                OnPropertyChanged(nameof(IsCustomWindowSize));
                if (value != DefaultWindowSizePreset.Custom)
                {
                    var (w, h) = AppSettings.GetPresetDimensions(value, DefaultWindowWidth, DefaultWindowHeight);
                    DefaultWindowWidth = w;
                    DefaultWindowHeight = h;
                }
            }
        }
    }

    public bool IsCustomWindowSize => WindowSizePreset == DefaultWindowSizePreset.Custom;

    public double DefaultWindowWidth
    {
        get => _defaultWindowWidth;
        set => SetProperty(ref _defaultWindowWidth, value);
    }

    public double DefaultWindowHeight
    {
        get => _defaultWindowHeight;
        set => SetProperty(ref _defaultWindowHeight, value);
    }

    public AppThemeMode SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value))
            {
                _themeService.ApplyTheme(value);
            }
        }
    }

    public string GeminiHomepageUrl
    {
        get => _geminiHomepageUrl;
        set
        {
            if (SetProperty(ref _geminiHomepageUrl, value))
            {
                ValidateUrlInline();
            }
        }
    }

    public bool OpenExternalLinksInSystemBrowser
    {
        get => _openExternalLinksInSystemBrowser;
        set => SetProperty(ref _openExternalLinksInSystemBrowser, value);
    }

    public bool EnableCaptureExclusionByDefault
    {
        get => _enableCaptureExclusionByDefault;
        set => SetProperty(ref _enableCaptureExclusionByDefault, value);
    }

    public bool IsPrivacyModeSupported => _privacyService.CurrentStatus.IsExcludeFromCaptureSupported;

    public bool IsPrivacyModeEnabled
    {
        get => _privacyService.CurrentStatus.IsCaptureExclusionActive;
        set
        {
            if (_privacyService.CurrentStatus.IsCaptureExclusionActive == value)
            {
                return;
            }

            if (!PrivacyService.TryChangePrivacyModeWithPassword(_privacyService, _dialogService, value))
            {
                OnPropertyChanged(nameof(IsPrivacyModeEnabled));
                Application.Current?.Dispatcher?.BeginInvoke(
                    () => OnPropertyChanged(nameof(IsPrivacyModeEnabled)));
                return;
            }

            RefreshPrivacyAndAboutSummary();
        }
    }

    public string UrlValidationError
    {
        get => _urlValidationError;
        private set => SetProperty(ref _urlValidationError, value);
    }

    public string FeedbackMessage
    {
        get => _feedbackMessage;
        private set => SetProperty(ref _feedbackMessage, value);
    }

    public string PrivacyCapabilitySummary
    {
        get => _privacyCapabilitySummary;
        private set => SetProperty(ref _privacyCapabilitySummary, value);
    }

    public string PrivacyCurrentStatus
    {
        get => _privacyCurrentStatus;
        private set => SetProperty(ref _privacyCurrentStatus, value);
    }

    public string PrivacyLimitationSummary
    {
        get => _privacyLimitationSummary;
        private set => SetProperty(ref _privacyLimitationSummary, value);
    }

    public string ApplicationName => ConfigurationHelper.ApplicationName;

    public string ApplicationVersion =>
        typeof(SettingsViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "1.0.0";

    public string DeveloperInfo => "MyLaptop Contributors";

    public string CopyrightInfo => "Copyright (c) 2026 MyLaptop Contributors";

    public void RefreshPrivacyAndAboutSummary()
    {
        var status = _privacyService.CurrentStatus;
        PrivacyCapabilitySummary = status.CapabilitySummary;
        PrivacyCurrentStatus = status.StatusSummary;
        PrivacyLimitationSummary = status.LimitationNotice;
        OnPropertyChanged(nameof(IsPrivacyModeEnabled));
        OnPropertyChanged(nameof(IsPrivacyModeSupported));
    }

    private void ApplyMicrophoneState(MicrophoneState micState)
    {
        _microphoneState = micState;
        OnPropertyChanged(nameof(MicrophoneDevices));
        OnPropertyChanged(nameof(SelectedMicrophoneId));
        OnPropertyChanged(nameof(AllowMicrophoneAccess));
        OnPropertyChanged(nameof(EnableMicrophoneLevelMonitoring));
        OnPropertyChanged(nameof(IsMicrophoneListening));
        OnPropertyChanged(nameof(IsMicrophonePermissionDenied));
        OnPropertyChanged(nameof(MicrophoneLevelPercent));
        OnPropertyChanged(nameof(IsMicrophoneMuted));
        OnPropertyChanged(nameof(IsMicrophoneDeviceAvailable));
        OnPropertyChanged(nameof(MicrophoneMuteButtonText));
        OnPropertyChanged(nameof(MicrophoneStatusText));
        OnPropertyChanged(nameof(MicrophoneErrorMessage));
        OnPropertyChanged(nameof(ActiveMicrophoneName));
        StartMicrophoneCommand.RaiseCanExecuteChanged();
        StopMicrophoneCommand.RaiseCanExecuteChanged();
        ToggleMicrophoneMuteCommand.RaiseCanExecuteChanged();
    }

    private void ApplySystemAudioState(SystemAudioState sysState)
    {
        _systemAudioState = sysState;
        OnPropertyChanged(nameof(EnableSystemAudioMonitoring));
        OnPropertyChanged(nameof(IsSystemAudioListening));
        OnPropertyChanged(nameof(SystemAudioLevelPercent));
        OnPropertyChanged(nameof(IsSystemAudioMuted));
        OnPropertyChanged(nameof(IsSystemAudioDeviceAvailable));
        OnPropertyChanged(nameof(SystemAudioMuteButtonText));
        OnPropertyChanged(nameof(SystemAudioStatusText));
        OnPropertyChanged(nameof(SystemAudioErrorMessage));
        OnPropertyChanged(nameof(ActiveSystemAudioDeviceName));
        StartSystemAudioCommand.RaiseCanExecuteChanged();
        StopSystemAudioCommand.RaiseCanExecuteChanged();
        ToggleSystemAudioMuteCommand.RaiseCanExecuteChanged();
    }

    private void ApplyTranscriptionState(TranscriptionState txState)
    {
        _transcriptionState = txState;
        OnPropertyChanged(nameof(EnableLiveSpeechTranscription));
        OnPropertyChanged(nameof(TranscriptionStatusText));
        OnPropertyChanged(nameof(RecognizerDisplayName));
        OnPropertyChanged(nameof(TranscriptionErrorMessage));
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private void LoadFromSettings(AppSettings s)
    {
        LaunchBehavior = s.LaunchBehavior;
        LaunchOnWindowsStartup = s.LaunchOnWindowsStartup;
        StartMinimized = s.StartMinimized;
        ShowMiniPopupWhenMinimized = s.ShowMiniPopupWhenMinimized;
        RememberLastWindowPosition = s.RememberLastWindowPosition;
        WindowSizePreset = s.WindowSizePreset;
        DefaultWindowWidth = s.DefaultWindowWidth;
        DefaultWindowHeight = s.DefaultWindowHeight;
        _selectedTheme = s.Theme;
        OnPropertyChanged(nameof(SelectedTheme));
        GeminiHomepageUrl = s.GeminiHomepageUrl;
        OpenExternalLinksInSystemBrowser = s.OpenExternalLinksInSystemBrowser;
        EnableCaptureExclusionByDefault = s.EnableScreenCaptureExclusionByDefault;
        _windowOpacity = AppSettings.ClampOpacity(s.WindowOpacity);
        OnPropertyChanged(nameof(WindowOpacity));
        OnPropertyChanged(nameof(WindowOpacityPercent));
        OnPropertyChanged(nameof(BackgroundVisibilityPercent));
        UrlValidationError = string.Empty;
    }

    private void ApplyOpacityPreset(string? rawPercent)
    {
        if (double.TryParse(rawPercent, out var pct))
        {
            WindowOpacity = pct / 100.0;
        }
    }

    private bool ValidateUrlInline()
    {
        if (ConfigurationHelper.TryValidateGeminiHomepageUrl(GeminiHomepageUrl, out _, out var error))
        {
            UrlValidationError = string.Empty;
            return true;
        }

        UrlValidationError = error;
        return false;
    }

    private void SaveSettings()
    {
        if (!ConfigurationHelper.TryValidateGeminiHomepageUrl(GeminiHomepageUrl, out var normalizedUrl, out var error))
        {
            UrlValidationError = error;
            FeedbackMessage = "Please correct the Gemini homepage URL before saving.";
            return;
        }

        var updated = new AppSettings
        {
            LaunchBehavior = LaunchBehavior,
            LaunchOnWindowsStartup = LaunchOnWindowsStartup,
            StartMinimized = StartMinimized,
            ShowMiniPopupWhenMinimized = ShowMiniPopupWhenMinimized,
            RememberLastWindowPosition = RememberLastWindowPosition,
            WindowSizePreset = WindowSizePreset,
            DefaultWindowWidth = Math.Clamp(DefaultWindowWidth, WindowHelper.DefaultMinWidth, 3840),
            DefaultWindowHeight = Math.Clamp(DefaultWindowHeight, WindowHelper.DefaultMinHeight, 2160),
            Theme = SelectedTheme,
            GeminiHomepageUrl = normalizedUrl,
            OpenExternalLinksInSystemBrowser = OpenExternalLinksInSystemBrowser,
            EnableScreenCaptureExclusionByDefault = EnableCaptureExclusionByDefault,
            WindowOpacity = AppSettings.ClampOpacity(WindowOpacity),
            PreferredMicrophoneId = SelectedMicrophoneId,
            AllowMicrophoneAccess = AllowMicrophoneAccess,
            EnableMicrophoneLevelMonitoring = EnableMicrophoneLevelMonitoring,
            EnableSystemAudioMonitoring = EnableSystemAudioMonitoring,
            EnableLiveSpeechTranscription = EnableLiveSpeechTranscription,
            IsAudioPanelVisible = _settingsService.CurrentSettings.IsAudioPanelVisible
        };

        _settingsService.SaveSettings(updated);
        _windowService.SetWindowOpacity(updated.WindowOpacity, persist: false);
        _themeService.ApplyTheme(updated.Theme);
        GeminiHomepageUrl = updated.GeminiHomepageUrl;
        FeedbackMessage = $"Settings saved at {DateTime.Now:t}.";
    }

    private void ResetAllSettingsWithConfirmation()
    {
        if (!_dialogService.Confirm(
                "Reset Application Settings",
                "Reset all general, appearance, audio, and browser preferences back to their defaults? Your Google account session in WebView2 will remain untouched.",
                isDestructive: false))
        {
            return;
        }

        var defaults = _settingsService.ResetSettingsToDefaults();
        LoadFromSettings(defaults);
        _themeService.ApplyTheme(defaults.Theme);
        _microphoneService.SetWorkspaceMicrophoneAllowed(defaults.AllowMicrophoneAccess);
        _microphoneService.SetMonitoringEnabled(defaults.EnableMicrophoneLevelMonitoring);
        _microphoneService.SelectDevice(defaults.PreferredMicrophoneId);
        if (defaults.EnableSystemAudioMonitoring) _systemAudioService.StartListening();
        else _systemAudioService.StopListening();
        _transcriptionService.SetEnabled(defaults.EnableLiveSpeechTranscription);
        FeedbackMessage = "All application settings were restored to defaults.";
    }

    private void ApplyConfiguredWindowSizeNow()
    {
        SaveSettings();
        _windowService.ApplyConfiguredDefaultSize();
        FeedbackMessage = "Applied configured default window size.";
    }

    private void ResetHomepageUrl()
    {
        GeminiHomepageUrl = AppSettings.DefaultGeminiUrl;
        UrlValidationError = string.Empty;
        FeedbackMessage = "Homepage URL reset to https://gemini.google.com/ (click Save Settings to persist).";
    }

    private void RefreshBrowserSession()
    {
        _browserService.Reload();
        FeedbackMessage = "Requested browser session refresh.";
    }

    private async Task ClearBrowserCacheAsync()
    {
        if (!_dialogService.Confirm(
                "Clear Browser Cache",
                "Clear cached WebView2 files and offline cache storage? Your signed-in Google account session cookies will be preserved.",
                isDestructive: false))
        {
            return;
        }

        await _browserService.ClearCacheAsync().ConfigureAwait(true);
        FeedbackMessage = "Browser cache cleared cleanly.";
    }

    private async Task ResetBrowserWorkspaceAsync()
    {
        if (!_dialogService.Confirm(
                "Reset Browser Workspace",
                "This will clear all WebView2 cookies, cached site data, and sign you out of Google Gemini on this device. Continue?",
                isDestructive: true))
        {
            return;
        }

        await _browserService.ResetWorkspaceAsync().ConfigureAwait(true);
        FeedbackMessage = "Browser workspace was reset cleanly.";
    }
}
