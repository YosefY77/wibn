using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WinPlus
{
    /// <summary>
    /// The sound waves widget: moving waves for whatever a chosen device hears. A speaker
    /// (or headphones) is listened to with loopback (what comes out of it); a microphone
    /// is listened to directly (when you talk). Options: "device" = "out" (the default
    /// speaker), "in" (the default mic) or a device id; "style" = how it's drawn.
    /// It only listens while it's on the desktop.
    /// </summary>
    internal sealed class AudioWavesWidget : WidgetContent
    {
        private const int FftSize = 2048;

        public static readonly (string Key, string Name)[] Styles =
        {
            ("bars", "أعمدة"),
            ("mirror", "أعمدة من الوسط"),
            ("siri", "موجات ناعمة"),
            ("scope", "خط الصوت"),
            ("circle", "دائرة"),
            ("dots", "نقاط"),
        };

        private readonly Canvas2 _canvas;
        private readonly TextBlock _problem;
        private readonly SolidColorBrush _ink = new(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private readonly DispatcherTimer _watch = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };

        // Filled by the capture thread, read by the UI.
        private readonly object _lock = new();
        private readonly float[] _ring = new float[FftSize];
        private int _ringAt;
        private DateTime _lastData = DateTime.MinValue;
        private int _rate = 48000;

        private IWaveIn? _capture;
        private string _deviceId = "";
        private bool _running, _failed;

        public AudioWavesWidget()
        {
            _canvas = new Canvas2(this);
            _problem = new TextBlock
            {
                FontSize = 11, Foreground = _ink, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed, Margin = new Thickness(6),
            };
            var grid = new Grid();
            grid.Children.Add(_canvas);
            grid.Children.Add(_problem);
            Child = grid;
            _watch.Tick += (_, _) => Watch();
        }

        public override (int MinW, int MinH, int MaxW, int MaxH) Limits => (2, 1, Widgets.AnyMax, Widgets.MaxCells);

        public override (int W, int H)[]? SizeChoices => new[]
        {
            (3, 1), (4, 1), (6, 1), (8, 1),
            (2, 2), (3, 2), (4, 2), (6, 2), (8, 2),
            (3, 3), (4, 3), (6, 3), (4, 4),
        };

        public override Thickness? CardPadding => new Thickness(6);

        public override void Start()
        {
            _running = true;
            Open();
            _watch.Start();
            CompositionTarget.Rendering += OnFrame;
        }

        private bool _paused;

        /// <summary>Under a maximized / full-screen window: stops listening and drawing until it shows again.</summary>
        public override void Covered(bool covered)
        {
            if (covered && _running) { _paused = true; Stop(); }
            else if (!covered && _paused) { _paused = false; Start(); }
        }

        public override void Stop()
        {
            _running = false;
            _watch.Stop();
            CompositionTarget.Rendering -= OnFrame;
            Close();
        }

        public override void SetAccent(Color color) => _canvas.SetColor(color);

        public override void SetLightBehind(bool light) =>
            _ink.Color = light ? Color.FromArgb(0xB0, 0x3C, 0x3C, 0x43) : Color.FromRgb(0xA0, 0xA3, 0xAA);

        // ---- Options --------------------------------------------------------------------------

        private string Opt(string key, string fallback) =>
            Info?.Options.TryGetValue(key, out var v) == true && !string.IsNullOrEmpty(v) ? v : fallback;

        private void SetOpt(string key, string value)
        {
            if (Info == null) return;
            Info.Options[key] = value;
            Widgets.Save();
        }

        private string Device => Opt("device", "out");
        public string Look => Opt("style", "bars");

        public override IEnumerable<Control> MenuItems()
        {
            // Which device it listens to
            var dev = new MenuItem { Header = Lang.T("جهاز الصوت") };
            void Choice(string header, string key)
            {
                var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = Device == key };
                item.Click += (_, _) => { SetOpt("device", key); Reopen(); };
                dev.Items.Add(item);
            }
            Choice(Lang.T("السماعة الافتراضية"), "out");
            Choice(Lang.T("المايك الافتراضي"), "in");
            try
            {
                using var en = new MMDeviceEnumerator();
                var outs = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                var ins = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
                if (outs.Count > 0)
                {
                    dev.Items.Add(new Separator());
                    dev.Items.Add(new MenuItem { Header = Lang.T("السماعات"), IsEnabled = false });
                    foreach (var d in outs) Choice("   " + DeviceName(d), d.ID);
                }
                if (ins.Count > 0)
                {
                    dev.Items.Add(new Separator());
                    dev.Items.Add(new MenuItem { Header = Lang.T("المايكات"), IsEnabled = false });
                    foreach (var d in ins) Choice("   " + DeviceName(d), d.ID);
                }
                foreach (var d in outs.Concat(ins)) d.Dispose();
            }
            catch { }
            yield return dev;

            // How it looks
            var look = new MenuItem { Header = Lang.T("شكل الموجات") };
            foreach (var (key, name) in Styles)
            {
                var item = new MenuItem { Header = Lang.T(name), IsCheckable = true, IsChecked = Look == key };
                item.Click += (_, _) => { SetOpt("style", key); _canvas.Reset(); };
                look.Items.Add(item);
            }
            yield return look;
        }

        private static string DeviceName(MMDevice d)
        {
            try { return d.FriendlyName; } catch { return "?"; }
        }

        // ---- Listening ------------------------------------------------------------------------

        private void Reopen()
        {
            Close();
            if (_running) Open();
        }

        private void Open()
        {
            _failed = false;
            try
            {
                using var en = new MMDeviceEnumerator();
                string want = Device;
                MMDevice device = want switch
                {
                    "out" => en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia),
                    "in" => en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications),
                    _ => en.GetDevice(want),
                };
                if (device.State != DeviceState.Active) throw new InvalidOperationException();
                _deviceId = device.ID;
                IWaveIn capture = device.DataFlow == DataFlow.Render
                    ? new WasapiLoopbackCapture(device)
                    : new WasapiCapture(device, true, 30);
                var format = capture.WaveFormat;
                _rate = format.SampleRate;
                int bits = format.BitsPerSample, channels = Math.Max(1, format.Channels);
                bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                               || (format.Encoding == WaveFormatEncoding.Extensible && bits == 32);
                capture.DataAvailable += (_, e) => Take(e.Buffer, e.BytesRecorded, bits, channels, isFloat);
                capture.RecordingStopped += (s, e) =>
                {
                    if (e.Exception == null) return;
                    Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_capture, s)) { _failed = true; Close(); } });
                };
                capture.StartRecording();
                _capture = capture;
                _problem.Visibility = Visibility.Collapsed;
            }
            catch
            {
                _failed = true;
                _capture = null;
                _problem.Text = Lang.T("ما قدرت أفتح جهاز الصوت. كليك يمين عليه واختر جهاز ثاني");
                _problem.Visibility = Visibility.Visible;
            }
        }

        private void Close()
        {
            var c = _capture;
            _capture = null;
            _deviceId = "";
            if (c == null) return;
            Task.Run(() =>
            {
                try { c.StopRecording(); } catch { }
                try { c.Dispose(); } catch { }
            });
        }

        /// <summary>Every few seconds: follow the default device if it changed, or try again after a failure.</summary>
        private void Watch()
        {
            if (!_running) return;
            if (_failed) { Reopen(); return; }
            string want = Device;
            if (want != "out" && want != "in") return;
            try
            {
                using var en = new MMDeviceEnumerator();
                using var d = want == "out"
                    ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    : en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                if (d.ID != _deviceId) Reopen();
            }
            catch { }
        }

        /// <summary>On the capture thread: the sound, mixed down to one channel, into the ring.</summary>
        private void Take(byte[] buffer, int bytes, int bits, int channels, bool isFloat)
        {
            int frame = bits / 8 * channels;
            if (frame <= 0) return;
            int frames = bytes / frame;
            lock (_lock)
            {
                for (int f = 0; f < frames; f++)
                {
                    int at = f * frame;
                    float sum = 0;
                    for (int ch = 0; ch < channels; ch++, at += bits / 8)
                    {
                        sum += bits switch
                        {
                            32 when isFloat => BitConverter.ToSingle(buffer, at),
                            32 => BitConverter.ToInt32(buffer, at) / 2147483648f,
                            24 => ((buffer[at] << 8) | (buffer[at + 1] << 16) | (buffer[at + 2] << 24)) / 2147483648f,
                            16 => BitConverter.ToInt16(buffer, at) / 32768f,
                            _ => 0,
                        };
                    }
                    _ring[_ringAt] = sum / channels;
                    _ringAt = (_ringAt + 1) % FftSize;
                }
                if (frames > 0) _lastData = DateTime.UtcNow;
            }
        }

        /// <summary>The last FftSize samples, oldest first (silence if nothing came lately).</summary>
        private bool Snapshot(float[] into)
        {
            lock (_lock)
            {
                if ((DateTime.UtcNow - _lastData).TotalMilliseconds > 150)
                {
                    Array.Clear(into);
                    return false;
                }
                for (int i = 0; i < FftSize; i++) into[i] = _ring[(_ringAt + i) % FftSize];
                return true;
            }
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            if (!_running || !IsVisible) return;
            _canvas.Tick();
        }

        // ---- Drawing --------------------------------------------------------------------------

        /// <summary>Works out the levels each frame and draws them in the chosen style.</summary>
        private sealed class Canvas2 : FrameworkElement
        {
            private readonly AudioWavesWidget _w;
            private readonly float[] _samples = new float[FftSize];
            private readonly double[] _re = new double[FftSize], _im = new double[FftSize];
            private static readonly double[] Hann = Enumerable.Range(0, FftSize)
                .Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1))).ToArray();

            private double[] _bands = Array.Empty<double>();
            private double[] _peaks = Array.Empty<double>();
            private double _level, _phase, _gain = 1;
            private bool _quiet;
            private Color _color = Color.FromRgb(0x4C, 0x8D, 0xFF);
            private Brush _fill = null!, _soft = null!;
            private Pen _pen = null!, _thin = null!;

            public Canvas2(AudioWavesWidget w)
            {
                _w = w;
                SetColor(_color);
            }

            public void SetColor(Color c)
            {
                _color = c;
                Color light = Color.FromRgb((byte)(c.R + (255 - c.R) * 0.45), (byte)(c.G + (255 - c.G) * 0.45), (byte)(c.B + (255 - c.B) * 0.45));
                var fill = new LinearGradientBrush(light, c, 90);
                fill.Freeze();
                _fill = fill;
                var soft = new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B));
                soft.Freeze();
                _soft = soft;
                _pen = new Pen(fill, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                _pen.Freeze();
                _thin = new Pen(new SolidColorBrush(Color.FromArgb(0x90, light.R, light.G, light.B)), 1.4) { LineJoin = PenLineJoin.Round };
                _thin.Freeze();
                InvalidateVisual();
            }

            public void Reset()
            {
                _bands = Array.Empty<double>();
                _quiet = false;
                InvalidateVisual();
            }

            private int BandCount(string style)
            {
                double w = Math.Max(ActualWidth, 20);
                return style switch
                {
                    "circle" => 48,
                    "dots" => Math.Clamp((int)(w / 12), 6, 48),
                    "siri" or "scope" => 24,
                    _ => Math.Clamp((int)(w / 9), 8, 64),
                };
            }

            public void Tick()
            {
                string style = _w.Look;
                int n = BandCount(style);
                if (_bands.Length != n) { _bands = new double[n]; _peaks = new double[n]; _quiet = false; }

                bool sound = _w.Snapshot(_samples);
                if (!sound && _quiet) return;   // still silent and already drawn flat: nothing to do

                double[] target = new double[n];
                double rms = 0;
                if (sound)
                {
                    for (int i = 0; i < FftSize; i++)
                    {
                        rms += _samples[i] * _samples[i];
                        _re[i] = _samples[i] * Hann[i];
                        _im[i] = 0;
                    }
                    rms = Math.Sqrt(rms / FftSize);
                    Fft(_re, _im);

                    double res = (double)_w._rate / FftSize;
                    double lo = 45, hi = Math.Min(16000, _w._rate / 2.0 - res);
                    for (int b = 0; b < n; b++)
                    {
                        double f0 = lo * Math.Pow(hi / lo, (double)b / n), f1 = lo * Math.Pow(hi / lo, (double)(b + 1) / n);
                        int k0 = Math.Max(1, (int)(f0 / res)), k1 = Math.Max(k0 + 1, (int)Math.Ceiling(f1 / res));
                        double m = 0;
                        for (int k = k0; k < k1 && k < FftSize / 2; k++)
                            m = Math.Max(m, Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]));
                        double amp = m * 4 / FftSize;                             // Hann: ×2, one side: ×2
                        double db = 20 * Math.Log10(amp + 1e-9) + 7.0 * b / n;    // a little lift for the highs
                        target[b] = Math.Clamp((db + 62) / 54, 0, 1);
                    }
                }

                bool still = true;
                for (int b = 0; b < n; b++)
                {
                    double v = target[b], cur = _bands[b];
                    cur = v > cur ? cur + (v - cur) * 0.55 : Math.Max(v, cur - Math.Max(0.012, (cur - v) * 0.16));
                    _bands[b] = cur;
                    _peaks[b] = Math.Max(cur, _peaks[b] - 0.012);
                    if (cur > 0.002 || _peaks[b] > 0.002) still = false;
                }

                double lv = Math.Clamp(Math.Sqrt(rms) * 1.6, 0, 1);
                _level = lv > _level ? _level + (lv - _level) * 0.4 : _level + (lv - _level) * 0.08;
                if (_level > 0.002) still = false;
                _phase += 0.10 + _level * 0.25;

                // The line follows the loudest bit lately, so a quiet mic still shows.
                double peak = 0;
                foreach (var s in _samples) peak = Math.Max(peak, Math.Abs(s));
                double want = peak > 0.0005 ? Math.Clamp(0.8 / peak, 1, 40) : _gain;
                _gain += (want - _gain) * (want < _gain ? 0.3 : 0.03);

                _quiet = !sound && still;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                double w = ActualWidth, h = ActualHeight;
                if (w < 4 || h < 4) return;
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // so clicks and drags land on it
                if (_bands.Length == 0) return;
                switch (_w.Look)
                {
                    case "mirror": Bars(dc, w, h, true); break;
                    case "siri": Siri(dc, w, h); break;
                    case "scope": Scope(dc, w, h); break;
                    case "circle": Circle(dc, w, h); break;
                    case "dots": Dots(dc, w, h); break;
                    default: Bars(dc, w, h, false); break;
                }
            }

            private void Bars(DrawingContext dc, double w, double h, bool mirror)
            {
                int n = _bands.Length;
                double slot = w / n, bw = Math.Max(2, slot * 0.62), r = bw / 2;
                for (int i = 0; i < n; i++)
                {
                    double x = i * slot + (slot - bw) / 2;
                    double bh = Math.Max(bw, _bands[i] * h);
                    if (mirror)
                        dc.DrawRoundedRectangle(_fill, null, new Rect(x, (h - bh) / 2, bw, bh), r, r);
                    else
                    {
                        dc.DrawRoundedRectangle(_fill, null, new Rect(x, h - bh, bw, bh), r, r);
                        double py = h - Math.Max(bw, _peaks[i] * h) - 3;
                        if (_peaks[i] > _bands[i] + 0.02)
                            dc.DrawRoundedRectangle(_soft, null, new Rect(x, Math.Max(0, py), bw, 2), 1, 1);
                    }
                }
            }

            private void Dots(DrawingContext dc, double w, double h)
            {
                int n = _bands.Length;
                double slot = w / n, d = Math.Max(3, slot * 0.6);
                int rows = Math.Max(2, (int)(h / (d * 1.45)));
                double step = h / rows;
                for (int i = 0; i < n; i++)
                {
                    double cx = i * slot + slot / 2;
                    int lit = (int)Math.Round(_bands[i] * rows);
                    for (int r = 0; r < rows; r++)
                    {
                        double cy = h - step * r - step / 2;
                        dc.DrawEllipse(r < Math.Max(1, lit) ? _fill : _soft, null, new Point(cx, cy), d / 2, d / 2);
                    }
                }
            }

            private void Siri(DrawingContext dc, double w, double h)
            {
                double mid = h / 2, amp = (h / 2 - 3) * _level;
                double[] scale = { 1, 0.7, 0.45 }, speed = { 1, -1.4, 0.7 }, freq = { 1.6, 2.3, 3.1 };
                for (int k = scale.Length - 1; k >= 0; k--)
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(0, mid), true, true);
                        int steps = Math.Max(30, (int)(w / 3));
                        var top = new List<Point>(steps + 1);
                        for (int s = 0; s <= steps; s++)
                        {
                            double t = (double)s / steps, x = t * w;
                            double env = Math.Pow(1 - Math.Pow(2 * t - 1, 2), 2);
                            double y = Math.Sin(t * Math.PI * 2 * freq[k] + _phase * speed[k]) * amp * scale[k] * env;
                            top.Add(new Point(x, mid - Math.Abs(y) - 0.6));
                        }
                        c.PolyLineTo(top, true, true);
                        top.Reverse();
                        c.PolyLineTo(top.Select(p => new Point(p.X, 2 * mid - p.Y)).ToList(), true, true);
                    }
                    g.Freeze();
                    dc.PushOpacity(k == 0 ? 0.9 : 0.45);
                    dc.DrawGeometry(k == 0 ? _fill : _soft, k == 0 ? _thin : null, g);
                    dc.Pop();
                }
            }

            private void Scope(DrawingContext dc, double w, double h)
            {
                double mid = h / 2;
                int count = Math.Min(FftSize, 1024), steps = Math.Max(20, (int)(w / 2));
                int start = FftSize - count;
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    for (int s = 0; s <= steps; s++)
                    {
                        int i = start + (int)((double)s / steps * (count - 1));
                        double t = (double)s / steps, env = Math.Sin(t * Math.PI);
                        double y = mid - Math.Clamp(_samples[i] * _gain, -1, 1) * (mid - 3) * env;
                        if (s == 0) c.BeginFigure(new Point(0, y), false, false);
                        else c.LineTo(new Point(t * w, y), true, true);
                    }
                }
                g.Freeze();
                dc.DrawGeometry(null, _pen, g);
            }

            private void Circle(DrawingContext dc, double w, double h)
            {
                int n = _bands.Length;
                var center = new Point(w / 2, h / 2);
                double size = Math.Min(w, h);
                double r = size * 0.24 * (1 + _level * 0.12), len = size * 0.24;
                double thick = Math.Max(1.5, 2 * Math.PI * r / (n * 2) * 0.55);
                var pen = new Pen(_fill, thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                pen.Freeze();
                for (int i = 0; i < n * 2; i++)
                {
                    int b = i < n ? i : 2 * n - 1 - i;        // mirrored, so it meets itself smoothly
                    double a = Math.PI * 2 * i / (n * 2) - Math.PI / 2;
                    double l = 1.5 + _bands[b] * len;
                    var dir = new Vector(Math.Cos(a), Math.Sin(a));
                    dc.DrawLine(pen, center + dir * r, center + dir * (r + l));
                }
                dc.DrawEllipse(_soft, null, center, r * 0.82, r * 0.82);
            }

            /// <summary>In-place radix-2 FFT.</summary>
            private static void Fft(double[] re, double[] im)
            {
                int n = re.Length;
                for (int i = 1, j = 0; i < n; i++)
                {
                    int bit = n >> 1;
                    for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                    j ^= bit;
                    if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
                }
                for (int len = 2; len <= n; len <<= 1)
                {
                    double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
                    for (int i = 0; i < n; i += len)
                    {
                        double cr = 1, ci = 0;
                        for (int k = 0; k < len / 2; k++)
                        {
                            int a = i + k, b = a + len / 2;
                            double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                            re[b] = re[a] - tr; im[b] = im[a] - ti;
                            re[a] += tr; im[a] += ti;
                            double nr = cr * wr - ci * wi;
                            ci = cr * wi + ci * wr;
                            cr = nr;
                        }
                    }
                }
            }
        }
    }
}
