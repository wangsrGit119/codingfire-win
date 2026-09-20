//
//  FireStateMachine.cs — CodingFire for Windows
//
//  三个互相独立的状量：
//    intensity 火势 —— 最近 60 秒的消耗强度，涨得快、落得也快
//    fuel      燃料 —— 近期新增用量攒下的燃烧储备，决定明火能撑多久
//    emberHeat 余温 —— 明火退去后的热度，衰减比火势慢得多
//  当日用量只会悄悄抬高一点「底火」，绝不充当 headline UI——与 macOS 版一致。
//

using System;
using System.Collections.Generic;
using CodingFire.Core;

namespace CodingFire.Fire
{
    /// <summary>调试 / 控制台里冻结火焰外观用的预设。</summary>
    public enum FirePreviewStyle { Out, Ember, Hush, Glow, Crackle, Roar, Blaze }

    public static class FirePreviews
    {
        public static readonly FirePreviewStyle[] All =
        {
            FirePreviewStyle.Out, FirePreviewStyle.Ember, FirePreviewStyle.Hush,
            FirePreviewStyle.Glow, FirePreviewStyle.Crackle, FirePreviewStyle.Roar,
            FirePreviewStyle.Blaze
        };

        public static string Label(this FirePreviewStyle s)
        {
            switch (s)
            {
                case FirePreviewStyle.Out: return L10n.T("phase.out");
                case FirePreviewStyle.Ember: return L10n.T("phase.ember");
                case FirePreviewStyle.Hush: return L10n.T("tier.hush");
                case FirePreviewStyle.Glow: return L10n.T("tier.glow");
                case FirePreviewStyle.Crackle: return L10n.T("tier.crackle");
                case FirePreviewStyle.Roar: return L10n.T("tier.roar");
                default: return L10n.T("tier.blaze");
            }
        }

        public static FireSnapshot Snapshot(this FirePreviewStyle s)
        {
            switch (s)
            {
                case FirePreviewStyle.Out:
                    return new FireSnapshot { Intensity = 0, Fuel = 0, EmberHeat = 0, Phase = FirePhase.Out, SparkBurst = 0, Tier = FireTier.Hush };
                case FirePreviewStyle.Ember:
                    return new FireSnapshot { Intensity = 0, Fuel = 0, EmberHeat = 0.85, Phase = FirePhase.Ember, SparkBurst = 0.15, Tier = FireTier.Hush };
                case FirePreviewStyle.Hush:
                    return new FireSnapshot { Intensity = 0.10, Fuel = 0.18, EmberHeat = 0.35, Phase = FirePhase.Flame, SparkBurst = 0.1, Tier = FireTier.Hush };
                case FirePreviewStyle.Glow:
                    return new FireSnapshot { Intensity = 0.22, Fuel = 0.35, EmberHeat = 0.45, Phase = FirePhase.Flame, SparkBurst = 0.25, Tier = FireTier.Glow };
                case FirePreviewStyle.Crackle:
                    return new FireSnapshot { Intensity = 0.45, Fuel = 0.55, EmberHeat = 0.55, Phase = FirePhase.Flame, SparkBurst = 0.45, Tier = FireTier.Crackle };
                case FirePreviewStyle.Roar:
                    return new FireSnapshot { Intensity = 0.72, Fuel = 0.78, EmberHeat = 0.7, Phase = FirePhase.Flame, SparkBurst = 0.75, Tier = FireTier.Roar };
                default:
                    return new FireSnapshot { Intensity = 1.0, Fuel = 1.0, EmberHeat = 0.9, Phase = FirePhase.Flame, SparkBurst = 1.0, Tier = FireTier.Blaze };
            }
        }
    }

    public sealed class FireStateMachine
    {
        private struct Inflow
        {
            public DateTime Date;
            public double Tokens;
            public UsageSource? Source;
            public Inflow(DateTime d, double t, UsageSource? s) { Date = d; Tokens = t; Source = s; }
        }

