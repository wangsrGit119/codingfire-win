//
//  UsageMonitor.cs — TinyFire for Windows
//
//  扫描调度：每 4 秒增量读一次各数据源日志，去重、入库、喂火、刷新统计。
//  与 macOS 版 UsageMonitor 行为对齐（含初次 baseline、Claude 同 id 取最丰富快照、静默唤醒添柴）。
//

using System;
using System.Collections.Generic;
using System.Threading;
using TinyFire.Core;

namespace TinyFire.Data
{
    internal sealed class UsageMonitor
    {
        private readonly UsageStore _store;

        private readonly ClaudeCodeAdapter _claude = new ClaudeCodeAdapter();
        private readonly CodexAdapter _codex = new CodexAdapter();
        private readonly GrokAdapter _grok = new GrokAdapter();
        private readonly PiAdapter _pi = new PiAdapter();
        private readonly AmpAdapter _amp = new AmpAdapter();

        // ---- 新增：多工具聚合。每一项对应 Adapters2.cs 里的一个适配器。 ----
        private readonly WorkBuddyAdapter _workBuddy = new WorkBuddyAdapter();
        private readonly CodeBuddyAdapter _codeBuddy = new CodeBuddyAdapter();
        private readonly QoderAdapter _qoder = new QoderAdapter();
        private readonly QwenCodeAdapter _qwen = new QwenCodeAdapter();
        private readonly KimiAdapter _kimi = new KimiAdapter();
        private readonly CopilotAdapter _copilot = new CopilotAdapter();
        private readonly ZCodeAdapter _zcode = new ZCodeAdapter();
        private readonly OpenCodeAdapter _opencode = new OpenCodeAdapter();
        private readonly GeminiCliAdapter _gemini = new GeminiCliAdapter();
        private readonly DroidAdapter _droid = new DroidAdapter();
        private readonly ClineAdapter _cline = new ClineAdapter();
        private readonly RooCodeAdapter _roo = new RooCodeAdapter();
        private readonly KiloCodeAdapter _kilo = new KiloCodeAdapter();
        private readonly DeepSeekHarnessAdapter _dsh = new DeepSeekHarnessAdapter();
        private readonly CommandCodeAdapter _commandCode = new CommandCodeAdapter();
        private readonly OpenClawAdapter _openClaw = new OpenClawAdapter();
        private readonly EveryCodeAdapter _everyCode = new EveryCodeAdapter();

        /// <summary>
        /// 累计型源「见过的最大总量」：source -> (事件 id -> 已计入的 token 数)。
        /// 一条记录的 token 数会随流式输出不断变大时，只把差额当作新增。
        /// </summary>
        private readonly Dictionary<UsageSource, Dictionary<string, int>> _bestTotals =
            new Dictionary<UsageSource, Dictionary<string, int>>();

        private Dictionary<UsageSource, LogAdapter> _adapterMap;
        private readonly object _gate = new object();

        private Timer _timer;
        private SynchronizationContext _ui;
        private volatile bool _scanning;
        private bool _didCompleteBaseline;

        /// <summary>最近约 4 分钟内的用量直接算作「正在燃烧」，重启后火不会瞬间熄。</summary>
        private static readonly TimeSpan WarmWindow = TimeSpan.FromMinutes(4);

        public UsageMonitor(UsageStore store) { _store = store; }

        // ---- 供 App 注入（全部在 UI 线程被调用） ----
        public Action<double, UsageSource?, DateTime, bool> Ingest = null;
        public Action<int, Dictionary<UsageSource, int>> TodayTokensChanged = null;
        /// <summary>统计或扫描状态变化，UI 据此重绘。</summary>
        public event Action Updated;
        /// <summary>一批新事件被采纳（用于「今天的火」等增量反馈）。</summary>
        public event Action<int> EventsIngested;

        public SourceStatus[] Statuses { get; private set; }
        public int TodayTokens { get; private set; }
        public Dictionary<UsageSource, int> TodayBySource { get; private set; }
        public List<HourlyUsage> TodayHourly { get; private set; }
        public UsageBreakdown TodayBreakdown { get; private set; }
        public UsageEvent LastEvent { get; private set; }
        public bool IsScanning { get { return _scanning; } }
        public bool HasAnySource { get; private set; }

