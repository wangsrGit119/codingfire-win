//
//  CampfireRenderer.cs — CodingFire for Windows
//
//  把热场 + 柴堆 + 火星合成到一张 32bppPArgb 位图（预乘 alpha，供 UpdateLayeredWindow 使用）。
//  布局逐项对应 macOS 版 FireScene：
//    logNode   : 以场景原点为中心
//    flameNode : 底部锚点，位于 flameBaseY - px
//    sparkNodes: 按档位数量与上浮速度运动
//

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using CodingFire.Core;
using CodingFire.Fire;

namespace CodingFire.Ui
{
    internal sealed class CampfireRenderer : IDisposable
    {
        private readonly PixelFireEngine _engine = new PixelFireEngine(PixelFireEngine.FireW, PixelFireEngine.FireH);
        private readonly Rgba[,] _log = CampfireSprites.Log();
        private readonly Rgba[,] _spark = CampfireSprites.Spark();

        private Bitmap _bitmap;
        private int _width;
        private int _height;

        private double _flameAlpha;
        private double _lastStepTime;
        private FireTier _lastTier = FireTier.Hush;
        private FirePhase _lastPhase = FirePhase.Unlit;
        private bool _firstFrame = true;
        private bool _dragging;

        /// <summary>当前面板尺寸（物理像素）。</summary>
        public int Width { get { return _width; } }
        public int Height { get { return _height; } }
        public Bitmap Bitmap { get { return _bitmap; } }
        public PixelFireEngine Engine { get { return _engine; } }

        /// <summary>火焰在面板中的位置，供悬停卡片定位。</summary>
        public int FlameTipY { get; private set; }
        public int FlameBaseY { get; private set; }

        public void Resize(int width, int height)
        {
            if (width == _width && height == _height && _bitmap != null) return;
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            if (_bitmap != null) _bitmap.Dispose();
            _bitmap = new Bitmap(_width, _height, PixelFormat.Format32bppPArgb);
        }

        public void ResetEngine() { _engine.Reset(); }

        public void SetDragging(bool dragging) { _dragging = dragging; }

        /// <summary>合成一帧。</summary>
        public void Render(FireSnapshot snap, double pixelScale, bool reduceMotion, double timeSeconds)
        {
            if (_bitmap == null) return;

            _engine.Intensity = snap.Intensity;
            _engine.Tier = snap.Tier;
            _engine.Phase = snap.Phase;
            _engine.EmberHeat = snap.EmberHeat;
            if (snap.FlameAccent != null)
            {
                _engine.FlameAccent = snap.FlameAccent;
                _engine.AccentEpoch++;  // trigger ramp rebuild
            }

            bool phaseChanged = snap.Phase != _lastPhase;
            bool tierChanged = snap.Tier != _lastTier;
            _lastPhase = snap.Phase;
            _lastTier = snap.Tier;

            // 阶段 / 档位切换时直接给到位，其余时候缓动，避免闪
            double targetAlpha;
            switch (snap.Phase)
            {
                case FirePhase.Unlit:
                case FirePhase.Out: targetAlpha = 0; break;
                case FirePhase.Ember: targetAlpha = 0.85; break;
                default: targetAlpha = 1.0; break;
            }
            if (phaseChanged || tierChanged) _flameAlpha = targetAlpha;
            else _flameAlpha = _flameAlpha + (targetAlpha - _flameAlpha) * 0.35;

            if (snap.Phase == FirePhase.Unlit || snap.Phase == FirePhase.Out)
            {
                if (_flameAlpha < 0.02) { _flameAlpha = 0; _engine.Reset(); }
            }

            // 模拟步进：减少动态效果时 6fps，否则 12fps
            double stepFps = reduceMotion ? 6 : 12;
            if (_lastStepTime == 0) _lastStepTime = timeSeconds;
            if (_firstFrame || timeSeconds - _lastStepTime >= 1.0 / stepFps)
            {
                _firstFrame = false;
                _lastStepTime = timeSeconds;
                if (snap.Phase == FirePhase.Flame || snap.Phase == FirePhase.Ember || _flameAlpha > 0)
                {
                    _engine.Step();
                    _engine.Render();
                }
            }

            ComposeFrame(snap, pixelScale, reduceMotion, timeSeconds);
        }

        // ------------------------------------------------------------------