        private readonly FireTuning _tuning = new FireTuning();
        private readonly List<Inflow> _recentInflows = new List<Inflow>();
        private readonly Random _rng = new Random();

        private DateTime _lastTick = DateTime.Now;
        private double _burnIntensity;
        /// <summary>
        /// 只跟 60 秒窗口走的平滑火势。可见火势（_burnIntensity）会被突发窗口顶起来，
        /// 拿它反推 tok/s 会在每次添柴时瞬间顶到显示上限；用这个量反推才是「平均烧多快」。
        /// </summary>
        private double _slowIntensity;
        private double _smoothedTokensPerSecond;
        private double _rateJitter;
        // 与 macOS 版一致：初始相位随机化，否则每次启动的抖动波形逐帧相同。
        private double _rateNoisePhase;
        // Last token event time. After ~3s of silence the fire decays
        // faster and RateFromFlame fades out so the displayed tok/s drops promptly.
        private DateTime? _lastInflowAt;

        /// <summary>实时速率用更小的单条封顶，避免出现荒谬的 tok/s。</summary>
        private const double RateEventCreditTokens = 15000;
        private const double RateDisplayCapTps = 320;

        public FireSnapshot LiveSnapshot { get; private set; }
        public int TodayTokens { get; private set; }
        public Dictionary<UsageSource, int> TodayBySource { get; private set; }
        /// <summary>近期流入估算的实时速率（tokens/秒），做了平滑与轻微抖动。</summary>
        public double TokensPerSecond { get; private set; }
        /// <summary>用户改过火焰颜色后 +1，供渲染层判断是否要重建色阶。</summary>
        public int ColorPaletteEpoch { get; private set; }

        /// <summary>外部调用以触发色阶重建（用户换了火焰颜色）。</summary>
        public void BumpPaletteEpoch() { ColorPaletteEpoch++; }

        public bool AnimationPaused;

        // ------------------------------------------------------------------

        public FireStateMachine()
        {
            LiveSnapshot = FireSnapshot.Extinguished();
            TodayBySource = new Dictionary<UsageSource, int>();
            _lastTick = DateTime.Now;
            // macOS 版在声明处就做了随机化；字段初始化器里不能碰 _rng，故挪到构造函数。
            _rateNoisePhase = _rng.NextDouble() * 2 * Math.PI;
        }

        public void ApplySleepGap(double seconds)
        {
            if (seconds <= 0) return;
            Advance(Math.Min(seconds, 6 * 60 * 60));
        }

        public void ResetToUnlit()
        {
            _recentInflows.Clear();
            _burnIntensity = 0;
            _slowIntensity = 0;
            _smoothedTokensPerSecond = 0;
            _rateJitter = 0;
            TokensPerSecond = 0;
            LiveSnapshot = FireSnapshot.Extinguished();
            _lastTick = DateTime.Now;
        }

        public void UpdateTodayTokens(int tokens, Dictionary<UsageSource, int> bySource)
        {
            TodayTokens = Math.Max(0, tokens);
            TodayBySource = bySource ?? new Dictionary<UsageSource, int>();
            LiveSnapshot = Compose(LiveSnapshot);
        }

        public void NotifyColorsChanged()
        {
            ColorPaletteEpoch++;
            LiveSnapshot = Compose(LiveSnapshot);
        }

        // ------------------------------------------------------------------

