using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace WinPlus
{
    /// <summary>
    /// Ctrl+Z in File Explorer / on the desktop undoes the last convert / resize /
    /// compress / archive, the same way Explorer undoes its own moves: the file comes
    /// back exactly as it was (same name, same place, same content).
    ///
    /// Every change sets the previous file aside instead of deleting it (a move on the
    /// same drive: instant). One drop = one step, so several files dropped together
    /// come back together. Steps can be undone for 30 minutes (at most 20 of them);
    /// after that the set-aside copies are cleaned up (or sent to the Recycle Bin when
    /// "keep originals" is off, like before).
    /// </summary>
    internal static class FileUndo
    {
        public enum Finish { Keep, Delete, Recycle }

        public sealed class Item
        {
            public string Output { get; set; } = "";          // what the change left
            public long OutputSize { get; set; }
            public DateTime OutputTime { get; set; }
            public string Source { get; set; } = "";          // the file's place before the change
            public string? Restore { get; set; }              // where the previous file is now (null: the change only made a new file)
            public string? OutputBackTo { get; set; }         // undo moves Output here instead of deleting it
            public ConvertHistory.Entry? HistoryBefore { get; set; }  // written back on undo
            public string? HistoryAdded { get; set; }         // backup of an entry the change added (removed on undo)
            public Finish Done { get; set; }                  // what happens to Restore once it can't be undone
        }

        public sealed class Step
        {
            public List<Item> Items { get; set; } = new();
            public DateTime Time { get; set; }
        }

        private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(30);
        private const int MaxSteps = 20;
        private static readonly object Gate = new();
        private static readonly List<Step> Steps = new();
        private static readonly AsyncLocal<Step?> Open = new();
        private static DispatcherTimer? _timer;

        private static string StackFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus", "undo.json");

        // ---- Recording ---------------------------------------------------------------------

        /// <summary>Everything recorded until this is disposed is one step (one Ctrl+Z).</summary>
        public static IDisposable Batch()
        {
            var step = new Step();
            Open.Value = step;
            return new BatchEnd(step);
        }

        private sealed class BatchEnd : IDisposable
        {
            private readonly Step _step;
            public BatchEnd(Step step) => _step = step;
            public void Dispose()
            {
                if (Open.Value == _step) Open.Value = null;
                lock (_step) if (_step.Items.Count == 0) return;
                Push(_step);
            }
        }

        /// <summary>The previous file goes to a hidden folder on its own drive (instant), keeping its name.</summary>
        public static string Stash(string path)
        {
            string dir = Path.Combine(ConvertHistory.BackupFolderFor(path), "undo", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, Path.GetFileName(path));
            File.Move(path, target);
            return target;
        }

        /// <summary>The change is finished: remember it (in the open batch, or as its own step).</summary>
        public static void Record(Item item)
        {
            try
            {
                var info = new FileInfo(item.Output);
                item.OutputSize = info.Length;
                item.OutputTime = info.LastWriteTimeUtc;
            }
            catch { }
            var open = Open.Value;
            if (open != null) { lock (open) open.Items.Add(item); return; }
            Push(new Step { Items = { item } });
        }

        /// <summary>A new file was made next to the others (archive / zip): undo removes it.</summary>
        public static void RecordNew(string path) => Record(new Item { Output = path, Source = path });

        private static void Push(Step step)
        {
            step.Time = DateTime.Now;
            List<Step> dropped;
            lock (Gate)
            {
                Steps.Add(step);
                dropped = Steps.Count > MaxSteps ? Steps.Take(Steps.Count - MaxSteps).ToList() : new();
                Steps.RemoveRange(0, dropped.Count);
                Save();
            }
            foreach (var s in dropped) FinishStep(s);
            OnUi(Arm);
        }

        // ---- Undo --------------------------------------------------------------------------

        /// <summary>Ctrl+Z: puts the last step back. Runs on the UI thread.</summary>
        public static async void UndoLast()
        {
            Step? step;
            lock (Gate)
            {
                step = Steps.LastOrDefault();
                if (step != null) { Steps.RemoveAt(Steps.Count - 1); Save(); }
            }
            Arm();
            if (step == null) return;

            int failed = 0;
            foreach (var item in Enumerable.Reverse(step.Items))
            {
                var spot = DesktopIcons.GetPosition(item.Output);
                string? back = await Task.Run(() => Revert(item, checkOutput: true));
                if (back == null) { failed++; continue; }
                if (spot is DesktopIcons.POINT pt && !string.Equals(back, item.Output, StringComparison.OrdinalIgnoreCase))
                    _ = PlaceIconAsync(back, pt);
            }
            if (failed > 0)
                MessageBox.Show(Lang.T("ما قدرت أرجّع الملف: تغيّر أو انحذف بعد التعديل."), "Wibn",
                                MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, Lang.MsgOptions);
        }

        /// <summary>
        /// Puts one change back. Returns the restored file's path (null if it couldn't).
        /// <paramref name="checkOutput"/>: leave it alone if the result was changed since.
        /// </summary>
        public static string? Revert(Item item, bool checkOutput)
        {
            try
            {
                if (checkOutput)
                {
                    if (!File.Exists(item.Output)) return null;
                    var info = new FileInfo(item.Output);
                    if (info.Length != item.OutputSize || info.LastWriteTimeUtc != item.OutputTime) return null;
                }
                if (item.Restore != null && !File.Exists(item.Restore)) return null;

                if (File.Exists(item.Output))
                {
                    if (item.OutputBackTo != null) File.Move(item.Output, item.OutputBackTo);
                    else File.Delete(item.Output);
                }
                if (item.Restore == null) return item.Source;  // it only made a new file: gone again

                string place = File.Exists(item.Source) ? Converter.UniquePath(item.Source) : item.Source;
                File.Move(item.Restore, place);
                TryDeleteDir(item.Restore);

                if (item.HistoryAdded != null) ConvertHistory.RemoveBackup(item.HistoryAdded);
                if (item.HistoryBefore != null)
                {
                    item.HistoryBefore.Current = place;
                    ConvertHistory.Put(item.HistoryBefore);
                }
                return place;
            }
            catch (Exception ex)
            {
                CrashLog.Write("undo " + Path.GetFileName(item.Source), ex);
                return null;
            }
        }

        // ---- Hook / expiry -----------------------------------------------------------------

        /// <summary>Ctrl+Z is watched only while there's something recent to undo.</summary>
        private static void Arm()
        {
            List<Step> old;
            bool any;
            lock (Gate)
            {
                old = Steps.Where(s => DateTime.Now - s.Time > KeepFor).ToList();
                foreach (var s in old) Steps.Remove(s);
                if (old.Count > 0) Save();
                any = Steps.Count > 0;
            }
            foreach (var s in old) FinishStep(s);
            DragSpaceHook.UndoArmed = any;

            if (any && _timer == null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                _timer.Tick += (_, _) => Arm();
                _timer.Start();
            }
            else if (!any && _timer != null)
            {
                _timer.Stop();
                _timer = null;
            }
        }

        /// <summary>The step can't be undone any more: clean up what was set aside.</summary>
        private static void FinishStep(Step step)
        {
            foreach (var item in step.Items)
            {
                if (item.Restore == null || item.Done == Finish.Keep || !File.Exists(item.Restore)) continue;
                try
                {
                    if (item.Done == Finish.Recycle) ShellOps.RecycleFiles(IntPtr.Zero, new[] { item.Restore });
                    else File.Delete(item.Restore);
                }
                catch { }
                TryDeleteDir(item.Restore);
            }
        }

        /// <summary>At startup / exit: what's left from before can't be undone any more.</summary>
        public static void FinishAll()
        {
            List<Step> all;
            lock (Gate)
            {
                all = Steps.ToList();
                Steps.Clear();
                try
                {
                    if (File.Exists(StackFile))
                        all.AddRange(JsonSerializer.Deserialize<List<Step>>(File.ReadAllText(StackFile)) ?? new());
                }
                catch { }
                Save();
            }
            foreach (var s in all) FinishStep(s);
            OnUi(Arm);
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StackFile)!);
                if (Steps.Count == 0) File.Delete(StackFile);
                else File.WriteAllText(StackFile, JsonSerializer.Serialize(Steps));
            }
            catch { }
        }

        private static void TryDeleteDir(string fileInside)
        {
            try
            {
                string? dir = Path.GetDirectoryName(fileInside);
                if (dir != null && Path.GetFileName(Path.GetDirectoryName(dir)) == "undo" && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { }
        }

        private static async Task PlaceIconAsync(string path, DesktopIcons.POINT pt)
        {
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(100);
                if (DesktopIcons.SetPosition(path, pt)) return;
            }
        }

        private static void OnUi(Action action)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null) return;
            if (d.CheckAccess()) action(); else d.BeginInvoke(action);
        }
    }
}
