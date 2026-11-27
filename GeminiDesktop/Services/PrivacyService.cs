using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using Microsoft.Win32;

namespace GeminiDesktop.Services;

/// <summary>
/// Abstraction over Win32 display affinity calls to allow clean testing and hardware fallback diagnostics.
/// </summary>
public interface IDisplayAffinityAdapter
{
    WindowsVersionDetails GetWindowsVersion();
    bool TrySetDisplayAffinity(IntPtr hwnd, WindowDisplayAffinityMode mode, out int win32Error);
    bool TryGetDisplayAffinity(IntPtr hwnd, out WindowDisplayAffinityMode mode, out int win32Error);
    void SetDisallowPeek(IntPtr hwnd, bool disallowPeek);
    void ApplyAffinityToOwnedPopups(WindowDisplayAffinityMode mode, IntPtr primaryHwnd);
}

public sealed class Win32DisplayAffinityAdapter : IDisplayAffinityAdapter
{
    public WindowsVersionDetails GetWindowsVersion() => WindowHelper.GetWindowsVersion();

    public bool TrySetDisplayAffinity(IntPtr hwnd, WindowDisplayAffinityMode mode, out int win32Error) =>
        WindowHelper.TrySetWindowDisplayAffinity(hwnd, mode, out win32Error);

    public bool TryGetDisplayAffinity(IntPtr hwnd, out WindowDisplayAffinityMode mode, out int win32Error) =>
        WindowHelper.TryGetWindowDisplayAffinity(hwnd, out mode, out win32Error);

    public void SetDisallowPeek(IntPtr hwnd, bool disallowPeek) =>
        WindowHelper.SetWindowDisallowPeek(hwnd, disallowPeek);

    public void ApplyAffinityToOwnedPopups(WindowDisplayAffinityMode mode, IntPtr primaryHwnd) =>
        WindowHelper.ApplyDisplayAffinityToProcessTopLevelWindows(mode, primaryHwnd);
}

/// <summary>
/// Provides transparent, documented Windows window privacy and capture-exclusion management.
/// </summary>
public interface IPrivacyService : IDisposable
{
    PrivacyStatus CurrentStatus { get; }

    event EventHandler<PrivacyStatus>? PrivacyStatusChanged;

    void AttachWindow(Window window);
    void SyncSecondaryWindow(Window window);
    PrivacyStatus RefreshStatus();
    PrivacyStatus SetCaptureExclusion(bool enabled);
    void NotifyAlwaysOnTopChanged(bool isAlwaysOnTop);
}

public sealed class PrivacyService : IPrivacyService
{
    private const string StandardLimitationNotice =
        "Windows SetWindowDisplayAffinity applies only to compatible Windows desktop capture APIs (such as Windows Graphics Capture, Snipping Tool, Xbox Game Bar, PrintWindow, and BitBlt/Desktop Duplication). " +
        "It does NOT guarantee invisibility across all capture mechanisms: hardware capture cards, external cameras, privileged driver-level capture, remote desktop hosts, " +
        "or meeting software window/tab/browser-source sharing controls (Google Meet, Zoom, Microsoft Teams) may still capture or display screen content or restrict sharing. " +
        "This feature is provided solely for legitimate desktop privacy and never attempts to bypass meeting software controls or secretly hide AI assistance.";

    private static bool _popupClassHandlerRegistered;

    private readonly IDisplayAffinityAdapter _adapter;
    private readonly ILoggingService _loggingService;

    private Window? _window;
    private Window? _secondaryWindow;
    private IntPtr _hwnd = IntPtr.Zero;
    private WindowDisplayAffinityMode _desiredMode = WindowDisplayAffinityMode.None;
    private bool _isAlwaysOnTop;
    private int _lastWin32Error;
    private string? _lastErrorMessage;
    private bool _disposed;

    public PrivacyService(IDisplayAffinityAdapter adapter, ILoggingService loggingService)
    {
        _adapter = adapter;
        _loggingService = loggingService;
        CurrentStatus = BuildStatus(WindowDisplayAffinityMode.None);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public PrivacyStatus CurrentStatus { get; private set; }

    public event EventHandler<PrivacyStatus>? PrivacyStatusChanged;

    public void AttachWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        DetachWindowEvents();
        _window = window;

        _hwnd = new WindowInteropHelper(window).Handle;
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = new WindowInteropHelper(window).EnsureHandle();
        }

        _isAlwaysOnTop = window.Topmost;

        window.StateChanged += OnWindowReverifyNeeded;
        window.Activated += OnWindowReverifyNeeded;
        window.DpiChanged += OnWindowDpiChanged;
        window.Closed += OnWindowClosed;