        private void ComposeFrame(FireSnapshot snap, double px, bool reduceMotion, double timeSeconds)
        {
            float dpiScale = Dpi.Scale;
            px *= dpiScale;

            int panelW = _width;
            int panelH = _height;

            int originX = panelW / 2;
            int originY = (int)Math.Round(panelH * 0.72);

            double logW = PixelFireEngine.LogW * px;
            double logH = PixelFireEngine.LogH * px;
            double flameW = PixelFireEngine.FireW * px;
            double flameH = PixelFireEngine.FireH * px;
            double flameBaseY = PixelFireEngine.LogH * px * 0.5 - 4 * px;

            // 火焰左右摆动
            double jitter = 0;
            if (snap.Phase == FirePhase.Flame && !reduceMotion)
            {
                double amp;
                switch (snap.Tier)
                {
                    case FireTier.Hush: amp = 0; break;
                    case FireTier.Glow: amp = 0.5 * px / 3.5; break;
                    case FireTier.Crackle: amp = 1.0 * px / 3.5; break;
                    case FireTier.Roar: amp = 1.5 * px / 3.5; break;
                    default: amp = 2.0 * px / 3.5; break;
                }
                jitter = Math.Round(Math.Sin(timeSeconds * 2.2) * amp);
            }

            var data = _bitmap.LockBits(
                new Rectangle(0, 0, panelW, panelH),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppPArgb);
            try
            {
                int stride = data.Stride;
                var buffer = new byte[stride * panelH];
                Array.Clear(buffer, 0, buffer.Length);

                // ---- 柴堆 ----
                int logX = (int)Math.Round(originX - logW / 2);
                int logY = (int)Math.Round(originY - logH / 2);
                Blit(buffer, stride, panelW, panelH, logX, logY, (int)Math.Round(logW), (int)Math.Round(logH), _log, 1.0, null, 0);

                // ---- 外发光光晕（柔和径向暖色，叠加） ----
                if (_flameAlpha > 0.01)
                    RenderFireGlow(buffer, stride, panelW, panelH, snap, px, timeSeconds, originX, originY, flameBaseY, flameH);

                // ---- 火焰 ----
                if (_flameAlpha > 0.01)
                {
                    int flameX = (int)Math.Round(originX - flameW / 2 + jitter);
                    int flameY = (int)Math.Round(originY - (flameBaseY - px) - flameH);
                    BlitEngine(buffer, stride, panelW, panelH, flameX, flameY,
                        (int)Math.Round(flameW), (int)Math.Round(flameH), _flameAlpha);

                    FlameBaseY = (int)Math.Round(originY - (flameBaseY - px));
                    FlameTipY = flameY;
                }

                // ---- 柴堆余烬呼吸光（叠加在柴上） ----
                RenderEmberGlow(buffer, stride, panelW, panelH, logX, logY, (int)Math.Round(logW), (int)Math.Round(logH), snap, timeSeconds);

                // ---- 火星 ----
                RenderSparks(buffer, stride, panelW, panelH, snap, px, reduceMotion, timeSeconds, originX, originY, flameBaseY);

                // 前面全部画在本地缓冲上（比逐像素 LockBits 快），这里一次性拷进位图
                Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
            }
            finally
            {
                _bitmap.UnlockBits(data);
            }
        }

