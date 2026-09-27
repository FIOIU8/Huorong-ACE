//go:build !windows

package ui

import (
	"fmt"
	"image/color"
	"strconv"
	"strings"
	"time"

	"fyne.io/fyne/v2"
	"fyne.io/fyne/v2/canvas"
	"fyne.io/fyne/v2/container"
	"fyne.io/fyne/v2/dialog"
	"fyne.io/fyne/v2/layout"
	"fyne.io/fyne/v2/theme"
	"fyne.io/fyne/v2/widget"

	"huorong-ace/internal/config"
	"huorong-ace/internal/monitor"
)

// Palette for the settings window (light, matching the reference design).
var (
	mgrTextStrong = color.NRGBA{0x1C, 0x1C, 0x1E, 0xFF}
	mgrTextDim    = color.NRGBA{0x8A, 0x8A, 0x90, 0xFF}
	mgrCardFill   = color.NRGBA{0xFF, 0xFF, 0xFF, 0xFF}
	mgrCardStroke = color.NRGBA{0xE6, 0xE6, 0xEA, 0xFF}
	mgrPageFill   = color.NRGBA{0xF6, 0xF6, 0xF8, 0xFF}
	mgrSideFill   = color.NRGBA{0xF1, 0xF1, 0xF4, 0xFF}
	mgrDivider    = color.NRGBA{0xE3, 0xE3, 0xE8, 0xFF}
	mgrNavOn      = color.NRGBA{0xE4, 0xEC, 0xFC, 0xFF}
)

const sidebarWidth = 236

// Manage is the settings window surfaced from the tray menu.
type Manage struct {
	win           fyne.Window
	cfg           *config.Config
	mon           *monitor.Monitor
	status        *canvas.Text
	pollEntry     *widget.Entry
	kwEntry       *widget.Entry
	pathDesc      *canvas.Text
	enabledSwitch *switchWidget
	shown         bool

	selected  int
	pages     []fyne.CanvasObject
	navItems  []*navItem
	navGroups []*mgrGroup
}

// navItem is a sidebar entry: a transparent button over a rounded highlight.
type navItem struct {
	root *fyne.Container
	bg   *canvas.Rectangle
	btn  *widget.Button
}

// mgrGroup is a sidebar section: a header plus its navigation entries.
type mgrGroup struct {
	header *canvas.Text
	items  []*navItem
}

