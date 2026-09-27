//go:build windows

package ui

import (
	"image/color"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"unsafe"

	"fyne.io/fyne/v2"

	"huorong-ace/internal/config"
	"huorong-ace/internal/monitor"
)

// Native Win32 settings window, custom drawn.
//
// Chrome (background, sidebar, cards, separators, switches, buttons) is drawn
// with GDI so we control the look precisely, while text entry uses real EDIT
// controls so IME / clipboard / selection behave natively.

const (
	settingsClassName = "HuorongACESettings"

	// Design size of the CLIENT area; the outer window is derived from it with
	// AdjustWindowRectEx so the layout always gets exactly this much room.
	stgClientW = 900
	stgClientH = 520

	stgSidebarW = 236
	stgPadX     = 32
	stgPadY     = 28

	stgCardRadius = 10
	stgRowH       = 58
	stgNavH       = 34
	stgEditH      = 26
	stgFieldPad   = 3 // how far the drawn field sticks out past a native EDIT
	stgStatusH    = 48

	stgSearchY = 60
	stgSearchH = 32

	stgSwitchW = 42
	stgSwitchH = 22

	// uiFace is the UI font face; every drawn string uses it.
	uiFace = "Microsoft YaHei"
	// iconFace ships with Windows 10+ and carries the system glyph set.
	iconFace = "Segoe MDL2 Assets"

	wmSize         = 0x0005
	wmClose        = 0x0010
	wmCommand      = 0x0111
	wmCtlColorEdit = 0x0133
	wmTimer        = 0x0113
	wmMouseMove    = 0x0200
	wmMouseLeave   = 0x02A3
	wmSetFont      = 0x0030
	wmGetText      = 0x000D
	wmGetTextLen   = 0x000E

	wsChild        = 0x40000000
	wsTabStop      = 0x00010000
	wsCaption      = 0x00C00000
	wsSysMenu      = 0x00080000
	wsMinimize     = 0x00020000
	wsClipChildren = 0x02000000

	esAutoHScroll = 0x0080

	srcCopy = 0x00CC0020 // SRCCOPY

	swHide = 0

	vkEscape = 0x1B

	stgTimerStatus = 1

	enSetFocus  = 0x0100
	enKillFocus = 0x0200
	enChange    = 0x0300

	msgStgShow = wmUser + 5
	msgStgHide = wmUser + 6

	// GetOpenFileName flags
	ofnFileMustExist = 0x00001000
	ofnHideReadOnly  = 0x00000004
	ofnExplorer      = 0x00080000
)

// settingsStyle is a fixed-size captioned top-level window. Resizing is left
// out on purpose: the layout is tuned for one size, and a fixed dialog avoids
// both the WM_GETMINMAXINFO pointer plumbing and squeezed layouts.
const settingsStyle = wsCaption | wsSysMenu | wsMinimize | wsVisible | wsClipChildren

// Colours for the settings window.
var (
	stgTextStrong = rgb(0x1C, 0x1C, 0x1E)
	stgTextDim    = rgb(0x8A, 0x8A, 0x90)
	stgCardFill   = rgb(0xFF, 0xFF, 0xFF)
	stgCardStroke = rgb(0xE6, 0xE6, 0xEA)
	stgPageFill   = rgb(0xF6, 0xF6, 0xF8)
	stgSideFill   = rgb(0xF1, 0xF1, 0xF4)
	stgDivider    = rgb(0xE3, 0xE3, 0xE8)
	stgNavOn      = rgb(0xE4, 0xEC, 0xFC)
	stgNavHover   = rgb(0xE8, 0xE8, 0xED)
	stgBtnFill    = rgb(0xFF, 0xFF, 0xFF)
	stgBtnStroke  = rgb(0xD2, 0xD2, 0xDA)
	stgBtnHover   = rgb(0xF1, 0xF6, 0xFE)
	stgAccent     = rgb(0x2F, 0x6F, 0xED)
	stgTrackOff   = rgb(0xC9, 0xC9, 0xCE)
	stgKnob       = rgb(0xFF, 0xFF, 0xFF)
	stgSelectText = rgb(0x1E, 0x4E, 0xA8)
	stgShadow     = rgb(0xE9, 0xE9, 0xEF)
	stgFieldEdge  = rgb(0xD6, 0xD6, 0xDC)
	stgDotOK      = rgb(0x1F, 0xA9, 0x71)
	stgDotWarn    = rgb(0xE0, 0xA3, 0x2E)
	stgDotIdle    = rgb(0x9A, 0x9A, 0xA0)
	stgSearchMark = rgb(0x9A, 0x9A, 0xA0)
)

// ---------------------------------------------------------------------------
// Extra Win32 bindings needed only by the settings window
// ---------------------------------------------------------------------------

var (
	pMoveWindow        = user32.NewProc("MoveWindow")
	pSetTimer2         = user32.NewProc("SetTimer")
	pKillTimer2        = user32.NewProc("KillTimer")
	pSendMessageW      = user32.NewProc("SendMessageW")
	pMessageBoxW       = user32.NewProc("MessageBoxW")
	pTrackMouseEvt     = user32.NewProc("TrackMouseEvent")
	pSetBkColor        = gdi32.NewProc("SetBkColor")
	pAdjustWinRect     = user32.NewProc("AdjustWindowRectEx")
	pCreateIconFromRes = user32.NewProc("CreateIconFromResourceEx")
	pGetOpenFileNameW  = syscall.NewLazyDLL("comdlg32.dll").NewProc("GetOpenFileNameW")

	// Offscreen buffer used to keep repaints flicker-free.
	pCreateCompatDC  = gdi32.NewProc("CreateCompatibleDC")
	pCreateCompatBmp = gdi32.NewProc("CreateCompatibleBitmap")
	pBitBlt          = gdi32.NewProc("BitBlt")
	pDeleteDC        = gdi32.NewProc("DeleteDC")
)

