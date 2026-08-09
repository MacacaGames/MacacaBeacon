param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$SourceDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$PackageDirectory = Resolve-Path (Join-Path $SourceDirectory "..\..")
$OutputDirectory = Join-Path $PackageDirectory "Runtime\Plugins\Windows\x86_64"
$OutputPath = Join-Path $OutputDirectory "MacacaBeaconVideoWindows.dll"
$VsWhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"

if (-not (Test-Path $VsWhere)) {
    throw "Visual Studio Installer (vswhere.exe) was not found. Install Visual Studio 2022 Build Tools with Desktop development with C++."
}

$VisualStudio = & $VsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $VisualStudio) {
    throw "Visual Studio 2022 C++ x64 build tools were not found."
}

$DeveloperCommand = Join-Path $VisualStudio "Common7\Tools\VsDevCmd.bat"
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$CompilerFlags = if ($Configuration -eq "Debug") { "/Od /Zi /MTd" } else { "/O2 /GL /MT /DNDEBUG" }
$SourcePath = Join-Path $SourceDirectory "MacacaBeaconVideoWindows.cpp"
$ObjectPath = Join-Path $env:TEMP "MacacaBeaconVideoWindows.obj"
$ImportLibraryPath = Join-Path $env:TEMP "MacacaBeaconVideoWindows.lib"
$PdbPath = Join-Path $OutputDirectory "MacacaBeaconVideoWindows.pdb"
$Command = 'call "{0}" -no_logo -arch=x64 -host_arch=x64 && cl.exe /nologo /std:c++17 /EHsc /W4 {1} /DUNICODE /D_UNICODE /c "{2}" /Fo"{3}" && link.exe /nologo /DLL /MACHINE:X64 /LTCG /OUT:"{4}" /IMPLIB:"{5}" /PDB:"{6}" "{3}" mfplat.lib mfreadwrite.lib mfuuid.lib windowscodecs.lib shlwapi.lib ole32.lib' -f $DeveloperCommand, $CompilerFlags, $SourcePath, $ObjectPath, $OutputPath, $ImportLibraryPath, $PdbPath

& $env:ComSpec /d /s /c $Command
if ($LASTEXITCODE -ne 0) {
    throw "Native Windows encoder build failed with exit code $LASTEXITCODE."
}

Write-Host "Built $OutputPath"
