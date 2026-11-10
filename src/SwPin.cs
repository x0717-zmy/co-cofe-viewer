using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace SwPin
{
    static class Program
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            // 高分屏下必须声明 DPI 感知，否则窗口会被系统放大发虚
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { try { SetProcessDPIAware(); } catch { } }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var f = new PinForm())
            {
                if (args != null && args.Length > 0) f.QueueFiles(args);
                Application.Run(f);
            }
        }
    }

    /// <summary>自绘菜单的一项</summary>
    class MenuItem
    {
        public string Text;
        public bool Checked;
        public bool Enabled = true;
        public bool Separator;
        public Action Run;
    }

    /// <summary>置顶看图小窗：永远在所有窗口上方，可调大小 / 透明度 / 缩放</summary>
    class PinForm : Form
    {
        // ---------------- Win32 ----------------
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_LAYERED = 0x00080000;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;

        const int WM_HOTKEY = 0x0312;
        const int WM_NCHITTEST = 0x0084;

        const int HOTKEY_TOGGLE = 0x1001;
        const int HOTKEY_THROUGH = 0x1002;
        const uint MOD_ALT = 0x0001;
        const uint MOD_CONTROL = 0x0002;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        // 标题栏按钮序号
        const int BTN_HELP = 0, BTN_MIN = 1, BTN_MAX = 2, BTN_CLOSE = 3;
        const int HeaderBase = 30;   // 未经缩放时的标题栏高度

        // ---------------- 状态 ----------------
        readonly List<string> files = new List<string>();
        int index = 0;
        Image image;
        Image welcome;           // 欢迎页底图（welcome.jpg）

        float zoom = 1f;          // 1 = 适应窗口
        float offX, offY;         // 放大后的平移量
        float alpha = 0.88f;      // 整体不透明度
        bool clickThrough;

        bool dragging;            // 拖动窗口
        bool panning;             // 平移图片
        Point pressScreen;
        Point winAtPress;
        float panOffX, panOffY;

        int hoverBtn = -1;
        int pressBtn = -1;

        // 自绘右键菜单（不用系统弹出菜单，避免抢焦点打不开）
        bool menuOpen;
        int menuHover = -1;
        int menuPress = -1;
        Rectangle menuRect;
        List<MenuItem> menuItems;

        bool maximized;
        Rectangle restoreBounds;

        float dpiScale = 1f;
        NotifyIcon tray;

        readonly string stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwPin");
        string StatePath { get { return Path.Combine(stateDir, "state.txt"); } }

        static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".wmf", ".emf" };

        public PinForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(38, 38, 38);
            DoubleBuffered = true;
            AllowDrop = true;
            Text = "Co_cofe建模看图工具";
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            LoadState();
            BuildTray();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x00040000;      // WS_THICKFRAME：让系统负责边缘拖拽改大小
                cp.Style &= ~0x00C00000;     // 去掉标题栏
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using (Graphics g = CreateGraphics()) dpiScale = g.DpiX / 96f;
            ApplyAlpha();
            RegisterHotKey(Handle, HOTKEY_TOGGLE, MOD_CONTROL | MOD_ALT, (uint)Keys.O);
            RegisterHotKey(Handle, HOTKEY_THROUGH, MOD_CONTROL | MOD_ALT, (uint)Keys.P);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterHotKey(Handle, HOTKEY_TOGGLE);
            UnregisterHotKey(Handle, HOTKEY_THROUGH);
            base.OnHandleDestroyed(e);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            LoadWelcome();
            if (files.Count > 0) LoadImage(files[0]);
            ApplyAlpha();
            Invalidate();
        }

        // ---------------- 尺寸与区域 ----------------
        int S(int v) { return (int)Math.Round(v * dpiScale); }
        float SF(float v) { return v * dpiScale; }

        Rectangle HeaderRect { get { return new Rectangle(0, 0, ClientSize.Width, S(HeaderBase)); } }

        Rectangle ContentRect
        {
            get
            {
                int h = S(HeaderBase);
                return new Rectangle(0, h, ClientSize.Width, Math.Max(1, ClientSize.Height - h));
            }
        }

        Rectangle[] ButtonRects()
        {
            int h = S(HeaderBase);
            int w = S(36);
            int right = ClientSize.Width;
            return new[]
            {
                new Rectangle(right - 4 * w, 0, w, h),   // ?
                new Rectangle(right - 3 * w, 0, w, h),   // —
                new Rectangle(right - 2 * w, 0, w, h),   // □
                new Rectangle(right - 1 * w, 0, w, h)    // ✕
            };
        }

        int HitButton(Point p)
        {
            var rs = ButtonRects();
            for (int i = 0; i < rs.Length; i++)
                if (rs[i].Contains(p)) return i;
            return -1;
        }

        // ---------------- 透明度 ----------------
        void ApplyAlpha()
        {
            alpha = Clamp(alpha, 0.10f, 1f);
            if (Handle == IntPtr.Zero) return;
            SetLayeredWindowAttributes(Handle, 0, (byte)Math.Round(alpha * 255f), 0x2 /*LWA_ALPHA*/);
            Invalidate();
        }

        static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        // ---------------- 点击穿透 ----------------
        void SetClickThrough(bool on)
        {
            clickThrough = on;
            if (Handle == IntPtr.Zero) return;
            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            if (on) ex |= WS_EX_TRANSPARENT;
            else ex &= ~WS_EX_TRANSPARENT;
            SetWindowLong(Handle, GWL_EXSTYLE, ex);
            UpdateTray();
        }

        // ---------------- 窗口消息 ----------------
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HOTKEY_TOGGLE) ToggleVisible();
                else if (id == HOTKEY_THROUGH) SetClickThrough(!clickThrough);
                return;
            }

            if (m.Msg == WM_NCHITTEST && !clickThrough && !maximized)
            {
                base.WndProc(ref m);
                if ((int)m.Result == 1 /*HTCLIENT*/)
                {
                    int lp = m.LParam.ToInt32();
                    short sx = unchecked((short)(lp & 0xFFFF));
                    short sy = unchecked((short)((lp >> 16) & 0xFFFF));
                    Point p = PointToClient(new Point(sx, sy));
                    const int edge = 8;
                    bool l = p.X <= edge, r = p.X >= ClientSize.Width - edge;
                    bool t = p.Y <= edge, b = p.Y >= ClientSize.Height - edge;
                    if (t && l) m.Result = (IntPtr)13;
                    else if (t && r) m.Result = (IntPtr)14;
                    else if (b && l) m.Result = (IntPtr)16;
                    else if (b && r) m.Result = (IntPtr)17;
                    else if (l) m.Result = (IntPtr)10;
                    else if (r) m.Result = (IntPtr)11;
                    else if (t) m.Result = (IntPtr)12;
                    else if (b) m.Result = (IntPtr)15;
                }
                return;
            }

            base.WndProc(ref m);
        }

        // ---------------- 绘制 ----------------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;

            using (var bg = new SolidBrush(BackColor))
                g.FillRectangle(bg, ClientRectangle);

            Rectangle cr = ContentRect;
            if (image != null)
            {
                SizeF fit = Fit(image.Size, cr.Size);
                float w = fit.Width * zoom;
                float h = fit.Height * zoom;
                float x = cr.Left + (cr.Width - w) / 2f + offX;
                float y = cr.Top + (cr.Height - h) / 2f + offY;
                g.DrawImage(image, new RectangleF(x, y, w, h));
            }
            else
            {
                DrawWelcome(g, cr);
            }

            // 缩放倍率提示
            if (zoom > 1.01f)
            {
                string z = zoom.ToString("0.0") + "x";
                using (var f = new Font("Consolas", 9f))
                using (var b = new SolidBrush(Color.FromArgb(210, 255, 220, 120)))
                    g.DrawString(z, f, b, S(8), ContentRect.Top + S(4));
            }

            DrawHeader(g);
            DrawMenuOverlay(g);

            // 1px 边框，方便看清窗口边界
            using (var pen = new Pen(Color.FromArgb(140, 0, 190, 255), 1f))
                g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        void DrawHeader(Graphics g)
        {
            Rectangle hr = HeaderRect;
            using (var b = new SolidBrush(Color.FromArgb(215, 28, 28, 30)))
                g.FillRectangle(b, hr);
            using (var p = new Pen(Color.FromArgb(55, 255, 255, 255), 1f))
                g.DrawLine(p, hr.Left, hr.Bottom - 1, hr.Right, hr.Bottom - 1);

            var rs = ButtonRects();

            // 标题文字（文件名）
            Rectangle txt = new Rectangle(S(10), 0, Math.Max(10, rs[0].Left - S(18)), hr.Height);
            string title = "Co_cofe 建模看图工具";
            if (files.Count > 0 && index < files.Count) title = Path.GetFileName(files[index]);
            using (var f = new Font("Microsoft YaHei UI", 9f))
                TextRenderer.DrawText(g, title, f, txt,
                    Color.FromArgb(230, 225, 225, 225),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            for (int i = 0; i < rs.Length; i++)
                DrawButton(g, rs[i], i, i == hoverBtn, i == pressBtn);
        }

        void DrawButton(Graphics g, Rectangle r, int idx, bool hover, bool pressed)
        {
            if (hover || pressed)
            {
                Color back = idx == BTN_CLOSE
                    ? Color.FromArgb(pressed ? 230 : 200, 196, 43, 28)
                    : Color.FromArgb(pressed ? 120 : 80, 255, 255, 255);
                using (var b = new SolidBrush(back))
                    g.FillRectangle(b, r);
            }

            Color fg = Color.FromArgb(235, 240, 240, 240);
            int cx = r.Left + r.Width / 2;
            int cy = r.Top + r.Height / 2;
            int s = S(5);

            if (idx == BTN_HELP)
            {
                using (var f = new Font("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point))
                using (var b = new SolidBrush(fg))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("?", f, b, r, sf);
                return;
            }

            using (var pen = new Pen(fg, Math.Max(1f, SF(1.15f))))
            {
                switch (idx)
                {
                    case BTN_MIN:
                        g.DrawLine(pen, cx - s, cy, cx + s, cy);
                        break;
                    case BTN_MAX:
                        if (maximized)
                        {
                            // 还原图标：两个叠起来的方框
                            g.DrawRectangle(pen, cx - s + S(2), cy - s, s * 2 - S(2), s * 2 - S(2));
                            g.DrawLine(pen, cx - s, cy - s + S(2), cx - s, cy + s);
                            g.DrawLine(pen, cx - s, cy + s, cx + s - S(2), cy + s);
                        }
                        else
                        {
                            g.DrawRectangle(pen, cx - s, cy - s, s * 2, s * 2);
                        }
                        break;
                    case BTN_CLOSE:
                        g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
                        g.DrawLine(pen, cx - s, cy + s, cx + s, cy - s);
                        break;
                }
            }
        }

        static SizeF Fit(Size img, Size box)
        {
            if (img.Width <= 0 || img.Height <= 0 || box.Width <= 0 || box.Height <= 0)
                return new SizeF(0, 0);
            float s = Math.Min((float)box.Width / img.Width, (float)box.Height / img.Height);
            return new SizeF(img.Width * s, img.Height * s);
        }

        // ---------------- 欢迎页 ----------------
        void LoadWelcome()
        {
            // 1) 优先用程序目录下的外部底图（用户可替换）
            try
            {
                string[] cands = { "welcome.jpg", "welcome.png", "welcome.jpeg", "welcome.bmp" };
                foreach (string name in cands)
                {
                    string p = Path.Combine(Application.StartupPath, name);
                    if (!File.Exists(p)) continue;
                    using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read))
                    using (var tmp = Image.FromStream(fs))
                        welcome = new Bitmap(tmp);
                    return;
                }
            }
            catch { }

            // 2) 没有外部底图，用编译进 exe 的内置底图（单文件也能显示完整欢迎页）
            try
            {
                using (Stream s = typeof(PinForm).Assembly.GetManifestResourceStream("WELCOME_BG"))
                {
                    if (s != null)
                    {
                        using (var tmp = Image.FromStream(s))
                            welcome = new Bitmap(tmp);
                    }
                }
            }
            catch { }
        }

        void DrawWelcome(Graphics g, Rectangle cr)
        {
            // 底图：铺满窗口（等比放大后裁切，裁切稍微偏上，保证小人完整）
            if (welcome != null)
            {
                float s = Math.Max((float)cr.Width / welcome.Width, (float)cr.Height / welcome.Height);
                float w = welcome.Width * s;
                float h = welcome.Height * s;
                g.DrawImage(welcome, new RectangleF(
                    cr.Left + (cr.Width - w) / 2f,
                    cr.Top - (h - cr.Height) * 0.15f, w, h));
            }
            else
            {
                using (var b = new SolidBrush(Color.FromArgb(250, 243, 221, 236)))
                    g.FillRectangle(b, cr);
            }

            Color ink1 = Color.FromArgb(255, 62, 38, 72);
            Color ink2 = Color.FromArgb(240, 86, 56, 98);
            Color ink3 = Color.FromArgb(225, 108, 78, 120);

            int left = cr.Left + (int)(cr.Width * 0.06);
            int width = (int)(cr.Width * 0.88);
            int y = cr.Top + (int)(cr.Height * 0.545);

            var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Near,
                FormatFlags = StringFormatFlags.NoWrap
            };

            using (var f = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold))
            using (var br = new SolidBrush(ink1))
                g.DrawString("Co_cofe 建模看图工具", f, br, new RectangleF(left, y, width, S(56)), sf);
            y += S(52);

            using (var pen = new Pen(Color.FromArgb(110, 122, 74, 122), Math.Max(1f, SF(1.2f))))
            {
                int cx = cr.Left + cr.Width / 2;
                g.DrawLine(pen, cx - S(42), y, cx + S(42), y);
            }
            y += S(20);

            using (var f = new Font("Microsoft YaHei UI", 10f))
            using (var br = new SolidBrush(ink2))
                g.DrawString("欢迎使用 · 专治「建模时看图纸要来回切窗口」", f, br,
                    new RectangleF(left, y, width, S(30)), sf);
            y += S(38);

            using (var f = new Font("Microsoft YaHei UI", 9.5f))
            using (var br = new SolidBrush(ink3))
            {
                g.DrawString("了解用法请点标题栏的 ?，或右键 →「使用说明」", f, br,
                    new RectangleF(left, y, width, S(28)), sf);
                y += S(28);
                g.DrawString("把 JPG / PNG 图纸拖进窗口即可", f, br,
                    new RectangleF(left, y, width, S(28)), sf);
            }

            // 右下角署名
            sf.Alignment = StringAlignment.Far;
            var credit = new RectangleF(left, cr.Bottom - S(72), width, S(26));
            using (var cf1 = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold))
            using (var cb1 = new SolidBrush(Color.FromArgb(220, 78, 44, 92)))
                g.DrawString("冷咖啡制作", cf1, cb1, credit, sf);
            credit.Y += S(24);
            using (var cf2 = new Font("Segoe UI", 8.5f))
            using (var cb2 = new SolidBrush(Color.FromArgb(195, 110, 78, 122)))
                g.DrawString("Co_cofe", cf2, cb2, credit, sf);
            sf.Alignment = StringAlignment.Center;

            sf.Dispose();
        }

        // ---------------- 按钮动作 ----------------
        void DoButton(int idx)
        {
            switch (idx)
            {
                case BTN_HELP: ShowHelp(); break;
                case BTN_MIN: Hide(); break;
                case BTN_MAX: ToggleMaximize(); break;
                case BTN_CLOSE: Close(); break;
            }
        }

        void ToggleMaximize()
        {
            if (!maximized)
            {
                restoreBounds = Bounds;
                maximized = true;
                Bounds = Screen.FromControl(this).WorkingArea;
            }
            else
            {
                maximized = false;
                Bounds = restoreBounds;
            }
            Invalidate();
        }

        // ---------------- 鼠标 ----------------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (menuOpen)
            {
                if (e.Button == MouseButtons.Left) { menuPress = MenuHitTest(e.Location); return; }
                if (e.Button == MouseButtons.Right) { CloseMenu(); return; }
            }

            if (e.Button != MouseButtons.Left) return;

            int bi = HitButton(e.Location);
            if (bi >= 0)
            {
                pressBtn = bi;
                hoverBtn = bi;
                Invalidate();
                return;
            }

            pressScreen = Cursor.Position;
            Capture = true;

            if (HeaderRect.Contains(e.Location))
            {
                if (!maximized) { dragging = true; winAtPress = Location; }
                return;
            }

            if (zoom > 1.01f && (ModifierKeys & Keys.Control) == Keys.Control)
            {
                panning = true; panOffX = offX; panOffY = offY;
            }
            else
            {
                dragging = true; winAtPress = Location;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (menuOpen)
            {
                int hov = MenuHitTest(e.Location);
                if (hov != menuHover) { menuHover = hov; Invalidate(); }
                return;
            }

            int bi = HitButton(e.Location);
            if (bi != hoverBtn)
            {
                hoverBtn = bi;
                Invalidate();
            }

            if (dragging)
            {
                Location = new Point(
                    winAtPress.X + (Cursor.Position.X - pressScreen.X),
                    winAtPress.Y + (Cursor.Position.Y - pressScreen.Y));
            }
            else if (panning)
            {
                offX = panOffX + (Cursor.Position.X - pressScreen.X);
                offY = panOffY + (Cursor.Position.Y - pressScreen.Y);
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (menuOpen && e.Button == MouseButtons.Left)
            {
                int idx = MenuHitTest(e.Location);
                int pressed = menuPress;
                menuPress = -1;
                if (idx >= 0 && idx == pressed && menuItems != null && menuItems[idx].Enabled)
                {
                    Action act = menuItems[idx].Run;
                    CloseMenu();
                    if (act != null) act();
                }
                else
                {
                    CloseMenu();
                }
                return;
            }

            if (pressBtn >= 0)
            {
                int act = pressBtn;
                pressBtn = -1;
                if (HitButton(e.Location) == act) DoButton(act);
                Invalidate();
                return;
            }

            if (dragging || panning)
            {
                dragging = false; panning = false; Capture = false;
                SaveState();
            }

            if (e.Button == MouseButtons.Right) OpenMenu(e.Location);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverBtn != -1) { hoverBtn = -1; Invalidate(); }
            if (menuOpen) CloseMenu();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            Point p = PointToClient(Cursor.Position);
            if (HeaderRect.Contains(p)) { ToggleMaximize(); return; }
            zoom = 1f; offX = offY = 0; Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                alpha += (e.Delta > 0 ? 0.05f : -0.05f);
                ApplyAlpha(); SaveState();
            }
            else if ((ModifierKeys & Keys.Shift) == Keys.Shift)
            {
                Cycle(e.Delta > 0 ? -1 : 1);
            }
            else
            {
                ZoomAt(e.Location, e.Delta > 0 ? 1.1f : 1f / 1.1f);
            }
        }

        void ZoomAt(Point p, float factor)
        {
            float old = zoom;
            zoom = Clamp(zoom * factor, 1f, 10f);
            if (zoom <= 1.001f) { zoom = 1f; offX = offY = 0; }
            else
            {
                Rectangle cr = ContentRect;
                float dX = p.X - (cr.Left + cr.Width / 2f);
                float dY = p.Y - (cr.Top + cr.Height / 2f);
                float k = zoom / old;
                offX = dX - (dX - offX) * k;
                offY = dY - (dY - offY) * k;
            }
            Invalidate();
        }

        // ---------------- 右键菜单（自绘，避免抢焦点打不开） ----------------
        int MenuItemH { get { return S(30); } }

        void OpenMenu(Point p)
        {
            menuItems = BuildMenuItems();
            int pad = S(6);
            int h = pad;
            foreach (var it in menuItems) h += it.Separator ? S(9) : MenuItemH;
            h += pad;

            int w = S(210);
            int x = Math.Min(p.X, ClientSize.Width - w - S(4));
            int y = Math.Min(p.Y, ClientSize.Height - h - S(4));
            x = Math.Max(S(4), x);
            y = Math.Max(S(4), y);

            menuRect = new Rectangle(x, y, w, h);
            menuOpen = true;
            menuHover = -1;
            menuPress = -1;
            Invalidate();
        }

        void CloseMenu()
        {
            if (!menuOpen) return;
            menuOpen = false;
            menuHover = -1;
            menuPress = -1;
            menuItems = null;
            Invalidate();
        }

        /// <summary>返回命中的菜单项序号；-1 在菜单内但没命中项；-2 在菜单外</summary>
        int MenuHitTest(Point p)
        {
            if (!menuOpen || menuItems == null) return -2;
            if (!menuRect.Contains(p)) return -2;
            int pad = S(6);
            int y = menuRect.Y + pad;
            for (int i = 0; i < menuItems.Count; i++)
            {
                MenuItem it = menuItems[i];
                if (it.Separator) { y += S(9); continue; }
                var r = new Rectangle(menuRect.X + pad, y, menuRect.Width - pad * 2, MenuItemH);
                if (r.Contains(p)) return i;
                y += MenuItemH;
            }
            return -1;
        }

        void DrawMenuOverlay(Graphics g)
        {
            if (!menuOpen || menuItems == null) return;

            using (var b = new SolidBrush(Color.FromArgb(244, 34, 34, 38)))
                g.FillRectangle(b, menuRect);
            using (var pen = new Pen(Color.FromArgb(150, 0, 190, 255), 1f))
                g.DrawRectangle(pen, menuRect.X, menuRect.Y, menuRect.Width - 1, menuRect.Height - 1);

            int pad = S(6);
            int y = menuRect.Y + pad;
            for (int i = 0; i < menuItems.Count; i++)
            {
                MenuItem it = menuItems[i];
                if (it.Separator)
                {
                    using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1f))
                        g.DrawLine(pen, menuRect.X + pad, y + S(4), menuRect.Right - pad, y + S(4));
                    y += S(9);
                    continue;
                }

                var r = new Rectangle(menuRect.X + pad, y, menuRect.Width - pad * 2, MenuItemH);
                if (i == menuHover && it.Enabled)
                {
                    using (var b = new SolidBrush(Color.FromArgb(95, 255, 255, 255)))
                        g.FillRectangle(b, r);
                }

                Color fg = it.Enabled
                    ? Color.FromArgb(236, 236, 236, 236)
                    : Color.FromArgb(110, 175, 175, 175);

                using (var f = new Font("Microsoft YaHei UI", 9f))
                    TextRenderer.DrawText(g, it.Text, f,
                        new Rectangle(r.X + S(10), r.Y, r.Width - S(34), r.Height), fg,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                if (it.Checked)
                {
                    using (var pen = new Pen(Color.FromArgb(225, 120, 210, 255), Math.Max(1f, SF(1.6f))))
                    {
                        int cx = r.Right - S(16), cy = r.Y + r.Height / 2;
                        g.DrawLine(pen, cx - S(4), cy, cx - S(1), cy + S(4));
                        g.DrawLine(pen, cx - S(1), cy + S(4), cx + S(5), cy - S(4));
                    }
                }
                y += MenuItemH;
            }
        }

        List<MenuItem> BuildMenuItems()
        {
            var list = new List<MenuItem>();
            list.Add(new MenuItem { Text = "使用说明…", Run = delegate { ShowHelp(); } });
            list.Add(new MenuItem { Text = "打开图纸…", Run = delegate { OpenDialog(); } });
            list.Add(new MenuItem { Separator = true });
            list.Add(new MenuItem { Text = "上一张", Enabled = files.Count > 1, Run = delegate { Cycle(-1); } });
            list.Add(new MenuItem { Text = "下一张", Enabled = files.Count > 1, Run = delegate { Cycle(1); } });
            list.Add(new MenuItem { Separator = true });
            list.Add(new MenuItem { Text = "点击穿透", Checked = clickThrough, Run = delegate { SetClickThrough(!clickThrough); } });
            list.Add(new MenuItem { Text = "复位缩放", Run = delegate { zoom = 1f; offX = offY = 0; Invalidate(); } });
            list.Add(new MenuItem
            {
                Text = "透明度 " + (int)Math.Round(alpha * 100) + "%　(点击切换)",
                Run = delegate { CycleAlpha(); }
            });
            list.Add(new MenuItem { Separator = true });
            list.Add(new MenuItem { Text = "隐藏  Ctrl+Alt+O", Run = delegate { Hide(); } });
            list.Add(new MenuItem { Text = "退出", Run = delegate { Close(); } });
            return list;
        }

        void CycleAlpha()
        {
            float[] levels = { 1f, 0.9f, 0.8f, 0.7f, 0.6f, 0.5f, 0.4f, 0.3f, 0.2f };
            int cur = 0;
            float best = 9f;
            for (int i = 0; i < levels.Length; i++)
            {
                float d = Math.Abs(levels[i] - alpha);
                if (d < best) { best = d; cur = i; }
            }
            alpha = levels[(cur + 1) % levels.Length];
            ApplyAlpha();
            SaveState();
        }

        // ---------------- 使用说明窗口 ----------------
        void ShowHelp()
        {
            var f = new Form();
            f.Text = "Co_cofe建模看图工具 · 使用说明";
            f.StartPosition = FormStartPosition.CenterScreen;
            f.FormBorderStyle = FormBorderStyle.Sizable;
            f.MinimizeBox = false;
            f.MaximizeBox = true;
            f.ShowInTaskbar = false;
            f.TopMost = true;
            f.AutoScaleMode = AutoScaleMode.Font;
            f.Font = new Font("Microsoft YaHei UI", 9.75f);
            f.ClientSize = new Size(700, 640);
            f.MinimumSize = new Size(420, 320);

            var tb = new TextBox();
            tb.Multiline = true;
            tb.ReadOnly = true;
            tb.ScrollBars = ScrollBars.Both;
            tb.WordWrap = false;
            tb.Dock = DockStyle.Fill;
            tb.BorderStyle = BorderStyle.None;
            tb.BackColor = Color.FromArgb(250, 250, 250);
            tb.Font = new Font("Microsoft YaHei UI", 9.75f);
            tb.Text = LoadHelpText().Replace("\r\n", "\n").Replace("\n", "\r\n");
            tb.Select(0, 0);

            var bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 48;

            var ok = new Button();
            ok.Text = "关闭";
            ok.Width = 96;
            ok.Height = 30;
            ok.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            ok.Location = new Point(f.ClientSize.Width - 96 - 16, 10);
            ok.Click += delegate { f.Close(); };

            bottom.Controls.Add(ok);
            f.Controls.Add(tb);
            f.Controls.Add(bottom);
            f.AcceptButton = ok;
            f.KeyPreview = true;
            f.KeyDown += delegate (object s, KeyEventArgs ev)
            {
                if (ev.KeyCode == Keys.Escape) f.Close();
            };

            f.Show(this);
            f.BringToFront();
        }

        string LoadHelpText()
        {
            try
            {
                string p = Path.Combine(Application.StartupPath, "使用说明.txt");
                if (File.Exists(p))
                {
                    string t = File.ReadAllText(p, Encoding.UTF8);
                    if (t.Length > 20) return t;
                }
            }
            catch { }
            return BuiltinHelp;
        }

        const string BuiltinHelp =