        public void Ingest(double tokens, UsageSource? source, DateTime at, bool animate)
        {
            if (tokens <= 0) return;

            _recentInflows.Add(new Inflow(at, tokens, source));
            PruneInflows(at);
            _lastInflowAt = at;
            UpdateTokensPerSecond(at, 0.35);

            double compressed = SoftCompress(tokens);
            double fuelGain = Math.Min(0.55, compressed / _tuning.FuelTokenScale);

            var next = LiveSnapshot.Clone();
            next.Fuel = Math.Min(1.0, next.Fuel + fuelGain);
            next.EmberHeat = Math.Min(1.0, Math.Max(
                next.EmberHeat,
                next.Fuel * _tuning.EmberFromFuelGain + InstantaneousRatePush(tokens) * 0.25));

            if (animate)
            {
                double burst = Math.Min(1.0, InstantaneousRatePush(tokens));
                next.SparkBurst = Math.Min(_tuning.MaxSparkBurst, next.SparkBurst + 0.2 + burst * 0.9);
            }

            // 慢档：稳定燃烧水平，给 tok/s 读数当锚
            _slowIntensity = Approach(_slowIntensity, SlowTargetIntensity(at),
                                      _tuning.IntensityRiseSeconds, 0.35);

            // 火势朝「此刻烧多旺」推一把；真正的回落交给 Advance。
            // 目标一下跳得很高（添了一大把柴）就用更快的上升常数，火苗立刻窜起来。
            double target = TargetIntensity(at);
            _burnIntensity = Math.Max(_burnIntensity,
                Approach(_burnIntensity, target, _tuning.BurstRiseSeconds, 0.35));
            next.Intensity = _burnIntensity;
            next.Phase = FirePhase.Flame;
            LiveSnapshot = Compose(next);
        }

        /// <summary>由 UI 的 20Hz 定时器驱动。</summary>
        public void Tick()
        {
            var now = DateTime.Now;
            double dt = (now - _lastTick).TotalSeconds;
            _lastTick = now;
            if (dt <= 0 || AnimationPaused) return;
            Advance(Math.Min(dt, 1.0));
        }

        private void Advance(double dt)
        {
            var now = DateTime.Now;
            PruneInflows(now);
            UpdateTokensPerSecond(now, dt);

            double slowTarget = SlowTargetIntensity(now);
            double target = TargetIntensity(now);
            double fuel = LiveSnapshot.Fuel;
            double ember = LiveSnapshot.EmberHeat;
            double spark = LiveSnapshot.SparkBurst;

            // No new inflow for a while? Make the fire actually die down.
            // Default fall tau (14s) is too slow - looks "stuck on".
            double fallTau = _tuning.IntensityFallSeconds;
            if (_lastInflowAt.HasValue)
            {
                double silence = (now - _lastInflowAt.Value).TotalSeconds;
                if (silence > _tuning.IdleFallStartSeconds)
                {
                    double t = Math.Min(1.0, (silence - _tuning.IdleFallStartSeconds) /
                                           Math.Max(0.1, _tuning.IdleFallRampSeconds));
                    fallTau = _tuning.IntensityFallSeconds
                        + (_tuning.IntensityFallSecondsIdle - _tuning.IntensityFallSeconds) * t;
                }
            }
            else if (_burnIntensity > 0.02)
            {
                // First launch with only historical data: also fall fast
                fallTau = _tuning.IntensityFallSecondsIdle;
            }

            // 慢档跟着 60 秒窗口走，用原来的上升时间常数。它只服务 tok/s 读数。
            _slowIntensity = Approach(_slowIntensity, slowTarget, _tuning.IntensityRiseSeconds, dt);

            // 可见火势：目标里含 6 秒突发项。目标一下跳得很高（添了一大把柴）就用
            // 更快的上升常数，火苗立刻窜起来；回落仍然走上面那套慢的 fallTau，
            // 于是「窜得快、塌得慢」—— 真营火就是这个形状。
            double riseTau = (target - _burnIntensity) > _tuning.BurstJumpThreshold
                ? _tuning.BurstRiseSeconds
                : _tuning.IntensityRiseSeconds;
            double tau = target > _burnIntensity ? riseTau : fallTau;
            double alpha = 1.0 - Math.Exp(-dt / Math.Max(0.05, tau));
            _burnIntensity += (target - _burnIntensity) * alpha;
            _burnIntensity = Math.Min(1.0, Math.Max(0, _burnIntensity));

            // 燃料只是「余温的存量」，不会反过来抬高火势
            if (fuel > 0)
            {
                double burnRate = _tuning.FuelBurnPerSecondAtIdle
                    + (_tuning.FuelBurnPerSecondAtFull - _tuning.FuelBurnPerSecondAtIdle)
                      * Math.Pow(Math.Max(_burnIntensity, target), 1.1);
                fuel = Math.Max(0, fuel - burnRate * dt);
                ember = Math.Max(ember, fuel * 0.55 + _burnIntensity * 0.25);
            }
            else
            {
                ember = Math.Max(0, ember - _tuning.EmberDecayPerSecond * dt);
            }

            spark = Math.Max(0, spark - _tuning.SparkDecayPerSecond * dt);


            LiveSnapshot = Compose(new FireSnapshot
            {
                Intensity = _burnIntensity,
                Fuel = fuel,
                EmberHeat = ember,
                Phase = FirePhase.Flame,
                SparkBurst = spark,
                Tier = FireTier.Hush,
                FlameAccent = LiveSnapshot.FlameAccent,
            });

        }

