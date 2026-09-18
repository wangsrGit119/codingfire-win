//
//  Program.cs — CodingFire for Windows
//
//  入口。除正常启动外还提供两个无界面模式，方便在没有人盯着屏幕时验证：
//    --dump [file]    扫描本地日志，把统计结果写成文本报告
//    --render [dir]   把各档火势渲染成 PNG，用来核对像素外观
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using CodingFire.Core;
using CodingFire.Data;
using CodingFire.Fire;
using CodingFire.Ui;

namespace CodingFire
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                WriteCrash(ex);
                return 1;
            }
        }

        /// <summary>无界面模式下没有地方弹窗，把异常落到 exe 同目录，方便排查。</summary>
        private static void WriteCrash(Exception ex)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "codingfire-crash.txt");
                File.WriteAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + ex, new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        private static int Run(string[] args)
        {
            bool dump = false, render = false;
            string target = null;
            foreach (string a in args)
            {
                if (string.Equals(a, "--dump", StringComparison.OrdinalIgnoreCase)) dump = true;
                else if (string.Equals(a, "--render", StringComparison.OrdinalIgnoreCase)) render = true;
                else if (!a.StartsWith("--", StringComparison.Ordinal)) target = a;
            }

            if (dump) return Dump(target);
            if (render) return Render(target);

            // 单实例：第二个进程直接退出，避免托盘出现两把火
            bool created;
            using (var mutex = new Mutex(true, "CodingFire.SingleInstance", out created))
            {
                if (!created) return 0;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                try
                {
                    using (var app = new CodingFireApp())
                    {
                        Application.Run(app);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("fatal: " + ex);
                    MessageBox.Show(ex.Message, "CodingFire", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }
            return 0;
        }

        // ------------------------------------------------------------------
        // --dump
        // ------------------------------------------------------------------

        private static int Dump(string target)
        {
            string path = string.IsNullOrEmpty(target)
                ? Path.Combine(Directory.GetCurrentDirectory(), "codingfire-dump.txt")
                : target;

            // 顺手容错：万一给的是目录，就写进该目录下的 codingfire-dump.txt，
            // 否则 File.WriteAllText 会甩出一个和用法无关的「访问被拒绝」。
            try
            {
                if (!string.IsNullOrEmpty(target) && Directory.Exists(target))
                    path = Path.Combine(target, "codingfire-dump.txt");
            }
            catch (Exception) { }

            var sb = new StringBuilder();
            try
            {
                AppPaths.DataDir.ToString();
                var settings = Settings.Load();
                L10n.Current = settings.Language;

                var store = new UsageStore();
                store.Open();

                var monitor = new UsageMonitor(store);
                monitor.Start();

                // 等首轮 baseline 扫描收敛（最多 60 秒）。
                // 源多了以后首轮要读 SQLite 库、递归若干日志树，20 秒不够。
                var deadline = DateTime.Now.AddSeconds(60);
                while (monitor.IsScanning && DateTime.Now < deadline) Thread.Sleep(100);
                Thread.Sleep(600); // 再给最后一批解析留点时间

                monitor.Stop();
                store.Flush();

                sb.AppendLine("CodingFire Windows — local usage report");
                sb.AppendLine("time      : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                sb.AppendLine("data dir  : " + AppPaths.DataDir);
                sb.AppendLine();
                sb.AppendLine("sources");
                sb.AppendLine("  " + Pad("source", NameCol) + Pad("state", 14) + Pad("today", 12) + "path");
                if (monitor.Statuses != null)
                {
                    foreach (var st in monitor.Statuses)
                    {
                        sb.AppendLine("  " + Pad(st.Source.DisplayName(), NameCol)
                                      + Pad(st.State.ToString(), 14)
                                      + Pad(L10n.Compact(st.TodayTokens), 12)
                                      + (st.Detail ?? ""));
                    }
                }

                sb.AppendLine();
                sb.AppendLine("today total : " + monitor.TodayTokens.ToString("N0", CultureInfo.InvariantCulture));
                sb.AppendLine("by source   :");
                if (monitor.TodayBySource != null)
                {
                    foreach (var src in UsageSources.All)
                    {
                        int v;
                        monitor.TodayBySource.TryGetValue(src, out v);
                        if (v > 0) sb.AppendLine("  " + Pad(src.DisplayName(), NameCol) + v.ToString("N0", CultureInfo.InvariantCulture));
                    }
                }

                var b = monitor.TodayBreakdown;
                sb.AppendLine("breakdown   : input=" + (b.Input ?? 0).ToString("N0", CultureInfo.InvariantCulture)
                              + " output=" + (b.Output ?? 0).ToString("N0", CultureInfo.InvariantCulture)
                              + " cacheRead=" + (b.CacheRead ?? 0).ToString("N0", CultureInfo.InvariantCulture)
                              + " cacheWrite=" + (b.CacheWrite ?? 0).ToString("N0", CultureInfo.InvariantCulture));

                int peakHour = -1, peakVal = 0;
                if (monitor.TodayHourly != null)
                    for (int i = 0; i < monitor.TodayHourly.Count; i++)
                        if (monitor.TodayHourly[i].Tokens > peakVal)
                        {
                            peakVal = monitor.TodayHourly[i].Tokens;
                            peakHour = monitor.TodayHourly[i].Hour;
                        }
                sb.AppendLine("peak hour   : " + (peakHour >= 0
                    ? peakHour.ToString("00", CultureInfo.InvariantCulture) + ":00 = " + peakVal.ToString("N0", CultureInfo.InvariantCulture)
                    : "—"));

                store.Close();
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR: " + ex);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return 1;
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return 0;
        }

        /// <summary>
        /// 报告里「数据源」一列的宽度。要放得下最长的显示名
        /// "WorkBuddy (INTL)"（16 字符）—— Pad() 超宽会直接截断，别调小。
        /// </summary>
        private const int NameCol = 18;

        private static string Pad(string s, int width)
        {
            if (s == null) s = "";
            return s.Length >= width ? s.Substring(0, width - 1) + " " : s.PadRight(width);
        }

        // ------------------------------------------------------------------
        // --render
        // ------------------------------------------------------------------

        private static int Render(string dir)
        {
            string outDir = string.IsNullOrEmpty(dir)
                ? Path.Combine(Directory.GetCurrentDirectory(), "preview")
                : dir;
            Directory.CreateDirectory(outDir);

            var fire = new FireStateMachine();
            var settings = new Settings { Size = FlameSize.Medium };
            SourceFlameColors.Attach(settings);

            var renderer = new CampfireRenderer();
            double px = FlameSize.Medium.PixelScale();
            int w = (int)Math.Ceiling(PixelFireEngine.FireW * px + 28 * Dpi.Scale);
            int h = (int)Math.Ceiling((PixelFireEngine.FireH + PixelFireEngine.LogH) * px + 36 * Dpi.Scale);
            renderer.Resize(w, h);

            // 每一档都推进若干帧，让热场长起来再截图
            var styles = new List<KeyValuePair<string, FireSnapshot>>();
            foreach (var style in FirePreviews.All)
                styles.Add(new KeyValuePair<string, FireSnapshot>(style.ToString().ToLowerInvariant(), style.Snapshot()));

            // 再多加两档「混合来源配色」用于核对多色火焰
            var mixed = FirePreviewStyle.Blaze.Snapshot();
            var weights = new Dictionary<UsageSource, double>
            {
                { UsageSource.ClaudeCode, 0.4 },
                { UsageSource.Codex, 0.25 },
                { UsageSource.Cursor, 0.2 },
                { UsageSource.Grok, 0.15 }
            };
            mixed.ColorMix = new FlameColorMix(weights);
            styles.Add(new KeyValuePair<string, FireSnapshot>("mixed_blaze", mixed));

            // 来源一多色带就糊成一片，所以有了「可见上限」。两张对照图：
            // 同一份权重，一张不设上限，一张砍到 5 个来源。
            var many = new Dictionary<UsageSource, double>();
            var pool = UsageSources.All;
            double totalMany = 0;
            for (int i = 0; i < 12 && i < pool.Length; i++)
            {
                double weight = 12 - i;
                many[pool[i]] = weight;
                totalMany += weight;
            }
            foreach (var src in new List<UsageSource>(many.Keys)) many[src] /= totalMany;

            var openBand = FirePreviewStyle.Blaze.Snapshot();
            openBand.ColorMix = new FlameColorMix(many);
            styles.Add(new KeyValuePair<string, FireSnapshot>("band12_open", openBand));

            var cappedBand = FirePreviewStyle.Blaze.Snapshot();
            cappedBand.ColorMix = new FlameColorMix(FlameColorMix.CapToTop(many, 5, 0.03));
            styles.Add(new KeyValuePair<string, FireSnapshot>("band12_capped", cappedBand));

            double t = 0;
            foreach (var kv in styles)
            {
                try
                {
                    renderer.ResetEngine();
                    for (int i = 0; i < 90; i++)
                    {
                        t += 1.0 / 12.0;
                        renderer.Render(kv.Value, px, false, t);
                    }
                    using (var flat = new Bitmap(renderer.Bitmap.Width, renderer.Bitmap.Height, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(flat))
                        {
                            g.Clear(Color.FromArgb(255, 26, 22, 20));
                            g.DrawImage(renderer.Bitmap, 0, 0);
                        }
                        string file = Path.Combine(outDir, "fire_" + kv.Key + ".png");
                        flat.Save(file, ImageFormat.Png);
                    }
                }
                catch (Exception ex)
                {
                    WriteCrash(new Exception("render style " + kv.Key + " failed", ex));
                    return 1;
                }
            }

            // 悬停卡片样张
            var model = new HoverModel
            {
                TodayTokens = 1284000,
                TokensPerSecond = 42.7,
                ShowLiveRate = true,
                UpdatedAt = DateTime.Now
            };
            model.Rows.Add(new HoverRow { Source = UsageSource.ClaudeCode, Tokens = 812000 });
            model.Rows.Add(new HoverRow { Source = UsageSource.Codex, Tokens = 344000 });
            model.Rows.Add(new HoverRow { Source = UsageSource.Cursor, Tokens = 128000, Estimated = true });
            using (var card = HoverCardPainter.Draw(model))
            using (var flat = new Bitmap(card.Width, card.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(flat))
                {
                    g.Clear(Color.FromArgb(255, 26, 22, 20));
                    g.DrawImage(card, 0, 0);
                }
                flat.Save(Path.Combine(outDir, "hover_card.png"), ImageFormat.Png);
            }

            using (var icon = TrayIconArt.Build(64))
            using (var bmp = icon.ToBitmap())
                bmp.Save(Path.Combine(outDir, "tray_icon.png"), ImageFormat.Png);

            renderer.Dispose();
            return 0;
        }
    }
}
