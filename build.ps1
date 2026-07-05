<#
.SYNOPSIS
    Builds the Jukebox-Visualizations wrapper and stages a self-contained
    drop-in layout suitable for placing next to Jukebox.exe.

.DESCRIPTION
    Produces a single staged folder at `./publish/stage/` containing:

        publish/stage/
        ├── lib/                              (flat — all drop-in files)
        │   ├── JukeboxVisualizations.dll     (managed wrapper)
        │   ├── JukeboxVisualizations.deps.json
        │   ├── libprojectM.dll               (Windows — from source lib/)
        │   ├── libprojectM.so.4              (Linux   — from source lib/)
        │   └── glew32.dll                    (Windows — from source lib/)
        └── ProjectM/                         (preset data only)
            ├── presets/
            └── textures/

    The user drops this entire `publish/stage/` content next to Jukebox.exe
    to enable visualizations. The native lib/ folder is NOT shipped in this
    repo — the developer populates `lib/` manually by downloading from
    the GitHub release produced by the build-natives.yml CI workflow
    (see lib/README.md for instructions).

    JukeboxVisualizations.dll is pure IL — identical on every platform.
    We use `dotnet build` (not `dotnet publish`) so only the project's own
    DLL and deps.json land in bin/Release/net10.0/, with no transitive
    Avalonia or Silk.NET DLLs copied alongside.

.NOTES
    Run from the Jukebox-Visualizations project root (next to
    JukeboxVisualizations.csproj).
#>

Set-Location $PSScriptRoot

Write-Host "Building Jukebox-Visualizations - Windows x64 & Linux x64" -ForegroundColor Green
Write-Host "=========================================================" -ForegroundColor Green
Write-Host ""

# ── Validate source layout ──────────────────────────────────────────────
if (-not (Test-Path "JukeboxVisualizations.csproj")) {
    Write-Host "ERROR: JukeboxVisualizations.csproj not found in current directory." -ForegroundColor Red
    Write-Host "       Run this script from the Jukebox-Visualizations project root." -ForegroundColor Red
    exit 1
}

$sourceLib = ".\lib"

# ── Verify lib/ is populated ────────────────────────────────────────────
# The user must manually download the libprojectM binary from the GitHub
# release (see lib/README.md for instructions). We don't auto-fetch —
# PowerShell execution policies and script dependencies make that fragile.
# Just check that lib/ has something in it and warn if not.
$libPopulated = (Test-Path $sourceLib) -and ((Get-ChildItem $sourceLib -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin @("README.md", ".gitignore") }).Count -gt 0)
if (-not $libPopulated) {
    Write-Host "WARNING: lib/ is empty or missing." -ForegroundColor Yellow
    Write-Host "         The build will succeed but native libprojectM binaries" -ForegroundColor Yellow
    Write-Host "         will not be staged in the drop-in zips." -ForegroundColor Yellow
    Write-Host "" -ForegroundColor Yellow
    Write-Host "         To populate lib/:" -ForegroundColor Yellow
    Write-Host "           1. Go to https://github.com/RobG66/Jukebox-Visualizations/releases" -ForegroundColor Yellow
    Write-Host "           2. Download libprojectM-win-x64.zip (or libprojectM-linux-x64.tar.gz)" -ForegroundColor Yellow
    Write-Host "           3. Extract into lib/" -ForegroundColor Yellow
    Write-Host "         See lib/README.md for details." -ForegroundColor Yellow
    Write-Host ""
}

# ── Clean previous builds ───────────────────────────────────────────────
Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
dotnet clean "JukeboxVisualizations.csproj" --configuration Release 2>$null | Out-Null
foreach ($dir in @("./publish/win-x64", "./publish/linux-x64", "./publish/stage")) {
    if (Test-Path $dir) {
        Remove-Item -Recurse -Force $dir
    }
}
foreach ($file in @(
    "./publish/Jukebox-Visualizations-win-x64-dropin.zip",
    "./publish/Jukebox-Visualizations-linux-x64-dropin.zip",
    "./publish/Jukebox-Visualizations-dropin.zip"
)) {
    if (Test-Path $file) {
        Remove-Item -Force $file
    }
}
Write-Host ""

# ── Build managed wrappers for both runtimes ─────────────────────────────
# JukeboxVisualizations.dll is pure IL — identical on every platform.
# We build for both RIDs so the RID-specific deps.json is generated.
# dotnet build (not publish) keeps the output clean: only the project's
# own DLL and deps.json land in bin/Release/net10.0/<rid>/, with no
# transitive Avalonia or Silk.NET DLLs copied alongside.

# --- Windows x64 ---
Write-Host "Building Windows x64..." -ForegroundColor Cyan
Write-Host "-----------------------" -ForegroundColor Cyan

dotnet build "JukeboxVisualizations.csproj" `
    --configuration Release `
    --runtime win-x64 `
    -p:DebugType=None `
    -p:DebugSymbols=false

$winSuccess = ($LASTEXITCODE -eq 0)
Write-Host ""

# --- Linux x64 ---
Write-Host "Building Linux x64..." -ForegroundColor Cyan
Write-Host "---------------------" -ForegroundColor Cyan

dotnet build "JukeboxVisualizations.csproj" `
    --configuration Release `
    --runtime linux-x64 `
    -p:DebugType=None `
    -p:DebugSymbols=false

