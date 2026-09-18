//
//  PixelFireEngine.cs — CodingFire for Windows
//
//  Doom 风格的像素热场：每帧把下一行的热量往上抬、按风力横向偏移、再按档位散热。
//  逻辑与 macOS 版 PixelFireEngine 逐行对应，输出从 SKMutableTexture 换成 BGRA 字节缓冲。
//

using System;
using CodingFire.Core;

namespace CodingFire.Fire
{
    internal sealed class PixelFireEngine
    {
        public const int FireW = 28;
        public const int FireH = 36;
        public const int LogW = 28;
        public const int LogH = 12;

        public readonly int Width;
        public readonly int Height;

        private readonly byte[] _heat;
        /// <summary>BGRA，Windows 32bpp 位图的内存布局。</summary>
        private readonly byte[] _bgra;
        private readonly Random _rng = new Random();

        /// <summary>0…1 —— 底部火源的宽度与热度。</summary>
        public double Intensity = 0.4;
        public FireTier Tier = FireTier.Crackle;
        public FirePhase Phase = FirePhase.Flame;
        public double EmberHeat = 0;
        /// <summary>用户选择的火焰主题色（归一化 RGB 0…1）。默认经典橙。</summary>
        public double[] FlameAccent = new double[] { 0.95, 0.55, 0.2 };
        /// <summary>用户改了火焰颜色就 +1，触发色阶重建。</summary>
        public int AccentEpoch = 0;

        private int _cachedAccentEpoch = -1;
        private Rgba[] _accentRamp;

        public PixelFireEngine(int width, int height)
        {
            Width = width;
            Height = height;
            _heat = new byte[width * height];
            _bgra = new byte[width * height * 4];
            RebuildColorCaches();
        }

        public byte[] Bgra { get { return _bgra; } }

        public void Reset()
        {
            Array.Clear(_heat, 0, _heat.Length);
        }

        // ------------------------------------------------------------------
        // 模拟
        // ------------------------------------------------------------------

        public void Step()
        {
            int w = Width;
            int h = Height;

            // 上升：每格从下方取热，带风偏移与散热
            for (int y = 0; y < h - 1; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int below = _heat[(y + 1) * w + x];
                    int wind = _rng.Next(3) - 1;
                    int dstX = x + wind;
                    if (dstX < 0) dstX = 0;
                    else if (dstX > w - 1) dstX = w - 1;

                    int cool;
                    switch (Tier)
                    {
                        case FireTier.Hush: cool = _rng.Next(2, 5); break;
                        case FireTier.Glow: cool = _rng.Next(1, 4); break;
                        case FireTier.Crackle: cool = _rng.Next(1, 4); break;
                        case FireTier.Roar: cool = _rng.Next(1, 3); break;
                        default: cool = _rng.Next(0, 3); break;
                    }
                    int next = below - cool;
                    _heat[y * w + dstX] = (byte)(next < 0 ? 0 : next);
                }
            }

            int bottom = (h - 1) * w;
            for (int x = 0; x < w; x++) _heat[bottom + x] = 0;

            switch (Phase)
            {
                case FirePhase.Unlit:
                case FirePhase.Out:
                    break;
                case FirePhase.Ember:
                    SeedEmbers(bottom);
                    break;
                default:
                    SeedFlame(bottom);
                    break;
            }

            ApplyHeightCap();
        }

        private void SeedFlame(int bottom)
        {
            int w = Width;
            int cx = w / 2;

            int half;
            switch (Tier)
            {
                case FireTier.Hush: half = 2; break;
                case FireTier.Glow: half = 3; break;
                case FireTier.Crackle: half = 5; break;
                case FireTier.Roar: half = 7; break;
                default: half = 10; break;
            }

            int basePeak;
            switch (Tier)
            {
                case FireTier.Hush: basePeak = 18; break;
                case FireTier.Glow: basePeak = 22; break;
                case FireTier.Crackle: basePeak = 26; break;
                case FireTier.Roar: basePeak = 30; break;
                default: basePeak = 32; break;
            }
            int peak = Math.Min(32, (int)(basePeak * (0.55 + Intensity * 0.55)));

            for (int dx = -half; dx <= half; dx++)
            {
                int x = cx + dx;
                if (x < 0 || x >= w) continue;
                bool edge = Math.Abs(dx) == half;
                int v = peak - Math.Abs(dx) * 2 - (edge ? 4 : 0);
                v += _rng.Next(5) - 2;
                // 旺火与大火偶尔窜一下
                if ((Tier == FireTier.Blaze || Tier == FireTier.Roar) && _rng.Next(9) == 0)
                    v = Math.Min(32, v + 6);
                _heat[bottom + x] = (byte)(v < 0 ? 0 : v);
            }

            // 第二行补一记，让火根更厚
            if (Height >= 2)
            {
                int row = (Height - 2) * w;
                if (half > 1)
                {
                    for (int dx = -(half - 1); dx <= (half - 1); dx++)
                    {
                        int x = cx + dx;
                        if (x < 0 || x >= w) continue;
                        int cur = _heat[row + x];
                        int boost = peak / 2 - Math.Abs(dx);
                        _heat[row + x] = (byte)(cur > boost ? cur : (boost < 0 ? 0 : boost));
                    }
                }
            }
        }

