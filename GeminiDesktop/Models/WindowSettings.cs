namespace GeminiDesktop.Models;

/// <summary>
/// Persisted main window bounds and display preferences.
/// </summary>
public sealed class WindowSettings
{
    public double? Left { get; set; }

    public double? Top { get; set; }

    public double Width { get; set; } = 1280;

    public double Height { get; set; } = 820;

    public bool IsMaximized { get; set; } = false;

    public bool AlwaysOnTop { get; set; } = false;

    public bool IsSidebarExpanded { get; set; } = true;

    public NavigationSection LastNavigationSection { get; set; } = NavigationSection.Home;

    public bool HasSavedPosition =>
        Left is double l &&
        Top is double t &&
        !double.IsNaN(l) &&
        !double.IsNaN(t) &&
        !double.IsInfinity(l) &&
        !double.IsInfinity(t);

    public WindowSettings Clone() => new()
    {
        Left = Left,
        Top = Top,
        Width = Width,
        Height = Height,
        IsMaximized = IsMaximized,
        AlwaysOnTop = AlwaysOnTop,
        IsSidebarExpanded = IsSidebarExpanded,
        LastNavigationSection = LastNavigationSection
    };
}
