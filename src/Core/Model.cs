//
//  Model.cs — CodingFire for Windows
//
//  与 macOS 版 UsageEvent.swift / FireStateMachine 中的数据结构一一对应。
//

using System;
using System.Collections.Generic;

namespace CodingFire.Core
{
    /// <summary>
    /// 数据来源。rawValue 用于持久化与配色 key。
    /// 前六个与 macOS 版 UsageSource.allCases 完全一致（保持向后兼容）；
    /// 之后是为「多工具聚合」新增的来源，命名对齐 juejin-usage 的 sourceKey。
    /// </summary>
    public enum UsageSource
    {
        // ---- 原版 macOS 的六个 ----
        ClaudeCode,
        Codex,
        Cursor,
        Grok,
        Pi,
        Amp,

        // ---- 新增：国内常用 / 官方追踪器已覆盖的工具 ----
        WorkBuddy,
        CodeBuddy,
        Qoder,
        QwenCode,
        Kimi,
        Copilot,
        ZCode,
        OpenCode,
        GeminiCli,
        Kiro,
        Droid,
        Cline,
        RooCode,
        KiloCode,
        DeepSeekHarness,
        CommandCode,
        OpenClaw,
        EveryCode,

        // ---- 同一工具的「地区版本」变体 ----
        // WorkBuddy 国内版与国外版是两份独立安装，日志根目录不同（~/.workbuddy
        // 与 ~/.workbuddy-ai），格式一模一样。分开统计才能看出是哪边在烧 token。
        // 必须追加在枚举末尾：索引即配色分带的序号，插在中间会让既有来源集体变色。
        WorkBuddyIntl
    }

    public static class UsageSources
    {
        /// <summary>
        /// 顺序决定火焰横向配色分带的先后，所以「原有的六个必须原样排在最前」——
        /// 这样只跑老源的老用户，火苗配色与升级前完全一致。
        /// </summary>
        public static readonly UsageSource[] All =
        {
            UsageSource.ClaudeCode,
            UsageSource.Codex,
            UsageSource.Cursor,
            UsageSource.Grok,
            UsageSource.Pi,
            UsageSource.Amp,
            UsageSource.WorkBuddy,
            UsageSource.CodeBuddy,
            UsageSource.Qoder,
            UsageSource.QwenCode,
            UsageSource.Kimi,
            UsageSource.Copilot,
            UsageSource.ZCode,
            UsageSource.OpenCode,
            UsageSource.GeminiCli,
            UsageSource.Kiro,
            UsageSource.Droid,
            UsageSource.Cline,
            UsageSource.RooCode,
            UsageSource.KiloCode,
            UsageSource.DeepSeekHarness,
            UsageSource.CommandCode,
            UsageSource.OpenClaw,
            UsageSource.EveryCode,
            UsageSource.WorkBuddyIntl
        };

        private static readonly string[] RawNames =
        {
            "claude_code", "codex", "cursor", "grok", "pi", "amp",
            "workbuddy", "codebuddy", "qoder", "qwen", "kimi", "copilot",
            "zcode", "opencode", "gemini", "kiro", "droid", "cline",
            "roocode", "kilocode", "dsh", "command-code", "openclaw", "every-code",
            "workbuddy-intl"
        };

        private static readonly string[] DisplayNames =
        {
            "Claude Code", "Codex", "Cursor", "Grok", "Pi", "Amp",
            "WorkBuddy", "CodeBuddy", "Qoder", "Qwen Code", "Kimi", "GitHub Copilot",
            "ZCode", "OpenCode", "Gemini CLI", "Kiro", "Droid", "Cline",
            "Roo Code", "Kilo Code", "DeepSeek Harness", "Command Code", "OpenClaw", "Every Code",
            "WorkBuddy INTL"
        };

        static UsageSources()
        {
            // 三张表必须等长——不一致说明有人加源时漏改了配套数组。
            if (RawNames.Length != All.Length || DisplayNames.Length != All.Length)
                throw new InvalidOperationException("UsageSources tables out of sync");
        }

        public static string Raw(this UsageSource s)
        {
            int i = IndexOf(s);
            return RawNames[i];
        }

        public static UsageSource? FromRaw(string raw)
        {
            if (raw == null) return null;
            for (int i = 0; i < RawNames.Length; i++)
                if (string.Equals(RawNames[i], raw, StringComparison.Ordinal)) return All[i];
            return null;
        }

