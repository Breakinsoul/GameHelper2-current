# GameHelper2 Current

GameHelper2 is a Windows x64 .NET overlay application with a launcher, GitHub-based updater, ImGui settings UI, and plugin runtime.

Current release stream:

- Repository: `Breakinsoul/GameHelper2-current`
- Current app version: `2.3.12`
- Latest release asset pattern: `GameHelper*.zip`
- Update configuration: `Launcher/updater.json`

The launcher checks GitHub Releases, downloads the latest matching ZIP, applies the update through the launcher-native `--apply-update` mode, and restarts the application.

## Version History

### v2.3.12

- Added shared plugin runtime helpers for path filters, interpolation caches, screen projection, and screen bounds checks.
- Improved Radar settings UX with filter modes, quick prefix disable actions, and performance mode.
- Improved HealthBars settings with visibility presets and shared cache/screen helpers.
- Improved EncounterHelper rules table with live per-rule match counters and shared wildcard matching.
- Improved AtlasHelper with label modes and a searchable live node table.
- Improved RuneshapePriceChecker settings with compact runtime/OCR status.

### v2.3.11

- Published the current source snapshot.
- Bumped application metadata and README release instructions to `2.3.11`.
- Included current plugin changes in the source tree.

### v2.3.10

- Updated current game offsets for player, entity lists, and terrain metadata.
- Restored Radar and HealthBars entity reads after the game patch.
- Added Radar default ignored entity path filters and wildcard prefix matching.
- Disabled temporary diagnostics in the release package.

### v2.3.9

- Current release stream baseline before the latest offset and plugin UX updates.

## Requirements

- Windows x64
- Visual Studio 2022 or newer
- Visual Studio workload: `.NET desktop development`
- .NET 10 SDK for Windows x64
- Internet access for NuGet restore and GitHub update checks

If Visual Studio reports that `net10.0-windows` is unsupported, update Visual Studio and install the .NET 10 SDK.

## Repository Layout

```text
GameHelper2/
  GameHelper/              Main overlay application
  GameOffsets/             Offset and native structure definitions
  Launcher/                Launcher and update logic
  Plugins/                 Plugin source projects and assets
  GameOverlay.sln          Visual Studio solution
  Directory.Build.props    Shared build settings
  NuGet.config             NuGet source configuration
```

Important files:

```text
GameHelper/GameHelper.csproj
Launcher/Launcher.csproj
Launcher/AutoUpdate.cs
Launcher/updater.json
GameOverlay.sln
```

## Update Configuration

The updater is configured in:

```text
Launcher/updater.json
```

Current configuration:

```json
{
  "repository": "Breakinsoul/GameHelper2-current",
  "assetPattern": "^GameHelper.*\\.zip$",
  "allowPrerelease": false
}
```

Meaning:

- `repository` is the GitHub repository used for update checks.
- `assetPattern` selects the ZIP asset from the latest release.
- `allowPrerelease` controls whether prerelease builds can be used.

The release ZIP must contain `GameHelper.exe`, `Launcher.exe`, runtime DLLs, `updater.json`, and the built `Plugins` folder.

## Build From Visual Studio

1. Open `GameOverlay.sln`.
2. Select `Release`.
3. Keep platform as `Any CPU`.
4. Use `Build > Rebuild Solution`.
5. Wait for `Build succeeded`.

Do not build only `GameHelper.csproj` for release packaging. The full solution build is required because the launcher and plugins copy their outputs into the main runtime folder.

## Build From Command Line

From the repository root:

```powershell
dotnet build GameOverlay.sln -c Release
```

Expected successful result:

```text
Build succeeded.
0 Warning(s)
0 Error(s)
```

## Runtime Output

Release output is created at:

```text
GameHelper/bin/Release/net10.0-windows/win-x64/
```

Expected files:

```text
GameHelper.exe
Launcher.exe
GameHelper.dll
GameOffsets.dll
updater.json
Plugins/
```

Run the application from this folder using:

```text
Launcher.exe
```

`Launcher.exe` is the intended entry point. It checks for updates before starting `GameHelper.exe`.

## Plugins

The current workspace contains these plugin folders:

