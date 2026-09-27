//go:build !windows

package ui

import "fyne.io/fyne/v2"

// Notify shows a system notification (non-Windows fallback).
func Notify(app fyne.App, title, content string) {
	if app == nil {
		return
	}
	app.SendNotification(&fyne.Notification{Title: title, Content: content})
}

// NotifyBlocking shows a system notification (non-Windows fallback).
func NotifyBlocking(app fyne.App, title, content string) {
	Notify(app, title, content)
}
