using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace WinPlus
{
    /// <summary>
    /// Locks a file or a folder with a password: AES-256-GCM, key from PBKDF2-SHA256.
    ///
    /// ".wplock" layout:
    ///   header: "WPLOCK" | version(1) | iterations(4) | salt(16) | noncePrefix(8) | chunkSize(4)
    ///     version 1 = a file (the content is the file's bytes)
    ///     version 2 = a folder (the content is a simple archive, see below)
    ///   then chunks: ciphertext + 16-byte tag. Each chunk's nonce is noncePrefix + its
    ///   number, and the header plus a "last chunk" flag are authenticated with it, so
    ///   chunks can't be swapped, removed or cut off without the check failing.
    ///
    /// Folder archive (streamed, so nothing unencrypted is ever written to disk):
    ///   "WPDIR1", then entries: kind(1) [1=folder, 2=file, 0=end] | pathLength(2) | UTF-8
    ///   relative path | for files: length(8) + bytes.
    ///
    /// Everything streams in 1 MB chunks, so memory stays low even for huge files.
    /// </summary>
    internal static class FileLocker
    {
        public const string Extension = ".wplock";
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("WPLOCK");
        private static readonly byte[] DirMagic = Encoding.ASCII.GetBytes("WPDIR1");
        private const byte VersionFile = 1, VersionFolder = 2;
        private const int Iterations = 600_000;
        private const int ChunkSize = 1 << 20;
        private const int TagSize = 16;
        private const int HeaderSize = 6 + 1 + 4 + 16 + 8 + 4;

        public sealed class WrongPasswordException : Exception
        {
            public WrongPasswordException() : base("Wrong password") { }
        }

        /// <summary>Locked fine, but the original couldn't be fully removed (something has it open).</summary>
        public sealed class OriginalNotDeletedException : Exception
        {
            public string LockedPath { get; }
            public OriginalNotDeletedException(string locked, Exception inner) : base(inner.Message, inner)
                => LockedPath = locked;
        }

        /// <summary>Folders that must never be locked (a whole drive, Windows, the user folder...).</summary>
        public static bool IsProtectedFolder(string path)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string? root = Path.GetPathRoot(full);
            if (root != null && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (var special in new[]
            {
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData,
            })
            {
                string sp = Environment.GetFolderPath(special);
                if (sp.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(sp), full, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static bool IsLocked(string path) =>
            File.Exists(path) && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

        // ---- The list of locked files (shown in the lock page) ----------------------

        private const string ListKey = @"Software\WinPlus\TaskbarScroll";
        private const string ListValue = "LockedFiles";
        private static readonly object ListGate = new();

        /// <summary>Files we locked that still exist (missing ones are dropped).</summary>
        public static List<string> LockedFiles()
        {
            lock (ListGate)
            {
                var all = ReadList();
                var alive = all.FindAll(File.Exists);
                if (alive.Count != all.Count) WriteList(alive);
                return alive;
            }
        }

        public static event Action? ListChanged;

        private static void Remember(string path)
        {
            try
            {
                lock (ListGate)
                {
                    var list = ReadList();
                    list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                    list.Insert(0, path);
                    WriteList(list);
                }
                ListChanged?.Invoke();
            }
            catch { }
        }

        private static void Forget(string path)
        {
            try
            {
                lock (ListGate)
                {
                    var list = ReadList();
                    if (list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0) WriteList(list);
                }
                ListChanged?.Invoke();
            }
            catch { }
        }

        private static List<string> ReadList()
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ListKey);
            return key?.GetValue(ListValue) is string[] a ? new List<string>(a) : new List<string>();
        }

        private static void WriteList(List<string> list)
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ListKey);
            key.SetValue(ListValue, list.ToArray(), Microsoft.Win32.RegistryValueKind.MultiString);
        }

        // ---- Lock -----------------------------------------------------------------

        /// <summary>Encrypts a file or folder into "name.wplock" next to it and removes the original.</summary>
        public static string Lock(string path, string password, IProgress<double>? progress = null,
                                  CancellationToken cancel = default)
        {
            path = Path.TrimEndingDirectorySeparator(path);
            bool folder = Directory.Exists(path);
            if (folder && IsProtectedFolder(path)) throw new UnauthorizedAccessException("Protected folder");
            string target = UniquePath(path + Extension);
            string temp = target + ".tmp";

            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
                {
                    using (var writer = new EncryptWriter(output, password, folder ? VersionFolder : VersionFile))
                    {
                        if (folder) WriteFolder(writer, path, progress, cancel);
                        else WriteFile(writer, path, progress, cancel);
                        writer.Complete();
                    }
                    output.Flush(true);
                }
                File.Move(temp, target);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
            Remember(target);

            try
            {
                if (folder) DeleteFolder(path); else DeleteFile(path);
            }
            catch (Exception ex)
            {
                throw new OriginalNotDeletedException(target, ex);
            }
            return target;
        }

        private static void WriteFile(EncryptWriter writer, string path, IProgress<double>? progress,
                                      CancellationToken cancel)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            CopyIn(input, writer, input.Length, 0, Math.Max(1, input.Length), progress, cancel);
        }

        private static void WriteFolder(EncryptWriter writer, string root, IProgress<double>? progress,
                                        CancellationToken cancel)
        {
            // List everything first (for progress), without following links.
            var dirs = new List<string>();
            var files = new List<(string path, long size)>();
            Walk(root, dirs, files);
            long total = 1, done = 0;
            foreach (var f in files) total += f.size;

            writer.Write(DirMagic);
            Span<byte> len = stackalloc byte[8];
            foreach (var d in dirs) WriteEntry(writer, 1, Path.GetRelativePath(root, d));
            foreach (var (file, _) in files)
            {
                cancel.ThrowIfCancellationRequested();
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
                long length = input.Length;
                WriteEntry(writer, 2, Path.GetRelativePath(root, file));
                BinaryPrimitives.WriteInt64LittleEndian(len, length);
                writer.Write(len);
                CopyIn(input, writer, length, done, total, progress, cancel);
                done += length;
            }
            writer.Write(stackalloc byte[] { 0 });  // end
        }

        private static void Walk(string dir, List<string> dirs, List<(string, long)> files)
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;  // links: skip
                if (entry is DirectoryInfo d)
                {
                    dirs.Add(d.FullName);
                    Walk(d.FullName, dirs, files);
                }
                else if (entry is FileInfo f)
                {
                    files.Add((f.FullName, f.Length));
                }
            }
        }

        private static void WriteEntry(EncryptWriter writer, byte kind, string relative)
        {
            byte[] name = Encoding.UTF8.GetBytes(relative.Replace('\\', '/'));
            if (name.Length > ushort.MaxValue) throw new PathTooLongException();
            Span<byte> head = stackalloc byte[3];
            head[0] = kind;
            BinaryPrimitives.WriteUInt16LittleEndian(head[1..], (ushort)name.Length);
            writer.Write(head);
            writer.Write(name);
        }

        private static void CopyIn(Stream input, EncryptWriter writer, long length, long doneBefore, long total,
                                   IProgress<double>? progress, CancellationToken cancel)
        {
            byte[] buffer = new byte[ChunkSize];
            long left = length, done = doneBefore;
            while (left > 0)
            {
                cancel.ThrowIfCancellationRequested();
                int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n == 0) throw new IOException("The file changed while it was being locked");
                writer.Write(buffer.AsSpan(0, n));
                left -= n;
                done += n;
                progress?.Report((double)done / total);
            }
            CryptographicOperations.ZeroMemory(buffer);
        }

        // ---- Unlock ---------------------------------------------------------------

        /// <summary>Decrypts a ".wplock" back to the original file or folder and removes the locked copy.</summary>
        public static string Unlock(string path, string password, IProgress<double>? progress = null,
                                    CancellationToken cancel = default)
        {
            string target = UniquePath(path[..^Extension.Length]);
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            {
                // Checks the password (decrypts the first chunk) before anything is created.
                using var reader = new DecryptReader(input, password, progress, cancel);
                if (reader.Version == VersionFolder) ReadFolder(reader, target);
                else ReadFile(reader, target);
            }
            File.Delete(path);
            Forget(path);
            return target;
        }

        private static void ReadFile(DecryptReader reader, string target)
        {
            string temp = target + ".wptmp";
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
                {
                    byte[] buffer = new byte[ChunkSize];
                    int n;
                    while ((n = reader.Read(buffer)) > 0) output.Write(buffer, 0, n);
                    CryptographicOperations.ZeroMemory(buffer);
                    output.Flush(true);
                }
                File.Move(temp, target);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }

        private static void ReadFolder(DecryptReader reader, string target)
        {
            string temp = target + ".wptmp";
            try
            {
                byte[] magic = new byte[6];
                reader.ReadExact(magic);
                if (!magic.AsSpan().SequenceEqual(DirMagic)) throw new InvalidDataException("Bad folder data");

                Directory.CreateDirectory(temp);
                string root = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;
                byte[] head = new byte[3];
                byte[] buffer = new byte[ChunkSize];
                while (true)
                {
                    reader.ReadExact(head.AsSpan(0, 1));
                    if (head[0] == 0) break;
                    reader.ReadExact(head.AsSpan(1, 2));
                    byte[] name = new byte[BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(1))];
                    reader.ReadExact(name);
                    // Never write outside the folder, whatever the archive says.
                    string full = Path.GetFullPath(Path.Combine(root, Encoding.UTF8.GetString(name)));
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Bad path in folder data");

                    if (head[0] == 1)
                    {
                        Directory.CreateDirectory(full);
                    }
                    else if (head[0] == 2)
                    {
                        byte[] lenBytes = new byte[8];
                        reader.ReadExact(lenBytes);
                        long left = BinaryPrimitives.ReadInt64LittleEndian(lenBytes);
                        if (left < 0) throw new InvalidDataException("Bad size");
                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                        using var output = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1);
                        while (left > 0)
                        {
                            int n = (int)Math.Min(buffer.Length, left);
                            reader.ReadExact(buffer.AsSpan(0, n));
                            output.Write(buffer, 0, n);
                            left -= n;
                        }
                    }
                    else
                    {
                        throw new InvalidDataException("Bad entry");
                    }
                }
                CryptographicOperations.ZeroMemory(buffer);
                if (reader.Read(new byte[1]) != 0) throw new InvalidDataException("Extra data");
                Directory.Move(temp, target);
            }
            catch
            {
                try { if (Directory.Exists(temp)) DeleteFolder(temp); } catch { }
                throw;
            }
        }

        // ---- Encrypted stream pieces ------------------------------------------------

        /// <summary>Takes plain bytes and writes encrypted chunks. A full chunk is only written
        /// once more data arrives, so the final chunk can be marked as the last one.</summary>
        private sealed class EncryptWriter : IDisposable
        {
            private readonly Stream _out;
            private readonly AesGcm _aes;
            private readonly byte[] _header = new byte[HeaderSize];
            private readonly byte[] _prefix;
            private readonly byte[] _plain = new byte[ChunkSize];
            private readonly byte[] _cipher = new byte[ChunkSize];
            private readonly byte[] _tag = new byte[TagSize];
            private int _filled;
            private uint _index;

            public EncryptWriter(Stream output, string password, byte version)
            {
                _out = output;
                byte[] salt = RandomNumberGenerator.GetBytes(16);
                _prefix = RandomNumberGenerator.GetBytes(8);
                Magic.CopyTo(_header, 0);
                _header[6] = version;
                BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(7), Iterations);
                salt.CopyTo(_header, 11);
                _prefix.CopyTo(_header, 27);
                BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(35), ChunkSize);
                byte[] key = DeriveKey(password, salt, Iterations);
                _aes = new AesGcm(key, TagSize);
                CryptographicOperations.ZeroMemory(key);
                _out.Write(_header);
            }

            public void Write(ReadOnlySpan<byte> data)
            {
                while (data.Length > 0)
                {
                    if (_filled == ChunkSize) Flush(last: false);
                    int n = Math.Min(data.Length, ChunkSize - _filled);
                    data[..n].CopyTo(_plain.AsSpan(_filled));
                    _filled += n;
                    data = data[n..];
                }
            }

            public void Complete() => Flush(last: true);

            private void Flush(bool last)
            {
                _aes.Encrypt(Nonce(_prefix, _index), _plain.AsSpan(0, _filled), _cipher.AsSpan(0, _filled), _tag,
                             Aad(_header, last));
                _out.Write(_cipher, 0, _filled);
                _out.Write(_tag);
                _filled = 0;
                _index++;
            }

            public void Dispose()
            {
                CryptographicOperations.ZeroMemory(_plain);
                _aes.Dispose();
            }
        }

        /// <summary>Reads and checks encrypted chunks, hands out the plain bytes.</summary>
        private sealed class DecryptReader : IDisposable
        {
            private readonly Stream _in;
            private readonly AesGcm _aes;
            private readonly byte[] _header = new byte[HeaderSize];
            private readonly byte[] _prefix;
            private readonly byte[] _record;
            private readonly byte[] _plain;
            private readonly IProgress<double>? _progress;
            private readonly CancellationToken _cancel;
            private int _pos, _len;
            private uint _index;
            private bool _done;

            public byte Version { get; }

            public DecryptReader(Stream input, string password, IProgress<double>? progress, CancellationToken cancel)
            {
                _in = input;
                _progress = progress;
                _cancel = cancel;
                if (ReadFull(_in, _header) != HeaderSize || !_header.AsSpan(0, 6).SequenceEqual(Magic))
                    throw new InvalidDataException("Not a WinPlus locked file");
                Version = _header[6];
                if (Version != VersionFile && Version != VersionFolder) throw new InvalidDataException("Unknown version");
                int iterations = BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(7));
                byte[] salt = _header.AsSpan(11, 16).ToArray();
                _prefix = _header.AsSpan(27, 8).ToArray();
                int chunk = BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(35));
                if (iterations < 1 || iterations > 10_000_000 || chunk < 1 || chunk > 64 << 20)
                    throw new InvalidDataException("Bad header");
                _record = new byte[chunk + TagSize];
                _plain = new byte[chunk];
                byte[] key = DeriveKey(password, salt, iterations);
                _aes = new AesGcm(key, TagSize);
                CryptographicOperations.ZeroMemory(key);
                NextChunk();  // wrong password fails here, before anything is written
            }

            public int Read(Span<byte> buffer)
            {
                int total = 0;
                while (buffer.Length > 0)
                {
                    if (_pos == _len)
                    {
                        if (_done) break;
                        NextChunk();
                        continue;
                    }
                    int n = Math.Min(buffer.Length, _len - _pos);
                    _plain.AsSpan(_pos, n).CopyTo(buffer);
                    _pos += n;
                    total += n;
                    buffer = buffer[n..];
                }
                return total;
            }

            public void ReadExact(Span<byte> buffer)
            {
                if (Read(buffer) != buffer.Length) throw new InvalidDataException("Data is cut off");
            }

            private void NextChunk()
            {
                _cancel.ThrowIfCancellationRequested();
                int n = ReadFull(_in, _record);
                if (n < TagSize) throw new InvalidDataException("File is cut off");
                bool last = _in.Position >= _in.Length;
                if (!last && n != _record.Length) throw new InvalidDataException("File is damaged");
                int len = n - TagSize;
                try
                {
                    _aes.Decrypt(Nonce(_prefix, _index), _record.AsSpan(0, len), _record.AsSpan(len, TagSize),
                                 _plain.AsSpan(0, len), Aad(_header, last));
                }
                catch (AuthenticationTagMismatchException)
                {
                    // First chunk: almost surely the password. Later: damage.
                    if (_index == 0) throw new WrongPasswordException();
                    throw new InvalidDataException("File is damaged");
                }
                _pos = 0;
                _len = len;
                _done = last;
                _index++;
                _progress?.Report((double)_in.Position / Math.Max(1, _in.Length));
            }

            public void Dispose()
            {
                CryptographicOperations.ZeroMemory(_plain);
                _aes.Dispose();
            }
        }

        // ---- Helpers ----------------------------------------------------------------

        private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
            Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
                                      HashAlgorithmName.SHA256, 32);

        private static byte[] Nonce(byte[] prefix, uint index)
        {
            byte[] nonce = new byte[12];
            prefix.CopyTo(nonce, 0);
            BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), index);
            return nonce;
        }

        private static byte[] Aad(byte[] header, bool last)
        {
            byte[] aad = new byte[header.Length + 1];
            header.CopyTo(aad, 0);
            aad[^1] = last ? (byte)1 : (byte)0;
            return aad;
        }

        private static int ReadFull(Stream s, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int n = s.Read(buffer, total, buffer.Length - total);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        /// <summary>Overwrites with zeros before deleting, so it's harder to recover.
        /// (On SSDs the drive may still keep old copies internally; that's out of our hands.)</summary>
        private static void DeleteFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.IsReadOnly) info.IsReadOnly = false;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 1);
                byte[] zeros = new byte[Math.Min(ChunkSize, Math.Max(1, fs.Length))];
                long left = fs.Length;
                while (left > 0)
                {
                    int n = (int)Math.Min(left, zeros.Length);
                    fs.Write(zeros, 0, n);
                    left -= n;
                }
                fs.Flush(true);
            }
            catch { }
            File.Delete(path);
        }

        private static void DeleteFolder(string dir)
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // A link: remove the link itself, never what it points to.
                    if (entry is DirectoryInfo) Directory.Delete(entry.FullName); else File.Delete(entry.FullName);
                }
                else if (entry is DirectoryInfo d)
                {
                    DeleteFolder(d.FullName);
                }
                else
                {
                    DeleteFile(entry.FullName);
                }
            }
            var info = new DirectoryInfo(dir);
            if ((info.Attributes & FileAttributes.ReadOnly) != 0) info.Attributes &= ~FileAttributes.ReadOnly;
            Directory.Delete(dir);
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileName(path);
            // "photo.jpg.wplock" → "photo (1).jpg.wplock", "photo.jpg" → "photo (1).jpg"
            int dot = name.IndexOf('.', 1);
            string stem = dot > 0 ? name[..dot] : name;
            string rest = dot > 0 ? name[dot..] : "";
            for (int i = 1; ; i++)
            {
                string p = Path.Combine(dir, $"{stem} ({i}){rest}");
                if (!File.Exists(p) && !Directory.Exists(p)) return p;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
