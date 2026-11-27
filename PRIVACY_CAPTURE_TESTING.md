# Privacy Mode & Screen Capture Testing Report

## Overview

Gemini Desktop implements transparent, user-controlled desktop privacy management in [`PrivacyService.cs`](GeminiDesktop/Services/PrivacyService.cs) and [`WindowHelper.cs`](GeminiDesktop/Helpers/WindowHelper.cs) using documented Win32 Desktop Window Manager (DWM) APIs:

- `SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity)` (`user32.dll`)
- `GetWindowDisplayAffinity(IntPtr hWnd, out uint dwAffinity)` (`user32.dll`)
- `RtlGetVersion` (`ntdll.dll`) for accurate Windows 10/11 OS build capability detection

Supported affinity constants:
- `WDA_NONE (0x00000000)`: Normal desktop visibility; Privacy Mode is disabled and the window appears in screenshots and screen shares.
- `WDA_EXCLUDEFROMCAPTURE (0x00000011)`: Privacy Mode enabled on **Windows 10 Version 2004 (Build 19041)** and **Windows 11**. Compatible Windows capture APIs omit the window surface while the user continues viewing and interacting with the window normally on their physical display.

---

## Verified Test Results (Observed on Test Environment)

### 1. Automated Unit Tests (`GeminiDesktop.Tests`)
- **Result**: **37 / 37 passed** (`dotnet test`, Release configuration).
- Covered scenarios:
  - `PrivacyServiceTests`:
    - Windows 11 (`Build 26300`): enabling Privacy Mode applies `WDA_EXCLUDEFROMCAPTURE (0x11)`, verifies via `GetWindowDisplayAffinity`, updates `PrivacyStatus`, and disabling cleanly restores `WDA_NONE (0x00)`.
    - Unsupported OS (`Windows 10 Build 17763`, `< 19041`): detects capability unavailability, sets `IsExcludeFromCaptureSupported = false`, reports Win32 error `50`, and refuses to falsely claim capture protection.
    - Win32 API failure simulation (`Win32 code 5` / Access Denied, e.g. RDP/virtual display): surfaces clear error text without crashing.
  - `WindowHelperTests`: Multi-monitor work area clamping across primary (`1920x1040`), right secondary (`X=1920`), negative-coordinate left secondary (`X=-1920`), oversized resolution shrinking, and disconnected-monitor recovery.
  - `ViewModelTests`: Synchronized `Privacy Mode: Active` / `Privacy Mode: Off` status badge updates across `MainViewModel`, `SettingsViewModel`, and `PrivacyViewModel`.

### 2. Live Windows 11 Verification (`Windows 11 Build 26300`, Display `\\.\DISPLAY1`, `1536x864` DIPs)
Tested directly against the installed `GeminiDesktop.exe` top-level `MainWindow` HWND:

| Test Case | `GetWindowDisplayAffinity` | `PrintWindow(..., PW_RENDERFULLCONTENT)` | Window Interaction |
| :--- | :--- | :--- | :--- |
| **Privacy Mode Off** | `0x0` (`WDA_NONE`) | Captured **1,632 / 2,418** non-black sampled pixels (`AvgRGB=(16,19,26)`) | Visible & responsive (`1280x820`) |
| **Privacy Mode On** | `0x11` (`WDA_EXCLUDEFROMCAPTURE`) | **0 / 2,418** non-black sampled pixels (`AvgRGB=(0,0,0)`) — fully excluded | Visible & responsive (`1280x820`) |

*(Note: GDI `Graphics.CopyFromScreen` could not acquire a desktop DC in the automated background headless session — `"The handle is invalid"` — and single physical display `\\.\DISPLAY1` was active on the test host; multi-monitor placement was verified through the `WindowHelperTests` suite. Installed Windows capture tools confirmed on host: `Microsoft.ScreenSketch 11.2609.15.0` [Snipping Tool] and `Microsoft.XboxGamingOverlay 7.326.11042.0` [Xbox Game Bar].)*

---

## Capture Method Compatibility Matrix

| Capture Method / Application | Supported by `WDA_EXCLUDEFROMCAPTURE`? | Technical Behavior & Notes |
| :--- | :--- | :--- |
| **Windows Snipping Tool (`Win+Shift+S`, `Microsoft.ScreenSketch`)** | **Supported** (Windows 10 2004+ / Windows 11) | Uses DWM / Windows Graphics Capture (WGC) and Desktop Duplication; DWM omits `WDA_EXCLUDEFROMCAPTURE` windows from captured frames. |
| **Windows `PrtScn` / `Win+PrtScn` / GDI `BitBlt`** | **Supported** (Windows 10 2004+ / Windows 11) | DWM desktop composition excludes `WDA_EXCLUDEFROMCAPTURE` surfaces during screen `BitBlt` / Desktop Duplication. |
| **`PrintWindow` (`PW_RENDERFULLCONTENT`)** | **Supported** (Verified live on Win11 `26300`) | Returns blank/black `(0,0,0)` pixels when Privacy Mode is enabled, and normal rendered UI when disabled. |
| **Xbox Game Bar (`Win+G`, `Microsoft.XboxGamingOverlay`) & Windows Screen Recording** | **Supported** (Desktop / WGC recording) | Relies on Windows Graphics Capture / DXGI Desktop Duplication, which honors `WDA_EXCLUDEFROMCAPTURE`. |
| **OBS Studio (Window Capture via WGC / Display Capture via DXGI)** | **Supported** (WGC / DXGI Desktop Duplication) | Honors Windows display affinity when capturing via Windows 10+ WGC or DXGI Desktop Duplication. |
| **Browser / Meeting App Full-Screen Share (Google Meet, Zoom, Teams via WGC)** | **Subject to meeting software controls** | Standard OS window/screen capture APIs omit the window, **but** Gemini Desktop never bypasses meeting controls, tab sharing, or proctoring rules, and meeting clients can restrict or disable sharing policies independently. |
| **Windows 10 Versions Prior to 2004 (Build `< 19041`)** | **Not Supported** | `WDA_EXCLUDEFROMCAPTURE` is unavailable; Privacy Mode detects this and explains the OS requirement without failing. |
| **Remote Desktop (RDP) Host / Virtual Machine Hypervisor Screen Capture** | **Not Guaranteed / Unsupported** | Host-side hypervisor framebuffers or certain virtual display drivers capture outside the guest DWM affinity path or reject `SetWindowDisplayAffinity`. |
| **Hardware Capture Cards (HDMI/DisplayPort) & External Cameras** | **Not Supported** | Physical signal capture and external cameras record everything displayed on the monitor hardware. |
| **Kernel / Display-Driver Hooks & Privileged Capture Tools** | **Not Supported** | Privileged drivers operating below user-mode DWM composition are not constrained by `SetWindowDisplayAffinity`. |

---

## Ethical & Scope Commitments

- Privacy Mode uses only documented Microsoft Win32 APIs (`SetWindowDisplayAffinity`) for legitimate desktop privacy (for example, keeping personal workspace notes out of routine desktop screenshots).
- Gemini Desktop **never** claims universal invisibility across all capture methods, **never** attempts to bypass Google Meet, Zoom, or Microsoft Teams screen-sharing controls, and is **not** designed to conceal AI assistance during interviews, exams, or proctored sessions.
