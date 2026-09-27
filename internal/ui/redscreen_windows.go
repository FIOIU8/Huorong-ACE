//go:build windows

package ui

import (
	"runtime"
	"sync"
	"syscall"
	"unsafe"

	"fyne.io/fyne/v2"

	"huorong-ace/internal/monitor"
)

// ---------------------------------------------------------------------------
// Minimal Win32 declarations (no cgo beyond what Fyne already needs, no
// external wrapper packages). Layouts match the Windows 64-bit (LLP64) ABI.
// ---------------------------------------------------------------------------

type (
	HWND      uintptr
	HINSTANCE uintptr
	HMODULE   uintptr
	HDC       uintptr
	HBRUSH    uintptr
	HGDIOBJ   uintptr
	HICON     uintptr
	HCURSOR   uintptr
	HMENU     uintptr
	ATOM      uint16
	WPARAM    uintptr
	LPARAM    uintptr
)

type POINT struct {
	X, Y int32
}

type RECT struct {
	Left, Top, Right, Bottom int32
}

type MSG struct {
	Hwnd    HWND
	Message uint32
	WParam  WPARAM
	LParam  LPARAM
	Time    uint32
	Pt      POINT
}

type WNDCLASSEX struct {
	CbSize        uint32
	Style         uint32
	LpfnWndProc   uintptr
	CbClsExtra    int32
	CbWndExtra    int32
	HInstance     HINSTANCE
	HIcon         HICON
	HCursor       HCURSOR
	HbrBackground HBRUSH
	LpszMenuName  *uint16
	LpszClassName *uint16
	HIconSm       HICON
}

type PAINTSTRUCT struct {
	Hdc         HDC
	FErase      int32
	RcPaint     RECT
	FRestore    int32
	FIncUpdate  int32
	RgbReserved [32]byte
}