$linuxSuccess = ($LASTEXITCODE -eq 0)
Write-Host ""

if (-not ($winSuccess -and $linuxSuccess)) {
    Write-Host "================================" -ForegroundColor Red
    Write-Host "Build FAILED - staging skipped." -ForegroundColor Red
    exit 1
}

# ── Stage into publish/<rid>/ ───────────────────────────────────────────
Write-Host "Staging into publish/..." -ForegroundColor Cyan
Write-Host "------------------------" -ForegroundColor Cyan

foreach ($rid in @("win-x64", "linux-x64")) {
    $dest = "./publish/$rid"
    $dstLib = Join-Path $dest "lib"
    New-Item -ItemType Directory -Force -Path $dstLib | Out-Null

    # 1) Native libraries from the source lib/ folder
    if ($libPopulated) {
        Copy-Item (Join-Path $sourceLib "*") -Destination $dstLib -Recurse -Force
        
        # Clean up platform-incompatible native binaries from this staged lib folder
        if ($rid -eq "win-x64") {
            # Remove Linux/macOS binaries
            Get-ChildItem $dstLib -File | Where-Object { $_.Extension -eq ".so" -or $_.Name -like "*.so.*" -or $_.Extension -eq ".dylib" } | Remove-Item -Force
        }
        elseif ($rid -eq "linux-x64") {
            # Remove Windows binaries
            Get-ChildItem $dstLib -File | Where-Object { $_.Extension -eq ".dll" -or $_.Extension -eq ".pdb" -or $_.Extension -eq ".exe" } | Remove-Item -Force
        }
        Write-Host "  Staged native libs for ${rid}" -ForegroundColor Gray
    }
    else {
        Write-Host "  Skipped native libs for ${rid}: source lib/ is empty" -ForegroundColor Yellow
    }

    # 2) Managed wrapper — copy from the build output (bin/Release/net10.0/<rid>/).
    #    Also copy deps.json so the runtime can resolve Avalonia + Silk.NET
    #    dependencies when loaded via Assembly.LoadFrom. Lives in <dest>/lib/
    #    alongside the native libprojectM binary.
    $buildOutput = "./bin/Release/net10.0/$rid"
    foreach ($file in @(
        "JukeboxVisualizations.dll",
        "JukeboxVisualizations.deps.json"
    )) {
        $src = Join-Path $buildOutput $file
        if (Test-Path $src) {
            Copy-Item $src -Destination $dstLib -Force
            Write-Host "  Copied for ${rid}: lib/$file" -ForegroundColor Gray
        }
    }

    # 3) ProjectM preset data copy has been disabled to prevent long build times and large ZIP sizes.
}
Write-Host ""

# ── Zip for distribution ───────────────────────────────────────────────
Write-Host "Creating zip packages..." -ForegroundColor Cyan
Write-Host "------------------------" -ForegroundColor Cyan

foreach ($rid in @("win-x64", "linux-x64")) {
    $dest = "./publish/$rid"
    $zipPath = "./publish/Jukebox-Visualizations-$rid-dropin.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    
    if (Test-Path $dest) {
        Compress-Archive -Path (Join-Path $dest "*") -DestinationPath $zipPath -Force
        Write-Host "Zip created for ${rid}:" -ForegroundColor Green
        Write-Host "  $((Resolve-Path $zipPath).Path)" -ForegroundColor Cyan
        $zipSize = (Get-Item $zipPath).Length / 1MB
        Write-Host ("  Size: {0:N2} MB" -f $zipSize) -ForegroundColor Cyan
    }
}
Write-Host ""

# ── Summary ───────────────────────────────────────────────────────────
Write-Host "================================" -ForegroundColor Green
Write-Host "Stage & Zip Complete" -ForegroundColor Green
Write-Host "================================" -ForegroundColor Green
Write-Host ""

if ($winSuccess) {
    Write-Host "  Windows x64 : OK -> ./publish/Jukebox-Visualizations-win-x64-dropin.zip" -ForegroundColor Green
} else {
    Write-Host "  Windows x64 : FAILED" -ForegroundColor Red
}

if ($linuxSuccess) {
    Write-Host "  Linux x64   : OK -> ./publish/Jukebox-Visualizations-linux-x64-dropin.zip" -ForegroundColor Green
} else {
    Write-Host "  Linux x64   : FAILED" -ForegroundColor Red
}
Write-Host ""

Write-Host "To enable visualizations in Jukebox:" -ForegroundColor Yellow
Write-Host "  1. Unzip the archive into your Jukebox build output directory" -ForegroundColor Yellow
Write-Host "     (next to Jukebox or Jukebox.exe). The result is:" -ForegroundColor Yellow
Write-Host "         lib/                          (wrapper + native runtimes)" -ForegroundColor Yellow
Write-Host "  2. Also drop Jukebox's own native runtimes (bass.dll, libmpv-2.dll" -ForegroundColor Yellow
Write-Host "     or libbass.so, libmpv.so.2) into the same lib/ folder." -ForegroundColor Yellow
Write-Host "  3. Restart Jukebox. The visualizer button appears in the transport" -ForegroundColor Yellow
Write-Host "     bar automatically." -ForegroundColor Yellow
Write-Host ""

if ($winSuccess -and $linuxSuccess) {
    exit 0
} else {
    exit 1
}
