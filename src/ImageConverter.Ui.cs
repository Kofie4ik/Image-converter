// Конвертер картинок — interface: dark/light theme, custom-drawn controls, thumbnails, sizes before/after.
// Must stay C# 5 compatible (built with the .NET Framework csc.exe).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ImageConverter
{
    // UI language: Russian or English. Every visible string goes through T(ru, en).
    static class Lang
    {
        public static bool En;

        public static string T(string ru, string en) { return En ? en : ru; }

        public static bool SystemPrefersEnglish()
        {
            string l = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return !(l == "ru" || l == "uk" || l == "be" || l == "kk");
        }

        public static CultureInfo Numbers { get { return En ? CultureInfo.InvariantCulture : CultureInfo.CurrentCulture; } }
    }

    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint winIni);

        // Windows "Animation effects" (Settings → Accessibility → Visual effects); true if unknown
        public static bool AnimationsEnabled()
        {
            try { bool on = true; return !SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, ref on, 0) || on; }
            catch { return true; }
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length >= 3 && args[0] == "--cli") return Cli(args);
            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length >= 2 && args[0] == "--make-icon") return MakeIcon(args[1]);
            if (args.Length >= 2 && args[0] == "--screenshot") return Screenshot(args);
            Theme.Apply(Theme.LoadDark());
            Lang.En = Theme.LoadEnglish();
            Application.Run(new MainForm(args));
            return 0;
        }

        // --cli <in> <out.ext> [WxH] [fill|fit|stretch] [quality]; exit code 0 = ok, 1 = error, 2 = format unavailable
        static int Cli(string[] a)
        {
            try
            {
                int w = 0, h = 0, mode = 0, q = 90;
                for (int i = 3; i < a.Length; i++)
                {
                    string s = a[i].ToLowerInvariant();
                    if (s.Contains("x") && char.IsDigit(s[0]))
                    {
                        string[] p = s.Split('x'); w = int.Parse(p[0]); h = int.Parse(p[1]);
                        if (mode == 0) mode = 1;
                    }
                    else if (s == "fill") mode = 1;
                    else if (s == "fit") mode = 2;
                    else if (s == "stretch") mode = 3;
                    else int.TryParse(s, out q);
                }
                OutFormat f = OutFormat.FromExt(Path.GetExtension(a[2]));
                if (f == null || !f.IsAvailable) return 2;
                Converter.ConvertFile(a[1], a[2], f, q, mode, w, h);
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(a[2] + ".error.txt", ex.ToString()); } catch { }
                return 1;
            }
        }

        // --make-icon <out.ico>: multi-size icon drawn by Logo.Draw (used by the build)
        static int MakeIcon(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            List<byte[]> pngs = new List<byte[]>();
            foreach (int s in sizes)
                using (Bitmap b = new Bitmap(s, s, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(Color.Transparent);
                        Logo.Draw(g, new RectangleF(0, 0, s, s));
                    }
                    using (MemoryStream ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }
                }
            using (FileStream fs = File.Create(path))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32); w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] p in pngs) w.Write(p);
            }
            return 0;
        }

        // --screenshot <out.png> [--light] [--convert <dir>] [files...]: render the window to a PNG (layout check)
        static int Screenshot(string[] a)
        {
            Theme.SaveDisabled = true;
            bool light = false, wide = false, switchLang = false, spamTheme = false; string convertDir = null;
            List<string> files = new List<string>();
            for (int i = 2; i < a.Length; i++)
            {
                if (a[i] == "--light") light = true;
                else if (a[i] == "--wide") wide = true;
                else if (a[i] == "--en") Lang.En = true;
                else if (a[i] == "--switch-lang") switchLang = true;
                else if (a[i] == "--spam-theme") spamTheme = true;
                else if (a[i] == "--convert" && i + 1 < a.Length) convertDir = a[++i];
                else files.Add(a[i]);
            }
            Theme.Apply(!light);
            using (MainForm f = new MainForm(files.ToArray()))
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-20000, -20000);
                f.ShowInTaskbar = false;
                f.Show();
                if (wide) f.Width = f.Width * 3 / 2;
                Stopwatch sw = Stopwatch.StartNew();
                while (!f.ThumbsReady && sw.ElapsedMilliseconds < 20000) { Application.DoEvents(); Thread.Sleep(15); }
                if (convertDir != null)
                {
                    f.TestConvert(convertDir);
                    while (f.Busy && sw.ElapsedMilliseconds < 120000) { Application.DoEvents(); Thread.Sleep(15); }
                }
                while (!f.ThumbsReady && sw.ElapsedMilliseconds < 120000) { Application.DoEvents(); Thread.Sleep(15); }
                if (switchLang)
                {
                    MainForm.DebugSnapshotPath = a[1] + ".before.png";
                    f.SwitchLanguage();
                    Stopwatch fade = Stopwatch.StartNew();
                    while (fade.ElapsedMilliseconds < 600) { Application.DoEvents(); Thread.Sleep(10); }
                }
                if (spamTheme)
                {
                    // hammer the theme button: 20 clicks every 50 ms for 1 s, then again after a 1.2 s pause
                    for (int round = 0; round < 2; round++)
                    {
                        for (int i = 0; i < 20; i++) { f.ToggleTheme(); Stopwatch st = Stopwatch.StartNew(); while (st.ElapsedMilliseconds < 50) { Application.DoEvents(); Thread.Sleep(5); } }
                        Stopwatch pause = Stopwatch.StartNew(); while (pause.ElapsedMilliseconds < 1200) { Application.DoEvents(); Thread.Sleep(10); }
                    }
                }
                for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(10); }
                File.WriteAllText(a[1] + ".txt", f.DebugReport());
                using (Bitmap b = new Bitmap(f.Width, f.Height))
                {
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        IntPtr hdc = g.GetHdc();
                        Native.PrintWindow(f.Handle, hdc, 2);
                        g.ReleaseHdc(hdc);
                    }
                    b.Save(a[1], ImageFormat.Png);
                }
                f.Close();
            }
            return 0;
        }
    }

    // ------------------------------------------------------------------ theme

    static class Theme
    {
        public static bool Dark = true;
        public static Color Bg, Surface, Surface2, Border, Text, Muted, Accent, AccentHover, AccentPressed, OnAccent, Danger, Success, Selection, Hover;

        static Theme() { Apply(true); }

        static Color H(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(255,
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static void Apply(bool dark)
        {
            Dark = dark;
            if (dark)
            {
                Bg = H(0x111317); Surface = H(0x1A1D23); Surface2 = H(0x242830); Border = H(0x30353E);
                Text = H(0xE9EBEF); Muted = H(0x8C93A1);
                Accent = H(0x4F8BFF); AccentHover = H(0x6B9DFF); AccentPressed = H(0x3F76E0);
                Danger = H(0xFF6B6B); Success = H(0x4ACB86);
            }
            else
            {
                Bg = H(0xF1F3F7); Surface = H(0xFFFFFF); Surface2 = H(0xF0F2F6); Border = H(0xDADEE6);
                Text = H(0x1A1D23); Muted = H(0x6B7280);
                Accent = H(0x2F6FEB); AccentHover = H(0x2462DA); AccentPressed = H(0x1D54BE);
                Danger = H(0xD63B3B); Success = H(0x1E9B58);
            }
            OnAccent = Color.White;
            Selection = Mix(Surface, Accent, dark ? 0.22 : 0.12);
            Hover = Mix(Surface, Text, dark ? 0.05 : 0.035);
        }

        static string SettingsPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImageConverter", "settings.ini"); }
        }

        // settings.ini: "key=value" lines (theme, lang)
        static Dictionary<string, string> ReadSettings()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            try
            {
                if (File.Exists(SettingsPath))
                    foreach (string line in File.ReadAllLines(SettingsPath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
            }
            catch { }
            return d;
        }

        public static bool LoadDark()
        {
            string v;
            return !(ReadSettings().TryGetValue("theme", out v) && v == "light");
        }

        public static bool LoadEnglish()
        {
            string v;
            if (ReadSettings().TryGetValue("lang", out v)) return v == "en";
            return Lang.SystemPrefersEnglish();
        }

        public static bool SaveDisabled;             // test runs must not overwrite the user's settings

        public static void Save()
        {
            if (SaveDisabled) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath, "theme=" + (Dark ? "dark" : "light") + "\r\nlang=" + (Lang.En ? "en" : "ru") + "\r\n");
            }
            catch { }
        }
    }

    static class Gfx
    {
        static readonly Dictionary<float, Font> icons = new Dictionary<float, Font>();

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float rad, Color c)
        {
            using (GraphicsPath p = Round(r, rad)) using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, RectangleF r, float rad, Color c, float width)
        {
            using (GraphicsPath p = Round(r, rad)) using (Pen pen = new Pen(c, width)) g.DrawPath(pen, p);
        }

        public static Font Icons(float size)
        {
            Font f;
            if (!icons.TryGetValue(size, out f)) { f = new Font("Segoe MDL2 Assets", size); icons[size] = f; }
            return f;
        }

        public static void Draw(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags fl)
        {
            if (string.IsNullOrEmpty(s)) return;
            TextRenderer.DrawText(g, s, f, r, c, fl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        public static Size Measure(string s, Font f)
        {
            return TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        public static string Bytes(long n)
        {
            if (n < 1024) return n + Lang.T(" Б", " B");
            if (n < 1024 * 1024) return (n / 1024.0).ToString("0", Lang.Numbers) + Lang.T(" КБ", " KB");
            return (n / 1048576.0).ToString("0.0", Lang.Numbers) + Lang.T(" МБ", " MB");
        }

        public const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
        public const TextFormatFlags LeftMid = TextFormatFlags.Left | TextFormatFlags.VerticalCenter;
    }

    static class Logo
    {
        static PointF P(RectangleF r, float s, float x, float y) { return new PointF(r.X + s * x, r.Y + s * y); }

        public static void Draw(Graphics g, RectangleF r)
        {
            float s = r.Width;
            RectangleF box = new RectangleF(r.X + s * 0.03f, r.Y + s * 0.03f, s * 0.94f, s * 0.94f);
            using (GraphicsPath p = Gfx.Round(box, s * 0.24f))
            using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(box.X - 1, box.Y - 1, box.Width + 2, box.Height + 2),
                       Color.FromArgb(0x4F, 0x8B, 0xFF), Color.FromArgb(0x8B, 0x5C, 0xF6), 45f))
                g.FillPath(b, p);

            float sw = Math.Max(1.4f, s * 0.07f);
            RectangleF frame = new RectangleF(r.X + s * 0.23f, r.Y + s * 0.27f, s * 0.54f, s * 0.46f);
            using (GraphicsPath fp = Gfx.Round(frame, s * 0.07f))
            using (Pen pen = new Pen(Color.White, sw))
            using (SolidBrush wb = new SolidBrush(Color.White))
            {
                pen.LineJoin = LineJoin.Round;
                using (Region old = g.Clip)
                {
                    g.SetClip(fp, CombineMode.Intersect);
                    g.FillPolygon(wb, new[] { P(r, s, 0.20f, 0.76f), P(r, s, 0.40f, 0.50f), P(r, s, 0.52f, 0.63f), P(r, s, 0.61f, 0.54f), P(r, s, 0.82f, 0.76f) });
                    float cr = s * 0.055f;
                    g.FillEllipse(wb, r.X + s * 0.625f - cr, r.Y + s * 0.395f - cr, cr * 2, cr * 2);
                    g.Clip = old;
                }
                g.DrawPath(pen, fp);
            }
        }
    }

    // ------------------------------------------------------------------ custom controls

    class ThemedControl : Control
    {
        public ThemedControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        }
        protected float K { get { return DeviceDpi / 96f; } }
        protected Color ParentBg { get { return Parent != null ? Parent.BackColor : Theme.Bg; } }
        protected Graphics Prep(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(ParentBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            return g;
        }
    }

    class RoundPanel : Panel
    {
        public bool Input, Highlight, Dimmed;
        public float Radius = 14;

        public RoundPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        Color ParentBg { get { return Parent != null ? Parent.BackColor : Theme.Bg; } }

        public Color FillColor
        {
            get
            {
                if (!Input) return Theme.Surface;
                return Dimmed ? Theme.Mix(Theme.Surface2, ParentBg, 0.5) : Theme.Surface2;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(ParentBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = DeviceDpi / 96f, rad = (Input ? 8 : Radius) * k;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Gfx.Fill(g, r, rad, FillColor);
            bool hl = Highlight && !Dimmed;
            Color border = hl ? Theme.Accent : (Dimmed ? Theme.Mix(Theme.Border, ParentBg, 0.5) : Theme.Border);
            Gfx.Stroke(g, r, rad, border, hl ? 1.5f * k : 1f);
        }
    }

    class InputBox : RoundPanel
    {
        public readonly TextBox Box = new TextBox();

        public InputBox()
        {
            Input = true;
            Box.BorderStyle = BorderStyle.None;
            Controls.Add(Box);
            Box.GotFocus += delegate { Highlight = true; Invalidate(); };
            Box.LostFocus += delegate { Highlight = false; Invalidate(); };
            Click += delegate { if (!Dimmed) Box.Focus(); };
            Cursor = Cursors.IBeam;
        }

        public void SetDimmed(bool v)
        {
            Dimmed = v;
            Box.ReadOnly = v;
            Box.TabStop = !v;
            RefreshColors();
        }

        public void RefreshColors()
        {
            BackColor = FillColor;
            Box.BackColor = FillColor;
            Box.ForeColor = Dimmed ? Theme.Muted : Theme.Text;
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = (int)(10 * DeviceDpi / 96f);
            Box.SetBounds(pad, Math.Max(0, (Height - Box.Height) / 2), Math.Max(10, Width - 2 * pad), Box.Height);
        }
    }

    class NumBox : InputBox
    {
        readonly int min, max;
        int last;                  // last valid value: an emptied or garbled field falls back to it
        public int Step = 1;

        public NumBox(int min, int max, int value)
        {
            this.min = min; this.max = max;
            last = Math.Max(min, Math.Min(max, value));
            Box.TextAlign = HorizontalAlignment.Center;
            Box.Text = last.ToString();
            Box.KeyPress += delegate(object s, KeyPressEventArgs e) { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            Box.TextChanged += delegate { int v; if (int.TryParse(Box.Text, out v) && v >= min && v <= max) last = v; };
            Box.Leave += delegate { Box.Text = Value.ToString(); };
            Box.MouseWheel += delegate(object s, MouseEventArgs e) { if (!Dimmed) Value = Value + (e.Delta > 0 ? Step : -Step); };
        }

        public int Value
        {
            get
            {
                int v;
                if (!int.TryParse(Box.Text, out v)) return last;
                return Math.Max(min, Math.Min(max, v));
            }
            set { last = Math.Max(min, Math.Min(max, value)); Box.Text = last.ToString(); }
        }
    }

    class FlatButton : ThemedControl
    {
        public enum Kinds { Primary, Secondary, Ghost }
        public Kinds Kind;
        public string Glyph;
        public int HeightDip = 36;
        public bool Square;        // same footprint as an icon-only button (used for the short RU/EN label)
        bool hover, down;

        public FlatButton(string text, string glyph, Kinds kind)
        {
            Text = text; Glyph = glyph; Kind = kind;
            Cursor = Cursors.Hand; TabStop = true; AutoSize = true;
            Margin = new Padding(3);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            float k = K; int h = (int)(HeightDip * k);
            if (string.IsNullOrEmpty(Text) || Square) return new Size(h, h);
            int gw = Glyph != null ? (int)(24 * k) : 0;
            return new Size(Gfx.Measure(Text, Font).Width + gw + (int)(30 * k), h);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); if (Parent != null) Parent.PerformLayout(); base.OnTextChanged(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty); base.OnKeyUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Prep(e);
            float k = K; Color pb = ParentBg, bg, fg, border = Color.Empty;
            switch (Kind)
            {
                case Kinds.Primary:
                    bg = down ? Theme.AccentPressed : hover ? Theme.AccentHover : Theme.Accent; fg = Theme.OnAccent; break;
                case Kinds.Secondary:
                    bg = Theme.Mix(Theme.Surface2, Theme.Text, down ? 0.14 : hover ? 0.07 : 0); fg = Theme.Text; border = Theme.Border; break;
                default:
                    bg = down ? Theme.Mix(pb, Theme.Text, 0.14) : hover ? Theme.Mix(pb, Theme.Text, 0.08) : pb; fg = Theme.Text; break;
            }
            if (!Enabled)
            {
                bg = Theme.Mix(bg, pb, 0.55); fg = Theme.Mix(fg, pb, 0.55);
                if (border != Color.Empty) border = Theme.Mix(border, pb, 0.5);
            }
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Gfx.Fill(g, r, 8 * k, bg);
            if (border != Color.Empty) Gfx.Stroke(g, r, 8 * k, border, 1f);
            if (Focused && ShowFocusCues) Gfx.Stroke(g, new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), 7 * k, Theme.Accent, 1.5f * k);

            Font iconF = Gfx.Icons(10.5f);
            if (string.IsNullOrEmpty(Text)) { Gfx.Draw(g, Glyph, iconF, ClientRectangle, fg, Gfx.Center); return; }
            if (Glyph == null) { Gfx.Draw(g, Text, Font, ClientRectangle, fg, Gfx.Center); return; }
            Size ts = Gfx.Measure(Text, Font);
            int gw = Glyph != null ? (int)(24 * k) : 0;
            int x = (Width - ts.Width - gw) / 2;
            if (Glyph != null) Gfx.Draw(g, Glyph, iconF, new Rectangle(x, 0, (int)(16 * k), Height), fg, Gfx.Center);
            Gfx.Draw(g, Text, Font, new Rectangle(x + gw, 0, ts.Width + 4, Height), fg, Gfx.LeftMid);
        }
    }

    class ThemedColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Surface; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemSelected { get { return Theme.Hover; } }
        public override Color MenuItemBorder { get { return Theme.Hover; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Surface; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Surface; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Surface; } }
    }

    class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        public ThemedMenuRenderer() : base(new ThemedColors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Theme.Surface)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Gfx.Fill(e.Graphics, new RectangleF(3, 1, e.Item.Width - 6, e.Item.Height - 2), 6, Theme.Mix(Theme.Surface, Theme.Text, Theme.Dark ? 0.09 : 0.06));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.ForeColor;
            base.OnRenderItemText(e);
        }
    }

    class SelectBox : ThemedControl
    {
        public readonly List<object> Items = new List<object>();
        public event EventHandler SelectedIndexChanged;
        public event EventHandler Opening;          // raised before the list drops down (lets the owner refresh Items)
        int sel = -1;
        bool hover, open;

        public SelectBox() { Cursor = Cursors.Hand; TabStop = true; }

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                if (sel == value) return;
                sel = value; Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public object SelectedItem { get { return sel >= 0 && sel < Items.Count ? Items[sel] : null; } }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnClick(EventArgs e) { base.OnClick(e); ShowMenu(); }

        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down && sel < Items.Count - 1) SelectedIndex = sel + 1;
            else if (e.KeyCode == Keys.Up && sel > 0) SelectedIndex = sel - 1;
            else if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) ShowMenu();
            base.OnKeyDown(e);
        }

        void ShowMenu()
        {
            if (open) return;
            if (Opening != null) Opening(this, EventArgs.Empty);
            if (Items.Count == 0) return;
            float k = K;
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new ThemedMenuRenderer();
            m.ShowImageMargin = false; m.ShowCheckMargin = false;
            m.Font = Font; m.BackColor = Theme.Surface;
            m.Padding = new Padding((int)(4 * k));
            for (int i = 0; i < Items.Count; i++)
            {
                int idx = i;
                ToolStripMenuItem it = new ToolStripMenuItem(Items[i].ToString());
                it.ForeColor = i == sel ? Theme.Accent : Theme.Text;
                it.Padding = new Padding(0, (int)(6 * k), 0, (int)(6 * k));
                it.Click += delegate { SelectedIndex = idx; };
                m.Items.Add(it);
            }
            m.MinimumSize = new Size(Width, 0);
            m.Closed += delegate { open = false; Invalidate(); BeginInvoke((Action)m.Dispose); };
            open = true; Invalidate();
            m.Show(this, new Point(0, Height + (int)(4 * k)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Prep(e);
            float k = K; Color pb = ParentBg;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill = Enabled ? Theme.Mix(Theme.Surface2, Theme.Text, hover || open ? 0.05 : 0) : Theme.Mix(Theme.Surface2, pb, 0.5);
            bool hl = open || (Focused && ShowFocusCues);
            Gfx.Fill(g, r, 8 * k, fill);
            Gfx.Stroke(g, r, 8 * k, hl ? Theme.Accent : Theme.Border, hl ? 1.5f * k : 1f);
            string text = SelectedItem != null ? SelectedItem.ToString() : "";
            Color fg = Enabled ? Theme.Text : Theme.Muted;
            Gfx.Draw(g, text, Font, new Rectangle((int)(12 * k), 0, Width - (int)(44 * k), Height), fg, Gfx.LeftMid | TextFormatFlags.EndEllipsis);
            Gfx.Draw(g, "", Gfx.Icons(8f), new Rectangle(Width - (int)(34 * k), 0, (int)(22 * k), Height), Theme.Muted, Gfx.Center);
        }
    }

    class ToggleCheck : ThemedControl
    {
        bool chk, hover;
        public event EventHandler CheckedChanged;

        public ToggleCheck(string text) { Text = text; AutoSize = true; Cursor = Cursors.Hand; TabStop = true; }

        public bool Checked
        {
            get { return chk; }
            set
            {
                if (chk == value) return;
                chk = value; Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            float k = K;
            return new Size(Gfx.Measure(Text, Font).Width + (int)(32 * k), (int)(36 * k));
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Prep(e);
            float k = K, bs = 18 * k;
            RectangleF box = new RectangleF(1, (Height - bs) / 2, bs, bs);
            if (chk)
            {
                Gfx.Fill(g, box, 5 * k, !Enabled ? Theme.Mix(Theme.Accent, ParentBg, 0.5) : hover ? Theme.AccentHover : Theme.Accent);
                using (Pen p = new Pen(Theme.OnAccent, 2f * k))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                    g.DrawLines(p, new[] {
                        new PointF(box.X + bs * 0.27f, box.Y + bs * 0.52f),
                        new PointF(box.X + bs * 0.44f, box.Y + bs * 0.69f),
                        new PointF(box.X + bs * 0.74f, box.Y + bs * 0.35f) });
                }
            }
            else
            {
                Gfx.Fill(g, box, 5 * k, Theme.Surface2);
                Gfx.Stroke(g, box, 5 * k, hover && Enabled ? Theme.Accent : Theme.Border, 1.2f);
            }
            if (Focused && ShowFocusCues) Gfx.Stroke(g, new RectangleF(box.X - 2, box.Y - 2, bs + 4, bs + 4), 6 * k, Theme.Accent, 1.2f);
            Gfx.Draw(g, Text, Font, new Rectangle((int)(bs + 10 * k), 0, Width, Height), Enabled ? Theme.Text : Theme.Muted, Gfx.LeftMid);
        }
    }

    class DropZone : ThemedControl
    {
        public bool Compact;
        bool hover, drag;
        readonly Font titleFont = new Font("Segoe UI Semibold", 12.5f);
        readonly Font smallFont = new Font("Segoe UI", 8.5f);

        public DropZone() { Cursor = Cursors.Hand; }

        public bool DragActive { set { drag = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Prep(e);
            float k = K;
            bool active = Enabled && (drag || hover);
            RectangleF r = new RectangleF(1, 1, Width - 2.5f, Height - 2.5f);
            Color fill = drag ? Theme.Mix(Theme.Surface, Theme.Accent, 0.10) : hover && Enabled ? Theme.Mix(Theme.Surface, Theme.Text, 0.025) : Theme.Surface;
            Gfx.Fill(g, r, 14 * k, fill);
            using (GraphicsPath p = Gfx.Round(r, 14 * k))
            using (Pen pen = new Pen(active ? Theme.Accent : Theme.Mix(Theme.Border, Theme.Muted, 0.3), 1.5f * k))
            {
                pen.DashPattern = new float[] { 4f, 3f };
                g.DrawPath(pen, p);
            }

            if (Compact)
            {
                string t = Lang.T("Перетащите ещё или нажмите, чтобы добавить", "Drop more here or click to add");
                Size ts = Gfx.Measure(t, Font);
                int gw = (int)(26 * k), x = (Width - ts.Width - gw) / 2;
                Gfx.Draw(g, "", Gfx.Icons(11f), new Rectangle(x, 0, (int)(18 * k), Height), Theme.Accent, Gfx.Center);
                Gfx.Draw(g, t, Font, new Rectangle(x + gw, 0, ts.Width + 4, Height), active ? Theme.Text : Theme.Muted, Gfx.LeftMid);
                return;
            }

            float cs = 68 * k;
            int blockH = (int)(cs + 12 * k + 28 * k + 24 * k + 8 * k + 20 * k);
            float top = (Height - blockH) / 2f;
            RectangleF circ = new RectangleF((Width - cs) / 2, top, cs, cs);
            using (SolidBrush b = new SolidBrush(Theme.Mix(Theme.Surface, Theme.Accent, active ? 0.24 : 0.15))) g.FillEllipse(b, circ);
            Gfx.Draw(g, "", Gfx.Icons(22f), Rectangle.Round(circ), Theme.Accent, Gfx.Center);
            Rectangle t1 = new Rectangle(0, (int)(circ.Bottom + 12 * k), Width, (int)(28 * k));
            Gfx.Draw(g, Lang.T("Перетащите картинки или папки сюда", "Drop images or folders here"), titleFont, t1, Theme.Text, Gfx.Center);
            Rectangle t2 = new Rectangle(0, t1.Bottom, Width, (int)(24 * k));
            Gfx.Draw(g, Lang.T("или нажмите, чтобы выбрать файлы", "or click to choose files"), Font, t2, Theme.Muted, Gfx.Center);
            Rectangle t3 = new Rectangle(0, t2.Bottom + (int)(8 * k), Width, (int)(20 * k));
            Gfx.Draw(g, "JPG · PNG · WebP · HEIC · AVIF · JPEG XL · RAW · BMP · GIF · TIFF", smallFont, t3, Theme.Mix(Theme.Muted, Theme.Surface, 0.3), Gfx.Center);
        }
    }

    class ProgressLine : ThemedControl
    {
        float v;
        public float Value { get { return v; } set { v = Math.Max(0, Math.Min(1, value)); Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Prep(e);
            float rad = Height / 2f;
            Gfx.Fill(g, new RectangleF(0, 0, Width, Height), rad, Theme.Mix(Theme.Surface2, Theme.Text, Theme.Dark ? 0.04 : 0.02));
            if (v > 0) Gfx.Fill(g, new RectangleF(0, 0, Math.Max(Height, Width * v), Height), rad, Theme.Accent);
        }
    }

    class LogoMark : ThemedControl
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Prep(e);
            float s = Math.Min(Width, Height);
            Logo.Draw(g, new RectangleF((Width - s) / 2, (Height - s) / 2, s, s));
        }
    }

    // ------------------------------------------------------------------ file lists

    static class Glyph
    {
        static string G(int code) { return ((char)code).ToString(); }
        public static readonly string Add = G(0xE710), Folder = G(0xED25), OpenFolder = G(0xE8DA), Delete = G(0xE74D),
            Clear = G(0xE894), Sun = G(0xE706), Moon = G(0xE708), Convert = G(0xE8AB), Photo = G(0xE91B),
            Error = G(0xE783), Check = G(0xE73E), Clock = G(0xE823), Sync = G(0xE895), Open = G(0xE8A7);
    }

    class Entry
    {
        public string Path;
        public Size Dims;
        public bool Unreadable, Removed, InfoPending;
        public long Bytes, OutBytes, SrcBytes;
        public Bitmap Thumb;
        public int State;          // sources: 0 idle, 1 queued, 2 working, 3 done, 4 error
        public string OutPath, Error;
    }

    class FileList : ListView
    {
        public bool Results;
        public int HoverIndex = -1;
        readonly Font boldFont = new Font("Segoe UI Semibold", 9.5f);
        readonly Font smallFont = new Font("Segoe UI", 8.5f);
        static readonly ImageAttributes clampEdges = MakeClamp();

        static ImageAttributes MakeClamp() { ImageAttributes ia = new ImageAttributes(); ia.SetWrapMode(WrapMode.TileFlipXY); return ia; }

        public FileList()
        {
            DoubleBuffered = true;
            OwnerDraw = true;
            View = View.Details;
            HeaderStyle = ColumnHeaderStyle.None;
            FullRowSelect = true;
            BorderStyle = BorderStyle.None;
            HideSelection = false;
            ShowItemToolTips = true;
            Columns.Add("", 100);
        }

        public void SetRowHeight(int h)
        {
            ImageList il = new ImageList();
            il.ImageSize = new Size(1, Math.Min(255, h));
            SmallImageList = il;
        }

        public void FitColumn()
        {
            if (Columns.Count > 0 && ClientSize.Width > 0 && Columns[0].Width != ClientSize.Width) Columns[0].Width = ClientSize.Width;
        }

        public void ApplyScrollTheme()
        {
            if (IsHandleCreated) Native.SetWindowTheme(Handle, Theme.Dark ? "DarkMode_Explorer" : "Explorer", null);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyScrollTheme(); FitColumn(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); FitColumn(); Invalidate(); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            ListViewItem it = GetItemAt(e.X, e.Y);
            SetHover(it != null ? it.Index : -1);
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHover(-1); }

        void SetHover(int idx)
        {
            if (idx == HoverIndex) return;
            int old = HoverIndex; HoverIndex = idx;
            if (old >= 0 && old < Items.Count) Invalidate(Items[old].Bounds);
            if (idx >= 0 && idx < Items.Count) Invalidate(Items[idx].Bounds);
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e) { e.DrawDefault = false; }
        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e) { e.DrawDefault = false; }

        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            e.DrawDefault = false;
            if (Columns.Count > 0 && Columns[0].Width != ClientSize.Width) BeginInvoke((Action)FitColumn);
            Entry en = e.Item.Tag as Entry;
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, e.Bounds);
            if (en == null) return;
            float k = DeviceDpi / 96f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            Rectangle r = new Rectangle(e.Bounds.X + (int)(4 * k), e.Bounds.Y + (int)(2 * k), ClientSize.Width - (int)(8 * k), e.Bounds.Height - (int)(4 * k));
            if (e.Item.Selected) Gfx.Fill(g, r, 9 * k, Theme.Selection);
            else if (e.ItemIndex == HoverIndex) Gfx.Fill(g, r, 9 * k, Theme.Hover);

            int pad = (int)(10 * k);
            Rectangle thumb = new Rectangle(r.X + pad, r.Y + (r.Height - (int)(42 * k)) / 2, (int)(64 * k), (int)(42 * k));
            int rightW = (int)(150 * k);
            Rectangle right = new Rectangle(r.Right - pad - rightW, r.Y, rightW, r.Height);
            int tx = thumb.Right + (int)(14 * k);
            Rectangle text = new Rectangle(tx, r.Y, Math.Max(40, right.X - (int)(12 * k) - tx), r.Height);

            // preview
            Gfx.Fill(g, thumb, 6 * k, Theme.Surface2);
            if (en.Thumb != null)
            {
                using (GraphicsPath p = Gfx.Round(thumb, 6 * k))
                using (Region old = g.Clip)
                {
                    g.SetClip(p, CombineMode.Intersect);
                    DrawCover(g, en.Thumb, thumb);
                    g.Clip = old;
                }
            }
            else Gfx.Draw(g, en.Unreadable ? Glyph.Error : Glyph.Photo, Gfx.Icons(12f), thumb, Theme.Muted, Gfx.Center);

            // name + details
            int lh = (int)(20 * k);
            Rectangle top = new Rectangle(text.X, text.Y + text.Height / 2 - lh, text.Width, lh);
            Rectangle bot = new Rectangle(text.X, text.Y + text.Height / 2 + (int)(1 * k), text.Width, lh);
            Gfx.Draw(g, System.IO.Path.GetFileName(en.Path), boldFont, top, Theme.Text, TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis);
            string meta;
            if (en.InfoPending) meta = Lang.T("читаю…", "reading…") + " · " + Gfx.Bytes(en.Bytes);
            else if (en.Unreadable) meta = Lang.T("не удаётся открыть", "can't be opened") + " · " + Gfx.Bytes(en.Bytes);
            else
            {
                meta = en.Dims.Width + " × " + en.Dims.Height;
                if (Results) meta += " · " + System.IO.Path.GetExtension(en.Path).TrimStart('.').ToUpperInvariant() + Lang.T(" · было ", " · was ") + Gfx.Bytes(en.SrcBytes);
                else meta += " · " + Gfx.Bytes(en.Bytes);
            }
            Gfx.Draw(g, meta, smallFont, bot, Theme.Muted, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);

            if (Results) DrawSize(g, en, right, k); else DrawStatus(g, en, right, k);
        }

        static void DrawCover(Graphics g, Bitmap bmp, Rectangle dst)
        {
            double sr = (double)bmp.Width / bmp.Height, dr = (double)dst.Width / dst.Height;
            float sx = 0, sy = 0, sw = bmp.Width, sh = bmp.Height;
            if (sr > dr) { sw = (float)(bmp.Height * dr); sx = (bmp.Width - sw) / 2; }
            else { sh = (float)(bmp.Width / dr); sy = (bmp.Height - sh) / 2; }
            g.DrawImage(bmp, dst, sx, sy, sw, sh, GraphicsUnit.Pixel, clampEdges);
        }

        // right-aligned glyph + text
        void DrawStatus(Graphics g, Entry en, Rectangle r, float k)
        {
            string glyph = null, text = null;
            Color c = Theme.Muted;
            switch (en.State)
            {
                case 1: glyph = Glyph.Clock; text = Lang.T("В очереди", "Queued"); break;
                case 2: glyph = Glyph.Sync; text = Lang.T("Конвертирую…", "Converting…"); c = Theme.Accent; break;
                case 3: glyph = Glyph.Check; text = Lang.T("Готово", "Done"); c = Theme.Success; break;
                case 4: glyph = Glyph.Error; text = Lang.T("Ошибка", "Error"); c = Theme.Danger; break;
                default: if (en.Unreadable) { glyph = Glyph.Error; text = Lang.T("Не открывается", "Can't open"); c = Theme.Danger; } break;
            }
            if (text == null) return;
            Size ts = Gfx.Measure(text, Font);
            int gw = (int)(22 * k), x = r.Right - ts.Width - gw;
            Gfx.Draw(g, glyph, Gfx.Icons(9.5f), new Rectangle(x, r.Y, (int)(16 * k), r.Height), c, Gfx.Center);
            Gfx.Draw(g, text, Font, new Rectangle(x + gw, r.Y, ts.Width + 4, r.Height), c, Gfx.LeftMid);
        }

        // size + change pill, right-aligned
        void DrawSize(Graphics g, Entry en, Rectangle r, float k)
        {
            float pillW = 0;
            if (en.SrcBytes > 0)
            {
                double d = (double)en.Bytes / en.SrcBytes - 1;
                string pill = (d < 0 ? "−" : "+") + Math.Abs(d * 100).ToString("0") + "%";
                Color pc = d < 0 ? Theme.Success : Theme.Muted;
                Size ps = Gfx.Measure(pill, smallFont);
                pillW = ps.Width + 14 * k;
                RectangleF pr = new RectangleF(r.Right - pillW, r.Y + (r.Height - 20 * k) / 2, pillW, 20 * k);
                Gfx.Fill(g, pr, 10 * k, Theme.Mix(Theme.Surface, pc, 0.16));
                Gfx.Draw(g, pill, smallFont, Rectangle.Round(pr), pc, Gfx.Center);
            }
            string size = Gfx.Bytes(en.Bytes);
            Size ts = Gfx.Measure(size, boldFont);
            int x = (int)(r.Right - pillW - (pillW > 0 ? 10 * k : 0) - ts.Width);
            Gfx.Draw(g, size, boldFont, new Rectangle(x, r.Y, ts.Width + 4, r.Height), Theme.Text, Gfx.LeftMid);
        }
    }

    class EmptyHint : ThemedControl
    {
        readonly Font titleFont = new Font("Segoe UI Semibold", 11f);

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Prep(e);
            float k = K, cs = 56 * k;
            float top = (Height - (cs + 12 * k + 26 * k + 22 * k)) / 2f;
            RectangleF circ = new RectangleF((Width - cs) / 2, top, cs, cs);
            using (SolidBrush b = new SolidBrush(Theme.Mix(Theme.Surface, Theme.Text, Theme.Dark ? 0.06 : 0.05))) g.FillEllipse(b, circ);
            Gfx.Draw(g, Glyph.Photo, Gfx.Icons(18f), Rectangle.Round(circ), Theme.Muted, Gfx.Center);
            Rectangle t1 = new Rectangle(0, (int)(circ.Bottom + 12 * k), Width, (int)(26 * k));
            Gfx.Draw(g, Lang.T("Здесь появятся готовые файлы", "Converted files will appear here"), titleFont, t1, Theme.Text, Gfx.Center);
            Rectangle t2 = new Rectangle(0, t1.Bottom, Width, (int)(22 * k));
            Gfx.Draw(g, Lang.T("Двойной щелчок по картинке — открыть её", "Double-click an image to open it"), Font, t2, Theme.Muted, Gfx.Center);
        }
    }

    // Borderless, click-through window showing a snapshot of the old UI; it fades out over the changed UI.
    class FadeOverlay : Form
    {
        public static int Completed;               // finished fades (checked by the screenshot test)
        readonly Bitmap snapshot;
        System.Windows.Forms.Timer timer;
        Stopwatch clock;
        int duration;

        public FadeOverlay(Bitmap snapshot)
        {
            this.snapshot = snapshot;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackgroundImage = snapshot;
            BackgroundImageLayout = ImageLayout.None;
            DoubleBuffered = true;
            Opacity = 0.999;                        // makes it a layered window from the start
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x20 | 0x08000000;   // WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE
                return cp;
            }
        }

        public void Start(int ms)
        {
            duration = ms;
            clock = Stopwatch.StartNew();
            timer = new System.Windows.Forms.Timer { Interval = 15 };
            timer.Tick += delegate
            {
                double t = Math.Min(1.0, clock.ElapsedMilliseconds / (double)duration);
                Opacity = Math.Max(0, 0.999 * (1 - t * t * (3 - 2 * t)));   // smoothstep
                if (t >= 1) { timer.Stop(); Completed++; Close(); }
            };
            timer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (timer != null) timer.Dispose();
            BackgroundImage = null;
            snapshot.Dispose();
        }
    }

    static class Menus
    {
        public static ContextMenuStrip Create(Control owner)
        {
            float k = owner.DeviceDpi / 96f;
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new ThemedMenuRenderer();
            m.ShowImageMargin = false; m.ShowCheckMargin = false;
            m.Font = owner.Font; m.BackColor = Theme.Surface;
            m.Padding = new Padding((int)(4 * k));
            m.Closed += delegate { owner.BeginInvoke((Action)m.Dispose); };
            return m;
        }

        public static void Add(ContextMenuStrip m, string text, Action action)
        {
            float k = m.DeviceDpi / 96f;
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.ForeColor = Theme.Text;
            it.Padding = new Padding((int)(4 * k), (int)(6 * k), (int)(12 * k), (int)(6 * k));
            it.Click += delegate { action(); };
            m.Items.Add(it);
        }
    }

    static class Background
    {
        static BlockingCollection<Action> queue;

        public static void Run(Action a)
        {
            if (queue == null)
            {
                queue = new BlockingCollection<Action>();
                for (int i = 0; i < 2; i++)
                {
                    Thread t = new Thread(delegate() { foreach (Action x in queue.GetConsumingEnumerable()) { try { x(); } catch { } } });
                    t.IsBackground = true;
                    t.SetApartmentState(ApartmentState.STA);
                    t.Start();
                }
            }
            queue.Add(a);
        }
    }

    // ------------------------------------------------------------------ main window

    class MainForm : Form
    {
        FileList srcList, outList;
        RoundPanel srcCard, outCard, settingsCard;
        DropZone drop;
        EmptyHint outEmpty;
        Panel dropGap;
        SplitContainer split;
        Label srcCount, outCount, status;
        SelectBox cbFormat, cbSize;
        NumBox numQuality, numW, numH;
        Label lblQuality, lblWH, lblTitle, lblSub, lblSrcTitle, lblOutTitle, lblFormat, lblSize;
        ToggleCheck chkNextTo;
        InputBox txtOut;
        FlatButton btnAdd, btnAddDir, btnLang, btnTheme, btnRemove, btnClear, btnOpenDir, btnClearOut, btnOut, btnGo;
        int doneOk = -1, doneTotal;   // last finished run, to re-word the status line when the language changes
        ProgressLine progress;
        readonly ToolTip tips = new ToolTip();
        readonly HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string lastOutDir;
        int pendingThumbs;
        bool busy, applyingRatio, statusLocked, userDragging;
        double ratio = 0.5;
        readonly float k;

        public bool ThumbsReady { get { return pendingThumbs == 0; } }
        public bool Busy { get { return busy; } }

        int S(float v) { return (int)Math.Round(v * k); }

        public MainForm(string[] args)
        {
            k = DeviceDpi / 96f;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(S(1200), S(760));
            MinimumSize = new Size(S(980), S(660));
            StartPosition = FormStartPosition.CenterScreen;
            Padding = new Padding(S(18));
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            ApplyLanguage();
            ApplyTheme();

            DragEnter += OnDragEnter; DragDrop += OnDragDrop; DragLeave += delegate { drop.DragActive = false; };
            if (args != null && args.Length > 0) AddPaths(args);
            UpdateState();
        }

        Label L(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Tag = "muted", Margin = new Padding(0, S(4), S(10), S(4)) };
        }

        FlatButton Small(string text, string glyph)
        {
            return new FlatButton(text, glyph, FlatButton.Kinds.Ghost) { HeightDip = 32, Margin = new Padding(S(2), 0, 0, 0) };
        }

        // card = header row (title, counter, buttons) + body
        TableLayoutPanel CardHeader(out Label title, out Label counter, params Control[] buttons)
        {
            TableLayoutPanel h = new TableLayoutPanel { Dock = DockStyle.Top, Height = S(44), ColumnCount = 3, RowCount = 1, Padding = new Padding(S(10), 0, S(4), 0) };
            h.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            h.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            h.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            title = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, S(10), 0) };
            counter = new Label { AutoSize = false, AutoEllipsis = true, Tag = "muted", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, S(2), S(6), 0) };
            FlowLayoutPanel f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
            f.Controls.AddRange(buttons);
            h.Controls.Add(title, 0, 0); h.Controls.Add(counter, 1, 0); h.Controls.Add(f, 2, 0);
            return h;
        }

        void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // ---- app header
            TableLayoutPanel head = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 0, 0, S(14)) };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            LogoMark logo = new LogoMark { Size = new Size(S(42), S(42)), Margin = new Padding(0, 0, S(12), 0), Anchor = AnchorStyles.Left };
            Panel titles = new Panel { Size = new Size(S(320), S(46)), Margin = new Padding(0), Anchor = AnchorStyles.Left };
            lblTitle = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 14f), Location = new Point(-S(2), -S(1)) };
            lblSub = new Label { AutoSize = true, Tag = "muted", Location = new Point(0, S(26)) };
            titles.Controls.Add(lblTitle); titles.Controls.Add(lblSub);
            FlowLayoutPanel tools = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
            btnAdd = new FlatButton("", Glyph.Add, FlatButton.Kinds.Secondary);
            btnAddDir = new FlatButton("", Glyph.Folder, FlatButton.Kinds.Secondary);
            btnLang = new FlatButton("", null, FlatButton.Kinds.Ghost) { Square = true, Margin = new Padding(S(10), 3, 0, 3) };
            btnTheme = new FlatButton("", Glyph.Sun, FlatButton.Kinds.Ghost) { Margin = new Padding(S(2), 3, 0, 3) };
            tools.Controls.AddRange(new Control[] { btnAdd, btnAddDir, btnLang, btnTheme });
            head.Controls.Add(logo, 0, 0); head.Controls.Add(titles, 1, 0); head.Controls.Add(tools, 3, 0);
            root.Controls.Add(head, 0, 0);

            // ---- two panes
            split = new SplitContainer { Dock = DockStyle.Fill, Margin = new Padding(0), SplitterWidth = S(14), TabStop = false };
            split.Paint += delegate(object s, PaintEventArgs e)
            {
                Rectangle sr = split.SplitterRectangle;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = sr.X + sr.Width / 2f, cy = sr.Y + sr.Height / 2f, dot = 3.2f * k;
                using (SolidBrush b = new SolidBrush(Theme.Mix(Theme.Bg, Theme.Muted, 0.6)))
                    for (int i = -1; i <= 1; i++) e.Graphics.FillEllipse(b, cx - dot / 2, cy + i * 7 * k - dot / 2, dot, dot);
            };
            split.SplitterMoved += delegate
            {
                // only a drag by the user changes the proportion; layout-driven moves must not
                if (userDragging && !applyingRatio && split.Width > split.SplitterWidth) ratio = (double)split.SplitterDistance / (split.Width - split.SplitterWidth);
                userDragging = false;
                split.Invalidate();
            };
            split.SplitterMoving += delegate { userDragging = true; };
            split.SizeChanged += delegate { ApplyRatio(); };

            // left: sources
            srcCard = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(S(6), S(4), S(6), S(8)) };
            btnRemove = Small("", Glyph.Delete);
            btnClear = Small("", Glyph.Clear);
            TableLayoutPanel srcHead = CardHeader(out lblSrcTitle, out srcCount, btnRemove, btnClear);
            srcList = new FileList { Dock = DockStyle.Fill, AllowDrop = true };
            srcList.SetRowHeight(S(62));
            dropGap = new Panel { Dock = DockStyle.Top, Height = S(6) };
            drop = new DropZone { Dock = DockStyle.Top, Height = S(52), AllowDrop = true };
            srcCard.Controls.Add(srcList); srcCard.Controls.Add(dropGap); srcCard.Controls.Add(drop); srcCard.Controls.Add(srcHead);
            split.Panel1.Controls.Add(srcCard);

            // right: results
            outCard = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(S(6), S(4), S(6), S(8)) };
            btnOpenDir = Small("", Glyph.OpenFolder);
            btnClearOut = Small("", Glyph.Clear);
            TableLayoutPanel outHead = CardHeader(out lblOutTitle, out outCount, btnOpenDir, btnClearOut);
            outList = new FileList { Dock = DockStyle.Fill, Results = true };
            outList.SetRowHeight(S(62));
            outEmpty = new EmptyHint { Dock = DockStyle.Fill };
            outCard.Controls.Add(outList); outCard.Controls.Add(outEmpty); outCard.Controls.Add(outHead);
            split.Panel2.Controls.Add(outCard);
            root.Controls.Add(split, 0, 1);

            // ---- settings card
            settingsCard = new RoundPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Padding = new Padding(S(18), S(14), S(18), S(14)), Margin = new Padding(0, S(14), 0, 0) };
            TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 5, RowCount = 3 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(300)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            cbFormat = new SelectBox { Size = new Size(S(280), S(36)), Anchor = AnchorStyles.Left, Margin = new Padding(0, S(4), S(18), S(4)) };
            cbFormat.Items.AddRange(OutFormat.Available());
            cbFormat.SelectedIndex = 0;
            lblQuality = L("");
            numQuality = new NumBox(1, 100, 90) { Size = new Size(S(72), S(36)), Anchor = AnchorStyles.Left, Margin = new Padding(0, S(4), 0, S(4)) };

            cbSize = new SelectBox { Size = new Size(S(280), S(36)), Anchor = AnchorStyles.Left, Margin = new Padding(0, S(4), S(18), S(4)) };
            cbSize.Items.AddRange(SizeModes());
            cbSize.SelectedIndex = 0;
            lblWH = L("");
            numW = new NumBox(1, 30000, 3440) { Size = new Size(S(84), S(36)), Step = 10, Margin = new Padding(0) };
            numH = new NumBox(1, 30000, 1440) { Size = new Size(S(84), S(36)), Step = 10, Margin = new Padding(0) };
            FlowLayoutPanel wh = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = new Padding(0, S(4), 0, S(4)) };
            wh.Controls.AddRange(new Control[] { numW, new Label { Text = "×", AutoSize = true, Tag = "muted", Margin = new Padding(S(6), S(9), S(6), 0) }, numH });

            lblFormat = L(""); lblSize = L("");
            grid.Controls.Add(lblFormat, 0, 0); grid.Controls.Add(cbFormat, 1, 0);
            grid.Controls.Add(lblQuality, 2, 0); grid.Controls.Add(numQuality, 3, 0);
            grid.Controls.Add(lblSize, 0, 1); grid.Controls.Add(cbSize, 1, 1);
            grid.Controls.Add(lblWH, 2, 1); grid.Controls.Add(wh, 3, 1);

            TableLayoutPanel outRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, S(8), 0, 0) };
            outRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            outRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            chkNextTo = new ToggleCheck("") { Checked = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, S(12), 0) };
            txtOut = new InputBox { Dock = DockStyle.Fill, Height = S(36), Margin = new Padding(0, S(1), S(8), S(1)) };
            btnOut = new FlatButton("", Glyph.Folder, FlatButton.Kinds.Secondary) { Anchor = AnchorStyles.Left, Margin = new Padding(0) };
            outRow.Controls.Add(chkNextTo, 0, 0); outRow.Controls.Add(txtOut, 1, 0); outRow.Controls.Add(btnOut, 2, 0);
            grid.Controls.Add(outRow, 0, 2); grid.SetColumnSpan(outRow, 5);

            settingsCard.Controls.Add(grid);
            grid.SizeChanged += delegate { settingsCard.Height = grid.Height + settingsCard.Padding.Vertical; };
            root.Controls.Add(settingsCard, 0, 2);

            // ---- bottom bar
            TableLayoutPanel bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, S(16), 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            btnGo = new FlatButton("", Glyph.Convert, FlatButton.Kinds.Primary) { HeightDip = 42, Anchor = AnchorStyles.Left, Margin = new Padding(0), Font = new Font("Segoe UI Semibold", 10f) };
            Panel mid = new Panel { Dock = DockStyle.Fill, Height = S(42), Margin = new Padding(S(18), 0, 0, 0), Padding = new Padding(0, S(2), 0, S(6)) };
            status = new Label { Dock = DockStyle.Top, Height = S(24), Tag = "muted", AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            progress = new ProgressLine { Dock = DockStyle.Bottom, Height = S(6) };
            mid.Controls.Add(progress); mid.Controls.Add(status);
            bottom.Controls.Add(btnGo, 0, 0); bottom.Controls.Add(mid, 1, 0);
            root.Controls.Add(bottom, 0, 3);

            // ---- events
            btnAdd.Click += delegate { AddFilesDialog(); };
            btnAddDir.Click += delegate { AddFolderDialog(); };
            btnTheme.Click += delegate { ToggleTheme(); };
            btnLang.Click += delegate { SwitchLanguage(); };
            btnRemove.Click += delegate { RemoveSelected(srcList); };
            btnClear.Click += delegate { ClearList(srcList); };
            btnOpenDir.Click += delegate { if (lastOutDir != null) Process.Start("explorer.exe", "\"" + lastOutDir + "\""); };
            btnClearOut.Click += delegate { ClearList(outList); };
            drop.Click += delegate { if (!busy) AddFilesDialog(); };
            cbFormat.SelectedIndexChanged += delegate { SyncEnabled(); };
            cbFormat.Opening += delegate { RefreshFormats(); };
            cbSize.SelectedIndexChanged += delegate { SyncEnabled(); };
            chkNextTo.CheckedChanged += delegate { SyncEnabled(); };
            btnOut.Click += delegate { ChooseOutDir(); };
            btnGo.Click += delegate { Start(); };

            foreach (Control c in new Control[] { srcList, drop })
            {
                c.DragEnter += OnDragEnter;
                c.DragDrop += OnDragDrop;
                c.DragLeave += delegate { drop.DragActive = false; };
            }
            foreach (FileList l in new[] { srcList, outList })
            {
                FileList lst = l;
                lst.KeyDown += delegate(object s, KeyEventArgs e)
                {
                    if (e.KeyCode == Keys.Delete) RemoveSelected(lst);
                    else if (e.Control && e.KeyCode == Keys.A) foreach (ListViewItem it in lst.Items) it.Selected = true;
                    else if (e.KeyCode == Keys.Enter && lst.FocusedItem != null) OpenFile(PathOf(lst, lst.FocusedItem));
                };
                lst.SelectedIndexChanged += delegate { SyncEnabled(); };
                lst.MouseDoubleClick += delegate(object s, MouseEventArgs e)
                {
                    ListViewItem it = lst.GetItemAt(e.X, e.Y);
                    if (it != null) OpenFile(PathOf(lst, it));
                };
                lst.MouseUp += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Right) ShowItemMenu(lst, e.Location); };
            }

        }

        static object[] SizeModes()
        {
            return new object[] {
                Lang.T("Как есть", "Keep original"),
                Lang.T("Заполнить (обрезать лишнее)", "Fill (crop the overflow)"),
                Lang.T("Вписать (поля по краям)", "Fit (add borders)"),
                Lang.T("Растянуть", "Stretch") };
        }

        // every static text in the window; called at startup and when the language is switched
        void ApplyLanguage()
        {
            SuspendLayout();
            Text = Lang.T("Конвертер картинок", "Image Converter");
            lblTitle.Text = Text;
            lblSub.Text = Lang.T("Форматы, размер и сжатие — сразу пачкой", "Formats, size and compression — in batches");
            btnAdd.Text = Lang.T("Файлы", "Files");
            btnAddDir.Text = Lang.T("Папка", "Folder");
            btnLang.Text = Lang.En ? "RU" : "EN";
            lblSrcTitle.Text = Lang.T("Исходные", "Source");
            lblOutTitle.Text = Lang.T("Готовые", "Converted");
            btnRemove.Text = Lang.T("Убрать", "Remove");
            btnClear.Text = Lang.T("Очистить", "Clear");
            btnOpenDir.Text = Lang.T("Открыть папку", "Open folder");
            btnClearOut.Text = Lang.T("Очистить", "Clear");
            lblFormat.Text = Lang.T("Формат", "Format");
            lblSize.Text = Lang.T("Размер", "Size");
            lblQuality.Text = Lang.T("Качество", "Quality");
            lblWH.Text = Lang.T("Ширина × высота", "Width × height");
            chkNextTo.Text = Lang.T("Сохранять рядом с исходником", "Save next to the original");
            btnOut.Text = Lang.T("Выбрать папку", "Choose folder");
            btnGo.Text = Lang.T("Конвертировать", "Convert");

            cbSize.Items.Clear();                        // the selected index is kept
            cbSize.Items.AddRange(SizeModes());
            cbSize.Invalidate();
            cbFormat.Invalidate();                       // format names are localized in OutFormat.Name
            chkNextTo.Parent.PerformLayout();

            tips.SetToolTip(btnAdd, Lang.T("Добавить картинки", "Add images"));
            tips.SetToolTip(btnAddDir, Lang.T("Добавить все картинки из папки (и вложенных)", "Add all images from a folder (and subfolders)"));
            tips.SetToolTip(btnRemove, Lang.T("Убрать выбранные из списка (Delete)", "Remove selected from the list (Delete)"));
            tips.SetToolTip(btnClear, Lang.T("Очистить список исходных", "Clear the source list"));
            tips.SetToolTip(btnClearOut, Lang.T("Очистить список готовых (сами файлы останутся)", "Clear the converted list (files stay on disk)"));
            tips.SetToolTip(btnOpenDir, Lang.T("Открыть папку с последними готовыми файлами", "Open the folder with the latest converted files"));
            tips.SetToolTip(btnLang, Lang.En ? "Русский" : "English");
            tips.SetToolTip(btnTheme, Theme.Dark ? Lang.T("Светлая тема", "Light theme") : Lang.T("Тёмная тема", "Dark theme"));
            foreach (ListViewItem it in outList.Items) it.ToolTipText = ((Entry)it.Tag).Path + Lang.T("\nДвойной щелчок — открыть", "\nDouble-click to open");
            foreach (ListViewItem it in srcList.Items) it.ToolTipText = SourceTip((Entry)it.Tag);

            if (!busy && statusLocked && doneOk >= 0) status.Text = DoneMessage(doneOk, doneTotal);
            ResumeLayout(true);
            UpdateState();
            Invalidate(true);
        }

        public static string DebugSnapshotPath;     // screenshot test: where to save the crossfade snapshot

        public static int ThemeSwitches;             // applied theme changes (checked by the screenshot test)
        FadeOverlay activeFade;
        readonly Stopwatch sinceThemeSwitch = new Stopwatch();

        public void SwitchLanguage()
        {
            if (activeFade != null) return;            // no stacking of transitions
            Crossfade(240, delegate { Lang.En = !Lang.En; Theme.Save(); ApplyLanguage(); });
        }

        // Dark ↔ light swaps the brightness of the whole window. To stay far below the 3-flashes-per-second
        // threshold (WCAG 2.3.1) theme changes are limited to one per second, clicks during a transition are
        // ignored, and the change fades slowly instead of snapping.
        public void ToggleTheme()
        {
            if (activeFade != null) return;
            if (sinceThemeSwitch.IsRunning && sinceThemeSwitch.ElapsedMilliseconds < 1000) return;
            sinceThemeSwitch.Restart();
            ThemeSwitches++;
            Crossfade(500, delegate { Theme.Apply(!Theme.Dark); Theme.Save(); ApplyTheme(); });
        }

        // Snapshot the client area, cover it, apply the change underneath, then fade the snapshot out,
        // so relayout and repaint happen out of sight instead of jumping.
        void Crossfade(int ms, Action change)
        {
            if (!Native.AnimationsEnabled()) { change(); return; }   // user turned animations off in Windows
            Bitmap snap = null;
            try
            {
                if (IsHandleCreated && Visible && WindowState != FormWindowState.Minimized && ClientSize.Width > 0 && ClientSize.Height > 0)
                {
                    snap = new Bitmap(ClientSize.Width, ClientSize.Height);
                    bool ok;
                    using (Graphics g = Graphics.FromImage(snap))
                    {
                        IntPtr hdc = g.GetHdc();
                        ok = Native.PrintWindow(Handle, hdc, 1 | 2);   // PW_CLIENTONLY | PW_RENDERFULLCONTENT
                        g.ReleaseHdc(hdc);
                    }
                    if (!ok) { snap.Dispose(); snap = null; }
                }
            }
            catch { if (snap != null) { snap.Dispose(); snap = null; } }

            if (snap == null) { change(); return; }
            if (DebugSnapshotPath != null) snap.Save(DebugSnapshotPath, ImageFormat.Png);

            FadeOverlay overlay = new FadeOverlay(snap);
            overlay.Bounds = RectangleToScreen(ClientRectangle);
            overlay.FormClosed += delegate { if (activeFade == overlay) activeFade = null; };
            activeFade = overlay;
            overlay.Show(this);
            overlay.Update();
            change();
            Refresh();
            overlay.Start(ms);
        }

        static string SourceTip(Entry en)
        {
            if (en.State == 3) return Lang.T("Сохранено: ", "Saved: ") + en.OutPath;
            if (en.State == 4) return Lang.T("Ошибка: ", "Error: ") + en.Error;
            return en.Path;
        }

        static string DoneMessage(int ok, int total)
        {
            int failed = total - ok;
            string msg = Lang.T("Готово: ", "Done: ") + ok + Lang.T(" из ", " of ") + total;
            if (failed > 0) msg += Lang.T(" · ошибок: ", " · errors: ") + failed + Lang.T(" (наведите на строку, чтобы увидеть причину)", " (hover a row to see why)");
            return msg;
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); ApplyRatio(); }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try { split.Panel1MinSize = S(340); split.Panel2MinSize = S(340); } catch { }
            ApplyRatio();
        }

        void ApplyRatio()
        {
            int avail = split.Width - split.SplitterWidth;
            if (avail <= 0) return;
            int d = (int)Math.Round(avail * ratio);
            d = Math.Max(split.Panel1MinSize, Math.Min(avail - split.Panel2MinSize, d));
            if (d <= 0 || d == split.SplitterDistance) return;
            applyingRatio = true;
            try { split.SplitterDistance = d; } catch { }
            applyingRatio = false;
        }

        // ---------------------------------------------------------- theme

        void ApplyTheme()
        {
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Walk(this, Theme.Bg);
            btnTheme.Glyph = Theme.Dark ? Glyph.Sun : Glyph.Moon;
            tips.SetToolTip(btnTheme, Theme.Dark ? Lang.T("Светлая тема", "Light theme") : Lang.T("Тёмная тема", "Dark theme"));
            ApplyChrome();
            SyncEnabled();
            Invalidate(true);
        }

        void Walk(Control parent, Color bg)
        {
            foreach (Control c in parent.Controls)
            {
                Color childBg = bg;
                RoundPanel rp = c as RoundPanel;
                if (c is TextBox) continue;               // colored by its InputBox
                if (rp != null)
                {
                    InputBox ib = c as InputBox;
                    if (ib != null) ib.RefreshColors(); else rp.BackColor = rp.FillColor;
                    childBg = rp.BackColor;
                }
                else if (c is ListView) { c.BackColor = Theme.Surface; childBg = Theme.Surface; }
                else c.BackColor = bg;
                c.ForeColor = "muted".Equals(c.Tag) ? Theme.Muted : Theme.Text;
                Walk(c, childBg);
                c.Invalidate();
            }
        }

        void ApplyChrome()
        {
            if (!IsHandleCreated) return;
            try
            {
                int dark = Theme.Dark ? 1 : 0;
                Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                int caption = ColorTranslator.ToWin32(Theme.Bg);
                Native.DwmSetWindowAttribute(Handle, 35, ref caption, 4);
                int text = ColorTranslator.ToWin32(Theme.Text);
                Native.DwmSetWindowAttribute(Handle, 36, ref text, 4);
            }
            catch { }
            srcList.ApplyScrollTheme();
            outList.ApplyScrollTheme();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyChrome(); }

        // ---------------------------------------------------------- state

        void SyncEnabled()
        {
            OutFormat f = cbFormat.SelectedItem as OutFormat;
            bool q = f != null && f.HasQuality;
            numQuality.SetDimmed(!q || busy);
            lblQuality.ForeColor = q ? Theme.Muted : Theme.Mix(Theme.Muted, Theme.Surface, 0.55);
            bool sized = cbSize.SelectedIndex > 0;
            numW.SetDimmed(!sized || busy); numH.SetDimmed(!sized || busy);
            lblWH.ForeColor = sized ? Theme.Muted : Theme.Mix(Theme.Muted, Theme.Surface, 0.55);
            txtOut.SetDimmed(chkNextTo.Checked || busy);
            btnOut.Enabled = !chkNextTo.Checked && !busy;

            bool any = srcList.Items.Count > 0;
            btnAdd.Enabled = btnAddDir.Enabled = drop.Enabled = !busy;
            cbFormat.Enabled = cbSize.Enabled = chkNextTo.Enabled = !busy;
            btnRemove.Enabled = !busy && srcList.SelectedItems.Count > 0;
            btnClear.Enabled = !busy && any;
            btnGo.Enabled = !busy && any;
            btnOpenDir.Enabled = lastOutDir != null;
            btnClearOut.Enabled = !busy && outList.Items.Count > 0;
            AllowDrop = srcList.AllowDrop = drop.AllowDrop = !busy;
        }

        void UpdateState()
        {
            bool srcEmpty = srcList.Items.Count == 0, outEmptyNow = outList.Items.Count == 0;
            srcCard.SuspendLayout();
            drop.Compact = !srcEmpty;
            drop.Dock = srcEmpty ? DockStyle.Fill : DockStyle.Top;
            if (!srcEmpty) drop.Height = S(52);
            srcList.Visible = !srcEmpty;
            dropGap.Visible = !srcEmpty;
            srcCard.ResumeLayout();
            outList.Visible = !outEmptyNow;
            outEmpty.Visible = outEmptyNow;
            drop.Invalidate();
            srcList.FitColumn(); outList.FitColumn();

            long srcTotal = srcList.Items.Cast<ListViewItem>().Sum(it => ((Entry)it.Tag).Bytes);
            srcCount.Text = srcEmpty ? "" : srcList.Items.Count + " · " + Gfx.Bytes(srcTotal);
            if (outEmptyNow) outCount.Text = "";
            else
            {
                long was = outList.Items.Cast<ListViewItem>().Sum(it => ((Entry)it.Tag).SrcBytes);
                long now = outList.Items.Cast<ListViewItem>().Sum(it => ((Entry)it.Tag).Bytes);
                outCount.Text = outList.Items.Count + " · " + Gfx.Bytes(was) + " → " + Gfx.Bytes(now);
            }
            if (!busy && !statusLocked)
                status.Text = srcEmpty ? Lang.T("Добавьте картинки, чтобы начать", "Add some images to get started")
                                       : Lang.T("Выберите формат и нажмите «Конвертировать»", "Pick a format and press “Convert”");
            SyncEnabled();
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            bool files = !busy && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effect = files ? DragDropEffects.Copy : DragDropEffects.None;
            drop.DragActive = files;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            drop.DragActive = false;
            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null && !busy) AddPaths(paths);
        }

        void AddFilesDialog()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Multiselect = true;
                d.Title = Lang.T("Выберите картинки", "Choose images");
                d.Filter = Lang.T("Картинки", "Images") + "|" + string.Join(";", Converter.InputExt.Select(x => "*" + x)) + "|" + Lang.T("Все файлы", "All files") + "|*.*";
                if (d.ShowDialog(this) == DialogResult.OK) AddPaths(d.FileNames);
            }
        }

        void AddFolderDialog()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = Lang.T("Папка с картинками (вложенные папки тоже)", "Folder with images (subfolders included)");
                if (d.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { d.SelectedPath });
            }
        }

        void ChooseOutDir()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = Lang.T("Куда сохранять результат", "Where to save the results");
                if (Directory.Exists(txtOut.Box.Text)) d.SelectedPath = txtOut.Box.Text;
                if (d.ShowDialog(this) == DialogResult.OK) txtOut.Box.Text = d.SelectedPath;
            }
        }

        void AddPaths(IEnumerable<string> paths)
        {
            Cursor = Cursors.WaitCursor;
            srcList.BeginUpdate();
            try
            {
                foreach (string p in paths)
                {
                    if (Directory.Exists(p))
                    {
                        List<string> files;
                        try { files = Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Where(Converter.IsImage).ToList(); }
                        catch { continue; }
                        foreach (string f in files) AddSource(f);
                    }
                    else if (File.Exists(p) && Converter.IsImage(p)) AddSource(p);
                }
            }
            finally { srcList.EndUpdate(); Cursor = Cursors.Default; }
            statusLocked = false;
            UpdateState();
        }

        void AddSource(string path)
        {
            string full = Path.GetFullPath(path);
            if (!known.Add(full)) return;
            Entry en = new Entry { Path = full, InfoPending = true };
            try { en.Bytes = new FileInfo(full).Length; } catch { }
            ListViewItem it = new ListViewItem(Path.GetFileName(full)) { Tag = en, ToolTipText = full };
            srcList.Items.Add(it);
            QueueInfo(srcList, en, it);
        }

        void AddResult(string outPath, long outBytes, long srcBytes)
        {
            Entry en = new Entry { Path = outPath, Bytes = outBytes, SrcBytes = srcBytes, State = 3, InfoPending = true };
            ListViewItem it = new ListViewItem(Path.GetFileName(en.Path)) { Tag = en, ToolTipText = en.Path + Lang.T("\nДвойной щелчок — открыть", "\nDouble-click to open") };
            outList.Items.Add(it);
            outList.EnsureVisible(it.Index);
            QueueInfo(outList, en, it);
        }

        // Dimensions + preview are read off the UI thread, so big folders don't freeze the window
        void QueueInfo(FileList l, Entry en, ListViewItem it)
        {
            Interlocked.Increment(ref pendingThumbs);
            int tw = S(64) * 2, th = S(42) * 2;
            Background.Run(delegate
            {
                Bitmap bmp = null; Size dims = Size.Empty; bool unreadable = false;
                try
                {
                    try { dims = Converter.ReadSize(en.Path); } catch { unreadable = true; }
                    if (!unreadable) { try { bmp = Converter.LoadThumb(en.Path, tw, th); } catch { } }
                    BeginInvoke((Action)delegate
                    {
                        en.Dims = dims; en.Unreadable = unreadable; en.InfoPending = false;
                        if (en.Removed) { if (bmp != null) bmp.Dispose(); return; }
                        en.Thumb = bmp;
                        if (it.ListView != null) l.Invalidate(it.Bounds);
                    });
                }
                catch { if (bmp != null) bmp.Dispose(); }       // window already closed
                finally { Interlocked.Decrement(ref pendingThumbs); }
            });
        }

        // encoders dropped into .\tools while the app runs show up without a restart
        void RefreshFormats()
        {
            OutFormat current = cbFormat.SelectedItem as OutFormat;
            OutFormat[] now = OutFormat.Available();
            if (now.SequenceEqual(cbFormat.Items.Cast<OutFormat>())) return;
            cbFormat.Items.Clear();
            cbFormat.Items.AddRange(now);
            int idx = current != null ? Array.IndexOf(now, current) : -1;
            cbFormat.SelectedIndex = -1;
            cbFormat.SelectedIndex = idx >= 0 ? idx : 0;
        }

        static string PathOf(FileList l, ListViewItem it) { return ((Entry)it.Tag).Path; }

        void OpenFile(string path)
        {
            try { Process.Start(path); }
            catch (Exception ex) { MessageBox.Show(this, Lang.T("Не удалось открыть файл:\n", "Couldn't open the file:\n") + ex.Message, Text); }
        }

        void ShowItemMenu(FileList l, Point pt)
        {
            ListViewItem it = l.GetItemAt(pt.X, pt.Y);
            if (it == null) return;
            if (!it.Selected) { foreach (ListViewItem x in l.SelectedItems.Cast<ListViewItem>().ToList()) x.Selected = false; it.Selected = true; }
            string path = PathOf(l, it);
            ContextMenuStrip m = Menus.Create(this);
            Menus.Add(m, Lang.T("Открыть", "Open"), delegate { OpenFile(path); });
            Menus.Add(m, Lang.T("Показать в папке", "Show in folder"), delegate { Process.Start("explorer.exe", "/select,\"" + path + "\""); });
            if (!busy) Menus.Add(m, Lang.T("Убрать из списка", "Remove from list"), delegate { RemoveSelected(l); });
            m.Show(l, pt);
        }

        void Forget(FileList l, ListViewItem it)
        {
            Entry en = (Entry)it.Tag;
            if (l == srcList) known.Remove(en.Path);
            en.Removed = true;
            if (en.Thumb != null) { en.Thumb.Dispose(); en.Thumb = null; }
        }

        void RemoveSelected(FileList l)
        {
            if (busy) return;
            foreach (ListViewItem it in l.SelectedItems.Cast<ListViewItem>().ToList()) { Forget(l, it); l.Items.Remove(it); }
            l.HoverIndex = -1;
            UpdateState();
        }

        void ClearList(FileList l)
        {
            if (busy) return;
            foreach (ListViewItem it in l.Items) Forget(l, it);
            l.Items.Clear();
            l.HoverIndex = -1;
            UpdateState();
        }

        void Ui(Action a) { try { BeginInvoke(a); } catch { } }

        void InvalidateItem(ListViewItem it) { if (it.ListView != null) srcList.Invalidate(it.Bounds); }

        public string DebugReport()
        {
            var states = srcList.Items.Cast<ListViewItem>().GroupBy(it => ((Entry)it.Tag).State).OrderBy(gr => gr.Key)
                .Select(gr => "state" + gr.Key + "=" + gr.Count());
            int pending = srcList.Items.Cast<ListViewItem>().Concat(outList.Items.Cast<ListViewItem>()).Count(it => ((Entry)it.Tag).InfoPending);
            return "sources=" + srcList.Items.Count + " " + string.Join(" ", states) + " results=" + outList.Items.Count +
                   " infoPending=" + pending + " busy=" + busy + " status=" + status.Text +
                   " langBtn=" + WindowRect(btnLang) + " themeBtn=" + WindowRect(btnTheme) +
                   " fadesCompleted=" + FadeOverlay.Completed + " openForms=" + Application.OpenForms.Count +
                   " themeSwitches=" + ThemeSwitches + " dark=" + Theme.Dark + " animations=" + Native.AnimationsEnabled();
        }

        // control bounds in window coordinates (same frame as a PrintWindow screenshot)
        string WindowRect(Control c)
        {
            Point p = c.PointToScreen(Point.Empty);
            return (p.X - Left) + "," + (p.Y - Top) + "," + c.Width + "," + c.Height;
        }

        public void TestConvert(string dir)
        {
            for (int i = 0; i < cbFormat.Items.Count; i++)
                if (((OutFormat)cbFormat.Items[i]).Ext == ".heic") cbFormat.SelectedIndex = i;
            chkNextTo.Checked = false;
            txtOut.Box.Text = dir;
            Start();
        }

        void Start()
        {
            if (busy || srcList.Items.Count == 0) return;
            string outDir = null;
            if (!chkNextTo.Checked)
            {
                outDir = txtOut.Box.Text.Trim();
                if (outDir.Length == 0 || !Directory.Exists(outDir))
                {
                    MessageBox.Show(this, Lang.T("Выберите существующую папку для сохранения или включите «Сохранять рядом с исходником».",
                                                  "Choose an existing output folder or turn on “Save next to the original”."), Text);
                    return;
                }
            }
            OutFormat fmt = (OutFormat)cbFormat.SelectedItem;
            int quality = numQuality.Value, mode = cbSize.SelectedIndex, W = numW.Value, H = numH.Value;
            string suffix = mode > 0 ? " " + W + "x" + H : "";
            List<ListViewItem> items = srcList.Items.Cast<ListViewItem>().ToList();
            foreach (ListViewItem it in items)
            {
                Entry en = (Entry)it.Tag;
                en.State = 1; en.Error = null; en.OutPath = null; en.OutBytes = 0;
                it.ToolTipText = en.Path;
            }
            srcList.Invalidate();
            busy = true;
            statusLocked = true;
            progress.Value = 0;
            SyncEnabled();
            int total = items.Count;

            Thread t = new Thread(delegate()
            {
                int ok = 0, done = 0;
                string lastDir = null;
                foreach (ListViewItem it in items)
                {
                    Entry en = (Entry)it.Tag;
                    // state is only ever written on this thread; UI messages just repaint and use captured values,
                    // so a late "working" message can't overwrite "done"
                    en.State = 2;
                    string name = Path.GetFileName(en.Path);
                    Ui(delegate { InvalidateItem(it); status.Text = Lang.T("Конвертирую ", "Converting ") + name + "…"; });

                    bool success = false; string outPath = null, error = null; long outBytes = 0;
                    try
                    {
                        outPath = Converter.MakeOutputPath(en.Path, outDir, fmt, suffix);
                        Converter.ConvertFile(en.Path, outPath, fmt, quality, mode, W, H);
                        outBytes = new FileInfo(outPath).Length;
                        success = true;
                        ok++;
                        lastDir = Path.GetDirectoryName(outPath);
                    }
                    catch (Exception ex)
                    {
                        Exception inner = ex; while (inner.InnerException != null) inner = inner.InnerException;
                        error = string.IsNullOrEmpty(inner.Message) ? inner.GetType().Name : inner.Message;
                    }
                    en.OutPath = outPath; en.OutBytes = outBytes; en.Error = error;
                    en.State = success ? 3 : 4;
                    done++;
                    int d = done; string dirNow = lastDir; long srcBytes = en.Bytes;
                    Ui(delegate
                    {
                        it.ToolTipText = SourceTip(en);
                        InvalidateItem(it);
                        if (success) { AddResult(outPath, outBytes, srcBytes); lastOutDir = dirNow; UpdateState(); }
                        progress.Value = (float)d / total;
                    });
                }
                int okF = ok;
                Ui(delegate
                {
                    busy = false;
                    doneOk = okF; doneTotal = total;
                    status.Text = DoneMessage(okF, total);
                    UpdateState();
                });
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }
    }
}
