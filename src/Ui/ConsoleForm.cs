//
//  ConsoleForm.cs — TinyFire for Windows
//
//  控制台：统计 / 数据源 / 设置 / 关于。对应 macOS 版的 Console 窗口。
//  全部控件代码构建（无 designer），方便单文件编译。
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using TinyFire.Core;
using TinyFire.Fire;

namespace TinyFire.Ui
{
    internal sealed class ConsoleForm : Form
    {
        private readonly TinyFireApp _app;

        private TabControl _tabs;
        private TabPage _tabStats, _tabSources, _tabSettings, _tabAbout;

        // 统计页
        private Label _lblTodayCaption, _lblTierCaption, _lblBreakdownCaption, _lblTimelineCaption, _lblBySourceCaption;
        private Label _lblTodayValue, _lblTierValue, _lblPeakValue;
        private SourceBars _bars;
        private UsageBars _breakdownBars;
        private HourlyChart _chart;
        private FirePreviewBox _preview;
        private FlowLayoutPanel _flameColorsRow;
        private ToolTip _toolTip = new ToolTip();
        private Panel[] _colorCircles;
        private Panel _previewButtons;
        private Button _btnLive;

        // 数据源页
        private ListView _sources;
        private Button _btnRescan;
        private Label _lblNoSource;

        // 设置页
        private Label _lblFlame, _lblSize, _lblLanguage, _lblFlameColor;
        private ComboBox _cmbSize, _cmbLanguage;
        private CheckBox _chkVisible, _chkRate;

        private Button _btnResetPos;

        private Timer _refresh;

        public ConsoleForm(TinyFireApp app)
        {
            _app = app;
            BuildUi();
            Retranslate();
            RefreshData();
            LanguageChanged();
            L10n.LanguageChanged += LanguageChanged;

            _refresh = new Timer();
            _refresh.Interval = 900;
            _refresh.Tick += delegate { if (Visible) RefreshData(); };
            _refresh.Start();
        }

        private void LanguageChanged()
        {
            Retranslate();
            _app.Flame.RebuildMenu();
        }

        private void Retranslate()
        {
            Text = L10n.T("console.title");

            _tabSources.Text = L10n.T("console.tab.sources");
            _tabSettings.Text = L10n.T("console.tab.settings");
            _tabAbout.Text = L10n.T("console.tab.about");

            // Stats tab removed; skip its labels (fields are null)
            if (_lblTodayCaption != null) _lblTodayCaption.Text = L10n.T("stats.today");
            if (_lblTierCaption != null) _lblTierCaption.Text = L10n.T("stats.tier");
            if (_lblBySourceCaption != null) _lblBySourceCaption.Text = L10n.T("stats.bySource");
            if (_lblBreakdownCaption != null) _lblBreakdownCaption.Text = L10n.T("stats.breakdown");
            if (_lblTimelineCaption != null) _lblTimelineCaption.Text = L10n.T("stats.timeline");

            // 注意：_btnLive 在 RebuildPreviewButtons() 里才创建（本方法末尾会调它），
            // 这里不能再对它赋值 —— 首次构造时它还是 null（双击托盘打开控制台就炸在这）。

            _btnRescan.Text = L10n.T("sources.rescan");
            _lblNoSource.Text = L10n.T("hover.none");
            _sources.Columns[0].Text = L10n.T("sources.source");
            _sources.Columns[1].Text = L10n.T("sources.state");
            _sources.Columns[2].Text = L10n.T("sources.today");
            _sources.Columns[3].Text = L10n.T("sources.path");

            _lblFlame.Text = L10n.T("settings.flame");
            _lblSize.Text = L10n.T("settings.flameSize");
            _lblLanguage.Text = L10n.T("settings.language");
            _lblFlameColor.Text = L10n.T("settings.flameColor");
            _chkVisible.Text = L10n.T("settings.visible");
            _chkRate.Text = L10n.T("settings.showRate");
            _btnResetPos.Text = L10n.T("settings.resetPosition");

            // 组合框选项要重建才能跟着换语言
            int sizeIdx = _cmbSize.SelectedIndex;
            _cmbSize.Items.Clear();
            foreach (var s in FlameSizes.All) _cmbSize.Items.Add(s.Label());
            _cmbSize.SelectedIndex = sizeIdx < 0 ? 1 : sizeIdx;

            int langIdx = _cmbLanguage.SelectedIndex;
            _cmbLanguage.Items.Clear();
            _cmbLanguage.Items.Add(L10n.T("settings.lang.system"));
            _cmbLanguage.Items.Add("English");
            _cmbLanguage.Items.Add("简体中文");
            _cmbLanguage.Items.Add("日本語");
            _cmbLanguage.Items.Add("한국어");
            _cmbLanguage.SelectedIndex = langIdx < 0 ? 0 : langIdx;

            RebuildPreviewButtons();
        }