        private void RenderSparks(byte[] buffer, int stride, int panelW, int panelH,
            FireSnapshot snap, double px, bool reduceMotion, double timeSeconds,
            int originX, int originY, double flameBaseY)
        {
            const int sparkCount = 12;
            int active;
            switch (snap.Phase)
            {
                case FirePhase.Flame:
                    {
                        int baseCount;
                        switch (snap.Tier)
                        {
                            case FireTier.Hush: baseCount = 0; break;
                            case FireTier.Glow: baseCount = 1; break;
                            case FireTier.Crackle: baseCount = 3; break;
                            case FireTier.Roar: baseCount = 6; break;
                            default: baseCount = 10; break;
                        }
                        active = reduceMotion
                            ? Math.Max(0, baseCount / 2)
                            : Math.Min(sparkCount, baseCount + (int)(snap.SparkBurst * 2));
                        break;
                    }
                case FirePhase.Ember:
                    active = snap.EmberHeat > 0.55 ? 1 : 0;
                    break;
                default:
                    return;
            }
            if (active <= 0) return;

            double riseMax;
            switch (snap.Tier)
            {
                case FireTier.Hush: riseMax = 10 * px; break;
                case FireTier.Glow: riseMax = 14 * px; break;
                case FireTier.Crackle: riseMax = 20 * px; break;
                case FireTier.Roar: riseMax = 26 * px; break;
                default: riseMax = 32 * px; break;
            }

            var tintSources = ActiveSources(snap.ColorMix);
            int sparkW = Math.Max(1, (int)Math.Round(px));
            int sparkH = Math.Max(2, (int)Math.Round(px * 2));

            for (int i = 0; i < active; i++)
            {
                double seed = i * 1.7 + TierSeed(snap.Tier);
                double speed = 12.0 + snap.Intensity * 14.0;
                double t = timeSeconds * speed * 0.07 + seed;
                double rise = (t * 9) % Math.Max(1.0, riseMax);
                double sway = Math.Round(Math.Sin(t * 2.1 + seed) * px * 0.5);
                double spread = (i - active / 2) * px;

                // 场景坐标原点在面板 (originX, originY)，+y 向上
                double sceneX = sway + spread;
                double sceneY = flameBaseY + 4 + rise;
                int cx = (int)Math.Round(originX + sceneX);
                int cy = (int)Math.Round(originY - sceneY);

                double life = 1 - rise / Math.Max(1.0, riseMax);
                double alpha = Math.Max(0, life * (0.5 + snap.Intensity * 0.5));
                if (alpha <= 0.02) continue;

                double[] tint = null;
                if (tintSources.Length > 0)
                    tint = SourceFlameColors.Accent(tintSources[i % tintSources.Length]);

                // 火星尾迹：往上飞时尾部淡出（基于上升速度）
                double trailLen = Math.Max(2, speed * 0.08);
                double trailAlpha = alpha * 0.25;
                for (int tr = 1; tr <= 3; tr++)
                {
                    double fade = 1.0 - tr * 0.32;
                    int tx = cx;
                    int ty = cy + (int)(tr * trailLen * 0.4);
                    if (ty >= panelH) break;
                    if (tintSources.Length > 0)
                        tint = SourceFlameColors.Accent(tintSources[(i + tr) % tintSources.Length]);
                    Blit(buffer, stride, panelW, panelH,
                        tx - sparkW / 2, ty - sparkH / 2, sparkW, sparkH, _spark,
                        trailAlpha * fade, tint, 0.5);
                }

                Blit(buffer, stride, panelW, panelH,
                    cx - sparkW / 2, cy - sparkH / 2, sparkW, sparkH, _spark, alpha, tint, 0.65);
            }
        }

        private static double TierSeed(FireTier t)
        {
            switch (t)
            {
                case FireTier.Hush: return 4;
                case FireTier.Glow: return 4;
                case FireTier.Crackle: return 7;
                case FireTier.Roar: return 4;
                default: return 5;
            }
        }

        private static UsageSource[] ActiveSources(FlameColorMix mix)
        {
            if (mix == null || mix.IsClassic) return new UsageSource[0];
            var list = new System.Collections.Generic.List<UsageSource>();
            foreach (var s in UsageSources.All)
            {
                double w;
                mix.Weights.TryGetValue(s, out w);
                if (w > 0.02) list.Add(s);
            }
            return list.ToArray();
        }


