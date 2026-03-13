; BTC Trading Bot - Inno Setup Script
; Build: dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish

#define MyAppName "BTC Trading Bot"
#define MyAppVersion "0.5"
#define MyAppPublisher "BTC Trading Bot"
#define MyAppExeName "BtcTradingBot.exe"
#define MyAppURL ""

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
; 추가 탭(프로그램 그룹 선택) 비활성
DisableProgramGroupPage=yes
; 설치 경로 변경 화면 비활성 (기본 경로 자동 사용)
DisableDirPage=yes
OutputDir=..\installer_output
OutputBaseFilename=BtcTradingBot_Setup_v{#MyAppVersion}
SetupIconFile=..\BtcTradingBot\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; 앱 전체 파일 (네이티브 WPF DLL 포함)
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.zip,BtcTradingBot.deps.json"

[Icons]
; 시작 메뉴
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
; 바탕화면 아이콘 (항상 자동 생성)
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
; 설치 완료 후 앱 바로 실행
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
