# Go → C# 重构说明

本文对应一次完整重构：以 Go 版本（git tag `go-baseline`，备份 `backup/huorong-ace-go-baseline.zip`）为参考，用 C# 重写，完整迁移业务逻辑，**对外行为保持一致**。

- 基线提交：`c67c7f0`（"Go 版本终稿：C# 重构前的基线存档"）
- Go 代码仍在仓库中（`main.go` + `internal/`），可随时对照与回退
- C# 代码规模：约 2820 行 C# + 236 行 XAML（Go 版设置界面单文件即约 1200 行 GDI 自绘）

---

## 1. 关键设计差异

### 1.1 项目结构：单包 → 分层三件套

| Go | C# |
| --- | --- |
| `internal/config`、`internal/monitor`、`internal/ui` 同进程单包 | `HuorongAce.Core` / `HuorongAce.Native` / `HuorongAce.App` 三个项目 |
| 通过 `//go:build windows` 区分原生实现与 Fyne 回退 | 通过目标框架区分：`net8.0`（跨平台逻辑）、`net8.0-windows`（Win32）、`net8.0-windows10.0.19041.0`（WinUI） |

分层的好处是 Core 完全不碰系统 API，因此可以脱离 Windows 单测；Native 只做窗口与通知；App 只做界面装配。

### 1.2 并发模型

| Go | C# |
| --- | --- |
| goroutine + `chan struct{}` 停止信号 + `sync.Mutex` | `Task` + `CancellationTokenSource` + `PeriodicTimer` + `lock` |
| `runtime.LockOSThread()` 固定 goroutine 跑 Win32 消息循环 | 专用 STA 线程 + 显式消息泵（`NativeWindowHost`） |
| 包级函数互相调用 | 依赖注入 + `event`/`EventHandler<T>` |

回调改为事件后，红屏、通知、状态栏可以各自独立订阅，不再需要在一个函数里串起来。

### 1.3 界面分工

| 部位 | Go | C# |
| --- | --- | --- |
| 设置界面 | Fyne → 后改为 **约 1200 行 GDI 手工自绘** | **WinUI 3**：声明式 XAML + 约 270 行代码后置 |
| 红屏 | 原生 Win32 + GDI 自绘 | 原生 Win32 + **GDI+**（`System.Drawing`） |
| 托盘图标 | 原生 `Shell_NotifyIcon` | 原生 `Shell_NotifyIcon`（不变） |
| 系统通知 | PowerShell 脚本 | PowerShell 脚本（不变） |

红屏**故意**不作为 WinUI 窗口：它需要覆盖整个虚拟桌面（多显示器 + 盖住任务栏），这是 WinUI 窗口模型表达不了的，所以沿用原生窗口，与 Go 版取舍一致。

### 1.4 具体改观

- **不可变数据**：`monitor.Info` 是 Go 的值结构体，靠调用方不修改来约定；C# 用 `sealed record ThreatInfo`，语义上不可能被改动。
- **日志接口化**：Go GUI 程序里 `log` 输出无处可见，C# 抽象为 `IAppLog`（`DebugAppLog` / `NullAppLog`），测试可注入空实现。
- **绘制**：红屏从手写 GDI 助手函数（约 200 行）改为 GDI+ —— 抗锯齿文字、真正的圆角矩形、离屏缓冲一行搞定。双缓冲（离屏 Bitmap + 一次 `DrawImage`）直接解决了 Go 版需要手工维护的闪烁问题。
- **资源释放**：Go 靠 `defer`；C# 统一 `IDisposable` / `IAsyncDisposable` + `using`。
- **配置兼容**：`config.json` 的字段名通过 `[JsonPropertyName]` 保持 `log_path` / `poll_interval_sec` / `keywords` / `monitor_enabled` 不变，**Go 版写出的配置文件可直接沿用**，并保留了对旧版硬编码路径的自动迁移。

---

## 2. 原实现中不兼容 C# 之处的处理方式

1. **`syscall.NewLazyDLL` 手工绑定**（Go 的 `x/sys/windows` 不含 GDI/USER32）
   → 改为 `[DllImport]`，字符串/结构体的封送由运行时处理，省掉约 400 行绑定代码。
   踩坑：不能用 `LibraryImport`（源生成器拒绝封送 `WndClassEx` / `PaintStruct` / `NotifyIconData`），全部保留 `DllImport` + `CharSet.Unicode`。