        /// <summary>Outer radial glow around the fire (additive, before flame is drawn).</summary>
        private void RenderFireGlow(byte[] buf, int stride, int pW, int pH,
            FireSnapshot snap, double px, double timeSeconds,
            int originX, int originY, double flameBaseY, double flameH)
        {
            // Glow center: at the base of the flame, slightly above logs
            double cx = originX;
            double cy = originY - flameBaseY;

            // Radius: tighter, concentrate near fire base
            double baseRadius = flameH * 0.55 + 4 * px;
            double intensityBoost = 1.0 + snap.Intensity * 0.2;

            // Subtle breathing pulse
            double pulse = 0.9 + 0.1 * Math.Sin(timeSeconds * 1.3);

            double maxR = baseRadius * intensityBoost * pulse;

            // Glow color: warm amber derived from flame accent
            double ar = snap.FlameAccent != null ? snap.FlameAccent[0] : 0.95;
            double ag = snap.FlameAccent != null ? snap.FlameAccent[1] : 0.55;
            double ab = snap.FlameAccent != null ? snap.FlameAccent[2] : 0.20;

            // Sharp Gaussian falloff: concentrated near center
            int maxRadiusI = (int)Math.Ceiling(maxR);
            double maxR2 = maxR * maxR;
            for (int dy = -maxRadiusI; dy <= maxRadiusI; dy++)
            {
                for (int dx = -maxRadiusI; dx <= maxRadiusI; dx++)
                {
                    double dist2 = dx * dx + dy * dy;
                    if (dist2 > maxR2) continue;

                    // Sharper falloff (exp(-x²*5)) — drops off fast
                    double norm = Math.Sqrt(dist2) / maxR;
                    double falloff = Math.Exp(-norm * norm * 5.0);

                    // Much lower opacity — just a hint, not background
                    double glowAlpha = falloff * (0.06 + snap.Intensity * 0.06) * _flameAlpha;

                    int tx = (int)Math.Round(cx + dx);
                    int ty = (int)Math.Round(cy + dy);
                    if (tx < 0 || tx >= pW || ty < 0 || ty >= pH) continue;

                    // Premultiplied color components for additive blending
                    byte a = (byte)(Math.Min(1.0, glowAlpha) * 255);
                    byte r = (byte)(ar * 255);
                    byte g = (byte)(ag * 255);
                    byte b = (byte)(ab * 255);

                    int o = ty * stride + tx * 4;
                    // Additive: add glow on top of whatever is there (mostly transparent at this stage)
                    buf[o]     = (byte)Math.Min(255, buf[o]     + b * a / 255);
                    buf[o + 1] = (byte)Math.Min(255, buf[o + 1] + g * a / 255);
                    buf[o + 2] = (byte)Math.Min(255, buf[o + 2] + r * a / 255);
                    buf[o + 3] = (byte)Math.Min(255, buf[o + 3] + a);
                }
            }
        }

        /// <summary>Pulsing ember glow on top of the log sprite (additive).</summary>
        private void RenderEmberGlow(byte[] buf, int stride, int pW, int pH,
            int logX, int logY, int logW, int logH,
            FireSnapshot snap, double timeSeconds)
        {
            // Only glow when there's active burning
            double heat;
            if (snap.Phase == FirePhase.Flame) heat = Math.Max(snap.Intensity, snap.SparkBurst * 0.5);
            else if (snap.Phase == FirePhase.Ember) heat = snap.EmberHeat;
            else heat = 0;

            if (heat < 0.05) return;

            // Pulse: subtle breath synced with time
            double pulse = 0.8 + 0.2 * Math.Sin(timeSeconds * 2.1);
            double glowStrength = heat * pulse * 0.15;

            // Glow center: tighter to log tops
            double cx = logX + logW * 0.5;
            double cy = logY + logH * 0.8;
            double radius = logW * 0.35;

            double ar = snap.FlameAccent != null ? snap.FlameAccent[0] : 0.95;
            double ag = snap.FlameAccent != null ? snap.FlameAccent[1] : 0.55;
            double ab = snap.FlameAccent != null ? snap.FlameAccent[2] : 0.20;

            int rI = (int)Math.Ceiling(radius);
            double r2 = radius * radius;
            for (int dy = -rI; dy <= rI; dy++)
            {
                for (int dx = -rI; dx <= rI; dx++)
                {
                    double d2 = dx * dx + dy * dy;
                    if (d2 > r2) continue;
                    double norm = Math.Sqrt(d2) / radius;
                    double falloff = Math.Exp(-norm * norm * 1.8);
                    double a = falloff * glowStrength;
                    if (a < 0.02) continue;

                    int tx = (int)Math.Round(cx + dx);
                    int ty = (int)Math.Round(cy + dy);
                    if (tx < 0 || tx >= pW || ty < 0 || ty >= pH) continue;

                    byte ab_ = (byte)(a * 255);
                    byte r = (byte)(ar * 255);
                    byte g = (byte)(ag * 255);
                    byte b = (byte)(ab * 255);

                    int o = ty * stride + tx * 4;
                    buf[o]     = (byte)Math.Min(255, buf[o]     + b * ab_ / 255);
                    buf[o + 1] = (byte)Math.Min(255, buf[o + 1] + g * ab_ / 255);
                    buf[o + 2] = (byte)Math.Min(255, buf[o + 2] + r * ab_ / 255);
                    buf[o + 3] = (byte)Math.Min(255, buf[o + 3] + ab_);
                }
            }
        }

