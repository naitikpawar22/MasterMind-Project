# MasterMind-Project (MyLaptop)

A Windows 10 & Windows 11 desktop workspace built with **C#**, **.NET 10**, **WPF**, **MVVM**, **Microsoft Edge WebView2** (`WebView2CompositionControl`), **NAudio.Wasapi**, **System.Speech**, **Microsoft.Extensions.DependencyInjection**, and **Serilog**.

## Key Features

- **Integrated Web Workspace**: Hosts `https://gemini.google.com/` inside Microsoft Edge WebView2 with persistent isolated session storage (`%LocalAppData%\MyLaptop\WebView2UserData`), real Google Account authentication, Back/Forward/Reload/Home navigation, progress indicators, offline detection, and actionable recovery views.
- **Simultaneous Dual Audio Listening (Microphone + System Audio)**:
  - **Microphone Audio (`DataFlow.Capture`)**: Captures voice strictly from the user's physical microphone (built-in or external), with preferred device selection (`System Default` or specific mic), separate **Start / Stop / Mute** controls, Windows Privacy consent checks, and graceful disconnect/hot-plug fallback.
  - **System Audio (`DataFlow.Render` Multi-Output Loopback)**: Captures Windows playback directly from active Windows output endpoints — including **Windows Call Default (`Role.Communications`)**, **Windows Playback Default (`Role.Multimedia`)**, Bluetooth headsets (A2DP & Hands-Free HFP), headphones, and speakers — simultaneously using polling WASAPI loopback (`WasapiRecorder.WithLoopbackCapture()`), with selectable playback output (`Auto`, `Call Default`, `Playback Default`, or specific endpoint), separate **Start / Stop / Mute** controls (independent of Microphone mute/stop), debounced hot-plug switching, anti-aliased 16 kHz conversion, and gentle speech gain normalization (`AdaptiveSpeechGainProcessor`).
  - **Real-Time Waveform, Activity Indicators & Live Speech-to-Text**: Displays a live **20-bar System Audio waveform indicator** alongside `0%–100%` meters for both streams and runs two concurrent offline `System.Speech` SAPI recognition streams (`SpeechRecognitionEngine`) converting participant and microphone speech into timestamped, color-coded transcript entries clearly labeled `[Microphone]` vs `[System Audio]`.
- **Window Transparency (Background Visibility)**: Real-time opacity slider (`30%` to `100%` opaque, showing `0%–70%` desktop background) powered by WPF `AllowsTransparency` + `WebView2CompositionControl`.
- **Collapsible Navigation Sidebar, Dedicated `Audio & Transcript` Tab & Minimizable Drawers**: Collapse the left navigation bar (`Ctrl+B`), open `Audio & Transcript` directly from the sidebar (`Alt+6`), minimize the Workspace right drawer to a compact `32px` live indicator strip, and toggle the browser bar (`F11`) for edge-to-edge space.
- **Compact Restore Popup on Minimize**: Minimizing opens a compact `MyLaptop Mini` card near the bottom-right corner with quick `Workspace`, `Open`, and dismiss buttons.
- **Password-Protected Privacy Mode (`WDA_EXCLUDEFROMCAPTURE`)**: Enabled by default on supported Windows 10 (2004+) / Windows 11; toggling Privacy Mode **OFF → ON** requires no password, while switching **ON → OFF** requires password `12345`.
- **Security & Compliance**: Never captures passwords, never scrapes web conversations, strips URL query/fragment secrets from Serilog logs, validates all navigation schemes, and routes external HTTPS links safely to the system default browser.

## Dependencies

All NuGet dependencies are declared in [`GeminiDesktop/GeminiDesktop.csproj`](GeminiDesktop/GeminiDesktop.csproj) and [`tests/GeminiDesktop.Tests/GeminiDesktop.Tests.csproj`](tests/GeminiDesktop.Tests/GeminiDesktop.Tests.csproj) and are installed automatically via `dotnet restore`:

| Package | Version | Purpose |
| --- | --- | --- |
| `Microsoft.Extensions.DependencyInjection` | `10.0.0` | Service container and MVVM ViewModel wiring |
| `Microsoft.Web.WebView2` | `1.0.4370.26` | Embedded Chromium workspace (`WebView2CompositionControl`) |
| `NAudio.Wasapi` | `3.3.0` | WASAPI physical microphone (`DataFlow.Capture`), Windows output loopback (`WithLoopbackCapture`), volume/mute sync, and device hot-plug notifications |
| `System.Speech` | `10.0.15` | Real-time offline Windows Desktop SAPI speech-to-text across simultaneous audio streams |
| `Serilog` | `4.4.0` | Structured application diagnostics logging |
| `Serilog.Sinks.Debug` | `3.0.0` | Debug output logging sink |
| `Serilog.Sinks.File` | `7.0.0` | Rolling log files in `%LocalAppData%\MyLaptop\Logs` |
| `Microsoft.NET.Test.Sdk` / `xunit` / `xunit.runner.visualstudio` | `18.12.0` / `2.9.3` / `4.0.1` | Automated unit and speech integration test suite |

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
│   │   ├── AudioProcessingHelper.cs
│   │   ├── ConfigurationHelper.cs
│   │   ├── Converters.cs
│   │   ├── RelayCommand.cs
│   │   └── WindowHelper.cs
│   ├── Models/
│   │   ├── AppSettings.cs
│   │   ├── AudioTranscriptModels.cs
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
│   │   ├── DualAudioMixerService.cs
│   │   ├── GeminiBrowserService.cs
│   │   ├── LoggingService.cs
│   │   ├── MeetingRecorderService.cs
│   │   ├── MicrophoneService.cs
│   │   ├── PrivacyService.cs
│   │   ├── SettingsService.cs
│   │   ├── SpeechTranscriptionService.cs
│   │   ├── SystemAudioService.cs
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
│       ├── AudioMonitorView.xaml / .cs
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
        ├── DualAudioAndTranscriptionTests.cs
        ├── GeminiBrowserServiceTests.cs
        ├── MicrophoneServiceTests.cs
        ├── PrivacyServiceTests.cs
        ├── SettingsServiceTests.cs
        ├── ViewModelTests.cs
        └── WindowHelperTests.cs
```

## Quick Start

See [BUILDING.md](BUILDING.md) to install prerequisites, restore dependencies, run tests, and compile `MyLaptop.exe`, and [INSTALL.md](INSTALL.md) for Windows installer setup.