        // ------------------------------------------------------------------
        // 构建
        // ------------------------------------------------------------------

        private void BuildUi()
        {
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(640, 470);
            MinimumSize = new Size(560, 420);
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            ShowInTaskbar = true;
            Icon = TrayIconArt.AppIcon();

            _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
            Controls.Add(_tabs);

            _tabStats = new TabPage();
            _tabSources = new TabPage();
            _tabSettings = new TabPage();
            _tabAbout = new TabPage();
            _tabs.TabPages.AddRange(new TabPage[] { _tabSources, _tabSettings, _tabAbout });

            BuildSourcesTab();
            BuildSettingsTab();
            BuildAboutTab();
        }

        private void BuildStatsTab()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                ColumnCount = 2,
                RowCount = 4
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
            _tabStats.Controls.Add(root);

            // Today total + tier card (dark themed)
            var head = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 24, 22) };
            _lblTodayCaption = new Label { AutoSize = true, Location = new Point(14, 8), ForeColor = Color.FromArgb(140, 120, 100) };
            _lblTodayValue = new Label
            {
                AutoSize = true,
                Location = new Point(10, 22),
                Font = new Font("Segoe UI", 26f, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 180, 80)
            };
            _lblTierCaption = new Label { AutoSize = true, Location = new Point(240, 10), ForeColor = Color.FromArgb(130, 110, 90) };
            _lblTierValue = new Label { AutoSize = true, Location = new Point(240, 28), Font = new Font("Segoe UI", 13f, FontStyle.Bold), ForeColor = Color.FromArgb(255, 200, 120) };
            _lblPeakValue = new Label { AutoSize = true, Location = new Point(240, 54), ForeColor = Color.FromArgb(120, 100, 80) };
            head.Controls.Add(_lblTodayCaption);
            head.Controls.Add(_lblTodayValue);
            head.Controls.Add(_lblTierCaption);
            head.Controls.Add(_lblTierValue);
            head.Controls.Add(_lblPeakValue);
            root.Controls.Add(head, 0, 0);

            // 火势预览
            var previewPanel = new Panel { Dock = DockStyle.Fill };
            _preview = new FirePreviewBox(_app.Fire, _app.Settings) { Dock = DockStyle.Fill };
            var previewHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 20, 18) };
            previewHost.Controls.Add(_preview);
            root.Controls.Add(previewHost, 1, 0);
            root.SetRowSpan(previewHost, 2);

            _previewButtons = new Panel { Dock = DockStyle.Fill };
            root.Controls.Add(_previewButtons, 1, 2);

            // By source / breakdown / timeline (dark themed)
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.FromArgb(28, 24, 22) };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 52));

            _lblBySourceCaption = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(150, 130, 110) };
            _bars = new SourceBars { Dock = DockStyle.Fill };
            _lblBreakdownCaption = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(150, 130, 110) };
            _breakdownBars = new UsageBars { Dock = DockStyle.Fill };
            left.Controls.Add(_lblBySourceCaption, 0, 0);
            left.Controls.Add(_bars, 0, 1);
            left.Controls.Add(_lblBreakdownCaption, 0, 2);
            left.Controls.Add(_breakdownBars, 0, 3);
            root.Controls.Add(left, 0, 1);

            _lblTimelineCaption = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(150, 130, 110) };
            _chart = new HourlyChart { Dock = DockStyle.Fill };
            root.Controls.Add(_lblTimelineCaption, 0, 2);
            root.Controls.Add(_chart, 0, 3);
        }

        private void BuildSourcesTab()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };

            var top = new Panel { Dock = DockStyle.Top, Height = 34 };
            _btnRescan = new Button { Width = 110, Height = 26, Location = new Point(0, 2) };
            _btnRescan.Click += delegate { _app.Rescan(); RefreshData(); };
            top.Controls.Add(_btnRescan);

            _sources = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            _sources.Columns.Add("", 96);
            _sources.Columns.Add("", 110);
            _sources.Columns.Add("", 96, HorizontalAlignment.Right);
            _sources.Columns.Add("", 300);

            _lblNoSource = new Label { Dock = DockStyle.Bottom, Height = 24, ForeColor = Color.Gray, Visible = false };

            panel.Controls.Add(_sources);
            panel.Controls.Add(_lblNoSource);
            panel.Controls.Add(top);
            _tabSources.Controls.Add(panel);
        }

        private void BuildSettingsTab()
        {
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16) };
            var box = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 9
            };
            box.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _lblFlame = SectionHeader();
            box.Controls.Add(_lblFlame, 0, 0);
            box.SetColumnSpan(_lblFlame, 2);

            _lblSize = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.Gray };
            _cmbSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
            _cmbSize.SelectedIndexChanged += delegate
            {
                if (_cmbSize.SelectedIndex < 0) return;
                _app.SetFlameSize(FlameSizes.All[_cmbSize.SelectedIndex]);
            };
            box.Controls.Add(_lblSize, 0, 1);
            box.Controls.Add(_cmbSize, 1, 1);

            _chkVisible = new CheckBox { AutoSize = true };
            _chkVisible.CheckedChanged += delegate { _app.SetFlameVisible(_chkVisible.Checked); };
            box.Controls.Add(_chkVisible, 1, 2);

            _chkRate = new CheckBox { AutoSize = true };
            _chkRate.CheckedChanged += delegate
            {
                _app.Settings.ShowLiveRate = _chkRate.Checked;
                _app.Settings.Save();
            };
            box.Controls.Add(_chkRate, 1, 3);

            _lblLanguage = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.Gray };
            _cmbLanguage = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
            _cmbLanguage.SelectedIndexChanged += delegate
            {
                if (_cmbLanguage.SelectedIndex < 0) return;
                var lang = (AppLanguage)_cmbLanguage.SelectedIndex;
                _app.Settings.Language = lang;
                _app.Settings.Save();
                _app.SetLanguage(lang);
            };
            box.Controls.Add(_lblLanguage, 0, 5);
            box.Controls.Add(_cmbLanguage, 1, 5);

            _btnResetPos = new Button { Width = 200, Height = 28, Anchor = AnchorStyles.Left };
            _btnResetPos.Click += delegate { _app.Flame.PlaceDefault(); _app.Settings.Save(); };
            box.Controls.Add(_btnResetPos, 1, 6);

            _lblFlameColor = SectionHeader();
            box.Controls.Add(_lblFlameColor, 0, 7);
            box.SetColumnSpan(_lblFlameColor, 2);

            _flameColorsRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            BuildFlameColorsRow();
            box.Controls.Add(_flameColorsRow, 0, 8);
            box.SetColumnSpan(_flameColorsRow, 2);

            scroll.Controls.Add(box);
            _tabSettings.Controls.Add(scroll);
        }

        private void BuildAboutTab()
        {
            var text = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Window,
                ScrollBars = ScrollBars.Vertical
            };
            _tabAbout.Controls.Add(text);
            _aboutBox = text;
        }

        private TextBox _aboutBox;

        private static Label SectionHeader()
        {
            return new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Margin = new Padding(0, 10, 0, 4)
            };
        }

        private void RebuildPreviewButtons()
        {
            // Stats tab removed; _previewButtons is null — skip
            if (_previewButtons == null) return;

            _previewButtons.Controls.Clear();
            _btnLive = new Button { Text = L10n.T("settings.live"), Width = 62, Height = 26, Location = new Point(0, 0) };
            _btnLive.Click += delegate { _app.Fire.ReturnToLive(); RefreshData(); };
            _previewButtons.Controls.Add(_btnLive);

            int x = 68;
            foreach (var style in FirePreviews.All)
            {
                var b = new Button { Text = style.Label(), Width = 56, Height = 26, Location = new Point(x, 0) };
                var captured = style;
                b.Click += delegate { _app.Fire.ShowPreview(captured); RefreshData(); };
                _previewButtons.Controls.Add(b);
                x += 60;
            }
        }

        /// <summary>Flame color presets.</summary>
        private static readonly ExoticFireTheme[] ExoticFires =
        {
            new ExoticFireTheme("Teal",     0.00, 0.83, 0.67),
            new ExoticFireTheme("Emerald",   0.00, 1.00, 0.70),
            new ExoticFireTheme("Crimson",   1.00, 0.13, 0.00),
            new ExoticFireTheme("Ocean",     0.00, 0.53, 1.00),
            new ExoticFireTheme("Violet",    0.60, 0.20, 1.00),
            new ExoticFireTheme("Ice Blue",  0.53, 0.87, 1.00),
            new ExoticFireTheme("Gold",      1.00, 0.67, 0.00),
            new ExoticFireTheme("Dark Violet", 0.40, 0.00, 0.80),
            new ExoticFireTheme("Electric",  1.00, 0.84, 0.00),
            new ExoticFireTheme("Void",      0.10, 0.00, 0.20),
            new ExoticFireTheme("Imperial",  1.00, 0.27, 0.00),
            new ExoticFireTheme("Silver",    0.67, 0.80, 1.00),
        };

        private struct ExoticFireTheme
        {
            public readonly string Name;
            public readonly double R, G, B;
            public ExoticFireTheme(string name, double r, double g, double b) { Name = name; R = r; G = g; B = b; }
        }

        private void BuildFlameColorsRow()
        {
            _flameColorsRow.Controls.Clear();
            _colorCircles = new Panel[ExoticFires.Length];
            // Find which preset matches current flame color (if any)
            int activeIdx = -1;
            var fc = _app.Settings.FlameColor;
            for (int i = 0; i < ExoticFires.Length; i++)
            {
                var t = ExoticFires[i];
                if (Math.Abs(t.R - fc[0]) < 0.01 && Math.Abs(t.G - fc[1]) < 0.01 && Math.Abs(t.B - fc[2]) < 0.01)
                { activeIdx = i; break; }
            }
            for (int i = 0; i < ExoticFires.Length; i++)
            {
                var theme = ExoticFires[i];
                var c = Color.FromArgb((int)(theme.R * 255), (int)(theme.G * 255), (int)(theme.B * 255));
                int idx = i;
                var circle = new Panel
                {
                    Width = 22, Height = 22,
                    Margin = new Padding(0, 2, 6, 2),
                    BackColor = c,
                    Cursor = Cursors.Hand
                };
                // Selection ring: 2px white-ish border when active
                circle.Paint += delegate(object s, PaintEventArgs pe)
                {
                    var p = (Panel)s;
                    bool active = (activeIdx >= 0 && _flameColorsRow.Controls.IndexOf(p) == activeIdx);
                    if (active)
                    {
                        using (var pen = new Pen(Color.FromArgb(220, 220, 215), 2))
                            pe.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
                    }
                };
                circle.Click += delegate { ApplyExoticFire(idx); };
                _toolTip.SetToolTip(circle, theme.Name);
                _flameColorsRow.Controls.Add(circle);
                _colorCircles[i] = circle;
            }
            // Custom color "..." button
            var customBtn = new Label
            {
                Width = 22, Height = 22,
                Margin = new Padding(2, 2, 0, 2),
                BackColor = Color.FromArgb(38, 32, 28),
                ForeColor = Color.FromArgb(160, 140, 120),
                Text = "...",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            customBtn.Click += delegate { PickCustomFlameColor(); };
            _toolTip.SetToolTip(customBtn, L10n.T("settings.flameCustom"));
            _flameColorsRow.Controls.Add(customBtn);
        }

        private void UpdateFlameColorSelection()
        {
            if (_colorCircles == null) return;
            var fc = _app.Settings.FlameColor;
            int activeIdx = -1;
            for (int i = 0; i < ExoticFires.Length; i++)
            {
                var t = ExoticFires[i];
                if (Math.Abs(t.R - fc[0]) < 0.01 && Math.Abs(t.G - fc[1]) < 0.01 && Math.Abs(t.B - fc[2]) < 0.01)
                { activeIdx = i; break; }
            }
            for (int i = 0; i < _colorCircles.Length; i++)
                _colorCircles[i].Invalidate();
        }



        private void ApplyExoticFire(int index)
        {
            if (index < 0 || index >= ExoticFires.Length) return;
            var theme = ExoticFires[index];
            _app.SetFlameColor(theme.R, theme.G, theme.B);
            UpdateFlameColorSelection();
        }

        private void PickCustomFlameColor()
        {
            using (var dlg = new ColorDialog())
            {
                var fc = _app.Settings.FlameColor;
                dlg.Color = Color.FromArgb((int)(fc[0] * 255), (int)(fc[1] * 255), (int)(fc[2] * 255));
                dlg.FullOpen = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _app.SetFlameColor(dlg.Color.R / 255.0, dlg.Color.G / 255.0, dlg.Color.B / 255.0);
                UpdateFlameColorSelection();
            }
        }



        // ------------------------------------------------------------------
        // 刷新
        // ------------------------------------------------------------------

        public void RefreshData()
        {
            var monitor = _app.Monitor;
            var snap = _app.Fire.Snapshot;

            if (_lblTodayValue != null) _lblTodayValue.Text = L10n.Grouped(monitor.TodayTokens);
            if (_lblTierValue != null) { _lblTierValue.Text = snap.Tier.Label(); _lblTierValue.ForeColor = TierColor(snap.Phase); }

            int peakHour = -1, peakVal = 0;
            var hourly = monitor.TodayHourly;
            if (hourly != null)
                for (int i = 0; i < hourly.Count; i++)
                    if (hourly[i].Tokens > peakVal) { peakVal = hourly[i].Tokens; peakHour = hourly[i].Hour; }

            if (_lblPeakValue != null)
                _lblPeakValue.Text = peakHour >= 0
                    ? L10n.T("stats.peak") + "  " + peakHour.ToString("00") + ":00 · " + L10n.Compact(peakVal)
                    : "";

            if (_bars != null) _bars.Set(monitor.TodayBySource);
            if (_breakdownBars != null) _breakdownBars.Set(
                new[]
                {
                    L10n.T("breakdown.input"), L10n.T("breakdown.output"),
                    L10n.T("breakdown.cacheRead"), L10n.T("breakdown.cacheWrite")
                },
                new[]
                {
                    monitor.TodayBreakdown.Input ?? 0, monitor.TodayBreakdown.Output ?? 0,
                    monitor.TodayBreakdown.CacheRead ?? 0, monitor.TodayBreakdown.CacheWrite ?? 0
                });
            if (_chart != null) _chart.Set(hourly);

            // 数据源列表
            _sources.BeginUpdate();
            _sources.Items.Clear();
            var statuses = monitor.Statuses;
            int okCount = 0;
            if (statuses != null)
            {
                foreach (var st in statuses)
                {
                    if (st.State == SourceConnectionState.Ok) okCount++;
                    var item = new ListViewItem(st.Source.DisplayName());
                    item.SubItems.Add(st.State.Label());
                    item.SubItems.Add(L10n.Compact(st.TodayTokens));
                    item.SubItems.Add(st.Detail ?? "—");
                    item.ForeColor = st.State == SourceConnectionState.Ok ? Color.FromArgb(30, 30, 30) : Color.Gray;
                    _sources.Items.Add(item);
                }
            }
            _sources.EndUpdate();
            _lblNoSource.Visible = okCount == 0;

            // 设置页状态
            _chkVisible.Checked = _app.Settings.FlameVisible;
            _chkRate.Checked = _app.Settings.ShowLiveRate;
            _cmbSize.SelectedIndex = IndexOf(FlameSizes.All, _app.Settings.Size);
            _cmbLanguage.SelectedIndex = (int)_app.Settings.Language;

            if (_aboutBox != null)
            {
                _aboutBox.Text = L10n.T("app.name") + "  ·  Windows\r\n\r\n"
                    + L10n.T("app.tagline") + "\r\n\r\n"
                    + L10n.T("about.origin") + "\r\n\r\n"
                    + L10n.T("about.sources") + "\r\n\r\n"
                    + L10n.T("about.privacy") + "\r\n\r\n"
                    + L10n.T("about.powered") + "\r\n\r\n"
                    + L10n.T("about.gap");
            }

            if (_preview != null) _preview.Invalidate();
        }

        private static int IndexOf(FlameSize[] arr, FlameSize v)
        {
            for (int i = 0; i < arr.Length; i++) if (arr[i] == v) return i;
            return 1;
        }

        private static Color TierColor(FirePhase phase)
        {
            switch (phase)
            {
                case FirePhase.Flame: return Color.FromArgb(206, 78, 16);
                case FirePhase.Ember: return Color.FromArgb(150, 68, 40);
                case FirePhase.Unlit: return Color.Gray;
                default: return Color.FromArgb(110, 100, 96);
            }
        }

protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 关掉窗口只是隐藏，应用继续常驻托盘
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            L10n.LanguageChanged -= LanguageChanged;
            base.OnFormClosing(e);
        }
    }

    // ======================================================================
    // 自绘控件
    // ======================================================================

    // ======================================================================
    // GDI+ helpers for .NET 3.5 (no built-in rounded rects)
    // ======================================================================

    internal static class Gfx
    {
        public static void FillRoundedRect(Graphics g, Brush b, float x, float y, float w, float h, float r)
        {
            if (w < 1f || h < 1f) return;
            if (r > w / 2f) r = w / 2f;
            if (r > h / 2f) r = h / 2f;
            using (var path = new GraphicsPath())
            {
                path.AddArc(x, y, r * 2, r * 2, 180, 90);
                path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
                path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
                path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
                path.CloseFigure();
                g.FillPath(b, path);
            }
        }

        public static void FillRoundedRect(Graphics g, Brush b, RectangleF rect, float r)
        {
            FillRoundedRect(g, b, rect.X, rect.Y, rect.Width, rect.Height, r);
        }

        public static void DrawRoundedRect(Graphics g, Pen p, float x, float y, float w, float h, float r)
        {
            if (w < 1f || h < 1f) return;
            if (r > w / 2f) r = w / 2f;
            if (r > h / 2f) r = h / 2f;
            using (var path = new GraphicsPath())
            {
                path.AddArc(x, y, r * 2, r * 2, 180, 90);
                path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
                path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
                path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
                path.CloseFigure();
                g.DrawPath(p, path);
            }
        }
    }

        /// <summary>分来源用量条。</summary>
    internal sealed class SourceBars : Control
    {
        private readonly List<KeyValuePair<UsageSource, int>> _rows = new List<KeyValuePair<UsageSource, int>>();

        public SourceBars()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(28, 24, 22);
        }

        public void Set(Dictionary<UsageSource, int> bySource)
        {
            _rows.Clear();
            int total = 0;
            if (bySource != null)
                foreach (var s in UsageSources.All)
                {
                    int v;
                    if (bySource.TryGetValue(s, out v) && v > 0) { _rows.Add(new KeyValuePair<UsageSource, int>(s, v)); total += v; }
                }
            _total = total;
            Invalidate();
        }

        private int _total;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var f = new Font("Segoe UI", 8.5f))
            using (var fb = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            {
                int y = 4;
                int barH = 14;
                int barTop = y + 13;
                foreach (var row in _rows)
                {
                    var accent = SourceFlameColors.Accent(row.Key);
                    var c = Color.FromArgb((int)(accent[0] * 255), (int)(accent[1] * 255), (int)(accent[2] * 255));
                    // Colored dot indicator
                    using (var b = new SolidBrush(c))
                        g.FillEllipse(b, 2, y + 2, 10, 10);
                    // Source name
                    using (var _sb1 = new SolidBrush(Color.FromArgb(180, 160, 140)))
                        g.DrawString(row.Key.DisplayName(), f, _sb1, 16, y);
                    // Value right-aligned
                    string v = L10n.Compact(row.Value);
                    var sz = g.MeasureString(v, fb);
                    using (var _sb2 = new SolidBrush(Color.FromArgb(255, 220, 180)))
                        g.DrawString(v, fb, _sb2, Width - sz.Width - 4, y);
                    // Horizontal progress bar
                    if (_total > 0)
                    {
                        float frac = (float)row.Value / _total;
                        int barW = Math.Max(2, (int)((Width - 80) * frac));
                        using (var barBrush = new SolidBrush(Color.FromArgb(
                            Math.Min(255, c.R + 40), Math.Min(255, c.G + 30), Math.Min(255, c.B + 20))))
                            Gfx.FillRoundedRect(g, barBrush, 16, barTop, barW, barH, 3);
                        // Track background
                        using (var trackPen = new Pen(Color.FromArgb(40, 35, 30)))
                            Gfx.DrawRoundedRect(g, trackPen, 16, barTop, Width - 80, barH, 3);
                    }
                    y += 28;
                    if (y > Height - 20) break;
                }
            }
        }
    }

    /// <summary>用量构成条（输入 / 输出 / 缓存读 / 缓存写）。</summary>
    internal sealed class UsageBars : Control
    {
        private string[] _labels = new string[0];
        private int[] _values = new int[0];

        public UsageBars()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(28, 24, 22);
        }

        public void Set(string[] labels, int[] values)
        {
            _labels = labels; _values = values;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var f = new Font("Segoe UI", 8.5f))
            using (var fb = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            {
                var colors = new[]
                {
                    Color.FromArgb(255, 160, 60),
                    Color.FromArgb(255, 200, 60),
                    Color.FromArgb(100, 160, 220),
                    Color.FromArgb(140, 140, 160)
                };
                int y = 4;
                int barH = 12;
                for (int i = 0; i < _labels.Length && i < _values.Length; i++)
                {
                    using (var _ub1 = new SolidBrush(Color.FromArgb(160, 140, 120)))
                    g.DrawString(_labels[i], f, _ub1, 0, y);
                    string v = L10n.Compact(_values[i]);
                    var sz = g.MeasureString(v, fb);
                    using (var _ub2 = new SolidBrush(Color.FromArgb(255, 220, 180)))
                        g.DrawString(v, fb, _ub2, Width - sz.Width - 4, y);
                    float frac = (float)Fraction(i);
                    int barW = Math.Max(2, (int)((Width - 8) * frac));
                    using (var b = new SolidBrush(colors[i % colors.Length]))
                        Gfx.FillRoundedRect(g, b, 0, y + 16, barW, barH, 2);
                    using (var trackPen = new Pen(Color.FromArgb(40, 35, 30)))
                        Gfx.DrawRoundedRect(g, trackPen, 0, y + 16, Width - 8, barH, 2);
                    y += 30;
                    if (y > Height - 18) break;
                }
            }
        }

        private double Fraction(int i)
        {
            int total = 0;
            for (int k = 0; k < _values.Length; k++) total += _values[k];
            if (total <= 0) return 0;
            return (double)_values[i] / total;
        }
    }

    /// <summary>今日 24 小时时间线。</summary>
    internal sealed class HourlyChart : Control
    {
        private List<HourlyUsage> _data = new List<HourlyUsage>();

        public HourlyChart()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(28, 24, 22);
        }

        public void Set(List<HourlyUsage> data)
        {
            _data = data ?? new List<HourlyUsage>();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (_data.Count == 0) return;

            int max = 0;
            foreach (var h in _data) if (h.Tokens > max) max = h.Tokens;
            if (max <= 0) max = 1;

            int n = _data.Count;
            float slot = (float)Width / n;
            float barW = Math.Max(2f, slot - 2f);
            int baseY = Height - 14;

            using (var f = new Font("Segoe UI", 7f))
            {
                for (int i = 0; i < n; i++)
                {
                    int v = _data[i].Tokens;
                    float h = (float)(baseY - 4) * v / max;
                    if (h < 1f) h = v > 0 ? 1f : 0f;
                    float x = i * slot + 1f;
                    // Gradient from warm orange to bright yellow
                    using (var gb = new LinearGradientBrush(
                        new PointF(x, baseY), new PointF(x, baseY - h),
                        Color.FromArgb(255, 140, 50), Color.FromArgb(255, 220, 100)))
                    {
                        var rect = new RectangleF(x, baseY - h, barW, h);
                        Gfx.FillRoundedRect(g, gb, rect, 2);
                    }
                    if (v > 0)
                    {
                        // Top highlight
                        using (var hl = new SolidBrush(Color.FromArgb(255, 255, 200)))
                            Gfx.FillRoundedRect(g, hl, x, baseY - h, barW, Math.Min(2f, h), 1);
                    }

                    if (i % 3 == 0)
                    {
                        string lbl = i.ToString("00");
                        var sz = g.MeasureString(lbl, f);
                        using (var _hb = new SolidBrush(Color.FromArgb(120, 100, 80)))
                            g.DrawString(lbl, f, _hb, i * slot + (slot - sz.Width) / 2, baseY + 1);
                    }
                }
                using (var pen = new Pen(Color.FromArgb(50, 42, 38)))
                    g.DrawLine(pen, 0, baseY, Width, baseY);
            }
        }
    }

    /// <summary>控制台里的实时火焰预览。</summary>
    internal sealed class FirePreviewBox : Control
    {
        private readonly FireStateMachine _fire;
        private readonly Settings _settings;
        private readonly CampfireRenderer _renderer = new CampfireRenderer();
        private readonly Timer _timer;
        private readonly DateTime _start = DateTime.Now;

        public FirePreviewBox(FireStateMachine fire, Settings settings)
        {
            _fire = fire;
            _settings = settings;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(24, 20, 18);

            _timer = new Timer { Interval = 60 };
            _timer.Tick += delegate { if (Visible) Invalidate(); };
            _timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 8 || Height < 8) return;
            double px = Math.Min(Width * 0.62 / PixelFireEngine.FireW, Height * 0.72 / (PixelFireEngine.FireH + PixelFireEngine.LogH));
            if (px < 1) px = 1;

            int w = (int)Math.Ceiling(PixelFireEngine.FireW * px);
            int h = (int)Math.Ceiling((PixelFireEngine.FireH + PixelFireEngine.LogH) * px);
            _renderer.Resize(w, h);
            _renderer.Render(_fire.Snapshot, px / Dpi.Scale, false, (DateTime.Now - _start).TotalSeconds);

            int x = (Width - w) / 2;
            int y = (Height - h) / 2;
            e.Graphics.DrawImage(_renderer.Bitmap, x, y, w, h);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();
                _renderer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