// NewManage creates the settings window (hidden until Show is called).
func NewManage(app fyne.App, cfg *config.Config, mon *monitor.Monitor) *Manage {
	var m *Manage

	w := app.NewWindow("火绒ACE")
	w.Resize(fyne.NewSize(940, 660))

	// --- widgets -----------------------------------------------------------
	status := dimText("", 12)
	pathDesc := dimText(cfg.LogPath, 12)

	pollEntry := widget.NewEntry()
	pollEntry.SetText(strconv.Itoa(cfg.PollInterval))
	kwEntry := widget.NewEntry()
	kwEntry.SetText(strings.Join(cfg.Keywords, ", "))

	enabledSwitch := newSwitch(mon.Enabled(), func(v bool) {
		mon.SetEnabled(v)
	})

	browseBtn := widget.NewButtonWithIcon("选择文件", theme.FolderIcon(), func() {
		d := dialog.NewFileOpen(func(rc fyne.URIReadCloser, err error) {
			if err != nil || rc == nil {
				return
			}
			defer rc.Close()
			cfg.LogPath = rc.URI().Path()
			pathDesc.Text = cfg.LogPath
			pathDesc.Refresh()
		}, w)
		d.Show()
	})

	testBtn := widget.NewButtonWithIcon("测试", theme.MediaPlayIcon(), func() {
		mon.Simulate("EICAR-Test-Signature (模拟)")
	})

	saveBtn := widget.NewButtonWithIcon("保存设置", theme.DocumentIcon(), func() {
		if v := atoiSafe(pollEntry.Text); v >= 1 {
			cfg.PollInterval = v
		}
		cfg.Keywords = splitKw(kwEntry.Text)
		if err := cfg.Save(); err != nil {
			dialog.ShowError(err, w)
			return
		}
		dialog.ShowInformation("已保存", "设置已写入 config.json", w)
	})

	// --- pages -------------------------------------------------------------
	pageGeneral := scrollPage(
		status,
		sectionTitle("常规"),
		makeCard(
			makeRow("启用监控", dimText("监控火绒日志，命中关键词时弹出红屏", 12), enabledSwitch),
			cardSep(),
			makeRow("轮询间隔", dimText("每隔多少秒检查一次火绒日志", 12), fixedWidth(120, 36, pollEntry)),
			cardSep(),
			makeRow("病毒关键词", dimText("命中任一关键词即触发，英文逗号分隔", 12), fixedWidth(260, 36, kwEntry)),
		),
		sectionTitle("火绒"),
		makeCard(
			makeRow("火绒日志路径", pathDesc, browseBtn),
		),
	)

	pageActions := scrollPage(
		sectionTitle("操作"),
		makeCard(
			makeRow("测试红屏", dimText("立即模拟一次检测，验证红屏效果", 12), testBtn),
			cardSep(),
			makeRow("保存设置", dimText("将当前设置写入 config.json", 12), saveBtn),
		),
	)

	note := widget.NewLabel("本程序为娱乐 / 演示用途，仅模拟 ACE 反作弊红屏效果，并非真实反作弊系统，也不会与任何游戏交互。\n\n红屏弹出后不会自动关闭，需按任意键或点击鼠标退出。\n\n右键点击系统托盘图标，可选择「打开」管理界面或「退出」程序。")
	note.Wrapping = fyne.TextWrapWord

	pageAbout := scrollPage(
		sectionTitle("说明"),
		makeCard(container.New(layout.NewCustomPaddedLayout(6, 6, 16, 16), note)),
	)

	pages := []fyne.CanvasObject{pageGeneral, pageActions, pageAbout}

	// --- sidebar -----------------------------------------------------------
	type navDef struct {
		label string
		icon  fyne.Resource
		group int
	}
	groupTitles := []string{"常规", "关于"}
	defs := []navDef{
		{"监控", theme.SettingsIcon(), 0},
		{"测试与保存", theme.MediaPlayIcon(), 0},
		{"使用说明", theme.InfoIcon(), 1},
	}

	groups := make([]*mgrGroup, len(groupTitles))
	for i, t := range groupTitles {
		h := sectionTitle(t)
		groups[i] = &mgrGroup{header: h}
	}

	var navItems []*navItem
	for i, d := range defs {
		idx := i
		btn := widget.NewButtonWithIcon(d.label, d.icon, func() { m.selectPage(idx) })
		btn.Alignment = widget.ButtonAlignLeading
		btn.Importance = widget.LowImportance

		bg := canvas.NewRectangle(color.Transparent)
		bg.CornerRadius = 8
		root := container.NewStack(bg, btn)

		it := &navItem{root: root, bg: bg, btn: btn}
		navItems = append(navItems, it)
		groups[d.group].items = append(groups[d.group].items, it)
	}

	searchEntry := widget.NewEntry()
	searchEntry.SetPlaceHolder("搜索设置")
	searchBox := container.NewBorder(nil, nil, container.New(layout.NewCustomPaddedLayout(0, 0, 6, 0), widget.NewIcon(theme.SearchIcon())), nil, searchEntry)
	searchEntry.OnChanged = func(q string) {
		q = strings.ToLower(strings.TrimSpace(q))
		for _, g := range groups {
			any := false
			for _, it := range g.items {
				if q == "" || strings.Contains(strings.ToLower(it.btn.Text), q) {
					it.root.Show()
					any = true
				} else {
					it.root.Hide()
				}
			}
			if any {
				g.header.Show()
			} else {
				g.header.Hide()
			}
		}
	}

	sideItems := []fyne.CanvasObject{strongText("设置", 20, true), searchBox, vgap(6)}
	for _, g := range groups {
		sideItems = append(sideItems, container.New(layout.NewCustomPaddedLayout(4, 0, 4, 0), g.header))
		for _, it := range g.items {
			sideItems = append(sideItems, it.root)
		}
		sideItems = append(sideItems, vgap(6))
	}

	divider := canvas.NewRectangle(mgrDivider)
	divider.SetMinSize(fyne.NewSize(1, 1))

	sideBg := canvas.NewRectangle(mgrSideFill)
	sideInner := container.NewBorder(nil, nil, nil, divider,
		container.New(layout.NewCustomPaddedLayout(16, 16, 14, 14), container.NewVBox(sideItems...)))
	sidebar := container.New(&fixedWidthLayout{width: sidebarWidth}, container.NewStack(sideBg, sideInner))

	pageBg := canvas.NewRectangle(mgrPageFill)
	content := container.NewStack(pageBg, container.NewStack(pages...))

	root := container.NewBorder(nil, nil, sidebar, nil, content)

	w.SetContent(root)
	w.SetCloseIntercept(func() {
		m.shown = false
		w.Hide()
	})

	m = &Manage{
		win: w, cfg: cfg, mon: mon,
		status: status, pollEntry: pollEntry, kwEntry: kwEntry,
		pathDesc: pathDesc, enabledSwitch: enabledSwitch,
		pages: pages, navItems: navItems, navGroups: groups,
	}
	m.selectPage(0)
	m.updateStatus()
	go m.refreshLoop()
	return m
}

// Show brings the settings window to the centre of the screen and focuses it.
func (m *Manage) Show() {
	m.shown = true
	m.updateStatus()
	m.win.CenterOnScreen()
	m.win.Show()
	m.win.RequestFocus()
}

