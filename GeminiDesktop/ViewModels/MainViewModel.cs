using System.Windows;
using System.Windows.Input;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using GeminiDesktop.Services;

namespace GeminiDesktop.ViewModels;

/// <summary>
/// Shell ViewModel coordinating primary navigation, custom window controls, and live status badges.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly IWindowManagementService _windowService;
    private readonly IPrivacyService _privacyService;
    private readonly IGeminiBrowserService _browserService;
    private readonly ILoggingService _loggingService;

    private NavigationSection _currentSection = NavigationSection.Home;
    private ViewModelBase _currentDetailViewModel;
    private bool _isSidebarVisible = true;
    private bool _isAlwaysOnTop;
    private bool _isMaximized;
    private bool _isOpacityPopupOpen;
    private double _windowOpacity = 1.0;
    private bool _isPrivacyIndicatorActive;
    private string _privacyIndicatorText = "Privacy Mode: Off";
    private string _statusBarMessage = "Ready";
    private bool _isOffline;

    public MainViewModel(
        ISettingsService settingsService,
        IWindowManagementService windowService,
        IPrivacyService privacyService,
        IGeminiBrowserService browserService,
        ILoggingService loggingService,
        HomeViewModel homeViewModel,
        GeminiViewModel geminiViewModel,
        SettingsViewModel settingsViewModel,
        PrivacyViewModel privacyViewModel,
        AboutViewModel aboutViewModel)
    {
        _settingsService = settingsService;
        _windowService = windowService;
        _privacyService = privacyService;
        _browserService = browserService;
        _loggingService = loggingService;

        HomeViewModel = homeViewModel;
        GeminiViewModel = geminiViewModel;
        SettingsViewModel = settingsViewModel;
        PrivacyViewModel = privacyViewModel;
        AboutViewModel = aboutViewModel;

        _currentDetailViewModel = HomeViewModel;

        HomeViewModel.OpenWorkspaceRequested += (_, _) => NavigateTo(NavigationSection.GeminiWorkspace);
        HomeViewModel.OpenAudioMonitorRequested += (_, _) => NavigateTo(NavigationSection.AudioMonitor);
        HomeViewModel.OpenPrivacyRequested += (_, _) => NavigateTo(NavigationSection.PrivacyAndWindowManagement);
        HomeViewModel.OpenSettingsRequested += (_, _) => NavigateTo(NavigationSection.Settings);
        SettingsViewModel.NavigateToPrivacyRequested += (_, _) => NavigateTo(NavigationSection.PrivacyAndWindowManagement);
        SettingsViewModel.NavigateToAboutRequested += (_, _) => NavigateTo(NavigationSection.About);

        NavigateCommand = new RelayCommand<NavigationSection>(NavigateTo);
        ToggleSidebarCommand = new RelayCommand(ToggleSidebar);
        ToggleOpacityPopupCommand = new RelayCommand(() => IsOpacityPopupOpen = !IsOpacityPopupOpen);
        SetOpacityPresetCommand = new RelayCommand<string>(ApplyOpacityPreset);
        CommitOpacityCommand = new RelayCommand(() => _windowService.SetWindowOpacity(WindowOpacity, persist: true));
        MinimizeCommand = new RelayCommand(() => _windowService.Minimize());
        MaximizeRestoreCommand = new RelayCommand(() => _windowService.ToggleMaximizeRestore());
        CloseCommand = new RelayCommand(() => _windowService.CloseWindow());
        ToggleAlwaysOnTopCommand = new RelayCommand(() => _windowService.SetAlwaysOnTop(!IsAlwaysOnTop));

        _windowService.AlwaysOnTopChanged += (_, topmost) =>
        {
            IsAlwaysOnTop = topmost;
            _privacyService.NotifyAlwaysOnTopChanged(topmost);
        };
        _windowService.WindowStateChanged += (_, state) =>
        {
            IsMaximized = state == WindowState.Maximized;
            OnPropertyChanged(nameof(MaximizeRestoreTooltip));
        };
        _windowService.WindowOpacityChanged += (_, opacity) =>
        {
            if (Math.Abs(_windowOpacity - opacity) > 0.001)
            {
                _windowOpacity = opacity;
                OnPropertyChanged(nameof(WindowOpacity));
                OnPropertyChanged(nameof(WindowOpacityPercent));
                OnPropertyChanged(nameof(BackgroundVisibilityPercent));
                OnPropertyChanged(nameof(OpacityBadgeText));
            }
        };

        _privacyService.PrivacyStatusChanged += (_, status) => ApplyPrivacyIndicator(status);
        _browserService.StateChanged += (_, state) => ApplyBrowserState(state);

        InitializeStartupState();
    }

    public HomeViewModel HomeViewModel { get; }
    public GeminiViewModel GeminiViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }
    public PrivacyViewModel PrivacyViewModel { get; }
    public AboutViewModel AboutViewModel { get; }

    public ICommand NavigateCommand { get; }
    public ICommand ToggleSidebarCommand { get; }
    public ICommand ToggleOpacityPopupCommand { get; }
    public ICommand SetOpacityPresetCommand { get; }
    public ICommand CommitOpacityCommand { get; }
    public ICommand MinimizeCommand { get; }
    public ICommand MaximizeRestoreCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand ToggleAlwaysOnTopCommand { get; }

    public bool IsOpacityPopupOpen
    {
        get => _isOpacityPopupOpen;
        set => SetProperty(ref _isOpacityPopupOpen, value);
    }

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
                OnPropertyChanged(nameof(OpacityBadgeText));
            }
        }
    }

    public int WindowOpacityPercent => (int)Math.Round(WindowOpacity * 100);

    public int BackgroundVisibilityPercent => Math.Max(0, 100 - WindowOpacityPercent);

    public string OpacityBadgeText =>
        BackgroundVisibilityPercent > 0 ? $"Opacity {WindowOpacityPercent}% ({BackgroundVisibilityPercent}% bg)" : "Opacity 100%";

    public bool IsSidebarVisible
    {
        get => _isSidebarVisible;
        set
        {
            if (SetProperty(ref _isSidebarVisible, value))
            {
                OnPropertyChanged(nameof(SidebarToggleTooltip));
            }
        }
    }

    public string SidebarToggleTooltip =>
        IsSidebarVisible ? "Hide Navigation Sidebar (Ctrl+B)" : "Show Navigation Sidebar (Ctrl+B)";

    public NavigationSection CurrentSection
    {
        get => _currentSection;
        private set
        {
            if (SetProperty(ref _currentSection, value))
            {
                OnPropertyChanged(nameof(IsGeminiWorkspaceActive));
                OnPropertyChanged(nameof(IsAudioMonitorActive));
                OnPropertyChanged(nameof(IsDetailSectionActive));
                OnPropertyChanged(nameof(IsHomeActive));
                OnPropertyChanged(nameof(IsSettingsActive));
                OnPropertyChanged(nameof(IsPrivacyActive));
                OnPropertyChanged(nameof(IsAboutActive));
                OnPropertyChanged(nameof(SectionTitle));
            }
        }
    }

    public ViewModelBase CurrentDetailViewModel
    {
        get => _currentDetailViewModel;
        private set => SetProperty(ref _currentDetailViewModel, value);
    }

    public bool IsGeminiWorkspaceActive => CurrentSection == NavigationSection.GeminiWorkspace;
    public bool IsAudioMonitorActive => CurrentSection == NavigationSection.AudioMonitor;
    public bool IsDetailSectionActive => !IsGeminiWorkspaceActive && !IsAudioMonitorActive;
    public bool IsHomeActive => CurrentSection == NavigationSection.Home;
    public bool IsSettingsActive => CurrentSection == NavigationSection.Settings;
    public bool IsPrivacyActive => CurrentSection == NavigationSection.PrivacyAndWindowManagement;
    public bool IsAboutActive => CurrentSection == NavigationSection.About;

    public string SectionTitle => CurrentSection switch
    {
        NavigationSection.Home => "Home",
        NavigationSection.GeminiWorkspace => "MyLaptop Workspace",
        NavigationSection.AudioMonitor => "Audio & Transcript",
        NavigationSection.Settings => "Settings",
        NavigationSection.PrivacyAndWindowManagement => "Privacy & Window Management",
        NavigationSection.About => "About",
        _ => "MyLaptop"
    };

    public bool IsAlwaysOnTop
    {
        get => _isAlwaysOnTop;
        private set => SetProperty(ref _isAlwaysOnTop, value);
    }

    public bool IsMaximized
    {
        get => _isMaximized;
        private set => SetProperty(ref _isMaximized, value);
    }

    public string MaximizeRestoreTooltip => IsMaximized ? "Restore Down" : "Maximize";

    public bool IsPrivacyIndicatorActive
    {
        get => _isPrivacyIndicatorActive;
        private set => SetProperty(ref _isPrivacyIndicatorActive, value);
    }

    public string PrivacyIndicatorText
    {
        get => _privacyIndicatorText;
        private set => SetProperty(ref _privacyIndicatorText, value);
    }

    public string StatusBarMessage
    {
        get => _statusBarMessage;
        private set => SetProperty(ref _statusBarMessage, value);
    }

    public bool IsOffline
    {
        get => _isOffline;
        private set => SetProperty(ref _isOffline, value);
    }

    public void NavigateTo(NavigationSection section)
    {
        CurrentSection = section;

        if (section != NavigationSection.GeminiWorkspace)
        {
            CurrentDetailViewModel = section switch
            {
                NavigationSection.Home => HomeViewModel,
                NavigationSection.Settings => SettingsViewModel,
                NavigationSection.PrivacyAndWindowManagement => PrivacyViewModel,
                NavigationSection.About => AboutViewModel,
                _ => HomeViewModel
            };
        }

        if (section == NavigationSection.Home)
        {
            HomeViewModel.RefreshOverview();
        }
        else if (section == NavigationSection.Settings)
        {
            SettingsViewModel.RefreshPrivacyAndAboutSummary();
        }
        else if (section == NavigationSection.PrivacyAndWindowManagement)
        {
            PrivacyViewModel.RefreshFromService();
        }

        _loggingService.Debug("Navigated to section {Section}.", section);
    }

    public void ApplyWindowInitializedDefaults()
    {
        IsAlwaysOnTop = _windowService.IsAlwaysOnTop;
        IsMaximized = _windowService.IsMaximized;
        _windowOpacity = _windowService.WindowOpacity;
        OnPropertyChanged(nameof(WindowOpacity));
        OnPropertyChanged(nameof(WindowOpacityPercent));
        OnPropertyChanged(nameof(BackgroundVisibilityPercent));
        OnPropertyChanged(nameof(OpacityBadgeText));

        if (_settingsService.CurrentSettings.EnableScreenCaptureExclusionByDefault)
        {
            _privacyService.SetCaptureExclusion(true);
        }
        else
        {
            _privacyService.RefreshStatus();
        }

        ApplyPrivacyIndicator(_privacyService.CurrentStatus);
        ApplyBrowserState(_browserService.State);
    }

    public void ToggleSidebar()
    {
        IsSidebarVisible = !IsSidebarVisible;
        var winSettings = _settingsService.CurrentWindowSettings;
        winSettings.IsSidebarExpanded = IsSidebarVisible;
        _settingsService.SaveWindowSettings(winSettings);
    }

    public void OnWindowClosing()
    {
        _windowService.SetWindowOpacity(WindowOpacity, persist: true);
        _windowService.SaveCurrentWindowBounds(CurrentSection, IsSidebarVisible);
    }

    private void ApplyOpacityPreset(string? rawPercent)
    {
        if (double.TryParse(rawPercent, out var pct))
        {
            var value = AppSettings.ClampOpacity(pct / 100.0);
            _windowOpacity = value;
            _windowService.SetWindowOpacity(value, persist: true);
            OnPropertyChanged(nameof(WindowOpacity));
            OnPropertyChanged(nameof(WindowOpacityPercent));
            OnPropertyChanged(nameof(BackgroundVisibilityPercent));
            OnPropertyChanged(nameof(OpacityBadgeText));
        }
    }

    private void InitializeStartupState()
    {
        var settings = _settingsService.CurrentSettings;
        var winSettings = _settingsService.CurrentWindowSettings;

        _isSidebarVisible = winSettings.IsSidebarExpanded;
        OnPropertyChanged(nameof(IsSidebarVisible));
        OnPropertyChanged(nameof(SidebarToggleTooltip));

        var startSection = settings.LaunchBehavior switch
        {
            LaunchDestination.GeminiWorkspace => NavigationSection.GeminiWorkspace,
            LaunchDestination.LastVisited => winSettings.LastNavigationSection,
            _ => NavigationSection.Home
        };

        NavigateTo(startSection);
    }

    private void ApplyPrivacyIndicator(PrivacyStatus status)
    {
        IsPrivacyIndicatorActive = status.HasAnyActivePrivacyMode;

        if (status.IsCaptureExclusionActive && status.IsAlwaysOnTopActive)
        {
            PrivacyIndicatorText = "Privacy Mode: Active · Always on Top";
        }
        else if (status.IsCaptureExclusionActive)
        {
            PrivacyIndicatorText = "Privacy Mode: Active";
        }
        else if (status.IsAlwaysOnTopActive)
        {
            PrivacyIndicatorText = "Privacy Mode: Off · Always on Top";
        }
        else
        {
            PrivacyIndicatorText = "Privacy Mode: Off";
        }
    }

    private void ApplyBrowserState(BrowserState state)
    {
        IsOffline = state.IsOffline;
        StatusBarMessage = state.StatusText;
    }
}
