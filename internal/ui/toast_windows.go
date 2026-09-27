//go:build windows

package ui

import (
	"runtime"
	"sync"
	"syscall"
	"time"
	"unsafe"

	"fyne.io/fyne/v2"
)

// Native toast-style notification popup — fallback only.
//
// The primary path is a real Windows toast (see wintoast_windows.go). This
// self-drawn card is used when the system toast cannot be raised, so startup
// and shutdown messages are still visible.
//
// The popup is a singleton: the window class is registered with a callback
// bound to this one instance, so repeated notifications reuse it rather than
// re-registering a class whose callback points at a dead object.

const (
	toastClassName = "HuorongACENotification"

	msgToastClose  = wmUser + 3
	msgToastUpdate = wmUser + 4

	spiGetWorkArea = 0x0030
	lwaAlpha       = 0x00000002

	// WS_EX_LAYERED, required for the alpha fade-in.
	wsExLayered = 0x00080000

	dtLeft      = 0x00000000
	dtWordBreak = 0x00000010

	toastWidth  = 380
	toastHeight = 96
	toastMargin = 16
	toastCorner = 14

	toastTTL      = 4 * time.Second
	toastFadeStep = 17
	toastFadeWait = 12 * time.Millisecond
)

var (
	pSystemParametersInfo       = user32.NewProc("SystemParametersInfoW")
	pSetWindowRgn               = user32.NewProc("SetWindowRgn")
	pSetLayeredWindowAttributes = user32.NewProc("SetLayeredWindowAttributes")
	pCreateRoundRectRgn         = gdi32.NewProc("CreateRoundRectRgn")
	pPolygon                    = gdi32.NewProc("Polygon")
)

// Colors for the notification card.
var (
	toastBG     = rgb(0x2B, 0x2B, 0x2B)
	toastBorder = rgb(0x4A, 0x4A, 0x4A)
	toastTitle  = rgb(0xFF, 0xFF, 0xFF)
	toastText   = rgb(0xD0, 0xD0, 0xD0)
	toastAccent = rgb(0xD3, 0x37, 0x3A)
)

func getWorkArea(rc *RECT) bool {
	r, _, _ := pSystemParametersInfo.Call(uintptr(spiGetWorkArea), 0, uintptr(unsafe.Pointer(rc)), 0)
	return r != 0
}

func createRoundRectRgn(l, t, r, b, w, h int32) uintptr {
	res, _, _ := pCreateRoundRectRgn.Call(
		uintptr(uint32(l)), uintptr(uint32(t)), uintptr(uint32(r)), uintptr(uint32(b)),
		uintptr(uint32(w)), uintptr(uint32(h)),
	)
	return res
}

func setWindowRgn(hwnd HWND, rgn uintptr, redraw bool) {
	b := 0
	if redraw {
		b = 1
	}
	pSetWindowRgn.Call(uintptr(hwnd), rgn, uintptr(b))
}

func setLayeredAlpha(hwnd HWND, alpha uint8) {
	pSetLayeredWindowAttributes.Call(uintptr(hwnd), 0, uintptr(alpha), uintptr(lwaAlpha))
}

func polygon(hdc HDC, pts *POINT, count int32) {
	pPolygon.Call(uintptr(hdc), uintptr(unsafe.Pointer(pts)), uintptr(uint32(count)))
}

// toast is the single notification popup instance.
var toast = &toastWin{}

type toastWin struct {
	mu      sync.Mutex
	hwnd    HWND
	visible bool
	title   string
	content string
	timer   *time.Timer
}

// Notify raises a real Windows system notification (Action Center toast).
// If that cannot be raised, it falls back to the built-in card below.
func Notify(app fyne.App, title, content string) {
	go func() {
		if err := showWinToast(title, content); err != nil {
			toast.show(title, content)
		}
	}()
}

// NotifyBlocking is Notify, but waits until the system toast has actually been
// raised. Use it just before the process exits so the notification outlives us.
func NotifyBlocking(app fyne.App, title, content string) {
	if err := showWinToast(title, content); err != nil {
		toast.show(title, content)
	}
}

func (t *toastWin) show(title, content string) {
	t.mu.Lock()
	t.title = title
	t.content = content
	hwnd := t.hwnd
	if hwnd != 0 {
		// Replace the visible card's content and restart its lifetime.
		postMessageW(hwnd, msgToastUpdate, 0, 0)
		if t.timer != nil {
			t.timer.Stop()
		}
		t.timer = time.AfterFunc(toastTTL, t.closeViaMsg)
		t.mu.Unlock()
		return
	}
	t.visible = true
	t.mu.Unlock()
	go t.run()
}

func (t *toastWin) closeViaMsg() {
	t.mu.Lock()
	hwnd := t.hwnd
	t.mu.Unlock()
	if hwnd != 0 {
		postMessageW(hwnd, msgToastClose, 0, 0)
	}
}

