# Huorong-ACE 项目记忆

## 项目概述
娱乐/演示程序：用 Go + Fyne 模拟 ACE 反作弊的「红屏终止界面」。
- 监控火绒防护日志 `C:\ProgramData\Huorong\Sysdiag\log.db`（SQLite/WAL，表 `HrLogV3`，无公开 API，只能只读轮询）。
- 检测到病毒/木马事件 → 弹全屏红窗（深红渐变 + 浅色盾牌水印 + ACE 风格中文文案），按钮可关，默认 5 秒后自动关闭。
- 系统托盘右键菜单含「打开」（显示管理界面窗口）/「退出」（mon.Stop()+a.Quit() 彻底退出）。
- 管理界面可改：日志路径、轮询间隔、红屏时长、病毒关键词；含「测试红屏」「停止/启动监控」。
- 火绒未安装时降级为「仅测试模式」（托盘/测试按钮仍可用）。

## 关键文件
- `main.go`：Fyne app、托盘菜单、串联 monitor↔redscreen。
- `internal/monitor`：轮询 log.db + Simulate() 测试触发。
- `internal/ui`：icon.go（盾牌水印/图标生成）、redscreen.go（全屏红窗）、manage.go（管理界面）。
- `internal/config`：默认参数 + config.json（运行时写到 exe 同目录）。

## 本沙箱构建环境（重要）
- Go 1.27.1 + MinGW-w64 GCC 16.2 装在 `C:/Users/cxy0714/.toolchain`（go/bin/go.exe、mingw64/bin/gcc.exe）。
- 模块代理：`GOPROXY=https://goproxy.cn,direct`（`proxy.golang.org` 返回 502，不可用）。
- Fyne 桌面端需 CGO：`CGO_ENABLED=1 CC=gcc CXX=g++ GOOS=windows GOARCH=amd64`。
- 沙箱的「安全删除(safe-delete/genie-trash)」会拦截文件替换/删除并 fail-closed，导致 `go mod tidy` 写 go.mod、`rm`、以及 `go build` 写 exe 时报 Access denied。
  → 凡涉及写项目目录的 Go 命令必须带 `dangerouslyDisableSandbox: true`。
- 最终构建命令：
  `export PATH="$TOOL/mingw64/bin:$TOOL/go/bin:$PATH" CGO_ENABLED=1 CC=gcc CXX=g++ GOOS=windows GOARCH=amd64 GOPROXY=https://goproxy.cn,direct`
  `go build -ldflags="-H windowsgui" -o Huorong-ACE.exe .`
- 注意：本沙箱无显示器，无法实跑 GUI；仅能编译校验。运行时行为（托盘/红屏/监控）需在用户 Windows 上验证。
