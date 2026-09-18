//
//  FlameForm.cs — CodingFire for Windows
//
//  桌面篝火本体 + 悬停卡片。
//  动效节奏与 macOS 版对齐：模拟 12fps（减少动态效果 6fps）、窗口刷新 20Hz、悬停数字 0.12s 一刷。
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using CodingFire.Core;
using CodingFire.Fire;

namespace CodingFire.Ui
{
    internal sealed class HoverRow
    {
        public UsageSource Source;
        public int Tokens;
        public bool Estimated;
    }

    internal sealed class HoverModel
    {
        public int TodayTokens;
        public double TokensPerSecond;
        public bool ShowLiveRate;
        public List<HoverRow> Rows = new List<HoverRow>();
        public DateTime? UpdatedAt;
    }

    internal sealed class FlameForm : LayeredWindow
    {
        private const int FrameIntervalMs = 50;   // 20Hz 窗口刷新
        private const int CardRefreshMs = 120;    // 悬停数字刷新节奏

        private readonly FireStateMachine _fire;
        private readonly Settings _settings;
        private readonly Func<HoverModel> _hoverProvider;
        private readonly CampfireRenderer _renderer = new CampfireRenderer();
        private readonly Timer _timer;
        private readonly CardWindow _card;
        private readonly DateTime _startTime = DateTime.Now;
        private bool _dragging;
        private bool _dragArmed;
        private Point _dragStartCursor;
        private Point _dragStartWindow;

        private bool _hovering;
        private int _hoverMissCount;
        private DateTime _lastCardRefresh = DateTime.MinValue;
        private Bitmap _cardBitmap;

        public FlameForm(FireStateMachine fire, Settings settings, Func<HoverModel> hoverProvider)
        {
            _fire = fire;
            _settings = settings;
            _hoverProvider = hoverProvider;

            // 卡片是纯展示层，必须点击穿透，永远不拦鼠标
            _card = new CardWindow();

            ApplySize(settings.Size);
            BuildContextMenu();

            _timer = new Timer();
            _timer.Interval = FrameIntervalMs;
            _timer.Tick += delegate { OnFrame(); };
            _timer.Start();

            MouseEnter += delegate { OnFlameHover(true); };
            MouseLeave += delegate { OnFlameHover(false); };
            MouseDown += OnFlameMouseDown;
            MouseMove += OnFlameMouseMove;
            MouseUp += OnFlameMouseUp;
        }

        private sealed class CardWindow : LayeredWindow
        {
            protected override bool ClickThrough { get { return true; } }
        }

        // ------------------------------------------------------------------
        // 尺寸与位置
        // ------------------------------------------------------------------

        public void ApplySize(FlameSize size)
        {
            _settings.Size = size;
            double px = size.PixelScale() * Dpi.Scale;
            int w = (int)Math.Ceiling(PixelFireEngine.FireW * px + 28 * Dpi.Scale);
            int h = (int)Math.Ceiling((PixelFireEngine.FireH + PixelFireEngine.LogH) * px + 36 * Dpi.Scale);
            _renderer.Resize(w, h);

            var old = Location;
            Size = new Size(w, h);
            if (old.IsEmpty || !_settings.HasPosition) PlaceDefault();
            else Location = old;
        }

        public void PlaceDefault()
        {
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int x = wa.Right - Width - (int)Math.Round(36 * Dpi.Scale);
            int y = wa.Bottom - Height - (int)Math.Round(36 * Dpi.Scale);
            Location = new Point(Math.Max(wa.Left, x), Math.Max(wa.Top, y));
            PersistPosition();
        }

        public void ConstrainToScreen()
        {
            var wa = Screen.FromRectangle(Bounds).WorkingArea;
            int x = Math.Min(Math.Max(Left, wa.Left), wa.Right - Width);
            int y = Math.Min(Math.Max(Top, wa.Top), wa.Bottom - Height);
            Location = new Point(x, y);
        }

        private void PersistPosition()
        {
            _settings.PanelX = Left;
            _settings.PanelY = Top;
            _settings.HasPosition = true;
        }

        public void MoveBy(int dx, int dy)
        {
            Location = new Point(Left + dx, Top + dy);
        }

        // ------------------------------------------------------------------
        // 每帧
        // ------------------------------------------------------------------

        private void OnFrame()
        {
            if (!Visible) return;

            if (_dragging)
            {
                // 拖动时把模拟降级，避免透明窗口被反复合成时掉帧
                _fire.Tick();
                _renderer.Render(_fire.Snapshot, _settings.Size.PixelScale(), true, Elapsed());
                ApplyBitmap(_renderer.Bitmap);
                return;
            }

            _fire.Tick();
            _renderer.Render(_fire.Snapshot, _settings.Size.PixelScale(), false, Elapsed());
            ApplyBitmap(_renderer.Bitmap);

            UpdateHoverCard();
        }

