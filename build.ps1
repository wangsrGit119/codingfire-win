# build.ps1 - CodingFire for Windows
#
# Builds CodingFire.exe with whatever C# compiler is available on the system. The
# search order is:
#   1. _tools\roslyn-4.8.0\... - optional local copy of the Roslyn toolset
#      (used during development to enable C# 12 features; checked in nowhere
#      by default, drop it in if you have it)
#   2. %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe - ships with every
#      Windows 7+ install via the .NET 4.x in-box runtime
#   3. %WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe - 32-bit fallback
#
# Two targets:
#   default  -> .NET Framework 3.5 / CLR 2.0  (dist\)          runs on Win7 SP1 .. Win11
#   -Net4    -> .NET Framework 4.x  / CLR 4.0  (dist-net4\)    needs .NET 4.x installed
#
# The default target is the widest: Win7 SP1 ships 3.5.1 in-box, and the CLR 4 runtime
# on Win8/10/11 executes a CLR 2.0 assembly directly (see the dual supportedRuntime
# entries in the generated .config). A CLR 4.0 assembly can NOT go the other way -
# it needs .NET 4.x, which bare Win7 SP1 does not include.
#
# This script is intentionally ASCII-only so PowerShell 5.1 never mis-decodes it.

[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$Dump,
    [switch]$Net4
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Some hosts export the proxy settings twice under different casing (`http_proxy`
# AND `HTTP_PROXY`). PowerShell 5.1's process launcher builds a case-SENSITIVE
# dictionary out of the environment block and then dies with
#   "已添加项。字典中的关键字:"http_proxy"所添加的关键字:"HTTP_PROXY""
# The call operator swallows that exception, so the compiler never actually runs:
# no output, and $LASTEXITCODE stays empty. Drop the duplicates through the .NET
# API (the `$env:` provider is case-insensitive and does not reliably clear both).
foreach ($v in @('http_proxy', 'https_proxy', 'HTTP_PROXY', 'HTTPS_PROXY')) {
    [System.Environment]::SetEnvironmentVariable($v, $null)
}

$root       = $PSScriptRoot
$srcDir     = Join-Path $root 'src'
$distDir    = if ($Net4) { Join-Path $root 'dist-net4' } else { Join-Path $root 'dist' }
$exePath    = Join-Path $distDir 'CodingFire.exe'
$configOut  = "$exePath.config"

function Find-Compiler {
    # 1. Optional local Roslyn (preferred - enables modern C# language version)
    $local = Join-Path $root '_tools\roslyn-4.8.0\tasks\net472'
    $name  = 'cs' + 'c' + '.' + 'exe'
    $p = Join-Path $local $name
    if (Test-Path -LiteralPath $p) { return @{ Path = $p; Modern = $true } }

    # 2. In-box .NET 4.x 64-bit compiler
    $fw = Join-Path $env:WINDIR ('Microsoft.NET\Framework64\v4.0.30319\' + $name)
    if (Test-Path -LiteralPath $fw) { return @{ Path = $fw; Modern = $false } }

    # 3. In-box .NET 4.x 32-bit compiler
    $fw86 = Join-Path $env:WINDIR ('Microsoft.NET\Framework\v4.0.30319\' + $name)
    if (Test-Path -LiteralPath $fw86) { return @{ Path = $fw86; Modern = $false } }

    throw "No C# compiler found. Install .NET Framework 4.x (ships with Windows 7+), or Visual Studio / Build Tools."
}

$compiler = Find-Compiler
Write-Host "compiler : $($compiler.Path)" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
# Reference assemblies
# ---------------------------------------------------------------------------
# net35: base assemblies come from the in-box v2.0.50727 runtime (mscorlib et al.
#        are still version 2.0.0.0 in .NET 3.5), System.Core from the v3.5
#        reference-assembly set - that one is genuinely new in 3.5.
# net4 : everything from the in-box 4.x runtime.
$refArgs = @()

if ($Net4) {
    $refRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    if (-not (Test-Path -LiteralPath $refRoot)) {
        $refRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    }
    foreach ($n in @('mscorlib.dll','System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll')) {
        $full = Join-Path $refRoot $n
        if (Test-Path -LiteralPath $full) { $refArgs += "/reference:$full" }
    }
    Write-Host "target   : .NET Framework 4.x (CLR 4.0)  requires .NET 4.x" -ForegroundColor DarkGray
} else {
    $fx2 = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v2.0.50727'
    if (-not (Test-Path -LiteralPath $fx2)) {
        throw "In-box .NET 2.0 runtime not found at $fx2 (required for the 3.5 target)."
    }
    foreach ($n in @('mscorlib.dll','System.dll','System.Drawing.dll','System.Windows.Forms.dll')) {
        $full = Join-Path $fx2 $n
        if (-not (Test-Path -LiteralPath $full)) { throw "Missing reference assembly: $full" }
        $refArgs += "/reference:$full"
    }
    $coreRef = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\v3.5\System.Core.dll'
    if (-not (Test-Path -LiteralPath $coreRef)) {
        throw "System.Core 3.5 reference assembly not found at $coreRef. Install the '.NET Framework 3.5 SP1 Reference Assemblies' (ships with Visual Studio; standalone: https://go.microsoft.com/fwlink/?LinkId=2092350)."
    }
    $refArgs += "/reference:$coreRef"
    Write-Host "target   : .NET Framework 3.5 (CLR 2.0)  runs on Win7 SP1 .. Win11" -ForegroundColor DarkGray
}

$sources = Get-ChildItem -LiteralPath $srcDir -Recurse -Filter '*.cs' | Select-Object -ExpandProperty FullName
if (-not $sources -or $sources.Count -eq 0) { throw "No .cs sources under $srcDir" }

if (-not (Test-Path -LiteralPath $distDir)) { [System.IO.Directory]::CreateDirectory($distDir) | Out-Null }
if (Test-Path -LiteralPath $exePath) {
    # A running CodingFire.exe holds its own image open, so the delete fails with a
    # bare "access denied" and the script dies with no compiler output at all - which
    # reads exactly like a mystery compile failure. Say what is actually wrong.
    try {
        [System.IO.File]::Delete($exePath)
    } catch [System.IO.IOException] {
        throw "Cannot replace $exePath - it is still running. Quit CodingFire (tray icon -> Quit) and build again."
    } catch [System.UnauthorizedAccessException] {
        throw "Cannot replace $exePath - it is still running. Quit CodingFire (tray icon -> Quit) and build again."
    }
}

$argList = @(
    '/nologo',
    '/noconfig',
    '/target:winexe',
    '/platform:x86',
    '/optimize+',
    '/codepage:65001',
    '/nostdlib+',
    '/warn:4',
    "/out:$exePath"
)
if ($compiler.Modern) { $argList += '/langversion:latest' }
$argList += $refArgs
$argList += $sources

# Launch through Start-Process rather than the call operator. `&` goes through the
# same process launcher that trips over the duplicate proxy variables, and it
# swallows the resulting exception: no compiler output and an empty $LASTEXITCODE,
# which reads exactly like a silent compile failure. Start-Process also hands back
# the real exit code plus both streams.
$soFile = Join-Path ([System.IO.Path]::GetTempPath()) 'codingfire-csc-out.txt'
$seFile = Join-Path ([System.IO.Path]::GetTempPath()) 'codingfire-csc-err.txt'
[System.IO.File]::Delete($soFile)
[System.IO.File]::Delete($seFile)

# Start-Process joins -ArgumentList with spaces and does not quote, so any argument
# containing a space has to be quoted by hand (the .NET 3.5 reference assemblies
# live under "Program Files (x86)").
$quoted = @()
foreach ($a in $argList) {
    if ($a -match '\s') { $quoted += ('"' + $a + '"') } else { $quoted += $a }
}

$proc = Start-Process -FilePath $compiler.Path -ArgumentList $quoted -NoNewWindow -Wait -PassThru `
                      -RedirectStandardOutput $soFile -RedirectStandardError $seFile
$exit = $proc.ExitCode

$output = @()
if (Test-Path -LiteralPath $soFile) { $output += [System.IO.File]::ReadAllLines($soFile) }
if (Test-Path -LiteralPath $seFile) { $output += [System.IO.File]::ReadAllLines($seFile) }
if ($output.Count -gt 0) { $output | ForEach-Object { Write-Host $_ } }

if ($exit -ne 0 -or -not (Test-Path -LiteralPath $exePath)) {
    Write-Host "BUILD FAILED (exit $exit)" -ForegroundColor Red
    exit 1
}

# Dual-runtime startup config. A CLR 2.0 assembly lists v4.0 first so that Win8/10/11
# use their in-box CLR 4, and falls back to the 2.0 CLR on Win7 SP1.
$config = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup useLegacyV2RuntimeActivationPolicy="true">
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.0" />
    <supportedRuntime version="v2.0.50727" />
  </startup>
  <runtime>
    <gcServer enabled="false" />
    <gcConcurrent enabled="true" />
  </runtime>
</configuration>
'@
# Just-written executables get briefly locked by real-time antivirus / the indexer,
# so the config write can fail with "in use by another process" even though nothing
# of ours is running. Retry a few times before giving up.
$configWritten = $false
for ($i = 0; $i -lt 10 -and -not $configWritten; $i++) {
    try {
        [System.IO.File]::WriteAllText($configOut, $config, (New-Object System.Text.UTF8Encoding($true)))
        $configWritten = $true
    } catch [System.IO.IOException] {
        Start-Sleep -Milliseconds 200
    } catch [System.UnauthorizedAccessException] {
        Start-Sleep -Milliseconds 200
    }
}
if (-not $configWritten) {
    throw "Could not write $configOut - something is holding the file open. Quit CodingFire and build again."
}

$size = [math]::Round((Get-Item -LiteralPath $exePath).Length / 1KB, 1)
Write-Host "built    : $exePath ($size KB)" -ForegroundColor Green

# ---------------------------------------------------------------------------
# Windows 7 compatibility gate - static PE/CLR analysis, run on every build.
# ---------------------------------------------------------------------------
$checker = Join-Path $env:USERPROFILE '.workbuddy\skills\win7-compat\scripts\win7check.ps1'
if (Test-Path -LiteralPath $checker) {
    . $checker                       # dot-source: loads functions only
    $r = Test-Win7Compatible -FileName $exePath
    if ($r.Ok) {
        Write-Host "win7check: OK  ($($r.Summary))" -ForegroundColor Green
    } else {
        Write-Host "win7check: FAILED" -ForegroundColor Red
        $r.Blockers | ForEach-Object { Write-Host "  ! $_" -ForegroundColor Red }
        exit 1
    }
} else {
    Write-Host "win7check: skipped (checker not installed)" -ForegroundColor DarkYellow
}

if ($Dump) {
    & $exePath --dump
} elseif ($Run) {
    Start-Process -FilePath $exePath | Out-Null
}
