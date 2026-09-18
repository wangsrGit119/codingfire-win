//
//  Settings.cs — CodingFire for Windows
//
//  单文件 JSON 设置 + 本地路径约定。全部落在 %APPDATA%\CodingFire 下，不写注册表。
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CodingFire.Core
{
    public enum FlameSize { Small, Medium, Large }

    public static class FlameSizes
    {
        public static readonly FlameSize[] All = { FlameSize.Small, FlameSize.Medium, FlameSize.Large };

        public static string Raw(this FlameSize s)
        {
            switch (s)
            {
                case FlameSize.Small: return "small";
                case FlameSize.Medium: return "medium";
                default: return "large";
            }
        }

        public static FlameSize FromRaw(string raw)
        {
            switch (raw)
            {
                case "small": return FlameSize.Small;
                case "large": return FlameSize.Large;
                default: return FlameSize.Medium;
            }
        }

        public static string Label(this FlameSize s)
        {
            switch (s)
            {
                case FlameSize.Small: return L10n.T("size.small");
                case FlameSize.Large: return L10n.T("size.large");
                default: return L10n.T("size.medium");
            }
        }

        /// <summary>像素放大倍率——用户真正看到的尺寸。</summary>
        public static double PixelScale(this FlameSize s)
        {
            switch (s)
            {
                case FlameSize.Small: return 2.0;
                case FlameSize.Large: return 5.5;
                default: return 3.5;
            }
        }
    }

    public static class AppPaths
    {
        public static string DataDir
        {
            get
            {
                // 允许用环境变量指定数据目录：便携模式 / 自动化验证时不污染真实用户数据
                string overridden = null;
                try { overridden = Environment.GetEnvironmentVariable("CODINGFIRE_DATA_DIR"); }
                catch (Exception) { }
                if (!string.IsNullOrEmpty(overridden))
                    overridden = overridden.Trim();

                string dir = string.IsNullOrEmpty(overridden)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodingFire")
                    : Path.GetFullPath(overridden);

                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string SettingsFile { get { return Path.Combine(DataDir, "settings.json"); } }
        public static string UsageFile { get { return Path.Combine(DataDir, "usage.ndjson"); } }
        public static string CursorsFile { get { return Path.Combine(DataDir, "cursors.json"); } }
        public static string MetaFile { get { return Path.Combine(DataDir, "meta.json"); } }
        public static string LogFile { get { return Path.Combine(DataDir, "codingfire.log"); } }

        public static string Home
        {
            get
            {
                // SpecialFolder.UserProfile 是 .NET 4.0 才加入的枚举值，
                // 3.5 上用 USERPROFILE 环境变量，再兜底到「我的文档」的上一级。
                string h = Environment.GetEnvironmentVariable("USERPROFILE");
                if (!string.IsNullOrEmpty(h)) return h;

                string personal = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                if (!string.IsNullOrEmpty(personal))
                {
                    string parent = Path.GetDirectoryName(personal);
                    if (!string.IsNullOrEmpty(parent)) return parent;
                }
                return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
        }

        /// <summary>把绝对路径折叠成 ~/… 显示用短路径。</summary>
        public static string Shorten(string path)
        {
            if (string.IsNullOrEmpty(path)) return "—";
            string home = Home;
            if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
                return "~" + path.Substring(home.Length);
            return path;
        }

        /// <summary>
        /// 清理上次被强杀（或断电）时残留的 .tmp 文件。
        /// 正常退出不会留下它们；留着也不会被读取，只是碍眼。
        /// </summary>
        public static void CleanupStaleTempFiles()
        {
            try
            {
                foreach (string f in Directory.GetFiles(DataDir, "*.tmp"))
                {
                    try { File.Delete(f); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 原子替换写入：先写 .tmp，再整体替换目标文件。
        /// 避免读方（或崩溃/被杀）看到半截文件，也避免出现「先删旧文件再写」的空窗期。
        /// </summary>
        public static void WriteAtomicFile(string path, string content)
        {
            string tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Warn("write failed: " + path + " — " + ex.Message);
                return;
            }

            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null, true);
                else File.Move(tmp, path);
            }
            catch (Exception)
            {
                // 个别文件系统/杀软句柄下 Replace 会失败，退回「删除 + 改名」
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
                catch (Exception ex2)
                {
                    Log.Warn("atomic replace failed: " + path + " — " + ex2.Message);
                    try { if (File.Exists(tmp)) File.Delete(tmp); }
                    catch (Exception) { }
                }
            }
        }
    }

    public sealed class Settings
    {
        public FlameSize Size = FlameSize.Medium;
        public bool HasPosition;
        public int PanelX;
        public int PanelY;
        public AppLanguage Language = AppLanguage.System;
        public bool FlameVisible = true;
        public bool AnimationPaused;
        public bool ShowLiveRate = true;
        /// <summary>火焰主题色（归一化 RGB 0…1）。默认经典橙。</summary>
        public double[] FlameColor = new double[] { 1.0, 0.45, 0.12 };
        public bool AudioEnabled;
        public double Volume = 0.5;
        /// <summary>source rawValue -> [r,g,b] (0…1)</summary>
        public readonly Dictionary<string, double[]> SourceColors = new Dictionary<string, double[]>(StringComparer.Ordinal);

        private static readonly object Gate = new object();

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                {
                    var root = JObj.Of(Json.Parse(File.ReadAllText(AppPaths.SettingsFile, Encoding.UTF8)));
                    s.Size = FlameSizes.FromRaw(root.Str("size"));
                    s.HasPosition = (root.Bool("hasPosition") ?? false) && root.Has("x") && root.Has("y");
                    s.PanelX = (int)(root.Long("x") ?? 0);
                    s.PanelY = (int)(root.Long("y") ?? 0);
                    s.Language = FromLangRaw(root.Str("language"));
                    s.FlameVisible = root.Bool("visible") ?? true;
                    s.AnimationPaused = root.Bool("paused") ?? false;
                    s.ShowLiveRate = root.Bool("showLiveRate") ?? true;
                    // Flame color (stored as JSON array)
                    var fc = root.Arr("flameColor");
                    if (fc != null && fc.Count >= 3)
                    {
                        var fcr = new double[3];
                        bool ok = true;
                        for (int i = 0; i < 3; i++)
                        {
                            var n = fc.RawAt(i);
                            double d;
                            if (n is double) d = (double)n;
                            else { ok = false; break; }
                            fcr[i] = Math.Max(0, Math.Min(1, d));
                        }
                        if (ok) s.FlameColor = fcr;
                    }
                    s.AudioEnabled = root.Bool("audio") ?? false;
                    s.Volume = root.Num("volume") ?? 0.5;

                    var colors = root.Obj("colors");
                    if (colors != null)
                    {
                        foreach (var src in UsageSources.All)
                        {
                            var arr = colors.Arr(src.Raw());
                            if (arr.Count == 3)
                            {
                                var c = new double[3];
                                bool ok = true;
                                for (int i = 0; i < 3; i++)
                                {
                                    var n = arr.RawAt(i);
                                    double d;
                                    if (n is double) d = (double)n;
                                    else { ok = false; break; }
                                    c[i] = Math.Max(0, Math.Min(1, d));
                                }
                                if (ok) s.SourceColors[src.Raw()] = c;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("settings load failed: " + ex.Message);
            }
            return s;
        }

        public void Save()
        {
            lock (Gate)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.Append("{\n");
                    sb.Append("  \"size\": ").Append(Quote(Size.Raw())).Append(",\n");
                    sb.Append("  \"hasPosition\": ").Append(HasPosition ? "true" : "false").Append(",\n");
                    sb.Append("  \"x\": ").Append(PanelX.ToString(CultureInfo.InvariantCulture)).Append(",\n");
                    sb.Append("  \"y\": ").Append(PanelY.ToString(CultureInfo.InvariantCulture)).Append(",\n");
                    sb.Append("  \"language\": ").Append(Quote(LangRaw(Language))).Append(",\n");
                    sb.Append("  \"visible\": ").Append(FlameVisible ? "true" : "false").Append(",\n");
                    sb.Append("  \"paused\": ").Append(AnimationPaused ? "true" : "false").Append(",\n");
                    sb.Append("  \"showLiveRate\": ").Append(ShowLiveRate ? "true" : "false").Append(",\n");
                    sb.Append("  \"flameColor\": [")
                      .Append(FlameColor[0].ToString("0.####", CultureInfo.InvariantCulture)).Append(", ")
                      .Append(FlameColor[1].ToString("0.####", CultureInfo.InvariantCulture)).Append(", ")
                      .Append(FlameColor[2].ToString("0.####", CultureInfo.InvariantCulture)).Append("],\n");
                    sb.Append("  \"audio\": ").Append(AudioEnabled ? "true" : "false").Append(",\n");
                    sb.Append("  \"volume\": ").Append(Volume.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                    sb.Append("  \"colors\": {");
                    bool first = true;
                    foreach (var kv in SourceColors)
                    {
                        if (!first) sb.Append(",");
                        first = false;
                        sb.Append("\n    ").Append(Quote(kv.Key)).Append(": [")
                          .Append(kv.Value[0].ToString("0.####", CultureInfo.InvariantCulture)).Append(", ")
                          .Append(kv.Value[1].ToString("0.####", CultureInfo.InvariantCulture)).Append(", ")
                          .Append(kv.Value[2].ToString("0.####", CultureInfo.InvariantCulture)).Append("]");
                    }
                    if (!first) sb.Append("\n  ");
                    sb.Append("}\n");
                    sb.Append("}\n");

                    AppPaths.WriteAtomicFile(AppPaths.SettingsFile, sb.ToString());
                }
                catch (Exception ex)
                {
                    Log.Warn("settings save failed: " + ex.Message);
                }
            }
        }

        public UsageSource[] SourcesWithCustomColor
        {
            get
            {
                var list = new List<UsageSource>();
                foreach (var s in UsageSources.All) if (SourceColors.ContainsKey(s.Raw())) list.Add(s);
                return list.ToArray();
            }
        }

        private static string LangRaw(AppLanguage l)
        {
            switch (l)
            {
                case AppLanguage.English: return "en";
                case AppLanguage.ChineseSimplified: return "zh-Hans";
                case AppLanguage.Japanese: return "ja";
                case AppLanguage.Korean: return "ko";
                default: return "system";
            }
        }

        private static AppLanguage FromLangRaw(string raw)
        {
            switch (raw)
            {
                case "en": return AppLanguage.English;
                case "zh-Hans": return AppLanguage.ChineseSimplified;
                case "ja": return AppLanguage.Japanese;
                case "ko": return AppLanguage.Korean;
                default: return AppLanguage.System;
            }
        }

        internal static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append("\"");
            return sb.ToString();
        }
    }

    /// <summary>轻量文件日志，出问题时方便排查（不记录任何 prompt / 代码内容）。</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static bool _disabled;

        public static void Warn(string message) { Write("WARN", message); }
        public static void Info(string message) { Write("INFO", message); }

        private static void Write(string level, string message)
        {
            if (_disabled) return;
            lock (Gate)
            {
                try
                {
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                                  + " [" + level + "] " + message + Environment.NewLine;
                    File.AppendAllText(AppPaths.LogFile, line, new UTF8Encoding(false));
                }
                catch (Exception)
                {
                    _disabled = true; // 磁盘不可写时静默降级，不要反复抛
                }
            }
        }
    }
}
