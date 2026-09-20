//
//  LogWatcher.cs — CodingFire for Windows
//
//  日志变更监听：让扫描由「日志真的写了」驱动，而不是死等 4 秒轮询。
//  工具写完一行日志到火焰起反应，原来最坏要等一整个扫描周期；现在约 200ms。
//
//  三条保命规则：
//    - 去抖：一次写入会连发好几个事件（Create + Change×N），合并成一次扫描
//    - 溢出：内部缓冲满会触发 Error，此时事件已经丢了，必须退化成全量扫描，
//            绝不能静默——漏读比多读一次贵得多（增量游标保证多扫不重复计数）
//    - 降级：任何一步失败都只是「少一个监听点」，定时心跳扫描照样兜底，
//            并且下次心跳会重新尝试挂上
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace CodingFire.Data
{
    internal sealed class LogWatcher : IDisposable
    {
        /// <summary>事件合并窗口：一次写入触发的多个事件只换来一次扫描。</summary>
        private const int DebounceMs = 200;

        /// <summary>默认 8KB 缓冲在日志突发时很容易溢出；提到 64KB 再退化成全量扫描。</summary>
        private const int BufferBytes = 64 * 1024;

        private readonly Action _onChanged;
        private readonly Action _onOverflow;
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly HashSet<string> _watched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();

        private readonly Timer _debounce;
        private volatile bool _disposed;

        /// <summary>是否已有一个「待触发的合并窗口」。</summary>
        private int _pending;

        public LogWatcher(Action onChanged, Action onOverflow)
        {
            _onChanged = onChanged;
            _onOverflow = onOverflow;
            _debounce = new Timer(OnDebounce, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>已经挂上监听的根数量。0 表示监听完全没起来，只能靠心跳扫描。</summary>
        public int WatchedRoots { get { lock (_gate) { return _watchers.Count; } } }

        public int OverflowCount;

        /// <summary>
        /// 给这些根挂上监听。已经挂过的跳过；还不存在的下次调用再试
        /// （工具是后装的，等它出现的那一刻才需要监听）。
        /// </summary>
        public void Sync(IEnumerable<string> roots)
        {
            if (_disposed || roots == null) return;
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;

                string key;
                try { key = Path.GetFullPath(root).TrimEnd('\\', '/'); }
                catch (Exception) { continue; }

                lock (_gate)
                {
                    if (_watched.Contains(key)) continue;
                    _watched.Add(key);
                }

                var w = TryWatch(root);
                if (w == null)
                {
                    // 挂不上（还不存在 / 没有权限）——允许下次心跳重试
                    lock (_gate) { _watched.Remove(key); }
                    continue;
                }
                lock (_gate) { _watchers.Add(w); }
            }
        }

        private FileSystemWatcher TryWatch(string root)
        {
            try
            {
                string dir;
                if (Directory.Exists(root))
                {
                    dir = root;
                }
                else if (File.Exists(root))
                {
                    // 根是单个文件（Qoder 的 local.db 这类）。监听它所在的目录而不是
                    // 精确文件名：SQLite 的 -wal / -shm 也要算变更。
                    dir = Path.GetDirectoryName(root);
                }
                else
                {
                    return null;
                }

                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

                var w = new FileSystemWatcher(dir);
                w.Filter = "*";
                w.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                               | NotifyFilters.FileName | NotifyFilters.CreationTime;
                w.InternalBufferSize = BufferBytes;
                // 递归必须在 EnableRaisingEvents 之前设好
                w.IncludeSubdirectories = true;

                w.Created += delegate { Notify(); };
                w.Changed += delegate { Notify(); };
                w.Deleted += delegate { Notify(); };
                w.Renamed += delegate { Notify(); };
                w.Error += delegate { OnError(); };

                w.EnableRaisingEvents = true;
                return w;
            }
            catch (Exception)
            {
                // 目录被删 / 无权限 / 句柄耗尽——都不是致命错误，交给心跳兜底
                return null;
            }
        }

        private void Notify()
        {
            if (_disposed) return;

            // 只在没有待触发窗口时才重新计时。
            // 如果每个事件都 `Change(DebounceMs, …)` 重置计时器，那么持续写入
            // （日志每 <200ms 追加一行）会把回调**无限推迟** —— 恰好在最活跃、
            // 最该实时的时候最不实时。改成「首个事件后 200ms 触发」，
            // 触发前到达的事件都并进这一次。
            if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;

            try { _debounce.Change(DebounceMs, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
            catch (Exception) { }
        }

        private void OnError()
        {
            if (_disposed) return;
            Interlocked.Increment(ref OverflowCount);
            var h = _onOverflow;
            if (h != null)
            {
                try { h(); }
                catch (Exception) { }
            }
        }

        private void OnDebounce(object _)
        {
            Interlocked.Exchange(ref _pending, 0);
            if (_disposed) return;
            var h = _onChanged;
            if (h == null) return;
            try { h(); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            List<FileSystemWatcher> copy;
            lock (_gate) { copy = new List<FileSystemWatcher>(_watchers); _watchers.Clear(); }
            foreach (var w in copy)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); }
                catch (Exception) { }
            }

            try { _debounce.Dispose(); }
            catch (Exception) { }
        }
    }
}
