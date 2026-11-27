using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinPlus
{
    /// <summary>
    /// Apple's "Liquid Glass" for one rounded element of a window (the download bar):
    /// what's behind it on the screen, bent like a lens at the rounded edges, with
    /// the colors splitting a little there, a light frost, a dark tint so the text
    /// stays readable, and a shine on the rim.
    ///
    /// A picture of what's behind is taken when it starts (and again on Refresh): for
    /// that moment only, the window is left out of screen copies (WDA_EXCLUDEFROMCAPTURE),
    /// then it's back. So screen sharing (Discord, Teams, OBS…) and screenshots show the
    /// window with its glass; kept out all the time, they wouldn't show it at all.
    /// The bend for every pixel is worked out once; a picture is a copy, a blur and a
    /// lookup, done on its own thread.
    /// </summary>
    internal sealed class LiquidGlass : IDisposable
    {
        // Looks (in device-independent pixels where it's a size).
        private const double Bezel = 18;        // how far in from the edge the glass bends
        // The rest comes from the settings (Video download page).
        private readonly double Bend, Dispersion, TintAlpha, RimShine;   // from the settings
        private readonly int Frost;
        // The tint's color: dark gray, or the color asked for (e.g. the widgets' background color).
        private readonly byte TintR = 0x1C, TintG = 0x1C, TintB = 0x1E;
        private readonly double TintLight = 0.11;   // how light the tint color is (0–1)

        private readonly Window _window;
        private readonly FrameworkElement _host;     // not transformed (the element's parent)
        private readonly FrameworkElement _target;   // the element that gets the glass
        private readonly Thickness _offset;           // where the element sits in the host
        private readonly double _radius;

        private int _w, _h, _margin, _cw, _ch;        // output size, capture margin, capture size (pixels)
        private int[] _src = Array.Empty<int>(), _tmp = Array.Empty<int>(), _out = Array.Empty<int>();
        private int[] _mapR = Array.Empty<int>(), _mapG = Array.Empty<int>(), _mapB = Array.Empty<int>();
        private byte[] _rim = Array.Empty<byte>();
        private WriteableBitmap? _bitmap;
        private ImageBrush? _brush;
        private int[] _front = Array.Empty<int>();    // the finished frame, handed to the window
        private int _pending;                          // a frame is waiting for the window
        private IntPtr _screenDc, _memDc, _dib, _bits, _oldObj;
        private volatile bool _running;
        private Thread? _thread;
        private readonly IntPtr _hwnd;
        private readonly string _who;                  // which window (for the log)
        private readonly AutoResetEvent _wake = new(false);
        private int _wanted;                           // a new picture of what's behind is wanted
        private int _x, _y;                            // where the copy is taken (screen pixels)
        private readonly bool _desktop;                // copy the desktop itself, not the screen (widgets)
        private volatile int _ox, _oy;                 // desktop mode: the element's top left (screen pixels)
        private int _recapture = 1;                    // desktop mode: a new picture of the desktop is wanted
        private IntPtr _deskDc, _deskBmp, _deskOld;    // desktop mode: the last picture of the whole desktop
        private RECT _deskRect;
        private bool _sharedRef;                       // desktop mode: uses the shared picture of the desktop
        private bool _videoShown;                      // desktop mode: the glass shows a frame of the video wallpaper
        private long _videoAt;                         // desktop mode: when it last got one
        private readonly bool _live;                   // live mode: the screen behind, again and again (the taskbar)
        private int[] _last = Array.Empty<int>();      // live mode: the last copy, to skip drawing when nothing changed
        private const int LiveFrameMs = 33;            // live mode: about 30 copies a second
        /// <summary>
        /// Live mode: what's behind as it is right now, about 30 times a second (videos, windows changing…).
        /// false = one picture only when something changes near the bar (see GlassCalm), the calm way.
        /// Click-through overlays (NVIDIA's…) never count as behind, so with nothing behind it's the wallpaper,
        /// taken from the desktop's picture, not copied from the screen.
        /// </summary>
        internal static readonly bool LiveStream = true;
        private int _wx0, _wy0;                        // live mode: the window's top left when it started (pixels)
        private int _still;                            // live mode: copies in a row with nothing new behind
        private volatile bool _retry;                  // live mode: the last copy didn't work, try again soon
        private bool _useMag, _magTried;               // live mode: the screen copied without our windows (they stay in screen shares)
        private int _magMisses;
        private static long _blackNoted;
        private long _blackSince;                      // live mode: when the copies started coming back all black (0 = they aren't)

        /// <summary>The windows with a live glass: left out of every live glass's copy of the screen.</summary>
        private static readonly System.Collections.Generic.Dictionary<IntPtr, int> LiveWindows = new();   // window → how many
        private static int _liveVersion;
        private static IntPtr[] _liveList = Array.Empty<IntPtr>();

        /// <summary>
        /// What's behind turned light (true) or dark (false): the text on the glass
        /// should switch to dark / light to stay readable.
        /// </summary>
        public event Action<bool>? LightBehindChanged;
        private volatile bool _lightBehind;
        private bool _lightReported, _reportedOnce;

        /// <summary>Can the window be left out of a screen copy (Windows 10 2004 and later)? Without it there's no glass.</summary>
        public bool Available { get; }

        /// <param name="tint">A color for the tint instead of the dark gray, and how strong (0–1) it is.</param>
        /// <param name="live">Show what's behind as it is right now, all the time (windows, lights, videos…), not
        /// one picture. The screen is copied through the magnifier without our windows, so they still show in
        /// screenshots and screen shares.</param>
        public LiquidGlass(Window window, FrameworkElement host, FrameworkElement target, Thickness offset, double radius, GlassSettings settings,
                           bool desktop = false, (Color Color, double Alpha)? tint = null, bool live = false)
        {
            _live = live && !desktop;
            _desktop = desktop;
            Bend = Math.Clamp(settings.Bend, 0, 100) / 100.0;
            Dispersion = Math.Clamp(settings.Dispersion, 0, 100) / 100.0;
            Frost = Math.Clamp(settings.Frost, 0, 12);
            TintAlpha = Math.Clamp(settings.Tint, 0, 80) / 100.0;
            if (tint is { } t)
            {
                TintR = t.Color.R; TintG = t.Color.G; TintB = t.Color.B;
                TintAlpha = Math.Clamp(t.Alpha, 0, 1);
                TintLight = (t.Color.R * 3 + t.Color.G * 6 + t.Color.B) / 10.0 / 255;
            }
            RimShine = Math.Clamp(settings.Rim, 0, 100) / 100.0;
            _window = window;
            _host = host;
            _target = target;
            _offset = offset;
            _radius = radius;
            _hwnd = new WindowInteropHelper(window).EnsureHandle();
            _who = window.GetType().Name;
            // Shown normally in screen copies (older versions kept it out all the time).
            // Live mode copies the screen through the magnifier, which leaves our windows out of its copy only,
            // so the window stays in screenshots and screen shares (Discord…). Only if that fails is the window
            // left out of every screen copy instead (see Loop).
            // A live glass leaves it alone: the window is (or will be) out of screen copies for another live glass
            // on it, and putting it back in made that glass copy itself (it looked frozen).
            Available = Environment.OSVersion.Version.Build >= 19041
                        && ((live && !desktop && LiveStream) || SetWindowDisplayAffinity(_hwnd, 0u /* WDA_NONE */));
        }

        /// <summary>
        /// Starts the glass (call once the window is in its place). The screen copying
        /// and drawing run on their own thread, so the window never waits for them;
        /// the window only gets the finished picture.
        /// </summary>
        public void Start()
        {
            if (!Available || _running) return;
            if (!_window.IsVisible || _target.ActualWidth < 4 || PresentationSource.FromVisual(_host) == null) return;
            var dpi = VisualTreeHelper.GetDpi(_window);
            int w = (int)Math.Round(_target.ActualWidth * dpi.DpiScaleX);
            int h = (int)Math.Round(_target.ActualHeight * dpi.DpiScaleY);
            // Where the element is on the screen, in pixels (from its untransformed parent,
            // so the open/close animation doesn't move what's copied).
            Point origin = _host.PointToScreen(new Point(_offset.Left, _offset.Top));

            _bitmap = new WriteableBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Bgr32, null);
            _front = new int[w * h];
            _running = true;
            _wanted = 1;
            int ox = (int)Math.Round(origin.X), oy = (int)Math.Round(origin.Y);
            if (_desktop || _live)
            {
                // In real pixels, like the desktop picture.
                RECT r;
                using (new ScreenCapture.DpiScope()) GetWindowRect(_hwnd, out r);
                ox = r.Left + (int)Math.Round(_offset.Left * dpi.DpiScaleX);
                oy = r.Top + (int)Math.Round(_offset.Top * dpi.DpiScaleY);
                _ox = ox; _oy = oy;
                _wx0 = r.Left; _wy0 = r.Top;
            }
            double scale = dpi.DpiScaleX;
            _thread = new Thread(() => Loop(ox, oy, w, h, scale))
            {
                IsBackground = true, Name = "Wibn glass", Priority = ThreadPriority.BelowNormal,
            };
            // Left out of the screen copies before the first copy is taken (or that copy has the bar itself in it).
            if (_desktop) lock (DesktopGlasses) DesktopGlasses.Add(this);
            // Desktop mode: the window moved (a bar put in another place, the same size): the glass follows by itself.
            if ((_desktop || _live) && _onMoved == null) { _onMoved = (_, _) => Follow(); _window.LocationChanged += _onMoved; }
            if (_live) lock (LiveWindows) { LiveWindows[_hwnd] = (LiveWindows.TryGetValue(_hwnd, out int had) ? had : 0) + 1; LiveChanged(); }
            if (_live) { lock (LiveGlasses) LiveGlasses.Add(this); GlassCalm.Start(); if (LiveStream) StartClock(); }
            _thread.Start();
        }

        private static readonly System.Collections.Generic.HashSet<LiquidGlass> DesktopGlasses = new();

        /// <summary>The wallpaper changed: every glass over the desktop takes a new picture of it (any thread).</summary>
        public static void RefreshDesktop()
        {
            LiquidGlass[] all;
            lock (DesktopGlasses) all = System.Linq.Enumerable.ToArray(DesktopGlasses);
            foreach (var g in all) g.Refresh();
        }

        private static long _videoFrameAt;

        /// <summary>
        /// The video wallpaper showed a new frame (its own thread): every glass over the desktop (the widgets)
        /// draws again from it, at most 10 times a second, so the video moves behind them too.
        /// </summary>
        public static void VideoFrameShown()
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _videoFrameAt) < 100) return;
            Interlocked.Exchange(ref _videoFrameAt, now);
            LiquidGlass[] all;
            lock (DesktopGlasses)
            {
                if (DesktopGlasses.Count == 0) return;
                all = System.Linq.Enumerable.ToArray(DesktopGlasses);
            }
            foreach (var g in all)
            {
                Interlocked.Exchange(ref g._wanted, 1);
                g._wake.Set();
            }
        }

        /// <summary>Take a new picture of what's behind (e.g. the wallpaper changed).</summary>
        public void Refresh()
        {
            Interlocked.Exchange(ref _recapture, 1);
            Interlocked.Exchange(ref _wanted, 1);
            _wake.Set();
        }

        /// <summary>
        /// Desktop mode: the window moved (same size). The glass follows it from the
        /// picture of the desktop it already has, so it's quick enough while dragging.
        /// </summary>
        public void Follow()
        {
            if (_live && _running) { Interlocked.Exchange(ref _wanted, 1); _wake.Set(); return; }   // a new picture where it is now
            if (!_desktop || !_running) return;
            RECT r;
            using (new ScreenCapture.DpiScope()) if (!GetWindowRect(_hwnd, out r)) return;
            var dpi = VisualTreeHelper.GetDpi(_window);
            _ox = r.Left + (int)Math.Round(_offset.Left * dpi.DpiScaleX);
            _oy = r.Top + (int)Math.Round(_offset.Top * dpi.DpiScaleY);
            Interlocked.Exchange(ref _wanted, 1);
            _wake.Set();
        }

        private EventHandler? _onMoved;

        // ---- Live glass: the wallpaper for a moment (a window shrinking or growing over the bars) ----------

        private static readonly System.Collections.Generic.HashSet<LiquidGlass> LiveGlasses = new();
        private static long _wallpaperUntil;

        /// <summary>For the next ms, the live glasses show the wallpaper behind them, not the windows (see <see cref="GlassCalm"/>).</summary>
        public static void WallpaperForAMoment(int ms)
        {
            long until = Environment.TickCount64 + ms;
            if (until <= Interlocked.Read(ref _wallpaperUntil)) return;
            Interlocked.Exchange(ref _wallpaperUntil, until);
            RecaptureLive();   // the wallpaper right now
            _afterMoment.Change(ms + 150, Timeout.Infinite);   // and what's really behind once it's over
        }

        private static readonly Timer _afterMoment = new(_ => RecaptureLive(), null, Timeout.Infinite, Timeout.Infinite);

        /// <summary>Every live glass takes one new picture of what's behind it now.</summary>
        public static void RecaptureLive()
        {
            lock (LiveGlasses) foreach (var g in LiveGlasses) { Interlocked.Exchange(ref g._wanted, 1); g._wake.Set(); }
        }

        /// <summary>
        /// Does this screen area (real pixels) reach any live glass (with a little room for the bend)?
        /// A window changing far from the bars changes nothing on them, so no new picture is taken.
        /// </summary>
        public static bool TouchesLive(int left, int top, int right, int bottom)
        {
            lock (LiveGlasses)
                foreach (var g in LiveGlasses)
                    if (g.Touches(left, top, right, bottom)) return true;
            return false;
        }

        /// <summary>Only the live glasses this screen area (real pixels) reaches take a new picture.</summary>
        public static void RecaptureLive(int left, int top, int right, int bottom)
        {
            lock (LiveGlasses)
                foreach (var g in LiveGlasses)
                    if (g.Touches(left, top, right, bottom)) { Interlocked.Exchange(ref g._wanted, 1); g._wake.Set(); }
        }

        private bool Touches(int left, int top, int right, int bottom)
        {
            if (!_running) return false;
            RECT r;
            using (new ScreenCapture.DpiScope()) if (!GetWindowRect(_hwnd, out r)) return false;
            int m = _margin + 8;
            return left < r.Right + m && right > r.Left - m && top < r.Bottom + m && bottom > r.Top - m;
        }

        /// <summary>
        /// How much the live glasses copy (a line in the log once a minute, only when they did): to see
        /// what they cost while the PC is in use.
        /// </summary>
        private static class Stats
        {
            public static int Copies, Drawn, Screen;
            private static double _ms, _worst;
            private static long _since = Environment.TickCount64;
            private static readonly object Gate = new();

            private static string _worstWho = "";

            public static void Add(double ms, bool drawn, string who = "")
            {
                lock (Gate)
                {
                    Copies++; if (drawn) Drawn++;
                    _ms += ms; if (ms > _worst) { _worst = ms; _worstWho = who; }
                    long now = Environment.TickCount64;
                    if (now - _since < 60_000) return;
                    CrashLog.Note($"glass: {Copies} copies in {(now - _since) / 1000}s ({Volatile.Read(ref Screen)} of the screen, {Drawn} drawn), {_ms:0} ms in all, worst {_worst:0} ms ({_worstWho}), last behind: {BehindWhat}");
                    Copies = Drawn = 0; Volatile.Write(ref Screen, 0); _ms = _worst = 0; _since = now;
                }
            }
        }

        // ---- A window maximizing: its color for the animation -------------------------------------------------

        private static RECT _standIn;            // where the window will be (real pixels)
        private static int _standInColor;
        private static long _standInUntil;
        private const int StandInMs = 320;       // a little longer than Windows' animation

        /// <summary>
        /// A window started maximizing (from old to now, real pixels). Windows' animation isn't in screen copies,
        /// so until it's done, where the window will be is filled with the color of its top (its title bar, taken
        /// from where it still is) — under the frost it looks just like the window.
        /// </summary>
        public static void Maximizing(int ol, int ot, int or, int ob, int l, int t, int r, int b)
        {
            int w = or - ol, h = Math.Min(32, ob - ot);
            if (w <= 0 || h <= 0) return;
            using var _ = new ScreenCapture.DpiScope();
            IntPtr screen = GetDC(IntPtr.Zero), dc = CreateCompatibleDC(screen);
            try
            {
                var bmi = new BITMAPINFO { biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                IntPtr dib = CreateDIBSection(dc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero) return;
                IntPtr old = SelectObject(dc, dib);
                try
                {
                    if (!BitBlt(dc, 0, 0, w, h, screen, ol, ot, 0x00CC0020 /* SRCCOPY */)) return;
                    GdiFlush();
                    var px = new int[w * h];
                    Marshal.Copy(bits, px, 0, px.Length);
                    long sr = 0, sg = 0, sb = 0; int n = 0;
                    for (int i = 0; i < px.Length; i += 3) { int c = px[i]; sr += (c >> 16) & 0xFF; sg += (c >> 8) & 0xFF; sb += c & 0xFF; n++; }
                    if (n == 0) return;
                    _standInColor = (int)((sr / n) << 16 | (sg / n) << 8 | (sb / n));
                    _standIn = new RECT { Left = l, Top = t, Right = r, Bottom = b };
                    Interlocked.Exchange(ref _standInUntil, Environment.TickCount64 + StandInMs);
                }
                finally { SelectObject(dc, old); DeleteObject(dib); }
            }
            finally { DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen); }
        }

        /// <summary>While a window is maximizing: its color over the part of the copy (at x, y) it will cover.</summary>
        private void ApplyStandIn(int x, int y)
        {
            if (Environment.TickCount64 >= Interlocked.Read(ref _standInUntil)) return;
            var s = _standIn;
            int x0 = Math.Max(0, s.Left - x), x1 = Math.Min(_cw, s.Right - x);
            int y0 = Math.Max(0, s.Top - y), y1 = Math.Min(_ch, s.Bottom - y);
            if (x1 <= x0 || y1 <= y0) return;
            int c = _standInColor;
            for (int row = y0; row < y1; row++) _src.AsSpan(row * _cw + x0, x1 - x0).Fill(c);
        }

        private static bool WallpaperNow => Environment.TickCount64 < Interlocked.Read(ref _wallpaperUntil);

        // ---- Is any window behind a bar? ---------------------------------------------------------------

        private static readonly object BehindLock = new();
        private static readonly EnumProc BehindProc = BehindOne;
        private static RECT _behindRect;
        private static bool _behindFound;
        private static readonly uint OwnPid = (uint)Environment.ProcessId;

        /// <summary>
        /// No other program's window on screen overlaps this rectangle (screen pixels): only the wallpaper is
        /// behind it. Windows are measured by what's really drawn (not their invisible resize borders, which
        /// reach past a maximized window's edge onto the bar).
        /// </summary>
        private static bool NothingBehind(RECT bar)
        {
            lock (BehindLock)
            {
                _behindRect = bar;
                _behindFound = false;
                EnumWindows(BehindProc, IntPtr.Zero);
                return !_behindFound;
            }
        }

        /// <summary>
        /// While streaming (the bars in screen copies): the windows behind a bar, for its glass. The screen can't be
        /// copied then (the bar would copy itself), so each window gives its own picture (PrintWindow), kept and taken
        /// again a few times a second; every round they're drawn where the windows are right now, so a window dragged
        /// over a bar moves smoothly in its glass. Only on the glass clock's thread.
        /// </summary>
        private static class BehindShots
        {
            // Each window keeps only the band of its picture that is behind the bar (with a margin around it, so a
            // window moved a little still has what's needed). The whole window is drawn once into one shared scratch
            // picture and only that band is kept: a whole picture of every window took hundreds of MB.
            private sealed class Shot
            {
                public IntPtr Dc, Bmp, Old;
                public int W, H;          // the band's size
                public int X, Y;          // where the band is in the window (from the window's corner)
                public int WinW, WinH;    // the window's size when it was taken
                public long TakenAt, SeenAt;
                public bool Ok;
            }

            private const int RefreshMs = 200;    // a window's picture is taken again after this long
            private const int Margin = 120;       // kept around the part behind the bar
            private static readonly System.Collections.Generic.Dictionary<(IntPtr, int, int), Shot> Shots = new();
            private static readonly System.Collections.Generic.List<(IntPtr Hwnd, RECT Frame)> Found = new();
            private static readonly EnumProc CollectProc = CollectOne;
            private static RECT _area;
            private static IntPtr _screen, _scratchDc, _scratchBmp, _scratchOld;
            private static int _scratchW, _scratchH;
            private static long _cleanedAt, _scratchUsedAt;
            // When the bars stop asking (streaming over, glass off), what's kept is let go anyway.
            private static readonly System.Threading.Timer Sweep = new(_ =>
            {
                lock (Shots) { long n = Environment.TickCount64; if (n - _cleanedAt > 3000) { _cleanedAt = n; Clean(n); } }
            }, null, 5000, 5000);

            /// <summary>The windows behind (x, y, w, h) drawn over what's in dc. True when any was drawn.</summary>
            public static bool DrawBehind(IntPtr dc, int x, int y, int w, int h)
            {
                lock (Shots)
                {
                    long now = Environment.TickCount64;
                    _area = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
                    Found.Clear();
                    EnumWindows(CollectProc, IntPtr.Zero);   // front to back
                    bool any = false;
                    for (int i = Found.Count - 1; i >= 0; i--)   // back to front, so the front ones end up on top
                    {
                        var (hwnd, f) = Found[i];
                        if (!GetWindowRect(hwnd, out var wr)) continue;
                        // What's needed: the drawn part of the window that's behind the bar, from the window's corner.
                        int nl = Math.Max(f.Left, x) - wr.Left, nt = Math.Max(f.Top, y) - wr.Top;
                        int nr = Math.Min(f.Right, x + w) - wr.Left, nb = Math.Min(f.Bottom, y + h) - wr.Top;
                        if (nr <= nl || nb <= nt) continue;
                        var shot = Take(hwnd, wr, nl, nt, nr, nb, now, (w, h));
                        if (shot == null || !shot.Ok) continue;
                        if (BitBlt(dc, wr.Left + nl - x, wr.Top + nt - y, nr - nl, nb - nt, shot.Dc, nl - shot.X, nt - shot.Y, 0x00CC0020 /* SRCCOPY */)) any = true;
                    }
                    if (now - _cleanedAt > 2000) { _cleanedAt = now; Clean(now); }
                    return any;
                }
            }

            private static bool CollectOne(IntPtr h, IntPtr _)
            {
                if (!IsWindowVisible(h) || IsIconic(h)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == OwnPid || (pid != 0 && pid == GlassCopy.HelperPid)) return true;
                if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
                if (DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT f, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(h, out f)) return true;
                if (f.Right - f.Left <= 0 || f.Bottom - f.Top <= 0) return true;
                if (f.Left >= _area.Right || f.Right <= _area.Left || f.Top >= _area.Bottom || f.Bottom <= _area.Top) return true;
                var sb = new System.Text.StringBuilder(32);
                GetClassName(h, sb, sb.Capacity);
                string cls = sb.ToString();
                if (cls is "Progman" or "WorkerW") return true;   // the desktop: the wallpaper is drawn already
                if (IsOverlay(h, cls)) return true;
                Found.Add((h, f));
                return true;
            }

            /// <summary>
            /// The band of the window's picture holding (nl, nt)-(nr, nb): the kept one, or a new one when it's old,
            /// the window's size changed, or it moved so the kept band no longer holds what's needed.
            /// </summary>
            private static Shot? Take(IntPtr hwnd, RECT wr, int nl, int nt, int nr, int nb, long now, (int W, int H) bar)
            {
                int ww = wr.Right - wr.Left, wh = wr.Bottom - wr.Top;
                if (ww <= 0 || wh <= 0 || ww > 16384 || wh > 16384) return null;
                // Kept per window and bar size (not the bar's place): a bar sliding or dragged keeps its pictures.
                var key = (hwnd, bar.W, bar.H);
                if (!Shots.TryGetValue(key, out var shot)) Shots[key] = shot = new Shot();
                shot.SeenAt = now;
                bool holds = shot.Dc != IntPtr.Zero && shot.WinW == ww && shot.WinH == wh
                             && nl >= shot.X && nt >= shot.Y && nr <= shot.X + shot.W && nb <= shot.Y + shot.H;
                if (holds && now - shot.TakenAt < RefreshMs) return shot;

                if (_screen == IntPtr.Zero) _screen = GetDC(IntPtr.Zero);
                if (!Scratch(ww, wh)) return holds && shot.Ok ? shot : null;
                shot.TakenAt = now;
                // A window that can't draw itself (some games): it stays out, the wallpaper shows instead. A picture
                // that didn't work this time keeps the last good one.
                if (!PrintWindow(hwnd, _scratchDc, 2 /* PW_RENDERFULLCONTENT */)) return holds && shot.Ok ? shot : null;   // (only if it still holds what's needed)
                _scratchUsedAt = now;

                int bl = Math.Max(0, nl - Margin), bt = Math.Max(0, nt - Margin);
                int bw = Math.Min(ww, nr + Margin) - bl, bh = Math.Min(wh, nb + Margin) - bt;
                if (shot.Dc == IntPtr.Zero || shot.W != bw || shot.H != bh)
                {
                    Free(shot);
                    shot.Dc = CreateCompatibleDC(_screen);
                    shot.Bmp = CreateCompatibleBitmap(_screen, bw, bh);
                    if (shot.Dc == IntPtr.Zero || shot.Bmp == IntPtr.Zero) { Free(shot); return null; }
                    shot.Old = SelectObject(shot.Dc, shot.Bmp);
                    shot.W = bw; shot.H = bh;
                }
                BitBlt(shot.Dc, 0, 0, bw, bh, _scratchDc, bl, bt, 0x00CC0020 /* SRCCOPY */);
                shot.X = bl; shot.Y = bt; shot.WinW = ww; shot.WinH = wh;
                shot.Ok = true;
                return shot;
            }

            /// <summary>The shared scratch picture, at least w x h.</summary>
            private static bool Scratch(int w, int h)
            {
                if (_scratchDc != IntPtr.Zero && _scratchW >= w && _scratchH >= h) return true;
                FreeScratch();
                int sw = Math.Max(w, _scratchW), sh = Math.Max(h, _scratchH);
                _scratchDc = CreateCompatibleDC(_screen);
                _scratchBmp = CreateCompatibleBitmap(_screen, sw, sh);
                if (_scratchDc == IntPtr.Zero || _scratchBmp == IntPtr.Zero) { FreeScratch(); return false; }
                _scratchOld = SelectObject(_scratchDc, _scratchBmp);
                _scratchW = sw; _scratchH = sh;
                return true;
            }

            private static void FreeScratch()
            {
                if (_scratchDc != IntPtr.Zero && _scratchOld != IntPtr.Zero) SelectObject(_scratchDc, _scratchOld);
                if (_scratchBmp != IntPtr.Zero) DeleteObject(_scratchBmp);
                if (_scratchDc != IntPtr.Zero) DeleteDC(_scratchDc);
                _scratchDc = _scratchBmp = _scratchOld = IntPtr.Zero;
                _scratchW = _scratchH = 0;
            }

            private static void Clean(long now)
            {
                var gone = new System.Collections.Generic.List<(IntPtr, int, int)>();
                foreach (var (key, shot) in Shots)
                    if (now - shot.SeenAt > 2000) { Free(shot); gone.Add(key); }
                foreach (var key in gone) Shots.Remove(key);
                if (now - _scratchUsedAt > 3000) FreeScratch();   // not streaming any more: the big scratch picture goes
            }

            private static void Free(Shot s)
            {
                if (s.Dc != IntPtr.Zero && s.Old != IntPtr.Zero) SelectObject(s.Dc, s.Old);
                if (s.Bmp != IntPtr.Zero) DeleteObject(s.Bmp);
                if (s.Dc != IntPtr.Zero) DeleteDC(s.Dc);
                s.Dc = s.Bmp = s.Old = IntPtr.Zero;
                s.W = s.H = 0;
            }
        }

        private static bool BehindOne(IntPtr h, IntPtr _)
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == OwnPid) return true;   // Wibn's own (left out of the copies anyway)
            // The glass helper's copying window: it stays over the last bar it copied (unseen). Counted, every
            // copy after the first went through the magnifier, the slow way that holds up the whole screen.
            if (pid != 0 && pid == GlassCopy.HelperPid) return true;
            if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
            if (DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT w, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(h, out w)) return true;
            if (w.Right - w.Left <= 0 || w.Bottom - w.Top <= 0) return true;
            if (w.Left >= _behindRect.Right || w.Right <= _behindRect.Left || w.Top >= _behindRect.Bottom || w.Bottom <= _behindRect.Top) return true;
            var sb = new System.Text.StringBuilder(32);
            GetClassName(h, sb, sb.Capacity);
            string cls = sb.ToString();
            if (cls is "Progman" or "WorkerW") return true;   // the desktop itself
            if (IsOverlay(h, cls)) return true;
            _behindFound = true;
            BehindWhat = cls;
            return false;   // found one: stop looking
        }

        /// <summary>
        /// An overlay over the whole screen that clicks go through and that's (almost always) empty: NVIDIA's
        /// (CEF-OSC-WIDGET), Steam's, Discord's, Xbox Game Bar's… Counted as "a window behind the bar", every
        /// copy went through the magnifier (the slow way that holds up the whole screen), and its own changes
        /// kept asking for new copies all the time.
        /// </summary>
        internal static bool IsOverlay(IntPtr h, string? cls = null)
        {
            long ex = GetWindowLongPtr(h, -20 /* GWL_EXSTYLE */).ToInt64();
            if ((ex & 0x20 /* TRANSPARENT */) != 0 && (ex & 0x80000 /* LAYERED */) != 0) return true;   // click-through
            if (cls == null)
            {
                var sb = new System.Text.StringBuilder(32);
                GetClassName(h, sb, sb.Capacity);
                cls = sb.ToString();
            }
            return cls is "CEF-OSC-WIDGET" or "NVOpenGLPbuffer" or "SteamOverlayWindow";
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);

        /// <summary>The last window found behind a bar (its class, for the log).</summary>
        private static volatile string BehindWhat = "";

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr l);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);

        public void Dispose()   // the thread stops and cleans up by itself
        {
            if (_onMoved != null)
            {
                var moved = _onMoved;
                _onMoved = null;
                try { if (_window.Dispatcher.CheckAccess()) _window.LocationChanged -= moved; else _window.Dispatcher.BeginInvoke(() => _window.LocationChanged -= moved); } catch { }
            }
            lock (DesktopGlasses) DesktopGlasses.Remove(this);
            lock (LiveGlasses) LiveGlasses.Remove(this);
            bool lastOnWindow = true;
            if (_live && _thread != null) lock (LiveWindows)
            {
                if (LiveWindows.TryGetValue(_hwnd, out int n) && n > 1) { LiveWindows[_hwnd] = n - 1; lastOnWindow = false; }
                else LiveWindows.Remove(_hwnd);
                LiveChanged();
            }
            _running = false;
            _wake.Set();
            // Live mode kept the window out of screen copies: back in them (a new glass sets it again), unless
            // another live glass is still on it.
            if (_live && lastOnWindow)
            {
                try
                {
                    if (_window.Dispatcher.CheckAccess()) SetWindowDisplayAffinity(_hwnd, 0 /* WDA_NONE */);
                    else _window.Dispatcher.BeginInvoke(new Action(() => SetWindowDisplayAffinity(_hwnd, 0)));
                }
                catch { }
            }
        }

        private void Loop(int ox, int oy, int w, int h, double scale)
        {
            try
            {
                Prepare(w, h, scale);
                _x = ox - _margin; _y = oy - _margin;
                int frost = (int)Math.Round(Frost * scale);
                while (_running)
                {
                    // Streaming: the shared clock does the copying and drawing for every bar together (see Clock).
                    if (_live && LiveStream)
                    {
                        _frost = frost;
                        _ready = true;
                        _wake.WaitOne(1000);
                        continue;
                    }
                    if (_live)
                    {
                        if (!_magTried)
                        {
                            _magTried = true;
                            _useMag = !GlassCopy.Failed;
                            if (!_useMag) ExcludeFromCapture();
                        }
                        // A picture of what's behind: all the time (LiveStream), or only when asked: at the start, and a
                        // moment after a window moved, was resized, minimized, restored, shown, hidden or came to the
                        // front (see GlassCalm).
                        double took = 0;
                        if (Interlocked.Exchange(ref _wanted, 0) == 1)
                        {
                            _retry = false;
                            long t0 = Stopwatch.GetTimestamp();
                            bool drew = IsWindowVisible(_hwnd) && CopyLive() && _running;
                            took = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                            Stats.Add(took, drew);
                            if (drew)
                            {
                                DrawFrame(frost);
                                lock (_front) Array.Copy(_out, _front, _out.Length);
                                if (Interlocked.Exchange(ref _pending, 1) == 0)
                                    _window.Dispatcher.BeginInvoke(new Action(Present));
                            }
                        }
                        // A copy that didn't work (or came back black): once more soon. Otherwise a fresh one
                        // every minute anyway (something changing behind without a window moving: a video, the wallpaper).
                        int wait = _retry ? 400 : 60_000;
                        // Streaming: the next one a frame later, or longer when a copy is slow (it never takes more
                        // than about half the time, so a slow screen copy can't hold everything up).
                        if (LiveStream) wait = Math.Max(_retry ? 400 : LiveFrameMs, (int)(took * 2));
                        if (!_wake.WaitOne(wait)) Interlocked.Exchange(ref _wanted, 1);
                        continue;
                    }
                    if (Interlocked.Exchange(ref _wanted, 0) == 1)
                    {
                        // The copy itself on the window's thread (it hides the window from it for a moment).
                        bool copied = false;
                        if (_desktop) copied = CopyDesktop();
                        else
                        {
                            var op = _window.Dispatcher.InvokeAsync(() => copied = _running && CopyBehind());
                            op.Wait(TimeSpan.FromSeconds(2));
                        }
                        if (copied && _running)
                        {
                            DrawFrame(frost);
                            lock (_front) Array.Copy(_out, _front, _out.Length);
                            // One waiting at a time; it shows the latest picture anyway.
                            if (Interlocked.Exchange(ref _pending, 1) == 0)
                                _window.Dispatcher.BeginInvoke(new Action(Present));
                        }
                    }
                    _wake.WaitOne();
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("liquid glass", ex);   // the plain dark background stays
            }
            finally
            {
                lock (_stepLock)   // not while the clock is drawing it
                {
                    _running = false;
                    _ready = false;
                    FreeGdi();
                }
                // Its big buffers go (the card keeps only its last picture), and the memory goes back
                // to Windows a moment later, once nothing else is restarting.
                _src = _tmp = _out = _mapR = _mapG = _mapB = _last = Array.Empty<int>();
                _rim = Array.Empty<byte>();
                MemoryTrim.Soon();
            }
        }

        // ---- One clock for every live glass -----------------------------------------------------------------
        //
        // Every bar copies what's behind it in the same round and they're all shown in the same screen frame, so
        // they change together (each with its own thread, one changed before the other). Only each bar's own strip
        // is copied, not the whole screen: that would be about ten times the work.

        private static Thread? _clock;
        private readonly object _stepLock = new();
        private volatile bool _ready;                  // streaming: prepared, the clock may draw it
        private int _frost;

        private static void StartClock()
        {
            ScreenshotKeys.Start();   // (on the window thread, like Start)
            lock (LiveGlasses)
            {
                if (_clock != null) return;
                _clock = new Thread(Clock) { IsBackground = true, Name = "Wibn glass clock", Priority = ThreadPriority.BelowNormal };
                _clock.Start();
            }
        }

        private static void Clock()
        {
            var drawn = new System.Collections.Generic.List<LiquidGlass>();
            long lastChange = 0;
            while (true)
            {
                LiquidGlass[] all;
                lock (LiveGlasses) all = System.Linq.Enumerable.ToArray(LiveGlasses);
                long t0 = Stopwatch.GetTimestamp();
                drawn.Clear();
                // First every bar's copy, one right after the other (the same moment on the screen), then the
                // drawing: drawing between the copies let a change land on some bars a round before the others.
                var screen = new System.Collections.Generic.List<LiquidGlass>();
                foreach (var g in all)
                {
                    try
                    {
                        if (g.Capture()) drawn.Add(g);
                        else if (g._needScreen) screen.Add(g);
                    }
                    catch (Exception ex) { CrashLog.Write("liquid glass", ex); }
                }
                try { CopyRows(screen, drawn); }
                catch (Exception ex) { CrashLog.Write("liquid glass", ex); }
                for (int i = drawn.Count - 1; i >= 0; i--)
                {
                    try { if (!drawn[i].Draw()) drawn.RemoveAt(i); }
                    catch (Exception ex) { CrashLog.Write("liquid glass", ex); drawn.RemoveAt(i); }
                }
                // All of them in one go on the window thread: the same screen frame.
                foreach (var group in System.Linq.Enumerable.GroupBy(drawn, g => g._window.Dispatcher))
                {
                    var list = System.Linq.Enumerable.ToArray(group);
                    try { group.Key.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { foreach (var g in list) g.Present(); })); } catch { }
                }
                double took = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                long now = Environment.TickCount64;
                if (drawn.Count > 0) lastChange = now;
                // Something just changed behind (a window maximizing, moving…): for half a second, a round every
                // screen frame, so the glass keeps up with it. Otherwise one a frame later, or longer when copying
                // was slow (never more than about half the time, so slow screen copies can't hold everything up).
                // Nothing live: a slow look now and then.
                int wait = all.Length == 0 ? 1000
                         : now - lastChange < 500 ? Math.Max(16, (int)took)
                         : Math.Max(LiveFrameMs, (int)(took * 2));
                Thread.Sleep(wait);
            }
        }

        // The clock's one copy of a row of the screen (reused while big enough).
        private static IntPtr _rowScreenDc, _rowDc, _rowDib, _rowBits, _rowOld;
        private static int _rowW, _rowH;

        /// <summary>
        /// The bars that need the screen, in rows (bars side by side, e.g. all along the top): one copy of the
        /// screen for each row, each bar takes its part of it. So the bars in a row show the very same moment.
        /// </summary>
        private static void CopyRows(System.Collections.Generic.List<LiquidGlass> screen, System.Collections.Generic.List<LiquidGlass> drawn)
        {
            if (screen.Count == 0) return;
            // Rows: bars at the same height (top and bottom within a few pixels), e.g. all the bars along the top.
            // A bar of another height (a side bar) gets its own copy, not one as big as the screen.
            var rows = new System.Collections.Generic.List<System.Collections.Generic.List<LiquidGlass>>();
            foreach (var g in screen)
            {
                System.Collections.Generic.List<LiquidGlass>? row = null;
                foreach (var r in rows)
                    if (Math.Abs(r[0]._cy - g._cy) <= 8 && Math.Abs(r[0]._ch - g._ch) <= 8) { row = r; break; }
                if (row == null) rows.Add(row = new());
                row.Add(g);
            }
            using var _ = new ScreenCapture.DpiScope();
            if (_rowScreenDc == IntPtr.Zero) { _rowScreenDc = GetDC(IntPtr.Zero); _rowDc = CreateCompatibleDC(_rowScreenDc); }
            foreach (var row in rows)
            {
                var u = new RECT { Left = int.MaxValue, Top = int.MaxValue, Right = int.MinValue, Bottom = int.MinValue };
                foreach (var g in row)
                {
                    u.Left = Math.Min(u.Left, g._cx); u.Top = Math.Min(u.Top, g._cy);
                    u.Right = Math.Max(u.Right, g._cx + g._cw); u.Bottom = Math.Max(u.Bottom, g._cy + g._ch);
                }
                int w = u.Right - u.Left, h = u.Bottom - u.Top;
                if (w <= 0 || h <= 0) continue;
                if (w > _rowW || h > _rowH)
                {
                    if (_rowDib != IntPtr.Zero) { SelectObject(_rowDc, _rowOld); DeleteObject(_rowDib); }
                    _rowW = Math.Max(w, _rowW); _rowH = Math.Max(h, _rowH);
                    var bmi = new BITMAPINFO { biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = _rowW, biHeight = -_rowH, biPlanes = 1, biBitCount = 32 };
                    _rowDib = CreateDIBSection(_rowDc, ref bmi, 0, out _rowBits, IntPtr.Zero, 0);
                    _rowOld = SelectObject(_rowDc, _rowDib);
                }
                // Past the screens it stays black (each bar carries its screen's edge out over it).
                PatBlt(_rowDc, 0, 0, w, h, 0x00000042 /* BLACKNESS */);
                if (!BitBlt(_rowDc, 0, 0, w, h, _rowScreenDc, u.Left, u.Top, 0x00CC0020 /* SRCCOPY */)) continue;
                GdiFlush();
                foreach (var g in row)
                    if (g.TakeFrom(_rowBits, _rowW, u)) drawn.Add(g);
            }
        }

        [DllImport("gdi32.dll")] private static extern bool GdiFlush();

        /// <summary>
        /// The copy's pixels into _src. GDI holds drawing back in a queue (per thread), so the pixels were read
        /// now and then before the copy was really in them: half a picture, the black it was cleared with, or the
        /// last round's — the glass blinked. Flushed first, it's always the finished copy.
        /// </summary>
        private void ReadBits()
        {
            GdiFlush();
            Marshal.Copy(_bits, _src, 0, _src.Length);
        }

        /// <summary>On the clock's thread: the glass from the last copy, into _front.</summary>
        private bool Draw()
        {
            lock (_stepLock)
            {
                if (!_running || !_ready) return false;
                DrawFrame(_frost);
                lock (_front) Array.Copy(_out, _front, _out.Length);
                return true;
            }
        }

        /// <summary>On the clock's thread: a new copy of what's behind. True when it changed.</summary>
        private bool Capture()
        {
            lock (_stepLock)
            {
                if (!_running || !_ready) return false;
                // A screenshot is being taken: the bar is in screen copies for it, so no copy now (it would copy
                // itself); the glass keeps its last picture.
                if (Environment.TickCount64 < Interlocked.Read(ref _shotUntil) + ShotGraceMs) return false;
                // Out of screen copies (or, while streaming, in them)? Checked every second, and at once when
                // streaming starts or stops. Not as it should be: no copy this round (it could copy itself).
                long now = Environment.TickCount64;
                bool safe = StreamSafe;
                if (_magTried && (now - _affinityCheckedAt > 1000 || safe != _wasSafe))
                {
                    _affinityCheckedAt = now;
                    _wasSafe = safe;
                    if (GetWindowDisplayAffinity(_hwnd, out uint aff) && aff != (safe ? 0u : 0x11u)) { ApplyAffinity(); return false; }
                }
                if (!_magTried)
                {
                    // Streaming copies the screen straight (the bar left out of every screen copy, so it isn't in
                    // its own picture): the magnifier was far too slow for it (up to half a second a copy, the
                    // glass froze). The price: the bars don't show in screen shares and screenshots.
                    _magTried = true;
                    _useMag = false;
                    _wasSafe = safe;
                    ApplyAffinity();
                }
                _retry = false;
                long t0 = Stopwatch.GetTimestamp();
                bool drew = IsWindowVisible(_hwnd) && CopyLive(planOnly: true) && _running;
                if (!_needScreen) Stats.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, drew, _who);
                return drew;
            }
        }

        /// <summary>On the window's thread: show the latest picture.</summary>
        private void Present()
        {
            try
            {
                if (_bitmap == null) return;
                if (_brush == null)
                {
                    _brush = new ImageBrush(_bitmap) { Stretch = Stretch.Fill };
                    // Right-to-left (Wibn in Arabic): WPF mirrors what the element draws, the glass too, so what's
                    // behind showed the wrong way round. Mirrored once more, it's the right way.
                    if (_target.FlowDirection == FlowDirection.RightToLeft)
                        _brush.RelativeTransform = new ScaleTransform(-1, 1, 0.5, 0.5);
                    if (_target is System.Windows.Controls.Border border) border.Background = _brush;
                    else if (_target is System.Windows.Shapes.Shape shape) shape.Fill = _brush;   // e.g. a round menu's disc
                }
                lock (_front) _bitmap.WritePixels(new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight), _front, _bitmap.PixelWidth * 4, 0);
                if (!_reportedOnce || _lightReported != _lightBehind)
                {
                    _reportedOnce = true;
                    _lightReported = _lightBehind;
                    LightBehindChanged?.Invoke(_lightBehind);
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("liquid glass", ex);
                _running = false;
            }
            finally
            {
                Interlocked.Exchange(ref _pending, 0);
            }
        }

        /// <summary>
        /// On the window's thread: copies what's behind into _src. The window is left out
        /// of screen copies only until the next screen refresh is done (DwmFlush), then
        /// it's shown in them again, so a screen share only misses it for a frame or two.
        /// </summary>
        private bool CopyBehind()
        {
            if (!SetWindowDisplayAffinity(_hwnd, 0x11 /* WDA_EXCLUDEFROMCAPTURE */)) return false;
            try
            {
                DwmFlush();
                if (!BitBlt(_memDc, 0, 0, _cw, _ch, _screenDc, _x, _y, 0x00CC0020 /* SRCCOPY */)) return false;
            }
            finally { SetWindowDisplayAffinity(_hwnd, 0 /* WDA_NONE */); }
            ReadBits();
            FillPastEdges(_x, _y, null);
            return true;
        }

        /// <summary>
        /// Live mode, on the glass thread: the screen behind the element right now (the window is kept
        /// out of screen copies, so it isn't in it). Follows the window if it moved. False when nothing
        /// changed since the last copy (nothing to draw).
        /// </summary>
        private bool CopyLive(bool planOnly = false)
        {
            _needScreen = false;
            using var _ = new ScreenCapture.DpiScope();
            if (!GetWindowRect(_hwnd, out var r)) return false;
            int x = r.Left + (_ox - _wx0) - _margin, y = r.Top + (_oy - _wy0) - _margin;
            // A window is shrinking or growing over it (no window colors then), or no window is behind it at all
            // (most of the time: maximized windows stop at the bars): the wallpaper straight from the desktop's
            // picture. The same look, without asking Windows to copy the screen (each copy is work for all of it,
            // and with several bars at 30 a second the whole PC stuttered).
            // While streaming (see StreamSafe) the bars are in screen copies, so it never copies the screen: always
            // the desktop's own picture (the wallpaper; windows behind the bar don't show in the glass then).
            bool streamSafe = LiveStream && StreamSafe;
            if (WallpaperNow || streamSafe || (!DesktopPicture.Animated && NothingBehind(r)))
            {
                _wx0 = r.Left; _wy0 = r.Top; _ox = x + _margin; _oy = y + _margin;
                if (!CopyDesktop()) return false;
                // While streaming the screen can't be copied (the bar is in screen copies), so the windows behind
                // it are drawn over the wallpaper one by one, each from its own picture (see BehindShots).
                if (streamSafe && !WallpaperNow && BehindShots.DrawBehind(_memDc, x, y, _cw, _ch))
                {
                    ReadBits();
                    FillPastEdges(x, y, null);
                }
                ApplyStandIn(x, y);
                _blackSince = 0;
                if (_last.Length == _src.Length && _src.AsSpan().SequenceEqual(_last)) return false;
                if (_last.Length != _src.Length) _last = new int[_src.Length];
                Array.Copy(_src, _last, _src.Length);
                return true;
            }
            Interlocked.Increment(ref Stats.Screen);
            // The clock copies the bars in the same row together (see Clock): just where.
            if (planOnly) { _cx = x; _cy = y; _needScreen = true; return false; }
            if (_useMag)
            {
                IntPtr[] skip; int version;
                lock (LiveWindows) { skip = _liveList; version = _liveVersion; }
                bool? copied = GlassCopy.Copy(x, y, _cw, _ch, _src, skip, version);
                if (copied != true)
                {
                    // The magnifier can't work here (or never answers): back to leaving the window out of screen copies.
                    if (copied == false && ++_magMisses < 30) { _retry = true; return false; }
                    _useMag = false;
                    ExcludeFromCapture();
                    return false;
                }
                _magMisses = 0;
            }
            else
            {
                if (!BitBlt(_memDc, 0, 0, _cw, _ch, _screenDc, x, y, 0x00CC0020 /* SRCCOPY */)) return false;
                ReadBits();
            }
            return AfterScreenCopy(x, y);
        }

        /// <summary>The screen copy at (x, y) is in _src: its edges, a black copy skipped, and whether it changed.</summary>
        private bool AfterScreenCopy(int x, int y)
        {
            FillPastEdges(x, y, null);
            ApplyStandIn(x, y);
            // A copy that's all black (the screen copy now and then gives an empty frame, e.g. over a video or a game)
            // is skipped, so the glass keeps its last picture instead of blinking black. If it really stays black
            // behind for over a second, it's taken.
            if (AllBlack(_src))
            {
                long now = Environment.TickCount64;
                if (_blackSince == 0)
                {
                    _blackSince = now;
                    // A note in the log (at most once a minute), to know how often it happens.
                    if (now - Interlocked.Read(ref _blackNoted) > 60_000) { Interlocked.Exchange(ref _blackNoted, now); CrashLog.Note("glass: black screen copy skipped"); }
                }
                // Over Wibn's video wallpaper the screen copy can stay black for a while (the video isn't in it):
                // the video's own frame is shown instead.
                if (Wallpapers.VideoPlaying)
                {
                    PatBlt(_memDc, 0, 0, _cw, _ch, 0x00000042 /* BLACKNESS */);
                    if (VideoWallpaper.DrawInto(_memDc, x, y, _cw, _ch))
                    {
                        ReadBits();
                        FillPastEdges(x, y, null);
                    }
                    else if (_last.Length > 0) { _retry = true; return false; }
                }
                // Otherwise the last picture stays: black is taken only if it really stays black for 5 seconds
                // (a flash of black on the taskbar was worse than a picture a moment old).
                else if (now - _blackSince < 5000 && _last.Length > 0) { _retry = true; return false; }
            }
            else _blackSince = 0;
            if (_last.Length == _src.Length && _src.AsSpan().SequenceEqual(_last)) return false;
            if (_last.Length != _src.Length) _last = new int[_src.Length];
            Array.Copy(_src, _last, _src.Length);   // before the frost blurs _src
            return true;
        }

        private long _affinityCheckedAt;

        // ---- Screenshots: the bars in them -------------------------------------------------------------------

        private static long _shotUntil;
        private const int ShotMs = 1500, ShotGraceMs = 200;
        private static readonly Timer _shotOver = new(_ => ShotOver(), null, Timeout.Infinite, Timeout.Infinite);

        /// <summary>
        /// A screenshot is starting (PrintScreen, Win + Shift + S, Wibn's own; on the window thread). The live-glass
        /// bars are kept out of screen copies (or their glass would copy itself): for a moment they're back in them,
        /// so they're in the screenshot, and their glass keeps its last picture meanwhile. Returns once the screen
        /// shows them (one screen frame).
        /// </summary>
        public static void ShowInScreenshot()
        {
            if (!LiveStream || StreamSafe) return;   // streaming: they're in screen copies already
            IntPtr[] list;
            lock (LiveWindows) list = _liveList;
            if (list.Length == 0) return;
            Interlocked.Exchange(ref _shotUntil, Environment.TickCount64 + ShotMs);
            foreach (var h in list) SetWindowDisplayAffinity(h, 0 /* WDA_NONE */);
            DwmFlush();
            try { _shotOver.Change(ShotMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }

        /// <summary>The screenshot is taken: the bars out of screen copies again (then the glass goes on).</summary>
        private static void ShotOver()
        {
            try
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (Environment.TickCount64 < Interlocked.Read(ref _shotUntil)) return;   // another one started
                    IntPtr[] list;
                    lock (LiveWindows) list = _liveList;
                    uint want = StreamSafe ? 0u : 0x11u /* WDA_EXCLUDEFROMCAPTURE */;
                    foreach (var h in list) SetWindowDisplayAffinity(h, want);
                }));
            }
            catch { }
        }
        [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

        private int _cx, _cy;          // streaming: where this bar's screen copy goes (screen pixels)
        private bool _needScreen;      // streaming: this round it needs a copy of the screen (not the wallpaper)

        /// <summary>On the clock's thread: its part of a copy of the screen (u, w pixels wide at bits) into _src.</summary>
        private bool TakeFrom(IntPtr bits, int uw, RECT u)
        {
            lock (_stepLock)
            {
                if (!_running || !_ready || !_needScreen) return false;
                _needScreen = false;
                long t0 = Stopwatch.GetTimestamp();
                for (int row = 0; row < _ch; row++)
                {
                    long at = ((long)(_cy - u.Top + row) * uw + (_cx - u.Left)) * 4;
                    Marshal.Copy(bits + (nint)at, _src, row * _cw, _cw);
                }
                bool drew = AfterScreenCopy(_cx, _cy) && _running;
                Stats.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, drew, _who);
                return drew;
            }
        }


        /// <summary>Every pixel (one in 5 looked at) exactly black?</summary>
        private static bool AllBlack(int[] px)
        {
            for (int i = 0; i < px.Length; i += 5)
                if ((px[i] & 0xFFFFFF) != 0) return false;
            return px.Length > 0;
        }

        /// <summary>
        /// On the glass thread: the desktop (wallpaper) behind the element, drawn by the
        /// desktop window itself, so it works even with windows over the element.
        /// </summary>
        private bool CopyDesktop()
        {
            using var _ = new ScreenCapture.DpiScope();
            bool fresh = Interlocked.Exchange(ref _recapture, 0) == 1;
            int x = _ox - _margin, y = _oy - _margin;

            // Wibn's own video wallpaper: its frame right now (straight from the video, not a picture of the desktop).
            if (Wallpapers.VideoPlaying)
            {
                PatBlt(_memDc, 0, 0, _cw, _ch, 0x00000042 /* BLACKNESS */);
                if (VideoWallpaper.DrawInto(_memDc, x, y, _cw, _ch))
                {
                    ReadBits();
                    FillPastEdges(x, y, null);
                    _videoShown = true;
                    _videoAt = Environment.TickCount64;
                    return true;
                }
                // No frame for a moment (the video starting over, a hiccup): the last picture stays. Falling back to
                // the still picture of the desktop for one round and back to the video made the glass blink. But only
                // for a moment: no frame for longer (the glass moved off the video's screen) = the still picture.
                if (_videoShown && Environment.TickCount64 - _videoAt < 1500) return false;
                _videoShown = false;
            }
            else _videoShown = false;

            // A still wallpaper: one picture of the desktop for every glass (widgets, taskbars, menus).
            if (!DesktopPicture.Animated)
            {
                FreeDesktop();   // its own picture (from an animated wallpaper before) isn't needed now
                if (!_sharedRef) { DesktopPicture.AddRef(); _sharedRef = true; }
                PatBlt(_memDc, 0, 0, _cw, _ch, 0x00000042 /* BLACKNESS */);
                if (!DesktopPicture.CopyTo(_memDc, x, y, _cw, _ch, fresh, out var rect)) return false;
                ReadBits();
                FillPastEdges(x, y, rect);
                return true;
            }

            // An animated wallpaper (Wallpaper Engine, Lively…): each glass takes its own picture, when it
            // needs one, so it matches the wallpaper at that moment.
            if (_sharedRef) { DesktopPicture.Release(); _sharedRef = false; }
            if (_deskDc == IntPtr.Zero || fresh)
            {
                FreeDesktop();
                IntPtr desk = FindWindow("Progman", null);
                if (desk == IntPtr.Zero || !GetWindowRect(desk, out var r)) return false;
                int dw = r.Right - r.Left, dh = r.Bottom - r.Top;
                if (dw <= 0 || dh <= 0) return false;
                _deskDc = CreateCompatibleDC(_screenDc);
                _deskBmp = CreateCompatibleBitmap(_screenDc, dw, dh);
                if (_deskDc == IntPtr.Zero || _deskBmp == IntPtr.Zero) { FreeDesktop(); return false; }
                _deskOld = SelectObject(_deskDc, _deskBmp);
                _deskRect = r;
                if (!PrintWindow(desk, _deskDc, 2 /* PW_RENDERFULLCONTENT */)) { FreeDesktop(); return false; }
            }
            // Outside the desktop (the edge of the screen): black, the bend barely reaches there.
            PatBlt(_memDc, 0, 0, _cw, _ch, 0x00000042 /* BLACKNESS */);
            if (!BitBlt(_memDc, 0, 0, _cw, _ch, _deskDc, x - _deskRect.Left, y - _deskRect.Top, 0x00CC0020 /* SRCCOPY */)) return false;
            ReadBits();
            FillPastEdges(x, y, _deskRect);
            return true;
        }

        /// <summary>
        /// Near the edge of the screen, part of the copy lies past it and comes back black, and the
        /// bend at the glass's edges pulls that black in. Instead, everything past the screen (the
        /// one the glass is on) takes the color of the screen's last pixel there, so the edge just
        /// carries on.
        /// </summary>
        private void FillPastEdges(int copyX, int copyY, RECT? within)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            IntPtr mon = MonitorFromPoint(new POINT { X = copyX + _cw / 2, Y = copyY + _ch / 2 }, 2 /* NEAREST */);
            if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return;
            var m = mi.rcMonitor;
            if (within is { } w)
            {
                m.Left = Math.Max(m.Left, w.Left); m.Top = Math.Max(m.Top, w.Top);
                m.Right = Math.Min(m.Right, w.Right); m.Bottom = Math.Min(m.Bottom, w.Bottom);
            }
            // The part of the copy that's on the screen.
            int x0 = Math.Clamp(m.Left - copyX, 0, _cw), y0 = Math.Clamp(m.Top - copyY, 0, _ch);
            int x1 = Math.Clamp(m.Right - copyX, 0, _cw), y1 = Math.Clamp(m.Bottom - copyY, 0, _ch);
            if (x1 <= x0 || y1 <= y0) return;                                   // nothing on screen: leave it
            if (x0 == 0 && y0 == 0 && x1 == _cw && y1 == _ch) return;          // all on screen: the usual case
            int cw = _cw;
            // Across: rows on the screen carry their first / last pixel outward.
            for (int y = y0; y < y1; y++)
            {
                int row = y * cw;
                int left = _src[row + x0], right = _src[row + x1 - 1];
                for (int x = 0; x < x0; x++) _src[row + x] = left;
                for (int x = x1; x < cw; x++) _src[row + x] = right;
            }
            // Up and down: rows past the screen copy the nearest row on it.
            for (int y = 0; y < y0; y++) Array.Copy(_src, y0 * cw, _src, y * cw, cw);
            for (int y = y1; y < _ch; y++) Array.Copy(_src, (y1 - 1) * cw, _src, y * cw, cw);
        }

        private void FreeDesktop()
        {
            if (_deskDc != IntPtr.Zero)
            {
                if (_deskOld != IntPtr.Zero) SelectObject(_deskDc, _deskOld);
                DeleteDC(_deskDc);
            }
            if (_deskBmp != IntPtr.Zero) DeleteObject(_deskBmp);
            _deskDc = _deskBmp = _deskOld = IntPtr.Zero;
        }

        /// <summary>The glass picture into _out (on the glass thread), from _src.</summary>
        private void DrawFrame(int frost)
        {
            // Frost.
            if (frost > 0) BoxBlur(_src, _tmp, _cw, _ch, frost);

            // How light is it behind (after the tint)? Every 7th pixel is plenty.
            long sum = 0; int count = 0;
            for (int i = 0; i < _src.Length; i += 7)
            {
                int c = _src[i];
                sum += ((c >> 16) & 0xFF) * 3 + ((c >> 8) & 0xFF) * 6 + (c & 0xFF);
                count++;
            }
            double light = count > 0 ? (sum / (double)count / 10 / 255) * (1 - TintAlpha) + TintAlpha * TintLight : 0;
            _lightBehind = _lightBehind ? light > 0.50 : light > 0.60;   // a gap so it doesn't flicker

            // 3. Bend, split colors, tint, rim.
            int tintR = (int)(TintR * TintAlpha), tintG = (int)(TintG * TintAlpha), tintB = (int)(TintB * TintAlpha);
            int keep = (int)((1 - TintAlpha) * 256);
            for (int i = 0; i < _out.Length; i++)
            {
                int r = (_src[_mapR[i]] >> 16) & 0xFF;
                int g = (_src[_mapG[i]] >> 8) & 0xFF;
                int b = _src[_mapB[i]] & 0xFF;
                r = ((r * keep) >> 8) + tintR;
                g = ((g * keep) >> 8) + tintG;
                b = ((b * keep) >> 8) + tintB;
                int shine = _rim[i];
                if (shine > 0)
                {
                    r += ((255 - r) * shine) >> 8;
                    g += ((255 - g) * shine) >> 8;
                    b += ((255 - b) * shine) >> 8;
                }
                _out[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
            }
        }

        /// <summary>Works out, once per size, where every glass pixel takes its color from.</summary>
        private void Prepare(int w, int h, double scale)
        {
            _w = w; _h = h;
            double bezel = Bezel * scale, radius = Math.Min(_radius * scale, Math.Min(w, h) / 2.0);
            double maxShift = bezel * Bend * (1 + Dispersion);
            _margin = (int)Math.Ceiling(maxShift) + 2;
            _cw = w + 2 * _margin; _ch = h + 2 * _margin;

            _src = new int[_cw * _ch];
            _tmp = new int[_cw * _ch];
            _out = new int[w * h];
            _mapR = new int[w * h]; _mapG = new int[w * h]; _mapB = new int[w * h];
            _rim = new byte[w * h];

            double hx = w / 2.0, hy = h / 2.0;
            // Signed distance to the rounded rectangle (negative inside).
            double Sdf(double px, double py)
            {
                double qx = Math.Abs(px - hx) - (hx - radius), qy = Math.Abs(py - hy) - (hy - radius);
                double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
                return Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - radius;
            }
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    double d = -Sdf(px, py);                     // distance in from the edge
                    double nx = Sdf(px + 1, py) - Sdf(px - 1, py);
                    double ny = Sdf(px, py + 1) - Sdf(px, py - 1);
                    double len = Math.Sqrt(nx * nx + ny * ny);
                    if (len > 1e-6) { nx /= len; ny /= len; } else { nx = ny = 0; }

                    // Near the edge the glass shows what's just outside it, squeezed in
                    // (like the thick rim of a lens); the middle stays sharp.
                    double t = Math.Clamp(d / bezel, 0, 1);
                    double amount = bezel * Bend * (1 - t) * (1 - t);
                    int i = y * w + x;
                    _mapR[i] = Sample(px, py, nx, ny, amount * (1 + Dispersion));
                    _mapG[i] = Sample(px, py, nx, ny, amount);
                    _mapB[i] = Sample(px, py, nx, ny, amount * (1 - Dispersion));

                    // Rim: a thin bright line, stronger where the edge faces up (light from above).
                    double edge = Math.Clamp(1 - d / (1.6 * scale), 0, 1);
                    double facing = 0.35 + 0.65 * Math.Max(0, -ny);
                    double glow = Math.Clamp(1 - d / (bezel * 0.9), 0, 1);   // soft inner light
                    double shine = RimShine * (edge * facing + 0.12 * glow * glow * Math.Max(0, -ny));
                    _rim[i] = (byte)Math.Clamp(shine * 255, 0, 255);
                }
            }

            int Sample(double px, double py, double nx, double ny, double amount)
            {
                int sx = (int)Math.Round(px + nx * amount) + _margin;
                int sy = (int)Math.Round(py + ny * amount) + _margin;
                sx = Math.Clamp(sx, 0, _cw - 1); sy = Math.Clamp(sy, 0, _ch - 1);
                return sy * _cw + sx;
            }

            // Screen copy target.
            _screenDc = GetDC(IntPtr.Zero);
            _memDc = CreateCompatibleDC(_screenDc);
            var bmi = new BITMAPINFO
            {
                biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = _cw, biHeight = -_ch,  // top-down
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            _dib = CreateDIBSection(_memDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            _oldObj = SelectObject(_memDc, _dib);

        }

        /// <summary>Two passes of a sliding box blur (rows, then columns).</summary>
        internal static void BoxBlur(int[] px, int[] tmp, int w, int h, int r)
        {
            if (r < 1) return;
            int div = 2 * r + 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w, sr = 0, sg = 0, sb = 0;
                for (int k = -r; k <= r; k++)
                {
                    int c = px[row + Math.Clamp(k, 0, w - 1)];
                    sr += (c >> 16) & 0xFF; sg += (c >> 8) & 0xFF; sb += c & 0xFF;
                }
                for (int x = 0; x < w; x++)
                {
                    tmp[row + x] = ((sr / div) << 16) | ((sg / div) << 8) | (sb / div);
                    int a = px[row + Math.Clamp(x - r, 0, w - 1)], b = px[row + Math.Clamp(x + r + 1, 0, w - 1)];
                    sr += ((b >> 16) & 0xFF) - ((a >> 16) & 0xFF);
                    sg += ((b >> 8) & 0xFF) - ((a >> 8) & 0xFF);
                    sb += (b & 0xFF) - (a & 0xFF);
                }
            }
            for (int x = 0; x < w; x++)
            {
                int sr = 0, sg = 0, sb = 0;
                for (int k = -r; k <= r; k++)
                {
                    int c = tmp[Math.Clamp(k, 0, h - 1) * w + x];
                    sr += (c >> 16) & 0xFF; sg += (c >> 8) & 0xFF; sb += c & 0xFF;
                }
                for (int y = 0; y < h; y++)
                {
                    px[y * w + x] = ((sr / div) << 16) | ((sg / div) << 8) | (sb / div);
                    int a = tmp[Math.Clamp(y - r, 0, h - 1) * w + x], b = tmp[Math.Clamp(y + r + 1, 0, h - 1) * w + x];
                    sr += ((b >> 16) & 0xFF) - ((a >> 16) & 0xFF);
                    sg += ((b >> 8) & 0xFF) - ((a >> 8) & 0xFF);
                    sb += (b & 0xFF) - (a & 0xFF);
                }
            }
        }

        private void FreeGdi()
        {
            FreeDesktop();
            if (_sharedRef) { DesktopPicture.Release(); _sharedRef = false; }
            if (_memDc != IntPtr.Zero)
            {
                if (_oldObj != IntPtr.Zero) SelectObject(_memDc, _oldObj);
                DeleteDC(_memDc);
            }
            if (_dib != IntPtr.Zero) DeleteObject(_dib);
            if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
            _memDc = _dib = _screenDc = _oldObj = _bits = IntPtr.Zero;
        }

        private static void LiveChanged()   // under the lock
        {
            _liveVersion++;
            _liveList = System.Linq.Enumerable.ToArray(LiveWindows.Keys);
        }

        /// <summary>Streaming: out of screen copies, or in them while streaming (StreamSafe).</summary>
        private void ApplyAffinity()
        {
            uint want = StreamSafe ? 0u : 0x11u;
            try { _window.Dispatcher.BeginInvoke(new Action(() => { if (_running) SetWindowDisplayAffinity(_hwnd, want); })); }
            catch { }
        }

        private bool _wasSafe;

        /// <summary>
        /// The bars show in streams and recordings (Discord, OBS…): turned on by hand (CustomTaskbar.ShowInStreams),
        /// or a streaming app is running. Their glass then shows the wallpaper behind them, not the screen.
        /// </summary>
        internal static bool StreamSafe => CustomTaskbar.ShowInStreams || StreamApps.Running;

        /// <summary>Is a streaming / recording app running? Looked at every 3 seconds (once the live glass is on).</summary>
        private static class StreamApps
        {
            private static readonly string[] Names =
            {
                "obs64", "obs32", "obs", "streamlabs obs", "streamlabs desktop", "xsplit.core", "xsplit.broadcaster",
                "twitchstudio", "twitch studio", "prismlivestudio", "meld studio",
            };
            private static Timer? _timer;
            private static volatile bool _running;

            public static bool Running { get { Start(); return _running; } }

            private static void Start()
            {
                if (_timer != null) return;
                lock (Names)
                {
                    if (_timer != null) return;
                    _timer = new Timer(_ => Look(), null, 0, 3000);
                }
            }

            private static void Look()
            {
                bool any = false;
                try
                {
                    foreach (var p in System.Diagnostics.Process.GetProcesses())
                    {
                        if (!any && Array.IndexOf(Names, p.ProcessName.ToLowerInvariant()) >= 0) any = true;
                        p.Dispose();
                    }
                }
                catch { return; }
                _running = any;
            }
        }

        /// <summary>The old way: the window left out of every screen copy (it isn't in screen shares then).</summary>
        private void ExcludeFromCapture()
        {
            try { _window.Dispatcher.BeginInvoke(new Action(() => { if (_running) SetWindowDisplayAffinity(_hwnd, 0x11 /* WDA_EXCLUDEFROMCAPTURE */); })); }
            catch { }
        }

        // ---- Win32 ---------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
            public int bmiColors;
        }

        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] private static extern bool PatBlt(IntPtr dc, int x, int y, int w, int h, uint rop);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

        // ---- One picture of the desktop for every glass ----------------------------------------------

        /// <summary>
        /// The desktop (wallpaper) as one picture shared by every glass in desktop mode, instead of a
        /// copy of the whole desktop each (about 12 MB per glass on two screens). A glass asking for a
        /// new one (the wallpaper may have changed) gets it only if the last is over 3 seconds old, so
        /// the widgets' refreshes together take one picture. Freed when no glass uses it.
        /// Not used with an animated wallpaper (each glass then takes its own, at its moment).
        /// </summary>
        private static class DesktopPicture
        {
            private static readonly object Gate = new();
            private static IntPtr _dc, _bmp, _old;
            private static RECT _rect;
            private static DateTime _at;
            private static int _refs;

            public static void AddRef() { lock (Gate) _refs++; }

            public static void Release()
            {
                lock (Gate)
                {
                    if (--_refs > 0) return;
                    _refs = 0;
                    Free();
                }
            }

            /// <summary>Copies a part of it (screen pixels) into dst; takes it first if needed.</summary>
            public static bool CopyTo(IntPtr dst, int x, int y, int w, int h, bool fresh, out RECT rect)
            {
                lock (Gate)
                {
                    rect = default;
                    IntPtr desk = FindWindow("Progman", null);
                    if (desk == IntPtr.Zero || !GetWindowRect(desk, out var now)) return false;
                    bool moved = now.Left != _rect.Left || now.Top != _rect.Top || now.Right != _rect.Right || now.Bottom != _rect.Bottom;
                    if (_dc == IntPtr.Zero || moved || (fresh && (DateTime.UtcNow - _at).TotalSeconds > 3))
                        if (!Take(desk, now)) return false;
                    rect = _rect;
                    return BitBlt(dst, 0, 0, w, h, _dc, x - _rect.Left, y - _rect.Top, 0x00CC0020 /* SRCCOPY */);
                }
            }

            private static bool Take(IntPtr desk, RECT r)
            {
                Free();
                int dw = r.Right - r.Left, dh = r.Bottom - r.Top;
                if (dw <= 0 || dh <= 0) return false;
                IntPtr screen = GetDC(IntPtr.Zero);
                try
                {
                    _dc = CreateCompatibleDC(screen);
                    _bmp = CreateCompatibleBitmap(screen, dw, dh);
                }
                finally { ReleaseDC(IntPtr.Zero, screen); }
                if (_dc == IntPtr.Zero || _bmp == IntPtr.Zero) { Free(); return false; }
                _old = SelectObject(_dc, _bmp);
                _rect = r;
                if (!PrintWindow(desk, _dc, 2 /* PW_RENDERFULLCONTENT */)) { Free(); return false; }
                _at = DateTime.UtcNow;
                return true;
            }

            private static void Free()
            {
                if (_dc != IntPtr.Zero)
                {
                    if (_old != IntPtr.Zero) SelectObject(_dc, _old);
                    DeleteDC(_dc);
                }
                if (_bmp != IntPtr.Zero) DeleteObject(_bmp);
                _dc = _bmp = _old = IntPtr.Zero;
                _rect = default;
            }

            // ---- Is the wallpaper animated? (checked at most every 30 seconds) ----

            private static bool _animated;
            private static DateTime _checkedAt = DateTime.MinValue;

            private static readonly string[] AnimatedApps =
            {
                "wallpaper32", "wallpaper64", "lively", "livelywpf", "lively.playerwebview2", "lively.playermpv", "lively.playercefsharp",
                "rainwallpaper", "pushvideowallpaper", "desktophut", "videowallpaper", "deskscapes",
            };

            /// <summary>A live wallpaper is running: an app known for it, or a window of another app
            /// put inside the desktop window (that's how they draw under the icons).</summary>
            public static bool Animated
            {
                get
                {
                    lock (Gate)
                    {
                        if ((DateTime.UtcNow - _checkedAt).TotalSeconds < 30) return _animated;
                        _checkedAt = DateTime.UtcNow;
                        _animated = Check();
                        return _animated;
                    }
                }
            }

            private static bool Check()
            {
                try
                {
                    if (Wallpapers.VideoPlaying) return true;   // ours
                    foreach (var name in AnimatedApps)
                    {
                        var found = System.Diagnostics.Process.GetProcessesByName(name);
                        bool any = found.Length > 0;
                        foreach (var p in found) p.Dispose();
                        if (any) return true;
                    }
                    IntPtr progman = FindWindow("Progman", null);
                    if (progman == IntPtr.Zero) return false;
                    GetWindowThreadProcessId(progman, out uint shell);
                    bool foreign = false;
                    bool Look(IntPtr parent)
                    {
                        IntPtr c = IntPtr.Zero;
                        while ((c = FindWindowEx(parent, c, null, null)) != IntPtr.Zero)
                        {
                            GetWindowThreadProcessId(c, out uint pid);
                            if (pid != shell && pid != (uint)Environment.ProcessId) return true;
                        }
                        return false;
                    }
                    if (Look(progman)) return true;
                    IntPtr w = IntPtr.Zero;
                    while ((w = FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
                        if (Look(w)) { foreign = true; break; }
                    return foreign;
                }
                catch { return false; }
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    }
}