        // ------------------------------------------------------------------

        public void Start()
        {
            _ui = SynchronizationContext.Current;
            Statuses = BuildStatuses(null);
            TodayBySource = new Dictionary<UsageSource, int>();
            TodayHourly = EmptyHourly();
            TodayBreakdown = new UsageBreakdown();

            ReloadStats();
            RefreshStatuses();
            WarmFromStore();
            EnqueueScan(true);

            _timer = new Timer(delegate { EnqueueScan(false); }, null, 4000, 4000);
        }

        public void Stop()
        {
            var t = _timer;
            _timer = null;
            if (t != null) t.Dispose();
        }

        public void Rescan()
        {
            _didCompleteBaseline = false;
            _bestTotals.Clear();
            _store.ClearFileCursors();
            ReloadStats();
            RefreshStatuses();
            EnqueueScan(true);
        }

        public void RefreshStatuses()
        {
            var pairs = new List<KeyValuePair<UsageSource, SourceConnectionState>>();
            var details = new Dictionary<UsageSource, string>();
            int okCount = 0;

            var adapters = Adapters();
            foreach (var a in adapters)
            {
                string detail;
                var state = a.Value.CheckConnection(out detail);
                if (state == SourceConnectionState.Ok) okCount++;
                pairs.Add(new KeyValuePair<UsageSource, SourceConnectionState>(a.Key, state));
                details[a.Key] = detail;
            }

            var prev = Statuses;
            var list = new List<SourceStatus>();
            foreach (var p in pairs)
            {
                var st = new SourceStatus
                {
                    Source = p.Key,
                    State = p.Value,
                    Detail = details[p.Key],
                    TodayTokens = TodayBySource != null && TodayBySource.ContainsKey(p.Key) ? TodayBySource[p.Key] : 0
                };
                if (prev != null)
                {
                    foreach (var old in prev)
                        if (old.Source == p.Key) { st.LastReadAt = old.LastReadAt; break; }
                }
                list.Add(st);
            }
            Statuses = list.ToArray();
            HasAnySource = okCount > 0;
        }

        private Dictionary<UsageSource, LogAdapter> Adapters()
        {
            if (_adapterMap == null)
            {
                _adapterMap = new Dictionary<UsageSource, LogAdapter>
                {
                    { UsageSource.ClaudeCode, _claude },
                    { UsageSource.Codex, _codex },
                    { UsageSource.Grok, _grok },
                    { UsageSource.Pi, _pi },
                    { UsageSource.Amp, _amp },
                    { UsageSource.WorkBuddy, _workBuddy },
                    { UsageSource.CodeBuddy, _codeBuddy },
                    { UsageSource.Qoder, _qoder },
                    { UsageSource.QwenCode, _qwen },
                    { UsageSource.Kimi, _kimi },
                    { UsageSource.Copilot, _copilot },
                    { UsageSource.ZCode, _zcode },
                    { UsageSource.OpenCode, _opencode },
                    { UsageSource.GeminiCli, _gemini },
                    { UsageSource.Droid, _droid },
                    { UsageSource.Cline, _cline },
                    { UsageSource.RooCode, _roo },
                    { UsageSource.KiloCode, _kilo },
                    { UsageSource.DeepSeekHarness, _dsh },
                    { UsageSource.CommandCode, _commandCode },
                    { UsageSource.OpenClaw, _openClaw },
                    { UsageSource.EveryCode, _everyCode }
                };
            }
            return _adapterMap;
        }

        /// <summary>累计型源「见过的最大总量」表（按需创建；过长时整体重置）。</summary>
        private Dictionary<string, int> BestTotals(UsageSource src)
        {
            Dictionary<string, int> map;
            if (!_bestTotals.TryGetValue(src, out map))
            {
                map = new Dictionary<string, int>(StringComparer.Ordinal);
                _bestTotals[src] = map;
            }
            if (map.Count > 200000) map.Clear();
            return map;
        }