func createCompatibleDC(hdc HDC) HDC {
	r, _, _ := pCreateCompatDC.Call(uintptr(hdc))
	return HDC(r)
}

func createCompatibleBitmap(hdc HDC, w, h int32) HGDIOBJ {
	r, _, _ := pCreateCompatBmp.Call(uintptr(hdc), uintptr(uint32(w)), uintptr(uint32(h)))
	return HGDIOBJ(r)
}

func bitBlt(dst HDC, x, y, w, h int32, src HDC, sx, sy int32) {
	pBitBlt.Call(uintptr(dst), uintptr(uint32(x)), uintptr(uint32(y)),
		uintptr(uint32(w)), uintptr(uint32(h)),
		uintptr(src), uintptr(uint32(sx)), uintptr(uint32(sy)), srcCopy)
}

func deleteDC(hdc HDC) { pDeleteDC.Call(uintptr(hdc)) }

// adjustWindowRect grows a client-area rect into the full window rect that the
// given window style needs, so the client area keeps the designed size on any
// frame/DPI configuration.
func adjustWindowRect(rc *RECT, style uint32, menu bool, exStyle uint32) {
	m := 0
	if menu {
		m = 1
	}
	pAdjustWinRect.Call(uintptr(unsafe.Pointer(rc)), uintptr(style), uintptr(m), uintptr(exStyle))
}

func setBkColor(hdc HDC, color uint32) uint32 {
	r, _, _ := pSetBkColor.Call(uintptr(hdc), uintptr(color))
	return uint32(r)
}

// loadAppIcon turns the generated shield PNG into an HICON. Windows Vista and
// later accept a PNG payload here, so no .ico encoding is needed. Returns 0
// when the conversion is not supported, in which case the window keeps the
// default icon.
func loadAppIcon() HICON {
	pngBytes := GenShieldPNG(32, color.NRGBA{R: 0xC0, A: 0xFF}, nil)
	if len(pngBytes) == 0 {
		return 0
	}
	const (
		fIcon          = 1
		version30000   = 0x00030000
		lrDefaultColor = 0
		lrDefaultSize  = 0x00000040
	)
	r, _, _ := pCreateIconFromRes.Call(
		uintptr(unsafe.Pointer(&pngBytes[0])), uintptr(len(pngBytes)),
		fIcon, version30000, 0, 0, lrDefaultColor|lrDefaultSize,
	)
	return HICON(r)
}

