//
//  _FixtureMain.cs — dev-only driver for the aggregation fixture suite.
//
//  Compiled *together with* src\*.cs and selected via /main:, so it can touch the
//  internal adapters directly.  It never opens the real data store and never
//  writes outside _dev\.
//
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TinyFire.Core;
using TinyFire.Data;
using TinyFire.Fire;

namespace TinyFire.Dev
{
    internal static class FixtureMain
    {
        private static readonly List<string> Out = new List<string>();

        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--console-probe") return ConsoleProbe(args);
            return RunFixtures(args);
        }

        // ------------------------------------------------------------------
        // --console-probe : reproduce the tray double-click -> OpenConsole path.
        // Uses the user's REAL settings.json (copied into an isolated data dir)
        // so config-dependent breakage reproduces too.
        // ------------------------------------------------------------------
        private static int ConsoleProbe(string[] args)
        {
            string dev = Path.GetFullPath(".");
            string probeDir = Path.Combine(dev, "_probe_data");
            if (Directory.Exists(probeDir)) DeleteDir(probeDir);
            Directory.CreateDirectory(probeDir);

            // Real settings, if any, so Load() sees what the user sees.
            string realSettings = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TinyFire");
            realSettings = Path.Combine(realSettings, "settings.json");
            if (File.Exists(realSettings))
                File.Copy(realSettings, Path.Combine(probeDir, "settings.json"), true);

            Environment.SetEnvironmentVariable("TINYFIRE_DATA_DIR", probeDir);

            // Point every source at the (quiet) fixture corpus so the monitor
            // doesn't grind through the real 100M+ token logs while probing.
            string fixtures = Path.GetFullPath(Path.Combine(dev, "_fixtures"));
            if (Directory.Exists(fixtures))
            {
                Env("WORKBUDDY_HOME", P(fixtures, "workbuddy"));
                Env("WORKBUDDY_AI_HOME", P(fixtures, "workbuddy-intl"));
                Env("CODEBUDDY_HOME", P(fixtures, "codebuddy"));
                Env("AI_USAGE_QODER_ROOTS", P(fixtures, "qoder", "projects"));
                Env("AI_USAGE_QODER_IDE_ROOTS", P(fixtures, "qoder-ide", "Qoder"));
                Env("QWEN_TMP_DIR", P(fixtures, "qwen", "tmp"));
                Env("KIMI_CODE_HOME", P(fixtures, "kimi-code"));
                Env("KIMI_HOME", P(fixtures, "kimi"));
                Env("COPILOT_HOME", P(fixtures, "copilot"));
                Env("ZCODE_HOME", P(fixtures, "zcode"));
                Env("OPENCODE_HOME", P(fixtures, "opencode"));
                Env("GEMINI_HOME", P(fixtures, "gemini"));
                Env("DROID_SESSIONS_DIR", P(fixtures, "factory", "sessions"));
                Env("AI_USAGE_VSCODE_ROOTS", P(fixtures, "vscode"));
                Env("AI_USAGE_CLINE_ROOTS", P(fixtures, "vscode"));
                Env("AI_USAGE_ROOCODE_ROOTS", P(fixtures, "vscode"));
                Env("AI_USAGE_KILOCODE_ROOTS", P(fixtures, "vscode"));
                Env("DSH_HOME", P(fixtures, "dsh"));
                Env("AI_USAGE_COMMANDCODE_ROOTS", P(fixtures, "commandcode", "projects"));
                Env("OPENCLAW_STATE_DIR", P(fixtures, "openclaw"));
                Env("AI_USAGE_EVERY_CODE_HOME", P(fixtures, "everycode"));
            }

            var log = new List<string>();
            log.Add("probe start " + DateTime.Now.ToString("HH:mm:ss"));
            log.Add("settings copied: " + (File.Exists(realSettings) ? "yes" : "no (clean defaults)"));
            int rc = 0;

            var t = new System.Threading.Thread(delegate()
            {
                try
                {
                    System.Windows.Forms.Application.EnableVisualStyles();
                    System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                    using (var app = new TinyFire.Ui.TinyFireApp())
                    {
                        log.Add("app ctor ok");
                        app.OpenConsole();
                        log.Add("open console ok");
                        for (int i = 0; i < 20; i++)
                        {
                            System.Threading.Thread.Sleep(50);
                            System.Windows.Forms.Application.DoEvents();
                        }
                        log.Add("message pump ok");
                    }
                }
                catch (Exception ex)
                {
                    rc = 1;
                    log.Add("EXCEPTION: " + ex);
                }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
            t.Join();

            log.Add("probe end rc=" + rc);
            File.WriteAllText(Path.Combine(dev, "_console_probe.txt"),
                string.Join(Environment.NewLine, log.ToArray()) + Environment.NewLine,
                new UTF8Encoding(false));
            return rc;
        }

        private static void DeleteDir(string path)
        {
            foreach (string f in Directory.GetFiles(path))
                File.Delete(f);
            foreach (string d in Directory.GetDirectories(path))
                DeleteDir(d);
            Directory.Delete(path);
        }

        private static int RunFixtures(string[] args)
        {
            string fixtures = args.Length > 0 ? args[0] : "_fixtures";
            fixtures = Path.GetFullPath(fixtures);

            var root = new StringBuilder();
            root.AppendLine("fixture root : " + fixtures);
            Out.Add(root.ToString().TrimEnd());

            Env("WORKBUDDY_HOME", P(fixtures, "workbuddy"));
            Env("CODEBUDDY_HOME", P(fixtures, "codebuddy"));
            Env("AI_USAGE_QODER_ROOTS", P(fixtures, "qoder", "projects"));
            Env("AI_USAGE_QODER_IDE_ROOTS", P(fixtures, "qoder-ide", "Qoder"));
            Env("QWEN_TMP_DIR", P(fixtures, "qwen", "tmp"));
            Env("KIMI_CODE_HOME", P(fixtures, "kimi-code"));
            Env("KIMI_HOME", P(fixtures, "kimi"));
            Env("COPILOT_HOME", P(fixtures, "copilot"));
            Env("ZCODE_HOME", P(fixtures, "zcode"));
            Env("OPENCODE_HOME", P(fixtures, "opencode"));
            Env("GEMINI_HOME", P(fixtures, "gemini"));
            Env("DROID_SESSIONS_DIR", P(fixtures, "factory", "sessions"));
            Env("AI_USAGE_VSCODE_ROOTS", P(fixtures, "vscode"));
            Env("AI_USAGE_CLINE_ROOTS", P(fixtures, "vscode"));
            Env("AI_USAGE_ROOCODE_ROOTS", P(fixtures, "vscode"));
            Env("AI_USAGE_KILOCODE_ROOTS", P(fixtures, "vscode"));
            Env("DSH_HOME", P(fixtures, "dsh"));
            Env("AI_USAGE_COMMANDCODE_ROOTS", P(fixtures, "commandcode", "projects"));
            Env("OPENCLAW_STATE_DIR", P(fixtures, "openclaw"));
            Env("AI_USAGE_EVERY_CODE_HOME", P(fixtures, "everycode"));

            // Same window the monitor uses, so freshly written fixtures qualify.
            DateTime since = DateTime.Now.Date.AddHours(-12);

            Run("workbuddy", new WorkBuddyAdapter(), since);
            Run("workbuddy-intl", WorkBuddyAdapter.CreateIntl(), since);
            Run("codebuddy", new CodeBuddyAdapter(), since);
            Run("qoder", new QoderAdapter(), since);
            Run("qwen", new QwenCodeAdapter(), since);
            Run("kimi", new KimiAdapter(), since);
            Run("copilot", new CopilotAdapter(), since);
            Run("zcode", new ZCodeAdapter(), since);
            Run("opencode", new OpenCodeAdapter(), since);
            Run("gemini", new GeminiCliAdapter(), since);
            Run("droid", new DroidAdapter(), since);
            Run("cline", new ClineAdapter(), since);
            Run("roocode", new RooCodeAdapter(), since);
            Run("kilocode", new KiloCodeAdapter(), since);
            Run("dsh", new DeepSeekHarnessAdapter(), since);
            Run("command-code", new CommandCodeAdapter(), since);
            Run("openclaw", new OpenClawAdapter(), since);
            Run("every-code", new EveryCodeAdapter(), since);

            string dir = Path.GetDirectoryName(fixtures);

            string outPath = Path.Combine(dir, "_fixtures_got.txt");
            File.WriteAllText(outPath, string.Join(Environment.NewLine, Out.ToArray()) + Environment.NewLine,
                              new UTF8Encoding(false));

            // 速率估算的确定性核心。显示值叠了抖动、不可复现，所以断言的是平滑前的候选值。
            var rate = new List<string>();
            RunRate(rate);
            File.WriteAllText(Path.Combine(dir, "_rate_got.txt"),
                              string.Join(Environment.NewLine, rate.ToArray()) + Environment.NewLine,
                              new UTF8Encoding(false));

            // 色带的可见来源上限
            var cap = new List<string>();
            RunColorCap(cap);
            File.WriteAllText(Path.Combine(dir, "_colorcap_got.txt"),
                              string.Join(Environment.NewLine, cap.ToArray()) + Environment.NewLine,
                              new UTF8Encoding(false));
            return 0;
        }

        private static string P(string a, params string[] parts)
        {
            string p = a;
            foreach (string s in parts) p = Path.Combine(p, s);
            return p;
        }

        private static void Env(string name, string value)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        private static void Run(string name, LogAdapter a, DateTime since)
        {
            var events = new List<UsageEvent>();
            var detail = new List<string>();
            try
            {
                foreach (string f in a.DiscoverLogFiles(since))
                {
                    List<UsageEvent> whole = a.ParseThread(f);
                    if (whole != null)
                    {
                        detail.Add("  file " + Path.GetFileName(f) + " -> whole-file " + whole.Count);
                        events.AddRange(whole);
                        continue;
                    }
                    string[] lines;
                    try { lines = File.ReadAllLines(f); }
                    catch (Exception ex) { detail.Add("  read failed " + f + ": " + ex.Message); continue; }

                    int got = 0;
                    foreach (string line in lines)
                    {
                        UsageEvent e = a.ParseLine(line, f);
                        if (e != null) { events.Add(e); got++; }
                    }
                    detail.Add("  file " + Path.GetFileName(f) + " -> line-mode " + got);
                }

                List<UsageEvent> db = a.ParseDatabase();
                if (db != null && db.Count > 0)
                {
                    detail.Add("  database -> " + db.Count);
                    events.AddRange(db);
                }
            }
            catch (Exception ex)
            {
                Out.Add(name + "\tTHREW\t" + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            int sum = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (UsageEvent e in events) { sum += e.Tokens; seen.Add(e.Id); }

            Out.Add(name + "\t" + events.Count + "\t" + sum.ToString(CultureInfo.InvariantCulture));
            foreach (string d in detail) Out.Add(d);
            foreach (string id in seen) Out.Add("  id " + id + " -> " + ValueOf(events, id));
        }

        private static int ValueOf(List<UsageEvent> list, string id)
        {
            foreach (UsageEvent e in list) if (string.Equals(e.Id, id, StringComparison.Ordinal)) return e.Tokens;
            return 0;
        }

        // ------------------------------------------------------------------
        //  悬停卡片那个 tok/s 的确定性核心
        //
        //  口径（与 macOS 1.1.16 的 FireStateMachine 一致）：
        //    窗口 60 秒 · 每条封顶 15,000 · 显示上限 320 · 候选 = min(320, max(实测, 火势反推))
        //  只断言「平滑 + 抖动之前」的值：抖动一叠上去，显示值就不再可复现了。
        // ------------------------------------------------------------------
        private static void RunRate(List<string> o)
        {
            // 必须相对真实时钟取值：Ingest 内部的 SmoothColorMix 会按 DateTime.Now
            // 修剪一次流入，用固定的历史时刻造数据会在进窗口前就被剪掉。
            DateTime T = DateTime.Now;
            var src = UsageSource.ClaudeCode;

            // 1) 迟到事件。时间戳在窗口外 → 不能伪造出「当下正在以这个速度烧」的读数……
            var late = new FireStateMachine();
            late.Ingest(15000, src, T.AddSeconds(-600), false);
            o.Add("late_event_excluded\t" + F(late.RateFromInflows(T)));
            // ……但火焰本身仍该被点着：迟到的日志文件确实代表刚刚烧过。
            o.Add("late_event_still_flares\t" + F(late.RateFromFlame() > 0 ? 1.0 : 0.0));

            // 2) 窗口边界：T-61 落在窗口外不计，T-59 计 → 15000 / 60 = 250
            var edge = new FireStateMachine();
            edge.Ingest(15000, src, T.AddSeconds(-61), false);
            edge.Ingest(15000, src, T.AddSeconds(-59), false);
            o.Add("window_boundary\t" + F(edge.RateFromInflows(T)));

            // 3) 单条封顶：40000 只按 15000 计，再加 5000 → 20000 / 60
            var cap = new FireStateMachine();
            cap.Ingest(40000, src, T.AddSeconds(-30), false);
            cap.Ingest(5000, src, T.AddSeconds(-10), false);
            o.Add("per_event_cap\t" + F(cap.RateFromInflows(T)));
            // 实测 333.3 已越过 320，候选值必须被夹住
            o.Add("display_cap\t" + F(cap.ComputeInstantRate(T)));

            // 4) 组合式：候选值必须恒等于 min(上限, max(实测, 火势))
            var mix = new FireStateMachine();
            mix.Ingest(600, src, T.AddSeconds(-30), false);
            double expect = Math.Min(320.0, Math.Max(mix.RateFromInflows(T), mix.RateFromFlame()));
            o.Add("composition_gap\t" + F(Math.Abs(mix.ComputeInstantRate(T) - expect)));
        }

        private static string F(double v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------
        //  色带的可见来源上限
        //
        //  两条规则（都实现在 FlameColorMix.CapToTop 里）：
        //    1. 占比 < 3%（FireTuning.ColorMixMinShare）的来源不单独成色 ——
        //       色带 28 列，一列 ≈ 3.6%，比这更细的来源画出来只是一层脏色；
        //    2. 剩下的还多于上限个时，只留最重的几个。
        //  被砍掉的权重按比例并入留下的来源并重新归一化（和恒为 1），
        //  所以「砍色」只改变颜色构成，不改变色带的分量感。
        //
        //  期望值都按上面的口径手算，写在 _colorcap_check.py 里。
        // ------------------------------------------------------------------
        private static void RunColorCap(List<string> o)
        {
            var src = UsageSources.All;
            double[] w6 = { 600, 300, 60, 30, 6, 4 };      // 合计 1000
            var six = new Dictionary<UsageSource, double>();
            for (int i = 0; i < w6.Length; i++) six[src[i]] = w6[i];

            // 1) 上限 3：留下 600/300/60 → 按 960 归一化
            var top3 = FlameColorMix.CapToTop(six, 3, 0);
            o.Add("cap_top3_count\t" + F(top3.Count));
            o.Add("cap_top3_first\t" + F(Get(top3, src[0])));     // 600/960
            o.Add("cap_top3_third\t" + F(Get(top3, src[2])));     // 60/960
            o.Add("cap_top3_sum\t" + F(Sum(top3)));

            // 对照：不限个数时同一个来源只占 60/1000 —— 砍色后它被抬高了，
            // 因为被砍掉的 30+6+4 折进了留下的三个。
            var open = FlameColorMix.CapToTop(six, 0, 0);
            o.Add("cap_open_third\t" + F(Get(open, src[2])));

            // 2) 门槛 10%：600(60%) 与 300(30%) 留下，60(6%) 起全部出局
            var floor = FlameColorMix.CapToTop(six, 0, 0.10);
            o.Add("cap_floor_count\t" + F(floor.Count));
            o.Add("cap_floor_dropped\t" + F(Get(floor, src[2])));

            // 3) 门槛高到没人够 → 退化成不过滤，绝不能返回空（空 = 掉回经典橙）
            o.Add("cap_floor_fallback_count\t" + F(FlameColorMix.CapToTop(six, 0, 0.99).Count));

            // 4) 不限个数 / 来源本来就少：只重新归一化，不丢来源
            o.Add("cap_open_count\t" + F(open.Count));
            o.Add("cap_open_last\t" + F(Get(open, src[5])));
            var few = new Dictionary<UsageSource, double>();
            for (int i = 0; i < 3; i++) few[src[i]] = w6[i];
            var fewCap = FlameColorMix.CapToTop(few, 8, 0);
            o.Add("cap_few_count\t" + F(fewCap.Count));
            o.Add("cap_few_first\t" + F(Get(fewCap, src[0])));    // 600/960

            // 5) 权重打平 → 按「权重降序 · 枚举序升序」取，结果必须确定
            var ties = new Dictionary<UsageSource, double>();
            ties[src[0]] = 1; ties[src[1]] = 1; ties[src[2]] = 1;
            var tie1 = FlameColorMix.CapToTop(ties, 1, 0);
            o.Add("cap_ties_count\t" + F(tie1.Count));
            o.Add("cap_ties_kept_first\t" + F(Get(tie1, src[0])));
            o.Add("cap_ties_kept_second\t" + F(Get(tie1, src[1])));

            // 6) 端到端：12 个来源平分 → 上限 5 之后色带只剩 5 色。
            //    纯色列数顺手把 ColumnWeights 的布局也钉住（与 _bandsim.py 复刻件对账）。
            var twelve = new Dictionary<UsageSource, double>();
            for (int i = 0; i < 12; i++) twelve[src[i]] = 1;
            var capped = FlameColorMix.CapToTop(twelve, 5, 0.03);
            o.Add("band_open_distinct\t" + F(DistinctSources(twelve)));
            o.Add("band_capped_distinct\t" + F(DistinctSources(capped)));
            o.Add("band_open_pure_cols\t" + F(PureColumns(twelve)));
            o.Add("band_capped_pure_cols\t" + F(PureColumns(capped)));

            // 7) 接线：目标配色真的受上限约束。只测 CapToTop 这个纯函数证明不了这点。
            DateTime T = DateTime.Now;
            var live = new FireStateMachine();
            double[] ingest = { 600, 300, 60, 30, 6 };
            for (int i = 0; i < ingest.Length; i++)
                live.Ingest(ingest[i], src[i], T.AddSeconds(-5 - i), false);
            // 5 个来源里 6 那个只占 0.6%，先被 3% 门槛刷掉 → 4 个
            live.ColorMixSourceLimit = 5;
            o.Add("live_target_default_count\t" + F(live.TargetColorWeights(T).Count));
            live.ColorMixSourceLimit = 3;
            o.Add("live_target_cap3_count\t" + F(live.TargetColorWeights(T).Count));
            live.ColorMixSourceLimit = 0;   // 不限个数；门槛照旧
            o.Add("live_target_open_count\t" + F(live.TargetColorWeights(T).Count));
        }

        private static double Get(Dictionary<UsageSource, double> d, UsageSource s)
        {
            double v;
            return d.TryGetValue(s, out v) ? v : 0;
        }

        private static double Sum(Dictionary<UsageSource, double> d)
        {
            double s = 0;
            foreach (var kv in d) s += kv.Value;
            return s;
        }

        /// <summary>色带里实际出现的来源个数（权重高于渲染层的 0.001 门槛）。</summary>
        private static double DistinctSources(Dictionary<UsageSource, double> weights)
        {
            var rows = new FlameColorMix(weights).ColumnWeights(PixelFireEngine.FireW);
            int n = 0;
            for (int i = 0; i < UsageSources.All.Length; i++)
            {
                for (int x = 0; x < rows.Length; x++)
                {
                    if (rows[x][i] > 0.001) { n++; break; }
                }
            }
            return n;
        }

        /// <summary>纯色列数：该列 ≥90% 来自同一个来源。</summary>
        private static double PureColumns(Dictionary<UsageSource, double> weights)
        {
            var rows = new FlameColorMix(weights).ColumnWeights(PixelFireEngine.FireW);
            int n = 0;
            for (int x = 0; x < rows.Length; x++)
            {
                double max = 0;
                for (int i = 0; i < rows[x].Length; i++) if (rows[x][i] > max) max = rows[x][i];
                if (max >= 0.90) n++;
            }
            return n;
        }
    }
}
