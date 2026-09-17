//
//  SqliteReader.cs — TinyFire for Windows
//
//  极简只读 SQLite 读取器。
//
//  动机：不少 AI 编程工具（ZCode / OpenCode / Cursor / Goose / Zed / Warp…）把用量
//  记在 SQLite 里。原版 TinyFire 完全不含 SQLite（它只读 JSONL），而本程序刻意
//  「零外部依赖、免安装单文件」，不能引入 System.Data.SQLite 的原生 dll，
//  所以这里手写一个只做「顺序读表」的最小实现：
//
//    - 只处理表 b-tree（叶子 0x0D / 内部 0x05），索引页直接跳过
//    - 支持溢出页（payload 超过局部容量时沿链读）
//    - 支持 WAL：叠加 -wal 里已提交的前缀（不回放未提交帧）
//    - 不执行 SQL：schema 从 sqlite_master 里把列名解析出来，按名字取列
//
//  不做的事：写、事务、索引、校验和验证、页缓存淘汰。
//  校验和为了省事没验，所以「正在写入的最后一个未提交帧」理论上可能被误读；
//  已用「只认到最后一个 commit 帧为止」的前缀截断来规避绝大多数情况。
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TinyFire.Core;

namespace TinyFire.Data
{
    /// <summary>一张表的 schema 信息。</summary>
    internal sealed class SqliteTable
    {
        public string Name = "";
        public int RootPage;
        public string Sql = "";
        public readonly List<string> Columns = new List<string>();

