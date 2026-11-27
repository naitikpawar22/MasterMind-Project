using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using Microsoft.Win32;

namespace GeminiDesktop.Services;

/// <summary>
/// Persists user preferences, window bounds, and optional Windows per-user startup registration.
/// </summary>
public interface ISettingsService
{
    AppSettings CurrentSettings { get; }
    WindowSettings CurrentWindowSettings { get; }
    string AppDataDirectory { get; }

    event EventHandler<AppSettings>? SettingsChanged;

    AppSettings LoadSettings();
    void SaveSettings(AppSettings settings);
    AppSettings ResetSettingsToDefaults();

    WindowSettings LoadWindowSettings();
    void SaveWindowSettings(WindowSettings windowSettings);

    bool IsWindowsStartupRegistered();
    bool TrySetWindowsStartup(bool enabled, out string? errorMessage);
}

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILoggingService _loggingService;
    private readonly string? _appDataRootOverride;
    private readonly bool _allowRegistryStartupChanges;

    public SettingsService(
        ILoggingService loggingService,
        string? appDataRootOverride = null,
        bool allowRegistryStartupChanges = true)
    {
        _loggingService = loggingService;
        _appDataRootOverride = appDataRootOverride;
        _allowRegistryStartupChanges = allowRegistryStartupChanges;

        AppDataDirectory = ConfigurationHelper.GetAppDataDirectory(_appDataRootOverride);
        ConfigurationHelper.EnsureDirectoryExists(AppDataDirectory);
        MigrateLegacySettingsIfNeeded();

        CurrentSettings = LoadSettings();
        CurrentWindowSettings = LoadWindowSettings();
    }

    private void MigrateLegacySettingsIfNeeded()
    {
        if (!string.IsNullOrWhiteSpace(_appDataRootOverride))
        {
            return;
        }

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var legacyDirs = new[]
            {
                Path.Combine(localAppData, "MasterMind"),
                Path.Combine(localAppData, "GeminiDesktop")
            };

            var currentSettingsPath = ConfigurationHelper.GetSettingsFilePath();
            var currentWinPath = ConfigurationHelper.GetWindowSettingsFilePath();

            foreach (var legacyDir in legacyDirs)
            {
                if (!Directory.Exists(legacyDir))
                {
                    continue;
                }

                var legacySettingsPath = Path.Combine(legacyDir, "settings.json");
                if (!File.Exists(currentSettingsPath) && File.Exists(legacySettingsPath))
                {
                    File.Copy(legacySettingsPath, currentSettingsPath, overwrite: false);
                    _loggingService.Information("Migrated legacy settings from {Dir} to MyLaptop AppData.", legacyDir);
                }

                var legacyWinPath = Path.Combine(legacyDir, "window-settings.json");
                if (!File.Exists(currentWinPath) && File.Exists(legacyWinPath))
                {
                    File.Copy(legacyWinPath, currentWinPath, overwrite: false);
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.Warning("Legacy settings migration skipped: {Message}", ex.Message);
        }
    }

    public AppSettings CurrentSettings { get; private set; }

    public WindowSettings CurrentWindowSettings { get; private set; }

    public string AppDataDirectory { get; }

    public event EventHandler<AppSettings>? SettingsChanged;

    public AppSettings LoadSettings()
    {
        var path = ConfigurationHelper.GetSettingsFilePath(_appDataRootOverride);
        var loaded = ReadJsonWithRecovery(path, () => new AppSettings());
        SanitizeAppSettings(loaded);
        CurrentSettings = loaded;
        return CurrentSettings.Clone();
    }

    public void SaveSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var sanitized = settings.Clone();
        SanitizeAppSettings(sanitized);

        var path = ConfigurationHelper.GetSettingsFilePath(_appDataRootOverride);
        WriteJsonAtomically(path, sanitized);

        if (_allowRegistryStartupChanges)
        {
            if (!TrySetWindowsStartup(sanitized.LaunchOnWindowsStartup, out var regError) && !string.IsNullOrEmpty(regError))
            {
                _loggingService.Warning("Failed to update Windows startup preference: {Reason}", regError);
            }
        }

        CurrentSettings = sanitized;
        _loggingService.Information("Application settings saved. Theme={Theme}, LaunchBehavior={LaunchBehavior}",
            sanitized.Theme, sanitized.LaunchBehavior);

        SettingsChanged?.Invoke(this, sanitized.Clone());
    }

    public AppSettings ResetSettingsToDefaults()
    {
        var defaults = new AppSettings();
        SaveSettings(defaults);
        return defaults;
    }

    public WindowSettings LoadWindowSettings()
    {
        var path = ConfigurationHelper.GetWindowSettingsFilePath(_appDataRootOverride);
        var loaded = ReadJsonWithRecovery(path, () => new WindowSettings());

        if (double.IsNaN(loaded.Width) || loaded.Width < WindowHelper.DefaultMinWidth)
        {
            loaded.Width = 1280;
        }

        if (double.IsNaN(loaded.Height) || loaded.Height < WindowHelper.DefaultMinHeight)
        {
            loaded.Height = 820;
        }

        if (!Enum.IsDefined(typeof(NavigationSection), loaded.LastNavigationSection))
        {
            loaded.LastNavigationSection = NavigationSection.Home;
        }

        CurrentWindowSettings = loaded;
        return CurrentWindowSettings.Clone();
    }

    public void SaveWindowSettings(WindowSettings windowSettings)
    {
        ArgumentNullException.ThrowIfNull(windowSettings);

        var snapshot = windowSettings.Clone();
        var path = ConfigurationHelper.GetWindowSettingsFilePath(_appDataRootOverride);
        WriteJsonAtomically(path, snapshot);
        CurrentWindowSettings = snapshot;
    }

    public bool IsWindowsStartupRegistered()
    {
        if (!_allowRegistryStartupChanges)
        {
            return CurrentSettings.LaunchOnWindowsStartup;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ConfigurationHelper.StartupRegistryRunKeyPath, writable: false);
            var existing = key?.GetValue(ConfigurationHelper.StartupRegistryValueName) as string;
            return !string.IsNullOrWhiteSpace(existing);
        }
        catch (Exception ex)
        {
            _loggingService.Warning("Unable to read Windows startup registry key: {Error}", ex.Message);
            return false;
        }
    }

    public bool TrySetWindowsStartup(bool enabled, out string? errorMessage)
    {
        errorMessage = null;
        if (!_allowRegistryStartupChanges)
        {
            return true;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ConfigurationHelper.StartupRegistryRunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(ConfigurationHelper.StartupRegistryRunKeyPath, writable: true);

            if (key is null)
            {
                errorMessage = "Could not open Windows user Run registry key.";
                return false;
            }

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                {
                    errorMessage = "Executable path could not be resolved for startup registration.";
                    return false;
                }

                key.SetValue(ConfigurationHelper.StartupRegistryValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ConfigurationHelper.StartupRegistryValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            _loggingService.Error(ex, "Error configuring Windows startup registry entry.");
            return false;
        }
    }

    private T ReadJsonWithRecovery<T>(string filePath, Func<T> createDefault) where T : class
    {
        if (!File.Exists(filePath))
        {
            var initial = createDefault();
            WriteJsonAtomically(filePath, initial);
            return initial;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException("Configuration file is empty.");
            }

            var parsed = JsonSerializer.Deserialize<T>(json, SerializerOptions);
            if (parsed is null)
            {
                throw new InvalidDataException("Configuration deserialized to null.");
            }

            return parsed;
        }
        catch (Exception ex)
        {
            _loggingService.Warning("Invalid configuration detected at {FilePath}: {Message}. Recovering with defaults.",
                filePath, ex.Message);

            try
            {
                var backupPath = $"{filePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
                File.Copy(filePath, backupPath, overwrite: true);
            }
            catch
            {
                // Ignore backup failures during recovery.
            }

            var fallback = createDefault();
            WriteJsonAtomically(filePath, fallback);
            return fallback;
        }
    }

    private void WriteJsonAtomically<T>(string filePath, T value)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            ConfigurationHelper.EnsureDirectoryExists(directory);
        }

        var tempPath = filePath + ".tmp";
        var json = JsonSerializer.Serialize(value, SerializerOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, filePath, overwrite: true);
    }

    private void SanitizeAppSettings(AppSettings settings)
    {
        if (!Enum.IsDefined(typeof(AppThemeMode), settings.Theme))
        {
            settings.Theme = AppThemeMode.Light;
        }

        if (!Enum.IsDefined(typeof(LaunchDestination), settings.LaunchBehavior))
        {
            settings.LaunchBehavior = LaunchDestination.Home;
        }

        if (!Enum.IsDefined(typeof(DefaultWindowSizePreset), settings.WindowSizePreset))
        {
            settings.WindowSizePreset = DefaultWindowSizePreset.Standard;
        }

        settings.DefaultWindowWidth = Math.Clamp(
            double.IsNaN(settings.DefaultWindowWidth) ? 1280 : settings.DefaultWindowWidth,
            WindowHelper.DefaultMinWidth,
            3840);

        settings.DefaultWindowHeight = Math.Clamp(
            double.IsNaN(settings.DefaultWindowHeight) ? 820 : settings.DefaultWindowHeight,
            WindowHelper.DefaultMinHeight,
            2160);

        if (ConfigurationHelper.TryValidateGeminiHomepageUrl(settings.GeminiHomepageUrl, out var normalizedUrl, out var err))
        {
            settings.GeminiHomepageUrl = normalizedUrl;
        }
        else
        {
            _loggingService.Warning("Resetting invalid workspace homepage URL in configuration: {Reason}", err);
            settings.GeminiHomepageUrl = AppSettings.DefaultGeminiUrl;
        }

        settings.WindowOpacity = AppSettings.ClampOpacity(settings.WindowOpacity);
    }
}
