package monitor

import (
	"log"
	"sync"
	"time"

	"huorong-ace/internal/config"
)

// Info describes a detected threat event surfaced to the UI.
type Info struct {
	Name   string
	Detail string
	Time   time.Time
}

// dbOpener is the minimal interface the monitor needs from the log reader.
type dbOpener interface {
	Open() error
	Close()
	Poll() ([]Info, error)
}

// Monitor polls the Huorong log database and reports virus detections.
type Monitor struct {
	cfg          *config.Config
	db           dbOpener
	onDetect     func(Info)
	mu           sync.Mutex
	stop         chan struct{}
	running      bool
	enabled      bool
	huorongOK    bool
	lastDetected Info
	lastDetectT  time.Time
}

// New creates a Monitor from the given config.
func New(cfg *config.Config) *Monitor {
	return &Monitor{
		cfg:     cfg,
		stop:    make(chan struct{}),
		enabled: cfg.MonitorEnabled,
	}
}

// SetOnDetect registers the callback fired on each detection.
func (m *Monitor) SetOnDetect(fn func(Info)) { m.onDetect = fn }

// Start opens the Huorong log (if present) and begins polling in a goroutine.
func (m *Monitor) Start() {
	m.mu.Lock()
	if m.running {
		m.mu.Unlock()
		return
	}
	m.running = true
	m.mu.Unlock()

	db := newLogDB(m.cfg.LogPath, m.cfg)
	if err := db.Open(); err != nil {
		log.Printf("[monitor] 无法打开火绒日志，仅启用测试模式: %v", err)
		m.huorongOK = false
	} else {
		m.huorongOK = true
		m.db = db
	}

	go m.loop()
}

func (m *Monitor) loop() {
	ticker := time.NewTicker(m.cfg.PollDuration())
	defer ticker.Stop()
	for {
		select {
		case <-m.stop:
			if m.db != nil {
				m.db.Close()
			}
			return
		case <-ticker.C:
			m.pollOnce()
		}
	}
}

func (m *Monitor) pollOnce() {
	m.mu.Lock()
	enabled := m.enabled
	db := m.db
	m.mu.Unlock()
	if !enabled || db == nil {
		return
	}
	infos, err := db.Poll()
	if err != nil {
		log.Printf("[monitor] 读取日志失败: %v", err)
		return
	}
	if len(infos) == 0 {
		return
	}
	info := infos[len(infos)-1]
	m.mu.Lock()
	m.lastDetected = info
	m.lastDetectT = time.Now()
	m.mu.Unlock()
	if m.onDetect != nil {
		m.onDetect(info)
	}
}

// Reload switches the monitor to a different log database, re-opening it and
// parking the cursor at its current end. Used after the path is changed in the
// settings window, so the change takes effect without restarting the app.
func (m *Monitor) Reload(path string) error {
	db := newLogDB(path, m.cfg)
	if err := db.Open(); err != nil {
		return err
	}
	m.mu.Lock()
	if m.db != nil {
		m.db.Close()
	}
	m.db = db
	m.huorongOK = true
	m.mu.Unlock()
	log.Printf("[monitor] 已切换到日志库: %s", path)
	return nil
}

// Simulate fires a fake detection (used by the test button / hotkey).
func (m *Monitor) Simulate(name string) {
	info := Info{
		Name:   name,
		Detail: "（模拟测试触发）本程序为娱乐用途，并非真实反作弊系统。",
		Time:   time.Now(),
	}
	if m.onDetect != nil {
		m.onDetect(info)
	}
}

// SetEnabled toggles whether real log polling is active.
func (m *Monitor) SetEnabled(v bool) {
	m.mu.Lock()
	m.enabled = v
	m.mu.Unlock()
}

// Enabled reports whether real log polling is active.
func (m *Monitor) Enabled() bool {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.enabled
}

// HuorongOK reports whether the Huorong log was opened successfully.
func (m *Monitor) HuorongOK() bool {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.huorongOK
}

// LastDetected returns the most recent detection and when it happened.
func (m *Monitor) LastDetected() (Info, time.Time) {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.lastDetected, m.lastDetectT
}

// Stop signals the polling goroutine to exit.
func (m *Monitor) Stop() {
	m.mu.Lock()
	if !m.running {
		m.mu.Unlock()
		return
	}
	m.running = false
	close(m.stop)
	m.mu.Unlock()
}
