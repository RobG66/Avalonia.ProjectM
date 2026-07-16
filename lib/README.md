# lib/ — native runtime libraries for ProjectM

The libprojectM native binary (and GLEW on Windows) go in this
folder, flat — no subfolders. Windows `.dll` and Linux `.so` files
coexist by extension; the loader code in `Native/ProjectMNative.cs`
picks the right filename per OS at runtime.

This folder is intentionally empty in the repository. You must
populate it manually before building the drop-in zip. The easiest
way is to download the pre-built binaries from the GitHub release
produced by the `build-natives.yml` CI workflow.

---

## How to populate lib/

### Option A: Download from the GitHub release (recommended)

The `build-natives.yml` workflow builds libprojectM from source and
publishes the binaries to a GitHub release on this repo. This is the
recommended way — the binaries are built reproducibly from a known
upstream commit, and LICENSE files are bundled.

1. Go to: https://github.com/RobG66/Avalonia.ProjectM/releases
2. Find the latest "Native dependencies" release.
3. Download the asset for your platform:
   - Windows: `Avalonia.ProjectM-win-x64.zip`
   - Linux:   `Avalonia.ProjectM-linux-x64.tar.gz`
4. Extract the archive into this `lib/` folder. The archive contains:
   - `libprojectM.dll` (Windows) or `libprojectM.so.4` (Linux)
   - `glew32.dll` (Windows only)
   - `libprojectM-LICENSE.txt`
   - `glew-LICENSE.txt` (Windows only)
   - `BUILD-INFO.txt` (records the upstream commit + build time)

### Option B: Build libprojectM from source yourself

If you want to build from source (e.g. to use a different projectM
version or to apply patches), trigger the `build-natives.yml` workflow
manually:

1. Go to: https://github.com/RobG66/Avalonia.ProjectM/actions
2. Click "Build native dependencies" → "Run workflow".
3. Wait for the build to complete (~10 minutes).
4. Download the resulting release asset and extract into `lib/` as above.

To build a different version of libprojectM, edit `PROJECTM_REF` in
`.github/workflows/build-natives.yml` before triggering the workflow.

---

## What lives in lib/ after extraction

### Windows
| File | Description | License |
|------|-------------|---------|
| `libprojectM.dll` | ProjectM visualizer engine | LGPL v2.1+ |
| `glew32.dll` | OpenGL Extension Wrangler (required by libprojectM) | BSD 3-Clause / MIT |
| `libprojectM-LICENSE.txt` | LGPL license text (required for redistribution) | — |
| `glew-LICENSE.txt` | BSD/MIT license text | — |
| `BUILD-INFO.txt` | Records the upstream commit + build time | — |

### Linux
| File | Description | License |
|------|-------------|---------|
| `libprojectM.so.4` | ProjectM visualizer engine | LGPL v2.1+ |
| `libprojectM-LICENSE.txt` | LGPL license text (required for redistribution) | — |
| `BUILD-INFO.txt` | Records the upstream commit + build time | — |

---

## After populating lib/

Once `lib/` contains the libprojectM binary, run the build script to
produce the drop-in zip:

```bash
# Windows
.\build.ps1

# Linux / macOS
./build.sh
```

The build script:
1. Builds `Avalonia.ProjectM.dll` (the managed wrapper) for both
   `win-x64` and `linux-x64` RIDs.
2. Stages the managed wrapper + the contents of `lib/` into a single
   `lib/` subfolder in the output.
3. Stages the preset data from `ProjectM/` into a `ProjectM/` subfolder.
4. Produces zip packages under the `publish/` directory.

The user unzips this archive into their Jukebox build output directory
to enable visualizations.

---

## Why we don't commit these to git

libprojectM is LGPL v2.1+. Distributing an LGPL binary carries
obligations: include the LICENSE, make the corresponding source
available, and let users replace the library. Building from source in
CI — with the upstream commit hash recorded in the workflow file —
satisfies all three: the build is reproducible, the LICENSE file
travels with the binary, and Jukebox loads the library dynamically
(so users can swap it).

See [../THIRD_PARTY_LICENSES.md](../THIRD_PARTY_LICENSES.md) for the
full licensing breakdown.