        // ------------------------------------------------------------------
        // 速率估算
        // ------------------------------------------------------------------

        /// <summary>
        /// 窗口内的实测速率（token/秒）：每条封顶 <see cref="RateEventCreditTokens"/>，
        /// 且只认落在窗口内的流入。
        /// </summary>
        internal double RateFromInflows(DateTime now)
        {
            double window = Math.Max(1.0, _tuning.RateWindowSeconds);
            DateTime cutoff = now.AddSeconds(-window);
            double credited = 0;
            for (int i = 0; i < _recentInflows.Count; i++)
            {
                // 迟到事件（工具退出时才 flush 出来的旧时间戳）仍然该让火焰窜一下，
                // 但不该伪造出一个「当下正在以这个速度烧」的读数，所以按窗口剔除。
                if (_recentInflows[i].Date < cutoff) continue;
                credited += Math.Min(_recentInflows[i].Tokens, RateEventCreditTokens);
            }
            return credited / window;
        }

        /// <summary>
        /// 把当前火势反查成 token/秒，让显示的速率跟看得见的火势一致。
        /// 用慢档而不是可见火势：可见火势含 6 秒突发项，拿它反推会在每次添柴时
        /// 直接顶到显示上限（一条 45k 的添柴反推出来是 3000 tok/s），读数就没意义了。
        /// </summary>
        internal double RateFromFlame()
        {
            return TokensPerSecondMatchingFlame(_slowIntensity);
        }

        /// <summary>
        /// 平滑与抖动之前的候选速率 = min(显示上限, max(实测, 火势反推))。
        /// 单独拆出来是为了它能被夹具断言 —— 抖动一旦叠上去，显示值就不可复现了。
        /// </summary>
        internal double ComputeInstantRate(DateTime now)
        {
            double inflowRate = RateFromInflows(now);
            double flameRate = RateFromFlame();

            // When inflow stream goes stale, RateFromFlame keeps the number
            // high even though no tokens are coming in. Fade it out exponentially.
            if (_lastInflowAt.HasValue)
            {
                double silence = (now - _lastInflowAt.Value).TotalSeconds;
                if (silence > _tuning.IdleFallStartSeconds)
                {
                    double fade = Math.Exp(-(silence - _tuning.IdleFallStartSeconds)
                                            / Math.Max(0.1, _tuning.IdleFlameRateTau));
                    flameRate *= fade;
                }
            }
            else
            {
                flameRate = 0;
            }

            return Math.Min(RateDisplayCapTps, Math.Max(inflowRate, flameRate));
        }