type LOGFONT struct {
	LfHeight         int32
	LfWidth          int32
	LfEscapement     int32
	LfOrientation    int32
	LfWeight         int32
	LfItalic         byte
	LfUnderline      byte
	LfStrikeOut      byte
	LfCharSet        byte
	LfOutPrecision   byte
	LfClipPrecision  byte
	LfQuality        byte
	LfPitchAndFamily byte
	LfFaceName       [32]uint16
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const (
	wmDestroy     = 0x0002
	wmPaint       = 0x000F
	wmEraseBkgnd  = 0x0014
	wmKeyDown     = 0x0100
	wmChar        = 0x0102
	wmSysKeyDown  = 0x0104
	wmLButtonDown = 0x0201
	wmRButtonDown = 0x0204
	wmMButtonDown = 0x0207
	wmSetCursor   = 0x0020
	wmUser        = 0x0400
	msgRedClose   = wmUser + 1
	msgRedUpdate  = wmUser + 2

	smXVirtualScreen  = 76
	smYVirtualScreen  = 77
	smCXVirtualScreen = 78
	smCYVirtualScreen = 79
	smCXScreen        = 0
	smCYScreen        = 1

	wsExTopMost    = 0x00000008
	wsExToolWindow = 0x00000080
	wsExComposited = 0x02000000
	wsPopup        = 0x80000000
	wsVisible      = 0x10000000

	csHRedraw = 0x0002
	csVRedraw = 0x0001

	swShow = 5

	// Standard arrow cursor (IDC_ARROW) so the overlay never shows the
	// system busy/hourglass "spinning" cursor.
	idcArrow = 32512
	// Hand cursor (IDC_HAND) shown while hovering the button.
	idcHand = 32649

	dtCenter     = 0x00000001
	dtRight      = 0x00000002
	dtVCenter    = 0x00000004
	dtSingleLine = 0x00000020

	psSolid     = 0
	transparent = 1
)

// Screen copy. Wording reflects what actually happened: Huorong found a
// security threat, so this talks about the threat/process, not a game match.
const (
	titleText = "检测到安全威胁"
	subText   = "程序已终止"
	descText  = "威胁程序已被隔离，相关进程已终止，请及时进行全盘扫描。"
	hintText  = "点击「继续」或按 Esc 退出"
)

// ---------------------------------------------------------------------------
// Lazy DLL / procedure bindings
// ---------------------------------------------------------------------------

var (
	user32   = syscall.NewLazyDLL("user32.dll")
	gdi32    = syscall.NewLazyDLL("gdi32.dll")
	kernel32 = syscall.NewLazyDLL("kernel32.dll")

	pGetModuleHandleW    = kernel32.NewProc("GetModuleHandleW")
	pRegisterClassExW    = user32.NewProc("RegisterClassExW")
	pCreateWindowExW     = user32.NewProc("CreateWindowExW")
	pDefWindowProcW      = user32.NewProc("DefWindowProcW")
	pShowWindow          = user32.NewProc("ShowWindow")
	pUpdateWindow        = user32.NewProc("UpdateWindow")
	pSetForegroundWindow = user32.NewProc("SetForegroundWindow")
	pDestroyWindow       = user32.NewProc("DestroyWindow")
	pPostQuitMessage     = user32.NewProc("PostQuitMessage")
	pGetMessageW         = user32.NewProc("GetMessageW")
	pTranslateMessage    = user32.NewProc("TranslateMessage")
	pDispatchMessageW    = user32.NewProc("DispatchMessageW")
	pPostMessageW        = user32.NewProc("PostMessageW")
	pBeginPaint          = user32.NewProc("BeginPaint")
	pEndPaint            = user32.NewProc("EndPaint")
	pGetClientRect       = user32.NewProc("GetClientRect")
	pInvalidateRect      = user32.NewProc("InvalidateRect")
	pGetSystemMetrics    = user32.NewProc("GetSystemMetrics")
	pFillRect            = user32.NewProc("FillRect")
	pDrawTextW           = user32.NewProc("DrawTextW")
	pLoadCursorW         = user32.NewProc("LoadCursorW")
	pSetCursorW          = user32.NewProc("SetCursor")

	pCreateSolidBrush   = gdi32.NewProc("CreateSolidBrush")
	pCreatePen          = gdi32.NewProc("CreatePen")
	pSelectObject       = gdi32.NewProc("SelectObject")
	pDeleteObject       = gdi32.NewProc("DeleteObject")
	pCreateFontIndirect = gdi32.NewProc("CreateFontIndirectW")
	pSetBkMode          = gdi32.NewProc("SetBkMode")
	pSetTextColor       = gdi32.NewProc("SetTextColor")
	pRoundRect          = gdi32.NewProc("RoundRect")
)

// ---------------------------------------------------------------------------
// Thin wrappers
// ---------------------------------------------------------------------------

func getModuleHandle() HMODULE {
	r, _, _ := pGetModuleHandleW.Call(0)
	return HMODULE(r)
}

func loadCursor(instance HINSTANCE, id uint16) HCURSOR {
	r, _, _ := pLoadCursorW.Call(uintptr(instance), uintptr(id))
	return HCURSOR(r)
}

func setCursor(c HCURSOR) { pSetCursorW.Call(uintptr(c)) }

func registerClassEx(wc *WNDCLASSEX) ATOM {
	r, _, _ := pRegisterClassExW.Call(uintptr(unsafe.Pointer(wc)))
	return ATOM(uint16(r))
}

func createWindowEx(exStyle uint32, className, windowName *uint16, style uint32,
	x, y, width, height int32, parent HWND, menu HMENU, instance HINSTANCE, param uintptr) HWND {
	r, _, _ := pCreateWindowExW.Call(
		uintptr(exStyle),
		uintptr(unsafe.Pointer(className)),
		uintptr(unsafe.Pointer(windowName)),
		uintptr(style),
		uintptr(uint32(x)),
		uintptr(uint32(y)),
		uintptr(uint32(width)),
		uintptr(uint32(height)),
		uintptr(parent),
		uintptr(menu),
		uintptr(instance),
		param,
	)
	return HWND(r)
}

func defWindowProc(hwnd, msg, wparam, lparam uintptr) uintptr {
	r, _, _ := pDefWindowProcW.Call(hwnd, msg, wparam, lparam)
	return r
}

func showWindow(hwnd HWND, cmd int32) { pShowWindow.Call(uintptr(hwnd), uintptr(uint32(cmd))) }
func updateWindow(hwnd HWND)          { pUpdateWindow.Call(uintptr(hwnd)) }
func setForegroundWindow(hwnd HWND)   { pSetForegroundWindow.Call(uintptr(hwnd)) }

func destroyWindow(hwnd HWND) bool {
	r, _, _ := pDestroyWindow.Call(uintptr(hwnd))
	return r != 0
}

func postQuitMessage(code int32) { pPostQuitMessage.Call(uintptr(uint32(code))) }

func getMessage(msg *MSG) int {
	r, _, _ := pGetMessageW.Call(uintptr(unsafe.Pointer(msg)), 0, 0, 0)
	return int(int32(r))
}

func translateMessage(msg *MSG) { pTranslateMessage.Call(uintptr(unsafe.Pointer(msg))) }

func dispatchMessage(msg *MSG) uintptr {
	r, _, _ := pDispatchMessageW.Call(uintptr(unsafe.Pointer(msg)))
	return r
}

func postMessageW(hwnd HWND, msg uint32, wparam, lparam uintptr) bool {
	r, _, _ := pPostMessageW.Call(uintptr(hwnd), uintptr(msg), wparam, lparam)
	return r != 0
}

func beginPaint(hwnd HWND, ps *PAINTSTRUCT) HDC {
	r, _, _ := pBeginPaint.Call(uintptr(hwnd), uintptr(unsafe.Pointer(ps)))
	return HDC(r)
}

func endPaint(hwnd HWND, ps *PAINTSTRUCT) {
	pEndPaint.Call(uintptr(hwnd), uintptr(unsafe.Pointer(ps)))
}

func getClientRect(hwnd HWND, rc *RECT) {
	pGetClientRect.Call(uintptr(hwnd), uintptr(unsafe.Pointer(rc)))
}

func invalidateRect(hwnd HWND, rc *RECT, erase bool) {
	e := 0
	if erase {
		e = 1
	}
	pInvalidateRect.Call(uintptr(hwnd), uintptr(unsafe.Pointer(rc)), uintptr(e))
}

func getSystemMetrics(n int32) int32 {
	r, _, _ := pGetSystemMetrics.Call(uintptr(uint32(n)))
	return int32(int32(r))
}

func createSolidBrush(color uint32) HBRUSH {
	r, _, _ := pCreateSolidBrush.Call(uintptr(color))
	return HBRUSH(r)
}

func fillRect(hdc HDC, rc *RECT, brush HBRUSH) int {
	r, _, _ := pFillRect.Call(uintptr(hdc), uintptr(unsafe.Pointer(rc)), uintptr(brush))
	return int(int32(r))
}

func createPen(style, width int32, color uint32) HGDIOBJ {
	r, _, _ := pCreatePen.Call(uintptr(uint32(style)), uintptr(uint32(width)), uintptr(color))
	return HGDIOBJ(r)
}

func selectObject(hdc HDC, obj HGDIOBJ) HGDIOBJ {
	r, _, _ := pSelectObject.Call(uintptr(hdc), uintptr(obj))
	return HGDIOBJ(r)
}

func deleteObject(obj HGDIOBJ) bool {
	r, _, _ := pDeleteObject.Call(uintptr(obj))
	return r != 0
}

func roundRect(hdc HDC, l, t, r, b, ew, eh int32) {
	pRoundRect.Call(
		uintptr(hdc),
		uintptr(uint32(l)), uintptr(uint32(t)), uintptr(uint32(r)), uintptr(uint32(b)),
		uintptr(uint32(ew)), uintptr(uint32(eh)),
	)
}

func createFontIndirect(lf *LOGFONT) HGDIOBJ {
	r, _, _ := pCreateFontIndirect.Call(uintptr(unsafe.Pointer(lf)))
	return HGDIOBJ(r)
}

func setBkMode(hdc HDC, mode int32) int {
	r, _, _ := pSetBkMode.Call(uintptr(hdc), uintptr(mode))
	return int(int32(r))
}

func setTextColor(hdc HDC, color uint32) uint32 {
	r, _, _ := pSetTextColor.Call(uintptr(hdc), uintptr(color))
	return uint32(r)
}

func drawTextW(hdc HDC, text *uint16, cch int32, rc *RECT, format uint32) int {
	r, _, _ := pDrawTextW.Call(
		uintptr(hdc),
		uintptr(unsafe.Pointer(text)),
		uintptr(int32(cch)),
		uintptr(unsafe.Pointer(rc)),
		uintptr(format),
	)
	return int(int32(r))
}

// ---------------------------------------------------------------------------
// Colors (sampled from the real ACE termination screen)
// ---------------------------------------------------------------------------

func rgb(r, g, b uint8) uint32 {
	return uint32(r) | uint32(g)<<8 | uint32(b)<<16
}

var (
	colBG       = rgb(0xD3, 0x37, 0x3A) // #D3373A flat ACE red
	colWhite    = rgb(0xFF, 0xFF, 0xFF)
	colSepLine  = rgb(0xE4, 0x74, 0x78)
	colBtnFill  = rgb(0xD8, 0x60, 0x66)
	colBtnHover = rgb(0xE4, 0x74, 0x78)
	colBtnEdge  = rgb(0xE9, 0x9A, 0x9E)
	colInfo     = rgb(0xF0, 0xC4, 0xC6)
)

// ---------------------------------------------------------------------------
// RedScreen: native Win32 fullscreen overlay — covers the taskbar, renders
// statically (no animation) and closes on any key press or mouse click.
// ---------------------------------------------------------------------------

type RedScreen struct {
	mu      sync.Mutex
	app     fyne.App
	hwnd    HWND
	visible bool

	infoName   string
	infoDetail string

	// Geometry and hover state of the "继续" button. The overlay is dismissed
	// only through this button or Esc, so it has to be a real hit target.
	btnRect  RECT
	btnHover bool
}

// NewRedScreen builds the (hidden) native red-screen overlay.
func NewRedScreen(app fyne.App) *RedScreen {
	return &RedScreen{app: app}
}

// Show displays the red screen with detection info.
func (r *RedScreen) Show(info monitor.Info) {
	r.mu.Lock()
	r.infoName = info.Name
	r.infoDetail = info.Detail
	r.visible = true
	hwnd := r.hwnd
	if hwnd != 0 {
		// Already on screen: repaint with the new content.
		postMessageW(hwnd, msgRedUpdate, 0, 0)
		r.mu.Unlock()
		return
	}
	r.mu.Unlock()
	go r.run()
}

// Hide dismisses the red screen (interface compatibility).
func (r *RedScreen) Hide() { r.closeViaMsg() }

func (r *RedScreen) closeViaMsg() {
	r.mu.Lock()
	hwnd := r.hwnd
	r.mu.Unlock()
	if hwnd != 0 {
		postMessageW(hwnd, msgRedClose, 0, 0)
	}
}

func (r *RedScreen) run() {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()

	hInstance := getModuleHandle()
	cls, _ := syscall.UTF16PtrFromString(redClassName)

	var wc WNDCLASSEX
	wc.CbSize = uint32(unsafe.Sizeof(wc))
	wc.Style = csHRedraw | csVRedraw
	wc.LpfnWndProc = syscall.NewCallback(r.wndProc)
	wc.HInstance = HINSTANCE(hInstance)
	wc.HCursor = loadCursor(0, idcArrow) // never show the busy/hourglass cursor
	wc.HbrBackground = 0
	wc.LpszClassName = cls
	registerClassEx(&wc)

	x := getSystemMetrics(smXVirtualScreen)
	y := getSystemMetrics(smYVirtualScreen)
	cx := getSystemMetrics(smCXVirtualScreen)
	cy := getSystemMetrics(smCYVirtualScreen)
	if cx <= 0 || cy <= 0 {
		cx = getSystemMetrics(smCXScreen)
		cy = getSystemMetrics(smCYScreen)
	}

	hwnd := createWindowEx(
		wsExTopMost|wsExToolWindow|wsExComposited,
		cls, nil,
		wsPopup|wsVisible,
		x, y, cx, cy,
		0, 0, HINSTANCE(hInstance), 0,
	)
	if hwnd == 0 {
		r.mu.Lock()
		r.visible = false
		r.mu.Unlock()
		return
	}
	r.mu.Lock()
	r.hwnd = hwnd
	r.mu.Unlock()

	showWindow(hwnd, swShow)
	updateWindow(hwnd)
	setForegroundWindow(hwnd)

	// No auto-close: the overlay stays up until the 继续 button is clicked or
	// Esc is pressed.
	var msg MSG
	for getMessage(&msg) != 0 {
		translateMessage(&msg)
		dispatchMessage(&msg)
	}

	r.mu.Lock()
	r.hwnd = 0
	r.visible = false
	r.mu.Unlock()
}

func (r *RedScreen) wndProc(hwnd, msg, wparam, lparam uintptr) uintptr {
	h := HWND(hwnd)
	switch msg {
	case wmEraseBkgnd:
		// wParam carries the device context for the erase.
		var rc RECT
		getClientRect(h, &rc)
		fillSolid(HDC(wparam), rc, colBG)
		return 1
	case wmPaint:
		r.onPaint(h)
		return 0
	case wmKeyDown, wmSysKeyDown:
		// Only Esc dismisses; every other key is swallowed so a stray
		// keystroke cannot get rid of the overlay by accident.
		if wparam == vkEscape {
			r.closeViaMsg()
		}
		return 0
	case wmMouseMove:
		x := int32(int16(uint32(lparam) & 0xFFFF))
		y := int32(int16(uint32(lparam) >> 16))
		var rc RECT
		getClientRect(h, &rc)
		hover := inRectRed(redButtonRect(rc.Right-rc.Left, rc.Bottom-rc.Top), x, y)
		if hover != r.btnHover {
			r.btnHover = hover
			invalidateRect(h, nil, false)
		}
		return 0
	case wmSetCursor:
		if r.btnHover {
			setCursor(loadCursor(0, idcHand))
			return 1
		}
	case wmLButtonDown:
		// Clicking anywhere else does nothing: the button is the only target.
		x := int32(int16(uint32(lparam) & 0xFFFF))
		y := int32(int16(uint32(lparam) >> 16))
		if inRectRed(r.btnRect, x, y) {
			r.closeViaMsg()
		}
		return 0
	case wmRButtonDown, wmMButtonDown:
		return 0
	case msgRedUpdate:
		invalidateRect(h, nil, false)
		return 0
	case msgRedClose:
		destroyWindow(h)
		return 0
	case wmDestroy:
		postQuitMessage(0)
		return 0
	}
	return defWindowProc(hwnd, msg, wparam, lparam)
}

func (r *RedScreen) infoLine() string {
	if r.infoName != "" {
		return "威胁名称: " + r.infoName
	}
	return r.infoDetail
}

func (r *RedScreen) onPaint(hwnd HWND) {
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

	fillSolid(hdc, rc, colBG)

	fw := float32(w)
	fh := float32(h)

	// Separator hairlines near the top and bottom edges.
	sepL := int32(fw * 0.040)
	sepR := int32(fw * 0.960)
	hline(hdc, sepL, sepR, int32(fh*0.045), colSepLine)
	hline(hdc, sepL, sepR, int32(fh*0.951), colSepLine)

	// Top-right "back" affordance.
	drawTextIn(hdc, int32(fw*0.860), int32(fh*0.058), int32(fw*0.958), int32(fh*0.098),
		"‹ 返回", "Microsoft YaHei", int32(fh*0.032), 400, colWhite, dtRight|dtVCenter|dtSingleLine)

	// ACE wordmark drawn as an outlined logo (white stroke + bg-red inner).
	logoY := int32(fh * 0.333)
	logoSize := int32(fh * 0.140)
	drawTextIn(hdc, 0, logoY-logoSize, w, logoY+logoSize,
		"ACE", "Arial Black", logoSize, 400, colWhite, dtCenter|dtVCenter|dtSingleLine)
	innerSize := logoSize - int32(fh*0.012)
	if innerSize < 1 {
		innerSize = 1
	}
	drawTextIn(hdc, 0, logoY-innerSize, w, logoY+innerSize,
		"ACE", "Arial Black", innerSize, 400, colBG, dtCenter|dtVCenter|dtSingleLine)

	// Static headline block (no typing animation).
	drawTextIn(hdc, 0, int32(fh*0.425), w, int32(fh*0.500),
		titleText, "Microsoft YaHei", int32(fh*0.085), 700, colWhite, dtCenter|dtVCenter|dtSingleLine)
	drawTextIn(hdc, 0, int32(fh*0.522), w, int32(fh*0.578),
		subText, "Microsoft YaHei", int32(fh*0.050), 400, colWhite, dtCenter|dtVCenter|dtSingleLine)
	drawTextIn(hdc, int32(fw*0.120), int32(fh*0.570), int32(fw*0.880), int32(fh*0.600),
		descText, "Microsoft YaHei", int32(fh*0.028), 400, colWhite, dtCenter|dtSingleLine)

	// Actual detection result from Huorong.
	if info := r.infoLine(); info != "" {
		drawTextIn(hdc, int32(fw*0.120), int32(fh*0.605), int32(fw*0.880), int32(fh*0.640),
			info, "Microsoft YaHei", int32(fh*0.024), 400, colInfo, dtCenter|dtSingleLine)
	}

	// "继续" button — the only click target that dismisses the overlay.
	brect := redButtonRect(w, h)
	r.btnRect = brect
	fill, edge := colBtnFill, colBtnEdge
	if r.btnHover {
		fill, edge = colBtnHover, colWhite
	}
	radius := int32(fh * 0.009)
	if radius < 2 {
		radius = 2
	}
	roundRectFill(hdc, brect, fill, edge, radius)
	drawTextIn(hdc, brect.Left, brect.Top, brect.Right, brect.Bottom,
		"继续", "Microsoft YaHei", int32(fh*0.035), 400, colWhite, dtCenter|dtVCenter|dtSingleLine)

	// Tell the user how to get out: the overlay no longer closes on any input.
	drawTextIn(hdc, int32(fw*0.120), int32(fh*0.902), int32(fw*0.880), int32(fh*0.932),
		hintText, "Microsoft YaHei", int32(fh*0.022), 400, colInfo, dtCenter|dtSingleLine)
}

// redButtonRect is the single source of truth for the button geometry, shared
// by painting and hit testing so they can never drift apart.
func redButtonRect(w, h int32) RECT {
	bw := int32(float32(w) * 0.125)
	bh := int32(float32(h) * 0.061)
	bcx := w / 2
	bcy := int32(float32(h) * 0.849)
	return RECT{Left: bcx - bw/2, Top: bcy - bh/2, Right: bcx + bw/2, Bottom: bcy + bh/2}
}

func inRectRed(r RECT, x, y int32) bool {
	return x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom
}

// ---------------------------------------------------------------------------
// Drawing helpers
// ---------------------------------------------------------------------------

func fillSolid(hdc HDC, rc RECT, color uint32) {
	b := createSolidBrush(color)
	fillRect(hdc, &rc, b)
	deleteObject(HGDIOBJ(b))
}

func hline(hdc HDC, x1, x2, y int32, color uint32) {
	b := createSolidBrush(color)
	rc := RECT{Left: x1, Top: y, Right: x2, Bottom: y + 1}
	fillRect(hdc, &rc, b)
	deleteObject(HGDIOBJ(b))
}

func roundRectFill(hdc HDC, rc RECT, fill, edge uint32, radius int32) {
	pen := createPen(psSolid, 1, edge)
	oldPen := selectObject(hdc, HGDIOBJ(pen))
	brush := createSolidBrush(fill)
	oldBrush := selectObject(hdc, HGDIOBJ(brush))
	roundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, radius*2, radius*2)
	selectObject(hdc, oldPen)
	selectObject(hdc, oldBrush)
	deleteObject(HGDIOBJ(pen))
	deleteObject(HGDIOBJ(brush))
}

func drawTextIn(hdc HDC, left, top, right, bottom int32, text, face string, sizePx, weight int32, color uint32, format uint32) {
	if text == "" {
		return
	}
	var lf LOGFONT
	lf.LfHeight = sizePx
	lf.LfWeight = weight
	face16, _ := syscall.UTF16FromString(face)
	for i := 0; i < len(face16) && i < len(lf.LfFaceName)-1; i++ {
		lf.LfFaceName[i] = face16[i]
	}
	hfont := createFontIndirect(&lf)
	old := selectObject(hdc, HGDIOBJ(hfont))
	setBkMode(hdc, transparent)
	setTextColor(hdc, color)
	rc := RECT{Left: left, Top: top, Right: right, Bottom: bottom}
	ptr, _ := syscall.UTF16PtrFromString(text)
	drawTextW(hdc, ptr, -1, &rc, format)
	selectObject(hdc, old)
	deleteObject(HGDIOBJ(hfont))
}

const redClassName = "HuorongACERedScreen"