// drawUIText draws a single run of text where px is the em height in pixels.
//
// drawTextIn passes a positive LOGFONT height, which means "character cell
// height" to GDI and therefore renders noticeably smaller than the requested
// number. UI text here passes a negative height so px is the real, visually
// expected size.
func drawUIText(hdc HDC, left, top, right, bottom int32, text, face string, px, weight int32, color uint32, format uint32) {
	if text == "" {
		return
	}
	var lf LOGFONT
	lf.LfHeight = -px
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

func moveWindow(hwnd HWND, x, y, w, h int32, repaint bool) {
	b := 0
	if repaint {
		b = 1
	}
	pMoveWindow.Call(uintptr(hwnd), uintptr(uint32(x)), uintptr(uint32(y)),
		uintptr(uint32(w)), uintptr(uint32(h)), uintptr(b))
}

func setTimer2(hwnd HWND, id uintptr, ms uint32) {
	pSetTimer2.Call(uintptr(hwnd), id, uintptr(ms), 0)
}

func killTimer2(hwnd HWND, id uintptr) { pKillTimer2.Call(uintptr(hwnd), id) }

func sendMessage(hwnd HWND, msg uint32, wparam, lparam uintptr) uintptr {
	r, _, _ := pSendMessageW.Call(uintptr(hwnd), uintptr(msg), wparam, lparam)
	return r
}

func messageBox(owner HWND, title, text string) {
	t, _ := syscall.UTF16PtrFromString(title)
	c, _ := syscall.UTF16PtrFromString(text)
	pMessageBoxW.Call(uintptr(owner), uintptr(unsafe.Pointer(c)), uintptr(unsafe.Pointer(t)), 0)
}

// TRACKMOUSEEVENT asks Windows to post WM_MOUSELEAVE when the cursor leaves.
type TRACKMOUSEEVENT struct {
	CbSize      uint32
	DwFlags     uint32
	HwndTrack   HWND
	DwHoverTime uint32
}

const tmeLeave = 0x00000002

func trackMouseLeave(hwnd HWND) {
	var t TRACKMOUSEEVENT
	t.CbSize = uint32(unsafe.Sizeof(t))
	t.DwFlags = tmeLeave
	t.HwndTrack = hwnd
	pTrackMouseEvt.Call(uintptr(unsafe.Pointer(&t)))
}

// OPENFILENAMEW mirrors commdlg.h (152 bytes on x64).
type OPENFILENAMEW struct {
	LStructSize       uint32
	HwndOwner         HWND
	HInstance         HINSTANCE
	LpstrFilter       *uint16
	LpstrCustomFilter *uint16
	NMaxCustFilter    uint32
	NFilterIndex      uint32
	LpstrFile         *uint16
	NMaxFile          uint32
	LpstrFileTitle    *uint16
	NMaxFileTitle     uint32
	LpstrInitialDir   *uint16
	LpstrTitle        *uint16
	Flags             uint32
	NFileOffset       uint16
	NFileExtension    uint16
	LpstrDefExt       *uint16
	LCustData         uintptr
	LpfnHook          uintptr
	LpTemplateName    *uint16
	PvReserved        uintptr
	DwReserved        uint32
	FlagsEx           uint32
}

// browseForFile shows the native open-file dialog and returns the chosen path.
func browseForFile(owner HWND) string {
	filter, _ := syscall.UTF16FromString("数据库文件 (*.db)\x00*.db\x00所有文件 (*.*)\x00*.*\x00\x00")
	buf := make([]uint16, 260)
	title, _ := syscall.UTF16PtrFromString("选择火绒日志文件")

	var ofn OPENFILENAMEW
	ofn.LStructSize = uint32(unsafe.Sizeof(ofn))
	ofn.HwndOwner = owner
	ofn.LpstrFilter = &filter[0]
	ofn.LpstrFile = &buf[0]
	ofn.NMaxFile = uint32(len(buf))
	ofn.LpstrTitle = title
	ofn.Flags = ofnFileMustExist | ofnHideReadOnly | ofnExplorer

	r, _, _ := pGetOpenFileNameW.Call(uintptr(unsafe.Pointer(&ofn)))
	if r == 0 {
		return ""
	}
	return syscall.UTF16ToString(buf)
}

func getEditText(hwnd HWND) string {
	n := sendMessage(hwnd, wmGetTextLen, 0, 0)
	if n <= 0 {
		return ""
	}
	buf := make([]uint16, int(n)+2)
	r := sendMessage(hwnd, wmGetText, uintptr(int(n)+1), uintptr(unsafe.Pointer(&buf[0])))
	if r == 0 {
		return ""
	}
	return syscall.UTF16ToString(buf)
}

func setEditText(hwnd HWND, s string) {
	p, _ := syscall.UTF16PtrFromString(s)
	sendMessage(hwnd, 0x000C, 0, uintptr(unsafe.Pointer(p))) // WM_SETTEXT
}

// ---------------------------------------------------------------------------
// Layout model
// ---------------------------------------------------------------------------

type stgRowKind int

const (
	rowNone stgRowKind = iota
	rowSwitch
	rowPollEdit
	rowKwEdit
	rowButton
)

type stgRowDef struct {
	title    string
	desc     string
	kind     stgRowKind
	btnLabel string
	act      string
}

type stgRow struct {
	title    string
	desc     string
	kind     stgRowKind
	btnLabel string
	rect     RECT
	ctrl     RECT
	act      string
	hover    bool
}

type stgCard struct {
	rect RECT
	rows []stgRow
}

type stgSection struct {
	text string
	y    int32
}

type stgNav struct {
	label string
	page  int
	group int
	rect  RECT
	shown bool
	hover bool
}

// ---------------------------------------------------------------------------
// Manage
// ---------------------------------------------------------------------------

type Manage struct {
	mu  sync.Mutex
	app fyne.App
	cfg *config.Config
	mon *monitor.Monitor

	hwnd     HWND
	shown    bool
	starting bool
	pending  bool

	page        int
	statusTxt   string
	statusLevel int // 0 idle, 1 warning, 2 running
	pathTxt     string
	enabled     bool
	filter      string

	searchHwnd HWND
	pollHwnd   HWND
	kwHwnd     HWND
	editFont   HGDIOBJ
	editBrush  HBRUSH
	focusEdit  HWND

	pollRect   RECT
	kwRect     RECT
	switchR    RECT
	searchRect RECT

	// last applied geometry and visibility of the native edit controls
	// (guards against flicker caused by redundant window operations)
	searchPlaced editState
	pollPlaced   editState
	kwPlaced     editState

	navs      []*stgNav
	cards     []stgCard
	sections  []stgSection
	groupYs   []stgGroup
	switchHov bool
}

// editState remembers what was last applied to a native EDIT child, so layout
// (which runs on every paint) only touches it when something really moved.
type editState struct {
	rect  RECT
	shown bool
}

type stgGroup struct {
	text    string
	y       int32
	visible bool
}

// NewManage creates the settings window (created on first Show).
func NewManage(app fyne.App, cfg *config.Config, mon *monitor.Monitor) *Manage {
	return &Manage{
		app: app, cfg: cfg, mon: mon,
		pathTxt: cfg.LogPath,
		enabled: mon.Enabled(),
		navs: []*stgNav{
			{label: "监控", page: 0, group: 0},
			{label: "测试与保存", page: 1, group: 0},
			{label: "使用说明", page: 2, group: 1},
		},
	}
}

// Show brings the window to the centre of the screen.
func (m *Manage) Show() {
	m.mu.Lock()
	m.shown = true
	hwnd := m.hwnd
	if hwnd == 0 {
		if !m.starting {
			m.starting = true
			m.pending = true
			m.mu.Unlock()
			go m.run()
			return
		}
		m.pending = true
		m.mu.Unlock()
		return
	}
	m.mu.Unlock()
	postMessageW(hwnd, msgStgShow, 0, 0)
}

// Close hides the settings window.
func (m *Manage) Close() {
	m.mu.Lock()
	m.shown = false
	hwnd := m.hwnd
	m.mu.Unlock()
	if hwnd != 0 {
		postMessageW(hwnd, msgStgHide, 0, 0)
	}
}

func (m *Manage) run() {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()

	hInstance := getModuleHandle()
	cls, _ := syscall.UTF16PtrFromString(settingsClassName)

	var wc WNDCLASSEX
	wc.CbSize = uint32(unsafe.Sizeof(wc))
	wc.Style = csHRedraw | csVRedraw
	wc.LpfnWndProc = syscall.NewCallback(m.wndProc)
	wc.HInstance = HINSTANCE(hInstance)
	wc.HCursor = loadCursor(0, idcArrow)
	wc.HbrBackground = 0
	wc.LpszClassName = cls
	icon := loadAppIcon()
	wc.HIcon = icon
	wc.HIconSm = icon
	registerClassEx(&wc)

	const style = settingsStyle

	// Turn the designed client size into the matching outer window size.
	frame := RECT{0, 0, stgClientW, stgClientH}
	adjustWindowRect(&frame, style, false, 0)
	winW := frame.Right - frame.Left
	winH := frame.Bottom - frame.Top

	sw := getSystemMetrics(smCXScreen)
	sh := getSystemMetrics(smCYScreen)
	x := (sw - winW) / 2
	y := (sh - winH) / 2
	if x < 0 {
		x = 0
	}
	if y < 0 {
		y = 0
	}

	hwnd := createWindowEx(
		0,
		cls, nil, style,
		x, y, winW, winH,
		0, 0, HINSTANCE(hInstance), 0,
	)
	if hwnd == 0 {
		m.mu.Lock()
		m.shown = false
		m.starting = false
		m.mu.Unlock()
		return
	}

	title, _ := syscall.UTF16PtrFromString("火绒ACE")
	sendMessage(hwnd, 0x000C, 0, uintptr(unsafe.Pointer(title)))

	// Persistent font + background brush for the native edit controls. A
	// negative height asks GDI for a 14px em, matching the drawn text.
	var lf LOGFONT
	lf.LfHeight = -14
	face, _ := syscall.UTF16FromString(uiFace)
	for i := 0; i < len(face) && i < len(lf.LfFaceName)-1; i++ {
		lf.LfFaceName[i] = face[i]
	}
	m.editFont = createFontIndirect(&lf)
	m.editBrush = createSolidBrush(stgCardFill)

	m.searchHwnd = m.newEdit(hwnd, "", 0)
	m.pollHwnd = m.newEdit(hwnd, strconv.Itoa(m.cfg.PollInterval), esAutoHScroll)
	m.kwHwnd = m.newEdit(hwnd, strings.Join(m.cfg.Keywords, ", "), esAutoHScroll)

	m.mu.Lock()
	m.hwnd = hwnd
	show := m.pending
	m.pending = false
	m.mu.Unlock()

	if show {
		showWindow(hwnd, swShow)
		setForegroundWindow(hwnd)
	} else {
		showWindow(hwnd, swHide)
	}

	m.updateStatus()
	setTimer2(hwnd, stgTimerStatus, 1000)

	var msg MSG
	for getMessage(&msg) != 0 {
		translateMessage(&msg)
		dispatchMessage(&msg)
	}

	m.mu.Lock()
	m.hwnd = 0
	m.starting = false
	m.mu.Unlock()
}

// newEdit creates a borderless EDIT: the visible field is a rounded rectangle
// painted by the parent, which the control sits inside of. A native sunken
// border would clash with the flat design.
func (m *Manage) newEdit(parent HWND, text string, extra uint32) HWND {
	name, _ := syscall.UTF16PtrFromString("EDIT")
	txt, _ := syscall.UTF16PtrFromString(text)
	h := createWindowEx(
		0,
		name, txt,
		wsChild|wsVisible|wsTabStop|extra,
		0, 0, 100, stgEditH,
		parent, 0, HINSTANCE(getModuleHandle()), 0,
	)
	if h != 0 && m.editFont != 0 {
		sendMessage(h, wmSetFont, uintptr(m.editFont), 1)
	}
	return h
}

// updateStatus refreshes the status strip and reports whether anything visible
// changed, so callers can skip a repaint when nothing did.
func (m *Manage) updateStatus() bool {
	var s string
	level := 0
	switch {
	case !m.mon.HuorongOK():
		s = "未检测到火绒日志，当前为测试模式"
		level = 1
	case m.mon.Enabled():
		s = "监控运行中，正在监听火绒检测事件"
		level = 2
	default:
		s = "监控已停止"
	}
	if info, t := m.mon.LastDetected(); !t.IsZero() {
		s += "　·　上次检测 " + info.Name + "（" + t.Format("15:04:05") + "）"
	}
	m.mu.Lock()
	changed := m.statusTxt != s || m.statusLevel != level || m.enabled != m.mon.Enabled()
	m.statusTxt = s
	m.statusLevel = level
	m.enabled = m.mon.Enabled()
	m.mu.Unlock()
	return changed
}

func (m *Manage) wndProc(hwnd, msg, wparam, lparam uintptr) uintptr {
	h := HWND(hwnd)
	switch msg {
	case wmEraseBkgnd:
		var rc RECT
		getClientRect(h, &rc)
		fillSolid(HDC(wparam), rc, stgPageFill)
		return 1
	case wmPaint:
		m.onPaint(h)
		return 0
	case wmSize:
		invalidateRect(h, nil, false)
		return 0
	case wmTimer:
		// Only repaint when the status actually changed; a blind repaint once a
		// second is wasted work and makes the window shimmer.
		if m.updateStatus() {
			invalidateRect(h, nil, false)
		}
		return 0
	case wmCtlColorEdit:
		// Force the edit background to our card white regardless of the
		// user's Windows theme, so the drawn field and the control blend.
		setBkColor(HDC(wparam), stgCardFill)
		setTextColor(HDC(wparam), stgTextStrong)
		return uintptr(m.editBrush)
	case wmCommand:
		code := (wparam >> 16) & 0xFFFF
		id := HWND(lparam)
		switch code {
		case enChange:
			// Search box: filters the sidebar navigation.
			if id == m.searchHwnd {
				m.mu.Lock()
				m.filter = getEditText(m.searchHwnd)
				m.mu.Unlock()
				invalidateRect(h, nil, false)
			}
		case enSetFocus:
			m.focusEdit = id
			invalidateRect(h, nil, false)
		case enKillFocus:
			if m.focusEdit == id {
				m.focusEdit = 0
			}
			invalidateRect(h, nil, false)
		}
		return 0
	case wmMouseMove:
		x := int32(int16(uint32(lparam) & 0xFFFF))
		y := int32(int16(uint32(lparam) >> 16))
		if m.hitTest(x, y) {
			invalidateRect(h, nil, false)
		}
		trackMouseLeave(h)
		return 0
	case wmMouseLeave:
		if m.hitTest(-1, -1) {
			invalidateRect(h, nil, false)
		}
		return 0
	case wmLButtonDown:
		m.onClick(h, int32(int16(uint32(lparam)&0xFFFF)), int32(int16(uint32(lparam)>>16)))
		return 0
	case wmKeyDown:
		if wparam == vkEscape {
			showWindow(h, swHide)
			m.mu.Lock()
			m.shown = false
			m.mu.Unlock()
		}
		return 0
	case msgStgShow:
		showWindow(h, swShow)
		setForegroundWindow(h)
		m.updateStatus()
		invalidateRect(h, nil, false)
		return 0
	case msgStgHide:
		showWindow(h, swHide)
		return 0
	case wmClose:
		showWindow(h, swHide)
		m.mu.Lock()
		m.shown = false
		m.mu.Unlock()
		return 0
	case wmDestroy:
		killTimer2(h, stgTimerStatus)
		postQuitMessage(0)
		return 0
	}
	return defWindowProc(hwnd, msg, wparam, lparam)
}

// hitTest updates the hover state of every interactive element and reports
// whether anything changed (so the caller only repaints when needed).
func (m *Manage) hitTest(x, y int32) bool {
	changed := false
	set := func(old, now bool) bool {
		if old != now {
			changed = true
		}
		return now
	}
	for _, n := range m.navs {
		n.hover = set(n.hover, n.shown && inRect(n.rect, x, y))
	}
	for i := range m.cards {
		for j := range m.cards[i].rows {
			r := &m.cards[i].rows[j]
			if r.kind != rowButton {
				continue
			}
			r.hover = set(r.hover, inRect(r.ctrl, x, y))
		}
	}
	m.switchHov = set(m.switchHov, m.page == 0 && inRect(m.switchR, x, y))
	return changed
}

func (m *Manage) onClick(hwnd HWND, x, y int32) {
	// Sidebar navigation.
	for _, n := range m.navs {
		if !n.visible() {
			continue
		}
		if inRect(n.rect, x, y) {
			m.mu.Lock()
			m.page = n.page
			m.mu.Unlock()
			invalidateRect(hwnd, nil, false)
			return
		}
	}
	// In-page buttons.
	for i := range m.cards {
		for j := range m.cards[i].rows {
			r := m.cards[i].rows[j]
			if r.kind == rowButton && inRect(r.ctrl, x, y) {
				m.doAction(hwnd, r.act)
				return
			}
		}
	}
	// Monitoring switch.
	if m.page == 0 && inRect(m.switchR, x, y) {
		m.mu.Lock()
		m.enabled = !m.enabled
		on := m.enabled
		m.mu.Unlock()
		m.mon.SetEnabled(on)
		m.updateStatus()
		invalidateRect(hwnd, nil, false)
	}
}

func (m *Manage) doAction(hwnd HWND, act string) {
	switch act {
	case "browse":
		if p := browseForFile(hwnd); p != "" {
			m.mu.Lock()
			m.cfg.LogPath = p
			m.pathTxt = p
			m.mu.Unlock()
			// Re-open the database now so the new path takes effect at once.
			if err := m.mon.Reload(p); err != nil {
				messageBox(hwnd, "无法打开该数据库", err.Error())
				return
			}
			m.updateStatus()
		}
	case "test":
		m.mon.Simulate("EICAR-Test-Signature (模拟)")
	case "save":
		if v := atoiSafeWin(getEditText(m.pollHwnd)); v >= 1 {
			m.cfg.PollInterval = v
		}
		m.cfg.Keywords = splitKwWin(getEditText(m.kwHwnd))
		if err := m.cfg.Save(); err != nil {
			messageBox(hwnd, "保存失败", err.Error())
			return
		}
		// Apply the possibly changed path without needing a restart.
		_ = m.mon.Reload(m.cfg.LogPath)
		m.updateStatus()
		messageBox(hwnd, "已保存", "设置已写入 config.json")
	}
	invalidateRect(hwnd, nil, false)
}

func (n *stgNav) visible() bool { return n.shown }

func inRect(r RECT, x, y int32) bool {
	return x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom
}

// ---------------------------------------------------------------------------
// Painting
// ---------------------------------------------------------------------------

// onPaint renders through an offscreen bitmap and blits it in one go.
//
// Painting the chrome directly onto the window DC is the classic source of
// Win32 flicker: the surface is visible while it is being drawn, so partial
// frames show up. Composing offscreen and blitting once removes that entirely.
func (m *Manage) onPaint(hwnd HWND) {
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

	mem := createCompatibleDC(hdc)
	if mem == 0 {
		m.paint(hdc, w, h)
		return
	}
	bmp := createCompatibleBitmap(hdc, w, h)
	prev := selectObject(mem, bmp)
	m.paint(mem, w, h)
	bitBlt(hdc, 0, 0, w, h, mem, 0, 0)
	selectObject(mem, prev)
	deleteObject(bmp)
	deleteDC(mem)
}

func (m *Manage) paint(hdc HDC, w, h int32) {
	fillSolid(hdc, RECT{0, 0, w, h}, stgPageFill)

	m.layout(w, h)

	// --- sidebar ---
	fillSolid(hdc, RECT{0, 0, stgSidebarW, h}, stgSideFill)
	fillSolid(hdc, RECT{stgSidebarW - 1, 0, stgSidebarW, h}, stgDivider)
	// App mark: accent rounded square with a white shield.
	roundRectFill(hdc, RECT{20, 16, 52, 48}, stgAccent, stgAccent, 8)
	drawShieldMarkStg(hdc, 36, 32, 18)
	drawUIText(hdc, 64, 16, stgSidebarW-16, 48, "设置", uiFace, 18, 700, stgTextStrong, dtLeft|dtVCenter|dtSingleLine)

	// Search field: a rounded outline drawn by us, with the borderless native
	// EDIT living inside it.
	m.drawField(hdc, m.searchRect, m.focusEdit == m.searchHwnd)
	drawUIText(hdc, m.searchRect.Left+10, m.searchRect.Top, m.searchRect.Left+28, m.searchRect.Bottom,
		"\uE721", iconFace, 13, 400, stgSearchMark, dtCenter|dtVCenter|dtSingleLine)

	for _, g := range m.groupYs {
		if !g.visible {
			continue
		}
		drawUIText(hdc, 28, g.y, stgSidebarW-12, g.y+20, g.text, uiFace, 12, 700, stgTextDim, dtLeft|dtSingleLine)
	}
	for _, n := range m.navs {
		if !n.shown {
			continue
		}
		fill := uint32(0)
		draw := false
		if n.page == m.page {
			fill, draw = stgNavOn, true
		} else if n.hover {
			fill, draw = stgNavHover, true
		}
		if draw {
			roundRectFill(hdc, n.rect, fill, fill, 8)
		}
		col := stgTextStrong
		weight := int32(400)
		if n.page == m.page {
			col, weight = stgSelectText, 700
		}
		drawUIText(hdc, n.rect.Left+18, n.rect.Top, n.rect.Right-10, n.rect.Bottom,
			n.label, uiFace, 13, weight, col, dtLeft|dtVCenter|dtSingleLine)
	}

	// --- content ---
	x0 := int32(stgSidebarW) + stgPadX
	x1 := w - stgPadX

	m.drawStatusCard(hdc, RECT{x0, stgPadY, x1, stgPadY + stgStatusH})

	for _, s := range m.sections {
		drawUIText(hdc, x0, s.y, x1, s.y+20, s.text, uiFace, 12, 700, stgTextDim, dtLeft|dtSingleLine)
	}

	for _, c := range m.cards {
		// Soft 1px shadow under each card.
		sh := RECT{c.rect.Left + 1, c.rect.Top + 2, c.rect.Right + 1, c.rect.Bottom + 2}
		roundRectFill(hdc, sh, stgShadow, stgShadow, stgCardRadius)
		roundRectFill(hdc, c.rect, stgCardFill, stgCardStroke, stgCardRadius)
		for i, r := range c.rows {
			if i > 0 {
				hline(hdc, r.rect.Left+16, r.rect.Right-16, r.rect.Top, stgCardStroke)
			}
			drawUIText(hdc, r.rect.Left+18, r.rect.Top+12, r.rect.Right-160, r.rect.Top+32,
				r.title, uiFace, 14, 700, stgTextStrong, dtLeft|dtSingleLine)
			if r.desc != "" {
				drawUIText(hdc, r.rect.Left+18, r.rect.Top+33, r.rect.Right-160, r.rect.Top+53,
					r.desc, uiFace, 12, 400, stgTextDim, dtLeft|dtSingleLine)
			}
			switch r.kind {
			case rowSwitch:
				m.drawSwitch(hdc, r.ctrl)
			case rowButton:
				m.drawButton(hdc, r.ctrl, r.btnLabel, r.hover)
			case rowPollEdit, rowKwEdit:
				m.drawField(hdc, r.ctrl, m.focusEdit == m.editForKind(r.kind))
			}
		}
	}

	if m.page == 2 {
		// Multi-line note inside the first card.
		if len(m.cards) > 0 {
			c := m.cards[0]
			drawUIText(hdc, c.rect.Left+20, c.rect.Top+16, c.rect.Right-20, c.rect.Bottom-16,
				noteText, uiFace, 13, 400, stgTextStrong, dtLeft|dtWordBreak)
		}
	}
}

// editForKind maps a row kind to its native edit control.
func (m *Manage) editForKind(k stgRowKind) HWND {
	if k == rowKwEdit {
		return m.kwHwnd
	}
	return m.pollHwnd
}

// drawStatusCard renders the always-visible status strip at the top of the
// content area: a coloured dot plus the current monitoring state.
func (m *Manage) drawStatusCard(hdc HDC, r RECT) {
	roundRectFill(hdc, r, stgCardFill, stgCardStroke, stgCardRadius)

	dot := stgDotIdle
	switch m.statusLevel {
	case 1:
		dot = stgDotWarn
	case 2:
		dot = stgDotOK
	}
	cy := (r.Top + r.Bottom) / 2
	cx := r.Left + 22
	roundRectFill(hdc, RECT{cx - 4, cy - 4, cx + 4, cy + 4}, dot, dot, 4)

	drawUIText(hdc, cx+14, r.Top, r.Right-16, r.Bottom, m.statusTxt,
		uiFace, 13, 400, stgTextStrong, dtLeft|dtVCenter|dtSingleLine)
}

// drawField paints the rounded white rectangle that visually backs a native
// EDIT control. The control is inset by stgFieldPad, so the outline and its
// corner arcs are never covered by the (square) child window.
func (m *Manage) drawField(hdc HDC, r RECT, focused bool) {
	edge := stgFieldEdge
	if focused {
		edge = stgAccent
	}
	roundRectFill(hdc, r, stgCardFill, edge, 6)
}

// drawShieldMarkStg draws a white shield outline centred on (cx, cy).
func drawShieldMarkStg(hdc HDC, cx, cy, size int32) {
	hw := size / 2
	top := cy - size/2
	bot := cy + size/2
	pts := []POINT{
		{X: cx - hw, Y: top},
		{X: cx + hw, Y: top},
		{X: cx + hw, Y: cy - size/8},
		{X: cx, Y: bot},
		{X: cx - hw, Y: cy - size/8},
	}
	pen := createPen(psSolid, 2, stgKnob)
	oldPen := selectObject(hdc, HGDIOBJ(pen))
	brush := createSolidBrush(stgAccent)
	oldBrush := selectObject(hdc, HGDIOBJ(brush))
	polygon(hdc, &pts[0], int32(len(pts)))
	selectObject(hdc, oldPen)
	selectObject(hdc, oldBrush)
	deleteObject(HGDIOBJ(pen))
	deleteObject(HGDIOBJ(brush))
}

func (m *Manage) drawSwitch(hdc HDC, r RECT) {
	track := stgTrackOff
	if m.enabled {
		track = stgAccent
	}
	// Fully rounded pill: the ellipse size equals the rect height.
	roundRectFill(hdc, r, track, track, (r.Bottom-r.Top)/2)

	// Circular knob. GDI's FillRect would render a square, so use RoundRect
	// with a half-diameter radius to get a true circle.
	const pad = 3
	d := (r.Bottom - r.Top) - pad*2
	x := r.Left + pad
	if m.enabled {
		x = r.Right - d - pad
	}
	kr := RECT{x, r.Top + pad, x + d, r.Top + pad + d}
	roundRectFill(hdc, kr, stgKnob, stgKnob, d/2)
}

func (m *Manage) drawButton(hdc HDC, r RECT, label string, hover bool) {
	fill := stgBtnFill
	edge := stgBtnStroke
	if hover {
		fill = stgBtnHover
		edge = stgAccent
	}
	roundRectFill(hdc, r, fill, edge, 8)
	drawUIText(hdc, r.Left, r.Top, r.Right, r.Bottom, label, uiFace, 13, 400, stgTextStrong, dtCenter|dtVCenter|dtSingleLine)
}

// ---------------------------------------------------------------------------
// Layout
// ---------------------------------------------------------------------------

const noteText = "本程序为娱乐 / 演示用途，仅模拟 ACE 反作弊红屏效果，并非真实反作弊系统，也不会与任何游戏交互。\n\n" +
	"红屏弹出后不会自动关闭，需点击「继续」按钮或按 Esc 才会退出。\n\n" +
	"右键点击系统托盘图标，可选择「打开」管理界面或「退出」程序。\n\n" +
	"修改「轮询间隔」与「病毒关键词」后，需要点击「保存设置」写入 config.json 才会长期生效。\n\n" +
	"启动前已存在的检测记录不会弹窗；同一个威胁在三分钟内只提示一次，避免重复打扰。"

func (m *Manage) layout(w, h int32) {
	m.cards = nil
	m.sections = nil
	m.groupYs = nil

	// Sidebar nav placement.
	y := int32(112)
	groupTitles := []string{"常规", "关于"}
	for gi, gt := range groupTitles {
		gy := y
		y += 22 // room for the group header
		any := false
		for _, n := range m.navs {
			if n.group != gi {
				continue
			}
			if !m.matchFilter(n.label) {
				n.shown = false
				n.rect = RECT{}
				continue
			}
			any = true
			n.shown = true
			n.rect = RECT{12, y, stgSidebarW - 12, y + stgNavH}
			y += stgNavH + 2
		}
		m.groupYs = append(m.groupYs, stgGroup{text: gt, y: gy, visible: any})
		if any {
			y += 8
		}
	}

	// Content starts below the always-visible status strip.
	x0 := int32(stgSidebarW) + stgPadX
	x1 := w - stgPadX
	cy := int32(stgPadY) + stgStatusH + 26

	switch m.page {
	case 0:
		m.sections = append(m.sections, stgSection{"常规", cy})
		cy += 24
		cy = m.addCard(x0, x1, cy, []stgRowDef{
			{title: "启用监控", desc: "监控火绒日志，命中关键词时弹出红屏", kind: rowSwitch},
			{title: "轮询间隔", desc: "每隔多少秒检查一次火绒日志", kind: rowPollEdit},
			{title: "病毒关键词", desc: "火绒隔离库记录一律视为威胁；关键词仅用于自定义日志库", kind: rowKwEdit},
		})
		cy += 24
		m.sections = append(m.sections, stgSection{"火绒", cy})
		cy += 24
		cy = m.addCard(x0, x1, cy, []stgRowDef{
			{title: "火绒日志路径", desc: m.pathTxt, kind: rowButton, btnLabel: "选择文件", act: "browse"},
		})
	case 1:
		m.sections = append(m.sections, stgSection{"操作", cy})
		cy += 24
		cy = m.addCard(x0, x1, cy, []stgRowDef{
			{title: "测试红屏", desc: "立即模拟一次检测，验证红屏效果", kind: rowButton, btnLabel: "测试", act: "test"},
			{title: "保存设置", desc: "将当前设置写入 config.json", kind: rowButton, btnLabel: "保存设置", act: "save"},
		})
	case 2:
		m.sections = append(m.sections, stgSection{"说明", cy})
		cy += 24
		m.cards = append(m.cards, stgCard{rect: RECT{x0, cy, x1, cy + 290}})
	}

	// The drawn field rects. Native edits sit inside them, inset by stgFieldPad
	// so the rounded outline stays visible.
	m.searchRect = RECT{12, stgSearchY, stgSidebarW - 12, stgSearchY + stgSearchH}
	searchInner := RECT{
		m.searchRect.Left + 30,
		m.searchRect.Top + stgFieldPad + 1,
		m.searchRect.Right - 10,
		m.searchRect.Bottom - stgFieldPad - 1,
	}
	m.placeEdit(m.searchHwnd, &m.searchPlaced, searchInner, true)

	showPoll := m.page == 0
	// pollRect / kwRect hold the drawn field; the control is inset inside it.
	m.placeEdit(m.pollHwnd, &m.pollPlaced, inset(m.pollRect, stgFieldPad, 3), showPoll)
	m.placeEdit(m.kwHwnd, &m.kwPlaced, inset(m.kwRect, stgFieldPad, 3), showPoll)
}

func inset(r RECT, dx, dy int32) RECT {
	return RECT{r.Left + dx, r.Top + dy, r.Right - dx, r.Bottom - dy}
}

// placeEdit moves / shows a native EDIT control only when its geometry changed.
// Repainting happens on every mouse move, and unconditionally re-positioning a
// child window there would make it flicker.
func (m *Manage) placeEdit(hwnd HWND, st *editState, r RECT, visible bool) {
	if hwnd == 0 {
		return
	}
	if r != st.rect {
		st.rect = r
		moveWindow(hwnd, r.Left, r.Top, r.Right-r.Left, r.Bottom-r.Top, true)
	}
	if visible != st.shown {
		st.shown = visible
		showWindow(hwnd, cond(visible, swShow, swHide))
	}
}

func cond(b bool, t, f int32) int32 {
	if b {
		return t
	}
	return f
}

func (m *Manage) matchFilter(label string) bool {
	if m.filter == "" {
		return true
	}
	return strings.Contains(strings.ToLower(label), strings.ToLower(m.filter))
}

func (m *Manage) addCard(x0, x1, y int32, defs []stgRowDef) int32 {
	cardH := int32(6) + int32(len(defs))*(stgRowH+1) + 6
	card := stgCard{rect: RECT{x0, y, x1, y + cardH}}
	for i, d := range defs {
		ry := y + 6 + int32(i)*(stgRowH+1)
		row := stgRow{
			title: d.title, desc: d.desc, kind: d.kind,
			btnLabel: d.btnLabel, act: d.act,
			rect: RECT{x0, ry, x1, ry + stgRowH},
		}
		switch d.kind {
		case rowSwitch:
			row.ctrl = RECT{x1 - 16 - stgSwitchW, ry + (stgRowH-stgSwitchH)/2, x1 - 16, ry + (stgRowH-stgSwitchH)/2 + stgSwitchH}
			m.switchR = row.ctrl
		case rowPollEdit:
			row.ctrl = RECT{x1 - 16 - 120, ry + (stgRowH-stgEditH)/2, x1 - 16, ry + (stgRowH-stgEditH)/2 + stgEditH}
			m.pollRect = row.ctrl
		case rowKwEdit:
			row.ctrl = RECT{x1 - 16 - 260, ry + (stgRowH-stgEditH)/2, x1 - 16, ry + (stgRowH-stgEditH)/2 + stgEditH}
			m.kwRect = row.ctrl
		case rowButton:
			row.ctrl = RECT{x1 - 16 - 104, ry + (stgRowH-32)/2, x1 - 16, ry + (stgRowH-32)/2 + 32}
		}
		card.rows = append(card.rows, row)
	}
	m.cards = append(m.cards, card)
	return y + cardH
}

func atoiSafeWin(s string) int {
	n, err := strconv.Atoi(strings.TrimSpace(s))
	if err != nil {
		return 0
	}
	return n
}

func splitKwWin(s string) []string {
	parts := strings.Split(s, ",")
	out := make([]string, 0, len(parts))
	for _, p := range parts {
		p = strings.TrimSpace(p)
		if p != "" {
			out = append(out, p)
		}
	}
	return out
}
