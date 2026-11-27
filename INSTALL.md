# Installing MyLaptop

## System Requirements

- Windows 10 (Version 1809 or newer; Version 2004 / Build 19041+ required for `WDA_EXCLUDEFROMCAPTURE`) or Windows 11 (64-bit)
- Microsoft Edge WebView2 Evergreen Runtime
  - Preinstalled on Windows 11 and most updated Windows 10 machines
  - If missing, the installer checks the Windows Registry and directs you to Microsoft's official WebView2 download page: https://developer.microsoft.com/en-us/microsoft-edge/webview2/
- Visual Studio is **not** required.

## Installation & Upgrade Steps

1. Run `dist\MyLaptop-Setup-3.0.0.exe`.
2. **Upgrading from v1.0.0 / v2.0.0**: Because v3.0.0 shares the same Windows `AppId`, running `MyLaptop-Setup-3.0.0.exe` automatically upgrades your existing `MasterMind` or `Gemini Desktop` installation in place, closes running instances cleanly, cleans up legacy filenames, updates Start Menu / Installed Apps to `MyLaptop` `3.0.0`, and preserves your settings.
3. Follow the setup wizard to choose the installation folder (on fresh installs, installs per-user by default without requiring administrator elevation, or pass `/ALLUSERS` for machine-wide installation) and optional Desktop shortcut.
4. Launch **MyLaptop** from the Start Menu or Desktop shortcut.

### Silent Installation / Upgrade (Optional)

```powershell
.\dist\MyLaptop-Setup-3.0.0.exe /VERYSILENT /NORESTART /CURRENTUSER
```

## Uninstallation

- Open **Windows Settings → Apps → Installed apps** (or **Control Panel → Programs and Features**), select **MyLaptop**, and click **Uninstall**, or run `unins000.exe` inside the installation directory.
- User profile data (`%LocalAppData%\MyLaptop`) is kept separate from program binaries so preferences or WebView2 caches can be managed independently from **Settings → Browser Settings**.
