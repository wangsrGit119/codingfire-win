//
//  UsageMonitor.cs — CodingFire for Windows
//
//  扫描调度：每 4 秒增量读一次各数据源日志，去重、入库、喂火、刷新统计。
//  与 macOS 版 UsageMonitor 行为对齐（含初次 baseline、Claude 同 id 取最丰富快照、静默唤醒添柴）。
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using CodingFire.Core;

namespace CodingFire.Data
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
        // 国外版 WorkBuddy 是独立安装，home 目录是 ~/.workbuddy-ai，单独统计。
        private readonly WorkBuddyAdapter _workBuddyIntl = WorkBuddyAdapter.CreateIntl();
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

        /// <summary>_bestTotals 现在会被扫描线程读写，重扫清空它时得跟扫描互斥。</summary>
        private readonly object _bestTotalsGate = new object();

        private Dictionary<UsageSource, LogAdapter> _adapterMap;

        private Timer _timer;
        private LogWatcher _watcher;
        private SynchronizationContext _ui;
        private volatile bool _scanning;
        private bool _didCompleteBaseline;

        // 扫描请求的合并/排队。用 int 而不是 bool 是为了能 Interlocked.Exchange 读清一体。
        private int _scanInFlight;
        private int _scanAgain;
        private int _baselineWanted;

        /// <summary>连接状态探测要列目录，没必要每轮都做。</summary>
        private const int StatusProbeSeconds = 20;
        private DateTime _lastStatusProbe = DateTime.MinValue;

        /// <summary>监听挂了之后心跳可以放宽；没挂上就还得靠它兜底。</summary>
        private const int HeartbeatMs = 4000;

        /// <summary>
        /// 超过这个耗时的一轮扫描记一条告警。事件驱动下扫描可能很密集
        /// （实测空闲单轮约 35ms，所以 300ms 只会在真的出问题时才响）。
        /// </summary>
        private const int SlowScanMs = 300;

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

            // 让扫描由「日志真的写了」驱动。工具落盘的瞬间就扫，不再干等定时器；
            // 监听挂不上（权限、目录还不存在）或缓冲溢出时，下面的心跳扫描兜底。
            _watcher = new LogWatcher(
                delegate { EnqueueScan(false); },
                delegate
                {
                    Log.Warn("log watcher buffer overflowed; forcing a full scan");
                    EnqueueScan(false);
                });
            _watcher.Sync(WatchRoots());
            Log.Info("log watcher: " + _watcher.WatchedRoots + " root(s) watched"
                     + (_watcher.WatchedRoots == 0 ? " (falling back to the timer only)" : ""));

            EnqueueScan(true);

            _timer = new Timer(delegate { Heartbeat(); }, null, HeartbeatMs, HeartbeatMs);
        }

        public void Stop()
        {
            var t = _timer;
            _timer = null;
            if (t != null) t.Dispose();

            var w = _watcher;
            _watcher = null;
            if (w != null) w.Dispose();
        }

        /// <summary>
        /// 定时兜底扫描。顺手把「启动之后才装上的工具」的根目录挂上监听 ——
        /// 监听是懒挂的，新出现的目录只有到下一轮心跳才有人发现。
        /// </summary>
        private void Heartbeat()
        {
            var w = _watcher;
            if (w != null)
            {
                try { w.Sync(WatchRoots()); }
                catch (Exception ex) { Log.Warn("watch sync failed: " + ex.Message); }
            }
            EnqueueScan(false);
        }

        /// <summary>所有适配器需要监听的路径（数据根目录 + SQLite 库文件）。</summary>
        private List<string> WatchRoots()
        {
            var list = new List<string>();
            foreach (var kv in Adapters())
            {
                try
                {
                    var roots = kv.Value.WatchRoots();
                    if (roots == null) continue;
                    foreach (string r in roots)
                        if (!string.IsNullOrEmpty(r)) list.Add(r);
                }
                catch (Exception) { }
            }
            return list;
        }

        public void Rescan()
        {
            _didCompleteBaseline = false;
            lock (_bestTotalsGate) { _bestTotals.Clear(); }
            _store.ClearFileCursors();
            ReloadStats();
            RefreshStatuses();
            EnqueueScan(true);
        }

        public void RefreshStatuses()
        {
            // 只有启动 / 重扫这种「用户正在看」的时刻才同步探测，其余走 ProbeStatuses 的节流后台探测。
            var probes = CollectProbes();
            ApplyStatuses(probes);
            _lastStatusProbe = DateTime.Now;
        }

        /// <summary>单个源的连接探测结果，凑齐后统一回 UI 线程组装。</summary>
        private struct Probe
        {
            public UsageSource Source;
            public SourceConnectionState State;
            public string Detail;
        }

        /// <summary>
        /// 在后台线程探测各源连接状态。这里会 Directory.GetFileSystemEntries 23 次，
        /// 原来是在 UI 线程上每 4 秒做一遍 —— 每轮都卡一下重绘。
        /// </summary>
        private void ProbeStatuses(bool force)
        {
            if (!force && (DateTime.Now - _lastStatusProbe).TotalSeconds < StatusProbeSeconds) return;
            _lastStatusProbe = DateTime.Now;
            var probes = CollectProbes();
            Post(delegate { ApplyStatuses(probes); });
        }

        private List<Probe> CollectProbes()
        {
            var probes = new List<Probe>();
            foreach (var a in Adapters())
            {
                string detail;
                SourceConnectionState state;
                try { state = a.Value.CheckConnection(out detail); }
                catch (Exception) { state = SourceConnectionState.ReadError; detail = "—"; }
                probes.Add(new Probe { Source = a.Key, State = state, Detail = detail });
            }
            return probes;
        }

        private void ApplyStatuses(List<Probe> probes)
        {
            var prev = Statuses;
            var list = new List<SourceStatus>(probes.Count);
            int okCount = 0;

            foreach (var p in probes)
            {
                if (p.State == SourceConnectionState.Ok) okCount++;
                var st = new SourceStatus
                {
                    Source = p.Source,
                    State = p.State,
                    Detail = p.Detail,
                    TodayTokens = TodayBySource != null && TodayBySource.ContainsKey(p.Source)
                        ? TodayBySource[p.Source] : 0
                };
                if (prev != null)
                {
                    foreach (var old in prev)
                        if (old.Source == p.Source) { st.LastReadAt = old.LastReadAt; break; }
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
                // 国外版 WorkBuddy：同一份解析逻辑、另一个 home 目录。
                _adapterMap[UsageSource.WorkBuddyIntl] = _workBuddyIntl;
            }
            return _adapterMap;
        }

        /// <summary>累计型源「见过的最大总量」表（按需创建；过长时整体重置）。</summary>
        private Dictionary<string, int> BestTotalsLocked(UsageSource src)
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
            if (baseline) Interlocked.Exchange(ref _baselineWanted, 1);

            // 请求永不丢弃。原来是 `if (_scanning) return;`：一轮扫描慢下来
            // （源多、磁盘忙、日志正被写）就等于把周期悄悄拉长，火焰实时性跟着塌；
            // 更糟的是丢掉的那一轮要等下一个心跳才补上。现在记一个「还要再扫」，
            // 扫完立刻接一轮。
            if (Interlocked.CompareExchange(ref _scanInFlight, 1, 0) != 0)
            {
                Interlocked.Exchange(ref _scanAgain, 1);
                return;
            }

            bool fromStart = Interlocked.Exchange(ref _baselineWanted, 0) == 1;
            bool completeBaseline = _didCompleteBaseline;

            _scanning = true;
            RaiseUpdated();

            DateTime fileSince = DateTime.Now.Date.AddHours(-12);

            ThreadPool.QueueUserWorkItem(delegate
            {
                var scanClock = Stopwatch.StartNew();
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

                // 去重 / 增量 / 落库全在后台线程做完。这些都是纯数据操作，
                // 放 UI 线程上等于每 4 秒拿磁盘 I/O 卡一次重绘。
                List<UsageEvent> accepted;
                try { accepted = ApplyAllToStore(ordered); }
                catch (Exception ex) { Log.Warn("store apply failed: " + ex.Message); accepted = new List<UsageEvent>(); }

                try { _store.Flush(); }
                catch (Exception ex) { Log.Warn("flush failed: " + ex.Message); }

                // 连接状态探测（23 次列目录）也留在后台，并按 StatusProbeSeconds 节流
                try { ProbeStatuses(false); }
                catch (Exception ex) { Log.Warn("status probe failed: " + ex.Message); }

                // 扫描现在由文件事件驱动，活跃时可能一秒好几轮 —— 单轮变贵会直接
                // 变成 CPU 开销，所以留一条慢扫描告警，出问题时有据可查。
                scanClock.Stop();
                if (scanClock.ElapsedMilliseconds > SlowScanMs)
                    Log.Warn("slow scan: " + scanClock.ElapsedMilliseconds + " ms ("
                             + accepted.Count + " new event(s))");

                Post(delegate
                {
                    try { FinishScan(accepted, touched, fromStart, completeBaseline); }
                    catch (Exception ex) { Log.Warn("finishScan failed: " + ex.Message); }
                    finally
                    {
                        _scanning = false;
                        RaiseUpdated();
                        Interlocked.Exchange(ref _scanInFlight, 0);
                        // 扫描期间又来了请求（日志刚写了）→ 立刻补一轮，别等心跳
                        if (Interlocked.Exchange(ref _scanAgain, 0) == 1) EnqueueScan(false);
                    }
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

            // events 已经在后台线程去重入库过了，这里只剩「喂火」这一件必须待在 UI 线程的事。
            int accepted = 0;
            foreach (var e in events)
            {
                LastEvent = e;
                if (e.Tokens <= 0) continue;

                if (baselinePass)
                {
                    // 首轮只把最近几分钟的量算进去，历史数据不产生爆发式添柴
                    if (e.Timestamp < warmCutoff) { accepted++; continue; }
                    if (Ingest != null) Ingest(e.Tokens, e.Source, e.Timestamp, false);
                }
                else
                {
                    if (Ingest != null) Ingest(e.Tokens, e.Source, e.Timestamp, true);
                }
                accepted++;
            }

            if (accepted > 0)
            {
                var h = EventsIngested;
                if (h != null) h(accepted);
            }

            ReloadStats();
            if (baselinePass) _didCompleteBaseline = true;
            ApplyTouched(touched);
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

        /// <summary>
        /// 后台线程：去重、累计型取增量、落库。返回真正入库的事件（UI 线程再拿去喂火）。
        /// 纯数据操作，放这里是为了不让磁盘 I/O 和 45 天库的遍历占住 UI 线程。
        /// </summary>
        private List<UsageEvent> ApplyAllToStore(List<UsageEvent> events)
        {
            var accepted = new List<UsageEvent>(events.Count);

            foreach (var e in events)
            {
                var toStore = e.Clone();

                // 累计型源：Claude 的流式快照、OpenCode / ZCode / Gemini / Droid 的累计用量……
                // 只保留最丰富的那次快照，把差额当增量入库。
                // 重启后首次见到的总量会以「原 id」落库，而原 id 早已在库里，
                // 于是被幂等丢弃；之后的增长才产生带 #总量 后缀的增量行——所以重启不会重复计数。
                if (IsCumulative(e.Source))
                {
                    int previous;
                    lock (_bestTotalsGate)
                    {
                        var best = BestTotalsLocked(e.Source);
                        best.TryGetValue(e.Id, out previous);
                        if (e.Tokens <= previous) continue;
                        best[e.Id] = e.Tokens;
                    }
                    toStore.Id = previous == 0 ? e.Id : e.Id + "#" + e.Tokens;
                    toStore.Tokens = e.Tokens - previous;
                }

                if (!_store.InsertEvent(toStore)) continue;
                accepted.Add(toStore);
            }
            return accepted;
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
