//go:build !windows

package ui

import (
	"image/color"

	"fyne.io/fyne/v2"
	"fyne.io/fyne/v2/canvas"
	"fyne.io/fyne/v2/container"
	"fyne.io/fyne/v2/widget"

	"huorong-ace/internal/monitor"
)

// RedScreen is the fullscreen ACE-style overlay (non-Windows fallback).
// It stays up until dismissed; there is no auto-close timer.
type RedScreen struct {
	win      fyne.Window
	subtitle *canvas.Text
	body     *canvas.Text
}

// NewRedScreen builds the fullscreen red window (hidden until Show is called).
func NewRedScreen(app fyne.App) *RedScreen {
	w := app.NewWindow("ACE 反作弊系统")
	w.SetFullScreen(true)
	w.SetFixedSize(true)

	bg := canvas.NewRectangle(color.NRGBA{0xD3, 0x37, 0x3A, 255})
	wm := ShieldWatermark()

	title := canvas.NewText("检测到安全威胁", color.White)
	title.TextStyle = fyne.TextStyle{Bold: true}
	title.TextSize = 46
	title.Alignment = fyne.TextAlignCenter

	subtitle := canvas.NewText("程序已终止", color.NRGBA{0xff, 0xd0, 0xd0, 255})
	subtitle.TextSize = 26
	subtitle.Alignment = fyne.TextAlignCenter

	body := canvas.NewText("", color.NRGBA{0xff, 0xe0, 0xe0, 255})
	body.TextSize = 18
	body.Alignment = fyne.TextAlignCenter

	btn := widget.NewButton("继续", func() {
		w.Hide()
	})

	col := container.NewVBox(
		container.NewCenter(title),
		container.NewCenter(subtitle),
		container.NewCenter(body),
		container.NewCenter(btn),
	)
	content := container.NewStack(bg, container.NewCenter(wm), container.NewCenter(col))
	w.SetContent(content)
	w.SetCloseIntercept(func() {
		w.Hide()
	})
	// Match the Windows build: only the 继续 button or Esc dismisses it.
	w.Canvas().SetOnTypedKey(func(k *fyne.KeyEvent) {
		if k.Name == fyne.KeyEscape {
			w.Hide()
		}
	})

	return &RedScreen{win: w, subtitle: subtitle, body: body}
}

// Show displays the red screen with the given detection info.
func (r *RedScreen) Show(info monitor.Info) {
	r.subtitle.Text = "程序已终止"
	detail := info.Detail
	if info.Name != "" {
		detail = "威胁名称: " + info.Name + "\n" + detail
	}
	r.body.Text = detail
	r.body.Refresh()

	r.win.Show()
	r.win.RequestFocus()
}

// Hide dismisses the red screen.
func (r *RedScreen) Hide() {
	r.win.Hide()
}
