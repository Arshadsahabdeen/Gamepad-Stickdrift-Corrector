; Built by installer/build.ps1, which passes AppVersion and fetches the driver setups into installer/deps.
#define AppName "StickFix"
#define ViGEmSetup "ViGEmBus_1.22.0_x64_x86_arm64.exe"
#define HidHideSetup "HidHide_1.5.230_x64.exe"

[Setup]
AppId={{7C1F2A4E-6B1D-4B57-9A4C-3F0E5D2A9C11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Arshadsahabdeen
AppPublisherURL=https://github.com/Arshadsahabdeen/Gamepad-Stickdrift-Corrector
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Drivers can only be installed by an administrator.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=output
OutputBaseFilename=StickFix-Setup-{#AppVersion}
SetupIconFile=..\src\StickFix.App\Assets\StickFix.ico
UninstallDisplayIcon={app}\StickFix.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
AppMutex=Local\StickFixApp
LicenseFile=..\LICENSE
UsedUserAreasWarning=no

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "deps\{#ViGEmSetup}"; Flags: dontcopy
Source: "deps\{#HidHideSetup}"; Flags: dontcopy

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\StickFix.exe"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\StickFix.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\StickFix.exe"; Description: "Open StickFix"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
; Hiding outlives the app, so give the controller back before the program that knows how to do it is deleted.
Filename: "{app}\StickFix.exe"; Parameters: "--recover"; Flags: runhidden waituntilterminated; RunOnceId: "RestoreController"

[Code]
const
  ViGEmIndex = 0;
  HidHideIndex = 1;

var
  DriverPage: TInputOptionWizardPage;
  RestartNeeded: Boolean;

function ServiceExists(const Name: String): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\' + Name);
end;

procedure AddDriverOption(const Text, Service: String; Index: Integer);
var
  Installed: Boolean;
begin
  Installed := ServiceExists(Service);
  if Installed then
    DriverPage.Add(Text + ' (already installed)')
  else
    DriverPage.Add(Text);
  DriverPage.Values[Index] := not Installed;
  DriverPage.CheckListBox.ItemEnabled[Index] := not Installed;
end;

procedure InitializeWizard;
begin
  DriverPage := CreateInputOptionPage(wpSelectTasks,
    'Required drivers', 'StickFix needs these two free drivers to work.',
    'Each one opens its own setup window. They are kernel drivers made by Nefarius Software Solutions, and they stay installed if you later remove StickFix. A restart may be needed.',
    False, False);
  AddDriverOption('ViGEmBus 1.22.0: creates the corrected virtual controller', 'ViGEmBus', ViGEmIndex);
  AddDriverOption('HidHide 1.5.230: hides the real controller from games', 'HidHide', HidHideIndex);
end;

procedure InstallDriver(const FileName, DisplayName: String);
var
  Code: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Installing ' + DisplayName + '...';
  ExtractTemporaryFile(FileName);
  if not Exec(ExpandConstant('{tmp}\') + FileName, '', '', SW_SHOW, ewWaitUntilTerminated, Code) then
  begin
    MsgBox(DisplayName + ' setup could not start: ' + SysErrorMessage(Code) + #13#10 +
      'StickFix will offer a download link for it later.', mbError, MB_OK);
    Exit;
  end;
  Log(DisplayName + ' setup exit code: ' + IntToStr(Code));
  if (Code = 3010) or (Code = 1641) then
    RestartNeeded := True
  else if (Code <> 0) and not ServiceExists(DisplayName) then
    MsgBox(DisplayName + ' was not installed. StickFix needs it, and will offer a download link for it later.', mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep <> ssPostInstall) or WizardSilent then Exit;
  if DriverPage.Values[ViGEmIndex] then InstallDriver('{#ViGEmSetup}', 'ViGEmBus');
  if DriverPage.Values[HidHideIndex] then InstallDriver('{#HidHideSetup}', 'HidHide');
end;

function NeedRestart: Boolean;
begin
  Result := RestartNeeded;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'StickFix');
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'StickFixRecovery');
  if not UninstallSilent and
     (MsgBox('Also delete your calibration and settings?' + #13#10#13#10 +
             'HidHide and ViGEmBus stay installed because other programs may use them. You can remove them in Settings, Installed apps.',
             mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    DelTree(ExpandConstant('{localappdata}\StickFix'), True, True, True);
end;
