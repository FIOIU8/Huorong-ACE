package monitor

import (
	"database/sql"
	"fmt"
	"io"
	"log"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"huorong-ace/internal/config"
	_ "modernc.org/sqlite"
)

// dedupWindow suppresses repeated triggers for the same threat.
//
// Huorong writes several rows per processed file (one per scan/extraction) and
// often repeats the whole set while a sample is being unzipped, so without this
// the red screen would pop over and over. Three minutes comfortably covers
// those bursts while still allowing a deliberate re-test shortly afterwards.
const dedupWindow = 3 * time.Minute

// Huorong stores processed threats in its quarantine database (QuarantineEx.db,
// table FilesV3_60) where "vn" holds the virus name and "fn" the file path.
// The database runs in WAL mode and is held open by the Huorong service, so it
// cannot be opened read-only in place; every read works on a copied snapshot.
type logDB struct {
	path  string
	cfg   *config.Config
	table string
	cols  []string

	// lastID advances over rowids; startTS is a second, time-based cursor.
	// Only rows with a timestamp strictly newer than startTS can ever alert,
	// which is what keeps pre-existing records from firing on start-up and
	// keeps duplicated historical rows quiet even if their rowids shift.
	lastID  int64
	startTS int64
	fired   map[string]time.Time
}

func newLogDB(path string, cfg *config.Config) *logDB {
	return &logDB{path: path, cfg: cfg, fired: map[string]time.Time{}}
}

// Open validates the database, resolves the threat table and positions the
// cursor at the newest row so historical records do not fire on start-up.
func (l *logDB) Open() error {
	if _, err := os.Stat(l.path); err != nil {
		return fmt.Errorf("日志文件不存在: %s", l.path)
	}
	snap, err := l.snapshot()
	if err != nil {
		return err
	}
	defer removeSnapshot(snap)

	db, err := openSnapshot(snap)
	if err != nil {
		return err
	}
	defer db.Close()

	tbl, err := resolveTable(db)
	if err != nil {
		return err
	}
	l.table = tbl
	l.cols = columnsOf(db, tbl)
	l.lastID = maxRowID(db, tbl)
	l.startTS = maxTS(db, tbl)
	log.Printf("[monitor] 已打开 %s (表 %s, 列 %v, 起始游标 rowid=%d ts=%d)",
		l.path, tbl, l.cols, l.lastID, l.startTS)
	return nil
}

// Close is a no-op: every read opens and closes its own private snapshot, so
// no handle is kept open between polls.
func (l *logDB) Close() {}

// Poll returns threats newer than the cursor, advancing it.
func (l *logDB) Poll() ([]Info, error) {
	snap, err := l.snapshot()
	if err != nil {
		return nil, err
	}
	defer removeSnapshot(snap)

	db, err := openSnapshot(snap)
	if err != nil {
		return nil, err
	}
	defer db.Close()

	rows, err := db.Query(fmt.Sprintf("SELECT rowid, * FROM %q WHERE rowid > ? ORDER BY rowid ASC", l.table), l.lastID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	cols, err := rows.Columns()
	if err != nil {
		return nil, err
	}

	var out []Info
	maxID := l.lastID
	maxTS := l.startTS
	for rows.Next() {
		vals := make([]interface{}, len(cols))
		ptrs := make([]interface{}, len(cols))
		for i := range vals {
			ptrs[i] = &vals[i]
		}
		if err := rows.Scan(ptrs...); err != nil {
			return nil, err
		}
		m := map[string]string{}
		for i, c := range cols {
			m[c] = stringify(vals[i])
		}
		if rid := atoi64(m["rowid"]); rid > maxID {
			maxID = rid
		}
		// Time cursor: anything at or before the watermark has already been
		// accounted for, including every duplicate of an older incident.
		ts := rowTS(m)
		if ts > maxTS {
			maxTS = ts
		}
		if l.startTS > 0 && ts > 0 && ts <= l.startTS {
			continue
		}
		if !l.isThreat(m) {
			continue
		}
		k := dedupKey(m)
		if t, seen := l.fired[k]; seen && time.Since(t) < dedupWindow {
			continue
		}
		l.fired[k] = time.Now()
		l.pruneFired()
		out = append(out, l.toInfo(m))
	}
	l.lastID = maxID
	l.startTS = maxTS
	return out, nil
}

// isThreat decides whether a row represents a threat.
//
// Rows carrying a non-empty "vn" (virus name) come from Huorong's quarantine
// database, which only ever holds processed threats, so those always count.
// Keyword filtering is applied only to generic/custom databases, where running
// it would otherwise cause false negatives (a name like Worm/Generic never
// matches a keyword list).
func (l *logDB) isThreat(m map[string]string) bool {
	if strings.TrimSpace(m["vn"]) != "" {
		return true
	}
	var sb strings.Builder
	for _, v := range m {
		sb.WriteString(v)
		sb.WriteByte(' ')
	}
	return l.cfg.MatchKeyword(sb.String())
}

// dedupKey identifies one threat. The virus name is the identity: the same
// sample rediscovered in several locations is still one threat worth one alert.
func dedupKey(m map[string]string) string {
	if vn := strings.TrimSpace(m["vn"]); vn != "" {
		return vn
	}
	var sb strings.Builder
	for _, v := range m {
		sb.WriteString(v)
		sb.WriteByte(' ')
	}
	return sb.String()
}

// pruneFired drops dedup entries that have aged out.
func (l *logDB) pruneFired() {
	for k, t := range l.fired {
		if time.Since(t) >= dedupWindow {
			delete(l.fired, k)
		}
	}
}

func (l *logDB) toInfo(m map[string]string) Info {
	name := strings.TrimSpace(m["vn"])
	if name == "" {
		for _, c := range l.cols {
			if v := m[c]; l.cfg.MatchKeyword(v) {
				name = v
				break
			}
		}
	}
	if name == "" {
		name = "未知威胁"
	}
	detail := strings.TrimSpace(m["fn"])
	if detail != "" {
		detail = "路径: " + detail
	}
	return Info{Name: name, Detail: detail, Time: rowTime(m)}
}

func rowTime(m map[string]string) time.Time {
	if n := rowTS(m); n > 0 {
		return time.Unix(n, 0)
	}
	return time.Now()
}

// rowTS returns the row's Unix timestamp, or 0 when the column is absent or
// unusable (custom databases without a ts column).
func rowTS(m map[string]string) int64 {
	s := strings.TrimSpace(m["ts"])
	if s == "" {
		return 0
	}
	n, err := strconv.ParseInt(s, 10, 64)
	if err != nil || n <= 0 {
		return 0
	}
	return n
}

// ---------------------------------------------------------------------------
// Snapshot helpers (WAL-safe reading of a database held by another process)
// ---------------------------------------------------------------------------

func (l *logDB) snapshot() (string, error) {
	dir, err := os.MkdirTemp("", "huorong-ace-snap")
	if err != nil {
		return "", err
	}
	dst := filepath.Join(dir, "snap.db")
	if err := copyFile(l.path, dst); err != nil {
		removeSnapshot(dst)
		return "", fmt.Errorf("复制日志失败: %v", err)
	}
	// The -wal / -shm files hold the newest rows; copy them too. Missing ones
	// are simply skipped.
	_ = copyFile(l.path+"-wal", dst+"-wal")
	_ = copyFile(l.path+"-shm", dst+"-shm")
	return dst, nil
}

func removeSnapshot(snap string) {
	if snap == "" {
		return
	}
	_ = os.RemoveAll(filepath.Dir(snap))
}

func openSnapshot(snap string) (*sql.DB, error) {
	dsn := "file:" + filepath.ToSlash(snap) + "?_pragma=busy_timeout(3000)"
	db, err := sql.Open("sqlite", dsn)
	if err != nil {
		return nil, err
	}
	db.SetMaxOpenConns(1)
	if err := db.Ping(); err != nil {
		db.Close()
		return nil, fmt.Errorf("无法打开数据库: %v", err)
	}
	return db, nil
}

func copyFile(src, dst string) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.Create(dst)
	if err != nil {
		return err
	}
	defer out.Close()
	if _, err := io.Copy(out, in); err != nil {
		return err
	}
	return out.Close()
}

