using System.Windows;
using System.Windows.Media;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using Microsoft.Win32;

namespace GeminiDesktop.Services;

/// <summary>
/// Manages window bounds persistence, multi-monitor clamping, display changes, and Always-on-Top state.
/// </summary>
public interface IWindowManagementService : IDisposable
{
    bool IsAlwaysOnTop { get; }
    bool IsMaximized { get; }
    double WindowOpacity { get; }

    event EventHandler<bool>? AlwaysOnTopChanged;
    event EventHandler<WindowState>? WindowStateChanged;
    event EventHandler<double>? WindowOpacityChanged;

    void AttachWindow(Window window);
    void DetachWindow();

    void Minimize();
    void ToggleMaximizeRestore();
    void RestoreFromMinimized();
    void CloseWindow();
    void SetAlwaysOnTop(bool enabled);
    void SetWindowOpacity(double opacity, bool persist = false);
    void EnsureWindowVisibleOnDesktop();
    void ApplyConfiguredDefaultSize();
    void SaveCurrentWindowBounds(NavigationSection currentSection);
    void SaveCurrentWindowBounds(NavigationSection currentSection, bool isSidebarExpanded);
}

public sealed class WindowManagementService : IWindowManagementService
{
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;

    private Window? _window;
    private bool _isApplyingBounds;
    private bool _disposed;

    public WindowManagementService(ISettingsService settingsService, ILoggingService loggingService)
    {
        _settingsService = settingsService;
        _loggingService = loggingService;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public bool IsAlwaysOnTop { get; private set; }

    public bool IsMaximized { get; private set; }

    public double WindowOpacity { get; private set; } = 1.0;

    public event EventHandler<bool>? AlwaysOnTopChanged;
    public event EventHandler<WindowState>? WindowStateChanged;
    public event EventHandler<double>? WindowOpacityChanged;

    public void AttachWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (_window is not null)
        {
            DetachWindow();
        }

        _window = window;

        var appSettings = _settingsService.CurrentSettings;
        var winSettings = _settingsService.CurrentWindowSettings;

        var (presetWidth, presetHeight) = AppSettings.GetPresetDimensions(
            appSettings.WindowSizePreset,
            appSettings.DefaultWindowWidth,
            appSettings.DefaultWindowHeight);

        var targetWidth = appSettings.RememberLastWindowPosition ? winSettings.Width : presetWidth;
        var targetHeight = appSettings.RememberLastWindowPosition ? winSettings.Height : presetHeight;
        var useSavedPos = appSettings.RememberLastWindowPosition && winSettings.HasSavedPosition;

        var monitors = GetMonitorsForWindow(window);
        var (clampedLeft, clampedTop, clampedWidth, clampedHeight) = WindowHelper.ClampBoundsToVisibleMonitors(
            winSettings.Left ?? double.NaN,
            winSettings.Top ?? double.NaN,
            targetWidth,
            targetHeight,
            monitors,
            centerIfInvalidPosition: !useSavedPos);

        _isApplyingBounds = true;
        try
        {
            window.Width = clampedWidth;
            window.Height = clampedHeight;
            window.Left = clampedLeft;
            window.Top = clampedTop;

            IsAlwaysOnTop = winSettings.AlwaysOnTop;
            window.Topmost = IsAlwaysOnTop;

            WindowOpacity = AppSettings.ClampOpacity(appSettings.WindowOpacity);
            window.Opacity = WindowOpacity;

            if (appSettings.StartMinimized)
            {
                window.WindowState = WindowState.Minimized;
            }
            else if (appSettings.RememberLastWindowPosition && winSettings.IsMaximized)
            {
                window.WindowState = WindowState.Maximized;
            }
            else
            {
                window.WindowState = WindowState.Normal;
            }

            IsMaximized = window.WindowState == WindowState.Maximized;
        }
        finally
        {
            _isApplyingBounds = false;
        }

        window.StateChanged += OnWindowStateChanged;
        window.DpiChanged += OnWindowDpiChanged;
        window.LocationChanged += OnWindowGeometryChanged;
        window.SizeChanged += OnWindowGeometryChanged;

        _loggingService.Information(
            "Attached window bounds ({Left}, {Top}, {Width}x{Height}), Maximized={Maximized}, AlwaysOnTop={Topmost}.",
            clampedLeft, clampedTop, clampedWidth, clampedHeight, IsMaximized, IsAlwaysOnTop);
    }