        /// <summary>
        /// 稳定、不本地化的英文产品名（用于日志与持久化）。
        /// 少数来源需要区分地区版本时，用 L10n 的 source.name.&lt;raw&gt; 覆盖显示名
        /// （例：WorkBuddy 国内版 / 国外版）；没写该键的来源一律走英文名。
        /// </summary>
        public static string DisplayName(this UsageSource s)
        {
            int i = IndexOf(s);
            string key = "source.name." + RawNames[i];
            if (L10n.Has(key)) return L10n.T(key);
            return DisplayNames[i];
        }

        private static int IndexOf(UsageSource s)
        {
            int i = (int)s;
            return (i >= 0 && i < All.Length) ? i : 0;
        }
    }

    public enum SourceConnectionState
    {
        Ok,
        NotFound,
        NoPermission,
        Unsupported,
        ReadError
    }

    public static class SourceStates
    {
        public static string LabelKey(this SourceConnectionState s)
        {
            switch (s)
            {
                case SourceConnectionState.Ok: return "source.state.ok";
                case SourceConnectionState.NotFound: return "source.state.notFound";
                case SourceConnectionState.NoPermission: return "source.state.noPermission";
                case SourceConnectionState.Unsupported: return "source.state.unsupported";
                default: return "source.state.readError";
            }
        }

        public static string Label(this SourceConnectionState s) { return L10n.T(s.LabelKey()); }
    }

    public sealed class UsageBreakdown
    {
        public int? Input;
        public int? Output;
        public int? CacheRead;
        public int? CacheWrite;

        public UsageBreakdown() { }

        public UsageBreakdown(int? input, int? output, int? cacheRead, int? cacheWrite)
        {
            Input = input; Output = output; CacheRead = cacheRead; CacheWrite = cacheWrite;
        }

        public int BillableTotal
        {
            get { return (Input ?? 0) + (Output ?? 0) + (CacheRead ?? 0) + (CacheWrite ?? 0); }
        }
    }

    public sealed class UsageEvent
    {
        public string Id;
        public UsageSource Source;
        /// <summary>本地时间。</summary>
        public DateTime Timestamp;
        public int Tokens;
        public UsageBreakdown Breakdown = new UsageBreakdown();
        public string FilePath = "";
        public bool IsEstimated;

        public UsageEvent Clone()
        {
            return new UsageEvent
            {
                Id = Id,
                Source = Source,
                Timestamp = Timestamp,
                Tokens = Tokens,
                Breakdown = new UsageBreakdown(Breakdown.Input, Breakdown.Output, Breakdown.CacheRead, Breakdown.CacheWrite),
                FilePath = FilePath,
                IsEstimated = IsEstimated
            };
        }
    }

    public sealed class SourceStatus
    {
        public UsageSource Source;
        public SourceConnectionState State = SourceConnectionState.NotFound;
        public string Detail = "—";
        public DateTime? LastReadAt;
        public int TodayTokens;
    }

    public sealed class HourlyUsage
    {
        public int Hour;
        public int Tokens;

        public HourlyUsage(int hour, int tokens) { Hour = hour; Tokens = tokens; }
    }

    // ------------------------------------------------------------------
    // 火焰
    // ------------------------------------------------------------------

    public enum FirePhase { Unlit, Flame, Ember, Out }

    public enum FireTier { Hush, Glow, Crackle, Roar, Blaze }

    public static class FireTiers
    {
        public static string LabelKey(this FireTier t)
        {
            switch (t)
            {
                case FireTier.Hush: return "tier.hush";
                case FireTier.Glow: return "tier.glow";
                case FireTier.Crackle: return "tier.crackle";
                case FireTier.Roar: return "tier.roar";
                default: return "tier.blaze";
            }
        }

        public static string Label(this FireTier t) { return L10n.T(t.LabelKey()); }

        /// <summary>火势档位只由实时 intensity 决定，燃料不参与（与 macOS 版一致）。</summary>
        public static FireTier FromIntensity(double intensity)
        {
            if (intensity < 0.16) return FireTier.Hush;
            if (intensity < 0.34) return FireTier.Glow;
            if (intensity < 0.56) return FireTier.Crackle;
            if (intensity < 0.78) return FireTier.Roar;
            return FireTier.Blaze;
        }
    }

    /// <summary>多来源火焰配色权重（空 = 经典橙色火）。</summary>
    public sealed class FlameColorMix
    {
        public readonly Dictionary<UsageSource, double> Weights;

        public FlameColorMix() { Weights = new Dictionary<UsageSource, double>(); }

