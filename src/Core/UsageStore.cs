//
//  UsageStore.cs — CodingFire for Windows
//
//  本地用量事件库。macOS 版用 SQLite，Windows 版改用「追加式 NDJSON + 内存索引」：
//   - 零外部依赖（不需要 native sqlite3.dll）
//   - 插入按 id 幂等，等价于原来的 INSERT OR IGNORE
//   - 只保存 token 数字与文件路径，绝不保存 prompt / 代码 / 凭据
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CodingFire.Core
{
    public sealed class UsageStore
    {
        private readonly object _gate = new object();
        private readonly List<UsageEvent> _events = new List<UsageEvent>();
        private readonly HashSet<string> _knownIds = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>
        /// 出现过事件的日志文件路径。JsonlReader 每轮扫描对「每个没长过的文件」都要问一次
        /// 「这个文件产出过事件吗」，原来是在 _events 上线性扫 —— 文件数 × 事件数，
        /// 重度用户下会拖慢整轮扫描，进而拖慢火焰反应。改成 O(1) 集合查询。
        /// </summary>
        private readonly HashSet<string> _knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CursorState> _cursors = new Dictionary<string, CursorState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _meta = new Dictionary<string, string>(StringComparer.Ordinal);

        // ---- 今日统计的增量累加器 ----
        // 原来 TodayTotals / TodayHourlyTotals / TodayBreakdown 各扫一遍全库，
        // 每轮扫描（4 秒）在 UI 线程上跑三次 O(45 天事件数)。改成插入时就累加，
        // 查询只是取快照；只有跨天 / 重扫 / 删除事件时才整体重算。
        private DateTime _statsDay = DateTime.MinValue;
        private int _statTotal;
        private readonly Dictionary<UsageSource, int> _statBySource = new Dictionary<UsageSource, int>();
        private readonly int[] _statHourly = new int[24];
        private int _statIn, _statOut, _statCacheRead, _statCacheWrite;

        private readonly StringBuilder _appendBuffer = new StringBuilder();
        private StreamWriter _appendWriter;
        private int _pendingWrites;

        /// <summary>超过这个天数的历史事件在启动时丢弃，避免内存无限增长。</summary>
        private const int RetainDays = 45;

        private sealed class CursorState
        {
            public long Offset;
            public string Partial;
        }

        // ------------------------------------------------------------------
        // 生命周期
        // ------------------------------------------------------------------

        public void Open()
        {
            lock (_gate)
            {
                AppPaths.CleanupStaleTempFiles();
                LoadEvents();
                LoadCursors();
                LoadMeta();
            }
        }

        private void LoadEvents()
        {
            string path = AppPaths.UsageFile;
            if (!File.Exists(path)) return;

            DateTime cutoff = DateTime.Now.AddDays(-RetainDays);
            int total = 0, kept = 0;
            bool needsCompact = false;

            foreach (string line in SafeReadLines(path))
            {
                if (line.Length == 0) continue;
                total++;
                var e = DecodeLine(line);
                if (e == null) { needsCompact = true; continue; }
                if (e.Timestamp < cutoff) { needsCompact = true; continue; }
                kept++;
                AddInMemory(e);
            }

            Log.Info("store loaded: " + kept + "/" + total + " events");
            RecomputeStatsLocked(DateTime.Now.Date);
            if (needsCompact) RewriteFile();
        }

        private void LoadCursors()
        {
            try
            {
                if (!File.Exists(AppPaths.CursorsFile)) return;
                var root = JObj.Of(Json.Parse(File.ReadAllText(AppPaths.CursorsFile, Encoding.UTF8)));
                foreach (var key in root.Keys)
                {
                    var o = root.Obj(key);
                    _cursors[key] = new CursorState
                    {
                        Offset = o.Long("o") ?? 0,
                        Partial = o.Str("p")
                    };
                }
            }
            catch (Exception ex) { Log.Warn("cursors load failed: " + ex.Message); }
        }

        private void LoadMeta()
        {
            try
            {
                if (!File.Exists(AppPaths.MetaFile)) return;
                var root = JObj.Of(Json.Parse(File.ReadAllText(AppPaths.MetaFile, Encoding.UTF8)));
                foreach (var key in root.Keys)
                {
                    string v = root.Str(key);
                    if (v != null) _meta[key] = v;
                }
            }
            catch (Exception ex) { Log.Warn("meta load failed: " + ex.Message); }
        }

        // ------------------------------------------------------------------
        // 事件
        // ------------------------------------------------------------------

        /// <summary>插入一条事件；id 已存在时返回 false（等价于 INSERT OR IGNORE 的 changes==0）。</summary>
        public bool InsertEvent(UsageEvent e)
        {
            lock (_gate)
            {
                if (e == null || e.Id == null || !_knownIds.Add(e.Id)) return false;
                _events.Add(e);
                if (!string.IsNullOrEmpty(e.FilePath)) _knownPaths.Add(e.FilePath);
                EnsureStatsDayLocked();
                AccumulateLocked(e, _statsDay.AddDays(1));
                _appendBuffer.Append(EncodeLine(e)).Append('\n');
                if (++_pendingWrites >= 64 || _appendBuffer.Length >= 128 * 1024) FlushAppendsLocked();
                return true;
            }
        }

        /// <summary>把缓冲的追加写落盘（扫描间隔与退出时调用）。</summary>
        public void Flush()
        {
            lock (_gate) { FlushAppendsLocked(); }
        }

        private void FlushAppendsLocked()
        {
            if (_appendBuffer.Length == 0) return;
            try
            {
                if (_appendWriter == null)
                {
                    var fs = new FileStream(AppPaths.UsageFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                    _appendWriter = new StreamWriter(fs, new UTF8Encoding(false));
                    _appendWriter.AutoFlush = false;
                }
                _appendWriter.Write(_appendBuffer.ToString());
                _appendWriter.Flush();
            }
            catch (Exception ex)
            {
                Log.Warn("append failed: " + ex.Message);
                CloseWriterLocked();
            }
            _appendBuffer.Length = 0;
            _pendingWrites = 0;
        }

        private void CloseWriterLocked()
        {
            if (_appendWriter == null) return;
            try { _appendWriter.Dispose(); }
            catch (Exception) { }
            _appendWriter = null;
        }

        public void Close()
        {
            lock (_gate)
            {
                FlushAppendsLocked();
                CloseWriterLocked();
            }
        }

        public bool HasEvent(string id)
        {
            lock (_gate) { return _knownIds.Contains(id); }
        }

        /// <summary>带前缀的批量成员查询——等价于原来的 eventIDs(withPrefix:)，避免 N 次单查。</summary>
        public HashSet<string> EventIDsWithPrefix(string prefix)
        {
            lock (_gate)
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in _knownIds)
                    if (id.StartsWith(prefix, StringComparison.Ordinal)) set.Add(id);
                return set;
            }
        }

        public bool HasEventsForFilePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            lock (_gate) { return _knownPaths.Contains(path); }
        }

        public sealed class TodayStats
        {
            public int Total;
            public readonly Dictionary<UsageSource, int> BySource = new Dictionary<UsageSource, int>();
        }

        public TodayStats TodayTotals(DateTime now)
        {
            lock (_gate)
            {
                EnsureStatsDayLocked(now);
                var stats = new TodayStats();
                stats.Total = _statTotal;
                foreach (var kv in _statBySource) stats.BySource[kv.Key] = kv.Value;
                return stats;
            }
        }

        /// <summary>今天 24 个小时桶（本地时区），空小时也在。</summary>
        public List<HourlyUsage> TodayHourlyTotals(DateTime now)
        {
            lock (_gate)
            {
                EnsureStatsDayLocked(now);
                var list = new List<HourlyUsage>(24);
                for (int h = 0; h < 24; h++) list.Add(new HourlyUsage(h, _statHourly[h]));
                return list;
            }
        }

        public UsageBreakdown TodayBreakdown(DateTime now)
        {
            lock (_gate)
            {
                EnsureStatsDayLocked(now);
                return new UsageBreakdown(_statIn, _statOut, _statCacheRead, _statCacheWrite);
            }
        }

        // ------------------------------------------------------------------
        // 今日统计的增量维护
        // ------------------------------------------------------------------

        /// <summary>缓存的天不是调用方要的那天就整体重算（跨天、或调用方主动查别的日子）。</summary>
        private void EnsureStatsDayLocked(DateTime wanted)
        {
            if (_statsDay != wanted.Date) RecomputeStatsLocked(wanted.Date);
        }

        private void EnsureStatsDayLocked() { EnsureStatsDayLocked(DateTime.Now); }

        private void RecomputeStatsLocked(DateTime day)
        {
            _statsDay = day;
            _statTotal = 0;
            _statBySource.Clear();
            for (int i = 0; i < 24; i++) _statHourly[i] = 0;
            _statIn = _statOut = _statCacheRead = _statCacheWrite = 0;

            DateTime end = day.AddDays(1);
            for (int i = 0; i < _events.Count; i++) AccumulateLocked(_events[i], end);
        }

        /// <summary>把一条事件并入当前统计日；不在该日内的直接跳过（O(1)）。</summary>
        private void AccumulateLocked(UsageEvent e, DateTime end)
        {
            if (e.Timestamp < _statsDay || e.Timestamp >= end) return;
            _statTotal += e.Tokens;
            int cur;
            _statBySource.TryGetValue(e.Source, out cur);
            _statBySource[e.Source] = cur + e.Tokens;
            int h = e.Timestamp.Hour;
            if (h >= 0 && h < 24) _statHourly[h] += e.Tokens;
            _statIn += e.Breakdown.Input ?? 0;
            _statOut += e.Breakdown.Output ?? 0;
            _statCacheRead += e.Breakdown.CacheRead ?? 0;
            _statCacheWrite += e.Breakdown.CacheWrite ?? 0;
        }

        /// <summary>按时间升序返回 since 之后的事件。</summary>
        public List<UsageEvent> RecentEvents(DateTime since, int limit)
        {
            lock (_gate)
            {
                var list = new List<UsageEvent>();
                for (int i = 0; i < _events.Count; i++)
                {
                    var e = _events[i];
                    if (e.Timestamp < since) continue;
                    list.Add(e);
                }
                list.Sort(delegate (UsageEvent a, UsageEvent b) { return a.Timestamp.CompareTo(b.Timestamp); });
                if (limit > 0 && list.Count > limit) list = list.GetRange(0, limit);
                return list;
            }
        }

        /// <summary>最近一条事件（按时间），没有则 null。</summary>
        public UsageEvent LatestEvent()
        {
            lock (_gate)
            {
                UsageEvent best = null;
                for (int i = 0; i < _events.Count; i++)
                {
                    var e = _events[i];
                    if (best == null || e.Timestamp > best.Timestamp) best = e;
                }
                return best;
            }
        }

        // ------------------------------------------------------------------
        // 文件游标
        // ------------------------------------------------------------------

        public void FileCursor(string path, out long offset, out string partial)
        {
            lock (_gate)
            {
                CursorState c;
                if (_cursors.TryGetValue(path, out c)) { offset = c.Offset; partial = c.Partial; }
                else { offset = 0; partial = null; }
            }
        }

        public void SetFileCursor(string path, long offset, string partial)
        {
            lock (_gate)
            {
                _cursors[path] = new CursorState { Offset = offset, Partial = partial };
                PersistCursors();
            }
        }

        public void ClearFileCursors()
        {
            lock (_gate)
            {
                _cursors.Clear();
                PersistCursors();
            }
        }

        // ------------------------------------------------------------------
        // meta
        // ------------------------------------------------------------------

        public string Meta(string key)
        {
            lock (_gate)
            {
                string v;
                return _meta.TryGetValue(key, out v) ? v : null;
            }
        }

        public void SetMeta(string key, string value)
        {
            lock (_gate)
            {
                _meta[key] = value;
                PersistMeta();
            }
        }

        // ------------------------------------------------------------------
        // Cursor 估算清理（与 macOS 版同语义）
        // ------------------------------------------------------------------

        public void PurgeCursorBubbleV1()
        {
            lock (_gate) RemoveWhere(delegate (string id) { return id.StartsWith("cursor:bubble:", StringComparison.Ordinal) && !id.StartsWith("cursor:bubble:v2:", StringComparison.Ordinal); });
        }

        public void PurgeCursorLocalEstimates()
        {
            lock (_gate) RemoveWhere(delegate (string id) { return id.StartsWith("cursor:bubble:", StringComparison.Ordinal); });
        }

        private void RemoveWhere(Predicate<string> match)
        {
            var remove = new List<UsageEvent>();
            for (int i = 0; i < _events.Count; i++) if (match(_events[i].Id)) remove.Add(_events[i]);
            if (remove.Count == 0) return;
            for (int i = 0; i < remove.Count; i++)
            {
                _events.Remove(remove[i]);
                _knownIds.Remove(remove[i].Id);
            }
            RebuildPathIndexLocked();
            RecomputeStatsLocked(_statsDay == DateTime.MinValue ? DateTime.Now.Date : _statsDay);
            RewriteFile();
        }

        /// <summary>事件集合被整体改动后重建路径索引（只有删除/清理路径会走到）。</summary>
        private void RebuildPathIndexLocked()
        {
            _knownPaths.Clear();
            for (int i = 0; i < _events.Count; i++)
            {
                string p = _events[i].FilePath;
                if (!string.IsNullOrEmpty(p)) _knownPaths.Add(p);
            }
        }

        // ------------------------------------------------------------------
        // 内部：内存 & 磁盘
        // ------------------------------------------------------------------

        private void AddInMemory(UsageEvent e)
        {
            _knownIds.Add(e.Id);
            if (!string.IsNullOrEmpty(e.FilePath)) _knownPaths.Add(e.FilePath);
            _events.Add(e);
        }

        private static IEnumerable<string> SafeReadLines(string path)
        {
            // FileShare.ReadWrite：日志文件可能正被 Claude Code / Codex 等工具占用
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8, false, 1 << 16))
            {
                string line;
                while ((line = sr.ReadLine()) != null) yield return line;
            }
        }

        private static string EncodeLine(UsageEvent e)
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"i\":").Append(Settings.Quote(e.Id));
            sb.Append(",\"s\":").Append(Settings.Quote(e.Source.Raw()));
            sb.Append(",\"t\":").Append(e.Timestamp.ToUniversalTime().Subtract(Epoch).TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture));
            sb.Append(",\"k\":").Append(e.Tokens.ToString(CultureInfo.InvariantCulture));
            if (e.Breakdown.Input.HasValue) sb.Append(",\"in\":").Append(e.Breakdown.Input.Value.ToString(CultureInfo.InvariantCulture));
            if (e.Breakdown.Output.HasValue) sb.Append(",\"out\":").Append(e.Breakdown.Output.Value.ToString(CultureInfo.InvariantCulture));
            if (e.Breakdown.CacheRead.HasValue) sb.Append(",\"cr\":").Append(e.Breakdown.CacheRead.Value.ToString(CultureInfo.InvariantCulture));
            if (e.Breakdown.CacheWrite.HasValue) sb.Append(",\"cw\":").Append(e.Breakdown.CacheWrite.Value.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(e.FilePath)) sb.Append(",\"f\":").Append(Settings.Quote(e.FilePath));
            if (e.IsEstimated) sb.Append(",\"e\":1");
            sb.Append('}');
            return sb.ToString();
        }

        private static UsageEvent DecodeLine(string line)
        {
            var o = Json.Parse(line) as JObj;
            if (o == null) return null;
            string id = o.Str("i");
            if (string.IsNullOrEmpty(id)) return null;
            var src = UsageSources.FromRaw(o.Str("s"));
            if (!src.HasValue) return null;

            double secs = o.Num("t") ?? 0;
            var e = new UsageEvent
            {
                Id = id,
                Source = src.Value,
                Timestamp = Epoch.AddSeconds(secs).ToLocalTime(),
                Tokens = o.Int("k") ?? 0,
                Breakdown = new UsageBreakdown(o.Int("in"), o.Int("out"), o.Int("cr"), o.Int("cw")),
                FilePath = o.Str("f") ?? "",
                IsEstimated = (o.Int("e") ?? 0) == 1
            };
            return e;
        }

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private void RewriteFile()
        {
            // 重写前必须先把缓冲落盘并关掉句柄，否则会丢事件 / 文件被占用
            FlushAppendsLocked();
            CloseWriterLocked();
            try
            {
                string tmp = AppPaths.UsageFile + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    _events.Sort(delegate (UsageEvent a, UsageEvent b) { return a.Timestamp.CompareTo(b.Timestamp); });
                    for (int i = 0; i < _events.Count; i++) sw.Write(EncodeLine(_events[i]) + "\n");
                }
                if (File.Exists(AppPaths.UsageFile)) File.Delete(AppPaths.UsageFile);
                File.Move(tmp, AppPaths.UsageFile);
            }
            catch (Exception ex) { Log.Warn("compact failed: " + ex.Message); }
        }

        private void PersistCursors()
        {
            try
            {
                var sb = new StringBuilder("{\n");
                bool first = true;
                foreach (var kv in _cursors)
                {
                    if (!first) sb.Append(",\n");
                    first = false;
                    sb.Append("  ").Append(Settings.Quote(kv.Key)).Append(": {\"o\":")
                      .Append(kv.Value.Offset.ToString(CultureInfo.InvariantCulture));
                    if (kv.Value.Partial != null) sb.Append(",\"p\":").Append(Settings.Quote(kv.Value.Partial));
                    sb.Append("}");
                }
                sb.Append("\n}\n");
                WriteAtomic(AppPaths.CursorsFile, sb.ToString());
            }
            catch (Exception ex) { Log.Warn("cursors save failed: " + ex.Message); }
        }

        private void PersistMeta()
        {
            try
            {
                var sb = new StringBuilder("{\n");
                bool first = true;
                foreach (var kv in _meta)
                {
                    if (!first) sb.Append(",\n");
                    first = false;
                    sb.Append("  ").Append(Settings.Quote(kv.Key)).Append(": ").Append(Settings.Quote(kv.Value));
                }
                sb.Append("\n}\n");
                WriteAtomic(AppPaths.MetaFile, sb.ToString());
            }
            catch (Exception ex) { Log.Warn("meta save failed: " + ex.Message); }
        }

        private static void WriteAtomic(string path, string content)
        {
            AppPaths.WriteAtomicFile(path, content);
        }
    }
}
