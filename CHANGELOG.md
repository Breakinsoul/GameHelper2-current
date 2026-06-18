# Changelog

All notable local changes are tracked in this file.

## [2.3.1] - 2026-06-03

### Added

- Added lightweight hitch detector for frame stalls.
- Added slowest frame phase and coroutine/plugin timing output in Performance Stats.
- Added `OffsetSearch` plugin for static-offset scanning from the GameHelper UI.
- Added `OffsetSearchTool` console scanner.
- Added automatic offset-result cache files:
  - `offset_search_results.json`
  - `offset_search_results.txt`
- Added starter `PreloadAlert/preloads.txt` list for PoE2 preload alerts.

### Changed

- Modernized the main ImGui theme and core settings status panel.
- Improved `OffsetSearch` UI and added cached result loading.
- Improved launcher startup behavior so path warnings do not block non-interactive launches.
- Moved static pattern scanning logic into shared `GameOffsets.PatternSearchEngine`.

### Fixed

- Fixed pattern scanner matching logic and duplicate/missing match validation.
- Fixed process object cleanup while scanning for game clients.
- Fixed several per-frame UI and render-loop allocation hot spots.
- Fixed several state-transition guard cases that could throw during game close/load transitions.

## [2.3.0] - 2026-05-29

### Baseline

- Imported GameHelper2 v2.3.0 source baseline.
