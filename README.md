# MasterMind-Project (MyLaptop)

A Windows 10 & Windows 11 desktop application built with **C#**, **.NET 10**, **WPF**, **MVVM**, **Microsoft Edge WebView2** (`WebView2CompositionControl`), **NAudio.Wasapi**, **Microsoft.Extensions.DependencyInjection**, and **Serilog**.

## Key Features

- **Integrated Web Workspace**: Hosts `https://gemini.google.com/` inside Microsoft Edge WebView2 with persistent isolated session storage (`%LocalAppData%\MyLaptop\WebView2UserData`), real Google Account authentication, Back/Forward/Reload/Home navigation, progress indicators, offline detection, and actionable recovery views.
- **Physical Microphone Input, Selection & Live Metering**: Uses `NAudio.Wasapi` (`DataFlow.Capture` / `WasapiRecorder`) strictly on physical user microphones (no system-audio loopback, virtual audio routing, or OS permission bypasses). Includes device selection (`System Default` or explicit microphone), real-time smoothed input level indicator (`0%–100%`), bidirectional hardware mute/unmute sync, Windows Privacy (`ConsentStore\microphone`) and WebView2 permission gating, and automatic hot-plug/disconnect fallback via `MMDeviceNotificationClient`.
- **Window Transparency (Background Visibility)**: Real-time opacity slider (`30%` to `100%` opaque, showing `0%–70%` desktop background) accessible from the title bar, Settings, and Privacy & Window Management, powered by WPF `AllowsTransparency` + `WebView2CompositionControl`.
- **Collapsible Sliding Sidebar & Edge-to-Edge UI**: Slide the left navigation panel hidden or open (`Ctrl+B` or the title bar sidebar button), plus `F11` browser-bar toggle for maximum screen space.
- **Compact Restore Popup on Minimize**: Minimizing opens a compact `MyLaptop Mini` card near the bottom-right corner with quick `Workspace`, `Open`, and dismiss buttons.
- **Password-Protected Privacy Mode (`WDA_EXCLUDEFROMCAPTURE`)**: Enabled by default on supported Windows 10 (2004+) / Windows 11; toggling Privacy Mode **OFF → ON** requires no password, while switching **ON → OFF** requires password `12345`.
- **Full Settings & Themes**: Startup launch behavior, Windows sign-in startup toggle, window size presets, Light / Dark / System Default themes, validated Gemini homepage URL, cache clearing, and workspace reset with confirmation dialogs.
- **Security & Compliance**: Never captures passwords, never scrapes conversations, strips URL query/fragment secrets from Serilog logs, validates all navigation schemes, and routes external HTTPS links safely to the system default browser.

## Dependencies

All NuGet dependencies are declared in [`GeminiDesktop/GeminiDesktop.csproj`](GeminiDesktop/GeminiDesktop.csproj) and [`tests/GeminiDesktop.Tests/GeminiDesktop.Tests.csproj`](tests/GeminiDesktop.Tests/GeminiDesktop.Tests.csproj) and are installed automatically via `dotnet restore`:

| Package | Version | Purpose |
| --- | --- | --- |
| `Microsoft.Extensions.DependencyInjection` | `10.0.0` | Service container and MVVM ViewModel wiring |
| `Microsoft.Web.WebView2` | `1.0.4370.26` | Embedded Chromium workspace (`WebView2CompositionControl`) |
| `NAudio.Wasapi` | `3.3.0` | Physical WASAPI microphone capture (`WasapiRecorder`), endpoint mute, and hot-plug notifications |
| `Serilog` | `4.4.0` | Structured application diagnostics logging |
| `Serilog.Sinks.Debug` | `3.0.0` | Debug output logging sink |
| `Serilog.Sinks.File` | `7.0.0` | Rolling log files in `%LocalAppData%\MyLaptop\Logs` |
| `Microsoft.NET.Test.Sdk` / `xunit` / `xunit.runner.visualstudio` | `18.12.0` / `2.9.3` / `4.0.1` | Automated unit test suite |

## Project Folder Structure

```text
MasterMind/
├── .gitignore
├── GeminiDesktop.sln
├── README.md
├── BUILDING.md
├── INSTALL.md
├── PRIVACY_CAPTURE_TESTING.md
├── Installer/
│   ├── GeminiDesktop.iss
│   ├── Package.appxmanifest
│   └── build-installer.ps1
├── GeminiDesktop/
│   ├── GeminiDesktop.csproj
│   ├── App.xaml / App.xaml.cs
│   ├── app.manifest
│   ├── Assets/
│   │   ├── app-logo.png
│   │   └── app-logo.svg
│   ├── Configuration/
│   │   └── appsettings.json
│   ├── Helpers/
│   │   ├── ConfigurationHelper.cs
│   │   ├── Converters.cs
│   │   ├── RelayCommand.cs
│   │   └── WindowHelper.cs
│   ├── Models/
│   │   ├── AppSettings.cs
│   │   ├── BrowserState.cs
│   │   ├── MicrophoneState.cs
│   │   ├── NavigationSection.cs
│   │   ├── PrivacyStatus.cs
│   │   └── WindowSettings.cs
│   ├── Resources/
│   │   ├── Icons/
│   │   │   └── MyLaptop.ico
│   │   ├── Styles/
│   │   │   ├── Buttons.xaml
│   │   │   ├── Cards.xaml
│   │   │   ├── Colors.xaml
│   │   │   └── Navigation.xaml
│   │   └── Themes/
│   │       ├── DarkTheme.xaml
│   │       └── LightTheme.xaml
│   ├── Services/
│   │   ├── DialogService.cs
│   │   ├── GeminiBrowserService.cs
│   │   ├── LoggingService.cs
│   │   ├── MicrophoneService.cs
│   │   ├── PrivacyService.cs
│   │   ├── SettingsService.cs
│   │   ├── ThemeService.cs
│   │   ├── WebViewService.cs
│   │   └── WindowManagementService.cs
│   ├── ViewModels/
│   │   ├── AboutViewModel.cs
│   │   ├── GeminiViewModel.cs
│   │   ├── HomeViewModel.cs
│   │   ├── MainViewModel.cs
│   │   ├── MiniPopupViewModel.cs
│   │   ├── PrivacyViewModel.cs
│   │   ├── SettingsViewModel.cs
│   │   └── ViewModelBase.cs
│   └── Views/
│       ├── AboutView.xaml / .cs
│       ├── GeminiView.xaml / .cs
│       ├── HomeView.xaml / .cs
│       ├── MainWindow.xaml / .cs
│       ├── MiniPopupWindow.xaml / .cs
│       ├── PasswordPromptWindow.xaml / .cs
│       ├── PrivacyView.xaml / .cs
│       └── SettingsView.xaml / .cs
└── tests/
    └── GeminiDesktop.Tests/
        ├── GeminiDesktop.Tests.csproj
        ├── ConfigurationHelperTests.cs
        ├── GeminiBrowserServiceTests.cs
        ├── MicrophoneServiceTests.cs
        ├── PrivacyServiceTests.cs
        ├── SettingsServiceTests.cs
        ├── ViewModelTests.cs
        └── WindowHelperTests.cs
```

## Quick Start

See [BUILDING.md](BUILDING.md) to install prerequisites, restore dependencies, run tests, and compile `MyLaptop.exe`, and [INSTALL.md](INSTALL.md) for Windows installer setup.
