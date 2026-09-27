package config

import (
	"encoding/json"
	"log"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// Config holds runtime settings for the program.
type Config struct {
	LogPath        string   `json:"log_path"`
	PollInterval   int      `json:"poll_interval_sec"`
	Keywords       []string `json:"keywords"`
	MonitorEnabled bool     `json:"monitor_enabled"`
}

// legacyLogPath is the path used by earlier builds. It never existed on real
// Huorong installs, so configs still pointing at it are migrated.
const legacyLogPath = `C:\ProgramData\Huorong\Sysdiag\log.db`

// QuarantineDBPath is where Huorong records threats it processed. Every row of
// its FilesV3_60 table is a quarantined threat ("vn" = virus name).
func QuarantineDBPath() string {
	if dir := os.Getenv("ProgramData"); dir != "" {
		return filepath.Join(dir, "Huorong", "Sysdiag", "QuarantineEx.db")
	}
	return `C:\ProgramData\Huorong\Sysdiag\QuarantineEx.db`
}

// DefaultConfig returns sane defaults for a typical Huorong install.
func DefaultConfig() *Config {
	return &Config{
		LogPath:        QuarantineDBPath(),
		PollInterval:   2,
		Keywords:       []string{"病毒", "木马", "Trojan", "Virus", "Malware", "风险", "威胁", "Win32", "Backdoor", "Rootkit"},
		MonitorEnabled: true,
	}
}

// PollDuration converts the poll interval (seconds) to a time.Duration.
func (c *Config) PollDuration() time.Duration {
	if c.PollInterval < 1 {
		return time.Second
	}
	return time.Duration(c.PollInterval) * time.Second
}

// ConfigPath returns the path of config.json next to the executable.
func ConfigPath() string {
	exe, err := os.Executable()
	if err != nil {
		return "config.json"
	}
	return filepath.Join(filepath.Dir(exe), "config.json")
}

// Load reads config.json if it exists, otherwise returns defaults.
func Load() *Config {
	c := DefaultConfig()
	b, err := os.ReadFile(ConfigPath())
	if err == nil {
		_ = json.Unmarshal(b, c)
	}
	c.migrate()
	return c
}

// migrate repairs configs written by earlier builds: the hard-coded log path
// they used does not exist on real Huorong installs, which silently disabled
// detection. Point those configs at the quarantine database instead.
func (c *Config) migrate() {
	if !strings.EqualFold(c.LogPath, legacyLogPath) {
		return
	}
	q := QuarantineDBPath()
	if _, err := os.Stat(q); err != nil {
		return
	}
	log.Printf("[config] 迁移日志路径: %s -> %s", c.LogPath, q)
	c.LogPath = q
	_ = c.Save()
}

// Save writes the current config to config.json.
func (c *Config) Save() error {
	b, err := json.MarshalIndent(c, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(ConfigPath(), b, 0644)
}

// MatchKeyword reports whether s contains any configured keyword (case-insensitive).
func (c *Config) MatchKeyword(s string) bool {
	low := strings.ToLower(s)
	for _, k := range c.Keywords {
		if k == "" {
			continue
		}
		if strings.Contains(low, strings.ToLower(k)) {
			return true
		}
	}
	return false
}
