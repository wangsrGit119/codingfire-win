//
//  Adapters2.cs — CodingFire for Windows
//
//  多工具聚合：为原版 macOS（只有 Claude Code / Codex / Cursor / Grok / Pi / Amp 六个源）
//  补上国内常见的其余工具。
//
//  口径来源：https://github.com/juejin-cn/juejin-usage（MIT）的 packages/core/src/parsers/*.ts。
//  逐字段对齐它的 token 拆分与去重，但不含「按字符数估算」的那几家——见文件末尾说明。
//
//  本文件遵守本项目的既有约束：
//   - 只用 in-box BCL，不引入任何第三方解析库
//   - 不记录 prompt / 代码 / 凭据，只取 token 数字与文件路径
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CodingFire.Core;

namespace CodingFire.Data
{
    // ======================================================================
    // 公共小工具
    // ======================================================================

    /// <summary>四项 token 计数（与 UsageBreakdown 同构）。</summary>
    internal struct Token4
    {
        public int Input, Output, CacheRead, CacheWrite;

        public Token4(int input, int output, int cacheRead, int cacheWrite)
        {
            Input = input; Output = output; CacheRead = cacheRead; CacheWrite = cacheWrite;
        }

        public int Total { get { return Input + Output + CacheRead + CacheWrite; } }

        public UsageBreakdown Breakdown()
        {
            return new UsageBreakdown(
                Input > 0 ? (int?)Input : null,
                Output > 0 ? (int?)Output : null,
                CacheRead > 0 ? (int?)CacheRead : null,
                CacheWrite > 0 ? (int?)CacheWrite : null);
        }
    }