        public FlameColorMix(Dictionary<UsageSource, double> weights)
        {
            Weights = weights ?? new Dictionary<UsageSource, double>();
        }

        public static readonly FlameColorMix Classic = new FlameColorMix();

        public bool IsClassic
        {
            get
            {
                double sum = 0;
                foreach (var kv in Weights) sum += kv.Value;
                return sum < 0.02;
            }
        }

        /// <summary>
        /// 色带的可见性上限：色带只有 28 列，来源一多每色都只剩一条缝，
        /// 糊成一片。这里砍到「能看清」为止——
        ///
        ///   1. 占比低于 <paramref name="minShare"/> 的来源不单独成色
        ///      （它们一列都站不满，单独占位只会把相邻色搅浑）；
        ///   2. 剩下的还多于 <paramref name="maxSources"/> 个时，只留最重的几个。
        ///
        /// 被砍掉的权重不丢弃，按比例并入留下来的来源（重新归一化），
        /// 所以色带的「分量感」不会因为砍色而变淡。
        /// <paramref name="maxSources"/> &lt;= 0 = 不限个数；<paramref name="minShare"/> &lt;= 0 = 不设门槛。
        /// 排序是「权重降序 · 枚举序升序」，权重打平时结果同样确定。
        /// </summary>
        public static Dictionary<UsageSource, double> CapToTop(
            Dictionary<UsageSource, double> weights, int maxSources, double minShare)
        {
            var result = new Dictionary<UsageSource, double>();
            if (weights == null || weights.Count == 0) return result;

            var all = new List<KeyValuePair<UsageSource, double>>();
            double total = 0;
            for (int i = 0; i < UsageSources.All.Length; i++)
            {
                var src = UsageSources.All[i];
                double w;
                if (!weights.TryGetValue(src, out w) || w <= 0) continue;
                all.Add(new KeyValuePair<UsageSource, double>(src, w));
                total += w;
            }
            if (all.Count == 0 || total <= 0) return result;

            var kept = new List<KeyValuePair<UsageSource, double>>();
            if (minShare > 0)
            {
                for (int i = 0; i < all.Count; i++)
                    if (all[i].Value / total >= minShare) kept.Add(all[i]);
                // 门槛把所有人都挡了（理论上不会发生）：退化成不过滤，别让火焰掉回经典橙。
                if (kept.Count == 0) kept.AddRange(all);
            }
            else kept.AddRange(all);

            if (maxSources > 0 && kept.Count > maxSources)
            {
                // 注意比较器的方向：返回正数 = a 排在 b 后面，所以「b 比 a 重」要返回 1，
                // 这样才是降序（重的在前）。写反了就成了「只留最轻的几个」。
                kept.Sort(delegate (KeyValuePair<UsageSource, double> a, KeyValuePair<UsageSource, double> b)
                {
                    if (a.Value != b.Value) return b.Value > a.Value ? 1 : -1;
                    return ((int)a.Key).CompareTo((int)b.Key);
                });
                kept.RemoveRange(maxSources, kept.Count - maxSources);
            }

            double sum = 0;
            for (int i = 0; i < kept.Count; i++) sum += kept[i].Value;
            if (sum <= 0) return result;
            for (int i = 0; i < kept.Count; i++) result[kept[i].Key] = kept[i].Value / sum;
            return result;
        }

        /// <summary>
        /// 按来源顺序切出柔和横向色带并做羽化，避免硬条纹；
        /// 直接对应 macOS 版 FlameColorMix.columnWeights(width:)。
        /// </summary>
        public double[][] ColumnWeights(int width)
        {
            var sources = UsageSources.All;
            var raw = new double[sources.Length];
            double total = 0;
            for (int i = 0; i < sources.Length; i++)
            {
                double w;
                Weights.TryGetValue(sources[i], out w);
                if (w < 0) w = 0;
                raw[i] = w;
                total += w;
            }

            var outRows = new double[Math.Max(width, 0)][];
            for (int x = 0; x < outRows.Length; x++) outRows[x] = new double[sources.Length];

            if (total <= 0.02 || width <= 0) return outRows;

            var norm = new double[sources.Length];
            for (int i = 0; i < sources.Length; i++) norm[i] = raw[i] / total;

            var edges = new double[sources.Length + 1];
            double cum = 0;
            for (int i = 0; i < sources.Length; i++) { cum += norm[i]; edges[i + 1] = cum; }

            const double feather = 0.14;
            for (int x = 0; x < width; x++)
            {
                double u = (x + 0.5) / width;
                var row = outRows[x];
                for (int i = 0; i < sources.Length; i++)
                {
                    if (norm[i] <= 0.001) continue;
                    double start = edges[i];
                    double end = edges[i + 1];
                    double center = (start + end) * 0.5;
                    double half = Math.Max(0.06, (end - start) * 0.5 + feather * 0.5);
                    double d = Math.Abs(u - center);
                    double t = Math.Max(0, 1 - d / half);
                    row[i] = t * t * (3 - 2 * t); // smoothstep
                }
                double s = 0;
                for (int i = 0; i < row.Length; i++) s += row[i];
                if (s > 0) for (int i = 0; i < row.Length; i++) row[i] /= s;
            }
            return outRows;
        }
    }

