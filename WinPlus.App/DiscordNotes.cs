using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace WinPlus
{
    /// <summary>
    /// Discord's notifications, read from Windows' own list (the ones in the notification center): who sent each and
    /// what it says. Discord puts the sender on the first line ("name" or "name (#channel, server)") and the message
    /// on the second. Looked at every 2 seconds while something shows them (the Discord widget, on the desktop or a
    /// bar). Nothing is sent anywhere, and Discord itself isn't touched: only what Windows already shows.
    /// </summary>
    internal static class DiscordNotes
    {
        public sealed record Note(uint Id, string Sender, string Text, DateTimeOffset Time);

        public enum State { Loading, Ready, NoAccess, NotAvailable }

        private static readonly object Gate = new();
        private static Timer? _timer;
        private static int _users, _busy;
        private static IReadOnlyList<Note> _notes = Array.Empty<Note>();
        private static State _state = State.Loading;

        /// <summary>The list changed (any thread).</summary>
        public static event Action? Changed;

        public static IReadOnlyList<Note> Notes { get { lock (Gate) return _notes; } }
        public static State Status { get { lock (Gate) return _state; } }

        /// <summary>Something shows them: start looking (counted; each Start needs its Stop).</summary>
        public static void Start()
        {
            lock (Gate)
            {
                if (_users++ > 0) return;
                _timer = new Timer(_ => _ = Look(), null, 0, 2000);
            }
        }

        public static void Stop()
        {
            lock (Gate)
            {
                if (_users == 0 || --_users > 0) return;
                _timer?.Dispose();
                _timer = null;
            }
        }

        /// <summary>Look again now (e.g. after access was given).</summary>
        public static void Refresh() => _ = Look();

        private static async Task Look()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                UserNotificationListener listener;
                try { listener = UserNotificationListener.Current; }
                catch { Set(State.NotAvailable, Array.Empty<Note>()); return; }
                if (listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
                {
                    Set(State.NoAccess, Array.Empty<Note>());
                    return;
                }
                var all = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                var notes = new List<Note>();
                foreach (var n in all)
                {
                    try
                    {
                        if (!IsDiscord(n)) continue;
                        var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                        var lines = binding?.GetTextElements().Select(t => t.Text?.Trim() ?? "").Where(t => t.Length > 0).ToList();
                        if (lines == null || lines.Count == 0) continue;
                        notes.Add(new Note(n.Id, lines[0], lines.Count > 1 ? string.Join(" ", lines.Skip(1)) : "", n.CreationTime));
                    }
                    catch { }   // one odd notification never stops the rest
                }
                Set(State.Ready, notes.OrderByDescending(x => x.Time).ToList());
            }
            catch (Exception ex)
            {
                CrashLog.Write("discord notes", ex);
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        private static bool IsDiscord(UserNotification n)
        {
            string id = "", name = "";
            try { id = n.AppInfo?.AppUserModelId ?? ""; } catch { }
            try { name = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }
            return id.Contains("Discord", StringComparison.OrdinalIgnoreCase) || name.Contains("Discord", StringComparison.OrdinalIgnoreCase);
        }

        private static void Set(State state, IReadOnlyList<Note> notes)
        {
            bool changed;
            lock (Gate)
            {
                changed = state != _state || notes.Count != _notes.Count || !notes.Select(n => n.Id).SequenceEqual(_notes.Select(n => n.Id));
                _state = state;
                _notes = notes;
            }
            if (changed) Changed?.Invoke();
        }

        /// <summary>Asks Windows to let Wibn read notifications (Windows shows its own question). On the window thread.</summary>
        public static async void AskAccess()
        {
            try
            {
                await UserNotificationListener.Current.RequestAccessAsync();
                Refresh();
            }
            catch (Exception ex) { CrashLog.Write("discord notes access", ex); }
        }

        /// <summary>Takes one (or all) of Discord's notifications off the notification center.</summary>
        public static void Remove(IEnumerable<Note> notes)
        {
            try
            {
                var listener = UserNotificationListener.Current;
                foreach (var n in notes) try { listener.RemoveNotification(n.Id); } catch { }
            }
            catch { }
            Refresh();
        }

        /// <summary>Opens Discord (brings it up if it's running).</summary>
        public static void OpenDiscord()
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("discord://-/channels/@me") { UseShellExecute = true }); }
            catch (Exception ex) { CrashLog.Write("open discord", ex); }
        }

        /// <summary>"now", "5 min", "2 h"… for a notification's time.</summary>
        public static string Ago(DateTimeOffset t)
        {
            var d = DateTimeOffset.Now - t;
            if (d.TotalMinutes < 1) return Lang.T("الحين");
            if (d.TotalHours < 1) return Lang.F("{0} د", (int)d.TotalMinutes);
            if (d.TotalDays < 1) return Lang.F("{0} س", (int)d.TotalHours);
            return Lang.F("{0} ي", (int)d.TotalDays);
        }
    }
}
