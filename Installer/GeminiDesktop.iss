#define MyAppName "MyLaptop"
#define MyAppVersion "3.0.0"
#define MyAppPublisher "MyLaptop Contributors"
#define MyAppURL "https://gemini.google.com/"
#define MyAppExeName "MyLaptop.exe"
#define PublishDir "..\dist\publish-win-x64"

[Setup]
; Keep exact AppId from v1.0.0/v2.0.0 so installing v3.0.0 performs a seamless in-place upgrade
AppId={{8E92D674-5C62-4C2B-9A4A-71F9B43A2E80}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL=https://developer.microsoft.com/en-us/microsoft-edge/webview2/
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
OutputDir=..\dist
OutputBaseFilename=MyLaptop-Setup-{#MyAppVersion}
SetupIconFile=..\GeminiDesktop\Resources\Icons\MyLaptop.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
VersionInfoVersion=3.0.0.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Windows Installer (v{#MyAppVersion} Upgrade)
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Clean up legacy MasterMind and GeminiDesktop binaries/shortcuts when upgrading
Type: files; Name: "{app}\MasterMind.exe"
Type: files; Name: "{app}\MasterMind.dll"
Type: files; Name: "{app}\MasterMind.deps.json"
Type: files; Name: "{app}\MasterMind.runtimeconfig.json"
Type: files; Name: "{app}\MasterMind.pdb"
Type: files; Name: "{app}\GeminiDesktop.exe"
Type: files; Name: "{app}\GeminiDesktop.dll"
Type: files; Name: "{app}\GeminiDesktop.deps.json"
Type: files; Name: "{app}\GeminiDesktop.runtimeconfig.json"
Type: files; Name: "{app}\GeminiDesktop.pdb"
Type: files; Name: "{group}\MasterMind.lnk"
Type: files; Name: "{group}\Uninstall MasterMind.lnk"
Type: files; Name: "{autodesktop}\MasterMind.lnk"
Type: files; Name: "{group}\Gemini Desktop.lnk"
Type: files; Name: "{group}\Uninstall Gemini Desktop.lnk"
Type: files; Name: "{autodesktop}\Gemini Desktop.lnk"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; IconFilename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  WebView2ClientGuid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2DownloadUrl = 'https://developer.microsoft.com/en-us/microsoft-edge/webview2/';

function IsWebView2RuntimeInstalled(): Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2ClientGuid, 'pv', Version) and (Version <> '')) or
    (RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2ClientGuid, 'pv', Version) and (Version <> '')) or
    (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\' + WebView2ClientGuid, 'pv', Version) and (Version <> ''));
end;

function InitializeSetup(): Boolean;
var
  PromptResult: Integer;
  ErrorCode: Integer;
begin
  Result := True;

  if not IsWebView2RuntimeInstalled() then
  begin
    if not WizardSilent() then
    begin
      PromptResult := MsgBox(
        'Microsoft Edge WebView2 Runtime was not detected on this PC.' + #13#10 + #13#10 +
        'MyLaptop requires the WebView2 Evergreen Runtime to display its workspace.' + #13#10 + #13#10 +
        'Click Yes to open the official Microsoft WebView2 download page now, No to continue installing MyLaptop anyway, or Cancel to exit setup.',
        mbConfirmation, MB_YESNOCANCEL);

      if PromptResult = IDYES then
      begin
        ShellExec('open', WebView2DownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
      end
      else if PromptResult = IDCANCEL then
      begin
        Result := False;
      end;
    end;
  end;
end;
