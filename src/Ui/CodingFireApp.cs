//
//  CodingFireApp.cs — CodingFire for Windows
//
//  应用装配：设置 → 事件库 → 扫描器 → 火焰状态机 → 悬浮窗 → 托盘图标 → 控制台。
//  所有跨线程更新都被 UsageMonitor 归拢回 UI 线程，这里不再做二次调度。
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using CodingFire.Core;
using CodingFire.Data;
using CodingFire.Fire;
using CodingFire.Ui;

namespace CodingFire.Ui
{
    internal sealed class CodingFireApp : ApplicationContext
    {
        public readonly Settings Settings;
        public readonly UsageStore Store;
        public readonly FireStateMachine Fire;
        public readonly UsageMonitor Monitor;
        public FlameForm Flame { get; private set; }

        private NotifyIcon _tray;
        private ConsoleForm _console;
        private Timer _tick;
        private bool _quitting;

        public CodingFireApp()
        {
            Dpi.Initialize();

            Settings = Settings.Load();
            L10n.Current = Settings.Language;
            SourceFlameColors.Attach(Settings);

            // 开机自启默认开：这里把设置落到注册表，失败则反过来把设置同步成真实状态，
            // 免得托盘上的勾骗人。放在 BuildTray 之前，菜单一建出来就是准的。
            SyncAutoStart();

            Store = new UsageStore();
            Store.Open();

            Fire = new FireStateMachine();
            Fire.AnimationPaused = Settings.AnimationPaused;
            // Push initial flame color into the live snapshot
            Fire.LiveSnapshot.FlameAccent = (double[])Settings.FlameColor.Clone();

            Monitor = new UsageMonitor(Store);
            Monitor.Ingest = delegate (double tokens, UsageSource? src, DateTime at, bool animate)
            {
                Fire.Ingest(tokens, src, at, animate);
            };
            Monitor.TodayTokensChanged = delegate (int total, Dictionary<UsageSource, int> bySource)
            {
                Fire.UpdateTodayTokens(total, bySource);
            };
            Monitor.Updated += delegate { /* 悬停卡片按自己的节奏刷新，这里不需要动作 */ };

            Flame = new FlameForm(Fire, Settings, BuildHoverModel);
            Flame.VisibilityRequested += SetFlameVisible;

            BuildTray();

            // 火焰状态机 20Hz（与渲染节奏一致）
            _tick = new Timer { Interval = 50 };
            _tick.Tick += delegate { Fire.Tick(); };
            _tick.Start();

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;

            if (Settings.FlameVisible) Flame.Show();
            Monitor.Start();
        }

        // ------------------------------------------------------------------
        // 托盘
        // ------------------------------------------------------------------

        private void BuildTray()
        {
            _tray = new NotifyIcon
            {
                Icon = TrayIconArt.AppIcon(),
                Text = "CodingFire",
                Visible = true
            };
            _tray.DoubleClick += delegate { OpenConsole(); };
            RebuildTrayMenu();
        }

