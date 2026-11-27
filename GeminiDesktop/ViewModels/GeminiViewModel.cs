using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using GeminiDesktop.Services;
using Microsoft.Web.WebView2.Wpf;

namespace GeminiDesktop.ViewModels;

/// <summary>
/// ViewModel driving the embedded web workspace view, dual Microphone & System Audio monitoring,
/// real-time speech-to-text transcription, and error recovery.
/// </summary>
public sealed class GeminiViewModel : ViewModelBase
{
    private readonly IGeminiBrowserService _browserService;
    private readonly IMicrophoneService _microphoneService;
    private readonly ISystemAudioService _systemAudioService;
    private readonly ISpeechTranscriptionService _transcriptionService;
    private readonly IMixedAudioRouterService _mixedAudioService;
    private readonly ISettingsService _settingsService;

    private BrowserState _state;
    private MicrophoneState _micState;
    private SystemAudioState _sysState;
    private TranscriptionState _txState;
    private MixedAudioState _mixedState;
    private bool _isToolbarVisible = true;
    private bool _isAudioPanelVisible = true;
    private string _microphoneAlertBanner = string.Empty;
    private string _transcriptNotice = string.Empty;

    public GeminiViewModel(
        IGeminiBrowserService browserService,
        IMicrophoneService microphoneService,
        ISystemAudioService systemAudioService,
        ISpeechTranscriptionService transcriptionService,
        IMixedAudioRouterService mixedAudioService,
        ISettingsService settingsService)
    {
        _browserService = browserService;
        _microphoneService = microphoneService;
        _systemAudioService = systemAudioService;
        _transcriptionService = transcriptionService;
        _mixedAudioService = mixedAudioService;
        _settingsService = settingsService;

        _state = _browserService.State;
        _micState = _microphoneService.CurrentState;
        _sysState = _systemAudioService.CurrentState;
        _txState = _transcriptionService.CurrentState;
        _mixedState = _mixedAudioService.CurrentState;
        _isAudioPanelVisible = _settingsService.CurrentSettings.IsAudioPanelVisible;

        BackCommand = new RelayCommand(() => _browserService.GoBack(), () => CanGoBack);
        ForwardCommand = new RelayCommand(() => _browserService.GoForward(), () => CanGoForward);
        ReloadCommand = new RelayCommand(ExecuteReload);
        HomeCommand = new RelayCommand(() => _browserService.NavigateHome());
        ToggleToolbarCommand = new RelayCommand(() => IsToolbarVisible = !IsToolbarVisible);
        ToggleAudioPanelCommand = new RelayCommand(() => IsAudioPanelVisible = !IsAudioPanelVisible);
        OpenWebView2GuideCommand = new RelayCommand(() => _browserService.OpenWebView2InstallGuide());

        // Microphone commands
        StartMicrophoneCommand = new RelayCommand(
            () => _microphoneService.StartListening(),
            () => !IsMicrophoneListening && IsMicrophoneAvailable && !IsMicrophonePermissionDenied);
        StopMicrophoneCommand = new RelayCommand(
            () => _microphoneService.StopListening(),
            () => IsMicrophoneListening);
        ToggleMicrophoneMuteCommand = new RelayCommand(
            () => _microphoneService.ToggleMute(),
            () => IsMicrophoneAvailable);
        RefreshMicrophonesCommand = new RelayCommand(() => _microphoneService.RefreshDevices());
        OpenWindowsMicPrivacyCommand = new RelayCommand(() => _microphoneService.OpenWindowsMicrophonePrivacySettings());
        DismissMicrophoneAlertCommand = new RelayCommand(() => MicrophoneAlertBanner = string.Empty);

        // System audio commands
        StartSystemAudioCommand = new RelayCommand(
            () => _systemAudioService.StartListening(),
            () => !IsSystemAudioListening && IsSystemAudioAvailable);
        StopSystemAudioCommand = new RelayCommand(
            () => _systemAudioService.StopListening(),
            () => IsSystemAudioListening);
        ToggleSystemAudioMuteCommand = new RelayCommand(
            () => _systemAudioService.ToggleMute(),
            () => IsSystemAudioAvailable);
        RefreshSystemAudioCommand = new RelayCommand(() => _systemAudioService.RefreshDevice());
        OpenWindowsSoundSettingsCommand = new RelayCommand(() => _systemAudioService.OpenWindowsSoundSettings());

        // Mixed Audio -> Gemini Virtual Mic commands
        ToggleMixedRoutingCommand = new RelayCommand(() => IsMixedRoutingEnabled = !IsMixedRoutingEnabled);
        RefreshMixedDriversCommand = new RelayCommand(() => _mixedAudioService.RefreshDevices());
        ToggleMixedMicMuteCommand = new RelayCommand(() => _mixedAudioService.ToggleMicrophoneMixMute());
        ToggleMixedSysMuteCommand = new RelayCommand(() => _mixedAudioService.ToggleSystemAudioMixMute());
        OpenVirtualAudioDriverPageCommand = new RelayCommand(() => _mixedAudioService.OpenVirtualAudioDriverWebsite());

        // Dual + Transcription commands
        StartBothAudioCommand = new RelayCommand(() => _transcriptionService.StartAllSources());
        StopBothAudioCommand = new RelayCommand(() => _transcriptionService.StopAllSources());
        ClearTranscriptCommand = new RelayCommand(ClearTranscript);
        CopyTranscriptCommand = new RelayCommand(CopyTranscript);

        _browserService.StateChanged += (_, updated) => RunOnUiThread(() => ApplyBrowserState(updated));
        _browserService.MicrophonePermissionBlocked += (_, reason) => RunOnUiThread(() => MicrophoneAlertBanner = reason);
        _microphoneService.StateChanged += (_, updated) => RunOnUiThread(() => ApplyMicrophoneState(updated));
        _systemAudioService.StateChanged += (_, updated) => RunOnUiThread(() => ApplySystemAudioState(updated));
        _transcriptionService.StateChanged += (_, updated) => RunOnUiThread(() => ApplyTranscriptionState(updated));
        _mixedAudioService.StateChanged += (_, updated) => RunOnUiThread(() => ApplyMixedAudioState(updated));
    }

