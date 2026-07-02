#!/usr/bin/env bash
#
# Builds the Jukebox-Visualizations wrapper and stages a self-contained
# drop-in layout suitable for placing next to Jukebox.exe.
# Bash equivalent of build.ps1 for Linux/macOS developers.
#
# JukeboxVisualizations.dll is pure IL — identical on every platform.
# We use `dotnet build` (not `dotnet publish`) so only the project's own
# DLL and deps.json land in bin/Release/net10.0/, with no transitive
# Avalonia or Silk.NET DLLs copied alongside.
#
# Run from the Jukebox-Visualizations project root (next to
# JukeboxVisualizations.csproj).
#
set -euo pipefail

cd "$(dirname "$0")"

echo "Building Jukebox-Visualizations"
echo "================================"
echo ""

# ── Validate source layout ──────────────────────────────────────────
if [[ ! -f "JukeboxVisualizations.csproj" ]]; then
    echo "ERROR: JukeboxVisualizations.csproj not found in current directory." >&2
    echo "       Run this script from the Jukebox-Visualizations project root." >&2
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
    echo "         will not be staged in the drop-in zip."
    echo ""
    echo "         To populate lib/:"
    echo "           1. Go to https://github.com/RobG66/Jukebox-Visualizations/releases"
    echo "           2. Download libprojectM-win-x64.zip (or libprojectM-linux-x64.tar.gz)"
    echo "           3. Extract into lib/"
    echo "         See lib/README.md for details."
    echo ""
fi

# ── Clean previous builds ───────────────────────────────────────────
echo "Cleaning previous builds..."
dotnet clean "JukeboxVisualizations.csproj" --configuration Release >/dev/null 2>&1 || true
rm -rf ./publish
mkdir -p ./publish/stage
echo ""

# ── Build managed wrapper ──────────────────────────────────────────────────
echo "Building JukeboxVisualizations.dll..."
echo "--------------------------------------"

dotnet build "JukeboxVisualizations.csproj" \
    --configuration Release \
    -p:DebugType=None \
    -p:DebugSymbols=false

if [ $? -ne 0 ]; then
    echo "================================" >&2
    echo "Build FAILED - staging skipped." >&2
    exit 1
fi
echo ""

# ── Stage into publish/stage/ ───────────────────────────────────────
echo "Staging into ./publish/stage/..."
echo "--------------------------------"

DEST="./publish/stage"
mkdir -p "$DEST/lib"

# 1) Native libraries from the source lib/ folder — flat, all platforms
#    coexist by extension. Goes into <dest>/lib/.
if [[ "$lib_populated" == "true" ]]; then
    for entry in "$SOURCE_LIB"/*; do
        name="$(basename "$entry")"
        case "$name" in
            README.md|.gitignore) continue ;;
        esac
        cp -r "$entry" "$DEST/lib/"
    done
    echo "  Copied native libs: lib/"
else
    echo "  Skipped native libs: source lib/ is empty"
fi

# 2) Managed wrapper — copy from the build output (bin/Release/net10.0/).
#    Also copy deps.json so the runtime can resolve Avalonia + Silk.NET
#    dependencies when loaded via Assembly.LoadFrom.
build_output="./bin/Release/net10.0"
for file in JukeboxVisualizations.dll JukeboxVisualizations.deps.json; do
    if [[ -f "$build_output/$file" ]]; then
        cp "$build_output/$file" "$DEST/lib/"
        echo "  Copied: lib/$file"
    fi
done

# 3) ProjectM preset data copy has been disabled to prevent long build times and large ZIP sizes.

echo ""

# ── Summary ─────────────────────────────────────────────────────────
echo "================================"
echo "Stage Complete"
echo "================================"
echo ""
echo "Drop-in layout staged at:"
echo "  $(cd "$DEST" && pwd)"
echo ""
echo "Layout:"
( cd "$DEST" && find . -maxdepth 2 -print | sort | while read -r p; do
    p="${p#./}"
    [[ -z "$p" ]] && continue
    if [[ -d "$DEST/$p" ]]; then
        echo "  [DIR] $p"
    else
        echo "        $p"
    fi
done )
echo ""

# ── Zip for distribution ────────────────────────────────────────────
zip_path="./publish/Jukebox-Visualizations-dropin.zip"
rm -f "$zip_path"
( cd "$DEST" && zip -r "$OLDPWD/$zip_path" . >/dev/null )
echo "Zip created:"
echo "  $(cd "$(dirname "$zip_path")" && pwd)/$(basename "$zip_path")"
zip_size=$(du -m "$zip_path" | cut -f1)
echo "  Size: ${zip_size} MB"
echo ""
echo "To enable visualizations in Jukebox:"
echo "  1. Unzip the archive into your Jukebox build output directory"
echo "     (next to Jukebox.exe). The result is:"
echo "         lib/                          (wrapper + native runtimes)"
echo "  2. Also drop Jukebox's own native runtimes (bass.dll, libmpv-2.dll"
echo "     or libbass.so, libmpv.so.2) into the same lib/ folder."
echo "  3. Restart Jukebox. The visualizer button appears in the transport"
echo "     bar automatically."
echo ""

exit 0
