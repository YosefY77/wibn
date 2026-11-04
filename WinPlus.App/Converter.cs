using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinPlus
{
    /// <summary>
    /// Converts files to formats of the same kind (image → image/PDF, video → video/audio,
    /// audio → audio).
    ///   * Common images (PNG, JPG, BMP, TIFF, GIF, ICO, PDF) use Windows' own image
    ///     codecs: nothing extra needed.
    ///   * Video, audio, WebP and AVIF use FFmpeg (free). It's found on the PATH or
    ///     downloaded once into %LOCALAPPDATA%\WinPlus\ffmpeg when first needed.
    /// The result goes next to the original ("name.webp", or "name (1).webp" if taken).
    /// </summary>
    internal static class Converter
    {
        public enum Kind { Image, Video, Audio }

        public sealed record Target(string Label, string Ext, string? Note = null);

        private static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".avif", ".jfif" };
        private static readonly string[] VideoExt = { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".flv", ".m4v", ".3gp", ".mpg", ".mpeg", ".ts" };
        private static readonly string[] AudioExt = { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma", ".opus", ".aiff", ".aif" };

        private static readonly Target[] ImageTargets =
        {
            new("PNG", ".png"), new("JPG", ".jpg"), new("WEBP", ".webp"), new("AVIF", ".avif"),
            new("PDF", ".pdf"), new("ICO", ".ico"), new("BMP", ".bmp"), new("TIFF", ".tiff"), new("GIF", ".gif"),
        };
        private static readonly Target[] VideoTargets =
        {
            new("MP4", ".mp4"), new("WEBM", ".webm"), new("MKV", ".mkv"), new("MOV", ".mov"),
            new("AVI", ".avi"), new("GIF", ".gif"), new("MP3", ".mp3"), new("WAV", ".wav"),
        };
        private static readonly Target[] AudioTargets =
        {
            new("MP3", ".mp3"), new("WAV", ".wav"), new("FLAC", ".flac"), new("M4A", ".m4a"),
            new("OGG", ".ogg"), new("OPUS", ".opus"),
        };

        public static Kind? KindOf(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ImageExt.Contains(ext)) return Kind.Image;
            if (VideoExt.Contains(ext)) return Kind.Video;
            if (AudioExt.Contains(ext)) return Kind.Audio;
            return null;
        }

        public static string KindName(Kind k) => k switch
        {
            Kind.Image => Lang.T("صورة"),
            Kind.Video => Lang.T("مقطع"),
            _ => Lang.T("صوت"),
        };

        /// <summary>Formats a file can become (its own format left out).</summary>
        public static Target[] TargetsFor(Kind kind, string sample)
        {
            string ext = Normalize(Path.GetExtension(sample));
            var all = kind switch { Kind.Image => ImageTargets, Kind.Video => VideoTargets, _ => AudioTargets };
            return all.Where(t => Normalize(t.Ext) != ext).ToArray();
        }

        private static string Normalize(string ext) => ext.ToLowerInvariant() switch
        {
            ".jpeg" or ".jfif" => ".jpg",
            ".tif" => ".tiff",
            ".heif" => ".heic",
            var e => e,
        };

        public static bool NeedsFfmpeg(Kind kind, Target target, string source)
        {
            if (kind != Kind.Image) return true;
            if (target.Ext is ".webp" or ".avif") return true;
            // Windows can't always read these itself; FFmpeg is the fallback.
            return false;
        }

        // ---- Convert ----------------------------------------------------------------

        /// <summary>The kind to offer formats for: a file converted before counts as its
        /// original (a song made from a video can become a video again).</summary>
        public static Kind? EffectiveKind(string path)
        {
            var orig = ConvertHistory.OriginalOf(path);
            return orig != null ? KindOf(orig.Backup) ?? KindOf(path) : KindOf(path);
        }

        /// <summary>
        /// Converts the file itself: the result takes its place (same name, new
        /// extension) and the original is kept aside (see ConvertHistory).
        /// </summary>
        public static async Task<string> ConvertAsync(string source, Kind kind, Target target,
                                                      IProgress<double>? progress, CancellationToken cancel)
        {
            progress = Throttle(progress);
            var orig = ConvertHistory.OriginalOf(source);
            string input = orig?.Backup ?? source;   // always start from the best original
            string dir = Path.GetDirectoryName(source)!;
            string name = Path.GetFileNameWithoutExtension(source);
            string output = UniquePath(Path.Combine(dir, name + target.Ext));

            // Back to the original format: just put the original back (instant).
            if (orig != null && Normalize(orig.Ext) == Normalize(target.Ext))
            {
                await Task.Run(() =>
                {
                    File.Delete(source);
                    File.Move(orig.Backup, output);
                }, cancel);
                ConvertHistory.Remove(orig);
                progress?.Report(1);
                return output;
            }

            // Work into a hidden temp file next to it, then swap.
            string temp = Path.Combine(dir, $".{name}.wpconv{target.Ext}");
            TryDelete(temp);
            try
            {
                await ProduceAsync(input, target, temp, progress, cancel);
                await Task.Run(() =>
                {
                    if (orig == null) ConvertHistory.Keep(source, output);
                    else { File.Delete(source); ConvertHistory.Update(orig, output); }
                    File.Move(temp, output);
                    File.SetAttributes(output, File.GetAttributes(output) & ~FileAttributes.Hidden);
                }, CancellationToken.None);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
            progress?.Report(1);
            return output;
        }

        /// <summary>The result would be as big or bigger: the original is kept as it is.</summary>
        public sealed class NotSmallerException : Exception
        {
            public NotSmallerException() : base("Not smaller") { }
        }

        /// <summary>
        /// Shared by convert / resize / compress: <paramref name="produce"/> writes the new
        /// file to a hidden temp path; then it takes the original's place (same name,
        /// <paramref name="newExt"/>) and the original is kept aside like a conversion
        /// (ConvertHistory), so it can always be brought back.
        /// </summary>
        public static async Task<string> ReplaceInPlaceAsync(string source, string newExt, Func<string, Task> produce,
                                                             bool onlyIfSmaller)
        {
            var orig = ConvertHistory.OriginalOf(source);
            string dir = Path.GetDirectoryName(source)!;
            string name = Path.GetFileNameWithoutExtension(source);
            bool sameExt = string.Equals(Path.GetExtension(source), newExt, StringComparison.OrdinalIgnoreCase);
            string output = sameExt ? source : UniquePath(Path.Combine(dir, name + newExt));
            string temp = Path.Combine(dir, $".{name}.wpconv{newExt}");
            TryDelete(temp);
            try
            {
                await produce(temp);
                if (onlyIfSmaller && new FileInfo(temp).Length >= new FileInfo(source).Length)
                    throw new NotSmallerException();
                await Task.Run(() =>
                {
                    if (orig == null) ConvertHistory.Keep(source, output);
                    else { File.Delete(source); ConvertHistory.Update(orig, output); }
                    File.Move(temp, output);
                    File.SetAttributes(output, File.GetAttributes(output) & ~FileAttributes.Hidden);
                });
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
            return output;
        }

        private static async Task ProduceAsync(string input, Target target, string temp,
                                               IProgress<double>? progress, CancellationToken cancel)
        {
            bool imageIn = KindOf(input) == Kind.Image;
            if (imageIn && target.Ext is not (".webp" or ".avif"))
            {
                try
                {
                    await Task.Run(() => ConvertImage(input, temp, target.Ext), cancel);
                    return;
                }
                catch (NotSupportedException)
                {
                    // Windows can't read this one (e.g. HEIC without the extension):
                    // FFmpeg decodes it to PNG first.
                    string png = Path.Combine(Path.GetTempPath(), $"winplus-{Guid.NewGuid():N}.png");
                    try
                    {
                        await RunFfmpegAsync(new[] { "-i", input, "-frames:v", "1", png }, null, cancel);
                        await Task.Run(() => ConvertImage(png, temp, target.Ext), cancel);
                        return;
                    }
                    finally { TryDelete(png); }
                }
            }

            // FFmpeg: try the quickest way first (just re-packing the streams, no
            // re-encoding: seconds even for long videos), then the graphics card's
            // encoder, then the processor.
            var info = await ProbeAsync(input, cancel);
            Exception? last = null;
            foreach (var plan in Plans(info, target.Ext, imageIn))
            {
                cancel.ThrowIfCancellationRequested();
                var args = new List<string> { "-i", input };
                args.AddRange(plan);
                args.Add(temp);
                try
                {
                    await RunFfmpegAsync(args.ToArray(), progress, cancel);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (FfmpegMissingException) { throw; }
                catch (Exception ex) { last = ex; TryDelete(temp); }
            }
            throw last ?? new InvalidOperationException("No way to convert this");
        }

        // ---- Plans ---------------------------------------------------------------------

        private sealed record StreamInfo(string? Video, string? Audio);

        /// <summary>Which codecs the file has ("h264", "aac" …), read from FFmpeg's output.</summary>
        private static async Task<StreamInfo> ProbeAsync(string input, CancellationToken cancel)
        {
            string exe = FindFfmpeg() ?? throw new FfmpegMissingException();
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-hide_banner", "-nostdin", "-i", input }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string err = await p.StandardError.ReadToEndAsync(cancel);
            await p.WaitForExitAsync(cancel);
            string? video = null, audio = null;
            foreach (var line in err.Split('\n'))
            {
                int v = line.IndexOf("Video: ", StringComparison.Ordinal);
                if (v >= 0 && video == null && !line.Contains("attached pic")) video = Codec(line, v + 7);
                int a = line.IndexOf("Audio: ", StringComparison.Ordinal);
                if (a >= 0 && audio == null) audio = Codec(line, a + 7);
            }
            return new StreamInfo(video, audio);
        }

        private static string Codec(string line, int start)
        {
            int end = start;
            while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_')) end++;
            return line[start..end].ToLowerInvariant();
        }

        private static readonly Dictionary<string, string[]> VideoCopyOk = new()
        {
            [".mp4"] = new[] { "h264", "hevc", "av1", "mpeg4" },
            [".mov"] = new[] { "h264", "hevc", "mpeg4", "prores", "mjpeg" },
            [".webm"] = new[] { "vp8", "vp9", "av1" },
            [".avi"] = new[] { "mpeg4", "h264", "mjpeg", "msmpeg4v3" },
        };

        private static readonly Dictionary<string, string[]> AudioCopyOk = new()
        {
            [".mp4"] = new[] { "aac", "mp3", "alac", "ac3", "eac3" },
            [".mov"] = new[] { "aac", "mp3", "alac", "ac3", "pcm_s16le" },
            [".webm"] = new[] { "opus", "vorbis" },
            [".avi"] = new[] { "mp3", "ac3", "pcm_s16le" },
            [".mp3"] = new[] { "mp3" },
            [".m4a"] = new[] { "aac", "alac" },
            [".ogg"] = new[] { "vorbis", "opus" },
            [".opus"] = new[] { "opus" },
            [".flac"] = new[] { "flac" },
            [".wav"] = new[] { "pcm_s16le", "pcm_s24le", "pcm_f32le" },
        };

        private static bool CanCopy(Dictionary<string, string[]> table, string ext, string? codec) =>
            codec != null && (ext == ".mkv" || (table.TryGetValue(ext, out var ok) && ok.Contains(codec)));

        private static IEnumerable<string[]> Plans(StreamInfo info, string ext, bool imageIn)
        {
            if (imageIn)
            {
                if (ext == ".webp")
                    yield return new[] { "-frames:v", "1", "-c:v", "libwebp", "-quality", "90" };
                else if (ext == ".avif")
                {
                    yield return new[] { "-frames:v", "1", "-c:v", "libsvtav1", "-preset", "8", "-crf", "30" };
                    yield return new[] { "-frames:v", "1", "-c:v", "libaom-av1", "-still-picture", "1", "-crf", "30", "-cpu-used", "8" };
                }
                yield break;
            }

            string[] audioEnc = ext switch
            {
                ".mp3" or ".avi" => new[] { "libmp3lame", "-q:a", "2" },
                ".wav" => new[] { "pcm_s16le" },
                ".flac" => new[] { "flac" },
                ".ogg" => new[] { "libvorbis", "-q:a", "5" },
                ".opus" or ".webm" => new[] { "libopus", "-b:a", "128k" },
                _ => new[] { "aac", "-b:a", "192k" },
            };

            // Audio only.
            if (ext is ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" or ".opus")
            {
                if (CanCopy(AudioCopyOk, ext, info.Audio))
                    yield return new[] { "-vn", "-map", "0:a:0", "-c:a", "copy" };
                yield return new[] { "-vn", "-map", "0:a:0", "-c:a" }.Concat(audioEnc).ToArray();
                yield break;
            }

            if (ext == ".gif")
            {
                yield return new[] { "-an", "-vf", "fps=15,scale='min(iw,480)':-2:flags=bicubic,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse", "-loop", "0" };
                yield break;
            }

            // Video: keep what fits, re-encode only what doesn't.
            string[] audio = info.Audio == null ? new[] { "-an" }
                : CanCopy(AudioCopyOk, ext, info.Audio) ? new[] { "-c:a", "copy" }
                : new[] { "-c:a" }.Concat(audioEnc).ToArray();
            string[] map = { "-map", "0:v:0?", "-map", "0:a:0?" };
            string[] extra = ext is ".mp4" or ".mov" ? new[] { "-movflags", "+faststart" } : Array.Empty<string>();
            IEnumerable<string> Build(params string[] video) => map.Concat(video).Concat(audio).Concat(extra);

            if (CanCopy(VideoCopyOk, ext, info.Video))
                yield return Build("-c:v", "copy").ToArray();

            if (ext == ".webm")
            {
                yield return Build("-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-row-mt", "1", "-crf", "34", "-b:v", "0").ToArray();
                yield break;
            }
            if (ext == ".avi")
            {
                yield return Build("-c:v", "mpeg4", "-q:v", "3").ToArray();
                yield break;
            }
            // MP4 / MOV / MKV → H.264: graphics card first (NVIDIA, Intel, AMD), then the processor.
            yield return Build("-c:v", "h264_nvenc", "-preset", "p2", "-cq", "23").ToArray();
            yield return Build("-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "23").ToArray();
            yield return Build("-c:v", "h264_amf", "-quality", "speed", "-rc", "cqp", "-qp_i", "22", "-qp_p", "22").ToArray();
            yield return Build("-c:v", "libx264", "-preset", "ultrafast", "-crf", "22", "-pix_fmt", "yuv420p").ToArray();
        }

        /// <summary>At most ~20 updates a second reach the screen (more only clog the UI).</summary>
        internal static IProgress<double>? Throttle(IProgress<double>? inner) =>
            inner == null ? null : new ThrottledProgress(inner);

        private sealed class ThrottledProgress : IProgress<double>
        {
            private readonly IProgress<double> _inner;
            private long _last;
            public ThrottledProgress(IProgress<double> inner) => _inner = inner;
            public void Report(double value)
            {
                long now = Environment.TickCount64;
                if (value < 1 && now - Interlocked.Read(ref _last) < 50) return;
                Interlocked.Exchange(ref _last, now);
                _inner.Report(value);
            }
        }

        internal static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ---- Images with Windows' codecs ----------------------------------------------

        private static void ConvertImage(string source, string output, string ext)
        {
            BitmapSource frame;
            try
            {
                using var fs = File.OpenRead(source);
                var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                frame = decoder.Frames[0];
                frame.Freeze();
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException
                                          or System.Runtime.InteropServices.COMException)
            {
                throw new NotSupportedException("Windows can't read this image", ex);
            }

            switch (ext)
            {
                case ".png": Save(new PngBitmapEncoder(), frame, output); break;
                case ".bmp": Save(new BmpBitmapEncoder(), Flatten(frame), output); break;
                case ".tiff": Save(new TiffBitmapEncoder(), frame, output); break;
                case ".gif": Save(new GifBitmapEncoder(), frame, output); break;
                case ".jpg": Save(new JpegBitmapEncoder { QualityLevel = 90 }, Flatten(frame), output); break;
                case ".ico": File.WriteAllBytes(output, ToIco(frame)); break;
                case ".pdf": File.WriteAllBytes(output, ToPdf(frame)); break;
                default: throw new NotSupportedException(ext);
            }
        }

        private static void Save(BitmapEncoder encoder, BitmapSource frame, string output)
        {
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var fs = new FileStream(output, FileMode.CreateNew);
            encoder.Save(fs);
        }

        /// <summary>JPG/BMP have no transparency: put the picture on white.</summary>
        internal static BitmapSource Flatten(BitmapSource src)
        {
            var v = new DrawingVisual();
            using (var dc = v.RenderOpen())
            {
                var r = new Rect(0, 0, src.PixelWidth, src.PixelHeight);
                dc.DrawRectangle(Brushes.White, null, r);
                dc.DrawImage(src, r);
            }
            var bmp = new RenderTargetBitmap(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(v);
            bmp.Freeze();
            return bmp;
        }

        private static BitmapSource Resize(BitmapSource src, int size)
        {
            // Fit in a square, centered, keeping its shape.
            double scale = Math.Min((double)size / src.PixelWidth, (double)size / src.PixelHeight);
            double w = src.PixelWidth * scale, h = src.PixelHeight * scale;
            var v = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(v, BitmapScalingMode.HighQuality);
            using (var dc = v.RenderOpen())
                dc.DrawImage(src, new Rect((size - w) / 2, (size - h) / 2, w, h));
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(v);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>An icon with the usual sizes (PNG inside, like Windows' own icons).</summary>
        private static byte[] ToIco(BitmapSource src)
        {
            int[] sizes = { 256, 128, 64, 48, 32, 16 };
            var images = sizes.Select(s =>
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(Resize(src, s)));
                using var ms = new MemoryStream();
                enc.Save(ms);
                return ms.ToArray();
            }).ToArray();

            using var outMs = new MemoryStream();
            using var bw = new BinaryWriter(outMs);
            bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((ushort)1); bw.Write((ushort)32);
                bw.Write(images[i].Length); bw.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) bw.Write(img);
            bw.Flush();
            return outMs.ToArray();
        }

        /// <summary>A one-page PDF with the picture (as JPEG) filling the page.</summary>
        private static byte[] ToPdf(BitmapSource src)
        {
            var flat = Flatten(src);
            var enc = new JpegBitmapEncoder { QualityLevel = 92 };
            enc.Frames.Add(BitmapFrame.Create(flat));
            byte[] jpg;
            using (var ms = new MemoryStream()) { enc.Save(ms); jpg = ms.ToArray(); }

            int w = flat.PixelWidth, h = flat.PixelHeight;
            // Page size in points: 72 per inch at the picture's DPI (96 if unknown).
            double dpi = src.DpiX > 1 ? src.DpiX : 96;
            string pw = (w * 72.0 / dpi).ToString("0.##", CultureInfo.InvariantCulture);
            string ph = (h * 72.0 / dpi).ToString("0.##", CultureInfo.InvariantCulture);
            string content = $"q {pw} 0 0 {ph} 0 0 cm /Im0 Do Q";

            using var pdf = new MemoryStream();
            var offsets = new List<long>();
            void Text(string s) { var b = Encoding.ASCII.GetBytes(s); pdf.Write(b, 0, b.Length); }
            void Obj(string body) { offsets.Add(pdf.Position); Text(body); }

            Text("%PDF-1.4\n");
            Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pw} {ph}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
            offsets.Add(pdf.Position);
            Text($"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpg.Length} >>\nstream\n");
            pdf.Write(jpg, 0, jpg.Length);
            Text("\nendstream\nendobj\n");
            Obj($"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
            long xref = pdf.Position;
            Text($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
            foreach (var o in offsets) Text($"{o:D10} 00000 n \n");
            Text($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return pdf.ToArray();
        }

        // ---- FFmpeg -------------------------------------------------------------------------

        public static string FfmpegFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus", "ffmpeg");

        /// <summary>ffmpeg.exe if we have one (ours, on the PATH, or from winget).</summary>
        public static string? FindFfmpeg()
        {
            string ours = Path.Combine(FfmpegFolder, "ffmpeg.exe");
            if (File.Exists(ours)) return ours;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (dir.Length > 0 && File.Exists(p)) return p;
                }
                catch { }
            }
            try
            {
                string winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                             "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(winget))
                {
                    foreach (var d in Directory.EnumerateDirectories(winget, "*FFmpeg*"))
                    {
                        var exe = Directory.EnumerateFiles(d, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                        if (exe != null) return exe;
                    }
                }
            }
            catch { }
            return null;
        }

        // A full build (has WebP, AVIF, VP9, Opus, MP3 …). About 140 MB, downloaded once.
        private const string FfmpegUrl =
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

        /// <summary>Downloads FFmpeg into our folder (only ffmpeg.exe is kept).</summary>
        public static async Task DownloadFfmpegAsync(IProgress<double>? progress, CancellationToken cancel)
        {
            progress = Throttle(progress);
            Directory.CreateDirectory(FfmpegFolder);
            string zip = Path.Combine(FfmpegFolder, "download.zip");
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
                using (var resp = await http.GetAsync(FfmpegUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? 0;
                    using var input = await resp.Content.ReadAsStreamAsync(cancel);
                    using var file = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                    byte[] buffer = new byte[1 << 16];
                    long done = 0;
                    int n;
                    while ((n = await input.ReadAsync(buffer, cancel)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, n), cancel);
                        done += n;
                        if (total > 0) progress?.Report(0.95 * done / total);
                    }
                }
                await Task.Run(() =>
                {
                    using var archive = ZipFile.OpenRead(zip);
                    var entry = archive.Entries.FirstOrDefault(e =>
                        e.FullName.EndsWith("bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("ffmpeg.exe not found in the download");
                    entry.ExtractToFile(Path.Combine(FfmpegFolder, "ffmpeg.exe"), true);
                }, cancel);
                progress?.Report(1);
            }
            finally
            {
                try { File.Delete(zip); } catch { }
            }
        }

        public sealed class FfmpegMissingException : Exception
        {
            public FfmpegMissingException() : base("FFmpeg is needed") { }
        }

        internal static async Task RunFfmpegAsync(string[] args, IProgress<double>? progress, CancellationToken cancel)
        {
            string exe = FindFfmpeg() ?? throw new FfmpegMissingException();
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in new[] { "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats" }) psi.ArgumentList.Add(a);
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg didn't start");
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }  // keep the PC responsive
            double duration = 0;
            var errors = new StringBuilder();
            var errTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await p.StandardError.ReadLineAsync()) != null)
                {
                    if (errors.Length < 4000) errors.AppendLine(line);
                    int i = line.IndexOf("Duration: ", StringComparison.Ordinal);
                    if (i >= 0 && duration == 0 && line.Length >= i + 21 &&
                        TimeSpan.TryParse(line.Substring(i + 10, 11), CultureInfo.InvariantCulture, out var d))
                        duration = d.TotalSeconds;
                }
            });
            var outTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await p.StandardOutput.ReadLineAsync()) != null)
                {
                    if (line.StartsWith("out_time_us=") && duration > 0 &&
                        long.TryParse(line.AsSpan(12), out long us))
                        progress?.Report(Math.Clamp(us / 1e6 / duration, 0, 1));
                }
            });

            using (cancel.Register(() => { try { p.Kill(true); } catch { } }))
            {
                await p.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(errTask, outTask);
            }
            cancel.ThrowIfCancellationRequested();
            if (p.ExitCode != 0)
            {
                CrashLog.Note("ffmpeg failed: " + string.Join(' ', args) + "\r\n" + errors);
                throw new InvalidOperationException("FFmpeg failed (" + p.ExitCode + ")");
            }
            progress?.Report(1);
        }

        internal static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path)!;
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                string p = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(p)) return p;
            }
        }
    }
}
