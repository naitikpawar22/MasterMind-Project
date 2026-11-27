using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GeminiDesktop.Models;

namespace GeminiDesktop.Helpers;

/// <summary>
/// Represents a display monitor work area in device-independent pixels (DIPs).
/// </summary>
public readonly record struct ScreenWorkArea(double Left, double Top, double Width, double Height, bool IsPrimary)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;

    public bool Intersects(double left, double top, double width, double height) =>
        left < Right && (left + width) > Left && top < Bottom && (top + height) > Top;

    public double IntersectionArea(double left, double top, double width, double height)
    {
        var ix = Math.Max(0, Math.Min(Right, left + width) - Math.Max(Left, left));
        var iy = Math.Max(0, Math.Min(Bottom, top + height) - Math.Max(Top, top));
        return ix * iy;
    }
}

/// <summary>
/// OS version details reported via RtlGetVersion.
/// </summary>
public readonly record struct WindowsVersionDetails(int Major, int Minor, int Build, string DisplayName)
{
    public bool IsWindows11 => Major >= 10 && Build >= 22000;
    public bool SupportsExcludeFromCapture => Major > 10 || (Major == 10 && Build >= 19041);
    public bool SupportsMonitorAffinity => Major >= 6;
}

/// <summary>
/// Pure Win32 P/Invoke and multi-monitor coordinate clamping helpers for WPF windows.
/// </summary>
public static class WindowHelper
{
    public const double DefaultMinWidth = 900;
    public const double DefaultMinHeight = 620;

    private const int DwmwaDisallowPeek = 12;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const uint MonitorinfoPrimaryFlag = 0x00000001;
    private const uint MdtEffectiveDpi = 0;
    private const int GwlStyle = -16;
    private const uint WsChild = 0x40000000;

    public static WindowsVersionDetails GetWindowsVersion()
    {
        var info = new RtlOsVersionInfoEx { OsVersionInfoSize = (uint)Marshal.SizeOf<RtlOsVersionInfoEx>() };
        if (RtlGetVersion(ref info) == 0)
        {
            var friendly = info.BuildNumber >= 22000
                ? $"Windows 11 (Build {info.BuildNumber})"
                : $"Windows 10 (Build {info.BuildNumber})";
            return new WindowsVersionDetails((int)info.MajorVersion, (int)info.MinorVersion, (int)info.BuildNumber, friendly);
        }

        var v = Environment.OSVersion.Version;
        var fallback = v.Build >= 22000 ? $"Windows 11 (Build {v.Build})" : $"Windows {v.Major} (Build {v.Build})";
        return new WindowsVersionDetails(v.Major, v.Minor, v.Build, fallback);
    }

    public static bool TrySetWindowDisplayAffinity(IntPtr hwnd, WindowDisplayAffinityMode mode, out int win32Error)
    {
        win32Error = 0;
        if (hwnd == IntPtr.Zero)
        {
            win32Error = 6; // ERROR_INVALID_HANDLE
            return false;
        }

        var ok = SetWindowDisplayAffinity(hwnd, (uint)mode);
        if (!ok)
        {
            win32Error = Marshal.GetLastWin32Error();
        }

        return ok;
    }

    public static bool TryGetWindowDisplayAffinity(IntPtr hwnd, out WindowDisplayAffinityMode mode, out int win32Error)
    {
        mode = WindowDisplayAffinityMode.None;
        win32Error = 0;
        if (hwnd == IntPtr.Zero)
        {
            win32Error = 6;
            return false;
        }

        var ok = GetWindowDisplayAffinity(hwnd, out var rawAffinity);
        if (!ok)
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        mode = Enum.IsDefined(typeof(WindowDisplayAffinityMode), rawAffinity)
            ? (WindowDisplayAffinityMode)rawAffinity
            : WindowDisplayAffinityMode.None;
        return true;
    }

