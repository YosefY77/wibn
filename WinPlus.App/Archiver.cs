using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Z while dragging files or folders: packs them into one archive next to them
    /// (the originals stay). ZIP and TAR.GZ are built in; 7Z uses 7-Zip (the one
    /// installed, or the small official 7zr.exe downloaded once); RAR shows only
    /// when WinRAR is installed (RAR files can only be made with WinRAR).
    /// Windows' own tar.exe isn't used: it crashes on non-English file names.
    /// </summary>
    internal static class Archiver
    {
        public static Converter.Target[] Options()
        {
            var list = new List<Converter.Target> { new("ZIP", "zip"), new("7Z", "7z") };
            if (FindRar() != null) list.Add(new("RAR", "rar"));
            list.Add(new("TAR.GZ", "tar.gz"));
            return list.ToArray();
        }

        /// <summary>Makes the archive and returns its path.</summary>
        public static async Task<string> CreateAsync(string[] items, string format, CancellationToken cancel = default)
        {
            items = items.Where(p => File.Exists(p) || Directory.Exists(p))
                         .Select(p => Path.GetFullPath(p).TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (items.Length == 0) throw new FileNotFoundException("Nothing to pack");
            string dir = Path.GetDirectoryName(items[0])!;
            string output = UniqueOutput(dir, BaseName(items), "." + format);
            string temp = output + ".part";
            try
            {
                switch (format)
                {
                    case "zip": await Task.Run(() => WriteZip(items, temp), cancel); break;
                    case "tar.gz": await Task.Run(() => WriteTarGz(items, temp), cancel); break;
                    case "7z": await Run7zAsync(items, temp, cancel); break;
                    case "rar": await RunRarAsync(items, temp, cancel); break;
                    default: throw new NotSupportedException(format);
                }
                File.Move(temp, output);
                return output;
            }
            finally { Converter.TryDelete(temp); }
        }

        /// <summary>One item: its own name. Several: the name of the folder they're in.</summary>
        private static string BaseName(string[] items)
        {
            if (items.Length == 1)
                return Directory.Exists(items[0]) ? Path.GetFileName(items[0]) : Path.GetFileNameWithoutExtension(items[0]);
            string parent = Path.GetFileName(Path.GetDirectoryName(items[0])!.TrimEnd('\\'));
            return string.IsNullOrEmpty(parent) ? "Archive" : parent;
        }

        private static string UniqueOutput(string dir, string name, string ext)
        {
            string p = Path.Combine(dir, name + ext);
            for (int i = 1; File.Exists(p) || Directory.Exists(p); i++) p = Path.Combine(dir, $"{name} ({i}){ext}");
            return p;
        }

        /// <summary>Every file under the items, with its name inside the archive (folders keep their own name).</summary>
        private static IEnumerable<(string full, string name, bool isDir)> Walk(string[] items)
        {
            foreach (var item in items)
            {
                string root = Path.GetDirectoryName(item)!;
                if (File.Exists(item)) { yield return (item, Path.GetFileName(item), false); continue; }
                yield return (item, Path.GetFileName(item) + "/", true);
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var d in Directory.EnumerateDirectories(item, "*", opts))
                    yield return (d, Path.GetRelativePath(root, d).Replace('\\', '/') + "/", true);
                foreach (var f in Directory.EnumerateFiles(item, "*", opts))
                    yield return (f, Path.GetRelativePath(root, f).Replace('\\', '/'), false);
            }
        }

        private static void WriteZip(string[] items, string output)
        {
            using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
            foreach (var (full, name, isDir) in Walk(items))
            {
                if (isDir) zip.CreateEntry(name);
                else zip.CreateEntryFromFile(full, name, CompressionLevel.SmallestSize);
            }
        }

        private static void WriteTarGz(string[] items, string output)
        {
            using var fs = new FileStream(output, FileMode.CreateNew);
            using var gz = new GZipStream(fs, CompressionLevel.SmallestSize);
            using var tar = new TarWriter(gz, TarEntryFormat.Pax);   // PAX keeps any file name (Arabic, long…)
            foreach (var (full, name, _) in Walk(items))
                tar.WriteEntry(full, name);
        }

        // ---- 7-Zip --------------------------------------------------------------------------

        private static string OurFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus", "7zip");

        private const string SevenZrUrl = "https://www.7-zip.org/a/7zr.exe";

        public sealed class SevenZipMissingException : Exception { }

        /// <summary>7z.exe from an installed 7-Zip, or our 7zr.exe.</summary>
        public static string? Find7z()
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(@"SOFTWARE\7-Zip");
                    foreach (var name in new[] { "Path64", "Path" })
                        if (key?.GetValue(name) is string p && File.Exists(Path.Combine(p, "7z.exe"))) return Path.Combine(p, "7z.exe");
                }
                catch { }
            }
            foreach (var p in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                string exe = Path.Combine(p, "7-Zip", "7z.exe");
                if (File.Exists(exe)) return exe;
            }
            string ours = Path.Combine(OurFolder, "7zr.exe");
            return File.Exists(ours) ? ours : null;
        }

        public static bool Has7z => Find7z() != null;

        /// <summary>Downloads the official standalone 7zr.exe (about 600 KB) once.</summary>
        public static async Task Download7zAsync(IProgress<double>? progress, CancellationToken cancel)
        {
            Directory.CreateDirectory(OurFolder);
            string target = Path.Combine(OurFolder, "7zr.exe"), temp = target + ".download";
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                byte[] data = await http.GetByteArrayAsync(SevenZrUrl, cancel);
                if (data.Length < 100_000 || data[0] != 'M' || data[1] != 'Z') throw new InvalidDataException("7zr.exe download is not a program");
                await File.WriteAllBytesAsync(temp, data, cancel);
                File.Move(temp, target, true);
                progress?.Report(1);
            }
            finally { Converter.TryDelete(temp); }
        }

        private static async Task Run7zAsync(string[] items, string output, CancellationToken cancel)
        {
            string exe = Find7z() ?? throw new SevenZipMissingException();
            var args = new List<string> { "a", "-t7z", "-mx=7", "-y", "-bso0", "-bsp0", output, "--" };
            args.AddRange(items.Select(Path.GetFileName)!);
            await RunAsync(exe, args, Path.GetDirectoryName(items[0])!, "7-Zip", cancel);
        }

        // ---- WinRAR -------------------------------------------------------------------------

        public static string? FindRar()
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\WinRAR");
                foreach (var name in new[] { "exe64", "exe32" })
                    if (key?.GetValue(name) is string p)
                    {
                        string rar = Path.Combine(Path.GetDirectoryName(p)!, "Rar.exe");
                        if (File.Exists(rar)) return rar;
                    }
            }
            catch { }
            foreach (var p in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                string rar = Path.Combine(p, "WinRAR", "Rar.exe");
                if (File.Exists(rar)) return rar;
            }
            return null;
        }

        private static async Task RunRarAsync(string[] items, string output, CancellationToken cancel)
        {
            string exe = FindRar() ?? throw new FileNotFoundException("WinRAR not found");
            // -m5 best, -r folders too, -ep1 names relative to the items' folder, -idq quiet.
            var args = new List<string> { "a", "-m5", "-r", "-ep1", "-idq", "-y", output, "--" };
            args.AddRange(items);
            await RunAsync(exe, args, Path.GetDirectoryName(items[0])!, "WinRAR", cancel);
            // Rar.exe adds .rar to a name that doesn't end in it.
            if (!File.Exists(output) && File.Exists(output + ".rar")) File.Move(output + ".rar", output);
        }

        private static async Task RunAsync(string exe, IEnumerable<string> args, string workDir, string what, CancellationToken cancel)
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException(what + " didn't start");
            var stderr = p.StandardError.ReadToEndAsync(cancel);
            var stdout = p.StandardOutput.ReadToEndAsync(cancel);
            try { await p.WaitForExitAsync(cancel); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
            if (p.ExitCode > 1)   // 1 = finished with warnings (e.g. a file in use was skipped)
            {
                CrashLog.Note($"{what} failed ({p.ExitCode}): {await stdout} {await stderr}");
                throw new InvalidOperationException($"{what} failed ({p.ExitCode})");
            }
        }
    }
}