    public event EventHandler? ReinitializeRequested;

    public ObservableCollection<TranscriptEntry> TranscriptEntries { get; } = [];

    public RelayCommand BackCommand { get; }
    public RelayCommand ForwardCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand HomeCommand { get; }
    public ICommand ToggleToolbarCommand { get; }
    public ICommand ToggleAudioPanelCommand { get; }
    public ICommand OpenWebView2GuideCommand { get; }

    public RelayCommand StartMicrophoneCommand { get; }
    public RelayCommand StopMicrophoneCommand { get; }
    public RelayCommand ToggleMicrophoneMuteCommand { get; }
    public ICommand RefreshMicrophonesCommand { get; }
    public ICommand OpenWindowsMicPrivacyCommand { get; }
    public ICommand DismissMicrophoneAlertCommand { get; }

    public RelayCommand StartSystemAudioCommand { get; }
    public RelayCommand StopSystemAudioCommand { get; }
    public RelayCommand ToggleSystemAudioMuteCommand { get; }
    public ICommand RefreshSystemAudioCommand { get; }
    public ICommand OpenWindowsSoundSettingsCommand { get; }

    public ICommand ToggleMixedRoutingCommand { get; }
    public ICommand RefreshMixedDriversCommand { get; }
    public ICommand ToggleMixedMicMuteCommand { get; }
    public ICommand ToggleMixedSysMuteCommand { get; }
    public ICommand OpenVirtualAudioDriverPageCommand { get; }

    public ICommand StartBothAudioCommand { get; }
    public ICommand StopBothAudioCommand { get; }
    public ICommand ClearTranscriptCommand { get; }
    public ICommand CopyTranscriptCommand { get; }

    public bool IsToolbarVisible
    {
        get => _isToolbarVisible;
        set
        {
            if (SetProperty(ref _isToolbarVisible, value))
            {
                OnPropertyChanged(nameof(ToolbarToggleTooltip));
            }
        }
    }

    public string ToolbarToggleTooltip =>
        IsToolbarVisible ? "Hide Browser Bar for Edge-to-Edge View (F11)" : "Show Browser Bar (F11)";

    public bool IsAudioPanelVisible
    {
        get => _isAudioPanelVisible;
        set
        {
            if (SetProperty(ref _isAudioPanelVisible, value))
            {
                var updated = _settingsService.CurrentSettings.Clone();
                updated.IsAudioPanelVisible = value;
                _settingsService.SaveSettings(updated);
                OnPropertyChanged(nameof(AudioPanelToggleTooltip));
            }
        }
    }

    public string AudioPanelToggleTooltip =>
        IsAudioPanelVisible ? "Hide Dual Audio & Transcript Panel" : "Show Dual Audio & Transcript Panel";

