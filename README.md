# Avalonia.ProjectM

**Avalonia.ProjectM** is a fully self-contained library for Avalonia UI applications that provides high-performance, OpenGL-accelerated audio visualizations via the [libprojectM](https://github.com/projectM-visualizer/projectm) engine.

It provides a plug-and-play solution for integrating stunning, music-reactive visuals into any compatible Avalonia application. Included in this repository is the complete suite of native bindings and a massive library of `.milk` presets and textures, ensuring you have an endless variety of dynamic visuals instantly available.

---

## ⚙️ How It Works

This project bridges unmanaged native C++ code with managed C# UI elements, operating across two primary layers:

1. **Native Interop (`Native/ProjectMNative.cs`)** — A comprehensive P/Invoke wrapper that interfaces with the C-based `libprojectM` API. It is responsible for managing the unmanaged lifecycle (creating/destroying instances), handling rendering contexts, loading `.milk` presets, and feeding raw PCM audio data directly into the visualization engine's beat detection algorithms.
2. **Avalonia Control (`Controls/ProjectMControl.cs`)** — A custom Avalonia `OpenGlControlBase` component that drops directly into your UI. It takes complete ownership of acquiring an OpenGL hardware-accelerated surface via `Silk.NET.OpenGL` and delegates the actual drawing instructions directly to the unmanaged ProjectM engine, compositing the result beautifully into the Avalonia rendering pipeline.

---

## 🛠️ Dependencies

This library targets **.NET 10.0** and relies on the following core frameworks:
* **Avalonia UI** (v12.x) for the cross-platform rendering engine.
* **Silk.NET.OpenGL** (v2.x) for hardware-accelerated OpenGL context bindings.

The native `libprojectM` binaries (and the **GLEW** extension wrangler `glew32.dll` on Windows) are required at runtime. They must reside in the same folder as the compiled assembly (`Avalonia.ProjectM.dll`) inside your deployment directory.

---

## 📁 Project Layout

```
<projm-av-root>/
├── Avalonia.ProjectM.csproj        # Managed control library project
├── Controls/
│   └── ProjectMControl.cs          # Avalonia OpenGlControlBase wrapper
├── Native/
│   └── ProjectMNative.cs           # P/Invoke declarations for libprojectM
├── lib/                            # ← Native runtime libraries (populated post-fetch)
│   ├── README.md                   # Instructions for populating lib/
│   └── .gitignore                  # Ignores binaries, keeps README
├── ProjectM/                       # ← Preset files shipped with the repo
│   ├── presets/
│   │   └── (... preset .milk files)
│   └── textures/
├── build.ps1                       # Windows build script (stages zip)
├── build.sh                        # Linux/macOS build script (same)
├── THIRD_PARTY_LICENSES.md         # Licensing details for libprojectM, GLEW, etc.
├── .github/workflows/
│   └── build-natives.yml           # CI: builds libprojectM from source → GitHub release
└── README.md                       # This file
```

---

## 📥 Fetching Native Dependencies

The `lib/` folder must be populated with `libprojectM.dll` / `libprojectM.so.4` (+ `glew32.dll` on Windows) before running the build scripts.

### Steps

1. Go to your repository's Releases page on GitHub.
2. Find the latest **Native dependencies** release.
3. Download the asset for your platform:
   - Windows: `libprojectM-win-x64.zip`
   - Linux:   `libprojectM-linux-x64.tar.gz`
4. Extract into the `lib/` folder at the root of this repo. The archive extracts flat — you should end up with:
   - `lib/libprojectM.dll` (Windows) or `lib/libprojectM.so.4` (Linux)
   - `lib/glew32.dll` (Windows only)
   - `lib/libprojectM-LICENSE.txt`
   - `lib/glew-LICENSE.txt` (Windows only)
   - `lib/BUILD-INFO.txt`

### Updating libprojectM

The native binaries are built from upstream projectM source by the `build-natives.yml` CI workflow. To update to a newer version of projectM:

1. Edit `PROJECTM_REF` in [.github/workflows/build-natives.yml](.github/workflows/build-natives.yml) to point at the new upstream tag (e.g., `'v4.1.7'`).
2. Commit and push the workflow file.
3. Run the workflow manually from the GitHub Actions tab.
4. Download the newly published native release assets and place them in the local `lib/` folder.

---

## 🔨 Packaging and Build

The repository ships two build scripts:

| Script | Platform | Description |
|--------|----------|-------------|
| `build.ps1` | Windows | PowerShell script — builds for win-x64 and linux-x64, stages into `publish/stage/`, zips for distribution. |
| `build.sh`  | Linux / macOS | Bash equivalent of `build.ps1`. |

Both scripts produce the standard staged folder structure under `./publish/stage/`:

```text
publish/stage/
└── Avalonia.ProjectM/
    ├── Avalonia.ProjectM.dll       # Managed control library
    ├── Avalonia.ProjectM.deps.json # Dependency manifest
    ├── libprojectM.dll             (Windows native library)
    ├── libprojectM.so.4            (Linux native library)
    └── glew32.dll                  (Windows only native dependency)
```

A `ProjectM-Avalonia-<platform>.zip` is produced inside `./publish/` for easy distribution. When unzipped in your host application's output directory, it creates the standalone `Avalonia.ProjectM/` directory containing all library assets.

### Staging Build (Windows)
```powershell
.\build.ps1
```

### Staging Build (Linux / macOS)
```bash
./build.sh
```

---

## 🚀 How to Use in Your Project

To integrate `ProjectMControl` directly into your Avalonia application:

### 1. Reference the Library
Add `Avalonia.ProjectM` project to your solution, or directly reference the compiled `Avalonia.ProjectM.dll` assembly.

### 2. Add the XAML Namespace
At the top of your Avalonia Window or UserControl (`.axaml` file), include the controls namespace:

```xml
xmlns:vis="clr-namespace:Avalonia.ProjectM.Controls;assembly=Avalonia.ProjectM"
```

### 3. Drop in the Control
Place the `ProjectMControl` anywhere in your layout. It will automatically resize to fill its parent container:

```xml
<Grid Background="Black">
    <vis:ProjectMControl Name="MyVisualizer" />
</Grid>
```

### 4. Feed it Audio Data (C#)
Feed it raw PCM audio samples. The control exposes a `FeedPcm` method that accepts `short[]` mono or stereo samples:

```csharp
// Feed short[] PCM samples (mono interleaved, or stereo interleaved)
MyVisualizer.FeedPcm(pcmSamples);
```

### 5. Control Presets (C#)
Load specific `.milk` preset files using `LoadPreset`:

```csharp
// Feed a specific .milk preset file path
MyVisualizer.LoadPreset(@"ProjectM\presets\Some Preset.milk");
```

---

## 🔗 External Links

* **[Avalonia UI](https://avaloniaui.net/)**: Cross-platform UI framework.
* **[Silk.NET](https://github.com/dotnet/Silk.NET)**: High-speed OpenGL bindings for .NET.
* **[ProjectM](https://github.com/projectM-visualizer/projectm)**: Upstream open-source music visualization engine.
