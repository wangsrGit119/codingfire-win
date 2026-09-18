//
//  TrayIconArt.cs — CodingFire for Windows
//
//  程序化生成托盘图标与窗口图标：一把像素小火堆，不依赖外部资源文件。
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CodingFire.Ui
{
    internal static class TrayIconArt
    {
        private static Icon _cached;

        public static Icon AppIcon()
        {
            if (_cached != null) return _cached;
            _cached = Build(32);
            return _cached;
        }

        /// <summary>生成指定边长的篝火图标。托盘用的是 16/32 两档，画 32 再让系统缩。</summary>
        public static Icon Build(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.None;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.Clear(Color.Transparent);

                int s = size;
                // 火焰：由下往上收窄的四层色阶
                var layers = new List<KeyValuePair<Color, double[]>>
                {
                    // 颜色, [底宽比例, 顶宽比例, 底部y比例, 顶部y比例]
                    new KeyValuePair<Color, double[]>(Color.FromArgb(255, 92, 16), new[] { 0.46, 0.16, 0.72, 0.16 }),
                    new KeyValuePair<Color, double[]>(Color.FromArgb(255, 158, 30), new[] { 0.32, 0.10, 0.70, 0.28 }),
                    new KeyValuePair<Color, double[]>(Color.FromArgb(255, 212, 60), new[] { 0.19, 0.05, 0.68, 0.44 }),
                    new KeyValuePair<Color, double[]>(Color.FromArgb(255, 246, 200), new[] { 0.08, 0.02, 0.66, 0.58 })
                };

                foreach (var layer in layers)
                {
                    double[] p = layer.Value;
                    int yTop = (int)Math.Round(p[3] * s);
                    int yBottom = (int)Math.Round(p[2] * s);
                    for (int y = yTop; y <= yBottom; y++)
                    {
                        double t = yBottom == yTop ? 0 : (double)(y - yTop) / (yBottom - yTop);
                        double halfW = (p[1] + (p[0] - p[1]) * t) * s;
                        int cx = s / 2;
                        int x0 = (int)Math.Round(cx - halfW);
                        int x1 = (int)Math.Round(cx + halfW);
                        for (int x = x0; x <= x1; x++)
                        {
                            if (x < 0 || y < 0 || x >= s || y >= s) continue;
                            bmp.SetPixel(x, y, layer.Key);
                        }
                    }
                }

                // 柴堆
                var logDark = Color.FromArgb(96, 54, 22);
                var logMid = Color.FromArgb(140, 86, 36);
                int ly0 = (int)Math.Round(0.72 * s);
                int ly1 = (int)Math.Round(0.86 * s);
                for (int y = ly0; y <= ly1; y++)
                {
                    int inset = y == ly0 ? (int)Math.Round(0.20 * s) : (int)Math.Round(0.12 * s);
                    var c = y <= ly0 + 1 ? logMid : logDark;
                    for (int x = inset; x < s - inset; x++)
                        if (x >= 0 && y < s) bmp.SetPixel(x, y, c);
                }
            }

            IntPtr h = bmp.GetHicon();
            bmp.Dispose();
            try
            {
                // 复制一份再销毁原句柄，避免 GDI 句柄泄漏
                using (var tmp = Icon.FromHandle(h))
                {
                    var clone = (Icon)tmp.Clone();
                    return clone;
                }
            }
            finally
            {
                DestroyIcon(h);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);
    }
}
