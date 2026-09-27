# Huorong-ACE

[中文文档](README.zh-CN.md)

[![CI](https://github.com/FIOIU8/Huorong-ACE/actions/workflows/ci.yml/badge.svg)](https://github.com/FIOIU8/Huorong-ACE/actions/workflows/ci.yml)

Huorong-ACE is a Windows utility that monitors Huorong quarantine records and alerts the user when a new threat is detected. It provides a system tray icon, Windows notifications, and a full-screen red warning overlay.

The current mainline implementation uses C#, .NET 8, and WinUI 3. The Go source code, `go.mod`, `go.sum`, and `backup/huorong-ace-go-baseline.zip` are kept as historical migration references and are not part of the current .NET build.

## Features

- Monitors threat records in Huorong's `QuarantineEx.db` and `FilesV3_60` table.
- Uses row ID cursors, timestamp watermarks, and a de-duplication window to avoid repeated alerts.
- Provides a system tray menu, settings window, Windows notifications, and a full-screen warning overlay.
- Keeps the JSON configuration field names compatible with the legacy Go version.

## Requirements

- Windows 10 version 1809 (build 17763) or later.
- .NET SDK 8.x.
- Visual Studio 2022 Build Tools with the Appx/PRI MSBuild tasks for WinUI resource generation.
- x64 architecture.

The application is published as a self-contained folder deployment, so the target machine does not need a separate .NET or Windows App SDK runtime installation.

## Build and test

```powershell
dotnet restore HuorongAce.sln
dotnet build HuorongAce.sln --configuration Release
dotnet test HuorongAce.sln --configuration Release
```

If Visual Studio Build Tools are installed outside the default location, set the tool root before building:

```powershell
$env:VS_BUILDTOOLS_PATH = 'C:\Path\To\VisualStudioBuildTools'
dotnet build HuorongAce.sln --configuration Release
```

To create a Windows x64 folder deployment:

```text
build.bat Release folder
```

The deployment is written to `dist/HuorongAce-folder/` and contains `HuorongAce.exe` plus its runtime files. `config.json` is generated when the application first runs or saves settings and must not be committed.

## GitHub Actions

- `CI` runs restore, Release builds, and tests on every push and pull request for every branch.
- `Draft Release` is started manually with **Run workflow**. Enter a version such as `v1.0.0` to build a Windows x64 ZIP and create a draft release with generated release notes.
- Draft releases are not published automatically. Review the package and notes in GitHub, then publish the release manually.

## Project layout

| Path | Description |
| --- | --- |
| `src/HuorongAce.Core` | Configuration, logging, quarantine reading, and monitoring logic |
| `src/HuorongAce.Native` | Win32 integration, tray icon, notifications, and warning overlay |
| `src/HuorongAce.App` | WinUI 3 application entry point and settings UI |
| `src/*Tests` | Core and Native automated tests |
| `internal/`, `main.go` | Legacy Go implementation kept for migration reference |
| `tools/` | Helper scripts used by the publishing process |

## License

This project is released under the [MIT License](LICENSE). Copyright (c) 2026 FIOIU8.
