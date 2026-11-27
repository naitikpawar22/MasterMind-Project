using System.IO;
using System.Windows;
using System.Windows.Interop;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using GeminiDesktop.Services;
using GeminiDesktop.ViewModels;
using Xunit;

namespace GeminiDesktop.Tests;

public sealed class ViewModelTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "MyLaptopVMTests_" + Guid.NewGuid().ToString("N"));

    private sealed class StubDialogService(bool confirmResult, string? passwordResult = null) : IDialogService
    {
        public int ConfirmCalls { get; private set; }
        public int PasswordPromptCalls { get; private set; }
        public int ErrorCalls { get; private set; }
        public string? NextPassword { get; set; } = passwordResult;

        public bool Confirm(string title, string message, bool isDestructive = false)
        {
            ConfirmCalls++;
            return confirmResult;
        }

        public void ShowError(string title, string message) => ErrorCalls++;

        public string? PromptPassword(string title, string message)
        {
            PasswordPromptCalls++;
            return NextPassword;
        }
    }

    private sealed class StubThemeService : IThemeService
    {
        public AppThemeMode ActiveThemeMode { get; private set; } = AppThemeMode.Light;
        public bool IsDarkThemeActive => ActiveThemeMode == AppThemeMode.Dark;
        public event EventHandler<bool>? EffectiveThemeChanged;

        public void ApplyTheme(AppThemeMode mode)
        {
            ActiveThemeMode = mode;
            EffectiveThemeChanged?.Invoke(this, IsDarkThemeActive);
        }

        public void ApplyWindowChromeTheme(Window window) { }
        public void Dispose() { }
    }

    private sealed class StubAffinityAdapter : IDisplayAffinityAdapter
    {
        public WindowDisplayAffinityMode StoredAffinity { get; set; } = WindowDisplayAffinityMode.None;
        public WindowsVersionDetails GetWindowsVersion() => new(10, 0, 26300, "Windows 11 (Build 26300)");
        public bool TrySetDisplayAffinity(IntPtr hwnd, WindowDisplayAffinityMode mode, out int win32Error)
        {
            StoredAffinity = mode;
            win32Error = 0;
            return true;
        }
        public bool TryGetDisplayAffinity(IntPtr hwnd, out WindowDisplayAffinityMode mode, out int win32Error)
        {
            mode = StoredAffinity;
            win32Error = 0;
            return true;
        }
        public void SetDisallowPeek(IntPtr hwnd, bool disallowPeek) { }
        public void ApplyAffinityToOwnedPopups(WindowDisplayAffinityMode mode, IntPtr primaryHwnd) { }
    }

    private sealed class StubMicrophoneBackend : IMicrophoneBackend
    {
        public event EventHandler<double>? AudioLevelMeasured;
        public event EventHandler<Exception?>? CaptureStoppedUnexpectedly;
        public event EventHandler? DevicesTopologyChanged;
        public event EventHandler<bool>? ExternalMuteChanged;

        public MicrophonePermissionState CheckWindowsPermission() => MicrophonePermissionState.Allowed;
        public IReadOnlyList<MicrophoneDevice> EnumerateCaptureDevices() =>
            [new MicrophoneDevice("mic-1", "Built-in Microphone", IsDefault: true)];
        public string? ResolveDefaultCaptureDeviceId() => "mic-1";
        public bool TryGetEndpointMute(string deviceId, out bool isMuted) { isMuted = false; return true; }
        public bool TrySetEndpointMute(string deviceId, bool muted) => true;
        public void StartCapture(string deviceId) { }
        public void StopCapture() { }
        public void Dispose()
        {
            _ = AudioLevelMeasured;
            _ = CaptureStoppedUnexpectedly;
            _ = DevicesTopologyChanged;
            _ = ExternalMuteChanged;
        }
    }

    [Fact]
    public void SettingsViewModel_RejectsInvalidUrlAndRespectsConfirmationOnReset()
    {
        using var log = new LoggingService(_tempRoot);
        var settingsService = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);
        var themeService = new StubThemeService();
        using var windowService = new WindowManagementService(settingsService, log);
        var privacyService = new PrivacyService(new StubAffinityAdapter(), log);
        using var micService = new MicrophoneService(new StubMicrophoneBackend(), settingsService, log);
        micService.Initialize();
        using var webViewService = new WebViewService(log, settingsService, micService);
        using var browserService = new GeminiBrowserService(webViewService, settingsService, log);
        var dialogService = new StubDialogService(confirmResult: false);

        var vm = new SettingsViewModel(settingsService, themeService, windowService, browserService, privacyService, micService, dialogService);

        vm.GeminiHomepageUrl = "http://insecure.example.com";
        vm.SaveSettingsCommand.Execute(null);

        Assert.False(string.IsNullOrWhiteSpace(vm.UrlValidationError));
        Assert.Equal(AppSettings.DefaultGeminiUrl, settingsService.CurrentSettings.GeminiHomepageUrl);

        vm.ResetToDefaultsCommand.Execute(null);
        Assert.Equal(1, dialogService.ConfirmCalls);
    }

    [Fact]
    public void MainViewModel_NavigatesBetweenAllFiveSections()
    {
        using var log = new LoggingService(_tempRoot);
        var settingsService = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);
        var themeService = new StubThemeService();
        using var windowService = new WindowManagementService(settingsService, log);
        var privacyService = new PrivacyService(new StubAffinityAdapter(), log);
        using var micService = new MicrophoneService(new StubMicrophoneBackend(), settingsService, log);
        micService.Initialize();
        using var webViewService = new WebViewService(log, settingsService, micService);
        using var browserService = new GeminiBrowserService(webViewService, settingsService, log);
        var dialogService = new StubDialogService(confirmResult: true);

        var mainVm = new MainViewModel(
            settingsService,
            windowService,
            privacyService,
            browserService,
            log,
            new HomeViewModel(browserService, privacyService, settingsService),
            new GeminiViewModel(browserService, micService),
            new SettingsViewModel(settingsService, themeService, windowService, browserService, privacyService, micService, dialogService),
            new PrivacyViewModel(privacyService, windowService, dialogService),
            new AboutViewModel(log, browserService));

        mainVm.NavigateCommand.Execute(NavigationSection.GeminiWorkspace);
        Assert.True(mainVm.IsGeminiWorkspaceActive);

        mainVm.NavigateCommand.Execute(NavigationSection.Settings);
        Assert.True(mainVm.IsSettingsActive);

        mainVm.NavigateCommand.Execute(NavigationSection.PrivacyAndWindowManagement);
        Assert.True(mainVm.IsPrivacyActive);

        mainVm.NavigateCommand.Execute(NavigationSection.About);
        Assert.True(mainVm.IsAboutActive);

        mainVm.NavigateCommand.Execute(NavigationSection.Home);
        Assert.True(mainVm.IsHomeActive);
    }

    [Fact]
    public void PrivacyMode_UpdatesMainIndicatorAndOpacityControls()
    {
        using var log = new LoggingService(_tempRoot);
        var settingsService = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);
        var themeService = new StubThemeService();
        using var windowService = new WindowManagementService(settingsService, log);
        var privacyService = new PrivacyService(new StubAffinityAdapter(), log);
        using var micService = new MicrophoneService(new StubMicrophoneBackend(), settingsService, log);
        micService.Initialize();
        using var webViewService = new WebViewService(log, settingsService, micService);
        using var browserService = new GeminiBrowserService(webViewService, settingsService, log);
        var dialogService = new StubDialogService(confirmResult: true);

        var settingsVm = new SettingsViewModel(settingsService, themeService, windowService, browserService, privacyService, micService, dialogService);
        var mainVm = new MainViewModel(
            settingsService,
            windowService,
            privacyService,
            browserService,
            log,
            new HomeViewModel(browserService, privacyService, settingsService),
            new GeminiViewModel(browserService, micService),
            settingsVm,
            new PrivacyViewModel(privacyService, windowService, dialogService),
            new AboutViewModel(log, browserService));

        Assert.Equal("Privacy Mode: Off", mainVm.PrivacyIndicatorText);
        Assert.False(mainVm.IsPrivacyIndicatorActive);

        mainVm.WindowOpacity = 0.70;
        Assert.Equal(70, mainVm.WindowOpacityPercent);
        Assert.Equal(30, mainVm.BackgroundVisibilityPercent);
        Assert.Equal("Opacity 70% (30% bg)", mainVm.OpacityBadgeText);

        mainVm.SetOpacityPresetCommand.Execute("10"); // Should clamp to minimum 30%
        Assert.Equal(30, mainVm.WindowOpacityPercent);
        Assert.Equal(70, mainVm.BackgroundVisibilityPercent);

        windowService.SetAlwaysOnTop(true);
        Assert.Contains("Always on Top", mainVm.PrivacyIndicatorText);
        Assert.True(mainVm.IsPrivacyIndicatorActive);

        windowService.SetAlwaysOnTop(false);
        Assert.Equal("Privacy Mode: Off", mainVm.PrivacyIndicatorText);
        Assert.False(settingsVm.IsPrivacyModeEnabled);
    }

    [Fact]
    public void PrivacyModePasswordLock_Requires12345OnlyOnToOff()
    {
        var result = RunOnStaThread(() =>
        {
            using var log = new LoggingService(_tempRoot);
            var settingsService = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);
            using var windowService = new WindowManagementService(settingsService, log);
            var adapter = new StubAffinityAdapter();
            var privacyService = new PrivacyService(adapter, log);
            var dialogService = new StubDialogService(confirmResult: true);

            var window = new Window { Width = 100, Height = 100, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            new WindowInteropHelper(window).EnsureHandle();
            privacyService.AttachWindow(window);

            var privacyVm = new PrivacyViewModel(privacyService, windowService, dialogService);

            privacyVm.IsCaptureExclusionEnabled = true;
            var promptsOnEnable = dialogService.PasswordPromptCalls;
            var activeAfterEnable = privacyVm.IsCaptureExclusionEnabled;

            dialogService.NextPassword = null;
            privacyVm.IsCaptureExclusionEnabled = false;
            var activeAfterCancel = privacyVm.IsCaptureExclusionEnabled;

            dialogService.NextPassword = "99999";
            privacyVm.IsCaptureExclusionEnabled = false;
            var activeAfterWrong = privacyVm.IsCaptureExclusionEnabled;
            var errorsAfterWrong = dialogService.ErrorCalls;

            dialogService.NextPassword = "12345";
            privacyVm.IsCaptureExclusionEnabled = false;
            var activeAfterCorrect = privacyVm.IsCaptureExclusionEnabled;
            var totalPrompts = dialogService.PasswordPromptCalls;

            window.Close();
            return (promptsOnEnable, activeAfterEnable, activeAfterCancel, activeAfterWrong, errorsAfterWrong, activeAfterCorrect, totalPrompts);
        });

        Assert.Equal(0, result.promptsOnEnable);
        Assert.True(result.activeAfterEnable);
        Assert.True(result.activeAfterCancel);
        Assert.True(result.activeAfterWrong);
        Assert.Equal(1, result.errorsAfterWrong);
        Assert.False(result.activeAfterCorrect);
        Assert.Equal(3, result.totalPrompts);
    }

    [Fact]
    public void SidebarAndEdgeToEdgeToolbar_ToggleAndPersistState()
    {
        using var log = new LoggingService(_tempRoot);
        var settingsService = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);
        var themeService = new StubThemeService();
        using var windowService = new WindowManagementService(settingsService, log);
        var privacyService = new PrivacyService(new StubAffinityAdapter(), log);
        using var micService = new MicrophoneService(new StubMicrophoneBackend(), settingsService, log);
        micService.Initialize();
        using var webViewService = new WebViewService(log, settingsService, micService);
        using var browserService = new GeminiBrowserService(webViewService, settingsService, log);
        var dialogService = new StubDialogService(confirmResult: true);
        var geminiVm = new GeminiViewModel(browserService, micService);

        var mainVm = new MainViewModel(
            settingsService,
            windowService,
            privacyService,
            browserService,
            log,
            new HomeViewModel(browserService, privacyService, settingsService),
            geminiVm,
            new SettingsViewModel(settingsService, themeService, windowService, browserService, privacyService, micService, dialogService),
            new PrivacyViewModel(privacyService, windowService, dialogService),
            new AboutViewModel(log, browserService));

        Assert.True(mainVm.IsSidebarVisible);
        Assert.True(geminiVm.IsToolbarVisible);

        mainVm.ToggleSidebarCommand.Execute(null);
        Assert.False(mainVm.IsSidebarVisible);
        Assert.False(settingsService.CurrentWindowSettings.IsSidebarExpanded);

        mainVm.ToggleSidebarCommand.Execute(null);
        Assert.True(mainVm.IsSidebarVisible);
        Assert.True(settingsService.CurrentWindowSettings.IsSidebarExpanded);

        geminiVm.ToggleToolbarCommand.Execute(null);
        Assert.False(geminiVm.IsToolbarVisible);

        var aboutVm = new AboutViewModel(log, browserService);
        Assert.Equal("3.0.0", aboutVm.Version);

        var miniVm = new MiniPopupViewModel(privacyService);
        bool? restoredGeminiFlag = null;
        var dismissed = false;
        miniVm.RestoreRequested += (_, openGemini) => restoredGeminiFlag = openGemini;
        miniVm.DismissRequested += (_, _) => dismissed = true;

        miniVm.OpenGeminiCommand.Execute(null);
        Assert.True(restoredGeminiFlag);
        miniVm.RestoreCommand.Execute(null);
        Assert.False(restoredGeminiFlag);
        miniVm.DismissCommand.Execute(null);
        Assert.True(dismissed);
    }

    private static T RunOnStaThread<T>(Func<T> action)
    {
        T? result = default;
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured is not null) throw captured;
        return result!;
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
            // Ignore cleanup errors.
        }
    }
}
