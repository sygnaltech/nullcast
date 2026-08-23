; ─────────────────────────────────────────────────────────────────────────────
;  Nullcast — Windows installer (Inno Setup 6/7)
;
;  Do not run ISCC against this file directly: it expects a self-contained
;  publish tree to already exist. Use build.ps1, which publishes the app,
;  derives the version from VideoPlayer.csproj, and then invokes ISCC with the
;  right /D defines.
; ─────────────────────────────────────────────────────────────────────────────

#define AppName        "Nullcast"
#define AppPublisher   "Sygnal"
#define AppExeName     "VideoPlayer.exe"
#define AppUrl         "https://github.com/sygnal/nullcast"

; Supplied by build.ps1. The fallbacks let a developer compile from the IDE
; after running a publish by hand.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\app-flyleaf\bin\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "Output"
#endif

[Setup]
; Never change AppId — it is the identity Windows uses to match an upgrade to
; an existing install. A new GUID would install a second copy alongside the old.
AppId={{91C7AAE8-E5B4-485C-97DB-078DEDADEFD8}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}

; Dual-scope install. PrivilegesRequired=lowest means we start without asking
; for elevation; PrivilegesRequiredOverridesAllowed=dialog adds the "for all
; users / for me only" page, and elevates only if all-users is chosen. Every
; {auto*} constant below then resolves to the machine- or user-scoped location
; to match.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
LicenseFile=..\LICENSE
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

; The payload is a self-contained .NET build with FFmpeg, so it is x64-only.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Roughly 400 MB of mostly-compressible DLLs — lzma2/max is worth the build time.
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4

OutputDir={#OutputDir}
OutputBaseFilename=nullcast-setup-{#AppVersion}
SetupIconFile=..\app-flyleaf\VideoPlayer.ico
WizardStyle=modern
ShowLanguageDialog=no

; Shut a running Nullcast down rather than demanding a reboot mid-upgrade.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole publish tree: VideoPlayer.exe, the .NET runtime, FlyleafLib, the
; FFmpeg 8.0 shared libraries, bundled fonts, and telemetry.json.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; yt-dlp.exe self-updates in place and Deno is fetched on first run, so the app
; directory legitimately holds files the installer never wrote. Without this the
; uninstaller leaves the folder behind.
Type: filesandordirs; Name: "{app}"

[Code]
{ ── Uninstall: offer to remove per-user state ───────────────────────────────
  Settings, history, queue, playlists and service credentials live in
  %AppData%\VideoPlayer, and the downloaded yt-dlp/Deno binaries in
  %LocalAppData%\Nullcast\tools. Both survive an uninstall by default so that
  reinstalling (or upgrading via uninstall-then-install) does not silently wipe
  a user's library. Removing them is opt-in, and only ever touches the account
  running the uninstaller. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir, ToolsDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir  := ExpandConstant('{userappdata}\VideoPlayer');
    ToolsDir := ExpandConstant('{localappdata}\Nullcast');

    if DirExists(DataDir) or DirExists(ToolsDir) then
    begin
      if SuppressibleMsgBox(
           'Also delete your Nullcast settings, watch history, queue and saved service logins?'
           + #13#10#13#10 + DataDir
           + #13#10#13#10 + 'Choose No to keep them for a future reinstall.',
           mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
      begin
        DelTree(DataDir, True, True, True);
        DelTree(ToolsDir, True, True, True);
      end;
    end;
  end;
end;