    public bool IsWebView2Available => _state.IsWebView2RuntimeAvailable;
    public bool IsInitializing => _state.IsInitializing;
    public bool IsLoading => _state.IsLoading;
    public bool CanGoBack => _state.CanGoBack;
    public bool CanGoForward => _state.CanGoForward;
    public string CurrentUrl => _state.CurrentUrl;
    public string PageTitle => _state.PageTitle;
    public bool IsOffline => _state.IsOffline;
    public bool HasError => _state.HasError;
    public string ErrorTitle => _state.ErrorTitle ?? string.Empty;
    public string ErrorMessage => _state.ErrorMessage ?? string.Empty;
    public string ErrorCode => _state.ErrorCode ?? string.Empty;
    public bool IsGoogleSignInFlow => _state.IsGoogleSignInFlow;
    public string StatusText => _state.StatusText;

    // Physical Microphone properties
    public IReadOnlyList<MicrophoneDevice> MicrophoneDevices => _micState.Devices;

    public string SelectedMicrophoneId
    {
        get => _micState.SelectedDeviceId;
        set
        {
            if (!string.Equals(_micState.SelectedDeviceId, value ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                _microphoneService.SelectDevice(value ?? string.Empty);
            }
        }
    }

    public bool IsMicrophoneListening => _micState.IsMonitoring;
    public bool IsMicrophoneAvailable => _micState.IsDeviceAvailable;
    public bool IsMicrophoneMuted => _micState.IsMuted;
    public bool IsMicrophonePermissionDenied => _micState.PermissionState == MicrophonePermissionState.DeniedByWindows;
    public int MicrophoneLevelPercent => _micState.InputLevelPercent;
    public string MicrophoneStatusText => _micState.StatusText;
    public string MicrophoneDeviceName => _micState.ActiveDeviceName;
    public string MicrophoneErrorMessage => _micState.ErrorMessage ?? string.Empty;
    public string MicrophoneMuteLabel => _micState.IsMuted ? "Muted" : "Mic On";
    public string MicrophoneMuteActionText => _micState.IsMuted ? "Unmute" : "Mute";
    public bool HasMicrophoneActivity => IsMicrophoneListening && !IsMicrophoneMuted && MicrophoneLevelPercent >= 4;

    public string MicrophoneTooltip => !_micState.IsDeviceAvailable
        ? "No active physical microphone detected"
        : _micState.PermissionState == MicrophonePermissionState.DeniedByWindows
            ? "Windows microphone access is disabled (click to open Windows Settings)"
            : !_micState.IsMonitoring
                ? $"Microphone stopped ({_micState.ActiveDeviceName})"
                : _micState.IsMuted
                    ? $"Microphone muted ({_micState.ActiveDeviceName}) — Click to unmute"
                    : $"Microphone active ({_micState.ActiveDeviceName}) · Level {_micState.InputLevelPercent}% — Click to mute";

    // System Audio Loopback properties
    public IReadOnlyList<SystemAudioDevice> SystemAudioDevices => _sysState.Devices;

    public string SelectedSystemAudioDeviceId
    {
        get => _sysState.SelectedDeviceId;
        set
        {
            if (!string.Equals(_sysState.SelectedDeviceId, value ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                _systemAudioService.SelectDevice(value ?? string.Empty);
            }
        }
    }

    public bool IsSystemAudioListening => _sysState.IsListening;
    public bool IsSystemAudioAvailable => _sysState.IsDeviceAvailable;
    public bool IsSystemAudioMuted => _sysState.IsMuted;
    public int SystemAudioLevelPercent => _sysState.OutputLevelPercent;
    public IReadOnlyList<double> SystemAudioWaveformBars => _sysState.WaveformBars;
    public string SystemAudioDeviceName => _sysState.ActiveDeviceName;
    public string SystemAudioStatusText => _sysState.StatusText;
    public string SystemAudioErrorMessage => _sysState.ErrorMessage ?? string.Empty;
    public string SystemAudioMuteActionText => _sysState.IsMuted ? "Unmute" : "Mute";
    public bool HasSystemAudioActivity => IsSystemAudioListening && !IsSystemAudioMuted && SystemAudioLevelPercent >= 4;

    public string SystemAudioTooltip => !_sysState.IsDeviceAvailable
        ? "No active Windows audio playback device detected"
        : !_sysState.IsListening
            ? $"System Audio stopped ({_sysState.ActiveDeviceName})"
            : _sysState.IsMuted
                ? $"System Audio capture muted (PC speakers remain audible) — Click to unmute"
                : $"System Audio active ({_sysState.ActiveDeviceName}) · Level {_sysState.OutputLevelPercent}% — Click to mute";

    // Gemini Mixed Audio Routing properties (Mic + System Audio -> Signed Virtual Mic Driver)
    public bool IsMixedRoutingEnabled
    {
        get => _mixedState.IsEnabled;
        set
        {
            if (_mixedState.IsEnabled != value)
            {
                _mixedAudioService.SetEnabled(value);
            }
        }
    }

    public bool IsMixedAudioStreaming => _mixedState.IsStreaming;
    public bool IsVirtualDriverInstalled => _mixedState.IsDriverInstalled;
    public IReadOnlyList<VirtualAudioOutputDevice> MixedVirtualOutputs => _mixedState.AvailableOutputs;

    public string SelectedMixedOutputDeviceId
    {
        get => _mixedState.SelectedOutputDeviceId;
        set
        {
            if (!string.Equals(_mixedState.SelectedOutputDeviceId, value ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                _mixedAudioService.SelectOutputDevice(value ?? string.Empty);
            }
        }
    }

    public int MixedMicrophoneVolumePercent
    {
        get => _mixedState.MicrophoneVolumePercent;
        set
        {
            if (_mixedState.MicrophoneVolumePercent != value)
            {
                _mixedAudioService.SetMicrophoneMixVolume(value);
            }
        }
    }

    public int MixedSystemAudioVolumePercent
    {
        get => _mixedState.SystemAudioVolumePercent;
        set
        {
            if (_mixedState.SystemAudioVolumePercent != value)
            {
                _mixedAudioService.SetSystemAudioMixVolume(value);
            }
        }
    }

    public bool IsMixedMicMuted => _mixedState.IsMicrophoneMixMuted;
    public bool IsMixedSysMuted => _mixedState.IsSystemAudioMixMuted;
    public string MixedMicMuteButtonText => _mixedState.IsMicrophoneMixMuted ? "Unmute Mic Mix" : "Mute Mic Mix";
    public string MixedSysMuteButtonText => _mixedState.IsSystemAudioMixMuted ? "Unmute Sys Mix" : "Mute Sys Mix";
    public int MixedOutputLevelPercent => _mixedState.MixedOutputLevelPercent;
    public string MixedOutputDeviceName => _mixedState.ActiveOutputDeviceName;
    public string MixedCaptureMicHint => _mixedState.PairedCaptureMicHint;
    public string MixedStatusText => _mixedState.StatusText;
    public string MixedDependencyMessage => _mixedState.DependencyMessage ?? string.Empty;
    public string MixedErrorMessage => _mixedState.ErrorMessage ?? string.Empty;

    public string MixedToolbarBadgeText => _mixedState.IsStreaming
        ? "Mixed → Gemini ON"
        : (_mixedState.IsDriverInstalled ? "Mixed Feed Off" : "Mixed: Driver Setup");

    public string MixedToolbarTooltip => _mixedState.IsStreaming
        ? $"Actively streaming Microphone + System Audio to '{_mixedState.ActiveOutputDeviceName}'. Ensure Gemini microphone is set to '{_mixedState.PairedCaptureMicHint}'."
        : (_mixedState.IsDriverInstalled
            ? $"Click to route mixed Microphone + System Audio to '{_mixedState.PairedCaptureMicHint}' for Gemini"
            : _mixedState.DependencyMessage ?? "Install VB-Audio Virtual Cable to feed mixed audio to Gemini");

    // Speech-to-Text properties
    public bool IsLiveTranscriptionEnabled
    {
        get => _txState.IsEnabled;
        set
        {
            if (_txState.IsEnabled != value)
            {
                _transcriptionService.SetEnabled(value);
            }
        }
    }

    public bool IsTranscriberAvailable => _txState.IsEngineAvailable;
    public string RecognizerDisplayName => _txState.RecognizerDisplayName;
    public string TranscriptionStatusText => _txState.StatusText;
    public string TranscriptionErrorMessage => _txState.ErrorMessage ?? string.Empty;
    public string LiveMicrophoneText => _txState.LiveMicrophoneText;
    public string LiveSystemAudioText => _txState.LiveSystemAudioText;
    public bool HasAnyLiveHypothesis =>
        !string.IsNullOrWhiteSpace(LiveMicrophoneText) || !string.IsNullOrWhiteSpace(LiveSystemAudioText);
    public bool HasTranscriptEntries => TranscriptEntries.Count > 0;

    public string TranscriptNotice
    {
        get => _transcriptNotice;
        private set => SetProperty(ref _transcriptNotice, value);
    }

    public string MicrophoneAlertBanner
    {
        get => _microphoneAlertBanner;
        private set => SetProperty(ref _microphoneAlertBanner, value);
    }

    public string DisplayHost
    {
        get
        {
            if (Uri.TryCreate(_state.CurrentUrl, UriKind.Absolute, out var uri))
            {
                return $"{uri.Scheme}://{uri.Host}";
            }

            return "https://gemini.google.com";
        }
    }

    public Task InitializeBrowserAsync(IWebView2 control) =>
        _browserService.InitializeWorkspaceAsync(control);

    private void ExecuteReload()
    {
        if (!_state.IsInitialized)
        {
            ReinitializeRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _browserService.Reload();
    }

    private void ClearTranscript()
    {
        _transcriptionService.ClearTranscript();
        TranscriptNotice = "Transcript cleared.";
    }

    private void CopyTranscript()
    {
        var formatted = _transcriptionService.FormatTranscriptText();
        if (string.IsNullOrWhiteSpace(formatted))
        {
            TranscriptNotice = "Nothing to copy yet — speak or play system audio first.";
            return;
        }

        try
        {
            Clipboard.SetText(formatted);
            TranscriptNotice = $"Copied {TranscriptEntries.Count} transcript lines to clipboard.";
        }
        catch (Exception ex)
        {
            TranscriptNotice = $"Clipboard unavailable: {ex.Message}";
        }
    }

    private void ApplyBrowserState(BrowserState updated)
    {
        _state = updated;
        OnPropertyChanged(nameof(IsWebView2Available));
        OnPropertyChanged(nameof(IsInitializing));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CurrentUrl));
        OnPropertyChanged(nameof(DisplayHost));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorTitle));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(ErrorCode));
        OnPropertyChanged(nameof(IsGoogleSignInFlow));
        OnPropertyChanged(nameof(StatusText));

