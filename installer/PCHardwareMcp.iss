; PC Hardware MCP - per-user installer
;
; Installs into the user's roaming profile (no admin rights for this part):
;   %APPDATA%\PCHardwareMcp\server\PCHardwareMcp.Server.exe     self-contained MCP server (what Claude runs)
;   %APPDATA%\PCHardwareMcp\service\PCHardwareMcp.Service.exe   staging copy of the sensor service
; and registers the server in Claude Desktop's claude_desktop_config.json (classic + Microsoft Store builds).
;
; Optional elevated step (one UAC prompt): the service copies itself to %ProgramFiles%\PCHardwareMcp, registers as a
; LocalSystem Windows service serving \\.\pipe\PCHardwareMcp, and can install the PawnIO driver with winget.
; Without it the MCP server still works in-process, minus the sensors that need admin rights.
;
; Build with ..\Build-Installer.ps1 (publishes both exes first).
; Silent: PCHardwareMcpSetup-<ver>.exe /VERYSILENT /TASKS="claude,service,service\pawnio"

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppName "PC Hardware MCP"
#define ServerExe "PCHardwareMcp.Server.exe"
#define ServiceExe "PCHardwareMcp.Service.exe"
#define ServiceName "PCHardwareMcp"
#define McpName "pc-hardware"

[Setup]
AppId={{B3E1F0A4-6C2D-4F8E-9A71-5D2C8E4B1F63}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Pete
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={userappdata}\PCHardwareMcp
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=PCHardwareMcpSetup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\server\{#ServerExe}
CloseApplications=no
SetupLogging=yes

[Files]
Source: "..\publish\server\{#ServerExe}"; DestDir: "{app}\server"; Flags: ignoreversion
Source: "..\publish\service\{#ServiceExe}"; DestDir: "{app}\service"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "claude"; Description: "Add the MCP server to Claude Desktop (claude_desktop_config.json)"
Name: "service"; Description: "Install the sensor service for full sensor data (asks for administrator rights)"
Name: "service\pawnio"; Description: "Install the PawnIO driver with winget (needed for CPU temps, fans, voltages, DIMM SPD)"; Check: not PawnIoInstalled

[Run]
Filename: "{app}\server\{#ServerExe}"; Parameters: "--register --name {#McpName}"; \
  Flags: runhidden waituntilterminated; StatusMsg: "Registering with Claude Desktop..."; Tasks: claude
Filename: "{app}\service\{#ServiceExe}"; Parameters: "--install{code:PawnIoArg}"; Verb: "runas"; \
  Flags: shellexec runhidden waituntilterminated; StatusMsg: "Installing the sensor service (administrator)..."; Tasks: service

[UninstallRun]
Filename: "{app}\server\{#ServerExe}"; Parameters: "--unregister --name {#McpName}"; \
  Flags: runhidden waituntilterminated; RunOnceId: "UnregisterClaude"

[Code]
function PawnIoInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

function ServiceInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\{#ServiceName}');
end;

function PawnIoArg(Param: String): String;
begin
  if WizardIsTaskSelected('service\pawnio') then
    Result := ' --with-pawnio'
  else
    Result := '';
end;

procedure KillServer;
var
  Code: Integer;
begin
  { Claude Desktop keeps the server exe open; stop it so it can be replaced. Claude restarts it. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#ServerExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillServer;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  KillServer;
  Result := True;
end;

{ Decided at uninstall time: [UninstallRun] Check functions are evaluated during *installation*, before the
  service exists, so a fresh install would never record the step. One UAC prompt, only when the service is there. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if (CurUninstallStep = usUninstall) and ServiceInstalled then
    if not ShellExec('runas', ExpandConstant('{app}\service\{#ServiceExe}'), '--uninstall', '', SW_HIDE,
                     ewWaitUntilTerminated, Code) then
      SuppressibleMsgBox('The PCHardwareMcp sensor service could not be removed (administrator prompt declined?).' + #13#10 +
        'Remove it later with:  sc.exe delete PCHardwareMcp', mbInformation, MB_OK, IDOK);
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Msg: String;
begin
  if CurPageID <> wpFinished then
    Exit;
  Msg := '';
  if WizardIsTaskSelected('service') then
  begin
    if ServiceInstalled then
      Msg := 'Sensor service: installed.'
    else
      Msg := 'Sensor service: NOT installed (administrator prompt declined or failed - see %ProgramData%\PCHardwareMcp\install.log). ' +
             'The MCP server still works with fewer sensors; rerun setup to retry.';
  end;
  if WizardIsTaskSelected('claude') then
    Msg := Msg + #13#10#13#10 + 'Restart Claude Desktop to load the "{#McpName}" MCP server.';
  WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + Trim(Msg);
end;