        /// <summary>把热场色缓冲按 px 放大贴进面板（最近邻）。</summary>
        private void BlitEngine(byte[] dst, int stride, int w, int h, int dx, int dy, int dw, int dh, double alphaMul)
        {
            var src = _engine.Bgra;
            int sw = _engine.Width;
            int sh = _engine.Height;
            double sx = (double)sw / dw;
            double sy = (double)sh / dh;

            for (int y = 0; y < dh; y++)
            {
                int ty = dy + y;
                if (ty < 0 || ty >= h) continue;
                int syy = (int)(y * sy);
                if (syy >= sh) syy = sh - 1;
                int srcRow = syy * sw * 4;

                for (int x = 0; x < dw; x++)
                {
                    int tx = dx + x;
                    if (tx < 0 || tx >= w) continue;
                    int sxx = (int)(x * sx);
                    if (sxx >= sw) sxx = sw - 1;

                    int so = srcRow + sxx * 4;
                    byte a = src[so + 3];
                    if (a == 0) continue;
                    byte b = src[so];
                    byte g = src[so + 1];
                    byte r = src[so + 2];

                    if (alphaMul < 0.999)
                    {
                        a = (byte)(a * alphaMul);
                        b = (byte)(b * alphaMul);
                        g = (byte)(g * alphaMul);
                        r = (byte)(r * alphaMul);
                    }

                    // 预乘 alpha（源已经是 Rgba 非预乘）
                    int o = ty * stride + tx * 4;
                    dst[o] = (byte)(b * a / 255);
                    dst[o + 1] = (byte)(g * a / 255);
                    dst[o + 2] = (byte)(r * a / 255);
                    dst[o + 3] = a;
                }
            }
        }

        private static void Blit(byte[] dst, int stride, int w, int h, int dx, int dy, int dw, int dh,
            Rgba[,] src, double alphaMul, double[] tint, double tintAmount)
        {
            int sh = src.GetLength(0);
            int sw = src.GetLength(1);
            if (dw <= 0 || dh <= 0) return;

            for (int y = 0; y < dh; y++)
            {
                int ty = dy + y;
                if (ty < 0 || ty >= h) continue;
                int syy = y * sh / dh;
                if (syy >= sh) syy = sh - 1;

                for (int x = 0; x < dw; x++)
                {
                    int tx = dx + x;
                    if (tx < 0 || tx >= w) continue;
                    int sxx = x * sw / dw;
                    if (sxx >= sw) sxx = sw - 1;

                    Rgba c = src[syy, sxx];
                    if (c.A == 0) continue;

                    double r = c.R, g = c.G, b = c.B, a = c.A * alphaMul;
                    if (tint != null && tintAmount > 0)
                    {
                        r = r + (tint[0] * 255 - r) * tintAmount;
                        g = g + (tint[1] * 255 - g) * tintAmount;
                        b = b + (tint[2] * 255 - b) * tintAmount;
                    }
                    int ai = (int)Math.Round(a);
                    if (ai <= 0) continue;
                    if (ai > 255) ai = 255;

                    int o = ty * stride + tx * 4;
                    dst[o] = (byte)((int)Math.Round(b) * ai / 255);
                    dst[o + 1] = (byte)((int)Math.Round(g) * ai / 255);
                    dst[o + 2] = (byte)((int)Math.Round(r) * ai / 255);
                    dst[o + 3] = (byte)ai;
                }
            }
        }

        public void Dispose()
        {
            if (_bitmap != null) { _bitmap.Dispose(); _bitmap = null; }
        }
    }

    /// <summary>进程级 DPI 缩放：像素画必须 1:1 才不糊。</summary>
    internal static class Dpi
    {
        private static float _scale = 1f;
        private static bool _init;

        public static void Initialize()
        {
            if (_init) return;
            _init = true;
            try
            {
                SetProcessDPIAware();
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                    _scale = g.DpiX / 96f;
            }
            catch (Exception) { _scale = 1f; }
            if (_scale < 1f) _scale = 1f;
        }

        public static float Scale { get { return _scale; } }

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
    }
}
