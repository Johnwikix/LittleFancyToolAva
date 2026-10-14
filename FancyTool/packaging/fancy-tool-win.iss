; Inno Setup script for the FancyTool Windows x64 installer.
; Compiled by ..\pack-win-installer.ps1, which injects every {#MyApp*} value below
; through ISPP /D switches, so paths and version stay in sync with the csproj.
; Invoke iscc by hand only for experimentation.

#define MyAppName "FancyTool"
#define MyAppExeName "FancyToolAva.exe"

[Setup]
; Stable GUID: keeps upgrades in-place instead of side-by-side installs. Regenerate
; only if this package must be treated as a different product.
AppId={{B8B448CF-0288-4FC7-BAEB-C7E4437830ED}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile={#MyAppIconFile}
LicenseFile={#MyAppLicenseFile}
OutputDir={#MyAppOutputDir}
OutputBaseFilename={#MyAppOutputBaseFilename}
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Admin rights are not forced: the PrivilegesRequiredOverridesAllowed dialog lets a
; non-elevated user install under %LOCALAPPDATA%\Programs via {autopf}.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
; Inno Setup 6 bundles no official Simplified Chinese .isl, so ChineseSimplified.isl
; (community translation, from jrsoftware.org/files/istrans) is vendored here.
; First entry is the fallback for messages a later file lacks. The wizard picks the
; language from the Windows UI locale and shows a dropdown since there is more than one.
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; *.pdb (debug symbols, roughly half of the publish folder) and the Linux packaging
; artifacts copied into the publish output must not reach the target machine.
Source: "{#MyAppPublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,packaging\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
