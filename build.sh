#!/usr/bin/env bash
#
# Builds the Avalonia.ProjectM library and stages a self-contained layout.
# Bash equivalent of build.ps1 for Linux/macOS developers.
#
# Avalonia.ProjectM.dll is pure IL — identical on every platform.
# We use `dotnet build` (not `dotnet publish`) so only the project's own
# DLL and deps.json land in bin/Release/net10.0/, with no transitive
# Avalonia or Silk.NET DLLs copied alongside.
#
# Run from the project root (next to Avalonia.ProjectM.csproj).
#
set -euo pipefail

cd "$(dirname "$0")"

echo "Building Avalonia.ProjectM - Windows x64 & Linux x64"
echo "========================================================="
echo ""

# ── Validate source layout ──────────────────────────────────────────
if [[ ! -f "Avalonia.ProjectM.csproj" ]]; then
    echo "ERROR: Avalonia.ProjectM.csproj not found in current directory." >&2
    echo "       Run this script from the project root." >&2
    exit 1
fi



SOURCE_LIB="./lib"

# ── Verify lib/ is populated ─────────────────────────────────────────
# The user must manually download the libprojectM binary from the GitHub
# release (see lib/README.md for instructions). We don't auto-fetch —
# that would require extra dependencies. Just check that lib/ has
# something in it and warn if not.
lib_populated=false
if [[ -d "$SOURCE_LIB" ]] && [[ -n "$(ls -A "$SOURCE_LIB" 2>/dev/null | grep -v -E '^(README\.md|\.gitignore)$')" ]]; then
    lib_populated=true
fi
if [[ "$lib_populated" != "true" ]]; then
    echo "WARNING: lib/ is empty or missing."
    echo "         The build will succeed but native libprojectM binaries"
    echo "         will not be staged in the drop-in zips."
    echo ""
    echo "         To populate lib/:"
    echo "           1. Go to https://github.com/RobG66/Avalonia.ProjectM/releases"
    echo "           2. Download libprojectM-win-x64.zip (or libprojectM-linux-x64.tar.gz)"
    echo "           3. Extract into lib/"
    echo "         See lib/README.md for details."
    echo ""
fi

# ── Clean previous builds ───────────────────────────────────────────
echo "Cleaning previous builds..."
dotnet clean "Avalonia.ProjectM.csproj" --configuration Release >/dev/null 2>&1 || true
rm -rf ./publish/win-x64 ./publish/linux-x64 ./publish/stage
rm -f ./publish/ProjectM-Avalonia-win-x64.zip ./publish/ProjectM-Avalonia-linux-x64.zip ./publish/ProjectM-Avalonia.zip
echo ""

# ── Build managed wrappers for both runtimes ─────────────────────────────
# Avalonia.ProjectM.dll is pure IL — identical on every platform.
# We build for both RIDs so the RID-specific deps.json is generated.

# --- Windows x64 ---
echo "Building Windows x64..."
echo "-----------------------"
dotnet build "Avalonia.ProjectM.csproj" \
    --configuration Release \
    --runtime win-x64 \
    -p:DebugType=None \
    -p:DebugSymbols=false
win_success=$?
echo ""

# --- Linux x64 ---
echo "Building Linux x64..."
echo "---------------------"
dotnet build "Avalonia.ProjectM.csproj" \
    --configuration Release \
    --runtime linux-x64 \
    -p:DebugType=None \
    -p:DebugSymbols=false
linux_success=$?
echo ""

if [ $win_success -ne 0 ] || [ $linux_success -ne 0 ]; then
    echo "================================" >&2
    echo "Build FAILED - staging skipped." >&2
    exit 1
fi
echo ""

# ── Stage into publish/ ─────────────────────────────────────────────
echo "Staging into publish/..."
echo "------------------------"

for rid in win-x64 linux-x64; do
    dest="./publish/$rid"
    dst_plugins="$dest/Avalonia.ProjectM"
    mkdir -p "$dst_plugins"

    # 1) Native libraries from the source lib/ folder
    if [[ "$lib_populated" == "true" ]]; then
        for entry in "$SOURCE_LIB"/*; do
            name="$(basename "$entry")"
            case "$name" in
                README.md|.gitignore) continue ;;
            esac
            
            # Clean up platform-incompatible native binaries
            if [ "$rid" = "win-x64" ]; then
                # Exclude Linux/macOS binaries
                case "$name" in
                    *.so|*.so.*|*.dylib) continue ;;
                esac
            elif [ "$rid" = "linux-x64" ]; then
                # Exclude Windows binaries
                case "$name" in
                    *.dll|*.pdb|*.exe) continue ;;
                esac
            fi
            
            cp -r "$entry" "$dst_plugins/"
        done
        echo "  Staged native libs for $rid"
    else
        echo "  Skipped native libs for $rid: source lib/ is empty"
    fi

    # 2) Managed wrapper — copy from the build output (bin/Release/net10.0/<rid>/).
    build_output="./bin/Release/net10.0/$rid"
    for file in Avalonia.ProjectM.dll Avalonia.ProjectM.deps.json; do
        if [[ -f "$build_output/$file" ]]; then
            cp "$build_output/$file" "$dst_plugins/"
            echo "  Copied for $rid: $file"
        fi
    done

    # 3) ProjectM preset data copy has been disabled to prevent long build times and large ZIP sizes.
done
echo ""

# ── Zip for distribution ────────────────────────────────────────────
echo "Creating zip packages..."
echo "------------------------"

for rid in win-x64 linux-x64; do
    dest="./publish/$rid"
    zip_path="./publish/ProjectM-Avalonia-$rid.zip"
    rm -f "$zip_path"
    
    if [[ -d "$dest" ]]; then
        ( cd "$dest" && zip -r "$OLDPWD/$zip_path" . >/dev/null )
        echo "Zip created for $rid:"
        echo "  $(cd "$(dirname "$zip_path")" && pwd)/$(basename "$zip_path")"
        
        if command -v du >/dev/null 2>&1; then
            zip_size=$(du -m "$zip_path" | cut -f1)
            echo "  Size: ${zip_size} MB"
        fi
    fi
done
echo ""

# ── Summary ─────────────────────────────────────────────────────────
echo "========================================================="
echo "Stage & Zip Complete"
echo "========================================================="
echo ""

if [ $win_success -eq 0 ]; then
    echo "  Windows x64 : OK -> ./publish/ProjectM-Avalonia-win-x64.zip"
else
    echo "  Windows x64 : FAILED"
fi

if [ $linux_success -eq 0 ]; then
    echo "  Linux x64   : OK -> ./publish/ProjectM-Avalonia-linux-x64.zip"
else
    echo "  Linux x64   : FAILED"
fi
echo ""

exit 0