func (m *Manage) selectPage(i int) {
	if i < 0 || i >= len(m.pages) {
		return
	}
	m.selected = i
	for j, it := range m.navItems {
		if j == i {
			it.bg.FillColor = mgrNavOn
		} else {
			it.bg.FillColor = color.Transparent
		}
		it.bg.Refresh()
	}
	for j, p := range m.pages {
		if j == i {
			p.Show()
		} else {
			p.Hide()
		}
	}
}

func (m *Manage) refreshLoop() {
	t := time.NewTicker(1 * time.Second)
	defer t.Stop()
	for range t.C {
		if m.shown {
			fyne.Do(func() { m.updateStatus() })
		}
	}
}

func (m *Manage) updateStatus() {
	var s string
	switch {
	case !m.mon.HuorongOK():
		s = "监控状态：未检测到火绒日志（测试模式）"
	case m.mon.Enabled():
		s = "监控状态：运行中 ✓"
	default:
		s = "监控状态：已停止"
	}
	if info, t := m.mon.LastDetected(); !t.IsZero() {
		s += fmt.Sprintf("    上次检测：%s（%s）", info.Name, t.Format("15:04:05"))
	}
	m.status.Text = s
	m.status.Refresh()
	m.enabledSwitch.SetOn(m.mon.Enabled())
}

// ---------------------------------------------------------------------------
// Layout / drawing helpers
// ---------------------------------------------------------------------------

func strongText(s string, size float32, bold bool) *canvas.Text {
	t := canvas.NewText(s, mgrTextStrong)
	t.TextSize = size
	t.TextStyle = fyne.TextStyle{Bold: bold}
	return t
}

func dimText(s string, size float32) *canvas.Text {
	t := canvas.NewText(s, mgrTextDim)
	t.TextSize = size
	return t
}

func sectionTitle(s string) *canvas.Text {
	t := canvas.NewText(s, mgrTextDim)
	t.TextSize = 13
	t.TextStyle = fyne.TextStyle{Bold: true}
	return t
}

func vgap(h float32) fyne.CanvasObject {
	r := canvas.NewRectangle(color.Transparent)
	r.SetMinSize(fyne.NewSize(0, h))
	return r
}

// makeCard groups rows into a rounded white panel on the grey page.
func makeCard(rows ...fyne.CanvasObject) fyne.CanvasObject {
	bg := canvas.NewRectangle(mgrCardFill)
	bg.CornerRadius = 12
	bg.StrokeColor = mgrCardStroke
	bg.StrokeWidth = 1
	body := container.New(layout.NewCustomPaddedLayout(6, 6, 0, 0), container.NewVBox(rows...))
	return container.NewStack(bg, body)
}

// cardSep is a hairline divider inset from the card edges.
func cardSep() fyne.CanvasObject {
	return container.New(layout.NewCustomPaddedLayout(0, 0, 16, 16), widget.NewSeparator())
}

// makeRow renders a label (+ optional description) on the left and an optional
// control on the right, matching the reference settings rows.
func makeRow(title string, desc fyne.CanvasObject, control fyne.CanvasObject) fyne.CanvasObject {
	left := container.NewVBox(strongText(title, 14, true))
	if desc != nil {
		left.Add(desc)
	}
	var inner fyne.CanvasObject = left
	if control != nil {
		inner = container.NewBorder(nil, nil, nil, container.NewCenter(control), left)
	}
	return container.New(layout.NewCustomPaddedLayout(12, 12, 16, 16), inner)
}

func fixedWidth(w, h float32, o fyne.CanvasObject) fyne.CanvasObject {
	return container.NewGridWrap(fyne.NewSize(w, h), o)
}

func scrollPage(items ...fyne.CanvasObject) fyne.CanvasObject {
	box := container.NewVBox()
	for i, it := range items {
		if i > 0 {
			box.Add(vgap(10))
		}
		box.Add(it)
	}
	return container.NewVScroll(container.New(layout.NewCustomPaddedLayout(28, 28, 32, 32), box))
}

// fixedWidthLayout pins its child to a fixed width while filling the height.
type fixedWidthLayout struct{ width float32 }

func (l *fixedWidthLayout) Layout(objs []fyne.CanvasObject, size fyne.Size) {
	for _, o := range objs {
		o.Move(fyne.NewPos(0, 0))
		o.Resize(fyne.NewSize(l.width, size.Height))
	}
}

func (l *fixedWidthLayout) MinSize(objs []fyne.CanvasObject) fyne.Size {
	var h float32
	for _, o := range objs {
		if s := o.MinSize(); s.Height > h {
			h = s.Height
		}
	}
	return fyne.NewSize(l.width, h)
}

func atoiSafe(s string) int {
	n, err := strconv.Atoi(strings.TrimSpace(s))
	if err != nil {
		return 0
	}
	return n
}

func splitKw(s string) []string {
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