        private void UpdateTokensPerSecond(DateTime now, double dt)
        {
            double instant = ComputeInstantRate(now);

            double tau = instant > _smoothedTokensPerSecond ? 0.9 : 2.8;
            double alpha = 1.0 - Math.Exp(-dt / Math.Max(0.05, tau));
            _smoothedTokensPerSecond += (instant - _smoothedTokensPerSecond) * alpha;

            if (_burnIntensity < 0.04)
            {
                _smoothedTokensPerSecond = 0;
                _rateJitter = 0;
                if (TokensPerSecond != 0) TokensPerSecond = 0;
                return;
            }

            // 多频小抖动（±8–14%），让悬停看到的数字「活着」
            _rateNoisePhase += dt;
            double baseRate = Math.Max(0.5, _smoothedTokensPerSecond);
            double amp = Math.Max(0.6, baseRate * (0.08 + 0.04 * _burnIntensity));
            double wander =
                Math.Sin(_rateNoisePhase * 1.65) * amp * 0.50
                + Math.Sin(_rateNoisePhase * 0.41 + 1.3) * amp * 0.32
                + Math.Sin(_rateNoisePhase * 3.1 + 0.4) * amp * 0.12
                + (_rng.NextDouble() * 2 - 1) * amp * 0.18;

            double jitterAlpha = 1.0 - Math.Exp(-dt / 0.35);
            _rateJitter += (wander - _rateJitter) * jitterAlpha;

            double displayed = Math.Min(RateDisplayCapTps, Math.Max(0.2, _smoothedTokensPerSecond + _rateJitter));
            double rounded = Math.Round(displayed * 10) / 10;
            if (Math.Abs(rounded - TokensPerSecond) >= 0.05 || (rounded == 0) != (TokensPerSecond == 0))
                TokensPerSecond = rounded;
        }

        /// <summary>把火焰的 TPM 锚点反过来用，让显示的速率跟着看得见的火势走。</summary>
        private double TokensPerSecondMatchingFlame(double intensity)
        {
            if (intensity <= 0.04) return 0;
            return TpmForIntensity(intensity) / 60.0;
        }

        private double TpmForIntensity(double intensity)
        {
            var tpm = _tuning.TpmAnchors;
            var val = _tuning.TpmIntensity;
            if (intensity <= val[0]) return tpm[0];
            if (intensity >= val[val.Length - 1]) return tpm[tpm.Length - 1];
            for (int i = 0; i < val.Length - 1; i++)
            {
                if (intensity <= val[i + 1])
                {
                    double span = Math.Max(0.0001, val[i + 1] - val[i]);
                    double t = (intensity - val[i]) / span;
                    return tpm[i] + (tpm[i + 1] - tpm[i]) * t;
                }
            }
            return tpm[tpm.Length - 1];
        }

        /// <summary>
        /// 火势窗口（IntensityWindowSeconds）下的火势：火苗此刻该烧多旺。
        /// 窗口比速率读数短，所以活动一起来火就窜、一停火就落。
        /// </summary>
        private double TargetIntensity(DateTime now)
        {
            return IntensityFromCredited(CreditedTokensIn(now, _tuning.IntensityWindowSeconds),
                                         _tuning.IntensityWindowSeconds);
        }

        /// <summary>速率读数窗口（RateWindowSeconds）下的火势：平均烧多快。只服务 tok/s 读数。</summary>
        private double SlowTargetIntensity(DateTime now)
        {
            return IntensityFromCredited(CreditedTokensIn(now, _tuning.RateWindowSeconds),
                                         _tuning.RateWindowSeconds);
        }

        /// <summary>窗口内各条流入的计分之和（单条按 EventCreditTokens 封顶）。</summary>
        private double CreditedTokensIn(DateTime now, double window)
        {
            double w = Math.Max(1.0, window);
            DateTime cutoff = now.AddSeconds(-w);
            double credited = 0;
            for (int i = 0; i < _recentInflows.Count; i++)
            {
                if (_recentInflows[i].Date < cutoff) continue;
                credited += Credit(_recentInflows[i].Tokens);
            }
            return credited;
        }

        private double IntensityFromCredited(double credited, double window)
        {
            double tokensPerMinute = credited * (60.0 / Math.Max(1.0, window));
            return IntensityFromTpm(tokensPerMinute);
        }

        /// <summary>指数逼近：dt 秒内向 target 走 (1 - e^(-dt/tau))。</summary>
        private static double Approach(double current, double target, double tau, double dt)
        {
            double alpha = 1.0 - Math.Exp(-dt / Math.Max(0.05, tau));
            double v = current + (target - current) * alpha;
            return Math.Min(1.0, Math.Max(0, v));
        }