        private bool IsCumulative(UsageSource src)
        {
            LogAdapter a;
            return Adapters().TryGetValue(src, out a) && a.IsCumulative;
        }

        // ------------------------------------------------------------------
        // 扫描
        // ------------------------------------------------------------------

        private void EnqueueScan(bool baseline)
        {
            if (_scanning) return;
            _scanning = true;
            RaiseUpdated();

            DateTime fileSince = DateTime.Now.Date.AddHours(-12);
            bool fromStart = baseline;
            bool completeBaseline = _didCompleteBaseline;

            ThreadPool.QueueUserWorkItem(delegate
            {
                var collected = new List<UsageEvent>();
                var touched = new List<UsageSource>();

                foreach (var kv in Adapters())
                {
                    var src = kv.Key;
                    var adapter = kv.Value;
                    touched.Add(src);
                    try
                    {
                        foreach (string f in adapter.DiscoverLogFiles(fileSince))
                        {
                            // 整文件型（会话存档 / ui_messages.json / thread JSON）由 ParseThread 认领；
                            // 它返回 null 就说明不是这种格式，退回行式增量读取。
                            var whole = adapter.ParseThread(f);
                            if (whole != null) collected.AddRange(whole);
                            else collected.AddRange(JsonlReader.ReadNew(f, _store, fromStart, adapter.ParseLine));
                        }

                        // 有些源的数据在 SQLite 里，没有「日志文件」可列
                        var dbEvents = adapter.ParseDatabase();
                        if (dbEvents != null && dbEvents.Count > 0) collected.AddRange(dbEvents);
                    }
                    catch (Exception ex)
                    {
                        // 单个源出问题不该拖垮整轮扫描
                        Log.Warn(src.Raw() + " scan failed: " + ex.Message);
                    }
                }

                // 稳定排序：同一毫秒内多条记录（Claude 流式快照）必须保持文件内的原始先后。
                // .NET 的 List.Sort 是不稳定排序，若把「中间态」排到「最终态」前面，
                // Claude 的增量口径就会多落一行（总量仍对，但 breakdown 会偏大）。
                var indexed = new List<KeyValuePair<int, UsageEvent>>(collected.Count);
                for (int i = 0; i < collected.Count; i++)
                    indexed.Add(new KeyValuePair<int, UsageEvent>(i, collected[i]));
                indexed.Sort(delegate (KeyValuePair<int, UsageEvent> a, KeyValuePair<int, UsageEvent> b)
                {
                    int c = a.Value.Timestamp.CompareTo(b.Value.Timestamp);
                    return c != 0 ? c : a.Key.CompareTo(b.Key);
                });

                var ordered = new List<UsageEvent>(indexed.Count);
                foreach (var kv in indexed) ordered.Add(kv.Value);

                Post(delegate
                {
                    try { FinishScan(ordered, touched, fromStart, completeBaseline); }
                    catch (Exception ex) { Log.Warn("finishScan failed: " + ex.Message); }
                    finally { _scanning = false; RaiseUpdated(); }
                });
            });
        }

        private void Post(Action action)
        {
            var ctx = _ui;
            if (ctx != null && SynchronizationContext.Current != ctx)
                ctx.Post(delegate (object _) { action(); }, null);
            else
                action();
        }

        private void FinishScan(List<UsageEvent> events, List<UsageSource> touched, bool isBaseline, bool alreadyBaselined)
        {
            DateTime warmCutoff = DateTime.Now.Subtract(WarmWindow);
            bool baselinePass = isBaseline || !alreadyBaselined;

            int accepted = 0;
            foreach (var e in events)
                if (Apply(e, warmCutoff, baselinePass)) accepted++;

            if (accepted > 0)
            {
                var h = EventsIngested;
                if (h != null) h(accepted);
            }

            _store.Flush();
            ReloadStats();
            if (baselinePass) _didCompleteBaseline = true;
            ApplyTouched(touched);
            RefreshStatuses();
            RaiseUpdated();
        }