@"Co_cofe建模看图工具 使用说明
==============================

【标题栏按钮】
  ?      打开本说明
  —      最小化到托盘（Ctrl+Alt+O 可再叫出来）
  □      最大化 / 还原（双击标题栏同效）
  ✕      退出程序

【基本操作】
  移动窗口        鼠标左键拖动画面，或拖动标题栏
  改窗口大小      拖动窗口四条边 / 四个角（图片自动跟着缩放）
  透明度          右键菜单选百分比；或 Ctrl + 滚轮无级微调（5% 一档）
  放大 / 缩小     滚轮（1x 为适应窗口，最大 10x）
  复位缩放        双击画面
  平移放大后的图  Ctrl + 左键拖动
  切换多张图      Shift + 滚轮；也支持一次拖入多张图片或整个文件夹
  打开文件        右键菜单「打开图纸…」
  点击穿透        右键菜单 / Ctrl+Alt+P，开启后鼠标直接点到底下的 SolidWorks
  隐藏 / 显示     Ctrl+Alt+O
  退出            标题栏 ✕，或右键菜单，或托盘图标右键

【几点设计说明】
  · 窗口不抢焦点（WS_EX_NOACTIVATE）：点它、拖它，SolidWorks 里的命令和
    键盘输入都不会中断，不用再点回 SW 激活。
  · 整窗半透明用分层窗口（WS_EX_LAYERED + LWA_ALPHA），图纸后面的模型、
    任务窗格都能透出来对照。
  · 置顶是 WS_EX_TOPMOST，SolidWorks 最大化也压不住它。

