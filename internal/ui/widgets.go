package ui

import (
	"image/color"

	"fyne.io/fyne/v2"
	"fyne.io/fyne/v2/canvas"
	"fyne.io/fyne/v2/driver/desktop"
	"fyne.io/fyne/v2/widget"
)

// switchWidget is a compact iOS-style toggle used by the settings window.
// Fyne has no built-in switch, so this draws a rounded track with a sliding
// knob and reports changes through changed.
type switchWidget struct {
	widget.BaseWidget

	on      bool
	changed func(bool)
}

func newSwitch(on bool, changed func(bool)) *switchWidget {
	s := &switchWidget{on: on, changed: changed}
	s.ExtendBaseWidget(s)
	return s
}

// On reports the current state.
func (s *switchWidget) On() bool { return s.on }

// SetOn updates the state without firing the change callback.
func (s *switchWidget) SetOn(v bool) {
	if s.on == v {
		return
	}
	s.on = v
	s.Refresh()
}

func (s *switchWidget) Tapped(*fyne.PointEvent) {
	s.on = !s.on
	s.Refresh()
	if s.changed != nil {
		s.changed(s.on)
	}
}

func (s *switchWidget) Cursor() desktop.Cursor { return desktop.PointerCursor }

func (s *switchWidget) CreateRenderer() fyne.WidgetRenderer {
	bg := canvas.NewRectangle(color.NRGBA{0xC9, 0xC9, 0xCE, 0xFF})
	knob := canvas.NewCircle(color.NRGBA{0xFF, 0xFF, 0xFF, 0xFF})
	return &switchRenderer{
		s:       s,
		bg:      bg,
		knob:    knob,
		objects: []fyne.CanvasObject{bg, knob},
	}
}

type switchRenderer struct {
	s       *switchWidget
	bg      *canvas.Rectangle
	knob    *canvas.Circle
	objects []fyne.CanvasObject
}

func (r *switchRenderer) Layout(size fyne.Size) {
	r.bg.Move(fyne.NewPos(0, 0))
	r.bg.Resize(size)
	r.bg.CornerRadius = size.Height / 2

	d := size.Height - 6
	if d < 1 {
		d = 1
	}
	r.knob.Resize(fyne.NewSize(d, d))
	x := float32(3)
	if r.s.on {
		x = size.Width - d - 3
	}
	r.knob.Move(fyne.NewPos(x, 3))
}

func (r *switchRenderer) MinSize() fyne.Size { return fyne.NewSize(44, 24) }

func (r *switchRenderer) Refresh() {
	if r.s.on {
		r.bg.FillColor = color.NRGBA{0x2F, 0x6F, 0xED, 0xFF}
	} else {
		r.bg.FillColor = color.NRGBA{0xC9, 0xC9, 0xCE, 0xFF}
	}
	r.bg.Refresh()
	r.knob.Refresh()
	r.Layout(r.s.Size())
}

func (r *switchRenderer) Objects() []fyne.CanvasObject { return r.objects }

func (r *switchRenderer) Destroy() {}
