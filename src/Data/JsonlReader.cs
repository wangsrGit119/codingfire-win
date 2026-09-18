//
//  JsonlReader.cs — CodingFire for Windows
//
//  JSONL 增量读取：按字节游标 seek，只读新增部分；跨块不完整的行留到下次。
//  与 macOS 版 UsageMonitor.readJSONLDetached 语义一致，包含两个自愈分支：
//    - 文件被截断（游标 > 文件大小）→ 从头重读
//    - 游标卡在 EOF 但该文件从未产出过任何事件 → 回退到 0（修「一直 0 token」的老问题）
//
//  与 Swift 版的差别：以「字节」而不是「字符串」做断行缓冲，
//  这样多字节 UTF-8 字符被块边界切开时不会产生乱码。
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CodingFire.Core;

namespace CodingFire.Data
{
    internal static class JsonlReader
    {
        private const int ChunkSize = 256 * 1024;

        public static List<UsageEvent> ReadNew(
            string path,
            UsageStore store,
            bool fromStart,
            Func<string, string, UsageEvent> parse)
        {
            var events = new List<UsageEvent>();
            if (parse == null) return events;

            long cursorOffset;
            string cursorPartial;
            store.FileCursor(path, out cursorOffset, out cursorPartial);

            long size;
            try { size = new FileInfo(path).Length; }
            catch (Exception) { return events; }

            bool truncated = cursorOffset > size;
            bool stuckAtEof = !fromStart && size > 0 && cursorOffset >= size && !store.HasEventsForFilePath(path);

            long offset = cursorOffset;
            byte[] carry = new byte[0];
            if (fromStart || truncated || stuckAtEof)
            {
                offset = 0;
            }
            else if (!string.IsNullOrEmpty(cursorPartial))
            {
                try { carry = Convert.FromBase64String(cursorPartial); }
                catch (Exception) { carry = new byte[0]; }
            }

            FileStream fs;
            try
            {
                // FileShare.ReadWrite：日志正在被写入，必须允许共享
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception)
            {
                return events;
            }

            using (fs)
            {
                if (offset > fs.Length) offset = 0;
                try { fs.Seek(offset, SeekOrigin.Begin); }
                catch (Exception) { return events; }

                var buf = new ByteBuf(carry);
                byte[] chunk = new byte[ChunkSize];

                while (true)
                {
                    int read;
                    try { read = fs.Read(chunk, 0, chunk.Length); }
                    catch (Exception) { break; }
                    if (read <= 0) break;
                    buf.Append(chunk, read);

                    int start = 0;
                    for (int i = 0; i < buf.Count; i++)
                    {
                        if (buf.At(i) != (byte)'\n') continue;
                        int len = i - start;
                        if (len > 0 && buf.At(i - 1) == (byte)'\r') len--;
                        if (len > 0)
                        {
                            string line = buf.GetString(start, len);
                            var ev = parse(line, path);
                            if (ev != null) events.Add(ev);
                        }
                        start = i + 1;
                    }
                    if (start > 0) buf.Consume(start);
                    // 防御：单行超过 8MB 说明不是我们的格式，直接丢弃避免吃光内存
                    if (buf.Count > 8 * 1024 * 1024) buf.Consume(buf.Count);
                }

                long newOffset = offset;
                try { newOffset = fs.Position; }
                catch (Exception) { }

                string newPartial = buf.Count > 0 ? Convert.ToBase64String(buf.ToArray()) : null;
                store.SetFileCursor(path, newOffset, newPartial);
            }

            return events;
        }

        /// <summary>可增长字节缓冲，避免逐行 ToArray 造成的大量小对象。</summary>
        private sealed class ByteBuf
        {
            private byte[] _data;
            private int _count;

            public ByteBuf(byte[] initial)
            {
                _data = new byte[Math.Max(ChunkSize, initial != null ? initial.Length : 0)];
                if (initial != null && initial.Length > 0)
                {
                    Buffer.BlockCopy(initial, 0, _data, 0, initial.Length);
                    _count = initial.Length;
                }
            }

            public int Count { get { return _count; } }

            public byte At(int i) { return _data[i]; }

            public void Append(byte[] src, int len)
            {
                if (_count + len > _data.Length)
                {
                    int size = _data.Length;
                    while (size < _count + len) size *= 2;
                    var next = new byte[size];
                    Buffer.BlockCopy(_data, 0, next, 0, _count);
                    _data = next;
                }
                Buffer.BlockCopy(src, 0, _data, _count, len);
                _count += len;
            }

            public string GetString(int start, int len)
            {
                return Encoding.UTF8.GetString(_data, start, len);
            }

            public void Consume(int n)
            {
                if (n <= 0) return;
                if (n >= _count) { _count = 0; return; }
                Buffer.BlockCopy(_data, n, _data, 0, _count - n);
                _count -= n;
            }

            public byte[] ToArray()
            {
                var copy = new byte[_count];
                Buffer.BlockCopy(_data, 0, copy, 0, _count);
                return copy;
            }
        }
    }
}
