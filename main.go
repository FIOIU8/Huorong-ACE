package main

import (
	"time"

	"fyne.io/fyne/v2"
	"fyne.io/fyne/v2/app"
	"fyne.io/fyne/v2/driver/desktop"
	"fyne.io/fyne/v2/theme"

	"huorong-ace/internal/config"
	"huorong-ace/internal/monitor"
	"huorong-ace/internal/ui"
)

// exitNoticeDelay keeps the process alive briefly after the exit
// notification, so the fallback card (if used) is still seen.
const exitNoticeDelay = 1500 * time.Millisecond

func main() {
	cfg := config.Load()

	a := app.NewWithID("com.huorong.ace")
	a.SetIcon(ui.IconResource())
	a.Settings().SetTheme(theme.LightTheme())

	rs := ui.NewRedScreen(a)
	mon := monitor.New(cfg)
	mg := ui.NewManage(a, cfg, mon)

	mon.SetOnDetect(func(info monitor.Info) {
		rs.Show(info)
	})

	// System tray with right-click context menu: 打开 / 退出.
	if d, ok := a.(desktop.App); ok {
		menu := fyne.NewMenu("火绒ACE",
			fyne.NewMenuItem("打开", func() { mg.Show() }),
			fyne.NewMenuItem("退出", func() {
				go func() {
					mon.Stop()
					ui.NotifyBlocking(a, "火绒ACE 已退出", "已停止监控火绒日志，程序即将退出。")
					time.Sleep(exitNoticeDelay)
					a.Quit()
				}()
			}),
		)
		d.SetSystemTrayMenu(menu)
		d.SetSystemTrayIcon(ui.IconResource())
	} else {
		// Fallback when no system tray is available (e.g. headless):
		// surface the management window so the app stays usable.
		mg.Show()
	}

	mon.Start()
	ui.Notify(a, "火绒ACE 已启动", "正在监控火绒日志，右键托盘图标可打开管理界面或退出。")
	a.Run()
}
