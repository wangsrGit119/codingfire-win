//
//  Adapters.cs — CodingFire for Windows
//
//  五种「本地日志型」数据源的解析器（Claude Code / Codex / Grok / Pi / Amp）。
//  每一项都对应 macOS 版 Data\*LogAdapter.swift，口径保持一致：
//   - 只统计真实计费的 token，缓存读写单独拆分
//   - 累计字段转增量、reasoning 不重复计入
//   - 日志路径改为 Windows 约定，并支持各家官方环境变量覆盖
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CodingFire.Core;

namespace CodingFire.Data
{
    internal static class PathUtil
    {
        public static string Home { get { return AppPaths.Home; } }

        public static string Env(string name)
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(name);
                return string.IsNullOrEmpty(v) ? null : v.Trim();
            }
            catch (Exception) { return null; }
        }

        public static string ExpandTilde(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            if (p.StartsWith("~", StringComparison.Ordinal))
                return Path.Combine(Home, p.TrimStart('~').TrimStart('\\', '/'));
            return p;
        }

        /// <summary>
        /// Path.Combine 的可变参数版本。.NET 4.0 才有多参数重载，
        /// 这里自己两两折叠，以便编译出可在 .NET 3.5 上运行的产物。
        /// </summary>
        public static string Combine(params string[] parts)
        {
            if (parts == null || parts.Length == 0) return "";
            string acc = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                acc = string.IsNullOrEmpty(acc) ? parts[i] : Path.Combine(acc, parts[i]);
            }
            return acc ?? "";
        }

        /// <summary>稳定哈希，用于没有显式 id 的行（Swift 版用 hashValue，进程间不稳定）。</summary>
        public static string StableHash(string s)
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            uint h = offset;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= prime;
            }
            return h.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>日志适配器公共实现。</summary>
    internal abstract class LogAdapter
    {
        public abstract UsageSource Source { get; }
        protected abstract string PrimaryRoot { get; }

        /// <summary>供 UI 显示的根目录（可能不存在）。</summary>
        public string RootForDisplay { get { return PrimaryRoot; } }

        /// <summary>返回连接状态与详情文本，语义对齐 macOS 版 connectionState()。</summary>
        public virtual SourceConnectionState CheckConnection(out string detail)
        {
            string root = PrimaryRoot;
            detail = AppPaths.Shorten(root);
            if (string.IsNullOrEmpty(root)) { detail = "—"; return SourceConnectionState.NotFound; }
            if (!Directory.Exists(root)) return SourceConnectionState.NotFound;
            try
            {
                // 探测可读性，等价于 contentsOfDirectory。
                // 用 GetFileSystemEntries 而不是 Enumerate…：后者是 .NET 4.0 才有的 API，
                // 而本程序要能在只有 .NET 3.5（Win7 SP1 自带）的机器上直接跑。
                Directory.GetFileSystemEntries(root);
                return SourceConnectionState.Ok;
            }
            catch (UnauthorizedAccessException) { return SourceConnectionState.NoPermission; }
            catch (Exception) { return SourceConnectionState.ReadError; }
        }

        /// <summary>列出 mtime >= modifiedSince 的日志文件。</summary>
        public abstract List<string> DiscoverLogFiles(DateTime modifiedSince);

        /// <summary>解析一行（或无行概念的整个文件，见 Amp）。</summary>
        public virtual UsageEvent ParseLine(string line, string filePath) { return null; }

        /// <summary>
        /// 整文件型数据源（Amp / OpenCode / Cline 这类「一个文件里装着若干条消息」的格式）。
        /// 返回 null 表示该源走 ParseLine 行式解析。
        /// </summary>
        public virtual List<UsageEvent> ParseThread(string filePath) { return null; }

        /// <summary>
        /// 数据不在「日志文件」里、而在 SQLite 库里的源（ZCode / OpenCode / WorkBuddy 的
        /// session_usage 回退…）走这里，每轮扫描调用一次。返回 null 表示该源没有库。
        /// </summary>
        public virtual List<UsageEvent> ParseDatabase() { return null; }

        /// <summary>
        /// 累计型：日志里的同一条消息会随流式输出不断变大（OpenCode / Gemini / Droid 等）。
        /// 这类源必须由 UsageMonitor 记录「见过的最大值」，只把差额当增量入库。
        /// </summary>
        public virtual bool IsCumulative { get { return false; } }

        // ------------------------------------------------------------------
        // token 口径工具
        //   juejin-usage 的 TokenTotals 把 reasoning 单列，且 total 是五项相加。
        //   本程序内部只保留 input/output/cacheRead/cacheWrite 四项（与 macOS 版一致），
        //   reasoning 归入 output —— 与 macOS 版把它们都算进「计费总量」的结果相同。
        // ------------------------------------------------------------------

        /// <summary>容错取非负整数（null / NaN / 负数 → 0）。</summary>
        protected static int NonNeg(object v)
        {
            double d;
            if (v == null) return 0;
            if (v is double) d = (double)v;
            else if (v is int) return ((int)v) < 0 ? 0 : (int)v;
            else if (v is long) { long l = (long)v; return l < 0 ? 0 : (int)Math.Min(l, int.MaxValue); }
            else if (v is bool) return 0;
            else
            {
                string s = v as string;
                if (s == null || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return 0;
            }
            if (double.IsNaN(d) || double.IsInfinity(d) || d < 0) return 0;
            return d >= int.MaxValue ? int.MaxValue : (int)Math.Floor(d);
        }

        /// <summary>按 id 前缀扫描文件里的最大 token 总量等用途的字符串拼接。</summary>
        protected static string JoinKey(params string[] parts)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(parts[i] ?? "");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // 时间戳
        // ------------------------------------------------------------------

        protected static DateTime? ParseIso8601(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            // 兼容 "2026-09-15T10:00:00.123Z" / "+00:00" / 无时区
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out dto))
                return dto.LocalDateTime;

            DateTime dt;
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out dt))
                return dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
            return null;
        }

        protected static DateTime? ParseEpochAny(double v)
        {
            if (v <= 0) return null;
            // > 1e12 说明是毫秒
            var utc = v > 1e12 ? Epoch.AddMilliseconds(v) : Epoch.AddSeconds(v);
            return utc.ToLocalTime();
        }

        protected static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // ------------------------------------------------------------------
        // 文件遍历
        // ------------------------------------------------------------------

        /// <summary>递归枚举，遇到无权限目录跳过而不是整体抛异常。</summary>
        protected static IEnumerable<string> Walk(string root, string extension)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(dir); }
                catch (Exception) { continue; }

                for (int i = 0; i < entries.Length; i++)
                {
                    string p = entries[i];
                    string name = Path.GetFileName(p);
                    if (name.StartsWith(".", StringComparison.Ordinal)) continue;

                    bool isDir;
                    try { isDir = (File.GetAttributes(p) & FileAttributes.Directory) == FileAttributes.Directory; }
                    catch (Exception) { continue; }

                    if (isDir) { stack.Push(p); continue; }
                    if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) yield return p;
                }
            }
        }

        protected static bool ModifiedSince(string path, DateTime since)
        {
            try { return File.GetLastWriteTime(path) >= since; }
            catch (Exception) { return false; }
        }

        /// <summary>string.IsNullOrWhiteSpace 是 .NET 4.0 才有的，这里自带一份。</summary>
        protected static bool Blank(string s)
        {
            return s == null || s.Trim().Length == 0;
        }
    }

    // ======================================================================
    // Claude Code — ~/.claude/projects/**/*.jsonl 中 type == "assistant" 的 message.usage
    // ======================================================================
    internal sealed class ClaudeCodeAdapter : LogAdapter
    {
        public override UsageSource Source { get { return UsageSource.ClaudeCode; } }

        /// <summary>
        /// 同一条 assistant 消息会随流式输出反复出现，每次的 usage 都是「到目前为止」的累计值。
        /// 由 UsageMonitor 记录见过的最大总量，只把差额算作新增。
        /// </summary>
        public override bool IsCumulative { get { return true; } }

        protected override string PrimaryRoot
        {
            get
            {
                string cfg = PathUtil.Env("CLAUDE_CONFIG_DIR");
                if (cfg != null) return Path.Combine(PathUtil.ExpandTilde(cfg), "projects");
                return PathUtil.Combine(PathUtil.Home, ".claude", "projects");
            }
        }

        public override List<string> DiscoverLogFiles(DateTime modifiedSince)
        {
            var list = new List<string>();
            string root = PrimaryRoot;
            if (!Directory.Exists(root)) return list;
            foreach (string f in Walk(root, ".jsonl"))
                if (ModifiedSince(f, modifiedSince)) list.Add(f);
            return list;
        }

        public override UsageEvent ParseLine(string line, string filePath)
        {
            if (Blank(line)) return null;
            var obj = Json.Parse(line.Trim()) as JObj;
            if (obj == null || obj.Str("type") != "assistant") return null;

            var msg = obj.Obj("message");
            var usage = msg.Obj("usage");
            if (!usage.Has("input_tokens") && !usage.Has("output_tokens")
                && !usage.Has("cache_read_input_tokens") && !usage.Has("cache_creation_input_tokens"))
                return null;

            int? input = usage.Int("input_tokens");
            int? output = usage.Int("output_tokens");
            int? cacheWrite = usage.Int("cache_creation_input_tokens");
            int? cacheRead = usage.Int("cache_read_input_tokens");

            // Anthropic：input / cache_read / cache_creation 是并列的三个桶
            int total = (input ?? 0) + (output ?? 0) + (cacheWrite ?? 0) + (cacheRead ?? 0);
            if (total <= 0) return null;

            string msgId = Trimmed(msg.Str("id"));
            string uuid = Trimmed(obj.Str("uuid"));
            string id;
            if (msgId != null) id = "claude:" + msgId;
            else if (uuid != null) id = "claude-uuid:" + uuid;
            else id = "claude:" + Path.GetFileName(filePath) + ":" + PathUtil.StableHash(line);

            return new UsageEvent
            {
                Id = id,
                Source = UsageSource.ClaudeCode,
                Timestamp = ParseIso8601(obj.Str("timestamp")) ?? DateTime.Now,
                Tokens = total,
                Breakdown = new UsageBreakdown(input, output, cacheRead, cacheWrite),
                FilePath = filePath
            };
        }

        private static string Trimmed(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }
    }

    // ======================================================================
    // Codex — ~/.codex/sessions/**/*.jsonl 中 type == "token_usage_record"
    // ======================================================================
    internal sealed class CodexAdapter : LogAdapter
    {
        public override UsageSource Source { get { return UsageSource.Codex; } }

        protected override string PrimaryRoot
        {
            get
            {
                string home = PathUtil.Env("CODEX_HOME");
                if (home != null) return Path.Combine(PathUtil.ExpandTilde(home), "sessions");
                return PathUtil.Combine(PathUtil.Home, ".codex", "sessions");
            }
        }

        public override List<string> DiscoverLogFiles(DateTime modifiedSince)
        {
            var list = new List<string>();
            string root = PrimaryRoot;
            if (!Directory.Exists(root)) return list;
            foreach (string f in Walk(root, ".jsonl"))
                if (ModifiedSince(f, modifiedSince)) list.Add(f);
            return list;
        }

        public override UsageEvent ParseLine(string line, string filePath)
        {
            if (Blank(line)) return null;
            var obj = Json.Parse(line.Trim()) as JObj;
            if (obj == null || obj.Str("type") != "token_usage_record") return null;

            var payload = obj.Obj("payload");
            var usage = payload.Obj("usage");

            int? input = usage.Int("input_tokens");
            int? output = usage.Int("output_tokens");
            // reasoning_output_tokens 是 output 的子集，不再重复累加
            int? cacheRead = usage.Int("cached_input_tokens");
            int? cacheWrite = usage.Int("cache_write_input_tokens");

            // OpenAI 口径：input_tokens 已含缓存命中；total ≈ input + output
            int total;
            int? listed = usage.Int("total_tokens");
            if (listed.HasValue && listed.Value > 0) total = listed.Value;
            else total = (input ?? 0) + (output ?? 0);
            if (total <= 0) return null;

            string responseId = payload.Str("response_id");
            string sessionId = payload.Str("session_id") ?? "unknown";
            int? ordinal = obj.Int("ordinal");
            string id;
            if (!string.IsNullOrEmpty(responseId)) id = "codex:" + responseId;
            else if (ordinal.HasValue) id = "codex:" + sessionId + ":" + ordinal.Value;
            else id = "codex:" + Path.GetFileName(filePath) + ":" + PathUtil.StableHash(line);

            // 统计口径：缓存单独展示，input 尽量显示为「非缓存」
            int? nonCacheInput = input;
            if (input.HasValue && cacheRead.HasValue) nonCacheInput = Math.Max(0, input.Value - cacheRead.Value);

            return new UsageEvent
            {
                Id = id,
                Source = UsageSource.Codex,
                Timestamp = ParseIso8601(obj.Str("timestamp")) ?? DateTime.Now,
                Tokens = total,
                Breakdown = new UsageBreakdown(nonCacheInput, output, cacheRead, cacheWrite),
                FilePath = filePath
            };
        }
    }

    // ======================================================================
    // Grok — ~/.grok/sessions/**/updates.jsonl (turn_completed)
    //        + 旧版 ~/.grok/logs/unified.jsonl (shell.turn.inference_done)
    // ======================================================================
    internal sealed class GrokAdapter : LogAdapter
    {
        public override UsageSource Source { get { return UsageSource.Grok; } }

        private string HomeRoot
        {
            get
            {
                string env = PathUtil.Env("GROK_HOME");
                if (env != null) return PathUtil.ExpandTilde(env);
                return Path.Combine(PathUtil.Home, ".grok");
            }
        }

        protected override string PrimaryRoot { get { return HomeRoot; } }

        private string SessionsRoot { get { return Path.Combine(HomeRoot, "sessions"); } }
        private string UnifiedLog { get { return PathUtil.Combine(HomeRoot, "logs", "unified.jsonl"); } }

        public override List<string> DiscoverLogFiles(DateTime modifiedSince)
        {
            var files = new List<string>();
            string sessions = SessionsRoot;
            if (Directory.Exists(sessions))
            {
                foreach (string f in Walk(sessions, ".jsonl"))
                {
                    if (!string.Equals(Path.GetFileName(f), "updates.jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ModifiedSince(f, modifiedSince)) files.Add(f);
                }
            }
            if (File.Exists(UnifiedLog)) files.Add(UnifiedLog); // 旧格式体量小，每次全读，靠 id 去重
            return files;
        }

        public override UsageEvent ParseLine(string line, string filePath)
        {
            if (Blank(line)) return null;
            string raw = line.Trim();
            var obj = Json.Parse(raw) as JObj;
            if (obj == null) return null;

            if (obj.Str("sessionUpdate") == "turn_completed") return ParseTurnCompleted(obj, filePath, raw);
            if (obj.Str("msg") == "shell.turn.inference_done") return ParseUnified(obj, filePath, raw);
            return null;
        }

        private UsageEvent ParseTurnCompleted(JObj obj, string filePath, string raw)
        {
            var usage = obj.Has("usage") ? obj.Obj("usage") : obj;
            int inputTotal = usage.Int("inputTokens") ?? 0;
            int output = usage.Int("outputTokens") ?? 0;
            int cacheRead = usage.Int("cachedReadTokens") ?? 0;
            int cacheWrite = usage.Int("cacheCreationTokens") ?? 0;

            int total;
            int? listed = usage.Int("totalTokens");
            if (listed.HasValue && listed.Value > 0) total = listed.Value;
            else total = inputTotal + output;
            if (total <= 0) return null;

            int nonCacheInput = Math.Max(0, inputTotal - cacheRead);
            string turnId = obj.Str("turnId") ?? obj.Str("promptId")
                            ?? (Path.GetFileName(filePath) + ":" + PathUtil.StableHash(raw));
            DateTime ts = ParseIso8601(obj.Str("timestamp")) ?? ParseIso8601(obj.Str("ts")) ?? DateTime.Now;

            return new UsageEvent
            {
                Id = "grok:turn:" + turnId,
                Source = UsageSource.Grok,
                Timestamp = ts,
                Tokens = total,
                Breakdown = new UsageBreakdown(
                    nonCacheInput,
                    output,
                    cacheRead > 0 ? (int?)cacheRead : null,
                    cacheWrite > 0 ? (int?)cacheWrite : null),
                FilePath = filePath
            };
        }

        private UsageEvent ParseUnified(JObj obj, string filePath, string raw)
        {
            var ctx = obj.Obj("ctx");
            int promptTotal = Math.Max(0, ctx.Int("prompt_tokens") ?? 0);
            int cached = Math.Min(promptTotal, Math.Max(0, ctx.Int("cached_prompt_tokens") ?? 0));
            int output = Math.Max(0, ctx.Int("completion_tokens") ?? 0);
            int total = promptTotal + output;
            if (total <= 0) return null;

            string sid = obj.Str("sid") ?? "unknown";
            DateTime ts = ParseIso8601(obj.Str("ts")) ?? DateTime.Now;

            return new UsageEvent
            {
                Id = "grok:unified:" + sid + ":" + ts.ToUniversalTime().Subtract(Epoch).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + ":" + total,
                Source = UsageSource.Grok,
                Timestamp = ts,
                Tokens = total,
                Breakdown = new UsageBreakdown(
                    Math.Max(0, promptTotal - cached),
                    output,
                    cached > 0 ? (int?)cached : null,
                    null),
                FilePath = filePath
            };
        }
    }

    // ======================================================================
    // Pi (pi-agent) — ~/.pi/agent/sessions/**/*.jsonl 中 message.role == "assistant"
    // ======================================================================
    internal sealed class PiAdapter : LogAdapter
    {
        public override UsageSource Source { get { return UsageSource.Pi; } }

        protected override string PrimaryRoot
        {
            get
            {
                string env = PathUtil.Env("PI_AGENT_DIR");
                if (env != null) return PathUtil.ExpandTilde(env);
                return PathUtil.Combine(PathUtil.Home, ".pi", "agent", "sessions");
            }
        }

        public override SourceConnectionState CheckConnection(out string detail)
        {
            // ~/.pi/agent 存在即视为「已安装但可能还没用过」
            var state = base.CheckConnection(out detail);
            if (state != SourceConnectionState.NotFound) return state;
            string parent = Path.GetDirectoryName(PrimaryRoot);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                detail = AppPaths.Shorten(parent);
                return SourceConnectionState.Ok;
            }
            return SourceConnectionState.NotFound;
        }

        public override List<string> DiscoverLogFiles(DateTime modifiedSince)
        {
            var list = new List<string>();
            string root = PrimaryRoot;
            if (!Directory.Exists(root)) return list;
            foreach (string f in Walk(root, ".jsonl"))
                if (ModifiedSince(f, modifiedSince)) list.Add(f);
            return list;
        }

        public override UsageEvent ParseLine(string line, string filePath)
        {
            if (Blank(line)) return null;
            var obj = Json.Parse(line.Trim()) as JObj;
            if (obj == null) return null;

            var message = obj.Obj("message");
            if (message.Str("role") != "assistant") return null;
            var usage = message.Obj("usage");
            if (usage == null) return null;

            int input = usage.Int("input") ?? 0;
            int output = usage.Int("output") ?? 0;
            int cacheRead = usage.Int("cacheRead") ?? 0;
            int cacheWrite = usage.Int("cacheWrite") ?? usage.Int("cacheWrite1h") ?? 0;

            int total;
            int? listed = usage.Int("totalTokens");
            if (listed.HasValue && listed.Value > 0) total = listed.Value;
            else total = input + output + cacheRead + cacheWrite;
            if (total <= 0) return null;

            DateTime ts = ParseIso8601(obj.Str("timestamp"))
                          ?? ParseEpochAny(message.Num("timestamp") ?? 0)
                          ?? DateTime.Now;

            string msgId = message.Str("id") ?? obj.Str("id")
                           ?? (Path.GetFileName(filePath) + ":" + PathUtil.StableHash(line));

            return new UsageEvent
            {
                Id = "pi:" + msgId,
                Source = UsageSource.Pi,
                Timestamp = ts,
                Tokens = total,
                Breakdown = new UsageBreakdown(
                    input > 0 ? (int?)input : null,
                    output > 0 ? (int?)output : null,
                    cacheRead > 0 ? (int?)cacheRead : null,
                    cacheWrite > 0 ? (int?)cacheWrite : null),
                FilePath = filePath
            };
        }
    }

    // ======================================================================
    // Amp (Sourcegraph) — <data root>\threads\**\*.json 中的 usageLedger.events
    //   macOS 用 ~/Library/Application Support/amp，Windows 对应 %APPDATA%\amp
    // ======================================================================
    internal sealed class AmpAdapter : LogAdapter
    {
        public override UsageSource Source { get { return UsageSource.Amp; } }

        public string[] DataRoots
        {
            get
            {
                string env = PathUtil.Env("AMP_DATA_DIR");
                if (env != null)
                {
                    var parts = env.Split(',');
                    var list = new List<string>();
                    foreach (string p in parts)
                    {
                        string t = p.Trim();
                        if (t.Length > 0) list.Add(PathUtil.ExpandTilde(t));
                    }
                    if (list.Count > 0) return list.ToArray();
                }
                return new[]
                {
                    PathUtil.Combine(PathUtil.Home, ".local", "share", "amp"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "amp")
                };
            }
        }

        protected override string PrimaryRoot
        {
            get
            {
                var roots = DataRoots;
                foreach (string r in roots) if (Directory.Exists(r)) return r;
                return roots.Length > 0 ? roots[0] : "";
            }
        }

        public override List<string> DiscoverLogFiles(DateTime modifiedSince)
        {
            var list = new List<string>();
            foreach (string root in DataRoots)
            {
                string threads = Path.Combine(root, "threads");
                if (!Directory.Exists(threads)) continue;
                foreach (string f in Walk(threads, ".json"))
                    if (ModifiedSince(f, modifiedSince)) list.Add(f);
            }
            return list;
        }

        /// <summary>Amp 的 thread 文件是整体 JSON，一次可能产出多条事件。</summary>
        public override List<UsageEvent> ParseThread(string filePath)
        {
            var outEvents = new List<UsageEvent>();
            string text;
            try { text = File.ReadAllText(filePath, Encoding.UTF8); }
            catch (Exception) { return outEvents; }

            var obj = Json.Parse(text) as JObj;
            if (obj == null) return outEvents;

            string threadId = obj.Str("id") ?? Path.GetFileNameWithoutExtension(filePath);
            var events = obj.Obj("usageLedger").Arr("events");
            if (events.Count == 0) return outEvents;
            var messages = obj.Arr("messages");

            for (int i = 0; i < events.Count; i++)
            {
                var ev = events.ObjAt(i);
                string model = ev.Str("model");
                if (model == null) continue;
                var tokens = ev.Obj("tokens");
                if (tokens == null) continue;

                int input = tokens.Int("input") ?? 0;
                int output = tokens.Int("output") ?? 0;
                int? toMessageId = ev.Int("toMessageId");
                int cacheWrite, cacheRead;
                FindCacheTokens(messages, toMessageId, out cacheWrite, out cacheRead);

                int total = input + output + cacheWrite + cacheRead;
                if (total <= 0) continue;

                DateTime ts = ParseIso8601(ev.Str("timestamp")) ?? DateTime.Now;
                string id = string.Join(":", new[]
                {
                    "amp", threadId,
                    ts.ToUniversalTime().Subtract(Epoch).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    model, input.ToString(CultureInfo.InvariantCulture), output.ToString(CultureInfo.InvariantCulture),
                    cacheWrite.ToString(CultureInfo.InvariantCulture), cacheRead.ToString(CultureInfo.InvariantCulture),
                    (toMessageId ?? -1).ToString(CultureInfo.InvariantCulture)
                });

                outEvents.Add(new UsageEvent
                {
                    Id = id,
                    Source = UsageSource.Amp,
                    Timestamp = ts,
                    Tokens = total,
                    Breakdown = new UsageBreakdown(
                        input > 0 ? (int?)input : null,
                        output > 0 ? (int?)output : null,
                        cacheRead > 0 ? (int?)cacheRead : null,
                        cacheWrite > 0 ? (int?)cacheWrite : null),
                    FilePath = filePath
                });
            }
            return outEvents;
        }

        private static void FindCacheTokens(JArr messages, int? toMessageId, out int cacheWrite, out int cacheRead)
        {
            cacheWrite = 0; cacheRead = 0;
            if (!toMessageId.HasValue) return;
            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages.ObjAt(i);
                if (m.Str("role") != "assistant") continue;
                if ((m.Int("messageId") ?? -1) != toMessageId.Value) continue;
                var usage = m.Obj("usage");
                cacheWrite = usage.Int("cacheCreationInputTokens") ?? 0;
                cacheRead = usage.Int("cacheReadInputTokens") ?? 0;
                return;
            }
        }
    }
}
