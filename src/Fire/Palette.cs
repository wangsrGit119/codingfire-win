//
//  Palette.cs — TinyFire for Windows
//
//  像素调色板 + 多来源火焰色带。数值逐条对应 macOS 版
//  PixelCampfireAtlas.swift 里的 PixelPalette 与 SourceFlameColors.swift。
//

using System;
using System.Collections.Generic;
using TinyFire.Core;

namespace TinyFire.Fire
{
    /// <summary>RGBA 四元组（与 Swift 版元组同序）。</summary>
    public struct Rgba
    {
        public byte R, G, B, A;
        public Rgba(byte r, byte g, byte b, byte a) { R = r; G = g; B = b; A = a; }
        public static readonly Rgba Clear = new Rgba(0, 0, 0, 0);
    }

    public static class PixelPalette
    {
        /// <summary>热量 0…32 → RGBA，索引 0 为全透明。</summary>
        public static readonly Rgba[] Fire = BuildFire();

        public static readonly Rgba LogDark = new Rgba(62, 34, 14, 255);
        public static readonly Rgba LogMid = new Rgba(110, 68, 28, 255);
        public static readonly Rgba LogLight = new Rgba(148, 98, 44, 255);
        public static readonly Rgba LogEnd = new Rgba(186, 148, 88, 255);
        public static readonly Rgba Ash = new Rgba(78, 74, 70, 255);
        public static readonly Rgba Coal = new Rgba(28, 24, 20, 255);
        public static readonly Rgba Ember = new Rgba(220, 48, 8, 255);
        public static readonly Rgba Spark = new Rgba(255, 236, 120, 255);

        private static Rgba[] BuildFire()
        {
            var t = new List<Rgba>(33);
            t.Add(Rgba.Clear);

            // 深红
            for (int i = 1; i <= 4; i++) t.Add(new Rgba(B(80 + i * 20), B(8 + i * 2), 0, 255));
            // 红
            for (int i = 0; i <= 5; i++) t.Add(new Rgba(B(180 + i * 10), B(20 + i * 8), 0, 255));
            // 橙
            for (int i = 0; i <= 6; i++) t.Add(new Rgba(255, B(70 + i * 14), B(i * 4), 255));
            // 黄
            for (int i = 0; i <= 6; i++) t.Add(new Rgba(255, B(170 + i * 8), B(20 + i * 12), 255));
            // 高温白黄
            for (int i = 0; i <= 5; i++) t.Add(new Rgba(255, B(230 + i * 4), B(140 + i * 18), 255));
            // 补足到 33 档
            while (t.Count < 33) t.Add(new Rgba(255, 252, 230, 255));
            return t.ToArray();
        }

        private static byte B(int v) { return v < 0 ? (byte)0 : (v > 255 ? (byte)255 : (byte)v); }
    }

    /// <summary>
    /// 每个工具的火焰主题色。首次访问时基于源枚举序用黄金分割在色相环上
    /// 均匀撒点，保证高饱和、高明度、视觉可区分。
    /// 不再手写 22 个颜色——加新源自动获得一个不撞车的颜色。
    /// </summary>
    public static class SourceFlameColors
    {
        /// <summary>黄金分割角（弧度），用于在色相环上均匀分布。</summary>
        private const double GoldenAngle = 2.3999632297286533;

        private static readonly Dictionary<UsageSource, double[]> BuiltIn = BuildBuiltIn();

        private static Dictionary<UsageSource, double[]> BuildBuiltIn()
        {
            var d = new Dictionary<UsageSource, double[]>();
            var sources = UsageSources.All;
            for (int i = 0; i < sources.Length; i++)
                d[sources[i]] = VibrantColor(i);
            return d;
        }

        /// <summary>
        /// 基于索引生成高饱和、高明度的随机感颜色。
        /// 用黄金分割角确保相邻索引的色相拉开；确定性输出（同 index 同色）。
        /// </summary>
        internal static double[] VibrantColor(int index)
        {
            // 偏移起始角，让 index 0 不落在红色区（红色太像经典火焰）
            double hue = ((index + 3) * GoldenAngle) % (2 * Math.PI); // +3 偏移 ~129°
            // 饱和在 0.72–0.95 之间波动（避免全一样亮）
            double sat = 0.72 + 0.23 * Math.Sin(hue * 3.7 + 1.3) * 0.5 + 0.5;
            // 明度在 0.62–0.88 之间
            double val = 0.62 + 0.26 * Math.Sin(hue * 2.9 + 0.7) * 0.5 + 0.5;
            return HsvToRgb(hue, sat, val);
        }