func (t *toastWin) run() {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()

	hInstance := getModuleHandle()
	cls, _ := syscall.UTF16PtrFromString(toastClassName)

	var wc WNDCLASSEX
	wc.CbSize = uint32(unsafe.Sizeof(wc))
	wc.Style = csHRedraw | csVRedraw
	wc.LpfnWndProc = syscall.NewCallback(t.wndProc)
	wc.HInstance = HINSTANCE(hInstance)
	wc.HCursor = loadCursor(0, idcArrow)
	wc.HbrBackground = 0
	wc.LpszClassName = cls
	registerClassEx(&wc)

	// Position above the taskbar, bottom-right of the work area.
	var wa RECT
	if !getWorkArea(&wa) {
		wa = RECT{Left: 0, Top: 0, Right: getSystemMetrics(smCXScreen), Bottom: getSystemMetrics(smCYScreen)}
	}
	x := wa.Right - toastWidth - toastMargin
	y := wa.Bottom - toastHeight - toastMargin

	hwnd := createWindowEx(
		wsExTopMost|wsExToolWindow|wsExComposited|wsExLayered,
		cls, nil,
		wsPopup|wsVisible,
		x, y, toastWidth, toastHeight,
		0, 0, HINSTANCE(hInstance), 0,
	)
	if hwnd == 0 {
		t.mu.Lock()
		t.visible = false
		t.mu.Unlock()
		return
	}
	t.mu.Lock()
	t.hwnd = hwnd
	t.mu.Unlock()

	// Rounded corners and a short fade-in.
	if rgn := createRoundRectRgn(0, 0, toastWidth, toastHeight, toastCorner, toastCorner); rgn != 0 {
		setWindowRgn(hwnd, rgn, true)
	}
	setLayeredAlpha(hwnd, 0)
	showWindow(hwnd, swShow)
	updateWindow(hwnd)
	go t.fadeIn(hwnd)

	t.mu.Lock()
	t.timer = time.AfterFunc(toastTTL, t.closeViaMsg)
	t.mu.Unlock()

	var msg MSG
	for getMessage(&msg) != 0 {
		translateMessage(&msg)
		dispatchMessage(&msg)
	}

	t.mu.Lock()
	if t.timer != nil {
		t.timer.Stop()
		t.timer = nil
	}
	t.hwnd = 0
	t.visible = false
	t.mu.Unlock()
}

func (t *toastWin) fadeIn(hwnd HWND) {
	for a := 0; a <= 255; a += toastFadeStep {
		setLayeredAlpha(hwnd, uint8(a))
		time.Sleep(toastFadeWait)
	}
	setLayeredAlpha(hwnd, 255)
}

func (t *toastWin) wndProc(hwnd, msg, wparam, lparam uintptr) uintptr {
	h := HWND(hwnd)
	switch msg {
	case wmEraseBkgnd:
		var rc RECT
		getClientRect(h, &rc)
		fillSolid(HDC(wparam), rc, toastBG)
		return 1
	case wmPaint:
		t.onPaint(h)
		return 0
	case wmLButtonDown, wmRButtonDown, wmMButtonDown:
		// Clicking dismisses the notification early.
		t.closeViaMsg()
		return 0
	case msgToastUpdate:
		invalidateRect(h, nil, false)
		return 0
	case msgToastClose:
		destroyWindow(h)
		return 0
	case wmDestroy:
		postQuitMessage(0)
		return 0
	}
	return defWindowProc(hwnd, msg, wparam, lparam)
}

func (t *toastWin) onPaint(hwnd HWND) {
	var ps PAINTSTRUCT
	hdc := beginPaint(hwnd, &ps)
	defer endPaint(hwnd, &ps)

	var rc RECT
	getClientRect(hwnd, &rc)
	w := rc.Right - rc.Left
	h := rc.Bottom - rc.Top
	if w <= 0 || h <= 0 {
		return
	}

	fillSolid(hdc, rc, toastBG)
	roundRectFill(hdc, RECT{Left: 0, Top: 0, Right: w - 1, Bottom: h - 1}, toastBG, toastBorder, toastCorner)

	// App shield mark on the left.
	drawShieldMark(hdc, 40, h/2, 30)

	t.mu.Lock()
	title, content := t.title, t.content
	t.mu.Unlock()

	drawTextIn(hdc, 68, 18, w-16, 42, title, "Microsoft YaHei", 15, 700, toastTitle, dtLeft|dtSingleLine)
	drawTextIn(hdc, 68, 44, w-16, h-10, content, "Microsoft YaHei", 13, 400, toastText, dtLeft|dtWordBreak)
}

// drawShieldMark draws the small shield used as the notification icon.
func drawShieldMark(hdc HDC, cx, cy, size int32) {
	hw := size / 2
	top := cy - size/2
	bot := cy + size/2
	pts := []POINT{
		{X: cx - hw, Y: top},
		{X: cx + hw, Y: top},
		{X: cx + hw, Y: cy},
		{X: cx, Y: bot},
		{X: cx - hw, Y: cy},
	}
	pen := createPen(psSolid, 2, toastTitle)
	oldPen := selectObject(hdc, HGDIOBJ(pen))
	brush := createSolidBrush(toastAccent)
	oldBrush := selectObject(hdc, HGDIOBJ(brush))
	polygon(hdc, &pts[0], int32(len(pts)))
	selectObject(hdc, oldPen)
	selectObject(hdc, oldBrush)
	deleteObject(HGDIOBJ(pen))
	deleteObject(HGDIOBJ(brush))
}
