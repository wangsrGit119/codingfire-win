# run-fixtures.ps1 - compile src\*.cs + FixtureMain.cs and run the aggregation
# fixtures (one synthetic log per source adapter, asserting token totals).
#
# It references the same assemblies build.ps1 uses for the .NET 3.5 / CLR 2.0
# target, so this doubles as a real 3.5 compile gate (referencing v4.0 would let
# 4.0-only APIs through and produce a false green).
$ProgressPreference = 'SilentlyContinue'
$root  = Split-Path -Parent $PSScriptRoot
$tests = $PSScriptRoot
$log   = Join-Path $tests '_fixtures_run.txt'

# Inlined compiler discovery - same fallback chain as build.ps1.
function Find-Compiler {
    $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (Test-Path -LiteralPath $fw) { return @{ Path = $fw; Modern = $false } }
    $fw86 = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
    if (Test-Path -LiteralPath $fw86) { return @{ Path = $fw86; Modern = $false } }
    throw "No C# compiler found. Install .NET Framework 4.x or Visual Studio Build Tools."
}
$compiler = Find-Compiler

$fx2 = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v2.0.50727'
if (-not (Test-Path -LiteralPath $fx2)) { throw "In-box .NET 2.0 not found at $fx2 (required for the 3.5 target)." }
$refs = @('mscorlib.dll','System.dll','System.Drawing.dll','System.Windows.Forms.dll') |
        ForEach-Object { '/reference:' + (Join-Path $fx2 $_) }
$refs += '/reference:' + 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\v3.5\System.Core.dll'

$srcs = @(Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs | Select-Object -ExpandProperty FullName)
$srcs += (Join-Path $tests 'FixtureMain.cs')

$exe = Join-Path $tests '_fixtures_test.exe'
$argList = @('/nologo','/target:exe','/platform:x86','/codepage:65001','/nostdlib+','/noconfig',
             '/main:TinyFire.Dev.FixtureMain', ('/out:' + $exe)) + $refs + $srcs
if ($compiler.Modern) { $argList = @('/langversion:latest') + $argList }

$r = & $compiler.Path @argList 2>&1
$text = ($r | Out-String)
if ([string]::IsNullOrWhiteSpace($text)) { $text = 'COMPILE CLEAN' }
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add($text)
$lines.Add("compile exit=$LASTEXITCODE")

# The compile gate stays in front of the run on purpose: running a previous
# build's exe after a failed compile hands back "run exit=0" and looks like a
# pass.
if ($LASTEXITCODE -eq 0) {
    & $exe (Join-Path $tests '_fixtures')
    $lines.Add("run exit=$LASTEXITCODE")
} else {
    $lines.Add("run skipped (compile failed)")
}

$lines | Out-File $log -Encoding utf8
Write-Host ("exit=" + $LASTEXITCODE)
