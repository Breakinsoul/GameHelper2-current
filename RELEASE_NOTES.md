# Release Notes

## v2.3.1

This local release focuses on diagnostics, offset-search workflow, UI polish, and stability.

### Highlights

- Hitch detector in Performance Stats for diagnosing overlay stalls.
- OffsetSearch plugin and console OffsetSearchTool.
- Cached offset results reused between launches.
- Starter PreloadAlert list included.
- Launcher no longer blocks on folder-name warning.

### Build

```powershell
dotnet build GameOverlay.sln -c Release
```

### Runtime

Main executable:

```text
GameHelper\bin\Release\net10.0-windows\win-x64\GameHelper.exe
```

### Notes

- GitHub repository publishing still needs a target remote repository URL.
- If a runtime plugin DLL is locked by a running GameHelper process, close GameHelper before rebuilding.