        internal static double[] HsvToRgb(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / (Math.PI / 3)) % 2 - 1));
            double m = v - c;
            double r1, g1, b1;
            int sector = (int)(h / (Math.PI / 3)) % 6;
            switch (sector)
            {
                case 0: r1 = c; g1 = x; b1 = 0; break;
                case 1: r1 = x; g1 = c; b1 = 0; break;
                case 2: r1 = 0; g1 = c; b1 = x; break;
                case 3: r1 = 0; g1 = x; b1 = c; break;
                case 4: r1 = x; g1 = 0; b1 = c; break;
                default: r1 = c; g1 = 0; b1 = x; break;
            }
            return new[] { Clamp01(r1 + m), Clamp01(g1 + m), Clamp01(b1 + m) };
        }

        private static Settings _settings;

        /// <summary>绑定设置对象后，用户改动会立即生效。</summary>
        public static void Attach(Settings settings) { _settings = settings; }

        public static double[] Accent(UsageSource source)
        {
            string key = source.Raw();
            double[] custom;
            if (_settings != null && _settings.SourceColors.TryGetValue(key, out custom) && custom.Length == 3)
                return custom;
            double[] builtin;
            if (BuiltIn.TryGetValue(source, out builtin)) return builtin;
            return new[] { 1.0, 0.45, 0.12 };
        }

        public static void SetAccent(UsageSource source, double r, double g, double b)
        {
            if (_settings == null) return;
            _settings.SourceColors[source.Raw()] = new[] { Clamp01(r), Clamp01(g), Clamp01(b) };
        }

        public static void ResetAll()
        {
            if (_settings == null) return;
            _settings.SourceColors.Clear();
        }

        public static bool IsCustom(UsageSource source)
        {
            return _settings != null && _settings.SourceColors.ContainsKey(source.Raw());
        }

        private static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
    }

    public static class FlamePaletteBuilder
    {
        /// <summary>经典 Doom 色阶（混色为空时保持原貌）。</summary>
        public static Rgba[] Classic { get { return PixelPalette.Fire; } }

        /// <summary>
        /// 保留火焰亮度曲线、源色全程可见的自定义色阶。
        ///
        /// 与旧版的关键区别：不再在 mid→hot 区段强制往橙色收敛，
        /// 源主题色从底部到火尖前都保持可辨识。只有最顶端 3 级（h≥30）
        /// 统一走向白热，保证多色混在一起仍然像「一把火」。
        /// </summary>
        public static Rgba[] Ramp(double[] accent)
        {
            var table = new List<Rgba>(33);
            table.Add(Rgba.Clear);
            double ar = accent[0], ag = accent[1], ab = accent[2];

            for (int h = 1; h <= 32; h++)
            {
                double u = h / 32.0;
                double r, g, b;
                if (u < 0.22)
                {
                    double k = u / 0.22;
                    double dark = 0.22 + k * 0.24;
                    r = ar * dark; g = ag * dark; b = ab * dark;
                }
                else if (u < 0.50)
                {
                    double k = (u - 0.22) / 0.28;
                    double lo = 0.46, hi = 0.78;
                    double v = lo + (hi - lo) * k;
                    r = ar * v; g = ag * v; b = ab * v;
                }
                else if (u < 0.78)
                {
                    double k = (u - 0.50) / 0.28;
                    double lo = 0.78, hi = 0.98;
                    double v = lo + (hi - lo) * k;
                    r = Math.Max(ar * v, ar * 0.35);
                    g = Math.Max(ag * v, ag * 0.35);
                    b = Math.Max(ab * v, ab * 0.35);
                }
                else if (u < 0.94)
                {
                    double k = (u - 0.78) / 0.16;
                    double srcR = Math.Min(1, ar * 0.95), srcG = Math.Min(1, ag * 0.95), srcB = Math.Min(1, ab * 0.95);
                    var white = new[] { 1.0, 0.98, 0.94 };
                    r = srcR + (white[0] - srcR) * k;
                    g = srcG + (white[1] - srcG) * k;
                    b = srcB + (white[2] - srcB) * k;
                }
                else
                {
                    double k = (u - 0.94) / 0.06;
                    var tip = new[] { 1.0, 0.97, 0.88 };
                    var wht = new[] { 1.0, 0.99, 0.96 };
                    r = tip[0] + (wht[0] - tip[0]) * k;
                    g = tip[1] + (wht[1] - tip[1]) * k;
                    b = tip[2] + (wht[2] - tip[2]) * k;
                }
                table.Add(ToByte(r, g, b));
            }
            return table.ToArray();
        }

        private static Rgba ToByte(double r, double g, double b)
        {
            return new Rgba(Clamp255(r * 255), Clamp255(g * 255), Clamp255(b * 255), 255);
        }

        private static byte Clamp255(double v)
        {
            int i = (int)Math.Round(v, MidpointRounding.AwayFromZero);
            return i < 0 ? (byte)0 : (i > 255 ? (byte)255 : (byte)i);
        }
    }
}