    public sealed class FireSnapshot
    {
        public double Intensity;
        public double Fuel;
        public double EmberHeat;
        public FirePhase Phase = FirePhase.Unlit;
        public double SparkBurst;
        public FireTier Tier = FireTier.Hush;
        public FlameColorMix ColorMix = FlameColorMix.Classic;
        /// <summary>用户选择的火焰主题色（归一化 RGB）。单色模式用。</summary>
        public double[] FlameAccent = new double[] { 0.95, 0.55, 0.2 };

        public static FireSnapshot Extinguished()
        {
            return new FireSnapshot { Intensity = 0, Fuel = 0, EmberHeat = 0, Phase = FirePhase.Unlit, SparkBurst = 0, Tier = FireTier.Hush, ColorMix = FlameColorMix.Classic, FlameAccent = new double[] { 0.95, 0.55, 0.2 } };
        }

        public FireSnapshot Clone()
        {
            return new FireSnapshot
            {
                Intensity = Intensity,
                Fuel = Fuel,
                EmberHeat = EmberHeat,
                Phase = Phase,
                SparkBurst = SparkBurst,
                Tier = Tier,
                ColorMix = ColorMix,
                FlameAccent = (double[])FlameAccent.Clone(),
            };
        }
    }

    /// <summary>调参起点，数值与 macOS 版 FireTuning 完全一致。</summary>
    public sealed class FireTuning
    {
        public double IntensityWindowSeconds = 60;

        /// <summary>分段 TPM → intensity：~0.8k 微火 · ~2.5k 小火 · ~12k 中火 · ~45k 大火 · ~180k 烈火</summary>
        public readonly double[] TpmAnchors = { 0, 800, 2500, 12000, 45000, 180000 };
        public readonly double[] TpmIntensity = { 0, 0.10, 0.26, 0.48, 0.72, 1.0 };

        /// <summary>单条日志最多记这么多 token（整轮 dump 的封顶）。</summary>
        public double EventCreditTokens = 45000;

        /// <summary>
        /// 一个来源要占窗口内多少占比，才有资格在色带上单独成色。
        /// 色带 28 列，一列 ≈ 3.6%；比这更细的来源画出来只是一层脏色。
        /// </summary>
        public double ColorMixMinShare = 0.03;

        public double IntensityRiseSeconds = 2.5;
        public double IntensityFallSeconds = 14;
        /// <summary>Fall tau used after the input stream has gone silent.
        /// Default 14s is too slow - looks "stuck on". Real campfire dies faster.</summary>
        public double IntensityFallSecondsIdle = 5.0;
        /// <summary>Seconds with no new inflow before we treat input as stopped.</summary>
        public double IdleFallStartSeconds = 3.0;
        /// <summary>How long to ramp from normal fall tau to idle fall tau after silence starts.</summary>
        public double IdleFallRampSeconds = 4.0;
        /// <summary>ComputeInstantRate: exponential fade tau for RateFromFlame after silence.</summary>
        public double IdleFlameRateTau = 3.0;
        public double FuelTokenScale = 120000;
        public double FuelBurnPerSecondAtFull = 1.0 / 90;
        public double FuelBurnPerSecondAtIdle = 1.0 / 150;
        public double EmberDecayPerSecond = 1.0 / (2.5 * 60);
        public double EmberFromFuelGain = 0.4;
        public double MaxSparkBurst = 1.0;
        public double SparkDecayPerSecond = 1.4;

        /// <summary>当日用量缓慢抬高的“底火”，不参与 headline UI。</summary>
        public double DailyBaseStartTokens = 40000;
        public double DailyBaseHalfTokens = 600000;
        public double DailyBaseMaxIntensity = 0.22;
    }
}
