; Harmoni Setup Script (Inno Setup 6)
; يُبنى تلقائياً من update-harmoni-v11.bat:
;   ISCC /DSourceDir=<مجلد البرنامج المبني> /DMyAppVersion=<رقم> /O<مجلد الإخراج> HarmoniSetup.iss
; نفس AppId القديم => يحدّث النسخة المثبتة في %LOCALAPPDATA%\Harmoni بدل ما يثبت نسخة ثانية.
; بيانات المستخدم في %LOCALAPPDATA%\HarmoniData وما يلمسها المثبّت ولا إلغاء التثبيت.
#define MyAppName "Harmoni"
#ifndef MyAppVersion
  #define MyAppVersion "1.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\outputs\Harmoni-discord\Harmoni"
#endif
#define MyAppExeName "Harmoni.exe"

[Setup]
AppId={{8E4B7C2A-9D31-4F5B-A6C8-HARMONI00001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Harmoni
AppComments=Harmoni - music with friends
VersionInfoDescription=Harmoni Setup
ShowLanguageDialog=auto
DefaultDirName={localappdata}\{#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
OutputDir=installer
OutputBaseFilename=HarmoniSetup
SetupIconFile=static\harmoni.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=force
RestartApplications=no

[Languages]
#if FileExists(AddBackslash(CompilerPath) + "Languages\Arabic.isl")
Name: "ar"; MessagesFile: "compiler:Languages\Arabic.isl"
#endif
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[InstallDelete]
; ملفات النسخة القديمة داخل مجلد البرنامج (البيانات صارت في HarmoniData)
Type: filesandordirs; Name: "{app}\_internal"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "profiles.json,profiles.json.bak,*.log,audio_cache\*,search_cache.json,stream_cache.json,artist_cache.json,spotify_links.json,discord_config.json,.harmoni_secret,youtube_cookies.txt,youtube_link.bin,youtube_link.json"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; AppUserModelID: "hiro.harmoni.1"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; AppUserModelID: "hiro.harmoni.1"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
; التحديث من داخل البرنامج يشغّل المثبّت بصمت مع /relaunch=1 ⇒ نرجّع نفتح البرنامج بعد التثبيت
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: RelaunchAfterUpdate

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function RelaunchAfterUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

{ pywebview needs Microsoft Edge WebView2 (built into Windows 11 and most Windows 10).
  If it's missing we warn and offer the official download page - we don't block the install. }
function HasWebView2(): Boolean;
var
  v: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) then
    if (v <> '') and (v <> '0.0.0.0') then Result := True;
  if not Result then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) then
      if (v <> '') and (v <> '0.0.0.0') then Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  code: Integer;
begin
  if (CurStep = ssPostInstall) and (not WizardSilent()) and (not HasWebView2()) then
    if MsgBox('Harmoni needs Microsoft Edge WebView2 Runtime, which is not on this PC.' + #13#10 +
              'Open the official Microsoft download page now?' + #13#10#13#10 +
              'يحتاج Harmoni برنامج WebView2 من مايكروسوفت وهو غير موجود بجهازك. تبي نفتح صفحة التحميل الرسمية؟',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://go.microsoft.com/fwlink/p/?LinkId=2124703', '', '', SW_SHOWNORMAL, ewNoWait, code);
end;
