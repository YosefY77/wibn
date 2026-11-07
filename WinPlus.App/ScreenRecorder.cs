using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WinPlus
{
    /// <summary>
    /// Builds and runs the FFmpeg screen recordings. The picture comes from
    /// ddagrab (Desktop Duplication: light, and sees games too) and falls back to
    /// gdigrab. The PC's sound comes from WASAPI loopback, fed to FFmpeg through a
    /// named pipe at a steady pace (silence when nothing plays, so it stays in sync).
    /// </summary>
    internal static class FfmpegCapture
    {
        private static string? _encoder;

        /// <summary>The first H.264 encoder that works here: the graphics card's, else x264.</summary>
        private static string Encoder(string ffmpeg)
        {
            if (_encoder != null) return _encoder;
            foreach (var enc in new[] { "h264_nvenc", "h264_amf", "h264_qsv" })
            {
                if (Works(ffmpeg, $"-hide_banner -loglevel error -f lavfi -i color=black:s=640x360:r=30 -frames:v 3 -pix_fmt nv12 -c:v {enc} -f null -"))
                    return _encoder = enc;
            }
            return _encoder = "libx264";
        }

        private static bool Works(string ffmpeg, string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(ffmpeg, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                })!;
                _ = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return false; }
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        private static string EncoderArgs(string enc, int fps) => enc switch
        {
            "h264_nvenc" => $"-c:v h264_nvenc -preset p4 -rc vbr -cq 23 -b:v 0 -g {fps}",
            "h264_amf" => $"-c:v h264_amf -quality speed -rc cqp -qp_i 22 -qp_p 24 -g {fps}",
            "h264_qsv" => $"-c:v h264_qsv -preset veryfast -global_quality 24 -g {fps}",
            _ => $"-c:v libx264 -preset superfast -tune zerolatency -crf 24 -g {fps}",   // zerolatency: a quarter of the memory
        };

        /// <summary>
        /// Starts FFmpeg recording one screen into <paramref name="output"/> (the
        /// output options come last). Returns null if it couldn't start.
        /// </summary>
        public static async Task<Session?> StartAsync(ScreenCapture.Monitor screen, string outputArgs, bool background)
        {
            string? ffmpeg = Converter.FindFfmpeg();
            if (ffmpeg == null) return null;
            int fps = ScreenCapture.Fps;
            string enc = await Task.Run(() => Encoder(ffmpeg));

            int output = await Task.Run(() => Dxgi.OutputIndex(screen.Device));
            // Even sizes: the H.264 encoders want them.
            const string even = "crop=trunc(iw/2)*2:trunc(ih/2)*2";
            var sources = new List<(string Input, string Filter)>();
            if (output >= 0)
                sources.Add(($"-f lavfi -i ddagrab=output_idx={output}:framerate={fps}:draw_mouse=1",
                             $"hwdownload,format=bgra,{even},format=nv12"));
            var b = screen.Bounds;
            sources.Add(($"-f gdigrab -framerate {fps} -draw_mouse 1 -offset_x {b.X} -offset_y {b.Y} -video_size {b.Width}x{b.Height} -i desktop",
                         $"{even},format=nv12"));

            foreach (var (input, filter) in sources)
            {
                AudioPump? audio = null;
                if (ScreenCapture.RecordSound)
                {
                    try { audio = new AudioPump(); }
                    catch (Exception ex) { CrashLog.Write("capture audio", ex); }
                }
                var args = new StringBuilder("-hide_banner -loglevel error -y ");
                args.Append(input).Append(' ');
                if (audio != null) args.Append(audio.InputArgs).Append(' ');
                args.Append("-map 0:v ");
                if (audio != null) args.Append("-map 1:a -c:a aac -b:a 160k ");
                args.Append("-vf ").Append(filter).Append(' ').Append(EncoderArgs(enc, fps)).Append(' ').Append(outputArgs);

                var psi = new ProcessStartInfo(ffmpeg, args.ToString())
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardError = true,
                };
                Process p;
                var started = DateTime.Now;
                try { p = Process.Start(psi)!; }
                catch (Exception ex) { audio?.Dispose(); CrashLog.Write("capture start", ex); return null; }
                KillWithUs.Add(p);
                if (background) { try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } }
                var errors = new StringBuilder();
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) if (errors.Length < 4000) errors.AppendLine(e.Data); };
                p.BeginErrorReadLine();
                audio?.Begin();

                // A source that doesn't work here quits right away: try the next one.
                if (await Task.Run(() => p.WaitForExit(2500)))
                {
                    lock (errors) CrashLog.Note("capture source failed: " + input + "\r\n" + errors);
                    audio?.Dispose();
                    p.Dispose();
                    continue;
                }
                return new Session(p, audio, errors, started);
            }
            return null;
        }

        /// <summary>A running FFmpeg recording.</summary>
        internal sealed class Session
        {
            public Session(Process process, AudioPump? audio, StringBuilder errors, DateTime started)
            {
                Started = started;
                Process = process;
                Audio = audio;
                Errors = errors;
            }

            public DateTime Started { get; }
            public Process Process { get; }
            public AudioPump? Audio { get; }
            public StringBuilder Errors { get; }
            public bool HasExited { get { try { return Process.HasExited; } catch { return true; } } }

            /// <summary>Asks FFmpeg to finish the file properly ('q'), then makes sure it's gone.</summary>
            public void Stop(int waitMs = 10000)
            {
                try
                {
                    if (!Process.HasExited)
                    {
                        try { Process.StandardInput.Write('q'); Process.StandardInput.Flush(); } catch { }
                        if (!Process.WaitForExit(waitMs)) Process.Kill();
                        Process.WaitForExit(2000);
                    }
                }
                catch { }
                Audio?.Dispose();
                Process.Dispose();
            }
        }
    }

    /// <summary>The video recording (Kind.Record).</summary>
    internal static class Recorder
    {
        private static FfmpegCapture.Session? _session;
        private static string? _file;
        private static bool _starting;
        private static RecordingBadge? _badge;

        public static bool IsRecording => _session != null || _starting;

        public static async Task StartAsync()
        {
            if (IsRecording) return;
            _starting = true;
            try
            {
                ScreenCapture.Monitor screen;
                using (new ScreenCapture.DpiScope()) screen = ScreenCapture.MonitorUnderCursor();
                string folder = ScreenCapture.VideoFolder;
                Directory.CreateDirectory(folder);
                string file = ScreenCapture.UniquePath(folder, "Screen Recording", ".mp4");
                int seconds = ScreenCapture.RecordMinutes * 60;
                var session = await FfmpegCapture.StartAsync(screen,
                    $"-t {seconds} -movflags +faststart \"{file}\"", background: false);
                if (session == null)
                {
                    CaptureToast.Show(Lang.T("ما قدرت أبدأ التسجيل"), Lang.T("جرّب مرة ثانية، ولو تكرر أرسل ملف السجل"));
                    return;
                }
                _session = session;
                _file = file;
                session.Process.EnableRaisingEvents = true;
                session.Process.Exited += (_, _) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(Finish);
                _badge = new RecordingBadge(TimeSpan.FromSeconds(seconds), session.Started);
                _badge.StopRequested += Stop;
                _badge.Show();
            }
            catch (Exception ex)
            {
                CrashLog.Write("record", ex);
            }
            finally
            {
                _starting = false;
            }
        }

        /// <summary>Stops early (the file keeps what was recorded).</summary>
        public static void Stop()
        {
            var s = _session;
            if (s == null) return;
            _badge?.SetSaving();
            Task.Run(() => s.Stop()).ContinueWith(_ => System.Windows.Application.Current?.Dispatcher.InvokeAsync(Finish));
        }

        private static void Finish()
        {
            var s = _session;
            if (s == null) return;
            if (!s.HasExited) { Stop(); return; }   // the time ran out: FFmpeg closed the file itself
            _session = null;
            s.Audio?.Dispose();
            _badge?.Close();
            _badge = null;
            string? file = _file;
            if (file != null && File.Exists(file) && new FileInfo(file).Length > 0)
            {
                CaptureToast.ShowVideo(file, Lang.T("انحفظ التسجيل"));
            }
            else
            {
                lock (s.Errors) CrashLog.Note("record failed:\r\n" + s.Errors);
                CaptureToast.Show(Lang.T("ما انحفظ التسجيل"), Lang.T("صار خطأ وقت التسجيل. أرسل ملف السجل لو تكرر."));
            }
        }
    }

    /// <summary>
    /// "The last minute" (Kind.Replay): while on, FFmpeg records the primary screen
    /// into a ring of 2-second files; saving joins the newest ones into an MP4.
    /// </summary>
    internal static class Replay
    {
        private const int SegmentSeconds = 2;
        private static FfmpegCapture.Session? _session;
        private static bool _wanted, _starting, _saving;
        private static readonly List<DateTime> Crashes = new();

        public static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus", "replay");

        public static bool IsRunning => _session != null;

        public static void Start()
        {
            _wanted = true;
            if (_session != null || _starting) return;
            _ = StartAsync();
        }

        public static void Stop()
        {
            _wanted = false;
            var s = _session;
            _session = null;
            if (s != null) Task.Run(() => { s.Stop(3000); Clean(); });
        }

        /// <summary>After a setting changed (length, sound, frame rate).</summary>
        public static void Restart()
        {
            if (!_wanted) return;
            var s = _session;
            _session = null;
            if (s != null) Task.Run(() => s.Stop(3000)).ContinueWith(_ => System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => { if (_wanted) Start(); }));
        }

        private static void Clean()
        {
            try { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); } catch { }
        }

        private static async Task StartAsync()
        {
            _starting = true;
            try
            {
                await Task.Run(Clean);
                Directory.CreateDirectory(Folder);
                ScreenCapture.Monitor screen;
                using (new ScreenCapture.DpiScope()) screen = ScreenCapture.PrimaryMonitor();
                int keep = ScreenCapture.ReplayMinutes * 60 / SegmentSeconds + 4;
                string pattern = Path.Combine(Folder, "part%03d.ts");
                var session = await FfmpegCapture.StartAsync(screen,
                    $"-f segment -segment_time {SegmentSeconds} -segment_wrap {keep} -segment_format mpegts -reset_timestamps 0 \"{pattern}\"",
                    background: true);
                if (session == null) return;
                if (!_wanted) { _ = Task.Run(() => session.Stop(3000)); return; }
                _session = session;
                session.Process.EnableRaisingEvents = true;
                session.Process.Exited += (_, _) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => OnExited(session));
            }
            catch (Exception ex)
            {
                CrashLog.Write("replay start", ex);
            }
            finally
            {
                _starting = false;
            }
        }

        /// <summary>FFmpeg quit by itself (screen settings changed, a UAC prompt…): start again,
        /// unless it keeps happening.</summary>
        private static async void OnExited(FfmpegCapture.Session session)
        {
            if (_session != session) return;   // stopped on purpose
            _session = null;
            lock (session.Errors) CrashLog.Note("replay stopped:\r\n" + session.Errors);
            session.Audio?.Dispose();
            var now = DateTime.Now;
            Crashes.RemoveAll(t => (now - t).TotalMinutes > 3);
            Crashes.Add(now);
            if (Crashes.Count >= 5)
            {
                _wanted = false;
                CaptureToast.Show(Lang.T("وقف التسجيل المستمر"), Lang.T("وقف أكثر من مرة. شغّله من جديد من صفحة تصوير الشاشة."));
                return;
            }
            await Task.Delay(3000);
            if (_wanted && _session == null) Start();
        }

        public static async Task SaveAsync()
        {
            if (_saving) return;
            _saving = true;
            try
            {
                string? ffmpeg = Converter.FindFfmpeg();
                if (ffmpeg == null) return;
                int minutes = ScreenCapture.ReplayMinutes;
                string folder = ScreenCapture.VideoFolder;
                Directory.CreateDirectory(folder);
                string file = ScreenCapture.UniquePath(folder, "Replay", ".mp4");
                bool ok = await Task.Run(() => Join(ffmpeg, minutes, file));
                if (ok)
                    CaptureToast.ShowVideo(file, minutes == 1 ? Lang.T("انحفظت آخر دقيقة") : Lang.F("انحفظت آخر {0} دقائق", minutes));
                else
                    CaptureToast.Show(Lang.T("ما انحفظ"), Lang.T("ما فيه تسجيل كفاية للحين، جرّب بعد ثواني"));
            }
            catch (Exception ex)
            {
                CrashLog.Write("replay save", ex);
            }
            finally
            {
                _saving = false;
            }
        }

        /// <summary>Copies the newest parts (FFmpeg may still be writing them) and joins them.</summary>
        private static bool Join(string ffmpeg, int minutes, string file)
        {
            var parts = new DirectoryInfo(Folder).GetFiles("part*.ts")
                .Where(f => f.Length > 0)
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();
            int count = minutes * 60 / SegmentSeconds + 1;   // + the one being written
            parts = parts.Skip(Math.Max(0, parts.Count - count)).ToList();
            if (parts.Count == 0) return false;

            string temp = Path.Combine(Path.GetTempPath(), "WibnReplay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var list = new StringBuilder();
                int n = 0;
                foreach (var part in parts)
                {
                    string copy = Path.Combine(temp, $"{n++:D4}.ts");
                    try
                    {
                        using var src = new FileStream(part.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var dst = File.Create(copy);
                        src.CopyTo(dst);
                    }
                    catch { continue; }
                    list.Append("file '").Append(copy.Replace("\\", "/").Replace("'", "'\\''")).Append("'\n");
                }
                string listFile = Path.Combine(temp, "list.txt");
                File.WriteAllText(listFile, list.ToString());
                var psi = new ProcessStartInfo(ffmpeg,
                    $"-hide_banner -loglevel error -y -f concat -safe 0 -i \"{listFile}\" -c copy -movflags +faststart \"{file}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                };
                using var p = Process.Start(psi)!;
                string errors = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) CrashLog.Note("replay join failed:\r\n" + errors);
                return File.Exists(file) && new FileInfo(file).Length > 0;
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }
    }

    /// <summary>
    /// The PC's sound (whatever the speakers play) into a named pipe that FFmpeg
    /// reads as raw audio. Written at the clock's pace: real sound when there is
    /// some, silence otherwise (loopback sends nothing while nothing plays).
    /// </summary>
    internal sealed class AudioPump : IDisposable
    {
        private readonly WasapiLoopbackCapture _capture;
        private readonly NamedPipeServerStream _pipe;
        private readonly string _pipeName = "wibn-audio-" + Guid.NewGuid().ToString("N");
        private readonly int _rate, _frameBytes;
        private readonly Queue<byte[]> _chunks = new();
        private int _queued, _skip;
        private volatile bool _stopped;

        public string InputArgs { get; }

        public AudioPump()
        {
            _capture = new WasapiLoopbackCapture();
            var f = _capture.WaveFormat;
            _rate = f.SampleRate;
            _frameBytes = f.BlockAlign;
            bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (f is WaveFormatExtensible x && x.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
            string fmt = isFloat ? (f.BitsPerSample == 64 ? "f64le" : "f32le")
                       : f.BitsPerSample switch { 16 => "s16le", 24 => "s24le", _ => "s32le" };
            InputArgs = $"-f {fmt} -ar {_rate} -ac {f.Channels} -i \\\\.\\pipe\\{_pipeName}";
            _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                                              PipeOptions.None, 0, 1 << 20);
            _capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                var copy = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
                lock (_chunks)
                {
                    _chunks.Enqueue(copy);
                    _queued += copy.Length;
                    // FFmpeg isn't reading: keep at most 2 seconds.
                    while (_queued > _rate * _frameBytes * 2 && _chunks.Count > 1) Drop(_chunks.Peek().Length - _skip);
                }
            };
        }

        /// <summary>Call once FFmpeg is started: it connects to the pipe.</summary>
        public void Begin()
        {
            new Thread(Pump) { IsBackground = true, Name = "Wibn audio", Priority = ThreadPriority.AboveNormal }.Start();
        }

        private void Pump()
        {
            try
            {
                _pipe.WaitForConnection();
                if (_stopped) return;
                _capture.StartRecording();
                var clock = Stopwatch.StartNew();
                long written = 0;
                int maxAhead = _rate / 5;     // more queued than 200 ms: drop the oldest to stay in sync
                int padAfter = _rate * 6 / 100;
                var zeros = new byte[_rate * _frameBytes / 10];
                while (!_stopped)
                {
                    Thread.Sleep(10);
                    long need = clock.ElapsedTicks * _rate / Stopwatch.Frequency - written;
                    if (need <= 0) continue;
                    long avail;
                    lock (_chunks)
                    {
                        avail = _queued / _frameBytes;
                        if (avail > need + maxAhead) { Drop((int)(avail - need) * _frameBytes); avail = need; }
                    }
                    long take = Math.Min(need, avail);
                    if (take > 0) { WriteQueued((int)take * _frameBytes); written += take; }
                    long missing = need - take;
                    if (missing > padAfter)
                    {
                        long pad = missing - padAfter / 3;
                        while (pad > 0)
                        {
                            int n = (int)Math.Min(pad, zeros.Length / _frameBytes);
                            _pipe.Write(zeros, 0, n * _frameBytes);
                            pad -= n;
                            written += n;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // FFmpeg closed the pipe or we're stopping.
            }
            catch (Exception ex)
            {
                CrashLog.Write("capture audio pump", ex);
            }
        }

        /// <summary>Drops bytes from the front (caller holds the lock).</summary>
        private void Drop(int bytes)
        {
            while (bytes > 0 && _chunks.Count > 0)
            {
                var c = _chunks.Peek();
                int left = c.Length - _skip;
                if (left <= bytes) { _chunks.Dequeue(); _queued -= left; bytes -= left; _skip = 0; }
                else { _skip += bytes; _queued -= bytes; bytes = 0; }
            }
        }

        private void WriteQueued(int bytes)
        {
            while (bytes > 0)
            {
                byte[] c; int offset, n;
                lock (_chunks)
                {
                    if (_chunks.Count == 0) return;
                    c = _chunks.Peek();
                    offset = _skip;
                    n = Math.Min(bytes, c.Length - _skip);
                    if (n == c.Length - _skip) { _chunks.Dequeue(); _skip = 0; }
                    else _skip += n;
                    _queued -= n;
                }
                _pipe.Write(c, offset, n);
                bytes -= n;
            }
        }

        public void Dispose()
        {
            if (_stopped) return;
            _stopped = true;
            try { _capture.StopRecording(); } catch { }
            try { _capture.Dispose(); } catch { }
            try { _pipe.Dispose(); } catch { }
        }
    }

    /// <summary>Which DXGI output (on the first graphics card, the one ddagrab uses) shows a screen.</summary>
    internal static class Dxgi
    {
        public static int OutputIndex(string device)
        {
            try
            {
                var iid = typeof(IDXGIFactory).GUID;
                if (CreateDXGIFactory1(ref iid, out var factory) != 0) return -1;
                try
                {
                    if (factory.EnumAdapters(0, out var adapter) != 0) return -1;
                    try
                    {
                        for (uint i = 0; adapter.EnumOutputs(i, out var output) == 0; i++)
                        {
                            try
                            {
                                if (output.GetDesc(out var desc) == 0 &&
                                    string.Equals(desc.DeviceName, device, StringComparison.OrdinalIgnoreCase))
                                    return (int)i;
                            }
                            finally { Marshal.ReleaseComObject(output); }
                        }
                    }
                    finally { Marshal.ReleaseComObject(adapter); }
                }
                finally { Marshal.ReleaseComObject(factory); }
            }
            catch (Exception ex)
            {
                CrashLog.Write("dxgi", ex);
            }
            return -1;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public ScreenCapture.RECT DesktopCoordinates;
            public int AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
        }

        // Only the methods we call are real; the others hold their place in the vtable.
        [ComImport, Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            [PreserveSig] int EnumAdapters(uint index, out IDXGIAdapter adapter);
        }

        [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
        }

        [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
        }

        [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory factory);
    }

    /// <summary>Child processes that close with Wibn, even if it crashes (a Windows job object).</summary>
    internal static class KillWithUs
    {
        private static IntPtr _job;

        public static void Add(Process p)
        {
            try
            {
                if (_job == IntPtr.Zero)
                {
                    _job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = 0x2000;   // KILL_ON_JOB_CLOSE
                    int size = Marshal.SizeOf(info);
                    IntPtr ptr = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.StructureToPtr(info, ptr, false);
                        SetInformationJobObject(_job, 9 /* ExtendedLimitInformation */, ptr, (uint)size);
                    }
                    finally { Marshal.FreeHGlobal(ptr); }
                }
                AssignProcessToJobObject(_job, p.Handle);
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount,
                         ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }
}
