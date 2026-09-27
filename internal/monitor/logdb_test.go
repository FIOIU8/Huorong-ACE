package monitor

import (
	"os"
	"testing"
	"time"

	"huorong-ace/internal/config"
)

// TestRealQuarantineDB exercises the reader against a real Huorong install.
// It is skipped when Huorong is not present.
func TestRealQuarantineDB(t *testing.T) {
	path := config.QuarantineDBPath()
	if _, err := os.Stat(path); err != nil {
		t.Skipf("未安装火绒: %v", err)
	}
	cfg := config.DefaultConfig()
	cfg.LogPath = path

	db := newLogDB(path, cfg)
	if err := db.Open(); err != nil {
		t.Fatalf("Open: %v", err)
	}
	t.Logf("table=%s cols=%v lastID=%d startTS=%d", db.table, db.cols, db.lastID, db.startTS)

	// 1. History must never alert: Open parks both cursors at the end.
	got, err := db.Poll()
	if err != nil {
		t.Fatalf("Poll: %v", err)
	}
	if len(got) != 0 {
		t.Fatalf("启动后不应弹出历史记录，实际得到 %d 条", len(got))
	}

	// 2. Rewinding the rowid cursor alone still must not alert: the time
	//    cursor is what keeps duplicated historical rows quiet.
	db.lastID = 0
	db.fired = map[string]time.Time{}
	got, err = db.Poll()
	if err != nil {
		t.Fatalf("Poll(rowid rewind): %v", err)
	}
	if len(got) != 0 {
		t.Fatalf("时间游标未生效：重放 rowid 后仍得到 %d 条", len(got))
	}

	// 3. Rewinding both cursors replays everything, deduplicated per threat.
	db.startTS = 0
	db.fired = map[string]time.Time{}
	got, err = db.Poll()
	if err != nil {
		t.Fatalf("Poll(full rewind): %v", err)
	}
	for i, g := range got {
		t.Logf("  #%d name=%q detail=%q time=%s", i, g.Name, g.Detail, g.Time.Format("15:04:05"))
	}
	if len(got) == 0 {
		t.Fatal("完整重放后没有得到任何检测结果")
	}
	seen := map[string]bool{}
	for _, g := range got {
		if seen[g.Name] {
			t.Fatalf("同一威胁 %q 重复触发", g.Name)
		}
		seen[g.Name] = true
	}
}
