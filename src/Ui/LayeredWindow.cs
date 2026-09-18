//
//  LayeredWindow.cs — CodingFire for Windows
//
//  桌面悬浮窗基类：无边框 + 逐像素透明（UpdateLayeredWindow）+ 不抢焦点。
//  用分层窗口而不是 TransparencyKey，因为篝火需要真正的 alpha（余烬渐隐、火星淡出）。
//  副作用还是个好处：透明像素天然点击穿透，不会挡住桌面图标。
//

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodingFire.Ui
{
    internal class LayeredWindow : Form
    {
        private Bitmap _lastApplied;

        protected virtual bool ClickThrough { get { return false; } }

        public LayeredWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            MinimizeBox = false;
            MaximizeBox = false;
            BackColor = Color.Black;
            // 双缓冲对我们没用——内容靠 UpdateLayeredWindow 直接推给 DWM
            SetStyle(ControlStyles.Opaque, true);
        }

        /// <summary>分层窗口 + 工具栏窗口（不占 Alt+Tab）+ 不激活，靠 ShowWithoutActivation 也不抢焦点。</summary>
        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_LAYERED = 0x00080000;
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_NOACTIVATE = 0x08000000;
                const int WS_EX_TRANSPARENT = 0x00000020;

                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                if (ClickThrough) cp.ExStyle |= WS_EX_TRANSPARENT;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 不做传统绘制，避免闪烁
        }

        protected override void OnPaint(PaintEventArgs e) { }

        /// <summary>把位图整体推给窗口（位置一并设定）。</summary>
        public void ApplyBitmap(Bitmap bitmap, Point location)
        {
            if (bitmap == null) return;
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0, 0, 0, 0));
                oldBitmap = SelectObject(memDc, hBitmap);

                var size = new SIZE(bitmap.Width, bitmap.Height);
                var pointSource = new POINT(0, 0);
                var topPos = new POINT(location.X, location.Y);
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };

                UpdateLayeredWindow(Handle, screenDc, ref topPos, ref size, memDc, ref pointSource, 0, ref blend, ULW_ALPHA);
                _lastApplied = bitmap;
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public Bitmap LastAppliedBitmap { get { return _lastApplied; } }

        /// <summary>只更新内容、保持位置。</summary>
        public void ApplyBitmap(Bitmap bitmap)
        {
            ApplyBitmap(bitmap, new Point(Left, Top));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _lastApplied != null) { _lastApplied = null; }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------

        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; public POINT(int x, int y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; public SIZE(int w, int h) { cx = w; cy = h; } }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}