2. **Go 构建标签分文件**（`redscreen_windows.go` / `manage.go` 的 `!windows` 回退）
   → C# 无此机制，改为按目标框架分项目；WinUI 设置界面本身就是 Windows-only。
   踩坑：`main.go` 里 Windows 与非 Windows 定义过同名函数，Go 靠构建标签隔离；C# 天然不会重复。

3. **WinUI 自动生成入口点**
   → unpackaged 部署需要手写 `Program.Main`（初始化 COM + `DispatcherQueueSynchronizationContext`），与 XAML 编译器生成的 `App.g.i.cs` 中的 `Program` 冲突（CS0101/CS0111）。
   处理：项目里定义 `DISABLE_XAML_GENERATED_MAIN`，关闭生成版本，保留手写入口点。

4. **WinUI PRI 资源索引依赖 Visual Studio 的 MSBuild 任务**
   → `Microsoft.Build.AppxPackage.dll` / `Microsoft.Build.Packaging.Pri.Tasks.dll` 不在 .NET SDK 里，也不在任何 NuGet 包里。
   处理：安装 VS 2022 生成工具（本例装在 `C:\Users\cxy0714\.toolchain\vsbuildtools`），并在 `src/Directory.Build.props` 中把 `AppxMSBuildToolsPath` 指过去（用环境变量 `VS_BUILDTOOLS_PATH`，或命令行 `-p:AppxMSBuildToolsPath=...`）。装在默认位置时无需任何设置。

5. **WAL 模式下的火绒数据库**
   → 原样迁移，仍需"复制 db + `-wal` + `-shm` 到临时目录再读快照"；`mode=ro` 原地打开依旧会失败。`Directory.CreateTempSubdirectory` 替代 Go 的临时目录处理。

6. **`NOTIFYICONDATA` 结构体尺寸**
   → 这是 C# 封送特有的问题（Go 逐字段布局时不存在）：`Shell_NotifyIcon` 只接受 V1/V2/V3/V4 四个精确 `cbSize`，结构体若停在 `dwInfoFlags` 会得到 952 —— 四个都不匹配，调用静默返回 FALSE，托盘图标直接消失。
   处理：结构体只声明到 `szTip`（V1，296 字节），并用测试把这个数字钉死。

7. **`GetModuleHandle` 的 DLL 归属**
   → 冒烟测试抓到的真实崩溃：它属于 `kernel32.dll`，`Shell_NotifyIcon` 属于 `shell32.dll`，两者都曾被误声明到 `user32.dll`，运行时抛 `EntryPointNotFoundException`。已修正并归位。

8. **goroutine 与窗口线程**
   → Win32 窗口要求消息循环与窗口过程在同一线程，C# 用 STA 线程 + `Marshal.GetFunctionPointerForDelegate` 保持委托存活；窗口过程内统一 `try/catch`，绝不让异常逃进 native。

---

## 3. 依赖库与运行环境

### 运行环境

| 项 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 1809（build 17763）及以上 |
| 运行时 | .NET 8（**自包含发布**，目标机无需预装 .NET） |
| Windows App SDK | 1.6.250108002（自包含，随产物目录一起分发） |
| 构建 | .NET SDK 8.0.425（本机装在 `C:\Users\cxy0714\.toolchain\dotnet`），**外加 Visual Studio 2022 生成工具**（`UniversalBuildTools` 工作负载，仅 WinUI 的 PRI 索引需要） |
| 架构 | x64 |

### NuGet 依赖

| 项目 | 包 |
| --- | --- |
| HuorongAce.Core | `Microsoft.Data.Sqlite` 8.0.8 |
| HuorongAce.Native | `System.Drawing.Common` 8.0.8 |
| HuorongAce.App | `Microsoft.WindowsAppSDK` 1.6.250108002、`Microsoft.Windows.SDK.BuildTools` 10.0.26100.174 |
| 测试（两个） | `Microsoft.NET.Test.Sdk` 17.11.1、`xunit` 2.9.2、`xunit.runner.visualstudio` 2.8.2 |

### 构建命令

```bash
# 生成工具不在默认位置时先指过去（本机）
export VS_BUILDTOOLS_PATH='C:\Users\cxy0714\.toolchain\vsbuildtools'

dotnet build HuorongAce.sln
dotnet test  HuorongAce.sln
```

产物目录：`src/HuorongAce.App/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/`（含 `HuorongAce.exe`、`HuorongAce.pri`、`*.xbf` 与自包含运行时）。

