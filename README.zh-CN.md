# Huorong-ACE

[English documentation](README.md)

[![CI](https://github.com/FIOIU8/Huorong-ACE/actions/workflows/ci.yml/badge.svg)](https://github.com/FIOIU8/Huorong-ACE/actions/workflows/ci.yml)

Huorong-ACE 是一个面向 Windows 的火绒日志监控工具：它读取火绒隔离区数据库，在发现新的威胁记录时通过托盘通知和全屏红屏提示用户。

当前版本的主线实现使用 C#、.NET 8 和 WinUI 3。仓库中的 Go 源码、`go.mod`、`go.sum` 以及 `backup/huorong-ace-go-baseline.zip` 是迁移前的历史参考，不参与当前 .NET 构建。

## 功能

- 监控火绒 `QuarantineEx.db` 中的 `FilesV3_60` 威胁记录。
- 使用 rowid、时间水位和去重窗口，避免历史记录或重复记录连续弹窗。
- 提供系统托盘菜单、设置窗口、系统通知和全屏红屏覆盖层。
- 保持与旧 Go 版本兼容的 JSON 配置字段名称。

## 环境要求

- Windows 10 1809（Build 17763）或更高版本。
- .NET SDK 8.x。
- 构建 WinUI 资源需要 Visual Studio 2022 Build Tools 的 Appx/PRI MSBuild 任务。
- x64 架构。

应用使用自包含目录部署，目标机器不需要另外安装 .NET Runtime 或 Windows App SDK Runtime。

## 构建与测试

```powershell
dotnet restore HuorongAce.sln
dotnet build HuorongAce.sln --configuration Release
dotnet test HuorongAce.sln --configuration Release
```

如果 Visual Studio Build Tools 不在默认位置，可以设置工具根目录：

```powershell
$env:VS_BUILDTOOLS_PATH = 'C:\Path\To\VisualStudioBuildTools'
dotnet build HuorongAce.sln --configuration Release
```

生成 Windows x64 文件夹发布包：

```text
build.bat Release folder
```

发布目录为 `dist/HuorongAce-folder/`，其中包含 `HuorongAce.exe` 和所需运行时文件。`config.json` 会在首次运行或保存设置时生成，不应提交到仓库。

## GitHub Actions

- `CI` 会在所有分支的每次 push 和 pull request 上执行 restore、Release 构建和测试。
- `Draft Release` 通过 GitHub Actions 的 **Run workflow** 手动运行。输入形如 `v1.0.0` 的版本号后，工作流会构建 Windows x64 ZIP，并创建带自动 Release Notes 的草稿 Release。
- 草稿 Release 不会自动公开发布。检查构建包和说明后，可以在 GitHub 页面手动发布。

## 项目结构

| 路径 | 说明 |
| --- | --- |
| `src/HuorongAce.Core` | 配置、日志、隔离区读取和监控逻辑 |
| `src/HuorongAce.Native` | Win32、托盘图标、通知和红屏覆盖层 |
| `src/HuorongAce.App` | WinUI 3 应用入口和设置界面 |
| `src/*Tests` | Core 与 Native 的自动化测试 |
| `internal/`、`main.go` | 旧 Go 实现，仅作迁移参考 |
| `tools/` | 发布过程使用的辅助脚本 |

## 许可证

本项目以 [MIT License](LICENSE) 发布。版权所有 (c) 2026 FIOIU8。
