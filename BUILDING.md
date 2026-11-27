# Building MyLaptop (MasterMind-Project)

## 1. Prerequisites & Tooling Installation

- **OS**: Windows 10 (Version 1809+; Version 2004 / Build 19041+ for `WDA_EXCLUDEFROMCAPTURE`) or Windows 11 (x64)
- **.NET SDK** (.NET 10 SDK or newer with `net10.0-windows` support):
  ```powershell
  winget install --id Microsoft.DotNet.SDK.10 --exact
  ```
- **Microsoft Edge WebView2 Evergreen Runtime** (preinstalled on Windows 11 and most Windows 10 systems):
  ```powershell
  winget install --id Microsoft.EdgeWebView2Runtime --exact
  ```
- **Inno Setup 6** (only required if compiling `MyLaptop-Setup-3.0.0.exe`):
  ```powershell
  winget install --id JRSoftware.InnoSetup --exact
  ```
- **Windows Audio & Speech**:
  - Enable desktop microphone access under **Windows Settings → Privacy & security → Microphone**.
  - Ensure a Windows Speech desktop recognition language (e.g., English - US / UK) is installed under **Windows Settings → Time & language → Speech** for real-time transcription.

## 2. Installing NuGet Dependencies

All NuGet packages (`Microsoft.Extensions.DependencyInjection` `10.0.0`, `Microsoft.Web.WebView2` `1.0.4370.26`, `NAudio.Wasapi` `3.3.0`, `System.Speech` `10.0.15`, `Serilog` `4.4.0`, `Serilog.Sinks.Debug` `3.0.0`, `Serilog.Sinks.File` `7.0.0`, `Microsoft.NET.Test.Sdk` `18.12.0`, `xunit` `2.9.3`, `xunit.runner.visualstudio` `4.0.1`) are restored automatically from NuGet (`https://api.nuget.org/v3/index.json`):

```powershell
dotnet restore GeminiDesktop.sln
```

## 3. Build, Run & Test Commands

Build the Release configuration:

```powershell
dotnet build GeminiDesktop.sln -c Release
```

Run the desktop application directly from source:

```powershell
dotnet run --project GeminiDesktop\GeminiDesktop.csproj -c Release
```

Run the automated xUnit & SAPI speech integration test suite:

```powershell
dotnet test GeminiDesktop.sln -c Release --verbosity normal
```

## 4. Build Self-Contained Release Executable & Windows Installer

Run the automated packaging script:

```powershell
powershell -ExecutionPolicy Bypass -File Installer\build-installer.ps1
```

Outputs generated under `dist\` (excluded from git by `.gitignore`):

- **Release Executable**: `dist\publish-win-x64\MyLaptop.exe`
- **Windows Installer**: `dist\MyLaptop-Setup-3.0.0.exe`
