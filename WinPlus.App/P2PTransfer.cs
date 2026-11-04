using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WinPlus
{
    /// <summary>
    /// Sends files between two PCs over the internet, directly (peer to peer).
    ///
    /// The heavy lifting is done by "sendme" (open source, by the iroh project,
    /// MIT/Apache): it connects the two PCs directly through their routers (hole
    /// punching) and only falls back to a public relay when that's impossible.
    /// Everything is encrypted (TLS), checked (BLAKE3) and resumes after a
    /// dropped connection. No size limit. The sender has to stay online until the
    /// receiver is done.
    ///
    /// sendme.exe (a few MB) is downloaded once from its GitHub releases into
    /// %LOCALAPPDATA%\WinPlus\sendme.
    ///
    /// The code we show is "wp:SIZE:TICKET": sendme's ticket plus the total size,
    /// so the receiver can show real progress. A plain sendme ticket works too.
    /// </summary>
    internal static class P2PTransfer
    {
        public static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus", "sendme");

        public static string ExePath => Path.Combine(Folder, "sendme.exe");

        public static bool IsInstalled => File.Exists(ExePath);

        private static readonly List<Process> _running = new();

        /// <summary>Stops every transfer (when WinPlus closes).</summary>
        public static void StopAll()
        {
            lock (_running)
            {
                foreach (var p in _running) { try { if (!p.HasExited) p.Kill(true); } catch { } }
                _running.Clear();
            }
        }

        // ---- Install -----------------------------------------------------------------------

        /// <summary>Downloads the latest sendme for Windows from GitHub.</summary>
        public static async Task InstallAsync(IProgress<double>? progress, CancellationToken cancel)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WinPlus");
            string json = await http.GetStringAsync("https://api.github.com/repos/n0-computer/sendme/releases/latest", cancel);
            using var doc = JsonDocument.Parse(json);
            var assets = doc.RootElement.GetProperty("assets").EnumerateArray()
                .Select(a => (name: a.GetProperty("name").GetString() ?? "", url: a.GetProperty("browser_download_url").GetString() ?? ""))
                .ToList();
            var asset = assets.FirstOrDefault(a =>
                a.name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                (a.name.Contains("x86_64", StringComparison.OrdinalIgnoreCase) || a.name.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
                 a.name.Contains("amd64", StringComparison.OrdinalIgnoreCase)));
            if (asset.url == null || asset.url.Length == 0)
                throw new InvalidOperationException("No Windows build in the latest sendme release: " + string.Join(", ", assets.Select(a => a.name)));

            Directory.CreateDirectory(Folder);
            string download = Path.Combine(Folder, "download" + (asset.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ".exe" : ".pkg"));
            try
            {
                using (var resp = await http.GetAsync(asset.url, HttpCompletionOption.ResponseHeadersRead, cancel))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? 0;
                    using var input = await resp.Content.ReadAsStreamAsync(cancel);
                    using var file = File.Create(download);
                    byte[] buffer = new byte[1 << 16];
                    long done = 0, lastReport = 0;
                    int n;
                    while ((n = await input.ReadAsync(buffer, cancel)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, n), cancel);
                        done += n;
                        if (total > 0 && done - lastReport > total / 50) { lastReport = done; progress?.Report((double)done / total); }
                    }
                }

                if (asset.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(download, ExePath, true);
                }
                else if (asset.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var zip = ZipFile.OpenRead(download);
                    var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("sendme.exe", StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidDataException("sendme.exe not in the zip");
                    entry.ExtractToFile(ExePath, true);
                }
                else
                {
                    // .tar.gz
                    using var fs = File.OpenRead(download);
                    using var gz = new GZipStream(fs, CompressionMode.Decompress);
                    using var tar = new System.Formats.Tar.TarReader(gz);
                    System.Formats.Tar.TarEntry? e;
                    bool found = false;
                    while ((e = tar.GetNextEntry()) != null)
                    {
                        if (Path.GetFileName(e.Name).Equals("sendme.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            e.ExtractToFile(ExePath, true);
                            found = true;
                            break;
                        }
                    }
                    if (!found) throw new InvalidDataException("sendme.exe not in the archive");
                }
                progress?.Report(1);
            }
            finally
            {
                try { File.Delete(download); } catch { }
            }
        }

        // ---- Send --------------------------------------------------------------------------

        public sealed class Sending
        {
            public string Code = "";
            public Process Process = null!;
            public void Stop()
            {
                try { if (!Process.HasExited) Process.Kill(true); } catch { }
                lock (_running) _running.Remove(Process);
            }
        }

        // iroh tickets are long base32 strings starting with "blob".
        private static readonly Regex TicketRx = new(@"\b(blob[a-z0-9]{40,})\b", RegexOptions.Compiled);

        /// <summary>Starts sharing a file or folder; returns once the code is ready.</summary>
        public static async Task<Sending> SendAsync(string path, CancellationToken cancel)
        {
            long size = File.Exists(path) ? new FileInfo(path).Length
                : Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

            // sendme keeps a temp folder in its working directory: use ours, not the user's.
            string work = Path.Combine(Folder, "work");
            Directory.CreateDirectory(work);
            var psi = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = work,
            };
            psi.ArgumentList.Add("send");
            psi.ArgumentList.Add(path);
            var p = Process.Start(psi) ?? throw new InvalidOperationException("sendme didn't start");
            lock (_running) _running.Add(p);

            var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var log = new System.Text.StringBuilder();
            void Watch(StreamReader reader) => _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    lock (log) if (log.Length < 4000) log.AppendLine(line);
                    var m = TicketRx.Match(line);
                    if (m.Success) found.TrySetResult(m.Groups[1].Value);
                }
            });
            Watch(p.StandardOutput);
            Watch(p.StandardError);
            _ = p.WaitForExitAsync().ContinueWith(_ =>
                found.TrySetException(new InvalidOperationException("sendme stopped: " + log)));

            using (cancel.Register(() => found.TrySetCanceled()))
            {
                try
                {
                    string ticket = await found.Task.WaitAsync(TimeSpan.FromMinutes(5));
                    return new Sending { Code = $"wp:{size}:{ticket}", Process = p };
                }
                catch
                {
                    try { p.Kill(true); } catch { }
                    lock (_running) _running.Remove(p);
                    throw;
                }
            }
        }

        // ---- Receive -------------------------------------------------------------------------

        public sealed record TransferProgress(long Bytes, long Total, double BytesPerSecond);

        /// <summary>Takes "wp:SIZE:TICKET" (or a plain ticket). Returns null if it's not a code.</summary>
        public static (string ticket, long size)? ParseCode(string text)
        {
            text = text.Trim();
            var m = Regex.Match(text, @"^wp:(\d+):(blob[a-z0-9]+)$");
            if (m.Success) return (m.Groups[2].Value, long.Parse(m.Groups[1].Value));
            var t = TicketRx.Match(text);
            return t.Success ? (t.Groups[1].Value, 0) : null;
        }

        /// <summary>Downloads into <paramref name="folder"/>. Returns the new files/folders there.</summary>
        public static async Task<string[]> ReceiveAsync(string ticket, long size, string folder,
                                                        IProgress<TransferProgress>? progress, CancellationToken cancel)
        {
            Directory.CreateDirectory(folder);
            var before = new HashSet<string>(Directory.EnumerateFileSystemEntries(folder), StringComparer.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = folder,
            };
            psi.ArgumentList.Add("receive");
            psi.ArgumentList.Add(ticket);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("sendme didn't start");
            lock (_running) _running.Add(p);
            var log = new System.Text.StringBuilder();
            var readOut = Task.Run(async () => { string? l; while ((l = await p.StandardOutput.ReadLineAsync()) != null) lock (log) if (log.Length < 4000) log.AppendLine(l); });
            var readErr = Task.Run(async () => { string? l; while ((l = await p.StandardError.ReadLineAsync()) != null) lock (log) if (log.Length < 4000) log.AppendLine(l); });

            try
            {
                // Progress: how much sendme's temp folder (".sendme-…") holds so far.
                long last = 0;
                var lastTime = DateTime.Now;
                double speed = 0;
                while (!p.HasExited)
                {
                    await Task.WhenAny(p.WaitForExitAsync(cancel), Task.Delay(500, cancel));
                    cancel.ThrowIfCancellationRequested();
                    long now = TempBytes(folder);
                    var t = DateTime.Now;
                    double secs = (t - lastTime).TotalSeconds;
                    if (secs > 0.2 && now >= last)
                    {
                        double instant = (now - last) / secs;
                        speed = speed == 0 ? instant : speed * 0.7 + instant * 0.3;  // smooth it
                        last = now;
                        lastTime = t;
                    }
                    progress?.Report(new TransferProgress(now, size, speed));
                }
                await Task.WhenAll(readOut, readErr);
                if (p.ExitCode != 0) throw new InvalidOperationException("sendme failed: " + log);
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
                throw;
            }
            finally
            {
                lock (_running) _running.Remove(p);
            }

            progress?.Report(new TransferProgress(size, size, 0));
            return Directory.EnumerateFileSystemEntries(folder)
                .Where(e => !before.Contains(e) && !Path.GetFileName(e).StartsWith(".sendme", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        private static long TempBytes(string folder)
        {
            long total = 0;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(folder, ".sendme*"))
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        try { total += new FileInfo(f).Length; } catch { }
            }
            catch { }
            return total;
        }
    }
}
