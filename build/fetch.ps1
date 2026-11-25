# Downloads and stages Apache, PHP, MariaDB, phpMyAdmin and the VC++ runtime
# into build\stage\ so the installer can bundle them.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root  = Split-Path -Parent $PSScriptRoot
$Dl    = Join-Path $PSScriptRoot 'downloads'
$Stage = Join-Path $PSScriptRoot 'stage'
$Tmp   = Join-Path $PSScriptRoot 'extract'
New-Item -ItemType Directory -Force $Dl, $Stage | Out-Null
$UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36'

$Versions = @{
    Apache     = '2.4.71-261112'
    Php83      = '8.3.37'
    Php85      = '8.5.13'
    MariaDB    = '11.8.10'
    PhpMyAdmin = '5.2.3'
}

$Items = @(
    @{ Name='apache.zip';     Url="https://www.apachelounge.com/download/VS18/binaries/httpd-$($Versions.Apache)-Win64-VS18.zip"; Sha=$null },
    @{ Name='php.zip';        Url="https://windows.php.net/downloads/releases/php-$($Versions.Php83)-Win32-vs16-x64.zip"; Sha='07984bdcfd4391888954c2e73b1cf2c0d403cfa5fd8cb32ad60ddcd854c6b7de' },
    @{ Name='php85.zip';      Url="https://windows.php.net/downloads/releases/php-$($Versions.Php85)-Win32-vs17-x64.zip"; Sha='792fe1553895ee98f9bc3c9d50c539910f7d0066676a6523207ca91429d4d475' },
    @{ Name='mariadb.zip';    Url="https://archive.mariadb.org/mariadb-$($Versions.MariaDB)/winx64-packages/mariadb-$($Versions.MariaDB)-winx64.zip"; Sha='05d04b46763ea3dc9111bfc82dc061e0a158c682a73d31ba34b13f2653c88d17' },
    @{ Name='phpmyadmin.zip'; Url="https://files.phpmyadmin.net/phpMyAdmin/$($Versions.PhpMyAdmin)/phpMyAdmin-$($Versions.PhpMyAdmin)-all-languages.zip"; Sha='pma' },
    @{ Name='vc_redist.x64.exe'; Url='https://aka.ms/vc14/vc_redist.x64.exe'; Sha=$null },
    @{ Name='cacert.pem'; Url='https://curl.se/ca/cacert.pem'; Sha=$null }
)

foreach ($it in $Items) {
    $dest = Join-Path $Dl $it.Name
    if (-not (Test-Path $dest)) {
        Write-Host "Downloading $($it.Url)"
        Invoke-WebRequest -Uri $it.Url -OutFile "$dest.part" -UserAgent $UA -UseBasicParsing
        Move-Item "$dest.part" $dest -Force
    }
    $expected = $it.Sha
    if ($expected -eq 'pma') {
        $c = (Invoke-WebRequest -Uri ($it.Url + '.sha256') -UseBasicParsing -UserAgent $UA).Content
        if ($c -is [byte[]]) { $c = [Text.Encoding]::ASCII.GetString($c) }
        $expected = ($c.Trim() -split '\s+')[0]
    }
    if ($expected) {
        $actual = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower()
        if ($actual -ne $expected.ToLower()) { Remove-Item $dest; throw "Checksum mismatch for $($it.Name)" }
        Write-Host "  sha256 OK  $($it.Name)"
    }
}

function Expand-Fresh($zip, $name) {
    $out = Join-Path $Tmp $name
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    New-Item -ItemType Directory -Force $out | Out-Null
    & 'C:\Program Files\7-Zip\7z.exe' x -y -bso0 -bsp0 "-o$out" (Join-Path $Dl $zip)
    if ($LASTEXITCODE -ne 0) { throw "extract failed: $zip" }
    return $out
}

if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
New-Item -ItemType Directory -Force $Stage | Out-Null

# Apache: zip contains Apache24\
$a = Expand-Fresh 'apache.zip' 'apache'
Move-Item (Join-Path $a 'Apache24') (Join-Path $Stage 'apache')
Remove-Item (Join-Path $Stage 'apache\htdocs\*') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $Stage 'apache\manual') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $Stage 'apache\logs\*') -Recurse -Force -ErrorAction SilentlyContinue

# PHP: zip contents are flat. Both versions ship; the user picks one in Settings.
New-Item -ItemType Directory -Force (Join-Path $Stage 'php') | Out-Null
$p = Expand-Fresh 'php.zip' 'php83'
Move-Item $p (Join-Path $Stage 'php\8.3')
$p = Expand-Fresh 'php85.zip' 'php85'
Move-Item $p (Join-Path $Stage 'php\8.5')

# MariaDB: zip contains mariadb-x.y.z-winx64\
$m = Expand-Fresh 'mariadb.zip' 'mariadb'
$mdir = Get-ChildItem $m -Directory | Select-Object -First 1
Move-Item $mdir.FullName (Join-Path $Stage 'mariadb')
# Trim things a local dev server never needs (tests, debug symbols, headers, static libs)
foreach ($junk in 'mysql-test','sql-bench','include','lib\*.lib','data') {
    Remove-Item (Join-Path $Stage "mariadb\$junk") -Recurse -Force -ErrorAction SilentlyContinue
}
Get-ChildItem (Join-Path $Stage 'mariadb') -Recurse -Include *.pdb | Remove-Item -Force

# phpMyAdmin: zip contains phpMyAdmin-x-all-languages\
$q = Expand-Fresh 'phpmyadmin.zip' 'phpmyadmin'
$qdir = Get-ChildItem $q -Directory | Select-Object -First 1
Move-Item $qdir.FullName (Join-Path $Stage 'phpmyadmin')

Copy-Item (Join-Path $Dl 'vc_redist.x64.exe') (Join-Path $Stage 'vc_redist.x64.exe')

# CA bundle so PHP (curl/openssl/composer) can make HTTPS requests out of the box
foreach ($pv in '8.3','8.5') {
    New-Item -ItemType Directory -Force (Join-Path $Stage "php\$pv\extras\ssl") | Out-Null
    Copy-Item (Join-Path $Dl 'cacert.pem') (Join-Path $Stage "php\$pv\extras\ssl\cacert.pem")
}

# Never ship a stock httpd.conf: NAMP generates its own on first start and an
# upgrade must not overwrite the user's edited copy.
Remove-Item (Join-Path $Stage 'apache\conf\httpd.conf') -Force -ErrorAction SilentlyContinue
Remove-Item $Tmp -Recurse -Force

$Versions | ConvertTo-Json | Set-Content (Join-Path $Stage 'versions.json') -Encoding ASCII
$size = (Get-ChildItem $Stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Stage ready: {0:N0} MB" -f $size)
