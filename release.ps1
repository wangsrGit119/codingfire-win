# release.ps1 - CodingFire for Windows
#
# Builds, packages and publishes a GitHub release in one go:
#   build -> zip (exe + config) -> annotated tag -> push tag -> gh release create
#
# The zip is the release asset. dist\ and *.exe/*.zip are gitignored, so the
# binary never enters git history - only the tag and the release point at it.
#
# Usage (run from the repo root, or anywhere - paths are resolved from $PSScriptRoot):
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.1.0
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.1.0 -SkipBuild
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.1.0 -NoPush
#
# -SkipBuild   reuse an existing dist\CodingFire.exe instead of rebuilding
# -NoPush      do everything locally (zip + tag) and print the push commands
# -Notes       release notes body; a sensible default is generated if omitted
# -Force       overwrite an existing local tag of the same name
#
# Requires: git. gh CLI is optional - without it the script prints the exact
# web-UI steps instead of creating the release for you.
#
# This script is intentionally ASCII-only so PowerShell 5.1 never mis-decodes it.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = '',
    [switch]$SkipBuild,
    [switch]$NoPush,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = $PSScriptRoot
$distDir = Join-Path $root 'dist'
$exePath = Join-Path $distDir 'CodingFire.exe'
$cfgPath = "$exePath.config"

function Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Ok([string]$text) { Write-Host "    $text" -ForegroundColor Green }
function Warn([string]$text) { Write-Host "    $text" -ForegroundColor DarkYellow }

# ---------------------------------------------------------------------------
# 1. Version + tag name
# ---------------------------------------------------------------------------
$ver = $Version.Trim()
if ($ver.StartsWith('v') -or $ver.StartsWith('V')) { $ver = $ver.Substring(1) }
if ($ver -notmatch '^\d+\.\d+(\.\d+)?$') {
    throw "Version must look like 1.1.0 or 1.1 (got '$Version')."
}
$tag = "v$ver"
$zipName = "CodingFire-win-x86-$tag.zip"
$zipPath = Join-Path $root $zipName

Write-Host "CodingFire release : $tag" -ForegroundColor White
Write-Host "target           : .NET 4.x (CLR 4.0), Windows 8..11" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
# 2. Sanity: must be a git repo, tag must not exist yet
# ---------------------------------------------------------------------------
Step "Checking repository state"
if (-not (Test-Path -LiteralPath (Join-Path $root '.git'))) { throw "Not a git repository: $root" }

$dirty = (& git -C $root status --porcelain) -join "`n"
if ($dirty.Trim().Length -gt 0) {
    Warn "working tree has uncommitted changes - the release will NOT contain them:"
    $dirty.Trim().Split("`n") | ForEach-Object { Warn "  $_" }
}

$existing = (& git -C $root tag -l $tag) -join ''
if ($existing.Trim().Length -gt 0) {
    if (-not $Force) { throw "Tag $tag already exists. Bump the version or pass -Force to move it." }
    Warn "tag $tag exists - deleting it because -Force was given"
    & git -C $root tag -d $tag | Out-Null
}

# ---------------------------------------------------------------------------
# 3. Build (unless -SkipBuild)
# ---------------------------------------------------------------------------
if ($SkipBuild) {
    Step "Skipping build (-SkipBuild)"
} else {
    Step "Building"
    # Call build.ps1 in-process: it `exit 1`s on failure, which aborts this script
    # too - exactly what we want, since a failed build must never produce a tag.
    # (Spawning a child powershell would only hide that failure behind an exit code.)
    $buildScript = Join-Path $root 'build.ps1'
    & $buildScript
}

if (-not (Test-Path -LiteralPath $exePath)) { throw "Missing $exePath - build first (drop -SkipBuild)." }
if (-not (Test-Path -LiteralPath $cfgPath)) { throw "Missing $cfgPath - build.ps1 should have produced it." }

$sizeKb = [math]::Round((Get-Item -LiteralPath $exePath).Length / 1KB, 1)
Ok "$exePath ($sizeKb KB)"

