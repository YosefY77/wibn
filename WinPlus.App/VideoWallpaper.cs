using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinPlus
{
    /// <summary>
    /// Plays a video wallpaper, light on the PC:
    ///   * Windows' own Media Foundation decodes it on the graphics card, and its video processor
    ///     shrinks each frame to the screen's size there too (falls back to the processor if the card can't);
    ///   * only the frames that are shown (30 a second by default) are copied back and drawn, with plain
    ///     GDI, into our windows under the desktop icons;
    ///   * all of it on one background thread of its own, so the desktop (whose input is tied to the
    ///     thread that owns those windows) never waits for it.
    /// </summary>
    internal sealed unsafe class VideoWallpaper : IDisposable
    {
        private readonly string _path;
        private readonly int _maxW, _maxH;
        private readonly Thread _thread;
        private readonly TaskCompletionSource<BitmapSource?> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _firstShown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _go = new(false), _running = new(true);
        private volatile bool _stop;
        private volatile int _fps;

        private readonly object _frameLock = new();
        private IntPtr _bits;            // the last frame, top-down BGRX
        private int _w, _h;              // its size
        private List<(IntPtr Hwnd, int W, int H)> _targets = new();

        static VideoWallpaper()
        {
            try { MFStartup(0x00020070 /* MF_VERSION */, 0); } catch { }
        }

        /// <param name="maxW">The biggest screen: frames are made just big enough to fill it.</param>
        private readonly long _startAt;
        private long _position;

        /// <summary>Where it is in the video now (100 ns units), to carry on from there later.</summary>
        public long Position => Interlocked.Read(ref _position);

        /// <param name="startAt">Where to start in the video (100 ns units).</param>
        public VideoWallpaper(string path, int maxW, int maxH, int fps, long startAt = 0)
        {
            _path = path;
            _startAt = Math.Max(0, startAt);
            _maxW = Math.Max(16, maxW);
            _maxH = Math.Max(16, maxH);
            _fps = Math.Clamp(fps, 10, 60);
            _thread = new Thread(Run) { IsBackground = true, Name = "Wibn video wallpaper", Priority = ThreadPriority.BelowNormal };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        /// <summary>The first frame (for the transition), or null if the video can't be played.</summary>
        public Task<BitmapSource?> FirstFrame => _first.Task;

        /// <summary>Done when the first frame is in the windows (or it gave up).</summary>
        public Task FirstShown => _firstShown.Task;

        public int Fps { set => _fps = Math.Clamp(value, 10, 60); }

        public bool Paused
        {
            set { if (value) _running.Reset(); else _running.Set(); }
        }

        /// <summary>Starts drawing into these windows (each fills its window).</summary>
        public void Play(List<(IntPtr Hwnd, int W, int H)> targets)
        {
            lock (_frameLock) _targets = targets;
            _go.Set();
        }

        /// <summary>The frame on screen now (to start the next transition from).</summary>
        public BitmapSource? Snapshot()
        {
            lock (_frameLock) return MakeBitmap();
        }

        public void Dispose()
        {
            if (_onScreen == this) _onScreen = null;
            _stop = true;
            _go.Set();
            _running.Set();
            if (Thread.CurrentThread != _thread) _thread.Join(2000);
        }

        // ---- The thread -------------------------------------------------------------------------

        private void Run()
        {
            IntPtr reader = IntPtr.Zero, device = IntPtr.Zero, manager = IntPtr.Zero;
            try
            {
                reader = Open(out device, out manager);
                if (reader == IntPtr.Zero) { _first.TrySetResult(null); return; }
                if (_startAt > 0) Seek(reader, _startAt);

                // The first frame, for the transition.
                long firstTs = 0;
                bool got = false;
                for (int tries = 0; tries < 30 && !got && !_stop; tries++)
                {
                    int r = Read(reader, out var sample, out long ts, out uint flags);
                    if (r < 0 || (flags & 0x2 /* ENDOFSTREAM */) != 0) break;
                    if ((flags & 0x10 /* CURRENTMEDIATYPECHANGED */) != 0) ReadSize(reader);
                    if (sample == IntPtr.Zero) continue;
                    got = CopyFrame(sample);
                    Release(sample);
                    firstTs = ts;
                }
                if (!got) { _first.TrySetResult(null); return; }
                lock (_frameLock) _first.TrySetResult(MakeBitmap());

                _go.Wait();
                if (_stop) return;
                Present();
                _firstShown.TrySetResult(true);

                // Play: show a frame when its time comes, at most _fps a second; frames in between
                // are decoded (on the graphics card) but never copied or drawn.
                var clock = Stopwatch.StartNew();
                long baseTicks = firstTs;          // video time that matches clock 0 (100 ns units)
                double lastShown = 0, lastTs = firstTs;
                int errors = 0;
                while (!_stop)
                {
                    if (!_running.IsSet)
                    {
                        _running.Wait();
                        if (_stop) break;
                        // Carry on from the same spot.
                        clock.Restart();
                        baseTicks = (long)lastTs;
                        lastShown = -1000;
                    }

                    int r = Read(reader, out var sample, out long ts, out uint flags);
                    if (r < 0)
                    {
                        // A hiccup (the graphics card was busy or reset): start the video over instead of stopping.
                        if (++errors > 5) { CrashLog.Write("video wallpaper", new Exception("read failed 0x" + r.ToString("X8"))); break; }
                        Thread.Sleep(200);
                        Rewind(reader);
                        clock.Restart();
                        baseTicks = -1;
                        lastShown = -1000;
                        continue;
                    }
                    errors = 0;
                    if ((flags & 0x10) != 0) ReadSize(reader);
                    if ((flags & 0x2 /* ENDOFSTREAM */) != 0)
                    {
                        if (sample != IntPtr.Zero) Release(sample);
                        // Again from the start, right after the last frame.
                        Rewind(reader);
                        clock.Restart();
                        baseTicks = -1;
                        lastShown = -1000;
                        continue;
                    }
                    if (sample == IntPtr.Zero) continue;
                    if (baseTicks < 0) baseTicks = ts;
                    lastTs = ts;
                    Interlocked.Exchange(ref _position, ts);

                    double due = (ts - baseTicks) / 10000.0;   // ms after clock 0
                    double now = clock.Elapsed.TotalMilliseconds;
                    double gap = 1000.0 / _fps;
                    if (due - lastShown < gap - 2 || now - due > 250)
                    {
                        // Not this one: too soon after the last, or too late (catching up).
                        Release(sample);
                        if (now - due > 1500) { clock.Restart(); baseTicks = ts; lastShown = -1000; }
                        continue;
                    }
                    if (due > now) Thread.Sleep((int)Math.Min(500, due - now));
                    bool ok = CopyFrame(sample);
                    Release(sample);
                    if (ok) Present();
                    lastShown = due;
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("video wallpaper", ex);
                _first.TrySetResult(null);
            }
            finally
            {
                _firstShown.TrySetResult(false);
                Release(reader);
                Release(manager);
                Release(device);
                lock (_frameLock)
                {
                    if (_bits != IntPtr.Zero) Marshal.FreeHGlobal(_bits);
                    _bits = IntPtr.Zero;
                }
            }
        }

        // ---- Media Foundation ---------------------------------------------------------------------

        private static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new("0F81DA2C-B537-4672-A8B2-A681B17307A3");
        private static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("A634A91C-822B-41B9-A494-4DE4643612B0");
        private static readonly Guid MF_SOURCE_READER_D3D_MANAGER = new("EC822DA2-E1E9-4B29-A0D8-563C719F5269");
        private static readonly Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
        private static readonly Guid MF_MT_SUBTYPE = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
        private static readonly Guid MF_MT_FRAME_SIZE = new("1652C33D-D6B2-4012-B834-72030849A37D");
        private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
        private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
        private static readonly Guid IID_IMF2DBuffer = new("7DC9D5F9-9ED9-44EC-9BBF-0600BB589FBB");
        private static readonly Guid IID_ID3D10Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");
        private const uint FirstVideo = 0xFFFFFFFC, AllStreams = 0xFFFFFFFE;

        /// <summary>A source reader that gives RGB32 frames just big enough for the biggest screen.</summary>
        private IntPtr Open(out IntPtr device, out IntPtr manager)
        {
            device = manager = IntPtr.Zero;
            // The graphics card does the decoding and the shrinking when it can.
            try
            {
                if (D3D11CreateDevice(IntPtr.Zero, 1 /* HARDWARE */, IntPtr.Zero, 0x20 /* BGRA */ | 0x800 /* VIDEO_SUPPORT */,
                        IntPtr.Zero, 0, 7, out device, out _, out var context) >= 0)
                {
                    Release(context);
                    if (QI(device, IID_ID3D10Multithread, out var mt) >= 0)
                    {
                        ((delegate* unmanaged<IntPtr, int, int>)Slot(mt, 5))(mt, 1);   // SetMultithreadProtected
                        Release(mt);
                    }
                    if (MFCreateDXGIDeviceManager(out uint token, out manager) < 0 ||
                        ((delegate* unmanaged<IntPtr, IntPtr, uint, int>)Slot(manager, 7))(manager, device, token) < 0)   // ResetDevice
                    {
                        Release(manager); manager = IntPtr.Zero;
                    }
                }
                else device = IntPtr.Zero;
            }
            catch { device = manager = IntPtr.Zero; }

            IntPtr reader = MakeReader(manager);
            if (reader == IntPtr.Zero && manager != IntPtr.Zero)
            {
                // The card couldn't: the processor then.
                Release(manager); manager = IntPtr.Zero;
                reader = MakeReader(IntPtr.Zero);
            }
            return reader;
        }

        private IntPtr MakeReader(IntPtr manager)
        {
            IntPtr attrs = IntPtr.Zero, reader = IntPtr.Zero, native = IntPtr.Zero, type = IntPtr.Zero;
            try
            {
                if (MFCreateAttributes(out attrs, 3) < 0) return IntPtr.Zero;
                SetUInt32(attrs, MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
                if (manager != IntPtr.Zero)
                {
                    SetUInt32(attrs, MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
                    SetUnknown(attrs, MF_SOURCE_READER_D3D_MANAGER, manager);
                }
                if (MFCreateSourceReaderFromURL(_path, attrs, out reader) < 0) return IntPtr.Zero;

                var r = reader;
                ((delegate* unmanaged<IntPtr, uint, int, int>)Slot(r, 4))(r, AllStreams, 0);   // SetStreamSelection
                ((delegate* unmanaged<IntPtr, uint, int, int>)Slot(r, 4))(r, FirstVideo, 1);

                // The video's own size, then just enough to fill the biggest screen (never bigger than the video).
                int vw = 1920, vh = 1080;
                if (((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Slot(r, 5))(r, FirstVideo, 0, &native) >= 0)
                {
                    if (GetUInt64(native, MF_MT_FRAME_SIZE, out ulong size)) { vw = (int)(size >> 32); vh = (int)(size & 0xFFFFFFFF); }
                }
                double scale = Math.Min(1, Math.Max((double)_maxW / vw, (double)_maxH / vh));
                int tw = Math.Max(2, (int)Math.Round(vw * scale / 2) * 2), th = Math.Max(2, (int)Math.Round(vh * scale / 2) * 2);

                bool set = false;
                foreach (bool sized in new[] { true, false })
                {
                    if (MFCreateMediaType(out type) < 0) return Fail(ref reader);
                    SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Video);
                    SetGuid(type, MF_MT_SUBTYPE, MFVideoFormat_RGB32);
                    if (sized) SetUInt64(type, MF_MT_FRAME_SIZE, ((ulong)(uint)tw << 32) | (uint)th);
                    int hr = ((delegate* unmanaged<IntPtr, uint, uint*, IntPtr, int>)Slot(r, 7))(r, FirstVideo, null, type);   // SetCurrentMediaType
                    Release(type); type = IntPtr.Zero;
                    if (hr >= 0) { set = true; break; }
                }
                if (!set) return Fail(ref reader);
                ReadSize(reader);
                return reader;
            }
            catch (Exception ex)
            {
                CrashLog.Write("video wallpaper open", ex);
                return Fail(ref reader);
            }
            finally
            {
                Release(native);
                Release(type);
                Release(attrs);
            }
        }

        private static IntPtr Fail(ref IntPtr reader)
        {
            Release(reader);
            reader = IntPtr.Zero;
            return IntPtr.Zero;
        }

        /// <summary>The size of the frames the reader gives now.</summary>
        private void ReadSize(IntPtr reader)
        {
            IntPtr type = IntPtr.Zero;
            if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Slot(reader, 6))(reader, FirstVideo, &type) < 0) return;   // GetCurrentMediaType
            if (GetUInt64(type, MF_MT_FRAME_SIZE, out ulong size))
            {
                int w = (int)(size >> 32), h = (int)(size & 0xFFFFFFFF);
                lock (_frameLock)
                {
                    if (w != _w || h != _h || _bits == IntPtr.Zero)
                    {
                        if (_bits != IntPtr.Zero) Marshal.FreeHGlobal(_bits);
                        _w = w; _h = h;
                        _bits = Marshal.AllocHGlobal(w * h * 4);
                        new Span<byte>((void*)_bits, w * h * 4).Clear();
                    }
                }
            }
            Release(type);
        }

        private static int Read(IntPtr reader, out IntPtr sample, out long ts, out uint flags)
        {
            IntPtr s = IntPtr.Zero; long t = 0; uint f = 0, actual = 0;
            int hr = ((delegate* unmanaged<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Slot(reader, 9))(reader, FirstVideo, 0, &actual, &f, &t, &s);
            sample = s; ts = t; flags = f;
            return hr;
        }

        private static void Rewind(IntPtr reader) => Seek(reader, 0);

        private static void Seek(IntPtr reader, long position)
        {
            var nullGuid = Guid.Empty;
            byte* pv = stackalloc byte[24];
            new Span<byte>(pv, 24).Clear();
            *(ushort*)pv = 20;   // VT_I8
            *(long*)(pv + 8) = position;
            ((delegate* unmanaged<IntPtr, Guid*, void*, int>)Slot(reader, 8))(reader, &nullGuid, pv);   // SetCurrentPosition
        }

        /// <summary>Copies the sample's picture into our frame (top-down rows).</summary>
        private bool CopyFrame(IntPtr sample)
        {
            IntPtr buffer = IntPtr.Zero;
            if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(sample, 41))(sample, &buffer) < 0 || buffer == IntPtr.Zero) return false;   // ConvertToContiguousBuffer
            try
            {
                lock (_frameLock)
                {
                    if (_bits == IntPtr.Zero) return false;
                    int row = _w * 4;
                    if (QI(buffer, IID_IMF2DBuffer, out var b2) >= 0)
                    {
                        byte* scan0 = null; int pitch = 0;
                        try
                        {
                            if (((delegate* unmanaged<IntPtr, byte**, int*, int>)Slot(b2, 3))(b2, &scan0, &pitch) < 0) return false;   // Lock2D
                            for (int y = 0; y < _h; y++)
                                Buffer.MemoryCopy(scan0 + (long)y * pitch, (byte*)_bits + (long)y * row, row, row);
                            ((delegate* unmanaged<IntPtr, int>)Slot(b2, 4))(b2);   // Unlock2D
                            return true;
                        }
                        finally { Release(b2); }
                    }
                    byte* data = null; uint max = 0, len = 0;
                    if (((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &len) < 0) return false;   // Lock
                    long need = (long)row * _h;
                    if (len >= need) Buffer.MemoryCopy(data, (void*)_bits, need, need);
                    ((delegate* unmanaged<IntPtr, int>)Slot(buffer, 4))(buffer);   // Unlock
                    return len >= need;
                }
            }
            finally { Release(buffer); }
        }

        /// <summary>Draws the frame into every window, filling it (cut at the sides it doesn't fit).</summary>
        private void Present()
        {
            lock (_frameLock)
            {
                if (_bits == IntPtr.Zero) return;
                var bmi = new BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = _w, biHeight = -_h, biPlanes = 1, biBitCount = 32,
                };
                foreach (var (hwnd, w, h) in _targets)
                {
                    IntPtr dc = GetDC(hwnd);
                    if (dc == IntPtr.Zero) continue;
                    double scale = Math.Max((double)w / _w, (double)h / _h);
                    int sw = Math.Min(_w, (int)Math.Round(w / scale)), sh = Math.Min(_h, (int)Math.Round(h / scale));
                    int sx = (_w - sw) / 2, sy = (_h - sh) / 2;
                    SetStretchBltMode(dc, sw == w && sh == h ? 3 : 4 /* COLORONCOLOR when the same size, HALFTONE when scaled */);
                    StretchDIBits(dc, 0, 0, w, h, sx, sy, sw, sh, _bits, ref bmi, 0, 0x00CC0020 /* SRCCOPY */);
                    ReleaseDC(hwnd, dc);
                }
                GdiFlush();   // out of GDI's queue now, not whenever it fills up
            }
            _onScreen = this;
            LiquidGlass.VideoFrameShown();   // the widgets' glass shows the video moving behind them too
        }

        // ---- The frame on screen, for the widgets' glass ----------------------------------------------

        private static volatile VideoWallpaper? _onScreen;

        /// <summary>
        /// Draws what the video shows now at (x, y), w × h screen pixels, into dc (the parts with no video stay as
        /// they were). False when no video is on the desktop. Call with the thread's DPI awareness set to real pixels.
        /// </summary>
        public static bool DrawInto(IntPtr dst, int x, int y, int w, int h)
        {
            var v = _onScreen;
            if (v == null) return false;
            lock (v._frameLock)
            {
                if (v._bits == IntPtr.Zero || v._stop) return false;
                var bmi = new BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = v._w, biHeight = -v._h, biPlanes = 1, biBitCount = 32,
                };
                bool any = false;
                foreach (var (hwnd, tw, th) in v._targets)
                {
                    if (!GetWindowRect(hwnd, out var r)) continue;
                    if (r.Right <= x || r.Left >= x + w || r.Bottom <= y || r.Top >= y + h) continue;   // not under it
                    // The same crop as on the desktop (filling the screen), drawn where that screen is.
                    double scale = Math.Max((double)tw / v._w, (double)th / v._h);
                    int sw = Math.Min(v._w, (int)Math.Round(tw / scale)), sh = Math.Min(v._h, (int)Math.Round(th / scale));
                    int sx = (v._w - sw) / 2, sy = (v._h - sh) / 2;
                    SetStretchBltMode(dst, 3 /* COLORONCOLOR: the glass blurs it anyway */);
                    StretchDIBits(dst, r.Left - x, r.Top - y, r.Right - r.Left, r.Bottom - r.Top, sx, sy, sw, sh, v._bits, ref bmi, 0, 0x00CC0020 /* SRCCOPY */);
                    any = true;
                }
                return any;
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct WINRECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out WINRECT r);

        private BitmapSource? MakeBitmap()
        {
            if (_bits == IntPtr.Zero || _w <= 0 || _h <= 0) return null;
            var bmp = BitmapSource.Create(_w, _h, 96, 96, PixelFormats.Bgr32, null, _bits, _w * _h * 4, _w * 4);
            bmp.Freeze();
            return bmp;
        }

        // ---- COM by hand (vtable slots) -------------------------------------------------------------

        private static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

        private static void Release(IntPtr obj)
        {
            if (obj != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)Slot(obj, 2))(obj);
        }

        private static int QI(IntPtr obj, Guid iid, out IntPtr result)
        {
            IntPtr r = IntPtr.Zero;
            int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Slot(obj, 0))(obj, &iid, &r);
            result = r;
            return hr;
        }

        // IMFAttributes: GetUINT64 8, SetUINT32 21, SetUINT64 22, SetGUID 24, SetUnknown 27.
        private static bool GetUInt64(IntPtr a, Guid key, out ulong value)
        {
            ulong v = 0;
            int hr = ((delegate* unmanaged<IntPtr, Guid*, ulong*, int>)Slot(a, 8))(a, &key, &v);
            value = v;
            return hr >= 0;
        }

        private static void SetUInt32(IntPtr a, Guid key, uint v) => ((delegate* unmanaged<IntPtr, Guid*, uint, int>)Slot(a, 21))(a, &key, v);
        private static void SetUInt64(IntPtr a, Guid key, ulong v) => ((delegate* unmanaged<IntPtr, Guid*, ulong, int>)Slot(a, 22))(a, &key, v);
        private static void SetGuid(IntPtr a, Guid key, Guid v) => ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Slot(a, 24))(a, &key, &v);
        private static void SetUnknown(IntPtr a, Guid key, IntPtr v) => ((delegate* unmanaged<IntPtr, Guid*, IntPtr, int>)Slot(a, 27))(a, &key, v);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth, biHeight;
            public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
        }

        [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out IntPtr attrs, uint size);
        [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IntPtr type);
        [DllImport("mfplat.dll")] private static extern int MFCreateDXGIDeviceManager(out uint token, out IntPtr manager);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attrs, out IntPtr reader);
        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint count,
            uint sdk, out IntPtr device, out int level, out IntPtr context);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool GdiFlush();
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")]
        private static extern int StretchDIBits(IntPtr dc, int x, int y, int w, int h, int sx, int sy, int sw, int sh,
            IntPtr bits, ref BITMAPINFOHEADER bmi, uint usage, uint rop);
    }
}
