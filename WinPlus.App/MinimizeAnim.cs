using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Wibn's own animation when any window is minimized (like the Mac's genie). Windows' own minimize animation is
    /// turned off while this is on (only for this sign-in: it comes back by itself after a restart, and when this is
    /// turned off or Wibn closes).
    ///
    /// How: the moment a window is minimized its live picture (a DWM thumbnail) is put exactly where the window was,
    /// so nothing seems to move; that spot of the screen is copied once, and the copy is drawn on the graphics card as
    /// a soft sheet (a 3D mesh, smooth edges, smooth scaling) that bends and slides into the window's taskbar button.
    /// It all runs on a thread of its own, so a busy Wibn never makes it stutter.
    /// </summary>
    internal static class MinimizeAnim
    {
        /// <summary>The animations (Windows' own is never used: always one of Wibn's).</summary>
        public static readonly (int Id, string Name)[] Styles =
        {
            (1, "جيني (مثل الماك)"),
            (2, "يصغر للأيقونة"),
            (3, "تلاشي"),
            (4, "يطيح لتحت"),
            (5, "يبعد ويختفي"),
        };

        private const string Key = @"Software\WinPlus\Animations";
        public const int DefaultMs = 300;

        public static int Style { get => Math.Clamp(Get("MinStyle", 1), 1, 5); set { Set("MinStyle", value); Refresh(); } }
        public static int Ms { get => Math.Clamp(Get("MinMs", DefaultMs), 100, 1000); set => Set("MinMs", value); }

        private static IntPtr _hook;
        private static WinEventProc? _proc;
        private static DispatcherTimer? _watch;
        private static readonly Dictionary<IntPtr, (RECT Win, RECT Seen)> _rects = new();   // the last place seen of the windows in front
        private static readonly HashSet<IntPtr> _playing = new();                            // windows being animated now
        private static Dispatcher? _anim;                                                    // the animations' own thread
        private static IntPtr _mouse;
        private static MouseProc? _mouseProc;
        // A copy of a window's picture made a moment before it may be minimized (see OnMouse).
        private static BitmapSource? _snap;
        private static IntPtr _snapFor;
        private static RECT _snapRect;
        private static long _snapAt;

        private static IntPtr OnMouse(int code, IntPtr msg, IntPtr data)
        {
            if (code >= 0 && msg == (IntPtr)0x201 /* LBUTTONDOWN */)
            {
                var pt = Marshal.PtrToStructure<POINT>(data);
                _anim?.BeginInvoke(new Action(() => { try { Snap(pt); } catch { } }), DispatcherPriority.Send);
            }
            return CallNextHookEx(IntPtr.Zero, code, msg, data);
        }

        /// <summary>Copies the window that may be minimized by this press: the one whose title bar it is, or (a press outside it, like on a taskbar) the window in front.</summary>
        private static void Snap(POINT pt)
        {
            IntPtr pick = IntPtr.Zero;
            IntPtr under = GetAncestor(WindowFromPoint(pt), 2 /* GA_ROOT */);
            if (under != IntPtr.Zero && !IsIconic(under) && (GetWindowLongPtr(under, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) == 0
                && Rects(under, out _, out var us) && pt.X >= us.Left && pt.X < us.Right && pt.Y >= us.Top
                && pt.Y < us.Top + 60 * (int)Math.Max(96, GetDpiForWindow(under)) / 96)
                pick = under;
            else
            {
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero && !IsIconic(fg) && Rects(fg, out _, out var fs)
                    && !(pt.X >= fs.Left && pt.X < fs.Right && pt.Y >= fs.Top && pt.Y < fs.Bottom)
                    && (GetWindowLongPtr(fg, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) == 0)
                    pick = fg;
            }
            if (pick == IntPtr.Zero || !Rects(pick, out _, out var seen) || seen.W < 50 || seen.H < 50) return;
            var copy = Run.Copy(seen, (int)GetDpiForWindow(pick));
            if (copy == null) return;
            _snap = copy; _snapFor = pick; _snapRect = seen; _snapAt = Environment.TickCount64;
        }

        /// <summary>Starts (at start-up, and whenever the setting changes).</summary>
        public static void Refresh()
        {
            WindowsAnimation(false);
            // All on the animations' thread: Windows tells it right away that a window is going (not through Wibn's
            // own thread, which may be busy), so the window's picture is back in its place before a gap shows.
            AnimThread().BeginInvoke(new Action(() =>
            {
                if (_hook != IntPtr.Zero) return;
                _proc = OnEvent;
                _hook = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZESTART, IntPtr.Zero, _proc, 0, 0, 0 /* OUTOFCONTEXT */);
                // Where the window in front is, every moment (a minimized window no longer says where it was, and a
                // snapped one would say where it was before it was snapped).
                _watch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
                _watch.Tick += (_, _) =>
                {
                    var fg = GetForegroundWindow(); Remember(fg); Run.Prepare(fg);
                    if (_snap != null && Environment.TickCount64 - _snapAt > 3000) { _snap = null; _snapFor = IntPtr.Zero; MemoryTrim.Soon(); }
                };
                _watch.Start();
            }));
            // A press on a window's title bar (where its minimize button is) or on a taskbar: the window's picture is
            // copied right then, so if it gets minimized the animation starts that very moment. The hook has a thread
            // of its own that does nothing else, so the mouse never waits for an animation or a copy.
            HookThread().Invoke(() =>
            {
                if (_mouse != IntPtr.Zero) return;
                _mouseProc = OnMouse;
                _mouse = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _mouseProc, GetModuleHandle(null), 0);
            });
        }

        /// <summary>Stops, and gives Windows its own animation back (also when Wibn closes).</summary>
        public static void Stop()
        {
            _anim?.Invoke(() =>
            {
                if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
                _watch?.Stop(); _watch = null;
                _rects.Clear();
                _snap = null; _snapFor = IntPtr.Zero;
                Run.Unprepare();
            });
            _hooker?.Invoke(() => { if (_mouse != IntPtr.Zero) { UnhookWindowsHookEx(_mouse); _mouse = IntPtr.Zero; } });
            WindowsAnimation(true);
        }

        /// <summary>Minimizes Wibn's own window, to see the animation.</summary>
        public static void Try()
        {
            var w = Application.Current.MainWindow;
            if (w != null) w.WindowState = WindowState.Minimized;
        }

        private static Dispatcher? _hooker;   // the mouse hook's own thread

        private static Dispatcher AnimThread() => _anim ??= NewThread("Wibn minimize");
        private static Dispatcher HookThread() => _hooker ??= NewThread("Wibn minimize mouse");

        private static Dispatcher NewThread(string name)
        {
            Dispatcher? d = null;
            using var ready = new ManualResetEventSlim();
            var t = new Thread(() =>
            {
                d = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            }) { IsBackground = true, Name = name, Priority = ThreadPriority.AboveNormal };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            ready.Wait();
            return d!;
        }

        // ---- Windows' own animation ------------------------------------------------------------------------

        /// <summary>
        /// Windows' animation off (it would play together with ours) or back as it was. What it was is kept in the
        /// registry, so it comes back right even after a crash (and the change isn't saved: a restart brings it back too).
        /// </summary>
        private static void WindowsAnimation(bool back)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(Key);
                var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>() };
                if (back)
                {
                    if (k.GetValue("MinWasOn") is not int was) return;
                    info.iMinAnimate = was;
                    SystemParametersInfo(SPI_SETANIMATION, info.cbSize, ref info, SPIF_SENDCHANGE);
                    k.DeleteValue("MinWasOn", false);
                }
                else
                {
                    if (!SystemParametersInfo(SPI_GETANIMATION, info.cbSize, ref info, 0)) return;
                    if (k.GetValue("MinWasOn") is not int) k.SetValue("MinWasOn", info.iMinAnimate, RegistryValueKind.DWord);
                    if (info.iMinAnimate == 0) return;
                    info.iMinAnimate = 0;
                    SystemParametersInfo(SPI_SETANIMATION, info.cbSize, ref info, SPIF_SENDCHANGE);
                }
            }
            catch { }
        }

        // ---- A window minimized ----------------------------------------------------------------------------

        /// <summary>The window's whole rectangle, and the part that is seen (without the invisible resize borders).</summary>
        private static bool Rects(IntPtr h, out RECT win, out RECT seen)
        {
            seen = default;
            if (!GetWindowRect(h, out win)) return false;
            if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out seen, Marshal.SizeOf<RECT>()) != 0 || seen.W <= 0) seen = win;
            return true;
        }

        private static void Remember(IntPtr h)
        {
            if (h == IntPtr.Zero || IsIconic(h) || !Rects(h, out var w, out var s) || s.W < 50 || s.H < 50) return;
            if (_rects.Count > 300) _rects.Clear();
            _rects[h] = (w, s);
        }

        private static void OnEvent(IntPtr hook, uint ev, IntPtr h, int obj, int child, uint thread, uint time)
        {
            if (obj != 0 || child != 0 || h == IntPtr.Zero) return;
            try { Start(h); } catch (Exception ex) { CrashLog.Write("minimize animation", ex); }
        }

        private static void Start(IntPtr h)
        {
            if (_playing.Count >= 4 || _playing.Contains(h)) return;   // many at once (Win+M): the rest just go
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0) return;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return;

            // Where it was.
            RECT w, s;
            if (!IsIconic(h) && Rects(h, out var nw, out var ns)) { w = nw; s = ns; }
            else if (_rects.TryGetValue(h, out var seen)) (w, s) = seen;
            else if (Placement(h, out w)) s = w;
            else return;
            if (s.W < 50 || s.H < 50) return;

            // Where it goes is asked of Wibn's thread (its taskbar lives there) while the picture is already up.
            var target = Application.Current.Dispatcher.InvokeAsync(() => Target(h, s), DispatcherPriority.Send);
            _playing.Add(h);
            var run = new Run(h, w, s, MonitorOf(s), target, Style, Ms, (int)GetDpiForWindow(h));
            try { run.Go(); }
            catch (Exception e) { run.End(); CrashLog.Write("minimize animation", e); }   // nothing left on screen
        }

        /// <summary>An animation finished.</summary>
        private static void Done(IntPtr h) => _playing.Remove(h);

        /// <summary>Where a minimized window was, from what Windows keeps to restore it.</summary>
        private static bool Placement(IntPtr h, out RECT r)
        {
            r = default;
            var p = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(h, ref p)) return false;
            var mi = Info(MonitorFromRect(ref p.rcNormalPosition, 2));
            if ((p.flags & 2 /* WPF_RESTORETOMAXIMIZED */) != 0) { r = mi.rcWork; return true; }
            // Workspace coordinates: off by the work area's corner.
            int dx = mi.rcWork.Left - mi.rcMonitor.Left, dy = mi.rcWork.Top - mi.rcMonitor.Top;
            r = new RECT { Left = p.rcNormalPosition.Left + dx, Top = p.rcNormalPosition.Top + dy, Right = p.rcNormalPosition.Right + dx, Bottom = p.rcNormalPosition.Bottom + dy };
            return true;
        }

        private static RECT MonitorOf(RECT w) => Info(MonitorFromRect(ref w, 2 /* NEAREST */)).rcMonitor;

        private static MONITORINFO Info(IntPtr mon)
        {
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(mon, ref mi);
            return mi;
        }

        /// <summary>
        /// Where it goes: its button on Wibn's taskbar, else the taskbar of its screen (under the window's middle),
        /// else the bottom of the screen. And which edge of the screen that is (0 bottom, 1 top, 2 left, 3 right).
        /// </summary>
        private static (RECT Target, int Edge) Target(IntPtr h, RECT w)
        {
            int cx = (w.Left + w.Right) / 2, cy = (w.Top + w.Bottom) / 2;
            if (TaskbarBar.ButtonRect(h) is { } b)
            {
                var t = new RECT { Left = (int)b.Left, Top = (int)b.Top, Right = (int)b.Right, Bottom = (int)b.Bottom };
                var m = MonitorOf(t);
                int tx = (t.Left + t.Right) / 2, ty = (t.Top + t.Bottom) / 2;
                int[] gap = { m.Bottom - ty, ty - m.Top, tx - m.Left, m.Right - tx };
                return (t, Array.IndexOf(gap, gap.Min()));
            }
            return WindowsTarget(w);
        }

        private static (RECT Target, int Edge) WindowsTarget(RECT w)
        {
            int cx = (w.Left + w.Right) / 2, cy = (w.Top + w.Bottom) / 2;
            // Windows' taskbar on that screen: whichever edge isn't in the work area.
            var mi = Info(MonitorFromRect(ref w, 2));
            var (mo, wo) = (mi.rcMonitor, mi.rcWork);
            const int S = 48;
            if (wo.Bottom < mo.Bottom) return (Box(cx, (wo.Bottom + mo.Bottom) / 2, S), 0);
            if (wo.Top > mo.Top) return (Box(cx, (mo.Top + wo.Top) / 2, S), 1);
            if (wo.Left > mo.Left) return (Box((mo.Left + wo.Left) / 2, cy, S), 2);
            if (wo.Right < mo.Right) return (Box((wo.Right + mo.Right) / 2, cy, S), 3);
            return (Box(cx, mo.Bottom - S / 2, S), 0);   // a hidden taskbar: the bottom
        }

        private static RECT Box(int x, int y, int s) => new() { Left = x - s / 2, Top = y - s / 2, Right = x + s / 2, Bottom = y + s / 2 };

        // ---- One animation (on the animations' thread) -------------------------------------------------------

        private sealed class Run
        {
            // The one ready to go: already shown, but 1x1 and just off the screens (so it keeps no screen-sized
            // drawing in memory), with the front window's picture made and hidden. When the window is minimized the
            // picture only has to be shown and the overlay put over the screen: on screen in the very next frame.
            private static HwndSource? _ready;
            private static IntPtr _readyFor, _readyThumb;

            public static void Prepare(IntPtr fg)
            {
                if (_playing.Count > 0 || fg == IntPtr.Zero || IsIconic(fg)) return;   // never in the middle of an animation
                if (_ready != null && fg == _readyFor) return;
                if ((GetWindowLongPtr(fg, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return;
                if (!_rects.TryGetValue(fg, out var r)) return;
                if (_ready == null) { _ready = NewHost(); Park(_ready); }
                if (_readyThumb != IntPtr.Zero) { DwmUnregisterThumbnail(_readyThumb); _readyThumb = IntPtr.Zero; }
                _readyFor = IntPtr.Zero;
                if (DwmRegisterThumbnail(_ready.Handle, fg, out var t) != 0) return;
                var hidden = new DWM_THUMBNAIL_PROPERTIES { dwFlags = 0x8, fVisible = false };
                DwmUpdateThumbnailProperties(t, ref hidden);
                _readyThumb = t; _readyFor = fg;
            }

            public static void Unprepare()
            {
                if (_readyThumb != IntPtr.Zero) { DwmUnregisterThumbnail(_readyThumb); _readyThumb = IntPtr.Zero; }
                _readyFor = IntPtr.Zero;
                if (_ready != null) { Drop(_ready); _ready = null; }
            }

            /// <summary>Shown, 1x1, just off the left of all the screens.</summary>
            private static void Park(HwndSource host) =>
                SetWindowPos(host.Handle, new IntPtr(-1), GetSystemMetrics(76 /* SM_XVIRTUALSCREEN */) - 100, GetSystemMetrics(77 /* SM_YVIRTUALSCREEN */),
                             1, 1, 0x0010 /* NOACTIVATE */ | 0x0040 /* SHOW */);

            /// <summary>An overlay not needed any more: gone, with all it drew.</summary>
            private static void Drop(HwndSource host)
            {
                IntPtr hw = host.Handle;
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => TaskbarApps.Ours.Remove(hw)));
                host.Dispose();
            }

            private readonly IntPtr _src;
            private readonly RECT _win, _seen;
            private RECT _target, _area;
            private readonly DispatcherOperation<(RECT Target, int Edge)> _whereTo;
            private int _edge;
            private readonly int _style, _dpi;
            private readonly double _ms;
            private HwndSource? _host;
            private IntPtr _thumb;
            private MeshGeometry3D? _mesh;
            private ImageBrush? _brush;
            private int _cols, _rows;
            private Stopwatch? _clock;
            private int _shown;   // frames drawn by the mesh (the live picture goes after the first)

            public Run(IntPtr src, RECT win, RECT seen, RECT mon, DispatcherOperation<(RECT Target, int Edge)> whereTo, int style, int ms, int dpi)
            {
                _src = src; _win = win; _seen = seen; _whereTo = whereTo; _style = style; _dpi = dpi > 0 ? dpi : 96;
                _ms = style == 1 ? ms : ms * 0.8;
                _area = mon;   // the overlay covers the screen (and grows to the button, if that's on another one)
            }

            public void Go()
            {
                // The ready overlay (with this window's picture already made, most of the time).
                if (_ready != null)
                {
                    _host = _ready; _ready = null;
                    if (_readyFor == _src) _thumb = _readyThumb;
                    else if (_readyThumb != IntPtr.Zero) DwmUnregisterThumbnail(_readyThumb);
                    _readyThumb = IntPtr.Zero; _readyFor = IntPtr.Zero;
                }
                else _host = NewHost();
                IntPtr hwnd = _host.Handle;

                // 1. The window's live picture exactly where the window was (so it doesn't seem to move at all).
                if (_thumb == IntPtr.Zero && DwmRegisterThumbnail(hwnd, _src, out _thumb) != 0) { _thumb = IntPtr.Zero; End(); return; }
                DwmQueryThumbnailSourceSize(_thumb, out var size);
                // The picture is the whole window, or (some apps) only its seen part: put it on what it is.
                var at = Math.Abs(size.cx - _win.W) <= 2 && Math.Abs(size.cy - _win.H) <= 2 ? _win : _seen;
                void Place()
                {
                    var props = new DWM_THUMBNAIL_PROPERTIES
                    {
                        dwFlags = 0x1 | 0x4 | 0x8 | 0x10,
                        rcDestination = Local(at), opacity = 255, fVisible = true, fSourceClientAreaOnly = false,
                    };
                    DwmUpdateThumbnailProperties(_thumb, ref props);
                }
                Place();
                SetWindowPos(hwnd, new IntPtr(-1), _area.Left, _area.Top, _area.W, _area.H, 0x0010 /* NOACTIVATE */ | 0x0040 /* SHOW */);

                // Where it goes (asked of Wibn's thread meanwhile; if that's busy, Windows' taskbar is used).
                (_target, _edge) = _whereTo.Wait(TimeSpan.FromMilliseconds(40)) == DispatcherOperationStatus.Completed ? _whereTo.Result : WindowsTarget(_seen);
                var area = new RECT
                {
                    Left = Math.Min(_area.Left, _target.Left), Top = Math.Min(_area.Top, _target.Top),
                    Right = Math.Max(_area.Right, _target.Right), Bottom = Math.Max(_area.Bottom, _target.Bottom),
                };
                if (area.Left != _area.Left || area.Top != _area.Top || area.Right != _area.Right || area.Bottom != _area.Bottom)
                {
                    _area = area;
                    Place();
                    SetWindowPos(hwnd, new IntPtr(-1), _area.Left, _area.Top, _area.W, _area.H, 0x0010 | 0x0040);
                }
                // 2. The window's picture for the sheet: the copy made when it was pressed (most of the time), or else
                //    a copy of that spot of the screen now (once the live picture is surely on screen).
                BitmapSource? picture = null;
                if (_snapFor == _src && _snap != null && Environment.TickCount64 - _snapAt < 4000
                    && _snapRect.Left == _seen.Left && _snapRect.Top == _seen.Top && _snapRect.W == _seen.W && _snapRect.H == _seen.H)
                    picture = _snap;
                _snap = null; _snapFor = IntPtr.Zero;
                if (picture == null)
                {
                    DwmFlush();
                    DwmFlush();
                    picture = Copy(_seen, _dpi);
                }
                if (picture == null) { End(); return; }

                // 3. The sheet: a grid of points, many along the way it goes for the genie's curve.
                bool genie = _style == 1;
                _cols = genie ? 10 : 1;
                _rows = genie ? 72 : 1;
                _mesh = new MeshGeometry3D();
                var uv = new PointCollection((_cols + 1) * (_rows + 1));
                var idx = new Int32Collection(_cols * _rows * 6);
                for (int r = 0; r <= _rows; r++)
                    for (int c = 0; c <= _cols; c++)
                        uv.Add(Tex((double)c / _cols, (double)r / _rows));
                for (int r = 0; r < _rows; r++)
                    for (int c = 0; c < _cols; c++)
                    {
                        int a = r * (_cols + 1) + c, b = a + 1, d = a + _cols + 1, e = d + 1;
                        idx.Add(a); idx.Add(d); idx.Add(b);
                        idx.Add(b); idx.Add(d); idx.Add(e);
                    }
                uv.Freeze(); idx.Freeze();
                _mesh.TextureCoordinates = uv;
                _mesh.TriangleIndices = idx;
                _brush = new ImageBrush(picture) { Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(_brush, BitmapScalingMode.Linear);
                var material = new DiffuseMaterial(_brush);
                var model = new GeometryModel3D(_mesh, material) { BackMaterial = material };
                var group = new Model3DGroup();
                group.Children.Add(new AmbientLight(Colors.White));
                group.Children.Add(model);

                // 1 unit = 1 pixel of the overlay; y goes up in 3D, so the screen's y is turned over.
                double w = _area.W, h = _area.H;
                var view = new Viewport3D
                {
                    Camera = new OrthographicCamera(new Point3D(w / 2, -h / 2, 10), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), w)
                    { NearPlaneDistance = 0.1, FarPlaneDistance = 100 },
                    ClipToBounds = false, IsHitTestVisible = false,
                };
                view.Children.Add(new ModelVisual3D { Content = group });
                Frame(0);
                _host.RootVisual = new Border { Background = Brushes.Transparent, Child = view };
                CompositionTarget.Rendering += OnRender;
            }

            private void OnRender(object? sender, EventArgs e)
            {
                try
                {
                    // The mesh has been drawn a few times (surely on screen, under the live picture, looking the same): only
                    // then the live picture goes (never a frame with neither = no blink), and the animation starts.
                    if (_shown++ == 2 && _thumb != IntPtr.Zero) { DwmUnregisterThumbnail(_thumb); _thumb = IntPtr.Zero; _clock = Stopwatch.StartNew(); }
                    if (_clock == null) return;
                    double t = _clock.Elapsed.TotalMilliseconds / _ms;
                    if (t >= 1) { End(); return; }
                    Frame(t);
                }
                catch { End(); }
            }

            private static HwndSource NewHost()
            {
                var p = new HwndSourceParameters("Wibn minimize")
                {
                    WindowStyle = unchecked((int)0x80000000) /* WS_POPUP */,
                    ExtendedWindowStyle = 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */ | 0x8 /* TOPMOST */ | 0x20 /* TRANSPARENT */,
                    Width = 1, Height = 1,
                };
                var host = new HwndSource(p);
                host.AddHook((IntPtr hw, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
                {
                    if (msg == 0x84 /* NCHITTEST */) { handled = true; return (IntPtr)(-1) /* HTTRANSPARENT */; }
                    if (msg == 0x21 /* MOUSEACTIVATE */) { handled = true; return (IntPtr)3 /* NOACTIVATE */; }
                    return IntPtr.Zero;
                });
                IntPtr hwnd = host.Handle;
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => TaskbarApps.Ours.Add(hwnd)));
                // See-through everywhere but what's drawn: the whole window is "glass" and painted clear.
                host.CompositionTarget.BackgroundColor = Colors.Transparent;
                host.RootVisual = new Border { Background = Brushes.Transparent };
                var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);
                int one = 1, square = 1, noBorder = unchecked((int)0xFFFFFFFE);
                DwmSetWindowAttribute(hwnd, 3 /* TRANSITIONS_FORCEDISABLED */, ref one, 4);
                DwmSetWindowAttribute(hwnd, 33 /* WINDOW_CORNER_PREFERENCE */, ref square, 4);
                DwmSetWindowAttribute(hwnd, 34 /* BORDER_COLOR */, ref noBorder, 4);
                return host;
            }

            private bool _ended;

            public void End()
            {
                if (_ended) return;
                _ended = true;
                CompositionTarget.Rendering -= OnRender;
                if (_thumb != IntPtr.Zero) { DwmUnregisterThumbnail(_thumb); _thumb = IntPtr.Zero; }
                if (_host != null)
                {
                    _host.RootVisual = new Border { Background = Brushes.Transparent };
                    if (_ready == null && _hook != IntPtr.Zero) { _ready = _host; Park(_ready); }   // small again, ready for the next one
                    // (turned off meanwhile: it goes)
                    else Drop(_host);
                    _host = null;
                }
                _mesh = null; _brush = null;
                Done(_src);
                MemoryTrim.Soon();   // the window's picture and the sheet go back to Windows
            }

            private RECT Local(RECT r) => new() { Left = r.Left - _area.Left, Top = r.Top - _area.Top, Right = r.Right - _area.Left, Bottom = r.Bottom - _area.Top };

            /// <summary>
            /// A copy of part of the screen, with Windows 11's rounded corners cut out (so no bits of what's behind
            /// show at the corners).
            /// </summary>
            internal static unsafe BitmapSource? Copy(RECT r, int dpi)
            {
                if (r.W <= 0 || r.H <= 0) return null;
                double scale = Math.Min(1.0, 1600.0 / Math.Max(r.W, r.H));
                int w = Math.Max(1, (int)Math.Round(r.W * scale)), h = Math.Max(1, (int)Math.Round(r.H * scale));
                IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
                var bi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                IntPtr bmp = CreateDIBSection(mem, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
                try
                {
                    if (bmp == IntPtr.Zero) return null;
                    IntPtr old = SelectObject(mem, bmp);
                    if (scale >= 1) BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, 0x00CC0020 /* SRCCOPY */);
                    else
                    {
                        SetStretchBltMode(mem, 4 /* HALFTONE: smooth */);
                        SetBrushOrgEx(mem, 0, 0, IntPtr.Zero);
                        StretchBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, r.W, r.H, 0x00CC0020);
                    }
                    SelectObject(mem, old);

                    // The copy has no see-through part: all solid, except the rounded corners (not on a full-screen window).
                    uint* p = (uint*)bits;
                    for (long i = 0, n = (long)w * h; i < n; i++) p[i] |= 0xFF000000;
                    var mon = MonitorOf(r);
                    bool full = r.Left <= mon.Left && r.Top <= mon.Top && r.Right >= mon.Right && r.Bottom >= mon.Bottom;
                    var work = Info(MonitorFromRect(ref r, 2)).rcWork;
                    bool maxed = full || (r.Left <= work.Left && r.Top <= work.Top && r.Right >= work.Right && r.Bottom >= work.Bottom);
                    if (Environment.OSVersion.Version.Build >= 22000 && !maxed)
                    {
                        double rad = 8.0 * Math.Max(96, dpi) / 96 * scale;
                        int R = (int)Math.Ceiling(rad);
                        for (int y = 0; y < R && y < h; y++)
                            for (int x = 0; x < R && x < w; x++)
                            {
                                double dx = R - x - 0.5, dy = R - y - 0.5;
                                double d = Math.Sqrt(dx * dx + dy * dy) - (R - rad);
                                double keep = Math.Clamp(rad - d + 0.5, 0, 1);   // soft edge
                                if (keep >= 1) continue;
                                Fade(p, w, x, y, keep); Fade(p, w, w - 1 - x, y, keep);
                                Fade(p, w, x, h - 1 - y, keep); Fade(p, w, w - 1 - x, h - 1 - y, keep);
                            }
                    }
                    var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bits, w * h * 4, w * 4);
                    src.Freeze();
                    return src;
                }
                finally
                {
                    if (bmp != IntPtr.Zero) DeleteObject(bmp);
                    DeleteDC(mem);
                    ReleaseDC(IntPtr.Zero, screen);
                }
            }

            private static unsafe void Fade(uint* p, int w, int x, int y, double keep)
            {
                if (x < 0 || y < 0) return;
                uint c = p[(long)y * w + x];
                uint Ch(int shift) => (uint)Math.Round(((c >> shift) & 0xFF) * keep) << shift;
                p[(long)y * w + x] = Ch(24) | Ch(16) | Ch(8) | Ch(0);
            }

            private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
            private static double EaseInOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);
            private static double Smooth(double t) => t * t * (3 - 2 * t);
            private static double Lerp(double a, double b, double t) => a + (b - a) * t;

            /// <summary>Puts the sheet where it is at moment <paramref name="t"/> (0 → 1).</summary>
            private void Frame(double t)
            {
                var pts = new Point3DCollection((_cols + 1) * (_rows + 1));
                double alpha;
                if (_style == 1) alpha = Genie(t, pts);
                else
                {
                    var (x0, y0, x1, y1, a) = Plain(t);
                    alpha = a;
                    for (int r = 0; r <= _rows; r++)
                        for (int c = 0; c <= _cols; c++)
                            pts.Add(World(Lerp(x0, x1, (double)c / _cols), Lerp(y0, y1, (double)r / _rows)));
                }
                pts.Freeze();
                _mesh!.Positions = pts;
                _brush!.Opacity = Clamp01(alpha);
            }

            /// <summary>The simple ones: a rectangle that moves / shrinks, and how see-through it is.</summary>
            private (double, double, double, double, double) Plain(double t)
            {
                RECT s = _seen;
                double cx = (s.Left + s.Right) / 2.0, cy = (s.Top + s.Bottom) / 2.0, w = s.W, h = s.H;
                switch (_style)
                {
                    case 2:   // shrinks into the button
                    {
                        double e = EaseInOut(t);
                        return (Lerp(s.Left, _target.Left, e), Lerp(s.Top, _target.Top, e), Lerp(s.Right, _target.Right, e), Lerp(s.Bottom, _target.Bottom, e),
                                1 - Clamp01((t - 0.7) / 0.3));
                    }
                    case 3:   // fades where it is
                    {
                        double e = EaseOut(t), k = 1 - 0.08 * e;
                        return (cx - w * k / 2, cy - h * k / 2, cx + w * k / 2, cy + h * k / 2, 1 - e);
                    }
                    case 4:   // falls down
                    {
                        double e = t * t, k = 1 - 0.15 * e, drop = h * 0.4 * e;
                        return (cx - w * k / 2, cy - h * k / 2 + drop, cx + w * k / 2, cy + h * k / 2 + drop, 1 - Clamp01(t * 1.1));
                    }
                    default:  // goes far away
                    {
                        double e = EaseOut(t), k = 1 - 0.8 * e;
                        return (cx - w * k / 2, cy - h * k / 2, cx + w * k / 2, cy + h * k / 2, 1 - Clamp01((t - 0.3) / 0.7));
                    }
                }
            }

            /// <summary>
            /// The genie, worked out as if the button were below the window (the other edges are turned to that):
            /// the sides first bend in toward the button, then the window slides down through that funnel, its
            /// bottom first, each row as wide as the funnel where it is.
            /// </summary>
            private double Genie(double t, Point3DCollection pts)
            {
                var W = ToC(_seen);
                var T = ToC(_target);
                double bend = EaseOut(Clamp01(t / 0.4));
                double slide = Clamp01((t - 0.08) / 0.92);
                double depth = Math.Max(1, T.Top - W.Top);
                const double lead = 0.6;   // how far ahead the bottom goes

                for (int r = 0; r <= _rows; r++)
                {
                    double v = (double)r / _rows;
                    double q = Clamp01(slide * (1 + lead) - lead * (1 - v)), s = q * q;
                    double y = Lerp(W.Top + v * (W.Bottom - W.Top), T.Top + v * (T.Bottom - T.Top), s);
                    double f = Smooth(Clamp01((y - W.Top) / depth)) * bend;
                    double left = Lerp(W.Left, T.Left, f), right = Lerp(W.Right, T.Right, f);
                    for (int c = 0; c <= _cols; c++)
                    {
                        var (sx, sy) = FromC(Lerp(left, right, (double)c / _cols), y);
                        pts.Add(World(sx, sy));
                    }
                }
                return 1 - Clamp01((t - 0.9) / 0.1);
            }

            /// <summary>A point of the screen as a point of the overlay's 3D world.</summary>
            private Point3D World(double sx, double sy) => new(sx - _area.Left, -(sy - _area.Top), 0);

            // The genie's own frame: the button is "below". 0 bottom, 1 top, 2 left, 3 right.
            private RECT ToC(RECT r) => _edge switch
            {
                1 => new RECT { Left = r.Left, Top = -r.Bottom, Right = r.Right, Bottom = -r.Top },
                2 => new RECT { Left = r.Top, Top = -r.Right, Right = r.Bottom, Bottom = -r.Left },
                3 => new RECT { Left = r.Top, Top = r.Left, Right = r.Bottom, Bottom = r.Right },
                _ => r,
            };

            private (double X, double Y) FromC(double cx, double cy) => _edge switch
            {
                1 => (cx, -cy),
                2 => (-cy, cx),
                3 => (cy, cx),
                _ => (cx, cy),
            };

            /// <summary>Where grid point (u across, v toward the button) is on the picture.</summary>
            private Point Tex(double u, double v)
            {
                if (_style != 1) return new Point(u, v);
                return _edge switch
                {
                    1 => new Point(u, 1 - v),
                    2 => new Point(1 - v, u),
                    3 => new Point(v, u),
                    _ => new Point(u, v),
                };
            }
        }

        // ---- Settings -------------------------------------------------------------------------------------

        private static int Get(string name, int def)
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue(name) is int v ? v : def; }
            catch { return def; }
        }

        private static void Set(string name, int value)
        {
            try { using var k = Registry.CurrentUser.CreateSubKey(Key); k.SetValue(name, value, RegistryValueKind.DWord); } catch { }
        }

        // ---- Win32 ----------------------------------------------------------------------------------------

        private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x80;
        private const int DWMWA_CLOAKED = 14, DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const uint SPI_GETANIMATION = 0x0048, SPI_SETANIMATION = 0x0049, SPIF_SENDCHANGE = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int W => Right - Left;
            public int H => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] private struct MARGINS { public int Left, Right, Top, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct ANIMATIONINFO { public uint cbSize; public int iMinAnimate; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public uint length, flags, showCmd;
            public POINT ptMinPosition, ptMaxPosition;
            public RECT rcNormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination, rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
        }

        private delegate IntPtr MouseProc(int code, IntPtr msg, IntPtr data);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, MouseProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr msg, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);

        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);

        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint param, ref ANIMATIONINFO info, uint winIni);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
        [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
    }
}