# ---------------------------------------------------------------------------
# 3b. The binary must claim the version we are about to tag.
#
# The version lives in exactly one place (src\AssemblyInfo.cs). Forgetting to
# bump it is the kind of mistake you only notice when someone reports a bug
# against "1.0.1" that was actually fixed in 1.0.2 - so refuse to tag instead.
# ---------------------------------------------------------------------------
Step "Checking binary version"
$vi = (Get-Item -LiteralPath $exePath).VersionInfo
$reported = if ($vi -and $vi.ProductVersion) { $vi.ProductVersion.Trim() } else { '' }
if ($reported.Length -eq 0) {
    throw "$exePath carries no version resource. Did src\AssemblyInfo.cs get excluded from the build?"
}
if ($reported -ne $ver) {
    throw @"
Version mismatch: the binary reports '$reported' but you are releasing '$ver'.

Bump src\AssemblyInfo.cs and rebuild:
    [assembly: AssemblyFileVersion("$ver")]
    [assembly: AssemblyInformationalVersion("$ver")]
"@
}
Ok "binary reports $reported"

# ---------------------------------------------------------------------------
# 4. Package the release asset
# ---------------------------------------------------------------------------
Step "Packaging $zipName"
# -Force overwrites any previous zip; no separate delete step needed.
Compress-Archive -LiteralPath @($exePath, $cfgPath) -DestinationPath $zipPath -Force
$zipKb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1KB, 1)
Ok "$zipPath ($zipKb KB)"

# ---------------------------------------------------------------------------
# 5. Annotated tag
# ---------------------------------------------------------------------------
Step "Tagging $tag"
& git -C $root tag -a $tag -m "CodingFire for Windows $tag"
if ($LASTEXITCODE -ne 0) { throw "git tag failed (exit $LASTEXITCODE)." }
Ok "tagged $(git -C $root rev-parse --short HEAD)"

# ---------------------------------------------------------------------------
# 6. Push the tag
# ---------------------------------------------------------------------------
$remote = (& git -C $root remote get-url origin) -join ''
$remote = $remote.Trim()
Write-Host "    remote: $remote" -ForegroundColor DarkGray

if ($NoPush) {
    Step "-NoPush given - stopping before push"
    Warn "push the tag yourself when ready:"
    Warn "  git push origin $tag"
} else {
    Step "Pushing tag"
    & git -C $root push origin $tag
    if ($LASTEXITCODE -ne 0) {
        Warn "git push failed (exit $LASTEXITCODE). The tag exists locally; retry with:"
        Warn "  git push origin $tag"
    } else {
        Ok "pushed $tag"
    }
}

# ---------------------------------------------------------------------------
# 7. Create the release
# ---------------------------------------------------------------------------
if ($Notes.Trim().Length -eq 0) {
    $Notes = @"
CodingFire for Windows $tag

Download the zip, unpack it anywhere and run CodingFire.exe - no installer and no
runtime needed.

* Single file: CodingFire.exe (~190 KB) + CodingFire.exe.config
* One binary for Windows 8 through Windows 11 (.NET 4.x / CLR 4.0 target)
* 23 read-only local data sources, including WorkBuddy (CN) and WorkBuddy (INTL)
  counted separately
"@
}

$gh = Get-Command gh -ErrorAction SilentlyContinue
if ($gh) {
    Step "Creating release via gh"
    & gh release create $tag $zipPath --title "CodingFire for Windows $tag" --notes $Notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed (exit $LASTEXITCODE)." }
    Ok "release $tag published"
} else {
    Step "gh CLI not found - finish in the browser"
    Warn "1. open: $($remote -replace '\.git$', '')/releases/new?tag=$tag"
    Warn "2. set the title to: CodingFire for Windows $tag"
    Warn "3. attach this file: $zipPath"
    Warn "4. paste the notes below, then click 'Publish release'"
    Write-Host ''
    Write-Host $Notes
    Write-Host ''
}

Write-Host "Done." -ForegroundColor Green