    internal static class AdapterIo
    {
        /// <summary>
        /// 读取正被其他进程写着的文件。日志与 SQLite 都要求共享读；
        /// 超过 64MB 直接放弃（不是我们要的格式）。
        /// </summary>
        public static string ReadAllTextShared(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = fs.Length;
                    if (len <= 0 || len > 64L * 1024 * 1024) return null;
                    var buf = new byte[len];
                    int done = 0;
                    while (done < buf.Length)
                    {
                        int n = fs.Read(buf, done, buf.Length - done);
                        if (n <= 0) break;
                        done += n;
                    }
                    int start = 0;
                    // 去掉 UTF-8 BOM，否则 JSON.Parse 会在第一个字符上失败
                    if (done >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) start = 3;
                    return Encoding.UTF8.GetString(buf, start, done - start);
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>把 "a;b,c" 形式的环境变量拆成路径列表（Windows 上 : 不能当分隔符）。</summary>
        public static string[] SplitRoots(string value)
        {
            if (string.IsNullOrEmpty(value)) return new string[0];
            var parts = value.Split(new char[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<string>();
            foreach (string p in parts)
            {
                string t = p.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list.ToArray();
        }

        /// <summary>文件是否在上次读取后变动过（大小 + mtime 双判）。</summary>
        public static bool Changed(string path, ref long lastLen, ref long lastTicks)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return false;
                long len = fi.Length;
                long ticks = fi.LastWriteTimeUtc.Ticks;
                if (len == lastLen && ticks == lastTicks) return false;
                lastLen = len; lastTicks = ticks;
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// 「整文件型」源的变更缓存：文件没变就直接跳过，避免每 4 秒把几 MB 的
    /// 会话存档重新解析一遍（行式源有字节游标，不需要这个）。
    /// </summary>
    internal sealed class FileStampCache
    {
        private readonly Dictionary<string, long[]> _map = new Dictionary<string, long[]>(StringComparer.Ordinal);

        public bool Changed(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return false;
                long len = fi.Length;
                long ticks = fi.LastWriteTimeUtc.Ticks;

                long[] prev;
                if (_map.TryGetValue(path, out prev) && prev[0] == len && prev[1] == ticks) return false;
                _map[path] = new long[] { len, ticks };
                return true;
            }
            catch (Exception) { return false; }
        }

        public void Clear() { _map.Clear(); }
    }

    // ======================================================================
    // WorkBuddy — <home>/projects/**/*.jsonl 的 providerData.rawUsage
    //   口径与 CodeBuddy 不同：它的 prompt_tokens 里含缓存，且 reasoning 含在 completion 内。
    //   另有 SQLite 回退：workbuddy.db 的 session_usage（只有总量，没有明细）。
    //
    //   国内版与国外版是两份独立安装、两个互不相干的 home 目录：
    //       国内版  ~/.workbuddy      （app-config.json: locale = zh-CN）
    //       国外版  ~/.workbuddy-ai   （app-config.json: locale = en-US）
    //   两者的 jsonl 与 workbuddy.db 结构完全一致，所以共用这一个适配器，
    //   只把「源标识 / 环境变量名 / 目录名 / 事件 id 前缀」做成构造参数。
    //   只认 ~/.workbuddy 的话，国外版的用量会整条漏掉。
    // ======================================================================
    internal sealed class WorkBuddyAdapter : LogAdapter
    {
        private readonly HashSet<string> _jsonlSessions = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _dbBest = new Dictionary<string, int>(StringComparer.Ordinal);
        private string _dbPath;
        private long _dbLen = -1, _dbTicks = -1;

        private readonly UsageSource _source;
        private readonly string _envName;
        private readonly string _folderName;
        private readonly string _idPrefix;

        /// <summary>国内版（历史默认值，事件 id 前缀保持不变，老库可无缝续读）。</summary>
        public WorkBuddyAdapter()
            : this(UsageSource.WorkBuddy, "WORKBUDDY_HOME", ".workbuddy", "workbuddy") { }

        public WorkBuddyAdapter(UsageSource source, string envName, string folderName, string idPrefix)
        {
            _source = source;
            _envName = envName;
            _folderName = folderName;
            _idPrefix = idPrefix;
        }

        /// <summary>
        /// 国外版 WorkBuddy：home 是 ~/.workbuddy-ai，独立成源。
        /// 集中在这里构造，保证运行时代码与测试夹具用的是同一份配置。
        /// </summary>
        public static WorkBuddyAdapter CreateIntl()
        {
            return new WorkBuddyAdapter(
                UsageSource.WorkBuddyIntl, "WORKBUDDY_AI_HOME", ".workbuddy-ai", "workbuddy-intl");
        }

        public override UsageSource Source { get { return _source; } }

        private string HomeDir()
        {
            string env = PathUtil.Env(_envName);
            if (env != null) return PathUtil.ExpandTilde(env);
            return Path.Combine(PathUtil.Home, _folderName);
        }

        protected override string PrimaryRoot { get { return Path.Combine(HomeDir(), "projects"); } }

        /// <summary>session_usage 回退读的是 home 下的 workbuddy.db，不在 projects 树里，得单独监听。</summary>
        public override IEnumerable<string> WatchRoots()
        {
            yield return PrimaryRoot;
            yield return Path.Combine(HomeDir(), "workbuddy.db");
        }

        public override SourceConnectionState CheckConnection(out string detail)
        {
            var st = base.CheckConnection(out detail);
            if (st != SourceConnectionState.NotFound) return st;
            string home = HomeDir();
            if (Directory.Exists(home))
            {
                detail = AppPaths.Shorten(home);
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

            var provider = obj.Obj("providerData");
            object rawObj = provider.Raw("rawUsage");
            var raw = rawObj as JObj;
            if (raw == null) return null;

            int promptTokens = NonNeg(raw.Raw("prompt_tokens"));
            int completion = NonNeg(raw.Raw("completion_tokens"));
            var promptDetails = raw.Obj("prompt_tokens_details");

            // 三家写法各不相同，取最大者为准（与参考实现一致）
            int cacheRead = Math.Max(NonNeg(raw.Raw("cache_read_input_tokens")),
                            Math.Max(NonNeg(promptDetails.Raw("cached_tokens")),
                                     NonNeg(raw.Raw("prompt_cache_hit_tokens"))));
            int cacheWrite = NonNeg(raw.Raw("cache_creation_input_tokens"));

            int input = Math.Max(0, promptTokens - cacheRead - cacheWrite);
            var tok = new Token4(input, completion, cacheRead, cacheWrite);
            if (tok.Total <= 0) return null;

            string sessionId = obj.Str("sessionId");
            if (string.IsNullOrEmpty(sessionId)) sessionId = Path.GetFileNameWithoutExtension(filePath);
            _jsonlSessions.Add(sessionId);

            // 毫秒时间戳远超 int 范围：用 Int() 会被截断成 int.MaxValue，
            // 再被 ParseEpochAny 当成「秒」解释，时间直接飞到 2038 年。必须按 double 读。
            double? tsRaw = obj.Num("timestamp");
            DateTime ts = (tsRaw.HasValue && tsRaw.Value > 0)
                ? (ParseEpochAny(tsRaw.Value) ?? DateTime.Now)
                : DateTime.Now;

            string msgId = obj.Str("id");
            if (string.IsNullOrEmpty(msgId)) msgId = provider.Str("messageId");
            if (string.IsNullOrEmpty(msgId))
                msgId = tsRaw.HasValue
                    ? sessionId + ":" + ((long)tsRaw.Value).ToString(CultureInfo.InvariantCulture)
                    : PathUtil.StableHash(line);

            return new UsageEvent
            {
                Id = _idPrefix + ":" + msgId,
                Source = _source,
                Timestamp = ts,
                Tokens = tok.Total,
                Breakdown = tok.Breakdown(),
                FilePath = filePath
            };
        }

        /// <summary>
        /// workbuddy.db 的 session_usage 只有「本会话累计用量」，没有明细。
        /// 只在 JSONL 里没有该会话时才用，且按高水位取增量——和 Claude 同 id 增量同一套思路。
        /// </summary>
        public override List<UsageEvent> ParseDatabase()
        {
            var list = new List<UsageEvent>();
            string dbPath = Path.Combine(HomeDir(), "workbuddy.db");
            if (!File.Exists(dbPath)) return list;
            if (_dbPath != dbPath) { _dbPath = dbPath; _dbLen = -1; _dbTicks = -1; }
            if (!AdapterIo.Changed(dbPath, ref _dbLen, ref _dbTicks)) return list;

            var db = SqliteDb.Open(dbPath);
            if (db == null) return list;
            try
            {
                if (!db.HasTable("session_usage")) return list;
                var usage = db.Table("session_usage");
                int cSid = usage.IndexOf("session_id"), cUsed = usage.IndexOf("used"), cUpd = usage.IndexOf("updated_at");
                if (cSid < 0 || cUsed < 0) return list;

                foreach (object[] row in db.Rows("session_usage"))
                {
                    string sid = SqliteDb.Text(row, cSid);
                    if (string.IsNullOrEmpty(sid)) continue;
                    if (_jsonlSessions.Contains(sid)) continue;   // 有明细就以明细为准

                    int used = (int)Math.Min(SqliteDb.Number(row, cUsed), int.MaxValue);
                    if (used <= 0) continue;

                    long upd = cUpd >= 0 ? SqliteDb.Number(row, cUpd) : 0;
                    int prev;
                    _dbBest.TryGetValue(sid, out prev);

                    // 回退（比如用户清了会话）后 used 会变小，此时把差额按「从头重算」处理
                    if (used < prev) prev = 0;
                    int delta = used - prev;
                    if (delta <= 0) continue;
                    _dbBest[sid] = used;

                    DateTime ts = ParseEpochAny(upd) ?? DateTime.Now;
                    list.Add(new UsageEvent
                    {
                        Id = prev == 0 ? _idPrefix + "-db:" + sid : _idPrefix + "-db:" + sid + "#" + used,
                        Source = _source,
                        Timestamp = ts,
                        Tokens = delta,
                        Breakdown = new UsageBreakdown(delta, null, null, null),
                        FilePath = dbPath,
                        IsEstimated = true
                    });
                }
            }
            catch (Exception) { }
            finally { db.Dispose(); }
            return list;
        }
    }

    // ======================================================================
    // CodeBuddy — ~/.codebuddy/projects/**/*.jsonl，type=="message" && role=="assistant"
    // ======================================================================
    internal sealed class CodeBuddyAdapter : LogAdapter
    {
        public override UsageSource Source { get { return UsageSource.CodeBuddy; } }

        private static string HomeDir()
        {
            string env = PathUtil.Env("CODEBUDDY_HOME");
            if (env != null) return PathUtil.ExpandTilde(env);
            return Path.Combine(PathUtil.Home, ".codebuddy");
        }

        protected override string PrimaryRoot { get { return Path.Combine(HomeDir(), "projects"); } }

        public override SourceConnectionState CheckConnection(out string detail)
        {
            var st = base.CheckConnection(out detail);
            if (st != SourceConnectionState.NotFound) return st;
            string home = HomeDir();
            if (Directory.Exists(home)) { detail = AppPaths.Shorten(home); return SourceConnectionState.Ok; }
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
            if (obj.Str("type") != "message" || obj.Str("role") != "assistant") return null;

            object rawObj = obj.Obj("providerData").Raw("rawUsage");
            var raw = rawObj as JObj;
            if (raw == null) return null;

            int promptTokens = NonNeg(raw.Raw("prompt_tokens"));
            int completion = NonNeg(raw.Raw("completion_tokens"));
            var details = raw.Obj("prompt_tokens_details");

            int cacheRead = Math.Max(NonNeg(details.Raw("cached_tokens")),
                                     NonNeg(raw.Raw("cache_read_input_tokens")));
            int cacheWrite = NonNeg(raw.Raw("cache_creation_input_tokens"));
            int input = Math.Max(0, promptTokens - cacheRead);
            int reasoning = NonNeg(details.Raw("reasoning_tokens"));

            var tok = new Token4(input, completion + reasoning, cacheRead, cacheWrite);
            if (tok.Total <= 0) return null;

            string sessionId = obj.Str("sessionId");
            if (string.IsNullOrEmpty(sessionId)) sessionId = Path.GetFileNameWithoutExtension(filePath);

            // 同上：毫秒时间戳必须按 double 读，Int() 会截断到 int.MaxValue
            double? tsRaw = obj.Num("timestamp");
            DateTime ts = (tsRaw.HasValue && tsRaw.Value > 0)
                ? (ParseEpochAny(tsRaw.Value) ?? DateTime.Now)
                : DateTime.Now;

            string msgId = obj.Str("uuid");
            if (string.IsNullOrEmpty(msgId)) msgId = obj.Str("id");
            if (string.IsNullOrEmpty(msgId))
                msgId = tsRaw.HasValue
                    ? sessionId + ":" + ((long)tsRaw.Value).ToString(CultureInfo.InvariantCulture)
                    : PathUtil.StableHash(line);

            return new UsageEvent
            {
                Id = "codebuddy:" + msgId,
                Source = UsageSource.CodeBuddy,
                Timestamp = ts,
                Tokens = tok.Total,
                Breakdown = tok.Breakdown(),
                FilePath = filePath
            };
        }
    }

    // ======================================================================
    // Qoder — CLI / Work 的会话转写（~/.qoder/projects、~/.qoderwork/projects）
    //   IDE 版数据在 local.db 里；这里只覆盖 JSONL 那两支。
    // ======================================================================
    internal sealed class QoderAdapter : LogAdapter
    {
        private readonly FileStampCache _dbStamps = new FileStampCache();

        public override UsageSource Source { get { return UsageSource.Qoder; } }

        /// <summary>
        /// Qoder IDE（国际版 Qoder / 国内版 QoderCN）把每次请求的账写进
        /// %APPDATA%\&lt;产品&gt;\SharedClientCache\cache\db\local.db。
        /// </summary>
        private static string[] IdeDbPaths()
        {
            // 允许覆盖：既接受产品目录（…\Qoder），也接受库文件所在目录
            string env = PathUtil.Env("AI_USAGE_QODER_IDE_ROOTS");
            if (env != null)
            {
                var over = new List<string>();
                foreach (string r in AdapterIo.SplitRoots(env))
                {
                    string p = PathUtil.ExpandTilde(r);
                    over.Add(p);
                    over.Add(PathUtil.Combine(p, "SharedClientCache", "cache", "db", "local.db"));
                    over.Add(PathUtil.Combine(p, "local.db"));
                }
                if (over.Count > 0) return over.ToArray();
            }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData)) return new string[0];
            return new[]
            {
                PathUtil.Combine(appData, "Qoder", "SharedClientCache", "cache", "db", "local.db"),
                PathUtil.Combine(appData, "QoderCN", "SharedClientCache", "cache", "db", "local.db")
            };
        }

        public string[] Roots
        {
            get
            {
                string env = PathUtil.Env("AI_USAGE_QODER_ROOTS");
                if (env != null)
                {
                    string[] raw = AdapterIo.SplitRoots(env);
                    if (raw.Length > 0)
                    {
                        var exp = new List<string>();
                        foreach (string r in raw) exp.Add(PathUtil.ExpandTilde(r));
                        return exp.ToArray();
                    }
                }
                return new[]
                {
                    PathUtil.Combine(PathUtil.Home, ".qoder", "projects"),
                    PathUtil.Combine(PathUtil.Home, ".qoderwork", "projects")
                };
            }
        }

        protected override string PrimaryRoot { get { return Roots[0]; } }

        public override SourceConnectionState CheckConnection(out string detail)
        {
            foreach (string r in Roots)
            {
                if (Directory.Exists(r)) { detail = AppPaths.Shorten(r); return SourceConnectionState.Ok; }
            }
            foreach (string db in IdeDbPaths())
            {
                if (File.Exists(db)) { detail = AppPaths.Shorten(db); return SourceConnectionState.Ok; }
            }
            detail = AppPaths.Shorten(PrimaryRoot);
            return SourceConnectionState.NotFound;
        }

        /// <summary>账主要在几个 SQLite 库里，在日志树之外，库文件也要监听。</summary>
        public override IEnumerable<string> WatchRoots()
        {
            foreach (string r in Roots) yield return r;
            foreach (string db in IdeDbPaths()) yield return db;
        }

        public override List<string> DiscoverLogFiles(DateTime modifiedSince)
        {
            var list = new List<string>();
            foreach (string root in Roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string f in Walk(root, ".jsonl"))
                    if (ModifiedSince(f, modifiedSince)) list.Add(f);
            }
            return list;
        }

        public override UsageEvent ParseLine(string line, string filePath)
        {
            if (Blank(line)) return null;
            var obj = Json.Parse(line.Trim()) as JObj;
            if (obj == null) return null;

            // CLI 转写：type=="assistant" 且 message.usage 为 Anthropic 形状
            string type = obj.Str("type");
            var message = obj.Obj("message");
            var usage = message.Obj("usage");
            if (usage == JObj.Empty) usage = obj.Obj("usage");
            if (usage == JObj.Empty) return null;
            if (type != null && type != "assistant" && message.Str("role") != "assistant") return null;

            int input = NonNeg(usage.Raw("input_tokens"));
            int output = NonNeg(usage.Raw("output_tokens"));
            int cacheRead = NonNeg(usage.Raw("cache_read_input_tokens"));
            int cacheWrite = NonNeg(usage.Raw("cache_creation_input_tokens"));
            // Anthropic 口径：input_tokens 不含缓存
            var tok = new Token4(input, output, cacheRead, cacheWrite);
            if (tok.Total <= 0) return null;

            DateTime ts = ParseIso8601(obj.Str("timestamp"))
                          ?? ParseEpochAny(obj.Num("timestamp") ?? 0)
                          ?? DateTime.Now;

            string msgId = message.Str("id");
            if (string.IsNullOrEmpty(msgId)) msgId = obj.Str("uuid");
            if (string.IsNullOrEmpty(msgId)) msgId = obj.Str("id");
            if (string.IsNullOrEmpty(msgId)) msgId = obj.Str("requestId");
            if (string.IsNullOrEmpty(msgId)) msgId = PathUtil.StableHash(line);

            return new UsageEvent
            {
                Id = "qoder:" + msgId,
                Source = UsageSource.Qoder,
                Timestamp = ts,
                Tokens = tok.Total,
                Breakdown = tok.Breakdown(),
                FilePath = filePath
            };
        }

        /// <summary>
        /// IDE 那支的账：chat_message.role=='assistant' 且 token_info 是一段 JSON
        /// （prompt_tokens / completion_tokens / cached_tokens）。表结构明确，
        /// 直接用内置只读 SQLite 读取器取；没有这张表就静默跳过。
        /// </summary>
        public override List<UsageEvent> ParseDatabase()
        {
            var list = new List<UsageEvent>();
            foreach (string dbPath in IdeDbPaths())
            {
                if (!File.Exists(dbPath)) continue;
                if (!_dbStamps.Changed(dbPath)) continue;

                var db = SqliteDb.Open(dbPath);
                if (db == null) continue;
                try
                {
                    var t = db.Table("chat_message");
                    if (t == null) continue;

                    int cId = t.IndexOf("id");
                    int cRole = t.IndexOf("role");
                    int cTok = t.IndexOf("token_info");
                    int cGmt = t.IndexOf("gmt_create");
                    if (cRole < 0 || cTok < 0) continue;

                    foreach (object[] row in db.Rows("chat_message"))
                    {
                        if (!string.Equals(SqliteDb.Text(row, cRole), "assistant",
                                           StringComparison.Ordinal)) continue;

                        string raw = SqliteDb.Text(row, cTok);
                        if (string.IsNullOrEmpty(raw) || raw.Length <= 2) continue;
                        var o = Json.Parse(raw.Trim()) as JObj;
                        if (o == null) continue;

                        int prompt = NonNeg(o.Raw("prompt_tokens"));
                        int completion = NonNeg(o.Raw("completion_tokens"));
                        int cached = NonNeg(o.Raw("cached_tokens"));
                        int input = Math.Max(0, prompt - cached);
                        var tok = new Token4(input, completion, cached, 0);
                        if (tok.Total <= 0) continue;

                        string mid = cId >= 0 ? SqliteDb.Text(row, cId) : null;
                        if (string.IsNullOrEmpty(mid)) continue;

                        long gmt = cGmt >= 0 ? SqliteDb.Number(row, cGmt) : 0;
                        DateTime ts = ParseEpochAny(gmt) ?? DateTime.Now;

                        list.Add(new UsageEvent
                        {
                            Id = "qoder-ide:" + mid,
                            Source = UsageSource.Qoder,
                            Timestamp = ts,
                            Tokens = tok.Total,
                            Breakdown = tok.Breakdown(),
                            FilePath = dbPath
                        });
                    }
                }
                catch (Exception) { }
                finally { db.Dispose(); }
            }
            return list;
        }
    }

    // ======================================================================
    // Qwen Code — ~/.qwen/tmp/<proj>/chats/*.jsonl，type=="assistant" 带 usageMetadata
    // ======================================================================
    internal sealed class QwenCodeAdapter : LogAdapter
    {
        public override UsageSource Source { get { return UsageSource.QwenCode; } }

        private static string TmpDir()
        {
            string env = PathUtil.Env("QWEN_TMP_DIR");
            if (env != null) return PathUtil.ExpandTilde(env);
            return PathUtil.Combine(PathUtil.Home, ".qwen", "tmp");
        }

        protected override string PrimaryRoot { get { return TmpDir(); } }

        public override List<string> DiscoverLogFiles(DateTime modifiedSince)
        {
            var list = new List<string>();
            string root = TmpDir();
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
            if (obj.Str("type") != "assistant") return null;

            var meta = obj.Obj("usageMetadata");
            if (meta == JObj.Empty) return null;

            int prompt = NonNeg(meta.Raw("promptTokenCount"));
            int candidates = NonNeg(meta.Raw("candidatesTokenCount"));
            int cached = NonNeg(meta.Raw("cachedContentTokenCount"));
            int thoughts = NonNeg(meta.Raw("thoughtsTokenCount"));

            int input = Math.Max(0, prompt - cached);
            int output = Math.Max(0, candidates - thoughts) + thoughts; // reasoning 计入 output
            var tok = new Token4(input, output, cached, 0);
            if (tok.Total <= 0) return null;

            string msgId = obj.Str("uuid");
            if (string.IsNullOrEmpty(msgId)) msgId = obj.Str("id");
            if (string.IsNullOrEmpty(msgId)) msgId = PathUtil.StableHash(line);

            DateTime ts = ParseIso8601(obj.Str("timestamp"))
                          ?? ParseEpochAny(obj.Num("timestamp") ?? 0)
                          ?? DateTime.Now;

            return new UsageEvent
            {
                Id = "qwen:" + msgId,
                Source = UsageSource.QwenCode,
                Timestamp = ts,
                Tokens = tok.Total,
                Breakdown = tok.Breakdown(),
                FilePath = filePath
            };
        }
    }

    // ======================================================================
    // Kimi — 新版 ~/.kimi-code/sessions/*/wire.jsonl
    //        旧版 ~/.kimi/sessions/<workDir>/<session>/wire.jsonl
    // ======================================================================
    internal sealed class KimiAdapter : LogAdapter
    {
        public override UsageSource Source { get { return UsageSource.Kimi; } }

        public string[] Roots
        {
            get
            {
                var list = new List<string>();
                string code = PathUtil.Env("KIMI_CODE_HOME");
                list.Add(code != null
                    ? PathUtil.ExpandTilde(code)
                    : Path.Combine(PathUtil.Home, ".kimi-code"));
                string legacy = PathUtil.Env("KIMI_HOME");
                list.Add(legacy != null
                    ? PathUtil.ExpandTilde(legacy)
                    : Path.Combine(PathUtil.Home, ".kimi"));
                return list.ToArray();
            }
        }

        protected override string PrimaryRoot { get { return Roots[0]; } }

        public override SourceConnectionState CheckConnection(out string detail)
        {
            foreach (string r in Roots)
                if (Directory.Exists(r)) { detail = AppPaths.Shorten(r); return SourceConnectionState.Ok; }
            detail = AppPaths.Shorten(PrimaryRoot);
            return SourceConnectionState.NotFound;
        }

        public override List<string> DiscoverLogFiles(DateTime modifiedSince)
        {
            var files = new List<string>();
            foreach (string root in Roots)
            {
                string sessions = Path.Combine(root, "sessions");
                if (!Directory.Exists(sessions)) continue;
                foreach (string f in Walk(sessions, ".jsonl"))
                {
                    if (!string.Equals(Path.GetFileName(f), "wire.jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ModifiedSince(f, modifiedSince)) files.Add(f);
                }
            }
            return files;
        }

        public override UsageEvent ParseLine(string line, string filePath)
        {
            if (Blank(line)) return null;
            var obj = Json.Parse(line.Trim()) as JObj;
            if (obj == null) return null;

            // ---- 新版：step.end 带 usage ----
            // 外层有两种写法：{type:"step.end",usage:{...}}，
            // 以及 {type:"context.append_loop_event", event:{type:"step.end",usage:{...}}}。
            var evt = obj.Obj("event");
            if (evt == JObj.Empty) evt = obj;
            var usage = evt.Obj("usage");
            if (evt.Str("type") == "step.end" && usage != JObj.Empty)
            {
                // 参考实现用的是 inputCacheRead / inputCacheCreation
                // （老写法见过 cacheRead / cacheWrite，一并宽容接受）
                int inputOther = NonNeg(usage.Raw("inputOther"));
                int cacheRead = NonNeg(usage.Raw("inputCacheRead"));
                if (cacheRead == 0) cacheRead = NonNeg(usage.Raw("cacheRead"));
                int cacheWrite = NonNeg(usage.Raw("inputCacheCreation"));
                if (cacheWrite == 0) cacheWrite = NonNeg(usage.Raw("cacheWrite"));
                int output = NonNeg(usage.Raw("output"));
                var tok = new Token4(inputOther, output, cacheRead, cacheWrite);
                if (tok.Total <= 0) return null;

                DateTime ts2 = ParseEpochAny(obj.Num("time") ?? 0)
                               ?? ParseEpochAny(evt.Num("time") ?? 0)
                               ?? ParseIso8601(obj.Str("time"))
                               ?? DateTime.Now;
                string id2 = evt.Str("uuid");
                if (string.IsNullOrEmpty(id2)) id2 = obj.Str("uuid");
                if (string.IsNullOrEmpty(id2)) id2 = PathUtil.StableHash(line);
                return new UsageEvent
                {
                    Id = "kimi:" + id2,
                    Source = UsageSource.Kimi,
                    Timestamp = ts2,
                    Tokens = tok.Total,
                    Breakdown = tok.Breakdown(),
                    FilePath = filePath
                };
            }

            // ---- 旧版：message.type == "StatusUpdate"，用量在 message.payload.token_usage ----
            var message = obj.Obj("message");
            if (message.Str("type") != "StatusUpdate") return null;
            var payload = message.Obj("payload");
            var tu = payload.Obj("token_usage");
            if (tu == JObj.Empty) tu = message.Obj("token_usage");
            if (tu == JObj.Empty) tu = obj.Obj("token_usage");
            if (tu == JObj.Empty) return null;

            // 参考实现：input_other 就是输入量本身，缓存另计一列，不做扣减
            int nIn = NonNeg(tu.Raw("input_other"));
            if (nIn == 0) nIn = NonNeg(tu.Raw("input_tokens"));
            int outTok = NonNeg(tu.Raw("output"));
            int cRead = NonNeg(tu.Raw("input_cache_read"));
            if (cRead == 0) cRead = NonNeg(tu.Raw("cache_read_input_tokens"));
            int cWrite = NonNeg(tu.Raw("input_cache_creation"));
            if (cWrite == 0) cWrite = NonNeg(tu.Raw("cache_creation_input_tokens"));

            var t = new Token4(nIn, outTok, cRead, cWrite);
            if (t.Total <= 0) return null;

            // 旧格式的时间戳是「秒」，放在外层 entry 或 payload 上
            DateTime ts = ParseEpochAny(obj.Num("timestamp") ?? 0)
                          ?? ParseEpochAny(payload.Num("timestamp") ?? 0)
                          ?? ParseIso8601(message.Str("timestamp"))
                          ?? DateTime.Now;
            string id = payload.Str("message_id");
            if (string.IsNullOrEmpty(id)) id = message.Str("message_id");
            if (string.IsNullOrEmpty(id)) id = obj.Str("uuid");
            if (string.IsNullOrEmpty(id)) id = PathUtil.StableHash(line);

            return new UsageEvent
            {
                Id = "kimi:" + id,
                Source = UsageSource.Kimi,
                Timestamp = ts,
                Tokens = t.Total,
                Breakdown = t.Breakdown(),
                FilePath = filePath
            };
        }
    }

    // ======================================================================
    // GitHub Copilot CLI — ~/.copilot/session-state/<sid>/events.jsonl
    //   只在 session.shutdown 上带 modelMetrics，一条就是一次完整会话的账。
    // ======================================================================
    internal sealed class CopilotAdapter : LogAdapter
    {
        public override UsageSource Source { get { return UsageSource.Copilot; } }

        private static string HomeDir()
        {
            string env = PathUtil.Env("COPILOT_HOME");
            if (env != null) return PathUtil.ExpandTilde(env);
            return Path.Combine(PathUtil.Home, ".copilot");
        }

        protected override string PrimaryRoot { get { return Path.Combine(HomeDir(), "session-state"); } }

        public override SourceConnectionState CheckConnection(out string detail)
        {
            var st = base.CheckConnection(out detail);
            if (st != SourceConnectionState.NotFound) return st;
            string home = HomeDir();
            if (Directory.Exists(home)) { detail = AppPaths.Shorten(home); return SourceConnectionState.Ok; }
            return SourceConnectionState.NotFound;
        }

        public override List<string> DiscoverLogFiles(DateTime modifiedSince)
        {
            var list = new List<string>();
            string root = PrimaryRoot;
            if (!Directory.Exists(root)) return list;
            foreach (string f in Walk(root, ".jsonl"))
            {
                if (!string.Equals(Path.GetFileName(f), "events.jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                if (ModifiedSince(f, modifiedSince)) list.Add(f);
            }
            return list;
        }

        public override UsageEvent ParseLine(string line, string filePath)
        {
            if (Blank(line)) return null;
            var obj = Json.Parse(line.Trim()) as JObj;
            if (obj == null) return null;
            if (obj.Str("type") != "session.shutdown") return null;

            var metrics = obj.Obj("modelMetrics");
            if (metrics == JObj.Empty) return null;

            string sessionId = Path.GetFileName(Path.GetDirectoryName(filePath));
            if (string.IsNullOrEmpty(sessionId)) sessionId = PathUtil.StableHash(filePath);
            string stamp = obj.Str("timestamp");
            if (stamp == null) stamp = obj.Str("stamp");

            // 一次 shutdown 可能带多个模型；本项目不按模型分账，所以合并成一条。
            // （ParseLine 只能返回一条事件，逐个 return 会把其余模型的账丢掉。）
            int sumInput = 0, sumOutput = 0, sumRead = 0, sumWrite = 0;
            foreach (string model in metrics.Keys)
            {
                var m = metrics.Obj(model);
                var u = m.Obj("usage");
                if (u == JObj.Empty) continue;

                int inRaw = NonNeg(u.Raw("inputTokens"));
                int cRead = NonNeg(u.Raw("cacheReadTokens"));
                sumInput += Math.Max(0, inRaw - cRead);
                sumOutput += NonNeg(u.Raw("outputTokens"));
                sumRead += cRead;
                sumWrite += NonNeg(u.Raw("cacheWriteTokens"));
            }

            var tok = new Token4(sumInput, sumOutput, sumRead, sumWrite);
            if (tok.Total <= 0) return null;

            return new UsageEvent
            {
                Id = "copilot:" + sessionId + "|shutdown|" + (stamp ?? ""),
                Source = UsageSource.Copilot,
                Timestamp = ParseIso8601(stamp) ?? DateTime.Now,
                Tokens = tok.Total,
                Breakdown = tok.Breakdown(),
                FilePath = filePath
            };
        }
    }

    // ======================================================================
    // OpenCode 系（OpenCode / ZCode / Mimo / Kilo CLI 都是同一套 message 表结构）
    //   message.data 是一个 JSON：role / tokens{input,output,reasoning,cache{read,write}}
    //   累计型：同一条消息会随流式输出变大，交给 UsageMonitor 取增量。
    // ======================================================================
    internal abstract class OpencodeStyleDbAdapter : LogAdapter
    {
        private long _dbLen = -1, _dbTicks = -1;
        private DateTime _lastRead = DateTime.MinValue;

        /// <summary>数据库路径（库里没有就返回未安装）。</summary>
        protected abstract string DbFile { get; }

        /// <summary>ZCode 会内嵌 Claude/Codex/Gemini 子代理，那些由各自的源去数，这里排除。</summary>
        protected virtual bool AcceptMessage(JObj data) { return true; }

        protected override string PrimaryRoot { get { return DbFile; } }

        public override bool IsCumulative { get { return true; } }

        public override SourceConnectionState CheckConnection(out string detail)
        {
            detail = AppPaths.Shorten(DbFile);
            return File.Exists(DbFile) ? SourceConnectionState.Ok : SourceConnectionState.NotFound;
        }

        /// <summary>数据库不是「日志文件」，文件型遍历留空。</summary>
        public override List<string> DiscoverLogFiles(DateTime modifiedSince) { return new List<string>(); }

        public override List<UsageEvent> ParseDatabase()
        {
            var list = new List<UsageEvent>();
            string path = DbFile;
            if (!File.Exists(path)) return list;

            // 库可能有几十 MB，4 秒一轮全量重扫太贵：内容没变就直接跳过，
            // 变了也至少间隔 8 秒才重读一次。
            if (!AdapterIo.Changed(path, ref _dbLen, ref _dbTicks)) return list;
            if ((DateTime.Now - _lastRead).TotalSeconds < 8) return list;
            _lastRead = DateTime.Now;

            var db = SqliteDb.Open(path);
            if (db == null) return list;
            try
            {
                var t = db.Table("message");
                if (t == null) return list;
                int cData = t.IndexOf("data");
                int cId = t.IndexOf("id");
                int cSid = t.IndexOf("session_id");
                if (cData < 0) return list;

                foreach (object[] row in db.Rows("message"))
                {
                    string text = SqliteDb.Text(row, cData);
                    if (string.IsNullOrEmpty(text)) continue;
                    var data = Json.Parse(text) as JObj;
                    if (data == null) continue;
                    if (data.Str("role") != "assistant") continue;
                    if (!AcceptMessage(data)) continue;

                    var tok = OpencodeTokens(data.Obj("tokens"));
                    if (tok.Total <= 0) continue;

                    string sid = cSid >= 0 ? SqliteDb.Text(row, cSid) : null;
                    if (string.IsNullOrEmpty(sid)) sid = data.Str("sessionID");
                    string mid = cId >= 0 ? SqliteDb.Text(row, cId) : null;
                    if (string.IsNullOrEmpty(mid)) mid = data.Str("id");
                    if (string.IsNullOrEmpty(mid)) continue;

                    var time = data.Obj("time");
                    DateTime ts = ParseEpochAny(time.Num("completed") ?? 0)
                                  ?? ParseEpochAny(time.Num("created") ?? 0)
                                  ?? DateTime.Now;

                    list.Add(new UsageEvent
                    {
                        Id = Source.Raw() + ":" + (sid ?? "?") + "|" + mid,
                        Source = Source,
                        Timestamp = ts,
                        Tokens = tok.Total,
                        Breakdown = tok.Breakdown(),
                        FilePath = path
                    });
                }
            }
            catch (Exception) { }
            finally { db.Dispose(); }
            return list;
        }

        /// <summary>OpenCode 系列的 tokens 结构，四家通用。</summary>
        protected static Token4 OpencodeTokens(JObj tokens)
        {
            if (tokens == JObj.Empty) return new Token4(0, 0, 0, 0);
            int input = NonNeg(tokens.Raw("input"));
            int output = NonNeg(tokens.Raw("output"));
            int reasoning = NonNeg(tokens.Raw("reasoning"));
            var cache = tokens.Obj("cache");
            int cRead = NonNeg(cache.Raw("read"));
            int cWrite = NonNeg(cache.Raw("write"));
            // reasoning 计入 output（与其余源的计费口径一致）
            return new Token4(input, output + reasoning, cRead, cWrite);
        }
    }

    // ======================================================================
    // OpenCode — ~/.local/share/opencode/opencode.db（旧版还支持 storage/message 的散 JSON）
    // ======================================================================
    internal sealed class OpenCodeAdapter : OpencodeStyleDbAdapter
    {
        private readonly FileStampCache _stamps = new FileStampCache();

        public override UsageSource Source { get { return UsageSource.OpenCode; } }

        private static string DataDir()
        {
            string env = PathUtil.Env("OPENCODE_HOME");
            if (env != null) return PathUtil.ExpandTilde(env);
            string xdg = PathUtil.Env("XDG_DATA_HOME");
            if (xdg != null) return Path.Combine(PathUtil.ExpandTilde(xdg), "opencode");
            return PathUtil.Combine(PathUtil.Home, ".local", "share", "opencode");
        }

        protected override string DbFile { get { return Path.Combine(DataDir(), "opencode.db"); } }

        /// <summary>没有 db 时退回读 storage/message 下的散 JSON（老版本 OpenCode 的布局）。</summary>
        public override List<string> DiscoverLogFiles(DateTime modifiedSince)
        {
            var list = new List<string>();
            if (File.Exists(DbFile)) return list;    // db 才是权威，避免两处重复计数

            string root = PathUtil.Combine(DataDir(), "storage", "message");
            if (!Directory.Exists(root)) return list;
            foreach (string f in Walk(root, ".json"))
                if (ModifiedSince(f, modifiedSince)) list.Add(f);
            return list;
        }

        public override UsageEvent ParseLine(string line, string filePath)
        {
            // 散 JSON 是整文件（单行也兼容），交给 ParseThread 语义处理
            return null;
        }

        public override List<UsageEvent> ParseThread(string filePath)
        {
            var list = new List<UsageEvent>();
            if (File.Exists(DbFile)) return list;    // 有 db 就不看旧格式
            if (!_stamps.Changed(filePath)) return list;

            string text = AdapterIo.ReadAllTextShared(filePath);
            if (text == null) return list;
            var data = Json.Parse(text) as JObj;
            if (data == null) return list;
            if (data.Str("role") != "assistant") return list;

            var tok = OpencodeTokens(data.Obj("tokens"));
            if (tok.Total <= 0) return list;

            string sid = data.Str("sessionID");
            if (string.IsNullOrEmpty(sid)) sid = data.Str("sessionId");
            if (string.IsNullOrEmpty(sid)) sid = Path.GetFileName(Path.GetDirectoryName(filePath));
            string mid = data.Str("id");
            if (string.IsNullOrEmpty(mid)) mid = Path.GetFileNameWithoutExtension(filePath);

            var time = data.Obj("time");
            DateTime ts = ParseEpochAny(time.Num("completed") ?? 0)
                          ?? ParseEpochAny(time.Num("created") ?? 0)
                          ?? DateTime.Now;

            list.Add(new UsageEvent
            {
                Id = "opencode:" + (sid ?? "?") + "|" + mid,
                Source = UsageSource.OpenCode,
                Timestamp = ts,
                Tokens = tok.Total,
                Breakdown = tok.Breakdown(),
                FilePath = filePath
            });
            return list;
        }
    }

    // ======================================================================
    // ZCode — $ZCODE_HOME/cli/db/db.sqlite（默认 ~/.zcode），结构与 OpenCode 同源。
    //   它会把内嵌的 Claude/Codex/Gemini 子代理消息也写进同一张表，
    //   那些由各自的源统计，这里按 providerID 排除，避免重复计数。
    // ======================================================================
    internal sealed class ZCodeAdapter : OpencodeStyleDbAdapter
    {
        public override UsageSource Source { get { return UsageSource.ZCode; } }

        private static string HomeDir()
        {
            string env = PathUtil.Env("ZCODE_HOME");
            if (env != null) return PathUtil.ExpandTilde(env);
            return Path.Combine(PathUtil.Home, ".zcode");
        }

        protected override string DbFile
        {
            get { return PathUtil.Combine(HomeDir(), "cli", "db", "db.sqlite"); }
        }

        protected override bool AcceptMessage(JObj data)
        {
            string provider = data.Str("providerID");
            if (string.IsNullOrEmpty(provider)) return false;
            provider = provider.ToLowerInvariant();
            if (provider.IndexOf("anthropic", StringComparison.Ordinal) >= 0) return false;
            if (provider.IndexOf("openai", StringComparison.Ordinal) >= 0) return false;
            if (provider.IndexOf("google", StringComparison.Ordinal) >= 0) return false;
            return true;
        }
    }

    // ======================================================================
    // Gemini CLI — ~/.gemini/tmp/<proj>/chats/session-*.json（整文件）与 *.jsonl
    // ======================================================================
    internal sealed class GeminiCliAdapter : LogAdapter
    {
        private readonly FileStampCache _stamps = new FileStampCache();

        public override UsageSource Source { get { return UsageSource.GeminiCli; } }

        private static string HomeDir()
        {
            string env = PathUtil.Env("GEMINI_HOME");
            if (env != null) return PathUtil.ExpandTilde(env);
            return Path.Combine(PathUtil.Home, ".gemini");
        }

        private static string TmpDir() { return Path.Combine(HomeDir(), "tmp"); }

        protected override string PrimaryRoot { get { return TmpDir(); } }

        public override bool IsCumulative { get { return true; } }

        public override List<string> DiscoverLogFiles(DateTime modifiedSince)
        {
            var list = new List<string>();
            string root = TmpDir();
            if (!Directory.Exists(root)) return list;
            foreach (string f in Walk(root, ".jsonl"))
                if (ModifiedSince(f, modifiedSince)) list.Add(f);
            // 整文件会话（session-*.json）也一并列出：
            // UsageMonitor 看到 ParseThread 有返回就走整文件解析，否则退回行式读取。
            foreach (string f in Walk(root, ".json"))
                if (ModifiedSince(f, modifiedSince)) list.Add(f);
            return list;
        }

        /// <summary>
        /// 会话存档是「一个文件里装着全部 messages」，assistant 那侧带
        /// tokens{cached, input, output, thoughts, tool, total}；用户消息没有这个字段，自然跳过。
        /// 真遇到不带 usage 的旧会话就老老实实记 0，绝不按字符数估算。
        /// </summary>
        public override List<UsageEvent> ParseThread(string filePath)
        {
            var list = new List<UsageEvent>();
            // 不是 .json（也就是 .jsonl 那种一行一条的）必须返回 null，
            // 让 UsageMonitor 退回到行式解析；返回空表会被当成「这个文件我认领了」，
            // 那些会话就再也不会被读到。
            if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase)) return null;
            if (!_stamps.Changed(filePath)) return list;

            string text = AdapterIo.ReadAllTextShared(filePath);
            if (text == null) return list;
            var root = Json.Parse(text) as JObj;
            if (root == null) return list;

            var messages = root.Arr("messages");
            if (messages.Count == 0) messages = root.Arr("history");
            if (messages.Count == 0) return list;

            string sessionId = root.Str("sessionId");
            if (string.IsNullOrEmpty(sessionId)) sessionId = Path.GetFileNameWithoutExtension(filePath);

            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages.ObjAt(i);
                if (m == JObj.Empty) continue;

                var tok = GeminiMessageTokens(m);
                if (tok.Total <= 0) continue;

                string mid = m.Str("id");
                if (string.IsNullOrEmpty(mid)) mid = sessionId + ":" + i;

                DateTime ts = ParseIso8601(m.Str("timestamp"))
                              ?? ParseIso8601(root.Str("startTime"))
                              ?? DateTime.Now;

                list.Add(new UsageEvent
                {
                    Id = "gemini:" + mid,
                    Source = UsageSource.GeminiCli,
                    Timestamp = ts,
                    Tokens = tok.Total,
                    Breakdown = tok.Breakdown(),
                    FilePath = filePath
                });
            }
            return list;
        }

        public override UsageEvent ParseLine(string line, string filePath)
        {
            if (Blank(line)) return null;
            var obj = Json.Parse(line.Trim()) as JObj;
            if (obj == null) return null;
            var tok = GeminiMessageTokens(obj);
            if (tok.Total <= 0) return null;

            string id = obj.Str("uuid");
            if (string.IsNullOrEmpty(id)) id = obj.Str("id");
            if (string.IsNullOrEmpty(id)) id = PathUtil.StableHash(line);
            DateTime ts = ParseIso8601(obj.Str("timestamp")) ?? DateTime.Now;

            return new UsageEvent
            {
                Id = "gemini:" + id,
                Source = UsageSource.GeminiCli,
                Timestamp = ts,
                Tokens = tok.Total,
                Breakdown = tok.Breakdown(),
                FilePath = filePath
            };
        }

        private static Token4 GeminiMessageTokens(JObj m)
        {
            // 两种写法都见过：messages[].tokens 与 usageMetadata
            var t = m.Obj("tokens");
            if (t != JObj.Empty)
            {
                int ti = NonNeg(t.Raw("input"));
                int to = NonNeg(t.Raw("output"));
                int tool = NonNeg(t.Raw("tool"));
                int cached = NonNeg(t.Raw("cached"));
                int thoughts = NonNeg(t.Raw("thoughts"));
                return new Token4(ti, to + tool + thoughts, cached, 0);
            }

            var u = m.Obj("usageMetadata");
            if (u == JObj.Empty) u = m.Obj("usage");
            if (u == JObj.Empty) return new Token4(0, 0, 0, 0);

            int prompt = NonNeg(u.Raw("promptTokenCount"));
            int cand = NonNeg(u.Raw("candidatesTokenCount"));
            int cached2 = NonNeg(u.Raw("cachedContentTokenCount"));
            int thoughts2 = NonNeg(u.Raw("thoughtsTokenCount"));
            int input = Math.Max(0, prompt - cached2);
            int output = Math.Max(0, cand - thoughts2) + thoughts2;
            return new Token4(input, output, cached2, 0);
        }
    }

    // ======================================================================
    // Droid (Factory) — ~/.factory/sessions/**/*.settings.json，整文件里有 tokenUsage 累计值
    // ======================================================================
    internal sealed class DroidAdapter : LogAdapter
    {
        private readonly FileStampCache _stamps = new FileStampCache();

        public override UsageSource Source { get { return UsageSource.Droid; } }

        private static string SessionsDir()
        {
            string env = PathUtil.Env("DROID_SESSIONS_DIR");
            if (env != null) return PathUtil.ExpandTilde(env);
            string fac = PathUtil.Env("FACTORY_DIR");
            if (fac != null) return Path.Combine(PathUtil.ExpandTilde(fac), "sessions");
            return PathUtil.Combine(PathUtil.Home, ".factory", "sessions");
        }

        protected override string PrimaryRoot { get { return SessionsDir(); } }

        public override bool IsCumulative { get { return true; } }

        public override List<string> DiscoverLogFiles(DateTime modifiedSince)
        {
            var list = new List<string>();
            string root = SessionsDir();
            if (!Directory.Exists(root)) return list;
            foreach (string f in Walk(root, ".json"))
            {
                if (!f.EndsWith(".settings.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (ModifiedSince(f, modifiedSince)) list.Add(f);
            }
            return list;
        }

        public override List<UsageEvent> ParseThread(string filePath)
        {
            var list = new List<UsageEvent>();
            if (!_stamps.Changed(filePath)) return list;
            string text = AdapterIo.ReadAllTextShared(filePath);
            if (text == null) return list;
            var root = Json.Parse(text) as JObj;
            if (root == null) return list;

            var tu = root.Obj("tokenUsage");
            if (tu == JObj.Empty) return list;

            int input = NonNeg(tu.Raw("inputTokens"));
            int output = NonNeg(tu.Raw("outputTokens"));
            int cRead = NonNeg(tu.Raw("cacheReadTokens"));
            int cWrite = NonNeg(tu.Raw("cacheCreationTokens"));
            int thinking = NonNeg(tu.Raw("thinkingTokens"));
            int total = NonNeg(tu.Raw("totalTokens"));

            var tok = new Token4(input, output + thinking, cRead, cWrite);
            if (tok.Total <= 0 && total <= 0) return list;

            // 会话目录名即会话标识
            string sid = Path.GetFileName(Path.GetDirectoryName(filePath));
            if (string.IsNullOrEmpty(sid)) sid = PathUtil.StableHash(filePath);

            DateTime ts = DateTime.Now;
            try { ts = File.GetLastWriteTime(filePath); } catch (Exception) { }

            list.Add(new UsageEvent
            {
                Id = "droid:" + sid,
                Source = UsageSource.Droid,
                Timestamp = ts,
                Tokens = total > 0 ? total : tok.Total,
                Breakdown = tok.Breakdown(),
                FilePath = filePath
            });
            return list;
        }
    }

    // ======================================================================
    // VS Code 系插件（Cline / Roo Code / Kilo Code）
    //   都住在 <host>\User\globalStorage\<扩展 id>\tasks\<任务 id>\ui_messages.json。
    //   这里是那个「say: api_req_started」的 text，里面塞着一次请求的 token 明细。
    // ======================================================================
    internal abstract class VscodeTaskAdapter : LogAdapter
    {
        private readonly FileStampCache _stamps = new FileStampCache();

        /// <summary>扩展在 globalStorage 下的目录名。</summary>
        protected abstract string ExtensionId { get; }

        /// <summary>允许覆盖根目录的环境变量名。</summary>
        protected abstract string EnvOverride { get; }

        private static readonly string[] Hosts =
        {
            "Code", "Code - Insiders", "VSCodium", "Cursor", "Windsurf",
            "Trae", "Trae CN", "CodeBuddy"
        };

        public string[] TaskRoots
        {
            get
            {
                string env = PathUtil.Env(EnvOverride);
                if (env == null) env = PathUtil.Env("AI_USAGE_VSCODE_ROOTS");
                var roots = new List<string>();

                if (env != null)
                {
                    foreach (string r in AdapterIo.SplitRoots(env))
                    {
                        // 既接受 <host>\User\globalStorage，也接受直接给到 tasks
                        string p = PathUtil.ExpandTilde(r);
                        roots.Add(p);
                        roots.Add(PathUtil.Combine(p, ExtensionId, "tasks"));
                    }
                    return roots.ToArray();
                }

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(appData)) return new string[0];
                foreach (string host in Hosts)
                {
                    roots.Add(PathUtil.Combine(appData, host, "User", "globalStorage", ExtensionId, "tasks"));
                    roots.Add(PathUtil.Combine(appData, host, "User", "globalStorage", ExtensionId));
                }
                return roots.ToArray();
            }
        }

        protected override string PrimaryRoot { get { return TaskRoots.Length > 0 ? TaskRoots[0] : ""; } }

        public override SourceConnectionState CheckConnection(out string detail)
        {
            foreach (string r in TaskRoots)
            {
                if (Directory.Exists(r))
                {
                    detail = AppPaths.Shorten(Path.GetDirectoryName(r) ?? r);
                    return SourceConnectionState.Ok;
                }
            }
            detail = AppPaths.Shorten(PrimaryRoot);
            return SourceConnectionState.NotFound;
        }

        public override List<string> DiscoverLogFiles(DateTime modifiedSince)
        {
            var list = new List<string>();
            foreach (string root in TaskRoots)
            {
                if (!Directory.Exists(root)) continue;

                // 用户直接指到 tasks 目录时，路径里不会出现扩展 id，那就整棵树都算；
                // 否则要求路径里能看见本扩展的 id —— 不然把 globalStorage 根喂进来时，
                // 三家插件会各自把别家的 ui_messages.json 也认成自己的账。
                string trimmed = root.TrimEnd(new char[] { '\\', '/' });
                bool bare = string.Equals(Path.GetFileName(trimmed), "tasks", StringComparison.OrdinalIgnoreCase);

                foreach (string f in Walk(root, ".json"))
                {
                    if (!string.Equals(Path.GetFileName(f), "ui_messages.json", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!bare && f.IndexOf(ExtensionId, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (ModifiedSince(f, modifiedSince)) list.Add(f);
                }
            }
            return list;
        }

        public override List<UsageEvent> ParseThread(string filePath)
        {
            var list = new List<UsageEvent>();
            if (!_stamps.Changed(filePath)) return list;
            string text = AdapterIo.ReadAllTextShared(filePath);
            if (text == null) return list;
            var root = Json.Parse(text);
            var arr = root as JArr;
            if (arr == null) return list;

            string taskId = Path.GetFileName(Path.GetDirectoryName(filePath));
            if (string.IsNullOrEmpty(taskId)) taskId = PathUtil.StableHash(filePath);

            for (int i = 0; i < arr.Count; i++)
            {
                var e = arr.ObjAt(i);
                if (e == JObj.Empty) continue;
                if (e.Str("type") != "say") continue;

                string say = e.Str("say");
                if (say != "api_req_started") continue;

                string payloadText = e.Str("text");
                if (string.IsNullOrEmpty(payloadText)) continue;
                var payload = Json.Parse(payloadText.Trim()) as JObj;
                if (payload == null) continue;

                int tokIn = NonNeg(payload.Raw("tokensIn"));
                int tokOut = NonNeg(payload.Raw("tokensOut"));
                int cRead = NonNeg(payload.Raw("cacheReads"));
                int cWrite = NonNeg(payload.Raw("cacheWrites"));
                var tok = new Token4(tokIn, tokOut, cRead, cWrite);
                if (tok.Total <= 0) continue;

                string stamp = e.Str("ts");
                DateTime ts = ParseIso8601(stamp) ?? ParseEpochAny((double)(e.Num("ts") ?? 0)) ?? DateTime.Now;
                if (ts == DateTime.MinValue) ts = DateTime.Now;

                list.Add(new UsageEvent
                {
                    Id = Source.Raw() + ":" + taskId + ":" + (stamp ?? i.ToString()),
                    Source = Source,
                    Timestamp = ts,
                    Tokens = tok.Total,
                    Breakdown = tok.Breakdown(),
                    FilePath = filePath
                });
            }
            return list;
        }
    }

    internal sealed class ClineAdapter : VscodeTaskAdapter
    {
        public override UsageSource Source { get { return UsageSource.Cline; } }
        protected override string ExtensionId { get { return "saoudrizwan.claude-dev"; } }
        protected override string EnvOverride { get { return "AI_USAGE_CLINE_ROOTS"; } }
    }

    internal sealed class RooCodeAdapter : VscodeTaskAdapter
    {
        public override UsageSource Source { get { return UsageSource.RooCode; } }
        protected override string ExtensionId { get { return "rooveterinaryinc.roo-cline"; } }
        protected override string EnvOverride { get { return "AI_USAGE_ROOCODE_ROOTS"; } }
    }

    internal sealed class KiloCodeAdapter : VscodeTaskAdapter
    {
        public override UsageSource Source { get { return UsageSource.KiloCode; } }
        protected override string ExtensionId { get { return "kilocode.kilo-code"; } }
        protected override string EnvOverride { get { return "AI_USAGE_KILOCODE_ROOTS"; } }
    }

    // ======================================================================
    // DeepSeek Harness — ~/.dsh/sessions/<ws>/<sid>/session.jsonl
    //   （另有 .jsonl.zstd 变体，需要 zstd 解码，本项目不引入，故只读明文那份）
    // ======================================================================
    internal sealed class DeepSeekHarnessAdapter : LogAdapter
    {
        public override UsageSource Source { get { return UsageSource.DeepSeekHarness; } }

        private static string HomeDir()
        {
            string env = PathUtil.Env("DSH_HOME");
            if (env != null) return PathUtil.ExpandTilde(env);
            return Path.Combine(PathUtil.Home, ".dsh");
        }

        protected override string PrimaryRoot { get { return Path.Combine(HomeDir(), "sessions"); } }

        public override bool IsCumulative { get { return true; } }

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

            string type = obj.Str("type");
            if (type != "assistant" && type != "message") return null;

            var data = obj.Obj("data");
            var usage = data.Obj("usage");
            if (usage == JObj.Empty) usage = obj.Obj("usage");
            if (usage == JObj.Empty) return null;

            // 参考实现（dsh.ts）：inputTokens 已经是「不含缓存」的输入量，不要再减；
            // reasoningTokens 单列、不进总量；总量取上报值 vs 四项之和的较大者。
            int input = NonNeg(usage.Raw("inputTokens"));
            int output = NonNeg(usage.Raw("outputTokens"));
            int cRead = NonNeg(usage.Raw("cacheReadTokens"));
            int cWrite = NonNeg(usage.Raw("cacheWriteTokens"));
            var tok = new Token4(input, output, cRead, cWrite);
            int listedTotal = NonNeg(usage.Raw("totalTokens"));
            int dshTotal = Math.Max(listedTotal, tok.Total);
            if (dshTotal <= 0) return null;

            var time = data.Raw("time") ?? obj.Raw("time");
            DateTime ts = ParseEpochAny(time is double ? (double)time : 0)
                          ?? ParseIso8601(time as string)
                          ?? DateTime.Now;

            string mid = data.Str("id");
            if (string.IsNullOrEmpty(mid)) mid = obj.Str("id");
            if (string.IsNullOrEmpty(mid)) mid = PathUtil.StableHash(line);

            return new UsageEvent
            {
                Id = "dsh:" + Path.GetFileName(Path.GetDirectoryName(filePath)) + "|" + mid,
                Source = UsageSource.DeepSeekHarness,
                Timestamp = ts,
                Tokens = dshTotal,
                Breakdown = tok.Breakdown(),
                FilePath = filePath
            };
        }
    }

    // ======================================================================
    // Command Code — ~/.commandcode/projects/**/*.jsonl，type=="message" + role assistant
    // ======================================================================
    internal sealed class CommandCodeAdapter : LogAdapter
    {
        public override UsageSource Source { get { return UsageSource.CommandCode; } }

        private static string Root()
        {
            string env = PathUtil.Env("AI_USAGE_COMMANDCODE_ROOTS");
            if (env != null)
            {
                string[] parts = AdapterIo.SplitRoots(env);
                if (parts.Length > 0) return PathUtil.ExpandTilde(parts[0]);
            }
            return PathUtil.Combine(PathUtil.Home, ".commandcode", "projects");
        }

        protected override string PrimaryRoot { get { return Root(); } }

        public override bool IsCumulative { get { return true; } }

        public override List<string> DiscoverLogFiles(DateTime modifiedSince)
        {
            var list = new List<string>();
            string root = Root();
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
            if (obj.Str("type") != "message") return null;

            var message = obj.Obj("message");
            if (message.Str("role") != "assistant") return null;

            var usage = message.Obj("usage");
            if (usage == JObj.Empty) usage = obj.Obj("usage");
            if (usage == JObj.Empty) return null;

            int inRaw = NonNeg(usage.Raw("inputTokens"));
            if (inRaw == 0) inRaw = NonNeg(usage.Raw("input_tokens"));
            int output = NonNeg(usage.Raw("outputTokens"));
            if (output == 0) output = NonNeg(usage.Raw("output_tokens"));
            int cRead = NonNeg(usage.Raw("cacheReadTokens"));
            if (cRead == 0) cRead = NonNeg(usage.Raw("cache_read_input_tokens"));
            int cWrite = NonNeg(usage.Raw("cacheWriteTokens"));
            if (cWrite == 0) cWrite = NonNeg(usage.Raw("cache_creation_input_tokens"));
            int input = cRead > 0 ? Math.Max(0, inRaw - cRead) : inRaw;

            var tok = new Token4(input, output, cRead, cWrite);
            if (tok.Total <= 0) return null;

            string mid = message.Str("id");
            if (string.IsNullOrEmpty(mid)) mid = obj.Str("uuid");
            if (string.IsNullOrEmpty(mid)) mid = obj.Str("id");
            if (string.IsNullOrEmpty(mid)) mid = PathUtil.StableHash(line);

            DateTime ts = ParseIso8601(obj.Str("timestamp"))
                          ?? ParseEpochAny(obj.Num("timestamp") ?? 0)
                          ?? DateTime.Now;

            return new UsageEvent
            {
                Id = "command-code:" + mid,
                Source = UsageSource.CommandCode,
                Timestamp = ts,
                Tokens = tok.Total,
                Breakdown = tok.Breakdown(),
                FilePath = filePath
            };
        }
    }

    // ======================================================================
    // OpenClaw / AutoClaw — <state dir>/agents/<id>/sessions/*.jsonl
    //   记录形态是 type=="message" + msg.role=="assistant" + usage，
    //   但各家字段命名很杂（input / inputTokens / prompt_tokens …），所以宽容取键。
    // ======================================================================
    internal abstract class ClawStyleAdapter : LogAdapter
    {
        protected abstract string[] StateDirs { get; }
        protected abstract string IdPrefix { get; }

        protected override string PrimaryRoot { get { return StateDirs[0]; } }

        public override SourceConnectionState CheckConnection(out string detail)
        {
            foreach (string r in StateDirs)
                if (Directory.Exists(r)) { detail = AppPaths.Shorten(r); return SourceConnectionState.Ok; }
            detail = AppPaths.Shorten(PrimaryRoot);
            return SourceConnectionState.NotFound;
        }

        public override List<string> DiscoverLogFiles(DateTime modifiedSince)
        {
            var list = new List<string>();
            foreach (string dir in StateDirs)
            {
                string agents = Path.Combine(dir, "agents");
                if (!Directory.Exists(agents)) continue;
                foreach (string f in Walk(agents, ".jsonl"))
                    if (ModifiedSince(f, modifiedSince)) list.Add(f);
            }
            return list;
        }

        public override UsageEvent ParseLine(string line, string filePath)
        {
            if (Blank(line)) return null;
            var obj = Json.Parse(line.Trim()) as JObj;
            if (obj == null) return null;
            if (obj.Str("type") != "message") return null;

            var msg = obj.Obj("msg");
            if (msg == JObj.Empty) msg = obj.Obj("message");
            if (msg.Str("role") != "assistant") return null;

            var usage = msg.Obj("usage");
            if (usage == JObj.Empty) usage = obj.Obj("usage");
            if (usage == JObj.Empty) return null;

            int inRaw = FirstOf(usage, "input", "inputTokens", "input_tokens", "promptTokens", "prompt_tokens");
            int output = FirstOf(usage, "output", "outputTokens", "output_tokens", "completionTokens", "completion_tokens");
            int cRead = FirstOf(usage, "cacheRead", "cache_read", "cachedInputTokens", "cached_input_tokens", "cache_read_input_tokens");
            int cWrite = FirstOf(usage, "cacheWrite", "cache_write", "cache_creation_input_tokens");
            int input = cRead > 0 ? Math.Max(0, inRaw - cRead) : inRaw;

            var tok = new Token4(input, output, cRead, cWrite);
            if (tok.Total <= 0) return null;

            string mid = msg.Str("id");
            if (string.IsNullOrEmpty(mid)) mid = obj.Str("id");
            if (string.IsNullOrEmpty(mid)) mid = PathUtil.StableHash(line);

            object tsRaw = obj.Raw("timestamp") ?? msg.Raw("timestamp");
            DateTime ts = ParseEpochAny(tsRaw is double ? (double)tsRaw : 0)
                          ?? ParseIso8601(tsRaw as string)
                          ?? DateTime.Now;

            // agents/<agentId> 作为归属
            string agentId = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(filePath)));
            if (string.IsNullOrEmpty(agentId)) agentId = "?";

            return new UsageEvent
            {
                Id = IdPrefix + ":" + agentId + "|" + mid,
                Source = Source,
                Timestamp = ts,
                Tokens = tok.Total,
                Breakdown = tok.Breakdown(),
                FilePath = filePath
            };
        }

        private static int FirstOf(JObj o, params string[] keys)
        {
            foreach (string k in keys)
            {
                object v = o.Raw(k);
                if (v != null) return NonNeg(v);
            }
            return 0;
        }
    }

    internal sealed class OpenClawAdapter : ClawStyleAdapter
    {
        public override UsageSource Source { get { return UsageSource.OpenClaw; } }
        protected override string IdPrefix { get { return "openclaw"; } }

        protected override string[] StateDirs
        {
            get
            {
                string env = PathUtil.Env("OPENCLAW_STATE_DIR");
                if (env != null)
                {
                    var one = AdapterIo.SplitRoots(env);
                    if (one.Length > 0) return new[] { PathUtil.ExpandTilde(one[0]) };
                }
                return new[]
                {
                    PathUtil.Combine(PathUtil.Home, ".openclaw"),
                    Path.Combine(PathUtil.Home, ".clawdbot"),
                    PathUtil.Combine(PathUtil.Home, ".moltbot"),
                    PathUtil.Combine(PathUtil.Home, ".autoclaw"),
                    PathUtil.Combine(PathUtil.Home, ".openclaw-autoclaw")
                };
            }
        }
    }

    // ======================================================================
    // Every Code — <CODE_HOME>/sessions 与 archived_sessions，Codex rollout 同格式
    // ======================================================================
    internal sealed class EveryCodeAdapter : LogAdapter
    {
        /// <summary>total_token_usage 回退分支用：每个会话上次见过的累计总量。</summary>
        private readonly Dictionary<string, int> _bestTotals = new Dictionary<string, int>(StringComparer.Ordinal);

        public override UsageSource Source { get { return UsageSource.EveryCode; } }

        private static string HomeDir()
        {
            string env = PathUtil.Env("AI_USAGE_EVERY_CODE_HOME");
            if (env == null) env = PathUtil.Env("CODE_HOME");
            if (env != null) return PathUtil.ExpandTilde(env);
            return Path.Combine(PathUtil.Home, ".code");
        }

        protected override string PrimaryRoot { get { return Path.Combine(HomeDir(), "sessions"); } }

        public override List<string> DiscoverLogFiles(DateTime modifiedSince)
        {
            var list = new List<string>();
            string home = HomeDir();
            foreach (string sub in new[] { "sessions", "archived_sessions" })
            {
                string root = Path.Combine(home, sub);
                if (!Directory.Exists(root)) continue;
                foreach (string f in Walk(root, ".jsonl"))
                    if (ModifiedSince(f, modifiedSince)) list.Add(f);
            }
            return list;
        }

        public override UsageEvent ParseLine(string line, string filePath)
        {
            if (Blank(line)) return null;
            var obj = Json.Parse(line.Trim()) as JObj;
            if (obj == null) return null;

            // 两种外层写法：payload.type=="token_count" 与 payload.msg.type=="token_count"
            var payload = obj.Obj("payload");
            if (payload == JObj.Empty) return null;
            var info = payload.Obj("info");
            if (payload.Str("type") != "token_count" || info == JObj.Empty)
            {
                var msg = payload.Obj("msg");
                if (msg.Str("type") != "token_count") return null;
                info = msg.Obj("info");
                if (info == JObj.Empty) return null;
            }

            string uuid = payload.Str("session_id");
            if (string.IsNullOrEmpty(uuid)) uuid = obj.Str("session_id");
            if (string.IsNullOrEmpty(uuid)) uuid = Path.GetFileNameWithoutExtension(filePath);
            string stamp = payload.Str("timestamp");
            if (stamp == null) stamp = obj.Str("timestamp");

            // last_token_usage 本身就是「本轮增量」，直接采信；
            // 只有它缺席时才拿 total_token_usage 减去本会话上一轮的累计值。
            // （早先的写法直接读了 total_token_usage，等于把整个会话按行重复计了很多遍。）
            bool fromTotal = false;
            var usage = info.Obj("last_token_usage");
            if (usage == JObj.Empty)
            {
                usage = info.Obj("total_token_usage");
                if (usage != JObj.Empty) fromTotal = true;
                else usage = payload.Obj("usage");
            }
            if (usage == JObj.Empty) return null;

            int inRaw = NonNeg(usage.Raw("input_tokens"));
            if (inRaw == 0) inRaw = NonNeg(usage.Raw("inputTokens"));
            int outRaw = NonNeg(usage.Raw("output_tokens"));
            if (outRaw == 0) outRaw = NonNeg(usage.Raw("outputTokens"));
            int cRead = NonNeg(usage.Raw("cached_input_tokens"));
            if (cRead == 0) cRead = NonNeg(usage.Raw("cache_read_input_tokens"));
            int reasoning = NonNeg(usage.Raw("reasoning_output_tokens"));
            int cWrite = NonNeg(usage.Raw("cache_write_input_tokens"));
            if (cWrite == 0) cWrite = NonNeg(usage.Raw("cache_creation_input_tokens"));

            // 口径同参考实现：缓存在 input 内、reasoning 在 output 内，
            // 各自先减掉再加回来，于是总量 = input_tokens + output_tokens + cache_creation。
            int input = Math.Max(0, inRaw - cRead);
            int output = Math.Max(0, outRaw - reasoning);
            var tok = new Token4(input, output + reasoning, cRead, cWrite);
            int tokens = tok.Total;
            if (tokens <= 0) return null;

            if (fromTotal)
            {
                int listed = NonNeg(usage.Raw("total_tokens"));
                int now = listed > 0 ? listed : tokens;
                int prev;
                _bestTotals.TryGetValue(uuid, out prev);
                if (now < prev) prev = 0;          // 会话被重置 → 从头算
                tokens = now - prev;
                if (tokens <= 0) return null;
                _bestTotals[uuid] = now;
            }

            return new UsageEvent
            {
                Id = "every-code:" + uuid + ":" + (stamp ?? PathUtil.StableHash(line)),
                Source = UsageSource.EveryCode,
                Timestamp = ParseIso8601(stamp) ?? DateTime.Now,
                Tokens = tokens,
                Breakdown = tok.Breakdown(),
                FilePath = filePath
            };
        }
    }

    // ======================================================================
    // 刻意不采集的工具（不是漏了，是判断过不值得记）
    //
    //   Kiro / Antigravity / QwenWork
    //     这几家的本地日志里没有真实 token 元数据，官方追踪器是靠
    //     「字符数 ÷ 4」之类的启发式估算出来的。CodingFire 的火焰是给人看趋势的
    //     信号，混进估算值只会让它变成噪音，所以宁可不记。
    //
    //   需要 SQLite 且本机没有对应库的：Mimo / Kilo CLI / Hermes / Goose / Zed / Warp
    //     读取器已经有了（SqliteReader.cs），缺的只是确认各库的表结构；
    //     真要接的话在 OpencodeStyleDbAdapter 上再加子类即可。
    //
    //   Trae
    //     数据在 SQLCipher 加密库里，解密要有密钥派生实现，不引入。
    //
    //   Cursor
    //     原版 macOS 是走官方 Dashboard API 拉的，本地 state.vscdb 里没有 token 数。
    // ======================================================================
}