        /// <summary>TPM → 火势：分段不平坦，中火容易、大火次之、烈火很难。</summary>
        private double IntensityFromTpm(double tpm)
        {
            var anchors = _tuning.TpmAnchors;
            var val = _tuning.TpmIntensity;
            if (tpm <= anchors[0]) return val[0];
            if (tpm >= anchors[anchors.Length - 1]) return val[val.Length - 1];
            for (int i = 0; i < anchors.Length - 1; i++)
            {
                if (tpm <= anchors[i + 1])
                {
                    double span = Math.Max(1.0, anchors[i + 1] - anchors[i]);
                    double t = (tpm - anchors[i]) / span;
                    double s = t * t * (3 - 2 * t); // smoothstep，档位切换不会一格一格地跳
                    return val[i] + (val[i + 1] - val[i]) * s;
                }
            }
            return val[val.Length - 1];
        }

        private double Credit(double tokens) { return Math.Min(tokens, _tuning.EventCreditTokens); }

        private double InstantaneousRatePush(double tokens)
        {
            return IntensityFromTpm(Credit(tokens) * (60.0 / Math.Max(1.0, _tuning.IntensityWindowSeconds)));
        }

        /// <summary>静默底火：渐近逼近「微火」，从不自称是一种模式。</summary>
        private double DailyBaseIntensity()
        {
            double t = TodayTokens;
            if (t < _tuning.DailyBaseStartTokens) return 0;
            double x = (t - _tuning.DailyBaseStartTokens) / _tuning.DailyBaseHalfTokens;
            double shaped = 1.0 - Math.Exp(-x);
            return _tuning.DailyBaseMaxIntensity * shaped;
        }

        private FireSnapshot Compose(FireSnapshot snap)
        {
            var next = snap.Clone();
            double baseIntensity = DailyBaseIntensity();
            // 显示值 = max(实时火势, 静默底火)；底火绝不回灌进 burnIntensity
            double shown = Math.Max(_burnIntensity, baseIntensity);

            next.Intensity = shown;
            next.Tier = FireTiers.FromIntensity(shown);

            if (shown >= 0.035)
            {
                next.Phase = FirePhase.Flame;
                next.EmberHeat = Math.Max(next.EmberHeat, shown * 0.4);
            }
            else if (next.EmberHeat > 0.04 || next.Fuel > 0.03)
            {
                next.Phase = FirePhase.Ember;
                next.Intensity = 0;
                next.Tier = FireTier.Hush;
            }
            else if (next.Phase == FirePhase.Unlit)
            {
                next.Phase = FirePhase.Unlit;
                next.Intensity = 0;
            }
            else
            {
                next.Phase = FirePhase.Out;
                next.Intensity = 0;
                next.Fuel = 0;
                next.EmberHeat = 0;
                next.SparkBurst = 0;
            }
            return next;
        }

        /// <summary>
        /// 流入记录要留到**最长**的那个窗口之外才能丢：火势窗口比速率窗口短，
        /// 按火势窗口剪枝会把速率读数要用的记录提前扔掉。
        /// </summary>
        private double RetentionSeconds
        {
            get
            {
                return Math.Max(_tuning.IntensityWindowSeconds, _tuning.RateWindowSeconds);
            }
        }

        private void PruneInflows(DateTime now)
        {
            DateTime cutoff = now.AddSeconds(-RetentionSeconds);
            for (int i = _recentInflows.Count - 1; i >= 0; i--)
                if (_recentInflows[i].Date < cutoff) _recentInflows.RemoveAt(i);
            if (_recentInflows.Count == 0) _lastInflowAt = null;
        }

        /// <summary>只用于燃料增量的轻度压缩（火势走原始速率）。</summary>
        public static double SoftCompress(double tokens)
        {
            if (tokens <= 0) return 0;
            return tokens / (1.0 + tokens / 140000.0);
        }
    }
}
