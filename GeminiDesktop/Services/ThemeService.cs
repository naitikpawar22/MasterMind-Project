using System.Windows;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using Microsoft.Win32;

namespace GeminiDesktop.Services;

/// <summary>
/// Manages Light, Dark, and System-default themes for WPF resources and DWM window frames.
/// </summary>
public interface IThemeService : IDisposable
{
    AppThemeMode ActiveThemeMode { get; }
    bool IsDarkThemeActive { get; }

    event EventHandler<bool>? EffectiveThemeChanged;

    void ApplyTheme(AppThemeMode mode);
    void ApplyWindowChromeTheme(Window window);
}

public sealed class ThemeService : IThemeService
{
    private static readonly Uri LightThemeUri = new("Resources/Themes/LightTheme.xaml", UriKind.Relative);
    private static readonly Uri DarkThemeUri = new("Resources/Themes/DarkTheme.xaml", UriKind.Relative);

    private readonly ILoggingService _loggingService;
    private ResourceDictionary? _activeThemeDictionary;
    private bool _disposed;

    public ThemeService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
        SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;
    }

    public AppThemeMode ActiveThemeMode { get; private set; } = AppThemeMode.Light;

    public bool IsDarkThemeActive { get; private set; }

    public event EventHandler<bool>? EffectiveThemeChanged;

    public void ApplyTheme(AppThemeMode mode)
    {
        ActiveThemeMode = mode;
        var shouldUseDark = mode switch
        {
            AppThemeMode.Dark => true,
            AppThemeMode.SystemDefault => IsWindowsDarkModeEnabled(),
            _ => false
        };

        IsDarkThemeActive = shouldUseDark;
        SwapThemeDictionary(shouldUseDark ? DarkThemeUri : LightThemeUri);

        if (Application.Current?.Windows is not null)
        {
            foreach (Window window in Application.Current.Windows)
            {
                ApplyWindowChromeTheme(window);
            }
        }

        _loggingService.Debug("Applied theme mode {Mode} (IsDark={IsDark}).", mode, shouldUseDark);
        EffectiveThemeChanged?.Invoke(this, shouldUseDark);
    }

    public void ApplyWindowChromeTheme(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        WindowHelper.ApplyDwmWindowChromeAttributes(window, IsDarkThemeActive);
    }

    private void SwapThemeDictionary(Uri themeUri)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var merged = app.Resources.MergedDictionaries;
        var newDict = new ResourceDictionary { Source = themeUri };

        if (_activeThemeDictionary is not null)
        {
            var idx = merged.IndexOf(_activeThemeDictionary);
            if (idx >= 0)
            {
                merged[idx] = newDict;
                _activeThemeDictionary = newDict;
                return;
            }
        }

        // Replace existing Light/Dark theme entry if loaded from App.xaml initially.
        for (var i = 0; i < merged.Count; i++)
        {
            var src = merged[i].Source?.OriginalString;
            if (src is not null &&
                (src.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
                 src.EndsWith("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                merged[i] = newDict;
                _activeThemeDictionary = newDict;
                return;
            }
        }

        merged.Insert(0, newDict);
        _activeThemeDictionary = newDict;
    }

    private static bool IsWindowsDarkModeEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int intVal && intVal == 0;
        }
        catch
        {
            return false;
        }
    }

    private void OnSystemUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (ActiveThemeMode != AppThemeMode.SystemDefault)
        {
            return;
        }

        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => ApplyTheme(AppThemeMode.SystemDefault));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged;
    }
}
