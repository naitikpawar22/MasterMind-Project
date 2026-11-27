using System.IO;
using GeminiDesktop.Models;
using GeminiDesktop.Services;
using Xunit;

namespace GeminiDesktop.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public SettingsServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "GeminiDesktopTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void SaveAndReload_PersistsSettingsAndSanitizesTamperedUrl()
    {
        using var log = new LoggingService(Path.Combine(_tempRoot, "logs"));
        var service = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);

        var updated = new AppSettings
        {
            Theme = AppThemeMode.Dark,
            LaunchBehavior = LaunchDestination.GeminiWorkspace,
            WindowSizePreset = DefaultWindowSizePreset.Wide,
            RememberLastWindowPosition = false,
            StartMinimized = true,
            GeminiHomepageUrl = "https://gemini.google.com/app",
            OpenExternalLinksInSystemBrowser = false,
            WindowOpacity = 0.65
        };

        service.SaveSettings(updated);

        var reloadedService = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);
        var loaded = reloadedService.CurrentSettings;

        Assert.Equal(AppThemeMode.Dark, loaded.Theme);
        Assert.Equal(LaunchDestination.GeminiWorkspace, loaded.LaunchBehavior);
        Assert.Equal(DefaultWindowSizePreset.Wide, loaded.WindowSizePreset);
        Assert.False(loaded.RememberLastWindowPosition);
        Assert.True(loaded.StartMinimized);
        Assert.Equal("https://gemini.google.com/app", loaded.GeminiHomepageUrl);
        Assert.False(loaded.OpenExternalLinksInSystemBrowser);
        Assert.Equal(0.65d, loaded.WindowOpacity, 3);
    }

    [Fact]
    public void LoadSettings_RecoversGracefullyFromCorruptJson()
    {
        using var log = new LoggingService(Path.Combine(_tempRoot, "logs"));
        var appDir = Path.Combine(_tempRoot, "MyLaptop");
        Directory.CreateDirectory(appDir);
        var settingsPath = Path.Combine(appDir, "settings.json");
        File.WriteAllText(settingsPath, "{ INVALID JSON !!!");

        var service = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);

        Assert.Equal(AppSettings.DefaultGeminiUrl, service.CurrentSettings.GeminiHomepageUrl);
        Assert.Equal(AppThemeMode.Light, service.CurrentSettings.Theme);
        Assert.NotEmpty(Directory.GetFiles(appDir, "settings.json.corrupt-*.bak"));
    }

    [Fact]
    public void SaveWindowSettings_RoundTripsBoundsAndLastSection()
    {
        using var log = new LoggingService(Path.Combine(_tempRoot, "logs"));
        var service = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);

        service.SaveWindowSettings(new WindowSettings
        {
            Left = 210,
            Top = 140,
            Width = 1400,
            Height = 900,
            IsMaximized = true,
            AlwaysOnTop = true,
            LastNavigationSection = NavigationSection.PrivacyAndWindowManagement
        });

        var reloaded = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false).CurrentWindowSettings;
        Assert.Equal(210d, reloaded.Left ?? double.NaN);
        Assert.Equal(140d, reloaded.Top ?? double.NaN);
        Assert.Equal(1400d, reloaded.Width);
        Assert.Equal(900d, reloaded.Height);
        Assert.True(reloaded.IsMaximized);
        Assert.True(reloaded.AlwaysOnTop);
        Assert.Equal(NavigationSection.PrivacyAndWindowManagement, reloaded.LastNavigationSection);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup locks.
        }
    }
}
