using System.Windows;
using System.Windows.Input;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using GeminiDesktop.Services;
using Microsoft.Web.WebView2.Wpf;

namespace GeminiDesktop.ViewModels;

/// <summary>
/// ViewModel driving the embedded web workspace view, navigation toolbar, microphone status/mute, and error recovery.
/// </summary>
public sealed class GeminiViewModel : ViewModelBase
{
    private readonly IGeminiBrowserService _browserService;
    private readonly IMicrophoneService _microphoneService;

    private BrowserState _state;
    private MicrophoneState _micState;
    private bool _isToolbarVisible = true;
    private string _microphoneAlertBanner = string.Empty;

    public GeminiViewModel(IGeminiBrowserService browserService, IMicrophoneService microphoneService)
    {
        _browserService = browserService;
        _microphoneService = microphoneService;
        _state = _browserService.State;
        _micState = _microphoneService.CurrentState;

        BackCommand = new RelayCommand(() => _browserService.GoBack(), () => CanGoBack);
        ForwardCommand = new RelayCommand(() => _browserService.GoForward(), () => CanGoForward);
        ReloadCommand = new RelayCommand(ExecuteReload);
        HomeCommand = new RelayCommand(() => _browserService.NavigateHome());
        ToggleToolbarCommand = new RelayCommand(() => IsToolbarVisible = !IsToolbarVisible);
        OpenWebView2GuideCommand = new RelayCommand(() => _browserService.OpenWebView2InstallGuide());

        ToggleMicrophoneMuteCommand = new RelayCommand(() => _microphoneService.ToggleMute(), () => _micState.IsDeviceAvailable);
        RefreshMicrophonesCommand = new RelayCommand(() => _microphoneService.RefreshDevices());
        OpenWindowsMicPrivacyCommand = new RelayCommand(() => _microphoneService.OpenWindowsMicrophonePrivacySettings());
        DismissMicrophoneAlertCommand = new RelayCommand(() => MicrophoneAlertBanner = string.Empty);

        _browserService.StateChanged += (_, updated) => RunOnUiThread(() => ApplyBrowserState(updated));
        _browserService.MicrophonePermissionBlocked += (_, reason) => RunOnUiThread(() => MicrophoneAlertBanner = reason);
        _microphoneService.StateChanged += (_, updated) => RunOnUiThread(() => ApplyMicrophoneState(updated));
    }

    public event EventHandler? ReinitializeRequested;

    public RelayCommand BackCommand { get; }
    public RelayCommand ForwardCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand HomeCommand { get; }
    public ICommand ToggleToolbarCommand { get; }
    public ICommand OpenWebView2GuideCommand { get; }
    public RelayCommand ToggleMicrophoneMuteCommand { get; }
    public ICommand RefreshMicrophonesCommand { get; }
    public ICommand OpenWindowsMicPrivacyCommand { get; }
    public ICommand DismissMicrophoneAlertCommand { get; }

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

    // Live physical microphone properties
    public bool IsMicrophoneAvailable => _micState.IsDeviceAvailable;
    public bool IsMicrophoneMuted => _micState.IsMuted;
    public bool IsMicrophonePermissionDenied => _micState.PermissionState == MicrophonePermissionState.DeniedByWindows;
    public int MicrophoneLevelPercent => _micState.InputLevelPercent;
    public string MicrophoneStatusText => _micState.StatusText;
    public string MicrophoneMuteLabel => _micState.IsMuted ? "Muted" : "Mic On";

    public string MicrophoneTooltip => !_micState.IsDeviceAvailable
        ? "No active physical microphone detected"
        : _micState.PermissionState == MicrophonePermissionState.DeniedByWindows
            ? "Windows microphone access is disabled (click to inspect in Windows Settings)"
            : _micState.IsMuted
                ? $"Microphone muted ({_micState.ActiveDeviceName}) — Click to unmute"
                : $"Microphone active ({_micState.ActiveDeviceName}) · Level {_micState.InputLevelPercent}% — Click to mute";

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
        _micState = updated;
        OnPropertyChanged(nameof(IsMicrophoneAvailable));
        OnPropertyChanged(nameof(IsMicrophoneMuted));
        OnPropertyChanged(nameof(IsMicrophonePermissionDenied));
        OnPropertyChanged(nameof(MicrophoneLevelPercent));
        OnPropertyChanged(nameof(MicrophoneStatusText));
        OnPropertyChanged(nameof(MicrophoneMuteLabel));
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

        ToggleMicrophoneMuteCommand.RaiseCanExecuteChanged();
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
