package ui

import (
	"bytes"
	"image"
	"image/color"
	"image/draw"
	"image/png"
	"math"

	"fyne.io/fyne/v2"
	"fyne.io/fyne/v2/canvas"
)

// shieldAlpha returns 255 if the normalized point (nx,ny) in [0,1] lies inside
// a shield silhouette (rounded top, tapered to a point at the bottom).
func shieldAlpha(nx, ny float64) uint8 {
	const R = 0.32
	var hw float64
	switch {
	case ny < 0.16:
		dy := 0.16 - ny
		if dy > R {
			return 0
		}
		hw = math.Sqrt(R*R - dy*dy)
	case ny <= 0.5:
		hw = R
	default:
		t := (ny - 0.5) / 0.5
		hw = R * (1 - t)
		if hw < 0 {
			hw = 0
		}
	}
	if nx < 0.5-hw || nx > 0.5+hw {
		return 0
	}
	return 255
}

// GenShieldPNG renders a square PNG containing an optional background fill and a
// foreground shield shape.
func GenShieldPNG(size int, fg color.Color, bg color.Color) []byte {
	img := image.NewRGBA(image.Rect(0, 0, size, size))
	if bg != nil {
		draw.Draw(img, img.Bounds(), &image.Uniform{C: bg}, image.Point{}, draw.Src)
	}
	for y := 0; y < size; y++ {
		for x := 0; x < size; x++ {
			nx := float64(x) / float64(size-1)
			ny := float64(y) / float64(size-1)
			if shieldAlpha(nx, ny) > 0 {
				img.Set(x, y, fg)
			}
		}
	}
	var buf bytes.Buffer
	_ = png.Encode(&buf, img)
	return buf.Bytes()
}

func decodePNG(b []byte) image.Image {
	img, err := png.Decode(bytes.NewReader(b))
	if err != nil {
		return image.NewRGBA(image.Rect(0, 0, 1, 1))
	}
	return img
}

// IconResource returns the app/tray icon (red shield with white fill).
func IconResource() fyne.Resource {
	fg := color.White
	bg := color.NRGBA{0xC0, 0x00, 0x00, 0xFF}
	return fyne.NewStaticResource("icon.png", GenShieldPNG(64, fg, bg))
}

// ShieldWatermark returns a faint white shield image for the red screen.
func ShieldWatermark() *canvas.Image {
	b := GenShieldPNG(520, color.White, nil)
	img := canvas.NewImageFromImage(decodePNG(b))
	img.FillMode = canvas.ImageFillContain
	img.Translucency = 0.82
	return img
}