        BackCommand.RaiseCanExecuteChanged();
        ForwardCommand.RaiseCanExecuteChanged();
    }

    private void ApplyMicrophoneState(MicrophoneState updated)
    {
        var prev = _micState;
        _micState = updated;
        if (!ReferenceEquals(prev.Devices, updated.Devices))
        {
            OnPropertyChanged(nameof(MicrophoneDevices));
        }

        if (!string.Equals(prev.SelectedDeviceId, updated.SelectedDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            OnPropertyChanged(nameof(SelectedMicrophoneId));
        }

        OnPropertyChanged(nameof(IsMicrophoneListening));
        OnPropertyChanged(nameof(IsMicrophoneAvailable));
        OnPropertyChanged(nameof(IsMicrophoneMuted));
        OnPropertyChanged(nameof(IsMicrophonePermissionDenied));
        OnPropertyChanged(nameof(MicrophoneLevelPercent));
        OnPropertyChanged(nameof(MicrophoneStatusText));
        OnPropertyChanged(nameof(MicrophoneDeviceName));
        OnPropertyChanged(nameof(MicrophoneErrorMessage));
        OnPropertyChanged(nameof(MicrophoneMuteLabel));
        OnPropertyChanged(nameof(MicrophoneMuteActionText));
        OnPropertyChanged(nameof(HasMicrophoneActivity));
        OnPropertyChanged(nameof(MicrophoneTooltip));

        if (!string.IsNullOrWhiteSpace(updated.ErrorMessage))
        {
            MicrophoneAlertBanner = updated.ErrorMessage;
        }
        else if (updated.PermissionState == MicrophonePermissionState.Allowed &&
                 !string.IsNullOrEmpty(MicrophoneAlertBanner) &&
                 !updated.StatusText.Contains("disconnected", StringComparison.OrdinalIgnoreCase))
        {
            MicrophoneAlertBanner = string.Empty;
        }

        StartMicrophoneCommand.RaiseCanExecuteChanged();
        StopMicrophoneCommand.RaiseCanExecuteChanged();
        ToggleMicrophoneMuteCommand.RaiseCanExecuteChanged();
    }

    private void ApplySystemAudioState(SystemAudioState updated)
    {
        var prev = _sysState;
        _sysState = updated;
        if (!ReferenceEquals(prev.Devices, updated.Devices))
        {
            OnPropertyChanged(nameof(SystemAudioDevices));
        }

        if (!string.Equals(prev.SelectedDeviceId, updated.SelectedDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            OnPropertyChanged(nameof(SelectedSystemAudioDeviceId));
        }

        OnPropertyChanged(nameof(IsSystemAudioListening));
        OnPropertyChanged(nameof(IsSystemAudioAvailable));
        OnPropertyChanged(nameof(IsSystemAudioMuted));
        OnPropertyChanged(nameof(SystemAudioLevelPercent));
        OnPropertyChanged(nameof(SystemAudioWaveformBars));
        OnPropertyChanged(nameof(SystemAudioDeviceName));
        OnPropertyChanged(nameof(SystemAudioStatusText));
        OnPropertyChanged(nameof(SystemAudioErrorMessage));
        OnPropertyChanged(nameof(SystemAudioMuteActionText));
        OnPropertyChanged(nameof(HasSystemAudioActivity));
        OnPropertyChanged(nameof(SystemAudioTooltip));

        StartSystemAudioCommand.RaiseCanExecuteChanged();
        StopSystemAudioCommand.RaiseCanExecuteChanged();
        ToggleSystemAudioMuteCommand.RaiseCanExecuteChanged();
    }

    private void ApplyTranscriptionState(TranscriptionState updated)
    {
        _txState = updated;
        OnPropertyChanged(nameof(IsLiveTranscriptionEnabled));
        OnPropertyChanged(nameof(IsTranscriberAvailable));
        OnPropertyChanged(nameof(RecognizerDisplayName));
        OnPropertyChanged(nameof(TranscriptionStatusText));
        OnPropertyChanged(nameof(TranscriptionErrorMessage));
        OnPropertyChanged(nameof(LiveMicrophoneText));
        OnPropertyChanged(nameof(LiveSystemAudioText));
        OnPropertyChanged(nameof(HasAnyLiveHypothesis));

        SyncTranscriptCollection(updated.Entries);
        OnPropertyChanged(nameof(HasTranscriptEntries));
    }

    private void ApplyMixedAudioState(MixedAudioState updated)
    {
        var prev = _mixedState;
        _mixedState = updated;
        if (!ReferenceEquals(prev.AvailableOutputs, updated.AvailableOutputs))
        {
            OnPropertyChanged(nameof(MixedVirtualOutputs));
        }

        if (!string.Equals(prev.SelectedOutputDeviceId, updated.SelectedOutputDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            OnPropertyChanged(nameof(SelectedMixedOutputDeviceId));
        }

        OnPropertyChanged(nameof(IsMixedRoutingEnabled));
        OnPropertyChanged(nameof(IsMixedAudioStreaming));
        OnPropertyChanged(nameof(IsVirtualDriverInstalled));
        OnPropertyChanged(nameof(MixedMicrophoneVolumePercent));
        OnPropertyChanged(nameof(MixedSystemAudioVolumePercent));
        OnPropertyChanged(nameof(IsMixedMicMuted));
        OnPropertyChanged(nameof(IsMixedSysMuted));
        OnPropertyChanged(nameof(MixedMicMuteButtonText));
        OnPropertyChanged(nameof(MixedSysMuteButtonText));
        OnPropertyChanged(nameof(MixedOutputLevelPercent));
        OnPropertyChanged(nameof(MixedOutputDeviceName));
        OnPropertyChanged(nameof(MixedCaptureMicHint));
        OnPropertyChanged(nameof(MixedStatusText));
        OnPropertyChanged(nameof(MixedDependencyMessage));
        OnPropertyChanged(nameof(MixedErrorMessage));
        OnPropertyChanged(nameof(MixedToolbarBadgeText));
        OnPropertyChanged(nameof(MixedToolbarTooltip));
    }

    private void SyncTranscriptCollection(IReadOnlyList<TranscriptEntry> target)
    {
        if (target.Count == 0)
        {
            if (TranscriptEntries.Count > 0)
            {
                TranscriptEntries.Clear();
            }

            return;
        }

        // Fast incremental append when new entries arrive without full clear.
        var existingCount = TranscriptEntries.Count;
        if (target.Count >= existingCount &&
            (existingCount == 0 || TranscriptEntries[0].Id == target[0].Id))
        {
            for (var i = existingCount; i < target.Count; i++)
            {
                TranscriptEntries.Add(target[i]);
            }

            return;
        }

        TranscriptEntries.Clear();
        foreach (var entry in target)
        {
            TranscriptEntries.Add(entry);
        }
    }

    private static void RunOnUiThread(Action action)
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
}
