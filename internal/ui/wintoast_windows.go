//go:build windows

package ui

import (
	"fmt"
	"image/color"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
)

// Real Windows toast notifications (Action Center).
//
// A Windows toast is raised through WinRT, which Go cannot easily call
// directly. The standard approach for unpackaged Win32 apps is to hand a
// short script to PowerShell, which runs as a separate process — that also
// means the toast survives after our own process exits.
//
// If PowerShell is unavailable or the toast cannot be raised, the caller
// falls back to the self-drawn notification card.

// toastAppID is the AppUserModelID used to attribute the notifications.
const toastAppID = "com.huorong.ace"

// text-only variant, used when no icon file is available
const toastScriptTextTemplate = `$title = %s
$content = %s
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
$tpl = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
$xd = [xml] $tpl.GetXml()
$xd.GetElementsByTagName("text")[0].AppendChild($xd.CreateTextNode($title)) > $null
$xd.GetElementsByTagName("text")[1].AppendChild($xd.CreateTextNode($content)) > $null
$x = New-Object Windows.Data.Xml.Dom.XmlDocument
$x.LoadXml($xd.OuterXml)
$toast = [Windows.UI.Notifications.ToastNotification]::new($x)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier(%s).Show($toast);`

const toastScriptTemplate = `$title = %s
$content = %s
$img = %s
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
$tpl = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastImageAndText02)
$xd = [xml] $tpl.GetXml()
$xd.GetElementsByTagName("text")[0].AppendChild($xd.CreateTextNode($title)) > $null
$xd.GetElementsByTagName("text")[1].AppendChild($xd.CreateTextNode($content)) > $null
$xd.GetElementsByTagName("image")[0].SetAttribute("src", $img) > $null
$x = New-Object Windows.Data.Xml.Dom.XmlDocument
$x.LoadXml($xd.OuterXml)
$toast = [Windows.UI.Notifications.ToastNotification]::new($x)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier(%s).Show($toast);`

// psQuote escapes a value for a PowerShell double-quoted string.
func psQuote(s string) string {
	r := strings.NewReplacer("`", "``", `"`, "`\"", "$", "`$")
	return `"` + r.Replace(s) + `"`
}

var (
	iconOnce sync.Once
	iconPath string
)

// toastIconPath writes the app shield to a temp PNG for the toast image.
func toastIconPath() string {
	iconOnce.Do(func() {
		p := filepath.Join(os.TempDir(), "huorong-ace-toast-icon.png")
		data := GenShieldPNG(64, color.White, color.NRGBA{0xC0, 0x00, 0x00, 0xFF})
		if err := os.WriteFile(p, data, 0o644); err == nil {
			iconPath = p
			return
		}
		iconPath = ""
	})
	return iconPath
}

// showWinToast raises a real system toast. It blocks until PowerShell
// (a separate process) has finished, so the notification is already on
// screen when this returns.
func showWinToast(title, content string) error {
	if p := toastIconPath(); p != "" {
		img := "file:///" + filepath.ToSlash(p)
		return runToastScript(fmt.Sprintf(toastScriptTemplate,
			psQuote(title), psQuote(content), psQuote(img), psQuote(toastAppID)))
	}
	return runToastScript(fmt.Sprintf(toastScriptTextTemplate,
		psQuote(title), psQuote(content), psQuote(toastAppID)))
}

func runToastScript(script string) error {
	f, err := os.CreateTemp("", "huorong-ace-toast-*.ps1")
	if err != nil {
		return err
	}
	name := f.Name()
	defer os.Remove(name)
	if _, err := f.WriteString(script); err != nil {
		f.Close()
		return err
	}
	f.Close()

	launch := "(Get-Content -Encoding UTF8 -Path " + name + " -Raw) | Invoke-Expression"
	cmd := exec.Command("PowerShell", "-ExecutionPolicy", "Bypass", launch)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	return cmd.Run()
}