---

## 4. 改动清单

### 新增

| 文件 | 说明 |
| --- | --- |
| `HuorongAce.sln` | 解决方案 |
| `src/Directory.Build.props` | 把 WinUI 的 MSBuild 打包任务路径指向生成工具 |
| `src/HuorongAce.Core/**` | 配置、日志抽象、威胁读取器、监控器（Go `internal/config` + `internal/monitor`） |
| `src/HuorongAce.Native/**` | Win32 声明、窗口宿主、红屏、托盘图标、系统通知、盾牌图标 |
| `src/HuorongAce.App/**` | WinUI 应用入口、App、设置界面（XAML + 代码后置） |
| `src/HuorongAce.Core.Tests/**` | 火绒隔离库读取行为测试（Go `logdb_test.go` 的移植） |
| `src/HuorongAce.Native.Tests/**` | 托盘图标与结构体尺寸测试（Go 版没有对应项） |
| `docs/csharp-refactor.md` | 本文档 |

### 未改动（保持基线可回退）

- `main.go`、`internal/**`、`go.mod`、`go.sum` —— Go 版原样保留
- `.gitignore` —— 已含 `bin/`、`obj/`、`*.exe`、`config.json`
- `backup/huorong-ace-go-baseline.zip`、`git tag go-baseline`

### 行为对齐点（逐条对照 Go 版）

- 红屏文案与配色完全一致（`检测到安全威胁` / `程序已终止` / …，`#D3373A` 系）
- 红屏**只能**通过「继续」按钮或 Esc 关闭，不自动关闭、不响应其他按键
- 覆盖整个虚拟桌面（含任务栏），置顶
- 启动即落游标：历史记录不弹窗
- 双游标（rowid + 时间戳水位）+ 3 分钟去重窗口，重复记录只提示一次
- 托盘右键菜单「打开」/「退出」
- 启动与退出各发一次系统通知
- `config.json` 字段与旧版兼容，旧路径自动迁移

---

## 5. 验证方式

### 5.1 自动化

```bash
dotnet build HuorongAce.sln   # 结果：0 错误（仅 2 条 NU1603 版本号近似匹配警告）
dotnet test  HuorongAce.sln   # 结果：5 个测试全部通过
```

5 个测试覆盖：

| 测试 | 断言 |
| --- | --- |
| `RealQuarantineDatabase_CursorsSuppressHistory` | ① 打开后首轮轮询不得报警；② 只回退 rowid 游标仍不得报警（证明时间游标生效）；③ 两个游标都回退后应重放出全部记录，且同名威胁不重复 |
| `MissingDatabase_ThrowsFileNotFoundException` | 库文件缺失时抛异常而非静默失败 |
| `ThreatInfo_SimulatedCarriesGivenName` | 模拟触发的威胁名与时间戳正确 |
| `NotifyIconData_UsesTheV1Size` | `sizeof(NOTIFYICONDATAW)` == 296（V1），防止封送布局漂移导致托盘图标消失 |
| `TrayIcon_IsAcceptedByTheShell` | `Shell_NotifyIcon(NIM_ADD)` 实际返回 TRUE |

第一条在本机真实火绒库上跑过，输出：

```
table=FilesV3_60 rowid=19 ts=1790499526
  name=HEUR:TrojanDropper/BAT.Agent.a  detail=路径: C:\Users\cxy0714\Desktop\...bat
  name=Trojan/MEMZ.n                   detail=路径: ...彩虹猫.zip.c7b\geometry dash auto speedhack.exe
```

### 5.2 冒烟测试（已执行）

直接运行产物，进程稳定驻留（约 110 MB，无任何异常输出），未出现未处理异常。此步骤正是抓到 `GetModuleHandle` / `Shell_NotifyIcon` DLL 归属错误的手段。

### 5.3 需要人工确认的部分

以下几项依赖交互，自动化覆盖不到，建议手动过一遍：

1. **设置界面**：托盘右键 → 打开，确认三个页面（监控 / 测试与保存 / 使用说明）切换正常、窗口居中、状态条文案正确。
2. **红屏**：点「测试」按钮，确认全屏红屏弹出，且**只有**点「继续」或按 Esc 才关闭；鼠标悬停按钮有手型光标与高亮。
3. **真实检测**：让火绒处理一个样本（如 EICAR 测试文件），确认只弹一次而不是连续弹多次。
4. **通知**：启动与退出各应收到一条系统通知。
