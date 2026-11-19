# Builds FoxholeModManager.exe from this source with the .NET Framework's own C# compiler (no SDK needed).
# Foxhole Mod Manager (unofficial) - SPDX-License-Identifier: GPL-3.0-only
#   powershell -ExecutionPolicy Bypass -File build.ps1 [-Out <exe path>]
# The update check reads public_release.json (optional, next to this script: {"repo": "<owner>/<name>"} - copy
# public_release.example.json); without it the app builds and runs, and its update check says it has no update source.
param([string]$Out)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'the .NET Framework 4.x C# compiler (csc.exe) was not found' }
if (-not $Out) { $bin = Join-Path $here 'bin'; New-Item -ItemType Directory -Force $bin | Out-Null; $Out = Join-Path $bin 'FoxholeModManager.exe' }
$res = @("/win32icon:$(Join-Path $here 'resources\logo.ico')")
$rel = Join-Path $here 'public_release.json'
if (Test-Path $rel) { $res += "/resource:$rel,public_release.json" }
$manifest = Join-Path $here 'resources\launcher.manifest'
$src = Join-Path $here 'src'
$files = @((Get-ChildItem (Join-Path $src 'Launcher*.cs')).FullName) + @((Join-Path $src 'PakIni.cs'))
& $csc /nologo /target:winexe /optimize+ /define:PUBLIC /out:$Out "/win32manifest:$manifest" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    "/lib:$(Join-Path (Split-Path $csc) 'WPF')" /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll `
    @res @files
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }
Write-Host "built $Out"
