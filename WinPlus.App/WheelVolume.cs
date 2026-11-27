using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace WinPlus
{
    /// <summary>
    /// The mouse wheel on the bars: on the sound waves it moves the device's volume, on an app's button that app's
    /// own volume (all of its sounds, e.g. every tab of a browser). A small bubble shows the new level.
    /// </summary>
    internal static class WheelVolume
    {
        private const float Step = 0.02f;
        private static MMDeviceEnumerator? _enumerator;

        private static MMDevice? Speaker()
        {
            try
            {
                _enumerator ??= new MMDeviceEnumerator();
                return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch { _enumerator = null; return null; }
        }

        /// <summary>The device's volume up (wheel up) or down; shows the level over <paramref name="at"/>.</summary>
        public static void Device(FrameworkElement at, int delta)
        {
            using var d = Speaker();
            if (d == null) return;
            try
            {
                var v = d.AudioEndpointVolume;
                v.MasterVolumeLevelScalar = Math.Clamp(v.MasterVolumeLevelScalar + (delta > 0 ? Step : -Step), 0f, 1f);
                if (v.Mute && delta > 0) v.Mute = false;
                Bubble(at, v.MasterVolumeLevelScalar, v.Mute);
            }
            catch { }
        }

        /// <summary>An app's own volume (its windows' processes, and any other process of the same program).</summary>
        public static void App(FrameworkElement at, TaskbarApps.Group g, int delta)
        {
            var pids = new HashSet<uint>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in g.Windows)
            {
                GetWindowThreadProcessId(w.Hwnd, out uint pid);
                if (pid != 0) pids.Add(pid);
                // A Store app's window belongs to ApplicationFrameHost; the app itself (its sound) is its inner window's process.
                if (w.Uwp && FindWindowEx(w.Hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null) is var inner && inner != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(inner, out uint ipid);
                    if (ipid != 0) pids.Add(ipid);
                }
                if (!string.IsNullOrEmpty(w.Exe) && !w.Exe.EndsWith("applicationframehost.exe", StringComparison.OrdinalIgnoreCase)) paths.Add(w.Exe);
            }
            if (g.Pin?.Target is { Length: > 0 } t) paths.Add(t);
            if (pids.Count == 0 && paths.Count == 0) return;

            using var d = Speaker();
            if (d == null) return;
            try
            {
                var manager = d.AudioSessionManager;
                manager.RefreshSessions();
                var all = manager.Sessions;
                var mine = new List<AudioSessionControl>();
                for (int i = 0; i < all.Count; i++)
                {
                    var s = all[i];
                    if (s.IsSystemSoundsSession || s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                    uint pid = s.GetProcessID;
                    if (pid == 0) continue;
                    if (pids.Contains(pid) || (VolumePage.ProcessPath(pid) is { } p && paths.Contains(p))) mine.Add(s);
                }
                if (mine.Count == 0) { Bubble(at, null, false); return; }

                // All of the app's sounds move together, from the loudest one (so they end up even).
                float level = Math.Clamp(mine.Max(s => s.SimpleAudioVolume.Volume) + (delta > 0 ? Step : -Step), 0f, 1f);
                foreach (var s in mine)
                {
                    s.SimpleAudioVolume.Volume = level;
                    if (s.SimpleAudioVolume.Mute && delta > 0) s.SimpleAudioVolume.Mute = false;
                }
                Bubble(at, level, mine.All(s => s.SimpleAudioVolume.Mute));
            }
            catch { }
        }

        // ---- The level bubble ---------------------------------------------------------------------

        private static ToolTip? _tip;
        private static readonly DispatcherTimer _hide = NewHide();

        private static DispatcherTimer NewHide()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            t.Tick += (_, _) => { t.Stop(); if (_tip != null) _tip.IsOpen = false; };
            return t;
        }

        private static void Bubble(FrameworkElement at, float? level, bool muted)
        {
            string text = level == null ? Lang.T("ما يطلع منه صوت الحين")
                        : muted ? Lang.T("مكتوم")
                        : "🔊 " + (int)Math.Round(level.Value * 100) + "%";
            _tip ??= new ToolTip { Placement = PlacementMode.Top, StaysOpen = true };
            if (_tip.PlacementTarget != at) { _tip.IsOpen = false; _tip.PlacementTarget = at; }
            _tip.Content = text;
            _tip.IsOpen = true;
            _hide.Stop();
            _hide.Start();
        }

        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    }
}
