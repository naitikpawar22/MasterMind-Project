namespace GeminiDesktop.Models;

/// <summary>
/// Supported application visual themes.
/// </summary>
public enum AppThemeMode
{
    Light = 0,
    Dark = 1,
    SystemDefault = 2
}

/// <summary>
/// Initial section displayed when the application starts.
/// </summary>
public enum LaunchDestination
{
    Home = 0,
    GeminiWorkspace = 1,
    LastVisited = 2
}

/// <summary>
/// Preset default window dimensions available in Settings.
/// </summary>
public enum DefaultWindowSizePreset
{
    Compact = 0,
    Standard = 1,
    Wide = 2,
    Custom = 3
}

/// <summary>
/// Persistent user preferences for MyLaptop.
/// Does not store credentials, cookies, or web conversation data.
/// </summary>
public sealed class AppSettings
{
    public const string DefaultGeminiUrl = "https://gemini.google.com/";

    public LaunchDestination LaunchBehavior { get; set; } = LaunchDestination.Home;

    public bool LaunchOnWindowsStartup { get; set; } = false;

    public bool StartMinimized { get; set; } = false;

    public bool ShowMiniPopupWhenMinimized { get; set; } = true;

    public bool RememberLastWindowPosition { get; set; } = true;

    public DefaultWindowSizePreset WindowSizePreset { get; set; } = DefaultWindowSizePreset.Standard;

    public double DefaultWindowWidth { get; set; } = 1280;

    public double DefaultWindowHeight { get; set; } = 820;

    public AppThemeMode Theme { get; set; } = AppThemeMode.Light;

    public string GeminiHomepageUrl { get; set; } = DefaultGeminiUrl;

    public bool OpenExternalLinksInSystemBrowser { get; set; } = true;

    public bool EnableScreenCaptureExclusionByDefault { get; set; } = true;

    public double WindowOpacity { get; set; } = 1.0;

    public string PreferredMicrophoneId { get; set; } = string.Empty;

    public string PreferredSystemAudioDeviceId { get; set; } = string.Empty;

    public bool AllowMicrophoneAccess { get; set; } = true;

    public bool EnableMicrophoneLevelMonitoring { get; set; } = true;

    public bool EnableSystemAudioMonitoring { get; set; } = true;

    public bool EnableLiveSpeechTranscription { get; set; } = true;

    public bool IsAudioPanelVisible { get; set; } = true;

    public AppSettings Clone() => new()
    {
        LaunchBehavior = LaunchBehavior,
        LaunchOnWindowsStartup = LaunchOnWindowsStartup,
        StartMinimized = StartMinimized,
        ShowMiniPopupWhenMinimized = ShowMiniPopupWhenMinimized,
        RememberLastWindowPosition = RememberLastWindowPosition,
        WindowSizePreset = WindowSizePreset,
        DefaultWindowWidth = DefaultWindowWidth,
        DefaultWindowHeight = DefaultWindowHeight,
        Theme = Theme,
        GeminiHomepageUrl = GeminiHomepageUrl,
        OpenExternalLinksInSystemBrowser = OpenExternalLinksInSystemBrowser,
        EnableScreenCaptureExclusionByDefault = EnableScreenCaptureExclusionByDefault,
        WindowOpacity = ClampOpacity(WindowOpacity),
        PreferredMicrophoneId = PreferredMicrophoneId,
        PreferredSystemAudioDeviceId = PreferredSystemAudioDeviceId,
        AllowMicrophoneAccess = AllowMicrophoneAccess,
        EnableMicrophoneLevelMonitoring = EnableMicrophoneLevelMonitoring,
        EnableSystemAudioMonitoring = EnableSystemAudioMonitoring,
        EnableLiveSpeechTranscription = EnableLiveSpeechTranscription,
        IsAudioPanelVisible = IsAudioPanelVisible
    };

    public static double ClampOpacity(double opacity) =>
        double.IsNaN(opacity) || double.IsInfinity(opacity)
            ? 1.0
            : Math.Clamp(opacity, 0.30, 1.0);

    public static (double Width, double Height) GetPresetDimensions(DefaultWindowSizePreset preset, double customWidth, double customHeight)
    {
        return preset switch
        {
            DefaultWindowSizePreset.Compact => (1040, 700),
            DefaultWindowSizePreset.Standard => (1280, 820),
            DefaultWindowSizePreset.Wide => (1480, 920),
            DefaultWindowSizePreset.Custom => (Math.Clamp(customWidth, 900, 3840), Math.Clamp(customHeight, 620, 2160)),
            _ => (1280, 820)
        };
    }
}