    public void DetachWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.StateChanged -= OnWindowStateChanged;
        _window.DpiChanged -= OnWindowDpiChanged;
        _window.LocationChanged -= OnWindowGeometryChanged;
        _window.SizeChanged -= OnWindowGeometryChanged;
        _window = null;
    }

    public void Minimize()
    {
        if (_window is null)
        {
            return;
        }

        _window.WindowState = WindowState.Minimized;
    }

    public void ToggleMaximizeRestore()
    {
        if (_window is null)
        {
            return;
        }

        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    public void RestoreFromMinimized()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        EnsureWindowVisibleOnDesktop();
        _window.Activate();
    }

    public void CloseWindow()
    {
        _window?.Close();
    }

    public void SetAlwaysOnTop(bool enabled)
    {
        IsAlwaysOnTop = enabled;
        if (_window is not null)
        {
            _window.Topmost = enabled;
        }

        var updated = _settingsService.CurrentWindowSettings;
        updated.AlwaysOnTop = enabled;
        _settingsService.SaveWindowSettings(updated);

        _loggingService.Information("Always-on-top set to {Enabled}.", enabled);
        AlwaysOnTopChanged?.Invoke(this, enabled);
    }

    public void SetWindowOpacity(double opacity, bool persist = false)
    {
        var clamped = AppSettings.ClampOpacity(opacity);
        var changed = Math.Abs(WindowOpacity - clamped) > 0.001;
        WindowOpacity = clamped;

        if (_window is not null)
        {
            _window.Opacity = clamped;
        }

        if (persist)
        {
            var settings = _settingsService.CurrentSettings.Clone();
            settings.WindowOpacity = clamped;
            _settingsService.SaveSettings(settings);
        }

        if (changed)
        {
            WindowOpacityChanged?.Invoke(this, clamped);
        }
    }

    public void EnsureWindowVisibleOnDesktop()
    {
        if (_window is null || _window.WindowState == WindowState.Maximized)
        {
            return;
        }

        var monitors = GetMonitorsForWindow(_window);
        var (left, top, width, height) = WindowHelper.ClampBoundsToVisibleMonitors(
            _window.Left,
            _window.Top,
            _window.Width,
            _window.Height,
            monitors);

        if (Math.Abs(_window.Left - left) > 1 ||
            Math.Abs(_window.Top - top) > 1 ||
            Math.Abs(_window.Width - width) > 1 ||
            Math.Abs(_window.Height - height) > 1)
        {
            _isApplyingBounds = true;
            try
            {
                _window.Left = left;
                _window.Top = top;
                _window.Width = width;
                _window.Height = height;
            }
            finally
            {
                _isApplyingBounds = false;
            }

            _loggingService.Information("Repositioned window inside visible desktop ({Left}, {Top}, {Width}x{Height}).",
                left, top, width, height);
        }
    }

    public void ApplyConfiguredDefaultSize()
    {
        if (_window is null)
        {
            return;
        }

        var settings = _settingsService.CurrentSettings;
        var (targetW, targetH) = AppSettings.GetPresetDimensions(
            settings.WindowSizePreset,
            settings.DefaultWindowWidth,
            settings.DefaultWindowHeight);

        if (_window.WindowState == WindowState.Maximized)
        {
            _window.WindowState = WindowState.Normal;
        }

        var monitors = GetMonitorsForWindow(_window);
        var (left, top, width, height) = WindowHelper.ClampBoundsToVisibleMonitors(
            _window.Left,
            _window.Top,
            targetW,
            targetH,
            monitors,
            centerIfInvalidPosition: true);

        _isApplyingBounds = true;
        try
        {
            _window.Width = width;
            _window.Height = height;
            _window.Left = left;
            _window.Top = top;
        }
        finally
        {
            _isApplyingBounds = false;
        }
    }

    public void SaveCurrentWindowBounds(NavigationSection currentSection) =>
        SaveCurrentWindowBounds(currentSection, _settingsService.CurrentWindowSettings.IsSidebarExpanded);

    public void SaveCurrentWindowBounds(NavigationSection currentSection, bool isSidebarExpanded)
    {
        if (_window is null)
        {
            return;
        }

        var existing = _settingsService.CurrentWindowSettings;
        var isMaximized = _window.WindowState == WindowState.Maximized;

        var bounds = _window.WindowState == WindowState.Normal
            ? new Rect(_window.Left, _window.Top, _window.Width, _window.Height)
            : _window.RestoreBounds;

        if (!bounds.IsEmpty && bounds.Width >= WindowHelper.DefaultMinWidth && bounds.Height >= WindowHelper.DefaultMinHeight)
        {
            existing.Left = bounds.Left;
            existing.Top = bounds.Top;
            existing.Width = bounds.Width;
            existing.Height = bounds.Height;
        }

        existing.IsMaximized = isMaximized;
        existing.AlwaysOnTop = IsAlwaysOnTop;
        existing.IsSidebarExpanded = isSidebarExpanded;
        existing.LastNavigationSection = currentSection;

        _settingsService.SaveWindowSettings(existing);
    }

    private IReadOnlyList<ScreenWorkArea> GetMonitorsForWindow(Window window)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            return WindowHelper.GetDisplayWorkAreas(dpi.DpiScaleX, dpi.DpiScaleY);
        }
        catch
        {
            return WindowHelper.GetDisplayWorkAreas();
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_window is null)
        {
            return;
        }

        IsMaximized = _window.WindowState == WindowState.Maximized;
        if (_window.WindowState == WindowState.Normal)
        {
            EnsureWindowVisibleOnDesktop();
        }

        WindowStateChanged?.Invoke(this, _window.WindowState);
    }

    private void OnWindowDpiChanged(object sender, DpiChangedEventArgs e)
    {
        EnsureWindowVisibleOnDesktop();
    }

    private void OnWindowGeometryChanged(object? sender, EventArgs e)
    {
        if (_isApplyingBounds || _window is null || _window.WindowState != WindowState.Normal)
        {
            return;
        }

        var existing = _settingsService.CurrentWindowSettings;
        existing.Left = _window.Left;
        existing.Top = _window.Top;
        existing.Width = _window.Width;
        existing.Height = _window.Height;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _window?.Dispatcher.BeginInvoke(() =>
        {
            _loggingService.Information("Display configuration change detected; verifying window visibility.");
            EnsureWindowVisibleOnDesktop();
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        DetachWindow();
    }
}
