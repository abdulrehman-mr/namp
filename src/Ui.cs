using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Namp
{
    static class Theme
    {
        public static readonly Color Page = Hex("#F5F5F3");
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Hex("#E7E5E4");
        public static readonly Color Hover = Hex("#EEEDEA");
        public static readonly Color Text = Hex("#1C1917");
        public static readonly Color Muted = Hex("#78716C");
        public static readonly Color Faint = Hex("#A8A29E");
        public static readonly Color Accent = Hex("#EA580C");
        public static readonly Color AccentHover = Hex("#C2410C");
        public static readonly Color AccentSoft = Hex("#FFF1E8");
        public static readonly Color Dark = Hex("#1C1917");
        public static readonly Color DarkHover = Hex("#44403C");
        public static readonly Color Green = Hex("#16A34A");
        public static readonly Color GreenSoft = Hex("#DCFCE7");
        public static readonly Color Red = Hex("#DC2626");
        public static readonly Color RedSoft = Hex("#FEE2E2");
        public static readonly Color Amber = Hex("#B45309");
        public static readonly Color AmberSoft = Hex("#FEF3C7");
        public static readonly Color Blue = Hex("#2563EB");
        public static readonly Color BlueSoft = Hex("#E0ECFF");
        public static readonly Color GraySoft = Hex("#F1F0EE");

        public static readonly Font Base = new Font("Segoe UI", 9.75f);
        public static readonly Font Semi = new Font("Segoe UI Semibold", 9.75f);
        public static readonly Font Small = new Font("Segoe UI", 8.75f);
        public static readonly Font H2 = new Font("Segoe UI Semibold", 11.5f);
        public static readonly Font Title = new Font("Segoe UI Semilight", 21f);
        public static readonly Font Brand = new Font("Segoe UI Semibold", 13.5f);
        public static readonly Font Mono = new Font("Consolas", 9.5f);
        public static readonly Font Icon = IconFont(11f);
        public static readonly Font IconBig = IconFont(14f);

        static Font IconFont(float size)
        {
            foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                var f = new Font(name, size);
                if (f.Name == name) return f;
            }
            return new Font("Segoe UI Symbol", size);
        }

        static Color Hex(string h) { return ColorTranslator.FromHtml(h); }
    }

    /// <summary>Fluent icon code points.</summary>
    static class Glyph
    {
        public const string Home = "", Globe = "", Database = "", Logs = "", Settings = "";
        public const string Play = "", Stop = "", Open = "", Folder = "", Add = "", Delete = "";
        public const string Restore = "", Backup = "", Lock = "", Activity = "", Refresh = "", Doc = "";
    }

    enum BtnKind { Soft, Primary, Dark, Success, Danger }

    static class Ui
    {
        public static float Scale = 1f;
        public static int S(int v) { return (int)Math.Round(v * Scale); }
        public static Padding P(int l, int t, int r, int b) { return new Padding(S(l), S(t), S(r), S(b)); }
        public static Padding P(int all) { return new Padding(S(all)); }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Label Lbl(string text, Font font, Color color)
        {
            return new Label { Text = text, Font = font, ForeColor = color, AutoSize = true, Margin = P(0, 0, 0, 4), BackColor = Color.Transparent, UseMnemonic = false };
        }

        /// <summary>Rounded text input. Returns the container; the TextBox comes out via the out parameter.</summary>
        public static Control Input(string cue, out TextBox box, Font font = null)
        {
            var wrap = new RoundPanel { Radius = S(8), Padding = P(10, 8, 10, 6), Margin = P(0, 0, 8, 0), Height = S(36), BackColor = Color.White };
            var tb = new TextBox { BorderStyle = BorderStyle.None, Font = font ?? Theme.Base, Dock = DockStyle.Fill, BackColor = Color.White, ForeColor = Theme.Text };
            SetCue(tb, cue);
            tb.GotFocus += (s, e) => { wrap.Stroke = Theme.Accent; wrap.Invalidate(); };
            tb.LostFocus += (s, e) => { wrap.Stroke = Theme.Border; wrap.Invalidate(); };
            wrap.Controls.Add(tb);
            box = tb;
            return wrap;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        public static void SetCue(TextBox t, string text)
        {
            t.HandleCreated += (s, e) => SendMessage(t.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, text);
        }

        public static FlowLayoutPanel Row(params Control[] controls)
        {
            var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), BackColor = Color.Transparent };
            f.Controls.AddRange(controls);
            return f;
        }

        /// <summary>Single-column page: rows AutoSize, except a row tagged "fill".</summary>
        public static TableLayoutPanel Stack(Padding padding, params Control[] rows)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows.Length, BackColor = Color.Transparent, Padding = padding, Margin = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var r in rows)
            {
                bool fill = r.Tag as string == "fill";
                t.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
                r.Dock = DockStyle.Fill;
                t.Controls.Add(r);
            }
            return t;
        }

        /// <summary>Equal-width columns.</summary>
        public static TableLayoutPanel Columns(int gap, params Control[] cells)
        {
            var t = new TableLayoutPanel { ColumnCount = cells.Length, RowCount = 1, AutoSize = true, BackColor = Color.Transparent, Margin = P(0, 0, 0, 14) };
            for (int i = 0; i < cells.Length; i++)
            {
                t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cells.Length));
                cells[i].Dock = DockStyle.Fill;
                cells[i].Margin = new Padding(i == 0 ? 0 : S(gap) / 2, 0, i == cells.Length - 1 ? 0 : S(gap) / 2, 0);
                t.Controls.Add(cells[i], i, 0);
            }
            return t;
        }

        public static Control PageTitle(string title, string subtitle)
        {
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = P(0, 0, 0, 16) };
            var t = Lbl(title, Theme.Title, Theme.Text);
            t.Margin = new Padding(0);
            f.Controls.Add(t);
            if (subtitle != null)
            {
                var s = Lbl(subtitle, Theme.Base, Theme.Muted);
                s.Margin = P(2, 0, 0, 0);
                f.Controls.Add(s);
            }
            return f;
        }

        public static ListView List(params string[] columns)
        {
            var lv = new ListView
            {
                View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
                BorderStyle = BorderStyle.None, Font = Theme.Base, HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Dock = DockStyle.Fill, BackColor = Color.White, ForeColor = Theme.Text
            };
            // A 1px-wide image list is the standard trick to get comfortable row heights.
            lv.SmallImageList = new ImageList { ImageSize = new Size(1, S(30)) };
            foreach (var c in columns)
            {
                var parts = c.Split('|');
                lv.Columns.Add(parts[0], S(parts.Length > 1 ? int.Parse(parts[1]) : 150));
            }
            lv.Resize += (s, e) =>
            {
                if (lv.Columns.Count == 0) return;
                int used = 0;
                for (int i = 0; i < lv.Columns.Count - 1; i++) used += lv.Columns[i].Width;
                lv.Columns[lv.Columns.Count - 1].Width = Math.Max(S(60), lv.ClientSize.Width - used - S(4));
            };
            return lv;
        }

        public static CheckBox Check(string text)
        {
            return new CheckBox { Text = text, AutoSize = true, Font = Theme.Base, ForeColor = Theme.Text, Margin = P(0, 4, 0, 8), BackColor = Color.Transparent, UseMnemonic = false };
        }

        public static NumericUpDown Num(int min, int max)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Width = S(96), Font = Theme.Base, Margin = P(0, 2, 18, 8), BorderStyle = BorderStyle.FixedSingle };
        }
    }

    /// <summary>White card with rounded corners and a hairline border.</summary>
    class RoundPanel : Panel
    {
        public int Radius;
        public Color Fill = Color.White;
        public Color Stroke = Theme.Border;

        public RoundPanel()
        {
            Radius = Ui.S(14);
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Theme.Page;
            Padding = Ui.P(20, 18, 20, 18);
            Margin = Ui.P(0, 0, 0, 14);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            // BackColor = colour behind the rounded corners (page grey for cards, white for inputs inside cards)
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius))
            {
                using (var b = new SolidBrush(Fill)) g.FillPath(b, path);
                using (var p = new Pen(Stroke)) g.DrawPath(p, path);
            }
        }
    }

    /// <summary>Rounded button with optional Fluent icon.</summary>
    class FlatButton : Control
    {
        BtnKind kind;
        string glyph;
        bool hover, down;

        public FlatButton(string text, BtnKind kind, string glyph = null)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Font = Theme.Semi;
            Margin = Ui.P(0, 0, 8, 0);
            TabStop = true;
            Set(text, kind, glyph);
        }

        public int FixedWidth;

        public void Set(string text, BtnKind k, string g)
        {
            if (Text == text && kind == k && glyph == g && Width > 0) return;
            Text = text;
            kind = k;
            glyph = g;
            int w = FixedWidth > 0 ? FixedWidth : TextRenderer.MeasureText(Text, Font).Width + Ui.S(26) + (glyph != null ? Ui.S(22) : 0);
            Size = new Size(w, Ui.S(34));
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty);
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill, fore, stroke;
            switch (kind)
            {
                case BtnKind.Primary: fill = hover ? Theme.AccentHover : Theme.Accent; fore = Color.White; stroke = fill; break;
                case BtnKind.Dark: fill = hover ? Theme.DarkHover : Theme.Dark; fore = Color.White; stroke = fill; break;
                case BtnKind.Success: fill = hover ? ColorTranslator.FromHtml("#C8F5D7") : Theme.GreenSoft; fore = Theme.Green; stroke = fill; break;
                case BtnKind.Danger: fill = hover ? ColorTranslator.FromHtml("#FDD5D5") : Theme.RedSoft; fore = Theme.Red; stroke = fill; break;
                default: fill = hover ? Theme.Hover : Color.White; fore = Theme.Text; stroke = Theme.Border; break;
            }
            if (down) fill = ControlPaint.Dark(fill, 0.03f);
            if (!Enabled) { fill = Theme.GraySoft; fore = Theme.Faint; stroke = Theme.GraySoft; }
            using (var path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(8)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var p = new Pen(stroke)) g.DrawPath(p, path);
            }
            int x = Ui.S(13);
            if (glyph != null)
            {
                var gr = new Rectangle(x, 0, Ui.S(18), Height);
                TextRenderer.DrawText(g, glyph, Theme.Icon, gr, fore, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                x += Ui.S(22);
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x - Ui.S(10), Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues)
                using (var p = new Pen(Theme.Accent)) using (var path = Ui.Round(new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), Ui.S(7))) g.DrawPath(p, path);
        }
    }

    /// <summary>Sidebar navigation entry.</summary>
    class NavItem : Control
    {
        readonly string glyph;
        bool active, hover;

        public NavItem(string glyph, string text)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.glyph = glyph;
            Text = text;
            Font = Theme.Semi;
            Size = new Size(Ui.S(200), Ui.S(40));
            Margin = Ui.P(0, 0, 0, 4);
            Cursor = Cursors.Hand;
            BackColor = Theme.Page;
        }

        public bool Active { get { return active; } set { active = value; Invalidate(); } }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = active ? Color.White : hover ? Theme.Hover : BackColor;
            Color fore = active ? Theme.Text : Theme.Muted;
            if (active || hover)
            {
                using (var path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(10)))
                {
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                    if (active) using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
                }
            }
            if (active)
            {
                using (var b = new SolidBrush(Theme.Accent))
                using (var path = Ui.Round(new RectangleF(Ui.S(6), Ui.S(11), Ui.S(3), Height - Ui.S(22)), Ui.S(2))) g.FillPath(b, path);
            }
            TextRenderer.DrawText(g, glyph, Theme.Icon, new Rectangle(Ui.S(16), 0, Ui.S(22), Height), active ? Theme.Accent : fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.S(46), 0, Width - Ui.S(50), Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Rounded status badge with a dot.</summary>
    class Pill : Control
    {
        Color fill = Theme.GraySoft, fore = Theme.Muted;

        public Pill()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Font = Theme.Semi;
            Margin = new Padding(0);
            Set("Stopped", Theme.GraySoft, Theme.Muted);
        }

        public void Set(string text, Color f, Color c)
        {
            if (Text == text && fill == f && fore == c) return;
            Text = text; fill = f; fore = c;
            var sz = TextRenderer.MeasureText(text, Font);
            Size = new Size(sz.Width + Ui.S(30), sz.Height + Ui.S(8));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), (Height - 1) / 2f))
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            int d = Ui.S(7);
            using (var b = new SolidBrush(fore)) g.FillEllipse(b, Ui.S(11), (Height - d) / 2f, d, d);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.S(22), 0, Width - Ui.S(24), Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Coloured rounded square with an icon (card headers).</summary>
    class IconBadge : Control
    {
        readonly string glyph;
        public Color Fill, Fore;

        public IconBadge(string glyph, Color fill, Color fore)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.Transparent;
            this.glyph = glyph; Fill = fill; Fore = fore;
            Size = new Size(Ui.S(38), Ui.S(38));
            Margin = Ui.P(0, 0, 12, 0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Ui.S(10)))
            using (var b = new SolidBrush(Fill)) g.FillPath(b, path);
            TextRenderer.DrawText(g, glyph, Theme.IconBig, ClientRectangle, Fore, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>Clickable quick-action tile.</summary>
    class Tile : Control
    {
        readonly string glyph;
        readonly string caption;
        bool hover;

        public Tile(string glyph, string title, string caption)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.glyph = glyph;
            this.caption = caption;
            Text = title;
            Font = Theme.Semi;
            Height = Ui.S(72);
            Cursor = Cursors.Hand;
            BackColor = Theme.Page;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(14)))
            {
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                using (var p = new Pen(hover ? Theme.Accent : Theme.Border)) g.DrawPath(p, path);
            }
            int box = Ui.S(38), x = Ui.S(16), y = (Height - box) / 2;
            using (var path = Ui.Round(new RectangleF(x, y, box, box), Ui.S(10)))
            using (var b = new SolidBrush(Theme.AccentSoft)) g.FillPath(b, path);
            TextRenderer.DrawText(g, glyph, Theme.IconBig, new Rectangle(x, y, box, box), Theme.Accent, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            int tx = x + box + Ui.S(12);
            int th = TextRenderer.MeasureText(Text, Font).Height;
            int ch = TextRenderer.MeasureText(caption, Theme.Small).Height;
            int top = (Height - th - ch) / 2;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, top, Width - tx - Ui.S(8), th), Theme.Text, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, caption, Theme.Small, new Rectangle(tx, top + th, Width - tx - Ui.S(8), ch), Theme.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
