using System;
using System.IO;

namespace WinPlus
{
    internal static class CrashLog
    {
        public static void Write(string where, Exception? ex) => Append($"CRASH ({where}): {ex}");

        /// <summary>A plain line in the log (e.g. when the app starts).</summary>
        public static void Note(string text) => Append(text);

        private static void Append(string text)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "taskbar.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} [app] {text}\r\n");
            }
            catch { }
        }
    }
}