```text
Plugins/AreaTracker
Plugins/AtlasHelper
Plugins/AtlasNoFog
Plugins/AutoHotKeyTrigger
Plugins/EncounterHelper
Plugins/HealthBars
Plugins/PreloadAlert
Plugins/Radar
Plugins/RuneshapePriceChecker
Plugins/SamplePluginTemplate
Plugins/WorldDrawing
```

Projects included in `GameOverlay.sln` are built and copied into the runtime `Plugins` folder. Template or source-only plugin folders may exist in the repository without being included in the final runtime package.

Runtime plugin output example:

```text
GameHelper/bin/Release/net10.0-windows/win-x64/Plugins/Radar/Radar.dll
GameHelper/bin/Release/net10.0-windows/win-x64/Plugins/HealthBars/HealthBars.dll
GameHelper/bin/Release/net10.0-windows/win-x64/Plugins/AutoHotKeyTrigger/AutoHotKeyTrigger.dll
```

## Create A Release ZIP

After a successful release build, package the main runtime output folder:

```powershell
$out = Resolve-Path "GameHelper\bin\Release\net10.0-windows\win-x64"
New-Item -ItemType Directory -Force -Path artifacts | Out-Null
Compress-Archive -Path (Join-Path $out "*") -DestinationPath "artifacts\GameHelper-v2.3.12.zip" -Force
```

The ZIP should contain the application root directly, not a nested parent folder.

Correct:

```text
GameHelper.exe
Launcher.exe
Plugins/Radar/Radar.dll
updater.json
```

Wrong:

```text
GameHelper-v2.3.12/GameHelper.exe
```

## Publish A GitHub Release

Example using GitHub CLI:

```powershell
gh release create v2.3.12 artifacts\GameHelper-v2.3.12.zip `
  -R Breakinsoul/GameHelper2-current `
  --title "v2.3.12 current release" `
  --notes "Update offsets for the current game patch." `
  --latest
```

The updater reads the latest non-prerelease GitHub release and downloads the first asset matching:

```text
^GameHelper.*\.zip$
```

## Test Updating

1. Install or run an older release, for example `v2.3.5`.
2. Publish a newer release, for example `v2.3.12`.
3. Start the older app through `Launcher.exe`.
4. The launcher checks GitHub Releases.
5. If the latest release version is newer, the launcher downloads the ZIP.
6. The launcher starts its update runner with `--apply-update`.
7. Files are copied into the install folder.
8. The app restarts from the updated folder.

If the app is already running, close it before replacing files manually. The updater is designed to wait for the launcher process and then apply the copied payload.

## Runtime Configuration

Runtime settings are generated next to the executable:

```text
configs/core_settings.json
configs/plugins.json
Plugins/<PluginName>/config/
```

These files are local runtime data and are ignored by Git.

## Troubleshooting

### The updater downloads an old version

Check `Launcher/updater.json` in the running app folder. It must point to:

```text
Breakinsoul/GameHelper2-current
```

Also check that the newest GitHub Release is marked as latest and has a ZIP asset named like:

```text
GameHelper-v2.3.12.zip
```

### GitHub shows old source code

The local workspace is not necessarily a git repository. Publish from the live workspace snapshot, not from an older temporary clone.

### Build succeeded but plugins are missing

Use `dotnet build GameOverlay.sln -c Release` or Visual Studio `Rebuild Solution`. Building only the main project can skip plugin copy targets.

### Launcher says GameHelper.exe was not found

Run `Launcher.exe` from:

```text
GameHelper/bin/Release/net10.0-windows/win-x64/
```

Do not run the launcher from `Launcher/bin/...`; the copied launcher must be next to `GameHelper.exe`.

### Overlay does not attach

Run the app with the same privilege level as the target process. If the target process is elevated, run GameHelper as administrator too.

### Antivirus flags the updater

The current updater avoids PowerShell script-based replacement and uses launcher-native `--apply-update` mode. If security software still flags a build, verify the exact detected file and submit the release ZIP or executable for vendor review.

## Useful Links

- Visual Studio: https://visualstudio.microsoft.com/downloads/
- .NET downloads: https://dotnet.microsoft.com/download
- GitHub Releases: https://github.com/Breakinsoul/GameHelper2-current/releases