    public static void ApplyDwmWindowChromeAttributes(Window window, bool isDarkTheme)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var darkFlag = isDarkTheme ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkFlag, sizeof(int));

        if (GetWindowsVersion().IsWindows11)
        {
            var cornerPreference = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));
        }
    }

    /// <summary>
    /// Prevents or allows Windows Taskbar Aero Peek preview for the specified window handle.
    /// </summary>
    public static void SetWindowDisallowPeek(IntPtr hwnd, bool disallowPeek)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var flag = disallowPeek ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaDisallowPeek, ref flag, sizeof(int));
    }

    /// <summary>
    /// Applies display affinity to all top-level non-child windows belonging to the current process
    /// (such as WPF ToolTips, ContextMenus, ComboBox popups, and secondary dialogs).
    /// </summary>
    public static void ApplyDisplayAffinityToProcessTopLevelWindows(WindowDisplayAffinityMode mode, IntPtr primaryHwnd)
    {
        var currentPid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, lParam) =>
        {
            if (hwnd == primaryHwnd)
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != currentPid)
            {
                return true;
            }

            var style = (uint)GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
            if ((style & WsChild) != 0)
            {
                return true;
            }

            SetWindowDisplayAffinity(hwnd, (uint)mode);
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>
    /// Enumerates all active display work areas in WPF DIP coordinates relative to the specified window DPI (or primary DPI).
    /// </summary>
    public static IReadOnlyList<ScreenWorkArea> GetDisplayWorkAreas(double referenceScaleX = 1.0, double referenceScaleY = 1.0)
    {
        var areas = new List<ScreenWorkArea>();
        var scaleX = referenceScaleX > 0 ? referenceScaleX : 1.0;
        var scaleY = referenceScaleY > 0 ? referenceScaleY : 1.0;

        EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr hMonitor, IntPtr _, ref Rect _, IntPtr _) =>
            {
                var mi = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfoW(hMonitor, ref mi))
                {
                    var monScaleX = scaleX;
                    var monScaleY = scaleY;
                    if (GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out var dpiX, out var dpiY) == 0 && dpiX > 0 && dpiY > 0)
                    {
                        // WPF uses system/window DIP scaling when placing windows across virtual desktop coordinates.
                        // Using system virtual coordinates scaled by primary/reference scale keeps Left/Top consistent across monitors.
                        _ = dpiX;
                        _ = dpiY;
                    }

                    var work = mi.Work;
                    var left = work.Left / monScaleX;
                    var top = work.Top / monScaleY;
                    var width = Math.Max(100, (work.Right - work.Left) / monScaleX);
                    var height = Math.Max(100, (work.Bottom - work.Top) / monScaleY);
                    var isPrimary = (mi.Flags & MonitorinfoPrimaryFlag) != 0;
                    areas.Add(new ScreenWorkArea(left, top, width, height, isPrimary));
                }

                return true;
            },
            IntPtr.Zero);

        if (areas.Count == 0)
        {
            var wa = SystemParameters.WorkArea;
            areas.Add(new ScreenWorkArea(wa.Left, wa.Top, wa.Width, wa.Height, true));
        }

        return areas;
    }

    /// <summary>
    /// Pure, deterministic clamping logic guaranteeing that the window stays inside at least one visible monitor work area
    /// with valid dimensions and visible title bar grab area.
    /// </summary>
    public static (double Left, double Top, double Width, double Height) ClampBoundsToVisibleMonitors(
        double desiredLeft,
        double desiredTop,
        double desiredWidth,
        double desiredHeight,
        IReadOnlyList<ScreenWorkArea> monitors,
        bool centerIfInvalidPosition = false,
        double minWidth = DefaultMinWidth,
        double minHeight = DefaultMinHeight)
    {
        if (monitors is null || monitors.Count == 0)
        {
            var w = Math.Max(minWidth, desiredWidth);
            var h = Math.Max(minHeight, desiredHeight);
            return (0, 0, w, h);
        }

        var primary = monitors.FirstOrDefault(m => m.IsPrimary);
        if (primary.Width <= 0 || primary.Height <= 0)
        {
            primary = monitors[0];
        }

        var width = double.IsNaN(desiredWidth) || double.IsInfinity(desiredWidth) ? 1280 : desiredWidth;
        var height = double.IsNaN(desiredHeight) || double.IsInfinity(desiredHeight) ? 820 : desiredHeight;

        var hasValidPosition = !centerIfInvalidPosition &&
                               !double.IsNaN(desiredLeft) && !double.IsInfinity(desiredLeft) &&
                               !double.IsNaN(desiredTop) && !double.IsInfinity(desiredTop);

        ScreenWorkArea targetMonitor = primary;

        if (hasValidPosition)
        {
            // Find the monitor with the largest overlap area with the requested window rectangle.
            double bestArea = 0;
            foreach (var monitor in monitors)
            {
                var overlap = monitor.IntersectionArea(desiredLeft, desiredTop, Math.Max(1, width), Math.Max(1, height));
                if (overlap > bestArea)
                {
                    bestArea = overlap;
                    targetMonitor = monitor;
                }
            }

            // If completely offscreen (0 overlap with all monitors), snap back to primary monitor centered.
            if (bestArea <= 0)
            {
                hasValidPosition = false;
                targetMonitor = primary;
            }
        }

        var effectiveMinWidth = Math.Min(minWidth, targetMonitor.Width);
        var effectiveMinHeight = Math.Min(minHeight, targetMonitor.Height);
        var clampedWidth = Math.Clamp(width, effectiveMinWidth, targetMonitor.Width);
        var clampedHeight = Math.Clamp(height, effectiveMinHeight, targetMonitor.Height);

        if (!hasValidPosition)
        {
            var centeredLeft = targetMonitor.Left + ((targetMonitor.Width - clampedWidth) / 2.0);
            var centeredTop = targetMonitor.Top + ((targetMonitor.Height - clampedHeight) / 2.0);
            return (Math.Round(centeredLeft), Math.Round(centeredTop), Math.Round(clampedWidth), Math.Round(clampedHeight));
        }

        var maxLeft = targetMonitor.Right - clampedWidth;
        var maxTop = targetMonitor.Bottom - clampedHeight;
        var clampedLeft = Math.Clamp(desiredLeft, targetMonitor.Left, Math.Max(targetMonitor.Left, maxLeft));
        var clampedTop = Math.Clamp(desiredTop, targetMonitor.Top, Math.Max(targetMonitor.Top, maxTop));

        return (Math.Round(clampedLeft), Math.Round(clampedTop), Math.Round(clampedWidth), Math.Round(clampedHeight));
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RtlOsVersionInfoEx
    {
        public uint OsVersionInfoSize;
        public uint MajorVersion;
        public uint MinorVersion;
        public uint BuildNumber;
        public uint PlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string CsdVersion;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint dwAffinity);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, uint dpiType, out uint dpiX, out uint dpiY);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref RtlOsVersionInfoEx versionInfo);
}