// ---------------------------------------------------------------------------
// Schema discovery
// ---------------------------------------------------------------------------

func resolveTable(db *sql.DB) (string, error) {
	rows, err := db.Query("SELECT name FROM sqlite_master WHERE type='table'")
	if err != nil {
		return "", err
	}
	defer rows.Close()
	var tables []string
	for rows.Next() {
		var n string
		if err := rows.Scan(&n); err != nil {
			return "", err
		}
		tables = append(tables, n)
	}
	if rows.Err() != nil {
		return "", rows.Err()
	}
	// Huorong's quarantine table, then older/newer log tables, then any table
	// that looks like a quarantine file list.
	exact := []string{"FilesV3_60", "FilesV3", "HrLogV3", "HrLog"}
	for _, want := range exact {
		for _, t := range tables {
			if strings.EqualFold(t, want) {
				return t, nil
			}
		}
	}
	for _, t := range tables {
		if strings.HasPrefix(strings.ToLower(t), "filesv3") {
			return t, nil
		}
	}
	for _, t := range tables {
		if strings.EqualFold(t, "sqlite_sequence") {
			continue
		}
		return t, nil
	}
	return "", fmt.Errorf("未找到日志表")
}

func columnsOf(db *sql.DB, table string) []string {
	rows, err := db.Query(fmt.Sprintf("SELECT name FROM pragma_table_info(%q)", table))
	if err != nil {
		return nil
	}
	defer rows.Close()
	var cols []string
	for rows.Next() {
		var n string
		_ = rows.Scan(&n)
		cols = append(cols, n)
	}
	return cols
}

// maxTS returns the newest timestamp already in the table, used to seed the
// time cursor so historical records never alert. Returns 0 when the table has
// no usable ts column, in which case the rowid cursor alone is used.
func maxTS(db *sql.DB, table string) int64 {
	has := false
	for _, c := range columnsOf(db, table) {
		if strings.EqualFold(c, "ts") {
			has = true
			break
		}
	}
	if !has {
		return 0
	}
	var v sql.NullInt64
	_ = db.QueryRow(fmt.Sprintf("SELECT MAX(\"ts\") FROM %q", table)).Scan(&v)
	return v.Int64
}

func maxRowID(db *sql.DB, table string) int64 {
	var max sql.NullInt64
	_ = db.QueryRow(fmt.Sprintf("SELECT MAX(rowid) FROM %q", table)).Scan(&max)
	return max.Int64
}

func stringify(v interface{}) string {
	switch t := v.(type) {
	case nil:
		return ""
	case []byte:
		return string(t)
	case string:
		return t
	case int64:
		return strconv.FormatInt(t, 10)
	case float64:
		return strconv.FormatFloat(t, 'f', -1, 64)
	case bool:
		return strconv.FormatBool(t)
	case time.Time:
		return t.Format("2006-01-02 15:04:05")
	default:
		return fmt.Sprintf("%v", t)
	}
}

func atoi64(s string) int64 {
	n, _ := strconv.ParseInt(strings.TrimSpace(s), 10, 64)
	return n
}