        private double Elapsed() { return (DateTime.Now - _startTime).TotalSeconds; }

        // ------------------------------------------------------------------
        // 悬停
        // ------------------------------------------------------------------

        private void OnFlameHover(bool entering)
        {
            if (_dragging) return;
            if (entering)
            {
                _hovering = true;
                _hoverMissCount = 0;
                RefreshCard(true);
            }
            else
            {
                // 火焰在动，像素级命中会偶尔「掉出」——给几次机会再收卡片
                _hoverMissCount = 6;
            }
        }

        private void UpdateHoverCard()
        {
            if (!_hovering)
            {
                if (_card.Visible) _card.Hide();
                return;
            }

            // 简单去抖：鼠标已经不在窗口矩形内才算真的离开
            if (!Bounds.Contains(Cursor.Position))
            {
                if (_hoverMissCount-- <= 0)
                {
                    _hovering = false;
                    _card.Hide();
                    return;
                }
            }
            else _hoverMissCount = 6;

            if ((DateTime.Now - _lastCardRefresh).TotalMilliseconds >= CardRefreshMs) RefreshCard(false);
        }

        private void RefreshCard(bool force)
        {
            var model = _hoverProvider != null ? _hoverProvider() : new HoverModel();
            var bmp = HoverCardPainter.Draw(model);
            if (_cardBitmap != null) _cardBitmap.Dispose();
            _cardBitmap = bmp;

            int tipY = _renderer.FlameTipY;
            int gap = (int)Math.Round(8 * Dpi.Scale);
            int cardX = Left + (Width - bmp.Width) / 2;
            int cardY = Top + tipY - bmp.Height - gap;

            var wa = Screen.FromRectangle(Bounds).WorkingArea;
            if (cardY < wa.Top) cardY = Top + Height + gap;   // 上方放不下就翻到下面
            cardX = Math.Min(Math.Max(cardX, wa.Left), wa.Right - bmp.Width);

            _card.ApplyBitmap(bmp, new Point(cardX, cardY));
            if (!_card.Visible) _card.Show();
            _lastCardRefresh = DateTime.Now;
        }

        public void HideCard()
        {
            _hovering = false;
            if (_card.Visible) _card.Hide();
        }

        // ------------------------------------------------------------------
        // 拖动 / 菜单
        // ------------------------------------------------------------------

