using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace WinPlus
{
    /// <summary>One appointment on the calendar.</summary>
    internal sealed class Appointment
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "";
        /// <summary>Its day (the time part is ignored).</summary>
        public DateTime Date { get; set; }
        /// <summary>Its time in minutes after midnight; null = all day.</summary>
        public int? Minutes { get; set; }
        /// <summary>#RRGGBB.</summary>
        public string Color { get; set; } = "#4C8DFF";
        /// <summary>A Windows notification when it comes.</summary>
        public bool Notify { get; set; }
        /// <summary>How many minutes before it the notification comes.</summary>
        public int Before { get; set; }
        /// <summary>Its notification was already shown (or missed while Wibn was closed).</summary>
        public bool Notified { get; set; }

        /// <summary>When it is: its time, or 8 in the morning for an all-day one.</summary>
        [JsonIgnore] public DateTime When => Date.Date.AddMinutes(Minutes ?? 8 * 60);
        /// <summary>When its notification comes (an all-day one: 8 in the morning, never "before").</summary>
        [JsonIgnore] public DateTime AlertAt => Minutes == null ? When : When.AddMinutes(-Before);
    }

    /// <summary>
    /// The calendar's appointments (shared by every calendar widget), saved in
    /// %LocalAppData%\WinPlus\appointments.json, and their notifications: checked every
    /// 15 seconds while Wibn runs. One missed while Wibn was closed isn't shown late
    /// (more than two hours after its time).
    /// </summary>
    internal static class Appointments
    {
        private static readonly List<Appointment> _list = new();
        private static bool _loaded;
        private static DispatcherTimer? _timer;

        /// <summary>One was added, changed or removed.</summary>
        public static event Action? Changed;

        public static IReadOnlyList<Appointment> All { get { Load(); return _list; } }

        /// <summary>That day's, all-day ones first, then by time.</summary>
        public static List<Appointment> OnDay(DateTime day) =>
            All.Where(a => a.Date.Date == day.Date).OrderBy(a => a.Minutes ?? -1).ThenBy(a => a.Title).ToList();

        /// <summary>The next ones from now (today's all-day ones count until the day ends).</summary>
        public static List<Appointment> Upcoming(int count)
        {
            var now = DateTime.Now;
            return All.Where(a => a.Minutes == null ? a.Date.Date >= now.Date : a.When >= now.AddMinutes(-30))
                      .OrderBy(a => a.Date.Date).ThenBy(a => a.Minutes ?? -1).Take(count).ToList();
        }

        public static void Add(Appointment a)
        {
            Load();
            a.Notified = a.AlertAt <= DateTime.Now && !(a.Notify && a.When > DateTime.Now);
            _list.Add(a);
            Save();
        }

        /// <summary>Saves a changed one (its notification comes again if its time moved).</summary>
        public static void Update(Appointment a)
        {
            Load();
            if (!_list.Contains(a)) _list.Add(a);
            a.Notified = a.AlertAt <= DateTime.Now && !(a.Notify && a.When > DateTime.Now);
            Save();
        }

        public static void Remove(Appointment a)
        {
            Load();
            _list.Remove(a);
            Save();
        }

        // ---- Notifications ----------------------------------------------------------------------

        /// <summary>Starts checking for appointments that come (once; called at Wibn's start).</summary>
        public static void StartReminders()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
            _timer.Tick += (_, _) => { try { Check(); } catch (Exception ex) { CrashLog.Write("appointments", ex); } };
            _timer.Start();
            Check();
        }

        private static void Check()
        {
            var now = DateTime.Now;
            bool changed = false;
            foreach (var a in All.Where(a => !a.Notified && a.AlertAt <= now).ToList())
            {
                a.Notified = true;
                changed = true;
                if (!a.Notify || now > a.When.AddHours(2)) continue;   // missed while Wibn was closed
                App.Notify(Lang.F("موعد: {0}", a.Title), CalText.Alert(a));
            }
            if (changed) Save();
        }

        // ---- Saving -----------------------------------------------------------------------------

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus", "appointments.json");

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                var list = JsonSerializer.Deserialize<List<Appointment>>(File.ReadAllText(FilePath));
                if (list != null) _list.AddRange(list.Where(a => a != null));
            }
            catch (Exception ex) { CrashLog.Write("appointments load", ex); }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_list));
            }
            catch (Exception ex) { CrashLog.Write("appointments save", ex); }
            Changed?.Invoke();
        }
    }
}
