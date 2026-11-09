using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WinPlus
{
    /// <summary>
    /// The frozen screen for picking a part of it: drag a box, or click a window
    /// (left click, no drag) to take that whole window. Nothing is picked just by
    /// hovering. Once there's a box, a bar under it offers Save, Save as and Copy
    /// (Enter saves, Ctrl+C copies); dragging again starts a new box.
    ///
    /// The box is always plain (a white line). With the glass on, the bar of buttons
    /// looks like Liquid Glass: what's around bends at the rounded edges, the colors
    /// split a little, a shine on the rim, frosted and tinted so its text reads.
    /// Glass off: a plain dark bar. The saved picture is always the plain area.
    ///
    /// Esc or right-click cancels. A WinForms window created while the thread is
    /// per-monitor aware, so everything is in real pixels and lines up with the
    /// picture on every screen.
    /// </summary>
    internal sealed class RegionPicker : Form
    {
        public enum Choice { Save, SaveAs, Copy }

        // The glass: sizes in pixels at 100%; the rest from the Screen capture page.
        private const double Bezel = 20, Radius = 16;
        private const int DimAlpha = 110;
        private readonly bool _glassOn;               // the box
        private readonly bool _barGlassOn;            // the bar of buttons: glass when the Capture switch is on
        private readonly double _bend, _dispersion, _tint, _rim;
        private readonly int _frost;
        private int[] _src = Array.Empty<int>(), _blur = Array.Empty<int>(), _tmp = Array.Empty<int>(), _buf = Array.Empty<int>();

        private readonly Bitmap _frozen, _dim;
        private readonly int[] _px;                   // the frozen screen, one int per pixel
        private readonly int _fw, _fh;
        private readonly Rectangle _all;              // the whole desktop, screen coordinates
        private readonly List<Rectangle> _windows;    // screen coordinates, top first
        private readonly Rectangle _hintScreen;
        private Point _start;
        private bool _dragging, _picked;              // _picked: the box is done, the bar shows
        private Rectangle _sel, _shown;               // client coordinates
        private Bitmap? _glass, _barGlass;
        private Rectangle _bar;                       // the bar, client coordinates
        private Rectangle[] _buttons = Array.Empty<Rectangle>();
        private int _hover = -1, _pressed = -1;
        private Rectangle? _result;
        private Choice _choice;
        private float _scale = 1;

        private static readonly (Choice Choice, string Icon, string Text)[] Actions =
        {
            (Choice.Save, "", Lang.T("حفظ")),
            (Choice.SaveAs, "", Lang.T("حفظ باسم")),
            (Choice.Copy, "", Lang.T("نسخ")),
        };

        public RegionPicker(Bitmap frozen, Rectangle all, List<Rectangle> windows, Rectangle hintScreen)
        {
            _frozen = frozen;
            _all = all;
            _windows = windows;
            _hintScreen = hintScreen;
            var glass = GlassSettings.Capture;
            _glassOn = false;                // the box is never glass: a plain white line
            _barGlassOn = glass.On;
            _bend = Math.Clamp(glass.Bend, 0, 100) / 100.0;
            _dispersion = Math.Clamp(glass.Dispersion, 0, 100) / 100.0;
            _frost = Math.Clamp(glass.Frost, 0, 12);
            _tint = Math.Clamp(glass.Tint, 0, 80) / 100.0;
            _rim = Math.Clamp(glass.Rim, 0, 100) / 100.0;
            _fw = frozen.Width;
            _fh = frozen.Height;
            _px = new int[_fw * _fh];
            var data = frozen.LockBits(new Rectangle(0, 0, _fw, _fh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < _fh; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, _px, y * _fw, _fw);
            }
            finally { frozen.UnlockBits(data); }

            _dim = new Bitmap(_fw, _fh, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(_dim))
            {
                g.DrawImageUnscaled(frozen, 0, 0);
                using var shade = new SolidBrush(Color.FromArgb(DimAlpha, 0, 0, 0));
                g.FillRectangle(shade, 0, 0, _fw, _fh);
            }

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = all;
            Cursor = Cursors.Cross;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        }

        /// <summary>Shows it and returns the picked area (screen coordinates) and what to do
        /// with it, or null if cancelled.</summary>
        public (Rectangle Area, Choice Choice)? Pick()
        {
            ShowDialog();
            return _result is Rectangle r ? (r, _choice) : null;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            SetWindowPos(Handle, new IntPtr(-1) /* TOPMOST */, _all.X, _all.Y, _all.Width, _all.Height, 0x0040 /* SHOWWINDOW */);
            _scale = Math.Max(1f, GetDpiForWindow(Handle) / 96f);
            Activate();
            SetForegroundWindow(Handle);   // started from a shortcut, so Windows allows it
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            // Stay exactly over the desktop: no resizing when it spans screens with different scaling.
            if (m.Msg is 0x02E0 /* WM_DPICHANGED */ or 0x02E4 /* WM_GETDPISCALEDSIZE */) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
            else if (_picked && e.KeyCode == Keys.Enter) Finish(Choice.Save);
            else if (_picked && e.Control && e.KeyCode == Keys.C) Finish(Choice.Copy);
            else if (_picked && e.Control && e.KeyCode == Keys.S) Finish(e.Shift ? Choice.SaveAs : Choice.Save);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (Visible) Close();   // something else took over: cancel
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { Close(); return; }
            if (e.Button != MouseButtons.Left) return;
            int b = ButtonAt(e.Location);
            if (b >= 0) { _pressed = b; InvalidateBar(); return; }
            _start = e.Location;
            _dragging = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_dragging)
            {
                // Hovering picks nothing; it only lights up the bar's buttons.
                int b = ButtonAt(e.Location);
                if (b != _hover) { _hover = b; InvalidateBar(); }
                Cursor = b >= 0 ? Cursors.Hand : Cursors.Cross;
                return;
            }
            if (Math.Abs(e.X - _start.X) < 4 && Math.Abs(e.Y - _start.Y) < 4) return;
            if (_picked) HideBar();   // a new box
            SetSelection(Rectangle.FromLTRB(Math.Min(_start.X, e.X), Math.Min(_start.Y, e.Y),
                                            Math.Max(_start.X, e.X), Math.Max(_start.Y, e.Y)));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (_pressed >= 0)
            {
                int b = _pressed;
                _pressed = -1;
                InvalidateBar();
                if (ButtonAt(e.Location) == b) Finish(Actions[b].Choice);
                return;
            }
            if (!_dragging) return;
            _dragging = false;
            bool dragged = Math.Abs(e.X - _start.X) >= 4 || Math.Abs(e.Y - _start.Y) >= 4;
            if (!dragged)
            {
                // A click: the window under it (or its screen if there's none).
                var screen = new Point(e.X + _all.X, e.Y + _all.Y);
                Rectangle hit = Rectangle.Empty;
                foreach (var w in _windows)
                {
                    if (w.Contains(screen)) { hit = w; break; }
                }
                if (hit.IsEmpty) hit = Screen.FromPoint(screen).Bounds;
                hit.Offset(-_all.X, -_all.Y);
                if (_picked) HideBar();
                SetSelection(hit);
            }
            if (_sel.Width >= 2 && _sel.Height >= 2) ShowBar();
            else Invalidate(HintRect());
        }

        private void Finish(Choice choice)
        {
            var r = Rectangle.Intersect(_sel, new Rectangle(0, 0, _all.Width, _all.Height));
            if (r.Width < 2 || r.Height < 2) return;
            r.Offset(_all.X, _all.Y);
            _result = r;
            _choice = choice;
            Close();
        }

        private void SetSelection(Rectangle sel)
        {
            sel = Rectangle.Intersect(sel, new Rectangle(0, 0, _fw, _fh));
            if (sel == _sel) return;
            _sel = sel;
            _glass = Render(_sel, _glass, bar: false);
            var old = _shown;
            _shown = Decorated(sel);
            Invalidate(Rectangle.Union(old, _shown));
            Invalidate(HintRect());
        }

        // ---- The bar: Save · Save as · Copy ----------------------------------------------------

        private Font? _barFont, _iconFont;
        private Font BarFont => _barFont ??= new Font("Segoe UI", 14f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
        private Font IconFont => _iconFont ??= new Font(IconFamily(), 15f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);

        private static string IconFamily()
        {
            using var fonts = new System.Drawing.Text.InstalledFontCollection();
            foreach (var f in fonts.Families)
                if (f.Name == "Segoe Fluent Icons") return f.Name;
            return "Segoe MDL2 Assets";
        }

        private void ShowBar()
        {
            _picked = true;
            int pad = (int)(6 * _scale), h = (int)(46 * _scale), gap = (int)(4 * _scale);
            int iconW = (int)(18 * _scale), inner = (int)(8 * _scale), side = (int)(16 * _scale);
            var widths = new int[Actions.Length];
            int total = pad * 2 + gap * (Actions.Length - 1);
            for (int i = 0; i < Actions.Length; i++)
            {
                widths[i] = side * 2 + iconW + inner + TextRenderer.MeasureText(Actions[i].Text, BarFont).Width;
                total += widths[i];
            }

            // Under the box, else above it, else inside at the bottom; kept on its screen.
            var center = new Point(_sel.X + _sel.Width / 2 + _all.X, _sel.Y + _sel.Height / 2 + _all.Y);
            var scr = Screen.FromPoint(center).Bounds;
            scr.Offset(-_all.X, -_all.Y);
            int space = (int)(12 * _scale);
            int y = _sel.Bottom + space;
            if (y + h > scr.Bottom - space) y = _sel.Top - space - h;
            if (y < scr.Top + space) y = _sel.Bottom - space - h;
            int x = _sel.X + (_sel.Width - total) / 2;
            x = Math.Clamp(x, scr.Left + space, Math.Max(scr.Left + space, scr.Right - space - total));
            _bar = new Rectangle(x, y, total, h);

            _buttons = new Rectangle[Actions.Length];
            int bx = x + pad;
            for (int i = 0; i < Actions.Length; i++)
            {
                _buttons[i] = new Rectangle(bx, y + pad, widths[i], h - pad * 2);
                bx += widths[i] + gap;
            }
            if (!Lang.English) Array.Reverse(_buttons);   // right to left: Save on the right
            _barGlass = Render(_bar, _barGlass, bar: true);
            _hover = ButtonAt(PointToClient(Cursor.Position));
            InvalidateBar();
            Invalidate(HintRect());
        }

        private void HideBar()
        {
            InvalidateBar();
            _picked = false;
            _buttons = Array.Empty<Rectangle>();
            _hover = _pressed = -1;
            Cursor = Cursors.Cross;
        }

        private int ButtonAt(Point p)
        {
            if (!_picked) return -1;
            for (int i = 0; i < _buttons.Length; i++)
                if (_buttons[i].Contains(p)) return i;
            return -1;
        }

        private void InvalidateBar()
        {
            if (_bar.IsEmpty) return;
            var r = _bar;
            r.Inflate((int)(14 * _scale), (int)(14 * _scale));
            Invalidate(r);
        }

        // ---- The glass ---------------------------------------------------------------------------

        /// <summary>
        /// Draws a rounded piece of glass over <paramref name="rect"/> (client coordinates).
        /// The box shows the screen as it is (frost only on its rim); the bar shows the
        /// dimmed screen, frosted and tinted all over so its text reads. Near the rounded
        /// edge each pixel shows what's a little further out (a lens rim), red / blue a
        /// bit more / less than green; a bright rim lit from above; see-through outside
        /// the rounded corners. Glass off: the box is the screen as it is, square, and
        /// there's no bar picture (a plain one is drawn).
        /// </summary>
        private Bitmap? Render(Rectangle rect, Bitmap? reuse, bool bar)
        {
            int w = rect.Width, h = rect.Height;
            if (w < 2 || h < 2 || (bar && !_barGlassOn)) { reuse?.Dispose(); return null; }
            var bmp = reuse;
            if (bmp == null || bmp.Width != w || bmp.Height != h)
            {
                reuse?.Dispose();
                bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            }
            if (_buf.Length < w * h) _buf = new int[w * h];

            if (!bar && !_glassOn)
            {
                for (int y = 0; y < h; y++)
                    Array.Copy(_px, (rect.Y + y) * _fw + rect.X, _buf, y * w, w);
                Upload(bmp, w, h);
                return bmp;
            }

            double s = _scale;
            double radius = bar ? h / 2.0 : Math.Min(Radius * s, Math.Min(w, h) / 2.0);
            double bezel = Math.Max(1, Math.Min(Bezel * s, Math.Min(w, h) / 2.0));
            int frostPx = (int)Math.Round((bar ? Math.Max(_frost, 6) : _frost) * s);
            double tint = bar ? Math.Max(_tint, 0.30) : 0;   // the box stays clear: you see what you take
            bool frostAll = bar;
            int keepDim = 255 - DimAlpha;   // the bar sits on the dimmed screen

            // What the glass shows: the area plus a margin around it (the rim bends in
            // what's just outside).
            int m = (int)Math.Ceiling(bezel * _bend * (1 + _dispersion)) + 2;
            int cw = w + 2 * m, ch = h + 2 * m;
            if (_src.Length < cw * ch) { _src = new int[cw * ch]; _blur = new int[cw * ch]; _tmp = new int[cw * ch]; }
            for (int y = 0; y < ch; y++)
            {
                int sy = Math.Clamp(rect.Y - m + y, 0, _fh - 1) * _fw;
                int row = y * cw;
                for (int x = 0; x < cw; x++)
                {
                    int c = _px[sy + Math.Clamp(rect.X - m + x, 0, _fw - 1)];
                    if (bar)
                        c = ((((c >> 16) & 0xFF) * keepDim / 255) << 16) | ((((c >> 8) & 0xFF) * keepDim / 255) << 8) | ((c & 0xFF) * keepDim / 255);
                    _src[row + x] = c;
                }
            }
            if (frostPx > 0)
            {
                Array.Copy(_src, _blur, cw * ch);
                LiquidGlass.BoxBlur(_blur, _tmp, cw, ch, frostPx);
            }

            double hx = w / 2.0, hy = h / 2.0;
            double Sdf(double px, double py)   // signed distance to the rounded box (negative inside)
            {
                double qx = Math.Abs(px - hx) - (hx - radius), qy = Math.Abs(py - hy) - (hy - radius);
                double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
                return Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - radius;
            }

            const int TintR = 0x1C, TintG = 0x1C, TintB = 0x1E;
            int keep = (int)((1 - tint) * 256);
            int tr = (int)(TintR * tint), tg = (int)(TintG * tint), tb = (int)(TintB * tint);
            int band = (int)Math.Ceiling(bezel + radius) + 1;   // closer than this to the edge: bend and rim
            for (int y = 0; y < h; y++)
            {
                bool edgeRow = y < band || y >= h - band;
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    int r, g, b, shine = 0, a = 255;
                    bool middle = !edgeRow && x >= band && x < w - band;
                    double d = middle ? bezel : -Sdf(x + 0.5, y + 0.5);   // how far in from the edge
                    if (d <= -0.5) { _buf[i] = 0; continue; }             // outside a rounded corner
                    if (d >= bezel)
                    {
                        int at = (y + m) * cw + x + m;
                        int c = frostAll && frostPx > 0 ? _blur[at] : _src[at];
                        r = (c >> 16) & 0xFF; g = (c >> 8) & 0xFF; b = c & 0xFF;
                    }
                    else
                    {
                        double px = x + 0.5, py = y + 0.5;
                        double nx = Sdf(px + 1, py) - Sdf(px - 1, py);
                        double ny = Sdf(px, py + 1) - Sdf(px, py - 1);
                        double len = Math.Sqrt(nx * nx + ny * ny);
                        if (len > 1e-6) { nx /= len; ny /= len; } else { nx = ny = 0; }

                        double t = Math.Clamp(d / bezel, 0, 1);
                        double amount = bezel * _bend * (1 - t) * (1 - t);
                        int ir = Sample(px, py, nx, ny, amount * (1 + _dispersion));
                        int ig = Sample(px, py, nx, ny, amount);
                        int ib = Sample(px, py, nx, ny, amount * (1 - _dispersion));
                        r = (_src[ir] >> 16) & 0xFF;
                        g = (_src[ig] >> 8) & 0xFF;
                        b = _src[ib] & 0xFF;
                        if (frostPx > 0)
                        {
                            // The box: full frost at the edge, fading to sharp toward the middle (no seam).
                            int f = frostAll ? 256 : (int)((1 - t * t) * 256);
                            r += ((((_blur[ir] >> 16) & 0xFF) - r) * f) >> 8;
                            g += ((((_blur[ig] >> 8) & 0xFF) - g) * f) >> 8;
                            b += (((_blur[ib] & 0xFF) - b) * f) >> 8;
                        }

                        double edge = Math.Clamp(1 - d / (1.6 * s), 0, 1);
                        double facing = 0.35 + 0.65 * Math.Max(0, -ny);
                        double glow = Math.Clamp(1 - d / (bezel * 0.9), 0, 1);
                        shine = (int)Math.Clamp(_rim * (edge * facing + 0.18 * glow * glow * Math.Max(0, -ny)) * 255, 0, 255);
                        if (d < 0.5) a = (int)((d + 0.5) * 255);   // smooth the curved edge
                    }

                    if (keep < 256)
                    {
                        r = ((r * keep) >> 8) + tr;
                        g = ((g * keep) >> 8) + tg;
                        b = ((b * keep) >> 8) + tb;
                    }
                    if (shine > 0)
                    {
                        r += ((255 - r) * shine) >> 8;
                        g += ((255 - g) * shine) >> 8;
                        b += ((255 - b) * shine) >> 8;
                    }
                    if (a < 255) { r = r * a / 255; g = g * a / 255; b = b * a / 255; }
                    _buf[i] = (a << 24) | (r << 16) | (g << 8) | b;
                }
            }
            Upload(bmp, w, h);
            return bmp;

            int Sample(double px, double py, double nx, double ny, double amount)
            {
                int sx = Math.Clamp((int)Math.Round(px + nx * amount) + m, 0, cw - 1);
                int sy = Math.Clamp((int)Math.Round(py + ny * amount) + m, 0, ch - 1);
                return sy * cw + sx;
            }
        }

        private void Upload(Bitmap bmp, int w, int h)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(_buf, y * w, data.Scan0 + y * data.Stride, w);
            }
            finally { bmp.UnlockBits(data); }
        }

        // ---- Painting ----------------------------------------------------------------------------

        /// <summary>The box plus its shadow and the size label.</summary>
        private Rectangle Decorated(Rectangle sel)
        {
            if (sel.IsEmpty) return sel;
            var r = sel;
            r.Inflate((int)(14 * _scale), (int)(14 * _scale));
            return Rectangle.Union(r, LabelRect(sel));
        }

        private string SizeText => $"{_sel.Width} × {_sel.Height}";

        private Font? _font;
        private Font LabelFont => _font ??= new Font("Segoe UI", 13f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);

        private Rectangle LabelRect(Rectangle sel)
        {
            var size = TextRenderer.MeasureText(SizeText, LabelFont);
            int pad = (int)(8 * _scale);
            int w = size.Width + pad * 2, h = size.Height + pad;
            int y = sel.Top - h - (int)(8 * _scale);
            if (y < 0) y = sel.Top + (int)(8 * _scale);
            return new Rectangle(sel.Left, y, w, h);
        }

        private Rectangle HintRect()
        {
            int w = (int)(480 * _scale), h = (int)(40 * _scale);
            int x = _hintScreen.X - _all.X + (_hintScreen.Width - w) / 2;
            int y = _hintScreen.Y - _all.Y + (int)(28 * _scale);
            return new Rectangle(x, y, w, h);
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            if (d < 1) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>A soft shadow under a rounded shape, so it floats.</summary>
        private void Shadow(Graphics g, Rectangle r0, float radius)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int k = 3; k >= 1; k--)
            {
                var r = new RectangleF(r0.X - k * 3 * _scale, r0.Y - k * 3 * _scale + 3 * _scale,
                                       r0.Width + k * 6 * _scale, r0.Height + k * 6 * _scale);
                using var path = Rounded(r, radius + k * 3 * _scale);
                using var shade = new SolidBrush(Color.FromArgb(22, 0, 0, 0));
                g.FillPath(shade, path);
            }
            g.SmoothingMode = SmoothingMode.None;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            var clip = e.ClipRectangle;
            g.DrawImage(_dim, clip, clip, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;

            if (_glass != null && !_sel.IsEmpty)
            {
                if (_glassOn)
                {
                    Shadow(g, _sel, (float)Math.Min(Radius * _scale, Math.Min(_sel.Width, _sel.Height) / 2.0));
                    g.DrawImageUnscaled(_glass, _sel.X, _sel.Y);
                }
                else
                {
                    // Plain: the area as it is, with a white line around it.
                    g.DrawImageUnscaled(_glass, _sel.X, _sel.Y);
                    float t = Math.Max(2, 2 * _scale);
                    using var pen = new Pen(Color.White, t) { Alignment = PenAlignment.Outset };
                    g.DrawRectangle(pen, _sel.X - 1, _sel.Y - 1, _sel.Width + 1, _sel.Height + 1);
                }

                var label = LabelRect(_sel);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Rounded(label, label.Height / 2f))
                using (var back = new SolidBrush(Color.FromArgb(220, 30, 31, 34)))
                    g.FillPath(back, path);
                g.SmoothingMode = SmoothingMode.None;
                TextRenderer.DrawText(g, SizeText, LabelFont, label, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            if (_picked) PaintBar(g);

            if (!_dragging && !_picked)
            {
                var hint = HintRect();
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Rounded(hint, hint.Height / 2f))
                using (var back = new SolidBrush(Color.FromArgb(225, 30, 31, 34)))
                    g.FillPath(back, path);
                g.SmoothingMode = SmoothingMode.None;
                var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
                if (!Lang.English) flags |= TextFormatFlags.RightToLeft;
                TextRenderer.DrawText(g, Lang.T("اسحب على المكان اللي تبيه، أو اضغط على نافذة · Esc للإلغاء"),
                    LabelFont, hint, Color.White, flags);
            }
        }

        private void PaintBar(Graphics g)
        {
            float radius = _bar.Height / 2f;
            if (_barGlass != null)
            {
                Shadow(g, _bar, radius);
                g.DrawImageUnscaled(_barGlass, _bar.X, _bar.Y);
            }
            else
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Rounded(_bar, radius);
                using var back = new SolidBrush(Color.FromArgb(240, 30, 31, 34));
                g.FillPath(back, path);
                using var line = new Pen(Color.FromArgb(70, 255, 255, 255), 1);
                g.DrawPath(line, path);
                g.SmoothingMode = SmoothingMode.None;
            }

            int iconW = (int)(18 * _scale), inner = (int)(8 * _scale), side = (int)(16 * _scale);
            for (int i = 0; i < _buttons.Length; i++)
            {
                var b = _buttons[i];
                if (i == _hover || i == _pressed)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using var path = Rounded(b, b.Height / 2f);
                    using var lit = new SolidBrush(Color.FromArgb(i == _pressed ? 70 : 45, 255, 255, 255));
                    g.FillPath(lit, path);
                    g.SmoothingMode = SmoothingMode.None;
                }
                var (_, icon, text) = Actions[i];
                var iconRect = Lang.English
                    ? new Rectangle(b.X + side, b.Y, iconW, b.Height)
                    : new Rectangle(b.Right - side - iconW, b.Y, iconW, b.Height);
                var textRect = Lang.English
                    ? new Rectangle(iconRect.Right + inner, b.Y, b.Right - side - iconRect.Right - inner, b.Height)
                    : new Rectangle(b.X + side, b.Y, iconRect.Left - inner - b.X - side, b.Height);
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                TextRenderer.DrawText(g, icon, IconFont, iconRect, Color.White, flags);
                TextRenderer.DrawText(g, text, BarFont, textRect, Color.White,
                    flags | (Lang.English ? 0 : TextFormatFlags.RightToLeft));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _dim.Dispose();
                _glass?.Dispose();
                _barGlass?.Dispose();
                _font?.Dispose();
                _barFont?.Dispose();
                _iconFont?.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- The windows on screen (for click-to-pick) --------------------------------------------

        /// <summary>Visible top-level windows, top first, as they look (no invisible borders).
        /// Call while the thread is per-monitor aware.</summary>
        public static List<Rectangle> ListWindows(Rectangle all)
        {
            var list = new List<Rectangle>();
            var cls = new StringBuilder(64);
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
                if (DwmGetWindowAttribute(hwnd, 14 /* CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
                long ex = GetWindowLongPtr(hwnd, -20 /* EXSTYLE */).ToInt64();
                if ((ex & 0x20 /* TRANSPARENT */) != 0) return true;
                cls.Clear();
                GetClassName(hwnd, cls, cls.Capacity);
                string c = cls.ToString();
                if (c is "Progman" or "WorkerW") return true;
                if (GetWindowTextLength(hwnd) == 0 && c != "Shell_TrayWnd" && c != "Shell_SecondaryTrayWnd") return true;
                if (DwmGetWindowAttribute(hwnd, 9 /* EXTENDED_FRAME_BOUNDS */, out ScreenCapture.RECT r, 16) != 0
                    && !GetWindowRect(hwnd, out r)) return true;
                var rect = Rectangle.Intersect(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), all);
                if (rect.Width > 20 && rect.Height > 20) list.Add(rect);
                return true;
            }, IntPtr.Zero);
            return list;
        }


        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out ScreenCapture.RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out ScreenCapture.RECT value, int size);
    }
}
