# Runs the end-to-end self-test against a fresh copy of the staged stack.
#   -PhpDir <folder>  test with a different PHP build (e.g. build\extract\php85)
param([string]$PhpDir, [string]$Log = 'selftest.log')
$ErrorActionPreference = 'Stop'
$Build = $PSScriptRoot
$t = Join-Path $Build 'testenv'
if (Test-Path $t) { cmd /c rd /s /q "\\?\$t" }
robocopy (Join-Path $Build 'stage') $t /E /NFL /NDL /NJH /NJS /NP /XF vc_redist.x64.exe | Out-Null
if ($PhpDir) {
    $ssl = Join-Path $t 'php\extras\ssl\cacert.pem'
    $keep = Join-Path $Build 'cacert.tmp'
    Copy-Item $ssl $keep -Force
    Remove-Item (Join-Path $t 'php') -Recurse -Force
    robocopy $PhpDir (Join-Path $t 'php') /E /NFL /NDL /NJH /NJS /NP | Out-Null
    Copy-Item $keep (Join-Path $t 'php\extras\ssl\cacert.pem') -Force
    $v = (& (Join-Path $t 'php\php.exe') -r 'echo PHP_VERSION;')
    $vj = Get-Content (Join-Path $t 'versions.json') -Raw | ConvertFrom-Json
    $vj.Php = $v
    $vj | ConvertTo-Json | Set-Content (Join-Path $t 'versions.json') -Encoding ASCII
    Write-Host "Testing with PHP $v"
}
& (Join-Path $Build 'NAMP-SelfTest.exe') --selftest $t *> (Join-Path $Build $Log)
$code = $LASTEXITCODE
Select-String -Path (Join-Path $Build $Log) -Pattern '^(PASS|FAIL)|^      |TESTS' | ForEach-Object { $_.Line }
exit $code

