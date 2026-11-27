using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// The sign-in for downloads (the sites' cookies), kept encrypted with Windows' DPAPI:
    /// only this Windows account on this PC can read it, so a copied cookies.dat is useless.
    /// The tools need a plain cookies.txt, so <see cref="Open"/> writes one to a private
    /// folder for the length of one run and deletes it after (keeping the cookies the
    /// tool refreshed, so the sign-in lasts).
    /// </summary>
    internal static class CookieVault
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Wibn.download-sign-in.v1");
        private static readonly object Gate = new();
        private static bool _migrated, _swept, _loggedUnreadable;

        private static string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus");
        private static string VaultPath => Path.Combine(Root, "cookies.dat");
        private static string JarsRoot => Path.Combine(Root, "jars");
        /// <summary>Where versions before 2.8 kept the sign-in, unencrypted.</summary>
        private static string OldPlainPath => Path.Combine(Root, "cookies.txt");
        private const string OldKey = @"Software\WinPlus\Video";

        public static bool HasSignIn
        {
            // Readable, not just there: after a Windows password reset (or on another account) it can't be opened.
            get { Migrate(); return File.Exists(VaultPath) && Load() != null; }
        }

        public static void Save(string cookies)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Root);
                byte[] sealedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(cookies), Entropy, DataProtectionScope.CurrentUser);
                string tmp = VaultPath + ".new";
                File.WriteAllBytes(tmp, sealedBytes);
                File.Move(tmp, VaultPath, true);
            }
        }

        private static string? Load()
        {
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(VaultPath)) return null;
                    return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(VaultPath), Entropy, DataProtectionScope.CurrentUser));
                }
                catch (Exception ex)
                {
                    // Another Windows account, or a damaged file: as good as signed out.
                    if (!_loggedUnreadable) { _loggedUnreadable = true; CrashLog.Write("cookie vault", ex); }
                    return null;
                }
            }
        }

        public static void Delete()
        {
            lock (Gate)
            {
                Converter.TryDelete(VaultPath);
                Converter.TryDelete(OldPlainPath);
            }
        }

        /// <summary>
        /// Older versions: the plain cookies.txt in Wibn's folder, or a path (in the registry)
        /// to a cookies.txt the user picked. Both go into the vault; Wibn's own plain copy is deleted.
        /// </summary>
        private static void Migrate()
        {
            lock (Gate)
            {
                if (_migrated) return;
                _migrated = true;
                try
                {
                    string? picked = null;
                    using (var key = Registry.CurrentUser.OpenSubKey(OldKey, true))
                    {
                        picked = key?.GetValue("CookiesFile") as string;
                        key?.DeleteValue("CookiesFile", false);
                    }
                    if (!File.Exists(VaultPath))
                    {
                        string? from = File.Exists(OldPlainPath) ? OldPlainPath
                                     : picked != null && File.Exists(picked) ? picked : null;
                        if (from != null) Save(File.ReadAllText(from));
                    }
                    Converter.TryDelete(OldPlainPath);
                }
                catch (Exception ex) { CrashLog.Write("cookie vault migrate", ex); }
            }
        }

        /// <summary>The sign-in as a cookies.txt for one tool run, or null when there's none.</summary>
        public static Jar? Open()
        {
            Migrate();
            string? text = Load();
            if (string.IsNullOrWhiteSpace(text)) return null;
            lock (Gate)
            {
                if (!_swept)
                {
                    // Left behind if Wibn was closed mid-download.
                    _swept = true;
                    try { if (Directory.Exists(JarsRoot)) Directory.Delete(JarsRoot, true); } catch { }
                }
            }
            string dir = Path.Combine(JarsRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "cookies.txt");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return new Jar(dir, path, text);
        }

        private static int CookieLines(string text) =>
            text.Split('\n').Count(l => l.Count(c => c == '\t') >= 6);

        public sealed class Jar : IDisposable
        {
            private readonly string _dir, _original;
            public string Path { get; }

            internal Jar(string dir, string path, string original) { _dir = dir; Path = path; _original = original; }

            public void Dispose()
            {
                try
                {
                    // The tools write the cookies back with what the site refreshed. Keep that,
                    // unless it looks broken (a failed run can leave a half-empty file).
                    string now = File.Exists(Path) ? File.ReadAllText(Path) : "";
                    if (now != _original && CookieLines(now) * 2 >= CookieLines(_original) && CookieLines(now) > 0)
                        Save(now);
                }
                catch (Exception ex) { CrashLog.Write("cookie vault refresh", ex); }
                try { Directory.Delete(_dir, true); } catch { Converter.TryDelete(Path); }
            }
        }
    }
}
