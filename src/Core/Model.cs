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

    public sealed class FireSnapshot
    {
        public double Intensity;
        public double Fuel;
        public double EmberHeat;
        public FirePhase Phase = FirePhase.Unlit;
        public double SparkBurst;
        public FireTier Tier = FireTier.Hush;
        /// <summary>用户选择的火焰主题色（归一化 RGB）。火苗、辉光和火星都跟它走。</summary>
        public double[] FlameAccent = new double[] { 0.95, 0.55, 0.2 };

        public static FireSnapshot Extinguished()
        {
            return new FireSnapshot { Intensity = 0, Fuel = 0, EmberHeat = 0, Phase = FirePhase.Unlit, SparkBurst = 0, Tier = FireTier.Hush, FlameAccent = new double[] { 0.95, 0.55, 0.2 } };
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
                FlameAccent = (double[])FlameAccent.Clone(),
            };
        }
    }

    /// <summary>调参起点。除注明外，数值与 macOS 版 FireTuning 一致。</summary>
    public sealed class FireTuning
    {
        /// <summary>
        /// 火势窗口：火苗「此刻烧多旺」的观察尺度。
        ///
        /// 这里**故意**比 macOS 版的 60 秒短。窗口只影响瞬态，不影响稳态：
        /// 一个稳定速率 R 在任意窗口 W 下算出的 TPM 都是 60R（credited = R·W，
        /// 再乘 60/W 归一），所以锚点表不用动。
        ///
        /// 但瞬态差得很远。60 秒窗口下，一轮对话结束后那批事件还要在窗口里
        /// 待满 60 秒，火就一直亮着不掉 —— 用户感觉到的「不实时」主要是这个「不掉」，
        /// 而不是「不涨」。缩到 20 秒，火跟着活动起落，才像营火。
        /// </summary>
        public double IntensityWindowSeconds = 20;

        /// <summary>
        /// 速率读数窗口：显示成 tok/s 的那个数字。
        /// 保持 60 秒，因为它是「平均烧多快」，不是「刚刚那一下多猛」——
        /// 窗口再短，单条记录（封顶 15000）除以窗口就会超过显示上限 320，读数永远顶格。
        /// </summary>
        public double RateWindowSeconds = 60;

        /// <summary>突发时火势上升的时间常数。往营火里添一把柴，火是瞬间窜起来的。</summary>
        public double BurstRiseSeconds = 0.7;

        /// <summary>目标火势一下跳这么高才算「添了一大把柴」，才启用 BurstRiseSeconds。</summary>
        public double BurstJumpThreshold = 0.08;

        /// <summary>分段 TPM → intensity：~0.8k 微火 · ~2.5k 小火 · ~12k 中火 · ~45k 大火 · ~180k 烈火</summary>
        public readonly double[] TpmAnchors = { 0, 800, 2500, 12000, 45000, 180000 };
        public readonly double[] TpmIntensity = { 0, 0.10, 0.26, 0.48, 0.72, 1.0 };

        /// <summary>单条日志最多记这么多 token（整轮 dump 的封顶）。</summary>
        public double EventCreditTokens = 45000;

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
