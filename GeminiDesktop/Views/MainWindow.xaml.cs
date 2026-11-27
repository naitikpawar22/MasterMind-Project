using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using GeminiDesktop.Services;
using GeminiDesktop.ViewModels;

namespace GeminiDesktop.Views;

/// <summary>
/// Main application shell window hosting custom chrome, navigation, persistent workspace, and minimize popup coordination.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IWindowManagementService _windowService;
    private readonly IPrivacyService _privacyService;
    private readonly IThemeService _themeService;
    private readonly ISettingsService _settingsService;
    private readonly MiniPopupWindow _miniPopupWindow;
    private readonly MiniPopupViewModel _miniPopupViewModel;

    public MainWindow(
        MainViewModel viewModel,
        IWindowManagementService windowService,
        IPrivacyService privacyService,
        IThemeService themeService,
        ISettingsService settingsService,
        MiniPopupWindow miniPopupWindow,
        MiniPopupViewModel miniPopupViewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _windowService = windowService;
        _privacyService = privacyService;
        _themeService = themeService;
        _settingsService = settingsService;
        _miniPopupWindow = miniPopupWindow;
        _miniPopupViewModel = miniPopupViewModel;

        DataContext = _viewModel;

        _miniPopupViewModel.RestoreRequested += OnMiniPopupRestoreRequested;
        _miniPopupViewModel.DismissRequested += OnMiniPopupDismissRequested;

        StateChanged += OnMainWindowStateChanged;
        Loaded += OnMainWindowLoaded;
        _windowService.WindowOpacityChanged += OnWindowOpacityChanged;

        // Apply saved or preset window position/dimensions before first frame paints.
        _windowService.AttachWindow(this);
    }

    private void OnWindowOpacityChanged(object? sender, double opacity)
    {
        if (_miniPopupWindow.IsVisible)
        {
            _miniPopupWindow.Opacity = Math.Max(0.45, opacity);
        }
    }

    private void OnOpacitySliderMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _viewModel.CommitOpacityCommand.Execute(null);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _themeService.ApplyWindowChromeTheme(this);
        _privacyService.AttachWindow(this);
        _viewModel.ApplyWindowInitializedDefaults();
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settingsService.CurrentSettings.ShowMiniPopupWhenMinimized)
        {
            ShowMiniPopupAnchored();
        }
    }

    private void OnMainWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            if (_settingsService.CurrentSettings.ShowMiniPopupWhenMinimized)
            {
                ShowMiniPopupAnchored();
            }
        }
        else if (_miniPopupWindow.IsVisible)
        {
            _miniPopupWindow.Hide();
        }
    }

    private void ShowMiniPopupAnchored()
    {
        var monitors = GetMonitorsForWindow();
        var targetMonitor = monitors.FirstOrDefault(m =>
            m.Intersects(Left, Top, Math.Max(100, Width), Math.Max(100, Height)));
        if (targetMonitor.Width <= 0)
        {
            targetMonitor = monitors.FirstOrDefault(m => m.IsPrimary);
            if (targetMonitor.Width <= 0 && monitors.Count > 0)
            {
                targetMonitor = monitors[0];
            }
        }

        const double margin = 18;
        var desiredLeft = targetMonitor.Right - _miniPopupWindow.Width - margin;
        var desiredTop = targetMonitor.Bottom - _miniPopupWindow.Height - margin;

        var (clampedLeft, clampedTop, _, _) = WindowHelper.ClampBoundsToVisibleMonitors(
            desiredLeft,
            desiredTop,
            _miniPopupWindow.Width,
            _miniPopupWindow.Height,
            monitors,
            minWidth: _miniPopupWindow.Width,
            minHeight: _miniPopupWindow.Height);

        _miniPopupWindow.Left = clampedLeft;
        _miniPopupWindow.Top = clampedTop;
        _miniPopupWindow.Opacity = Math.Max(0.45, _windowService.WindowOpacity);
        _miniPopupWindow.Show();
        _themeService.ApplyWindowChromeTheme(_miniPopupWindow);
        _privacyService.SyncSecondaryWindow(_miniPopupWindow);
    }

    private IReadOnlyList<ScreenWorkArea> GetMonitorsForWindow()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return WindowHelper.GetDisplayWorkAreas(dpi.DpiScaleX, dpi.DpiScaleY);
        }
        catch
        {
            return WindowHelper.GetDisplayWorkAreas();
        }
    }

    private void OnMiniPopupRestoreRequested(object? sender, bool openGeminiWorkspace)
    {
        if (openGeminiWorkspace)
        {
            _viewModel.NavigateTo(NavigationSection.GeminiWorkspace);
        }

        _miniPopupWindow.Hide();
        _windowService.RestoreFromMinimized();
    }

    private void OnMiniPopupDismissRequested(object? sender, EventArgs e)
    {
        _miniPopupWindow.Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _miniPopupViewModel.RestoreRequested -= OnMiniPopupRestoreRequested;
        _miniPopupViewModel.DismissRequested -= OnMiniPopupDismissRequested;
        _windowService.WindowOpacityChanged -= OnWindowOpacityChanged;
        StateChanged -= OnMainWindowStateChanged;
        Loaded -= OnMainWindowLoaded;

        _miniPopupWindow.AllowClose = true;
        _miniPopupWindow.Close();
        _viewModel.OnWindowClosing();
        base.OnClosing(e);
    }
}