【已知限制】
  · 只支持图片格式：png / jpg / bmp / gif / tif / tiff 等，暂不支持 PDF。
    PDF 图纸可以先把页面截图或导出成 PNG。
  · 透明度作用于整个窗口（含边框），不能只让图纸白底变透明。

【配置文件】
  %APPDATA%\SwPin\state.txt  记录窗口位置、大小、透明度、上次打开的图。
  删掉该文件即恢复默认。

【本说明文件】
  程序目录下的 使用说明.txt 会被优先读取；想改说明文字直接改那个文件即可。
";

        // ---------------- 文件 ----------------
        public void QueueFiles(IEnumerable<string> paths)
        {
            var added = new List<string>();
            foreach (string p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (Directory.Exists(p))
                {
                    foreach (string f in Directory.GetFiles(p))
                        if (IsImage(f)) added.Add(f);
                }
                else if (File.Exists(p) && IsImage(p))
                {
                    added.Add(p);
                }
            }
            if (added.Count == 0) return;

            files.Clear();
            files.AddRange(added);
            index = 0;
            if (IsHandleCreated) LoadImage(files[0]);
        }

        static bool IsImage(string path)
        {
            string e = Path.GetExtension(path).ToLowerInvariant();
            return Array.IndexOf(Exts, e) >= 0;
        }

        void OpenDialog()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择图纸图片 - Co_cofe建模看图工具";
                dlg.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*";
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    files.Clear(); files.Add(dlg.FileName); index = 0;
                    LoadImage(dlg.FileName);
                }
            }
        }

        void Cycle(int step)
        {
            if (files.Count < 2) return;
            index = (index + step + files.Count) % files.Count;
            LoadImage(files[index]);
        }

        void LoadImage(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (var tmp = Image.FromStream(fs))
                {
                    var copy = new Bitmap(tmp);
                    if (image != null) image.Dispose();
                    image = copy;
                }
                zoom = 1f; offX = offY = 0;
                Text = "Co_cofe建模看图工具 - " + Path.GetFileName(path);
                UpdateTray();
                Invalidate();
                SaveState();
            }
            catch (Exception ex)
            {
                MessageBox.Show("打不开这个文件：\r\n" + ex.Message, "Co_cofe建模看图工具",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            var arr = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (arr != null) QueueFiles(arr);
        }

        // ---------------- 托盘 ----------------
        void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = (this.Icon != null) ? this.Icon : SystemIcons.Application;
            tray.Visible = true;
            tray.Text = "Co_cofe 建模看图工具";

            var menu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 9f) };
            menu.Items.Add("显示 / 隐藏  Ctrl+Alt+O", null, delegate { ToggleVisible(); });
            menu.Items.Add("使用说明…", null, delegate { ShowHelp(); });
            menu.Items.Add("打开图纸…", null, delegate { OpenDialog(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Close(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ToggleVisible(); };
            UpdateTray();
        }

        void UpdateTray()
        {
            if (tray == null) return;
            string tip = "Co_cofe 建模看图工具";
            if (files.Count > 0 && index < files.Count) tip += " - " + Path.GetFileName(files[index]);
            if (clickThrough) tip += "（穿透中）";
            if (tip.Length > 62) tip = tip.Substring(0, 62);
            tray.Text = tip;
        }

        void ToggleVisible()
        {
            if (Visible) Hide();
            else { Show(); Invalidate(); }
        }

        // ---------------- 状态持久化 ----------------
        void SaveState()
        {
            try
            {
                Directory.CreateDirectory(stateDir);
                var sb = new StringBuilder();
                sb.AppendLine("x=" + Left);
                sb.AppendLine("y=" + Top);
                sb.AppendLine("w=" + Width);
                sb.AppendLine("h=" + Height);
                sb.AppendLine("alpha=" + alpha.ToString("0.###", CultureInfo.InvariantCulture));
                if (files.Count > 0 && index < files.Count) sb.AppendLine("file=" + files[index]);
                File.WriteAllText(StatePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        void LoadState()
        {
            int x = -1, y = -1, w = 560, h = 780;
            try
            {
                if (File.Exists(StatePath))
                {
                    foreach (string line in File.ReadAllLines(StatePath))
                    {
                        int p = line.IndexOf('=');
                        if (p <= 0) continue;
                        string k = line.Substring(0, p), v = line.Substring(p + 1);
                        switch (k)
                        {
                            case "x": int.TryParse(v, out x); break;
                            case "y": int.TryParse(v, out y); break;
                            case "w": int.TryParse(v, out w); break;
                            case "h": int.TryParse(v, out h); break;
                            case "alpha":
                                float a;
                                if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out a))
                                    alpha = Clamp(a, 0.1f, 1f);
                                break;
                            case "file":
                                if (File.Exists(v)) { files.Clear(); files.Add(v); }
                                break;
                        }
                    }
                }
            }
            catch { }

            w = Math.Max(200, w);
            h = Math.Max(120, h);
            if (x < 0 || y < 0)
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                x = wa.Right - w - 48;
                y = wa.Top + 64;
            }
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(x, y, w, h);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveState();
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            if (image != null) { image.Dispose(); image = null; }
            if (welcome != null) { welcome.Dispose(); welcome = null; }
            base.OnFormClosing(e);
        }
    }
}
