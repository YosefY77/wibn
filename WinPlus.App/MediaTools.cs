using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinPlus
{
    /// <summary>
    /// The three round menus you get while dragging a file:
    ///   Space = convert format, R = resize, C = compress, Z = pack into an archive (Archiver).
    /// Resize and compress replace the file itself (the original is kept aside, like
    /// a conversion, so it can be brought back). Compress never makes a file bigger:
    /// if the result isn't smaller, the original stays.
    /// </summary>
    internal static class MediaTools
    {
        public enum Mode { Convert, Resize, Compress, Archive }

        private static readonly string[] WicImage = { ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".tif", ".tiff" };

        // ---- Menu choices ------------------------------------------------------------------

        public static Converter.Target[] OptionsFor(Mode mode, string file)
        {
            var kind = Converter.KindOf(file);
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (mode == Mode.Archive) return Archiver.Options();
            if (mode == Mode.Resize)
            {
                if (kind == Converter.Kind.Image && ext is not (".ico" or ".heic" or ".heif"))
                    return new Converter.Target[]
                    {
                        new("25%", "p:0.25"), new("50%", "p:0.5"), new("75%", "p:0.75"), new("150%", "p:1.5"),
                        new("200%", "p:2"), new("1920px", "w:1920"), new("1280px", "w:1280"), new("800px", "w:800"),
                    };
                if (kind == Converter.Kind.Video)
                    return new Converter.Target[]
                    {
                        new("4K", "h:2160"), new("1440p", "h:1440"), new("1080p", "h:1080"),
                        new("720p", "h:720"), new("480p", "h:480"), new("360p", "h:360"),
                    };
                return Array.Empty<Converter.Target>();
            }

            // Compress
            Converter.Target Lossless = new(Lang.T("بدون فقد"), "q:lossless");
            Converter.Target High = new(Lang.T("عالي"), "q:high"), Medium = new(Lang.T("متوسط"), "q:medium"), Small = new(Lang.T("صغير"), "q:small");
            return kind switch
            {
                Converter.Kind.Image => ext switch
                {
                    ".png" => new[] { Lossless, High, Medium, Small },
                    ".jpg" or ".jpeg" or ".jfif" or ".webp" or ".avif" or ".gif" => new[] { High, Medium, Small },
                    ".tif" or ".tiff" => new[] { Lossless },
                    _ => new[] { new Converter.Target("ZIP", "zip") },
                },
                Converter.Kind.Video => new[] { High, Medium, Small },
                Converter.Kind.Audio => ext is ".wav" or ".aiff" or ".aif" or ".flac"
                    ? new[] { Lossless }
                    : new[] { High, Medium, Small },
                _ => new[] { new Converter.Target("ZIP", "zip") },
            };
        }

        // ---- Run --------------------------------------------------------------------------

        public static Task<string> RunAsync(Mode mode, string file, Converter.Target option) => mode switch
        {
            Mode.Resize => ResizeAsync(file, option.Ext),
            Mode.Compress => CompressAsync(file, option.Ext),
            _ => throw new ArgumentException(nameof(mode)),
        };

        // ---- Resize -----------------------------------------------------------------------

        private static Task<string> ResizeAsync(string file, string code)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            var kind = Converter.KindOf(file);
            return Converter.ReplaceInPlaceAsync(file, Path.GetExtension(file), async temp =>
            {
                if (kind == Converter.Kind.Video)
                {
                    int h = int.Parse(code[2..], CultureInfo.InvariantCulture);
                    await RunPlansAsync(file, temp, VideoPlans(ext, $"scale=-2:'min({h},ih)':flags=lanczos", crf: 21));
                    return;
                }
                if (WicImage.Contains(ext))
                {
                    await Task.Run(() =>
                    {
                        var src = Load(file);
                        var (w, hh) = NewSize(src.PixelWidth, src.PixelHeight, code);
                        Encode(Scale(src, w, hh), ext, temp, quality: 92);
                    });
                    return;
                }
                // WebP / AVIF / GIF (keeps GIF animation): FFmpeg.
                string scale = code[0] == 'p'
                    ? $"scale=trunc(iw*{code[2..]}/2)*2:-2:flags=lanczos"
                    : $"scale='min({code[2..]},iw)':-2:flags=lanczos";
                string[] enc = ext switch
                {
                    ".webp" => new[] { "-c:v", "libwebp", "-quality", "90" },
                    ".avif" => new[] { "-c:v", "libsvtav1", "-crf", "28", "-preset", "8" },
                    _ => Array.Empty<string>(),
                };
                string vf = ext == ".gif" ? scale + ",split[a][b];[a]palettegen[p];[b][p]paletteuse" : scale;
                await Converter.RunFfmpegAsync(new[] { "-i", file, "-vf", vf }.Concat(enc).Append(temp).ToArray(),
                                               null, CancellationToken.None);
            }, onlyIfSmaller: false);
        }

        private static (int w, int h) NewSize(int w, int h, string code)
        {
            double f = code[0] == 'p'
                ? double.Parse(code[2..], CultureInfo.InvariantCulture)
                : Math.Min(1.0, double.Parse(code[2..], CultureInfo.InvariantCulture) / w);  // never enlarge to a width
            return (Math.Max(1, (int)Math.Round(w * f)), Math.Max(1, (int)Math.Round(h * f)));
        }

        // ---- Compress ----------------------------------------------------------------------

        private static Task<string> CompressAsync(string file, string code)
        {
            if (code == "zip") return ZipAsync(file);

            string ext = Path.GetExtension(file).ToLowerInvariant();
            var kind = Converter.KindOf(file);
            string level = code[2..];   // lossless / high / medium / small
            int L = level switch { "high" => 0, "medium" => 1, _ => 2 };
            string newExt = Path.GetExtension(file);
            if (kind == Converter.Kind.Audio && level == "lossless") newExt = ".flac";  // WAV → FLAC: same sound, much smaller

            return Converter.ReplaceInPlaceAsync(file, newExt, async temp =>
            {
                switch (kind)
                {
                    case Converter.Kind.Image when ext is ".jpg" or ".jpeg" or ".jfif":
                        await Task.Run(() => Encode(Load(file), ".jpg", temp, quality: new[] { 85, 75, 60 }[L]));
                        break;
                    case Converter.Kind.Image when ext is ".tif" or ".tiff":
                        await Task.Run(() => Encode(Load(file), ".tiff", temp, quality: 0));
                        break;
                    case Converter.Kind.Image when ext == ".png":
                        string[] png = level == "lossless"
                            ? new[] { "-c:v", "png", "-compression_level", "9", "-pred", "mixed" }
                            : new[] { "-vf", $"split[a][b];[a]palettegen=max_colors={new[] { 256, 128, 64 }[L]}:reserve_transparent=1[p];[b][p]paletteuse=dither=sierra2_4a",
                                      "-c:v", "png", "-compression_level", "9" };
                        await Converter.RunFfmpegAsync(new[] { "-i", file }.Concat(png).Append(temp).ToArray(), null, CancellationToken.None);
                        break;
                    case Converter.Kind.Image when ext == ".webp":
                        await Converter.RunFfmpegAsync(new[] { "-i", file, "-c:v", "libwebp", "-quality", new[] { "80", "70", "55" }[L], temp }, null, CancellationToken.None);
                        break;
                    case Converter.Kind.Image when ext == ".avif":
                        await Converter.RunFfmpegAsync(new[] { "-i", file, "-c:v", "libsvtav1", "-preset", "8", "-crf", new[] { "35", "40", "46" }[L], temp }, null, CancellationToken.None);
                        break;
                    case Converter.Kind.Image when ext == ".gif":
                        await Converter.RunFfmpegAsync(new[] { "-i", file, "-vf", $"split[a][b];[a]palettegen=max_colors={new[] { 128, 64, 32 }[L]}[p];[b][p]paletteuse", temp }, null, CancellationToken.None);
                        break;
                    case Converter.Kind.Video:
                        await RunPlansAsync(file, temp, VideoPlans(ext, null, crf: new[] { 23, 27, 31 }[L]));
                        break;
                    case Converter.Kind.Audio:
                        string[] a = level == "lossless"
                            ? new[] { "-vn", "-c:a", "flac", "-compression_level", "12" }
                            : ext switch
                            {
                                ".mp3" => new[] { "-c:v", "copy", "-c:a", "libmp3lame", "-b:a", new[] { "192k", "128k", "96k" }[L] },
                                ".ogg" => new[] { "-vn", "-c:a", "libvorbis", "-q:a", new[] { "5", "4", "2" }[L] },
                                ".opus" => new[] { "-vn", "-c:a", "libopus", "-b:a", new[] { "128k", "96k", "64k" }[L] },
                                _ => new[] { "-vn", "-c:a", "aac", "-b:a", new[] { "160k", "128k", "96k" }[L] },
                            };
                        await Converter.RunFfmpegAsync(new[] { "-i", file }.Concat(a).Append(temp).ToArray(), null, CancellationToken.None);
                        break;
                    default:
                        throw new NotSupportedException(ext);
                }
            }, onlyIfSmaller: true);
        }

        /// <summary>Any other file: a .zip next to it (the original stays).</summary>
        private static Task<string> ZipAsync(string file) => Task.Run(() =>
        {
            string zip = Converter.UniquePath(Path.ChangeExtension(file, ".zip"));
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                archive.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.SmallestSize);
            if (new FileInfo(zip).Length >= new FileInfo(file).Length)
            {
                Converter.TryDelete(zip);
                throw new Converter.NotSmallerException();
            }
            return zip;
        });

        // ---- Video ---------------------------------------------------------------------------

        /// <summary>H.264 on the graphics card first (NVIDIA, Intel, AMD), then the processor.
        /// Audio is copied when possible.</summary>
        private static IEnumerable<string[]> VideoPlans(string ext, string? filter, int crf)
        {
            string[] vf = filter != null ? new[] { "-vf", filter } : Array.Empty<string>();
            string[] map = { "-map", "0:v:0?", "-map", "0:a:0?" };
            string[] extra = ext is ".mp4" or ".mov" ? new[] { "-movflags", "+faststart" } : Array.Empty<string>();
            if (ext == ".webm")
            {
                foreach (var a in new[] { new[] { "-c:a", "copy" }, new[] { "-c:a", "libopus", "-b:a", "128k" } })
                    yield return map.Concat(vf).Concat(new[] { "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8",
                        "-row-mt", "1", "-crf", (crf + 12).ToString(), "-b:v", "0" }).Concat(a).ToArray();
                yield break;
            }
            if (ext == ".avi")
            {
                yield return map.Concat(vf).Concat(new[] { "-c:v", "mpeg4", "-q:v", Math.Max(2, crf / 5 - 1).ToString(), "-c:a", "libmp3lame", "-q:a", "4" }).ToArray();
                yield break;
            }
            string q = crf.ToString(CultureInfo.InvariantCulture);
            var encoders = new[]
            {
                new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", q, "-b:v", "0" },
                new[] { "-c:v", "h264_qsv", "-preset", "medium", "-global_quality", q },
                new[] { "-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", q, "-qp_p", q },
                new[] { "-c:v", "libx264", "-preset", "faster", "-crf", q, "-pix_fmt", "yuv420p" },
            };
            foreach (var audio in new[] { new[] { "-c:a", "copy" }, new[] { "-c:a", "aac", "-b:a", "160k" } })
                foreach (var enc in encoders)
                    yield return map.Concat(vf).Concat(enc).Concat(audio).Concat(extra).ToArray();
        }

        private static async Task RunPlansAsync(string input, string temp, IEnumerable<string[]> plans)
        {
            Exception? last = null;
            foreach (var plan in plans)
            {
                try
                {
                    await Converter.RunFfmpegAsync(new[] { "-i", input }.Concat(plan).Append(temp).ToArray(), null, CancellationToken.None);
                    return;
                }
                catch (Converter.FfmpegMissingException) { throw; }
                catch (Exception ex) { last = ex; Converter.TryDelete(temp); }
            }
            throw last ?? new InvalidOperationException("No way to do this");
        }

        // ---- Images with Windows' codecs ----------------------------------------------------

        private static BitmapSource Load(string file)
        {
            using var fs = File.OpenRead(file);
            var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            return frame;
        }

        private static BitmapSource Scale(BitmapSource src, int w, int h)
        {
            var v = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(v, BitmapScalingMode.HighQuality);
            using (var dc = v.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, w, h));
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(v);
            bmp.Freeze();
            return bmp;
        }

        private static void Encode(BitmapSource img, string ext, string output, int quality)
        {
            BitmapEncoder enc = ext switch
            {
                ".jpg" or ".jpeg" or ".jfif" => new JpegBitmapEncoder { QualityLevel = quality },
                ".bmp" => new BmpBitmapEncoder(),
                ".tif" or ".tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
                _ => new PngBitmapEncoder(),
            };
            if (enc is JpegBitmapEncoder or BmpBitmapEncoder) img = Converter.Flatten(img);
            enc.Frames.Add(BitmapFrame.Create(img));
            using var fs = new FileStream(output, FileMode.CreateNew);
            enc.Save(fs);
        }
    }
}