        private void SeedEmbers(int bottom)
        {
            int w = Width;
            int cx = w / 2;
            int n = 3 + (int)(EmberHeat * 4);
            for (int i = 0; i < n; i++)
            {
                int x = cx - n / 2 + i;
                if (x < 0 || x >= w) continue;
                int pulse = (int)(10 + EmberHeat * 12) + (_rng.Next(7) - 3);
                if (_rng.Next(3) == 0) _heat[bottom + x] = (byte)(pulse < 0 ? 0 : (pulse > 255 ? 255 : pulse));
            }
        }

        private void ApplyHeightCap()
        {
            int maxRows;
            switch (Phase)
            {
                case FirePhase.Unlit:
                case FirePhase.Out:
                    maxRows = 0;
                    break;
                case FirePhase.Ember:
                    maxRows = 4;
                    break;
                default:
                    switch (Tier)
                    {
                        case FireTier.Hush: maxRows = 8; break;
                        case FireTier.Glow: maxRows = 14; break;
                        case FireTier.Crackle: maxRows = 22; break;
                        case FireTier.Roar: maxRows = 30; break;
                        default: maxRows = Height; break;
                    }
                    break;
            }

            int cut = Height - maxRows;
            if (cut <= 0) return;
            int w = Width;
            for (int y = 0; y < cut; y++)
            {
                int dist = cut - y;
                for (int x = 0; x < w; x++)
                {
                    // 顶部不是硬切，而是渐隐
                    if (dist > 2) _heat[y * w + x] = 0;
                    else _heat[y * w + x] = (byte)(_heat[y * w + x] / (4 - dist));
                }
            }
        }

        // ------------------------------------------------------------------
        // 上色
        // ------------------------------------------------------------------

        public void RefreshPaletteIfNeeded()
        {
            if (_accentRamp == null || AccentEpoch != _cachedAccentEpoch)
                RebuildColorCaches();
        }

        public void RebuildColorCaches()
        {
            _cachedAccentEpoch = AccentEpoch;
            _accentRamp = FlamePaletteBuilder.Ramp(FlameAccent);
        }

        /// <summary>把热场着色写进 BGRA 缓冲。调用方负责拷贝到 Bitmap。</summary>
        public void Render()
        {
            RefreshPaletteIfNeeded();
            int w = Width;
            int count = w * Height;

            for (int i = 0; i < count; i++)
            {
                int h = _heat[i];
                if (h > 32) h = 32;
                int o = i * 4;
                if (h == 0)
                {
                    _bgra[o] = 0; _bgra[o + 1] = 0; _bgra[o + 2] = 0; _bgra[o + 3] = 0;
                    continue;
                }

                Rgba c = _accentRamp[h];
                _bgra[o] = c.B;
                _bgra[o + 1] = c.G;
                _bgra[o + 2] = c.R;
                _bgra[o + 3] = c.A;
            }
        }

        private static byte ClampByte(double v)
        {
            int i = (int)Math.Round(v, MidpointRounding.AwayFromZero);
            return i < 0 ? (byte)0 : (i > 255 ? (byte)255 : (byte)i);
        }
    }

    /// <summary>柴堆与火星的静态像素贴图。</summary>
    internal static class CampfireSprites
    {
        public static Rgba[,] Log()
        {
            var g = New(PixelFireEngine.LogW, PixelFireEngine.LogH);
            Stamp(g, 8, 4, 23, PixelPalette.Ash);
            Stamp(g, 9, 3, 24, PixelPalette.Coal);
            Stamp(g, 10, 5, 22, PixelPalette.Ash);
            Stamp(g, 11, 7, 20, PixelPalette.Coal);
            Put(g, 8, 9, PixelPalette.Ember);
            Put(g, 14, 9, PixelPalette.Ember);
            Put(g, 19, 9, PixelPalette.Ember);

            // 后一根柴（低一层）
            Stamp(g, 6, 2, 25, PixelPalette.LogDark);
            Stamp(g, 7, 2, 25, PixelPalette.LogMid);
            Put(g, 2, 6, PixelPalette.LogEnd); Put(g, 2, 7, PixelPalette.LogEnd);
            Put(g, 25, 6, PixelPalette.LogEnd); Put(g, 25, 7, PixelPalette.LogLight);

            // 前一根柴——顶面是平的，火焰就落在这上面
            Stamp(g, 4, 3, 24, PixelPalette.LogLight);
            Stamp(g, 5, 3, 24, PixelPalette.LogMid);
            Put(g, 3, 4, PixelPalette.LogEnd); Put(g, 3, 5, PixelPalette.LogEnd);
            Put(g, 24, 4, PixelPalette.LogEnd); Put(g, 24, 5, PixelPalette.LogLight);
            return g;
        }

        public static Rgba[,] Spark()
        {
            var g = New(3, 3);
            Put(g, 1, 1, PixelPalette.Spark);
            Put(g, 1, 0, PixelPalette.Spark);
            Put(g, 0, 1, new Rgba(255, 180, 40, 200));
            Put(g, 2, 1, new Rgba(255, 180, 40, 180));
            return g;
        }

        private static Rgba[,] New(int w, int h)
        {
            var g = new Rgba[h, w];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) g[y, x] = Rgba.Clear;
            return g;
        }

        private static void Put(Rgba[,] g, int x, int y, Rgba c)
        {
            if (y < 0 || y >= g.GetLength(0) || x < 0 || x >= g.GetLength(1)) return;
            g[y, x] = c;
        }

        private static void Stamp(Rgba[,] g, int row, int x0, int x1, Rgba c)
        {
            for (int x = x0; x <= x1; x++) Put(g, x, row, c);
        }
    }
}