        private void OnFlameMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) return;
            _dragArmed = true;
            _dragging = false;
            _dragStartCursor = Cursor.Position;
            _dragStartWindow = Location;
        }

        private void OnFlameMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragArmed || e.Button != MouseButtons.Left) return;

            var now = Cursor.Position;
            int dx = now.X - _dragStartCursor.X;
            int dy = now.Y - _dragStartCursor.Y;
            if (!_dragging && (Math.Abs(dx) > 2 || Math.Abs(dy) > 2)) _dragging = true;
            if (!_dragging) return;

            Location = new Point(_dragStartWindow.X + dx, _dragStartWindow.Y + dy);
            HideCard();
        }

        private void OnFlameMouseUp(object sender, MouseEventArgs e)
        {
            _dragArmed = false;
            if (!_dragging) return;
            _dragging = false;
            ConstrainToScreen();
            PersistPosition();
            _settings.Save();
            RaiseMoved();
        }

        public event Action Moved;

        private void RaiseMoved()
        {
            var h = Moved;
            if (h != null) h();
        }

        private void BuildContextMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(L10n.T("menu.resetPosition"), null, delegate
            {
                PlaceDefault();
                _settings.Save();
                RaiseMoved();
            });
            menu.Items.Add(L10n.T("menu.togglePause"), null, delegate
            {
                _fire.AnimationPaused = !_fire.AnimationPaused;
                _settings.AnimationPaused = _fire.AnimationPaused;
                _settings.Save();
                RaiseMoved();
            });
            menu.Items.Add(L10n.T("menu.hideFlame"), null, delegate
            {
                SetFlameVisible(false);
                RaiseMoved();
            });
            ContextMenuStrip = menu;
        }

        public void RebuildMenu() { BuildContextMenu(); }

        public event Action<bool> VisibilityRequested;

        private void SetFlameVisible(bool visible)
        {
            var h = VisibilityRequested;
            if (h != null) h(visible);
            else Visible = visible;
        }

        public void SetVisible(bool visible)
        {
            if (visible)
            {
                ConstrainToScreen();
                Show();
            }
            else
            {
                HideCard();
                Hide();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();
                if (_cardBitmap != null) _cardBitmap.Dispose();
                _card.Dispose();
                _renderer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>悬停卡片绘制：深色玻璃 HUD，与 macOS 版 HoverSummaryView 的信息层级一致。</summary>
    internal static class HoverCardPainter
    {
        public static Bitmap Draw(HoverModel model)
        {
            float dpi = Dpi.Scale;
            int cardW = (int)Math.Round(216 * dpi);
            int pad = (int)Math.Round(13 * dpi);
            int lineH = (int)Math.Round(19 * dpi);

            int rows = model.Rows != null ? model.Rows.Count : 0;
            int height = pad
                         + (int)Math.Round(15 * dpi)      // 标题
                         + (int)Math.Round(30 * dpi)      // 大数字
                         + (int)Math.Round(10 * dpi)      // 间距
                         + (rows > 0 ? rows * lineH + (int)Math.Round(6 * dpi) : (int)Math.Round(20 * dpi))
                         + (int)Math.Round(17 * dpi)      // 页脚
                         + pad;

            var bmp = new Bitmap(cardW, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // 透明表面上用 ClearType 会出彩边，AntiAliasGridFit 才干净
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                using (var path = Rounded(new Rectangle(0, 0, cardW - 1, height - 1), (int)Math.Round(12 * dpi)))
                {
                    using (var brush = new SolidBrush(Color.FromArgb(236, 30, 26, 24)))
                        g.FillPath(brush, path);
                    using (var pen = new Pen(Color.FromArgb(46, 255, 184, 92), Math.Max(1f, dpi)))
                        g.DrawPath(pen, path);
                }

                using (var titleFont = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point))
                using (var bigFont = new Font("Segoe UI", 19f, FontStyle.Bold, GraphicsUnit.Point))
                using (var rowFont = new Font("Segoe UI", 8.6f, FontStyle.Regular, GraphicsUnit.Point))
                using (var footFont = new Font("Segoe UI", 7.4f, FontStyle.Regular, GraphicsUnit.Point))
                using (var dim = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                using (var bright = new SolidBrush(Color.FromArgb(245, 255, 255, 255)))
                using (var faint = new SolidBrush(Color.FromArgb(110, 255, 255, 255)))
                {
                    int y = pad;

                    g.DrawString(L10n.T("hover.today"), titleFont, dim, pad, y);
                    if (model.ShowLiveRate && model.TokensPerSecond > 0)
                    {
                        string rate = model.TokensPerSecond.ToString("0.0") + " " + L10n.T("hover.tokensS");
                        var size = g.MeasureString(rate, footFont);
                        g.DrawString(rate, footFont, faint, cardW - pad - size.Width, y + 2);
                    }
                    y += (int)Math.Round(15 * dpi);

                    g.DrawString(L10n.Compact(model.TodayTokens), bigFont, bright, pad - 2, y);
                    y += (int)Math.Round(30 * dpi) + (int)Math.Round(10 * dpi);

                    if (rows == 0)
                    {
                        g.DrawString(L10n.T("hover.none"), rowFont, faint,
                            new RectangleF(pad, y, cardW - pad * 2, lineH * 2));
                    }
                    else
                    {
                        foreach (var row in model.Rows)
                        {
                            var accent = SourceFlameColors.Accent(row.Source);
                            using (var dot = new SolidBrush(Color.FromArgb(255,
                                (int)Math.Round(accent[0] * 255),
                                (int)Math.Round(accent[1] * 255),
                                (int)Math.Round(accent[2] * 255))))
                            {
                                g.FillEllipse(dot, pad, y + (int)Math.Round(5 * dpi), (int)Math.Round(8 * dpi), (int)Math.Round(8 * dpi));
                            }

                            string name = row.Source.DisplayName() + (row.Estimated ? " · " + L10n.T("hover.estimate") : "");
                            g.DrawString(name, rowFont, dim, pad + (int)Math.Round(15 * dpi), y);

                            string value = L10n.Compact(row.Tokens);
                            var vs = g.MeasureString(value, rowFont);
                            g.DrawString(value, rowFont, bright, cardW - pad - vs.Width, y);

                            y += lineH;
                        }
                    }

                    if (model.UpdatedAt.HasValue)
                    {
                        string when = model.UpdatedAt.Value.ToString("HH:mm");
                        g.DrawString(L10n.T("hover.updated") + " " + when, footFont, faint, pad, height - pad - (int)Math.Round(12 * dpi));
                    }
                }
            }
            return bmp;
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
