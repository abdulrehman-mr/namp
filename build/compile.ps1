param([switch]$SelfTestOnly)
# Compiles NAMP.exe (GUI, requires admin) and NAMP-SelfTest.exe (console, for automated tests)
# using the C# compiler that ships with Windows (.NET Framework 4.x) - no SDK needed.
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Csc  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$Src  = Join-Path $Root 'src'
$Out  = Join-Path $Root 'build'

$refs = @('System.dll','System.Core.dll','System.Windows.Forms.dll','System.Drawing.dll',
          'System.ServiceProcess.dll','System.Web.Extensions.dll') | ForEach-Object { "/r:$_" }
$refs += "/resource:$Root\assets\namp-logo.png,Namp.logo.png"
$refs += "/resource:$Src\welcome.php,Namp.welcome.php"
$core = @('Native.cs','Core.cs','Configs.cs','MariaDb.cs','Stack.cs','Program.cs','SelfTest.cs','Ssl.cs','Helper.cs') | ForEach-Object { Join-Path $Src $_ }
$ui   = @('Ui.cs','MainForm.cs') | ForEach-Object { Join-Path $Src $_ }

function Invoke-Csc($argsList) {
    $output = & $Csc @argsList 2>&1 | Where-Object { $_ -match 'error|warning CS' }
    if ($LASTEXITCODE -ne 0) { $output; throw 'Compilation failed' }
    $output | Where-Object { $_ -match 'error' }
}

if ($SelfTestOnly) {
    $stub = Join-Path $Out 'test-stub\MainFormStub.cs'
    Invoke-Csc (@('/nologo','/target:exe','/platform:x64','/optimize+',"/out:$Out\NAMP-SelfTest.exe",
        "/win32manifest:$Out\test-stub\asinvoker.manifest") + $refs + $core + @($stub))
    Write-Host 'Built NAMP-SelfTest.exe (core only)'
    return
}

Invoke-Csc (@('/nologo','/target:exe','/platform:x64','/optimize+',"/out:$Out\NAMP-SelfTest.exe",
    "/win32manifest:$Out\test-stub\asinvoker.manifest") + $refs + $core + $ui)
Invoke-Csc (@('/nologo','/target:winexe','/platform:x64','/optimize+',"/out:$Out\NAMP.exe",
    "/win32manifest:$Src\app.manifest","/win32icon:$Root\assets\namp.ico") + $refs + $core + $ui)
Write-Host 'Built NAMP.exe and NAMP-SelfTest.exe'

