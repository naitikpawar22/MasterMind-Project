using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Input;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using GeminiDesktop.Services;

namespace GeminiDesktop.ViewModels;

/// <summary>
/// ViewModel for the About view presenting application metadata, runtime diagnostics, and ethical/security notices.
/// </summary>
public sealed class AboutViewModel : ViewModelBase
{
    private readonly ILoggingService _loggingService;
    private readonly IGeminiBrowserService _browserService;

    public AboutViewModel(ILoggingService loggingService, IGeminiBrowserService browserService)
    {
        _loggingService = loggingService;
        _browserService = browserService;

        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);
        OpenOfficialGeminiInBrowserCommand = new RelayCommand(OpenOfficialGeminiInBrowser);
    }

    public ICommand OpenLogsFolderCommand { get; }
    public ICommand OpenOfficialGeminiInBrowserCommand { get; }

    public string ApplicationName => ConfigurationHelper.ApplicationName;

    public string Version =>
        typeof(AboutViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "1.0.0";

    public string DeveloperInfo => "MyLaptop Contributors";

    public string CopyrightInfo => "Copyright (c) 2026 MyLaptop Contributors. All rights reserved.";

    public string DotNetRuntimeDescription => RuntimeInformation.FrameworkDescription;

    public string OsDescription => WindowHelper.GetWindowsVersion().DisplayName;

    public string WebView2RuntimeVersion =>
        _browserService.State.WebView2RuntimeVersion ?? "Not detected";

    public string UserDataDirectory => ConfigurationHelper.GetWebView2UserDataDirectory();

    public string LogsDirectory => _loggingService.LogsDirectory;

    public string OfficialGeminiUrl => AppSettings.DefaultGeminiUrl;

    private void OpenLogsFolder()
    {
        ConfigurationHelper.EnsureDirectoryExists(LogsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogsDirectory}\"") { UseShellExecute = true });
    }

    private static void OpenOfficialGeminiInBrowser()
    {
        if (Uri.TryCreate(AppSettings.DefaultGeminiUrl, UriKind.Absolute, out var uri) &&
            ConfigurationHelper.IsSafeExternalLaunchUri(uri))
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }
}