        private void ApplyTouched(List<UsageSource> touched)
        {
            if (Statuses == null) return;
            foreach (var src in touched)
            {
                for (int i = 0; i < Statuses.Length; i++)
                {
                    if (Statuses[i].Source != src) continue;
                    Statuses[i].LastReadAt = DateTime.Now;
                    Statuses[i].TodayTokens = TodayBySource.ContainsKey(src) ? TodayBySource[src] : 0;
                }
            }
        }

        /// <summary>把一条事件落库并喂火，返回是否真的被采纳（新 id）。</summary>
        private bool Apply(UsageEvent e, DateTime warmCutoff, bool baselinePass)
        {
            var toStore = e.Clone();

            // 累计型源：Claude 的流式快照、OpenCode / ZCode / Gemini / Droid 的累计用量……
            // 只保留最丰富的那次快照，把差额当增量入库。
            // 重启后首次见到的总量会以「原 id」落库，而原 id 早已在库里，
            // 于是被幂等丢弃；之后的增长才产生带 #总量 后缀的增量行——所以重启不会重复计数。
            if (IsCumulative(e.Source))
            {
                var best = BestTotals(e.Source);
                int previous;
                best.TryGetValue(e.Id, out previous);
                if (e.Tokens <= previous) return false;
                int delta = e.Tokens - previous;
                best[e.Id] = e.Tokens;
                toStore.Id = previous == 0 ? e.Id : e.Id + "#" + e.Tokens;
                toStore.Tokens = delta;
            }

            if (!_store.InsertEvent(toStore)) return false;

            LastEvent = toStore;
            if (toStore.Tokens <= 0) return false;

            if (baselinePass)
            {
                // 首轮只把最近几分钟的量算进去，历史数据不产生爆发式添柴
                if (toStore.Timestamp < warmCutoff) return true;
                if (Ingest != null) Ingest(toStore.Tokens, toStore.Source, toStore.Timestamp, false);
            }
            else
            {
                if (Ingest != null) Ingest(toStore.Tokens, toStore.Source, toStore.Timestamp, true);
            }
            return true;
        }

        // ------------------------------------------------------------------

        private void ReloadStats()
        {
            var totals = _store.TodayTotals(DateTime.Now);
            TodayTokens = totals.Total;
            TodayBySource = totals.BySource;
            TodayHourly = _store.TodayHourlyTotals(DateTime.Now);
            TodayBreakdown = _store.TodayBreakdown(DateTime.Now);
            if (TodayTokensChanged != null) TodayTokensChanged(TodayTokens, TodayBySource);
        }

        private void WarmFromStore()
        {
            // 重启后把最近几分钟的用量静默补进火焰，避免一开就灭
            DateTime cutoff = DateTime.Now.Subtract(WarmWindow);
            var recent = _store.RecentEvents(cutoff, 0);
            foreach (var e in recent)
                if (Ingest != null) Ingest(e.Tokens, e.Source, e.Timestamp, false);

            // 今天烧过但已经凉了 → 留一点余温
            var last = _store.LatestEvent();
            if (last != null)
            {
                double age = (DateTime.Now - last.Timestamp).TotalSeconds;
                const double horizon = 2 * 60 * 60;
                if (age >= 0 && age < horizon)
                {
                    double remaining = Math.Max(0.12, 1 - age / horizon);
                    if (Ingest != null) Ingest(30000 * remaining, last.Source, last.Timestamp, false);
                }
            }
        }

        private void RaiseUpdated()
        {
            var h = Updated;
            if (h != null) h();
        }

        private static List<HourlyUsage> EmptyHourly()
        {
            var list = new List<HourlyUsage>(24);
            for (int h = 0; h < 24; h++) list.Add(new HourlyUsage(h, 0));
            return list;
        }

        private static SourceStatus[] BuildStatuses(SourceStatus[] prev)
        {
            var list = new List<SourceStatus>();
            foreach (var s in UsageSources.All)
                list.Add(new SourceStatus { Source = s, State = SourceConnectionState.NotFound, Detail = "—", TodayTokens = 0 });
            return list.ToArray();
        }
    }
}
