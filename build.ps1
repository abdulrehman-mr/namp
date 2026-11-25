# Full rebuild: download components (cached) -> compile NAMP.exe -> run self-test -> build installer
#   powershell -ExecutionPolicy Bypass -File build.ps1 [-SkipTests]
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
& .\build\fetch.ps1
& .\build\make-icon.ps1
& .\build\compile.ps1
if (-not $SkipTests) {
    & .\build\run-selftest.ps1
    if ($LASTEXITCODE -ne 0) { throw 'Self-test failed - see build\selftest.log' }
    Remove-Item .\build\testenv -Recurse -Force
}
$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
& $iscc /Q installer\NAMP.iss
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
Get-ChildItem .\dist