        RegisterPopupAffinityHook();
        RefreshStatus();
    }

    public void SyncSecondaryWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _secondaryWindow = window;

        var secHwnd = new WindowInteropHelper(window).Handle;
        if (secHwnd == IntPtr.Zero)
        {
            secHwnd = new WindowInteropHelper(window).EnsureHandle();
        }

        if (secHwnd != IntPtr.Zero)
        {
            _adapter.TrySetDisplayAffinity(secHwnd, _desiredMode, out _);
            _adapter.SetDisallowPeek(secHwnd, _desiredMode == WindowDisplayAffinityMode.ExcludeFromCapture);
        }
    }

    public void NotifyAlwaysOnTopChanged(bool isAlwaysOnTop)
    {
        _isAlwaysOnTop = isAlwaysOnTop;
        RefreshStatus();
    }

    public PrivacyStatus RefreshStatus()
    {
        var applied = WindowDisplayAffinityMode.None;
        if (_hwnd != IntPtr.Zero && _adapter.TryGetDisplayAffinity(_hwnd, out var current, out _))
        {
            applied = current;
        }

        CurrentStatus = BuildStatus(applied);
        PrivacyStatusChanged?.Invoke(this, CurrentStatus);
        return CurrentStatus;
    }

    public PrivacyStatus SetCaptureExclusion(bool enabled)
    {
        var os = _adapter.GetWindowsVersion();
        _lastWin32Error = 0;
        _lastErrorMessage = null;

        if (_hwnd == IntPtr.Zero)
        {
            _desiredMode = WindowDisplayAffinityMode.None;
            _lastWin32Error = 6;
            _lastErrorMessage = "Main window handle is not initialized yet.";
            CurrentStatus = BuildStatus(WindowDisplayAffinityMode.None);
            PrivacyStatusChanged?.Invoke(this, CurrentStatus);
            return CurrentStatus;
        }

        if (enabled && !os.SupportsExcludeFromCapture)
        {
            _desiredMode = WindowDisplayAffinityMode.None;
            _lastWin32Error = 50; // ERROR_NOT_SUPPORTED
            _lastErrorMessage =
                $"WDA_EXCLUDEFROMCAPTURE requires Windows 10 version 2004 (Build 19041) or Windows 11. Detected {os.DisplayName}.";
            _loggingService.Warning("Capture exclusion requested on unsupported OS build: {OS}", os.DisplayName);
            CurrentStatus = BuildStatus(WindowDisplayAffinityMode.None);
            PrivacyStatusChanged?.Invoke(this, CurrentStatus);
            return CurrentStatus;
        }

        var desiredMode = enabled
            ? WindowDisplayAffinityMode.ExcludeFromCapture
            : WindowDisplayAffinityMode.None;
        _desiredMode = desiredMode;

        if (!_adapter.TrySetDisplayAffinity(_hwnd, desiredMode, out var win32Err))
        {
            _lastWin32Error = win32Err;
            _lastErrorMessage =
                $"Windows rejected SetWindowDisplayAffinity ({new Win32Exception(win32Err).Message}, Win32 code {win32Err}). " +
                "This can happen in certain virtualized displays, remote desktop sessions, or incompatible GPU drivers.";
            _loggingService.Warning("SetWindowDisplayAffinity({Mode}) failed with Win32 error {Code}: {Msg}",
                desiredMode, win32Err, _lastErrorMessage);
        }
        else
        {
            _adapter.SetDisallowPeek(_hwnd, enabled);
            _adapter.ApplyAffinityToOwnedPopups(desiredMode, _hwnd);
            if (_secondaryWindow is not null)
            {
                SyncSecondaryWindow(_secondaryWindow);
            }
            _loggingService.Information("Window display affinity updated to {Mode}.", desiredMode);
        }

        var verified = WindowDisplayAffinityMode.None;
        _adapter.TryGetDisplayAffinity(_hwnd, out verified, out _);

        CurrentStatus = BuildStatus(verified);
        PrivacyStatusChanged?.Invoke(this, CurrentStatus);
        return CurrentStatus;
    }

    private void ReverifyActiveAffinity()
    {
        if (_hwnd == IntPtr.Zero || _desiredMode != WindowDisplayAffinityMode.ExcludeFromCapture)
        {
            return;
        }

        if (_adapter.TryGetDisplayAffinity(_hwnd, out var current, out _) && current != _desiredMode)
        {
            if (_adapter.TrySetDisplayAffinity(_hwnd, _desiredMode, out _))
            {
                _adapter.SetDisallowPeek(_hwnd, true);
                _loggingService.Information("Re-applied WDA_EXCLUDEFROMCAPTURE after window/display state change.");
            }
        }

        _adapter.ApplyAffinityToOwnedPopups(_desiredMode, _hwnd);
        RefreshStatus();
    }

    private void RegisterPopupAffinityHook()
    {
        if (_popupClassHandlerRegistered || Application.Current is null)
        {
            return;
        }

        _popupClassHandlerRegistered = true;
        var handler = new RoutedEventHandler((_, _) =>
        {
            if (_desiredMode == WindowDisplayAffinityMode.ExcludeFromCapture && _hwnd != IntPtr.Zero)
            {
                _adapter.ApplyAffinityToOwnedPopups(WindowDisplayAffinityMode.ExcludeFromCapture, _hwnd);
            }
        });

        EventManager.RegisterClassHandler(typeof(Popup), FrameworkElement.LoadedEvent, handler);
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu), FrameworkElement.LoadedEvent, handler);
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ToolTip), FrameworkElement.LoadedEvent, handler);
    }

    private void OnWindowReverifyNeeded(object? sender, EventArgs e)
    {
        if (_window?.WindowState != WindowState.Minimized)
        {
            ReverifyActiveAffinity();
        }
    }

    private void OnWindowDpiChanged(object sender, DpiChangedEventArgs e) => ReverifyActiveAffinity();

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _window?.Dispatcher.BeginInvoke(ReverifyActiveAffinity);

    private void OnWindowClosed(object? sender, EventArgs e) => DetachWindowEvents();

    private void DetachWindowEvents()
    {
        if (_window is null)
        {
            return;
        }

        _window.StateChanged -= OnWindowReverifyNeeded;
        _window.Activated -= OnWindowReverifyNeeded;
        _window.DpiChanged -= OnWindowDpiChanged;
        _window.Closed -= OnWindowClosed;
        _window = null;
    }

    private PrivacyStatus BuildStatus(WindowDisplayAffinityMode appliedAffinity)
    {
        var os = _adapter.GetWindowsVersion();
        var isExcluded = appliedAffinity == WindowDisplayAffinityMode.ExcludeFromCapture;

        var capabilitySummary = os.SupportsExcludeFromCapture
            ? $"Supported on {os.DisplayName}: Windows Desktop Window Manager (DWM) supports WDA_EXCLUDEFROMCAPTURE (0x11)."
            : $"Unavailable on {os.DisplayName}: WDA_EXCLUDEFROMCAPTURE requires Windows 10 Build 19041 (2004) or newer.";

        var statusSummary = isExcluded
            ? "Privacy Mode: Active (Capture Exclusion Enabled)"
            : appliedAffinity == WindowDisplayAffinityMode.MonitorOnly
                ? "Privacy Mode: Legacy Monitor-Only Affinity"
                : "Privacy Mode: Off (Standard Visibility)";

        var sharingExplanation = isExcluded
            ? "Privacy Mode is enabled with Windows WDA_EXCLUDEFROMCAPTURE (0x11). Compatible Windows screen capture APIs (Windows Graphics Capture, Snipping Tool, Xbox Game Bar, PrintWindow, BitBlt/Desktop Duplication) omit this window surface from captured frames while keeping MyLaptop fully interactive on your physical display. Switching Privacy Mode off requires the unlock password."
            : "Privacy Mode is disabled (WDA_NONE, 0x00). The MyLaptop window behaves as a normal Windows application and is visible in screenshots, screen recordings, and screen shares.";

        return new PrivacyStatus
        {
            IsCaptureExclusionActive = isExcluded,
            AppliedAffinity = appliedAffinity,
            IsExcludeFromCaptureSupported = os.SupportsExcludeFromCapture,
            IsMonitorAffinitySupported = os.SupportsMonitorAffinity,
            IsAlwaysOnTopActive = _isAlwaysOnTop,
            OsVersionDescription = os.DisplayName,
            CapabilitySummary = capabilitySummary,
            StatusSummary = statusSummary,
            SharingExplanation = sharingExplanation,
            LimitationNotice = StandardLimitationNotice,
            LastWin32ErrorCode = _lastWin32Error,
            LastErrorMessage = _lastErrorMessage
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        DetachWindowEvents();
    }

    /// <summary>
    /// Enables Privacy Mode immediately without a password, and requires password verification when switching from ON to OFF.
    /// </summary>
    public static bool TryChangePrivacyModeWithPassword(
        IPrivacyService privacyService,
        IDialogService dialogService,
        bool enable)
    {
        ArgumentNullException.ThrowIfNull(privacyService);
        ArgumentNullException.ThrowIfNull(dialogService);

        if (enable)
        {
            privacyService.SetCaptureExclusion(true);
            return true;
        }

        if (!privacyService.CurrentStatus.IsCaptureExclusionActive)
        {
            privacyService.SetCaptureExclusion(false);
            return true;
        }

        var enteredPassword = dialogService.PromptPassword(
            "Privacy Mode Password Required",
            "Privacy Mode is currently active. Enter the password to turn Privacy Mode off:");

        if (enteredPassword is null)
        {
            return false;
        }

        if (!ConfigurationHelper.VerifyPrivacyUnlockPassword(enteredPassword))
        {
            dialogService.ShowError(
                "Incorrect Password",
                "The entered password is incorrect. Privacy Mode remains active.");
            return false;
        }

        privacyService.SetCaptureExclusion(false);
        return true;
    }
}