        public int IndexOf(string column)
        {
            for (int i = 0; i < Columns.Count; i++)
                if (string.Equals(Columns[i], column, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }

    internal sealed class SqliteDb : IDisposable
    {
        private FileStream _fs;
        private readonly Dictionary<int, byte[]> _pages = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, byte[]> _wal = new Dictionary<int, byte[]>();
        private readonly Dictionary<string, SqliteTable> _tables =
            new Dictionary<string, SqliteTable>(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public int PageSize = 4096;
        public int ReservedSpace;

        private int Usable { get { return PageSize - ReservedSpace; } }

        // ------------------------------------------------------------------
        // 打开
        // ------------------------------------------------------------------

        public static SqliteDb Open(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var db = new SqliteDb();
            try
            {
                // 数据库正被别的进程写着，必须共享打开
                db._fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                        FileShare.ReadWrite | FileShare.Delete);
                if (!db.ReadHeader()) { db.Dispose(); return null; }
                db.ReadWal(path);
                db.LoadSchema();
                return db;
            }
            catch (Exception)
            {
                db.Dispose();
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_fs != null) _fs.Dispose(); } catch (Exception) { }
            _fs = null;
            _pages.Clear();
            _wal.Clear();
        }

        private bool ReadHeader()
        {
            var h = new byte[100];
            if (!ReadAt(h, 0, 100)) return false;
            // "SQLite format 3\0"
            const string magic = "SQLite format 3";
            for (int i = 0; i < magic.Length; i++)
                if (h[i] != (byte)magic[i]) return false;

            int ps = Be16(h, 16);
            PageSize = (ps == 1) ? 65536 : ps;
            if (PageSize < 512 || (PageSize & (PageSize - 1)) != 0) return false;
            ReservedSpace = h[20];
            if (ReservedSpace >= PageSize) return false;
            return true;
        }

        private bool ReadAt(byte[] buf, long offset, int count)
        {
            try
            {
                _fs.Seek(offset, SeekOrigin.Begin);
                int done = 0;
                while (done < count)
                {
                    int n = _fs.Read(buf, done, count - done);
                    if (n <= 0) return false;
                    done += n;
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------
        // WAL
        // ------------------------------------------------------------------

        /// <summary>
        /// 叠加 WAL 里「已提交」的页。规则：扫描所有帧，记下最后一个
        /// commit-size 非零的帧位置，只应用到那里为止——之后的帧属于尚未提交的事务。
        /// </summary>
        private void ReadWal(string dbPath)
        {
            string walPath = dbPath + "-wal";
            FileStream fs = null;
            try
            {
                if (!File.Exists(walPath)) return;
                fs = new FileStream(walPath, FileMode.Open, FileAccess.Read,
                                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception) { return; }

            try
            {
                long len = fs.Length;
                if (len < 32) return;

                var hdr = new byte[32];
                fs.Seek(0, SeekOrigin.Begin);
                if (fs.Read(hdr, 0, 32) != 32) return;

                uint magic = Be32(hdr, 0);
                if (magic != 0x377f0682 && magic != 0x377f0683) return;

                int walPageSize = (int)Be32(hdr, 8);
                if (walPageSize <= 0) walPageSize = PageSize;

                uint salt1 = Be32(hdr, 16);
                uint salt2 = Be32(hdr, 20);

                long frameSize = 24 + (long)walPageSize;
                long frameCount = (len - 32) / frameSize;
                if (frameCount <= 0) return;

                var frameHdr = new byte[24];
                var pageBuf = new byte[walPageSize];
                long lastCommit = -1;
                var pending = new List<KeyValuePair<int, byte[]>>();

                for (long i = 0; i < frameCount; i++)
                {
                    long off = 32 + i * frameSize;
                    fs.Seek(off, SeekOrigin.Begin);
                    if (fs.Read(frameHdr, 0, 24) != 24) break;

                    uint fSalt1 = Be32(frameHdr, 8);
                    uint fSalt2 = Be32(frameHdr, 12);
                    if (fSalt1 != salt1 || fSalt2 != salt2) break; // 属于上一轮 WAL

                    int pageNo = (int)Be32(frameHdr, 0);
                    uint commitSize = Be32(frameHdr, 4);
                    if (pageNo <= 0) break;

                    if (fs.Read(pageBuf, 0, walPageSize) != walPageSize) break;

                    var copy = new byte[walPageSize];
                    Buffer.BlockCopy(pageBuf, 0, copy, 0, walPageSize);
                    pending.Add(new KeyValuePair<int, byte[]>(pageNo, copy));

                    if (commitSize != 0)
                    {
                        lastCommit = pending.Count - 1;
                        for (int k = 0; k <= lastCommit; k++)
                            _wal[pending[k].Key] = pending[k].Value;
                        pending.Clear();
                        lastCommit = -1;
                    }
                }
                // pending 里剩下的都是未提交帧 —— 故意丢弃
            }
            catch (Exception) { }
            finally { try { fs.Dispose(); } catch (Exception) { } }
        }

        // ------------------------------------------------------------------
        // 页读取
        // ------------------------------------------------------------------

        private byte[] ReadPage(int pageNo)
        {
            if (pageNo <= 0) return null;
            byte[] cached;
            if (_wal.TryGetValue(pageNo, out cached)) return cached;
            if (_pages.TryGetValue(pageNo, out cached)) return cached;

            var buf = new byte[PageSize];
            long offset = ((long)pageNo - 1) * PageSize;
            if (!ReadAt(buf, offset, PageSize)) return null;
            _pages[pageNo] = buf;
            return buf;
        }

        // ------------------------------------------------------------------
        // schema
        // ------------------------------------------------------------------

        private void LoadSchema()
        {
            // sqlite_master 的根就是第 1 页，记录为 (type,name,tbl_name,rootpage,sql)
            var leaves = new List<int>();
            CollectLeaves(1, leaves, new HashSet<int>(), 0);

            foreach (int lp in leaves)
            {
                byte[] page = ReadPage(lp);
                if (page == null) continue;

                // 关键：第 1 页的前 100 字节是文件头，b-tree 头从 100 开始。
                // 小库（表少到 sqlite_master 的根就是页 1，且是叶子）如果按偏移 0 读，
                // 读到的会是大写 'S'（0x53），于是所有表名都被跳过——整个库看起来是空的。
                int b = HeaderBase(lp);
                if (b + 8 > page.Length) continue;
                int type = page[b];
                if (type != 0x0D) continue;
                int n = Be16(page, b + 3);
                for (int i = 0; i < n; i++)
                {
                    int co = Be16(page, b + 8 + i * 2);
                    object[] rec = ReadLeafCell(page, co);
                    if (rec == null || rec.Length < 5) continue;
                    string kind = rec[0] as string;
                    if (kind == null || kind != "table") continue;
                    string name = rec[1] as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name.StartsWith("sqlite_", StringComparison.Ordinal)) continue;

                    var t = new SqliteTable();
                    t.Name = name;
                    t.RootPage = ToInt(rec[3]);
                    t.Sql = rec[4] as string ?? "";
                    ParseColumns(t);
                    _tables[name] = t;
                }
            }
        }

        /// <summary>
        /// 从 CREATE TABLE 语句里抠出列名。只认顶层逗号分隔的定义，
        /// 跳过 CONSTRAINT / PRIMARY / UNIQUE / CHECK / FOREIGN 这类表级约束。
        /// </summary>
        private static void ParseColumns(SqliteTable t)
        {
            string sql = t.Sql;
            int open = sql.IndexOf('(');
            if (open < 0) return;

            var sb = new StringBuilder();
            int depth = 0;
            bool inStr = false;
            char quote = '\0';
            var defs = new List<string>();

            for (int i = open + 1; i < sql.Length; i++)
            {
                char c = sql[i];
                if (inStr)
                {
                    sb.Append(c);
                    if (c == quote) inStr = false;
                    continue;
                }
                if (c == '\'' || c == '"' || c == '`') { inStr = true; quote = c; sb.Append(c); continue; }
                if (c == '(') { depth++; sb.Append(c); continue; }
                if (c == ')')
                {
                    if (depth == 0) { defs.Add(sb.ToString()); break; }
                    depth--; sb.Append(c); continue;
                }
                if (c == ',' && depth == 0) { defs.Add(sb.ToString()); sb.Length = 0; continue; }
                sb.Append(c);
            }

            foreach (string raw in defs)
            {
                string def = raw.Trim();
                if (def.Length == 0) continue;

                string head = FirstToken(def);
                if (head.Length == 0) continue;

                string up = head.ToUpperInvariant();
                if (up == "CONSTRAINT" || up == "PRIMARY" || up == "UNIQUE" ||
                    up == "CHECK" || up == "FOREIGN" || up == "KEY") continue;

                t.Columns.Add(Unquote(head));
            }
        }

        private static string FirstToken(string s)
        {
            s = s.Trim();
            if (s.Length == 0) return "";
            char q = s[0];
            if (q == '"' || q == '`' || q == '[')
            {
                char close = (q == '[') ? ']' : q;
                int e = s.IndexOf(close, 1);
                return e < 0 ? s.Substring(1) : s.Substring(0, e + 1);
            }
            int sp = 0;
            while (sp < s.Length && !char.IsWhiteSpace(s[sp]) && s[sp] != '(') sp++;
            return s.Substring(0, sp);
        }

        private static string Unquote(string s)
        {
            if (s.Length >= 2)
            {
                char f = s[0], l = s[s.Length - 1];
                if ((f == '"' && l == '"') || (f == '`' && l == '`') || (f == '[' && l == ']'))
                    return s.Substring(1, s.Length - 2);
            }
            return s;
        }

        // ------------------------------------------------------------------
        // b-tree 遍历
        // ------------------------------------------------------------------

        /// <summary>页 1 的 b-tree 头从偏移 100 开始（前 100 字节是文件头）。</summary>
        private static int HeaderBase(int pageNo) { return pageNo == 1 ? 100 : 0; }

        private void CollectLeaves(int pageNo, List<int> outList, HashSet<int> visited, int depth)
        {
            if (pageNo <= 0 || depth > 64 || !visited.Add(pageNo)) return;
            byte[] page = ReadPage(pageNo);
            if (page == null) return;

            int b = HeaderBase(pageNo);
            if (b + 8 > page.Length) return;
            int type = page[b];

            if (type == 0x0D) { outList.Add(pageNo); return; }
            if (type != 0x05) return; // 索引页等一律忽略

            int n = Be16(page, b + 3);
            int ptr = b + 12;
            for (int i = 0; i < n; i++)
            {
                if (ptr + 2 > page.Length) break;
                int co = Be16(page, ptr);
                ptr += 2;
                if (co + 4 > page.Length) continue;
                CollectLeaves((int)Be32(page, co), outList, visited, depth + 1);
            }
            CollectLeaves((int)Be32(page, b + 8), outList, visited, depth + 1);
        }

        private object[] ReadLeafCell(byte[] page, int offset)
        {
            if (offset <= 0 || offset >= page.Length) return null;

            int pos = offset;
            long payloadLen = ReadVarint(page, ref pos);
            ReadVarint(page, ref pos); // rowid，用不到

            if (payloadLen < 0 || payloadLen > 64L * 1024 * 1024) return null;

            int U = Usable;
            int X = U - 35;
            int local;
            if (payloadLen <= X) local = (int)payloadLen;
            else
            {
                int M = ((U - 12) * 32 / 255) - 23;
                long K = M + ((payloadLen - M) % (U - 4));
                local = (int)(K <= X ? K : M);
            }
            if (local < 0) local = 0;

            var body = new byte[payloadLen];
            int copied = Math.Min(local, (int)payloadLen);
            if (pos + copied > page.Length) return null;
            Buffer.BlockCopy(page, pos, body, 0, copied);

            if (payloadLen > local)
            {
                int next = (pos + local + 4 <= page.Length) ? (int)Be32(page, pos + local) : 0;
                int remaining = (int)payloadLen - local;
                int guard = 0;
                while (next > 0 && remaining > 0 && guard++ < 200000)
                {
                    byte[] op = ReadPage(next);
                    if (op == null) break;
                    int chunk = Math.Min(U - 4, remaining);
                    if (chunk <= 0) break;
                    Buffer.BlockCopy(op, 4, body, (int)payloadLen - remaining, chunk);
                    remaining -= chunk;
                    next = (int)Be32(op, 0);
                }
                if (remaining > 0) return null; // 链断了，宁可不返回也不返回半条
            }

            return DecodeRecord(body);
        }

        // ------------------------------------------------------------------
        // 记录解码
        // ------------------------------------------------------------------

        private static object[] DecodeRecord(byte[] b)
        {
            int pos = 0;
            long headerSize = ReadVarint(b, ref pos);
            if (headerSize <= 0 || headerSize > b.Length) return null;
            int headerEnd = (int)headerSize;

            var serials = new List<long>();
            int p = pos;
            while (p < headerEnd)
            {
                long st = ReadVarint(b, ref p);
                serials.Add(st);
                if (serials.Count > 4096) break;
            }

            var values = new object[serials.Count];
            int vp = headerEnd;
            for (int i = 0; i < serials.Count; i++)
            {
                long st = serials[i];
                object v;
                int need;
                switch (st)
                {
                    case 0: v = null; need = 0; break;
                    case 1: case 2: case 3: case 4: case 5: case 6:
                        need = st == 1 ? 1 : st == 2 ? 2 : st == 3 ? 3 : st == 4 ? 4 : st == 5 ? 6 : 8;
                        v = (vp + need <= b.Length) ? (object)ReadBigEndianInt(b, vp, need) : null;
                        break;
                    case 7:
                        need = 8;
                        v = (vp + 8 <= b.Length) ? (object)BitConverter.ToDouble(Reverse8(b, vp), 0) : null;
                        break;
                    case 8: v = 0L; need = 0; break;
                    case 9: v = 1L; need = 0; break;
                    case 10: case 11: v = null; need = 0; break;
                    default:
                        if (st >= 12 && (st % 2) == 0)
                        {
                            need = (int)((st - 12) / 2);
                            if (vp + need > b.Length) { v = null; need = 0; }
                            else { var blob = new byte[need]; Buffer.BlockCopy(b, vp, blob, 0, need); v = blob; }
                        }
                        else if (st >= 13)
                        {
                            need = (int)((st - 13) / 2);
                            if (vp + need > b.Length) { v = null; need = 0; }
                            else v = Encoding.UTF8.GetString(b, vp, need);
                        }
                        else { v = null; need = 0; }
                        break;
                }
                values[i] = v;
                vp += need;
            }
            return values;
        }

        private static byte[] Reverse8(byte[] b, int off)
        {
            var r = new byte[8];
            for (int i = 0; i < 8; i++) r[i] = b[off + 7 - i];
            return r;
        }

        private static long ReadBigEndianInt(byte[] b, int off, int n)
        {
            long v = b[off];
            for (int i = 1; i < n; i++) v = (v << 8) | b[off + i];
            return v;
        }

        /// <summary>SQLite 变长整数：大端、每字节 7 位、最多 9 字节。</summary>
        private static long ReadVarint(byte[] b, ref int pos)
        {
            long v = 0;
            int i = 0;
            for (; i < 8; i++)
            {
                if (pos >= b.Length) return v;
                byte c = b[pos++];
                v = (v << 7) | (uint)(c & 0x7F);
                if ((c & 0x80) == 0) return v;
            }
            if (pos < b.Length) { byte c = b[pos++]; v = (v << 8) | c; }
            return v;
        }

        private static int Be16(byte[] b, int o)
        {
            if (o + 2 > b.Length) return 0;
            return (b[o] << 8) | b[o + 1];
        }

        private static uint Be32(byte[] b, int o)
        {
            if (o + 4 > b.Length) return 0;
            return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        }

        private static int ToInt(object v)
        {
            if (v is long) return (int)Math.Min((long)v, int.MaxValue);
            if (v is double) return (int)(double)v;
            return 0;
        }

        // ------------------------------------------------------------------
        // 对外查询
        // ------------------------------------------------------------------

        public bool HasTable(string name) { return _tables.ContainsKey(name); }

        public SqliteTable Table(string name)
        {
            SqliteTable t;
            return _tables.TryGetValue(name, out t) ? t : null;
        }

        /// <summary>顺序读出整张表（行数都不大，直接物化比流式简单）。</summary>
        public List<object[]> Rows(string tableName)
        {
            var result = new List<object[]>();
            SqliteTable t = Table(tableName);
            if (t == null || t.RootPage <= 0) return result;

            var leaves = new List<int>();
            CollectLeaves(t.RootPage, leaves, new HashSet<int>(), 0);
            foreach (int lp in leaves)
            {
                byte[] page = ReadPage(lp);
                if (page == null) continue;
                int b = HeaderBase(lp);
                if (b + 8 > page.Length || page[b] != 0x0D) continue;
                int n = Be16(page, b + 3);
                for (int i = 0; i < n; i++)
                {
                    int ptr = b + 8 + i * 2;
                    if (ptr + 2 > page.Length) break;
                    object[] rec = ReadLeafCell(page, Be16(page, ptr));
                    if (rec != null) result.Add(rec);
                }
            }
            return result;
        }

        public static string Text(object[] row, int index)
        {
            if (row == null || index < 0 || index >= row.Length) return null;
            return row[index] as string;
        }

        public static long Number(object[] row, int index)
        {
            if (row == null || index < 0 || index >= row.Length) return 0;
            object v = row[index];
            if (v is long) return (long)v;
            if (v is double) return (long)(double)v;
            return 0;
        }
    }
}
