# Third-Party Licenses

This document lists the third-party libraries, native binaries, and
NuGet packages used by the Avalonia.ProjectM project, along with
their licenses and where to obtain the source. It exists to make
licensing obligations explicit and to ensure compliance with permissive
and copyleft license terms.

For the avoidance of doubt: **Avalonia.ProjectM does not commit
third-party native binaries (`.dll` / `.so` / `.dylib`) to this
repository.** They are downloaded manually by the developer from the
GitHub release produced by the `build-natives.yml` CI workflow, then
placed in the `lib/` folder. See [README.md](README.md) and
[`lib/README.md`](lib/README.md) for details.

---

## Native runtime libraries (dropped into `lib/`)

These are unmanaged native binaries loaded at runtime by
`Native/ProjectMNative.cs`. They are NOT committed to the repo — the
developer downloads and places them manually. See `lib/README.md` for
the list of required files per platform and where to download each one.

### ProjectM (`libprojectM.dll` / `libprojectM.so.4`)

* **Website:** https://github.com/projectM-visualizer/projectm
* **License:** **LGPL v2.1+**.
* **License file:** https://github.com/projectM-visualizer/projectm/blob/main/LICENSE
* **Source availability:** Open source.
* **Build:** The `libprojectM.dll` / `libprojectM.so.4` artifact
  distributed via this project's GitHub releases is built from upstream
  source by the `build-natives.yml` CI workflow. The workflow file
  records the exact upstream commit/branch built, making the build
  reproducible.
* **Redistribution obligations (LGPL v2.1):**
  1. The `LICENSE` file from the upstream projectM source must
     accompany the binary in the distribution. The CI workflow bundles
     it as `libprojectM-LICENSE.txt` inside the release archive.
  2. The source code corresponding to the exact binary build must be
     available for at least 3 years after the binary is distributed.
     This is satisfied by the upstream projectM repository (the
     workflow records the commit hash) and by the workflow file itself,
     which can be re-run to reproduce the binary.
  3. The binary must be clearly identified as LGPL-licensed in the
     distribution.
  4. The user must be able to replace the LGPL library with a modified
     version. Because Jukebox loads `libprojectM` via
     `NativeLibrary.Load` at runtime (not statically linked), this
     requirement is satisfied — the user can drop in a replacement
     `libprojectM.dll` / `libprojectM.so.4` in the `lib/` folder.

### GLEW (OpenGL Extension Wrangler Library) — `glew32.dll`

* **Website:** https://glew.sourceforge.net/
* **License:** BSD 3-Clause + MIT (dual licensed).
* **License file:** https://github.com/nigels-com/glew/blob/master/LICENSE.txt
* **Source availability:** Open source.
* **Redistribution:** Allowed, including in binary form, provided the
  copyright notice and license are included. No copyleft obligation.

  Required only on Windows — `libprojectM.dll` depends on it at load
  time. The CI workflow bundles `glew-LICENSE.txt` alongside.

---

## NuGet packages (managed assemblies)

These are .NET assemblies pulled in via `<PackageReference>` in
`JukeboxVisualizations.csproj`. They ship with their own license files
in the NuGet package itself.

### Avalonia (`Avalonia`)

* **Website:** https://avaloniaui.net/
* **License:** MIT License.
* **Source:** https://github.com/AvaloniaUI/Avalonia

### Silk.NET.OpenGL (`Silk.NET.OpenGL`)

* **Website:** https://github.com/dotnet/Silk.NET
* **License:** MIT License.
* **Source:** https://github.com/dotnet/Silk.NET

---

## Build-time dependencies (not redistributed)

These are tools used by the `build-natives.yml` CI workflow to build
`libprojectM` from source. They are not distributed with
Avalonia.ProjectM — they exist only in the CI build environment.

### CMake

* **Website:** https://cmake.org/
* **License:** BSD 3-Clause.
* **Used by:** `build-natives.yml` to configure the libprojectM build.

### C++ compiler (MSVC on Windows, gcc on Linux)

* Provided by the GitHub Actions runner image. Not redistributed.

---

## Updating this file

When adding a new third-party dependency (native binary or NuGet
package), add an entry to the appropriate section above. Include:

1. Name and version (if pinned).
2. Website or source URL.
3. License name and link to the license text.
4. Whether source is available.
5. Any redistribution obligations or caveats.

When in doubt, prefer over-disclosure. This file is part of the legal
record of what the project ships.