        public void RebuildTrayMenu()
        {
            if (_tray == null) return;
            var menu = new ContextMenuStrip();

            var showHide = new ToolStripMenuItem(Settings.FlameVisible ? L10n.T("menu.hideFlame") : L10n.T("menu.show"));
            showHide.Click += delegate { SetFlameVisible(!Settings.FlameVisible); };
            menu.Items.Add(showHide);

            var pause = new ToolStripMenuItem(L10n.T("menu.togglePause"))
            {
                CheckOnClick = true,
                Checked = Settings.AnimationPaused
            };
            pause.Click += delegate { SetPaused(pause.Checked); };
            menu.Items.Add(pause);

            var autoStart = new ToolStripMenuItem(L10n.T("menu.autoStart"))
            {
                CheckOnClick = true,
                Checked = Settings.AutoStart
            };
            autoStart.Click += delegate { SetAutoStart(autoStart.Checked); };
            menu.Items.Add(autoStart);

            menu.Items.Add(new ToolStripSeparator());

            var sizeMenu = new ToolStripMenuItem(L10n.T("menu.size"));
            foreach (var size in FlameSizes.All)
            {
                var captured = size;
                var item = new ToolStripMenuItem(size.Label())
                {
                    Checked = Settings.Size == size,
                    CheckOnClick = false
                };
                item.Click += delegate { SetFlameSize(captured); };
                sizeMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(sizeMenu);

            var langMenu = new ToolStripMenuItem(L10n.T("menu.language"));
            var langs = new[]
            {
                new KeyValuePair<AppLanguage, string>(AppLanguage.System, L10n.T("settings.lang.system")),
                new KeyValuePair<AppLanguage, string>(AppLanguage.English, "English"),
                new KeyValuePair<AppLanguage, string>(AppLanguage.ChineseSimplified, "简体中文"),
                new KeyValuePair<AppLanguage, string>(AppLanguage.Japanese, "日本語"),
                new KeyValuePair<AppLanguage, string>(AppLanguage.Korean, "한국어")
            };
            foreach (var kv in langs)
            {
                var captured = kv.Key;
                var item = new ToolStripMenuItem(kv.Value) { Checked = Settings.Language == kv.Key };
                item.Click += delegate { SetLanguage(captured); };
                langMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(langMenu);

            menu.Items.Add(new ToolStripSeparator());

            var reset = new ToolStripMenuItem(L10n.T("menu.resetPosition"));
            reset.Click += delegate { Flame.PlaceDefault(); Settings.Save(); };
            menu.Items.Add(reset);

            var console = new ToolStripMenuItem(L10n.T("menu.console"));
            console.Click += delegate { OpenConsole(); };
            menu.Items.Add(console);

            menu.Items.Add(new ToolStripSeparator());

            var quit = new ToolStripMenuItem(L10n.T("menu.quit"));
            quit.Click += delegate { Quit(); };
            menu.Items.Add(quit);

            var old = _tray.ContextMenuStrip;
            _tray.ContextMenuStrip = menu;
            if (old != null) old.Dispose();
        }

        // ------------------------------------------------------------------
        // 动作
        // ------------------------------------------------------------------

        public void SetFlameVisible(bool visible)
        {
            Settings.FlameVisible = visible;
            Settings.Save();
            Flame.SetVisible(visible);
            RebuildTrayMenu();
            if (_console != null) _console.RefreshData();
        }

        public void SetPaused(bool paused)
        {
            Settings.AnimationPaused = paused;
            Settings.Save();
            Fire.AnimationPaused = paused;
            RebuildTrayMenu();
        }

        /// <summary>托盘菜单里勾/取消「开机自启」。</summary>
        public void SetAutoStart(bool enabled)
        {
            Settings.AutoStart = enabled;
            Settings.Save();
            SyncAutoStart();
            RebuildTrayMenu();
        }

        /// <summary>
        /// 把设置里的自启状态落到注册表。写不进去（组策略、权限、注册表被锁）时，
        /// 反过来把设置同步成注册表的真实状态 —— 菜单上的勾必须反映事实。
        /// </summary>
        private void SyncAutoStart()
        {
            if (AutoStart.Apply(Settings.AutoStart)) return;

            bool real = AutoStart.IsEnabled();
            if (real == Settings.AutoStart) return;

            Log.Warn("autostart could not be applied; settings synced to actual state: "
                     + (real ? "on" : "off"));
            Settings.AutoStart = real;
            Settings.Save();
        }

        public void SetFlameSize(FlameSize size)
        {
            Flame.ApplySize(size);
            Settings.Save();
            Flame.ConstrainToScreen();
            RebuildTrayMenu();
        }

        public void SetFlameColor(double r, double g, double b)
        {
            Settings.FlameColor[0] = Math.Max(0, Math.Min(1, r));
            Settings.FlameColor[1] = Math.Max(0, Math.Min(1, g));
            Settings.FlameColor[2] = Math.Max(0, Math.Min(1, b));
            Settings.Save();
            // Push to engine via epoch bump + snapshot
            Fire.LiveSnapshot.FlameAccent = (double[])Settings.FlameColor.Clone();
            Fire.BumpPaletteEpoch();
        }

        public void SetLanguage(AppLanguage lang)
        {
            Settings.Language = lang;
            Settings.Save();
            L10n.Current = lang;
            RebuildTrayMenu();
            if (_console != null) { /* 控制台订阅了 LanguageChanged，会自己重译 */ }
        }

        public void ResetColors()
        {
            SourceFlameColors.ResetAll();
            Settings.Save();
            Fire.NotifyColorsChanged();
        }

        public void Rescan() { Monitor.Rescan(); }

        public void OpenConsole()
        {
            if (_console == null || _console.IsDisposed)
            {
                _console = new ConsoleForm(this);
                _console.FormClosed += delegate { _console = null; };
            }
            _console.Show();
            if (_console.WindowState == FormWindowState.Minimized) _console.WindowState = FormWindowState.Normal;
            _console.Activate();
            _console.RefreshData();
        }

        public HoverModel BuildHoverModel()
        {
            var model = new HoverModel
            {
                TodayTokens = Monitor.TodayTokens,
                TokensPerSecond = Fire.TokensPerSecond,
                ShowLiveRate = Settings.ShowLiveRate
            };

            DateTime? latest = null;
            var statuses = Monitor.Statuses;
            if (statuses != null)
            {
                foreach (var st in statuses)
                {
                    if (st.LastReadAt.HasValue && (!latest.HasValue || st.LastReadAt.Value > latest.Value))
                        latest = st.LastReadAt;
                }
            }
            model.UpdatedAt = latest;

            // Cursor 是本地估算时，卡片上标注出来
            bool cursorEstimated = false;
            if (statuses != null)
            {
                foreach (var st in statuses)
                {
                    if (st.Source != UsageSource.Cursor) continue;
                    cursorEstimated = st.Detail != null
                        && st.Detail.IndexOf("estimate", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }

            if (Monitor.TodayBySource != null)
            {
                foreach (var src in UsageSources.All)
                {
                    int tokens;
                    if (!Monitor.TodayBySource.TryGetValue(src, out tokens) || tokens <= 0) continue;
                    model.Rows.Add(new HoverRow
                    {
                        Source = src,
                        Tokens = tokens,
                        Estimated = src == UsageSource.Cursor && cursorEstimated
                    });
                }
            }
            // 卡片上按用量从多到少排，读起来更顺
            model.Rows.Sort(delegate (HoverRow a, HoverRow b) { return b.Tokens.CompareTo(a.Tokens); });
            return model;
        }

        // ------------------------------------------------------------------

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                Fire.ApplySleepGap(60);
                Monitor.Rescan();
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                Monitor.Rescan();
            }
        }

        public void Quit()
        {
            if (_quitting) return;
            _quitting = true;

            try
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
            }
            catch (Exception) { }

            if (_tick != null) { _tick.Stop(); _tick.Dispose(); _tick = null; }
            if (Monitor != null) Monitor.Stop();
            Store.Flush();
            Store.Close();

            if (_console != null) { _console.Dispose(); _console = null; }
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            if (Flame != null) Flame.Dispose();

            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Quit();
            base.Dispose(disposing);
        }
    }
}
