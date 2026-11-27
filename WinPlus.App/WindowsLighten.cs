using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// "Lighten Windows": settings that make Windows lighter while you play and work, from your own
    /// settings only (HKCU and the power mode; nothing that needs admin, no system files, no services removed).
    /// Each one keeps what it changed under HKCU\Software\WinPlus\Lighten\&lt;id&gt; the moment it's turned
    /// on, and turning it off puts those exact values back (or removes them when they weren't there).
    /// </summary>
    internal static class WindowsLighten
    {
        internal sealed record Tweak(string Id, string Label, string Description, string Icon, bool Recommended, Action Apply, Action Revert);

        private const string Root = @"Software\WinPlus\Lighten";
        private const string Missing = "\u0001missing";   // the value wasn't there before

        public static readonly Tweak[] All =
        {
            new("gamedvr", Lang.T("تسجيل الألعاب في الخلفية"), Lang.T("ويندوز يسجّل آخر دقائق من لعبك طول الوقت، وهذا ياخذ من كرت الشاشة والقرص. يوقفه (اختصار Win+Alt+R ما يشتغل لين ترجّعه)"), "", true,
                () =>
                {
                    SetDword("gamedvr", @"System\GameConfigStore", "GameDVR_Enabled", 0);
                    SetDword("gamedvr", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0);
                },
                () => RevertAll("gamedvr")),
            new("gamemode", Lang.T("وضع اللعب حق ويندوز"), Lang.T("يخلي ويندوز يعطي اللعبة الأولوية ويأجّل التحديثات وهي شغالة"), "", true,
                () =>
                {
                    SetDword("gamemode", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1);
                    SetDword("gamemode", @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1);
                },
                () => RevertAll("gamemode")),
            new("power", Lang.T("أعلى أداء للطاقة"), Lang.T("وضع الطاقة يصير \"أفضل أداء\" عشان المعالج ما يبطّئ نفسه. باللابتوب البطارية تخلص أسرع"), "", true,
                ApplyPower, RevertPower),
            new("animations", Lang.T("حركات ويندوز"), Lang.T("يطفي الحركات والظلال الصغيرة (القوائم، التلميحات، ظل الماوس…) وتصير النوافذ أسرع"), "", true,
                ApplyAnimations, RevertAnimations),
            new("transparency", Lang.T("شفافية ويندوز"), Lang.T("يطفي الزجاج الشفاف في قائمة ابدأ والإعدادات، يخفف على كرت الشاشة"), "", false,
                () =>
                {
                    SetDword("transparency", @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0);
                    Broadcast("ImmersiveColorSet");
                },
                () => { RevertAll("transparency"); Broadcast("ImmersiveColorSet"); }),
            new("suggestions", Lang.T("الإعلانات والبرامج اللي تنزل لحالها"), Lang.T("يوقف الاقتراحات والنصائح، والبرامج اللي ويندوز ينزّلها من نفسه في الخلفية"), "", true,
                () =>
                {
                    const string k = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
                    foreach (var name in new[] { "SilentInstalledAppsEnabled", "SystemPaneSuggestionsEnabled", "SoftLandingEnabled",
                                                 "SubscribedContent-338388Enabled", "SubscribedContent-338389Enabled",
                                                 "SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled", "SubscribedContent-310093Enabled" })
                        SetDword("suggestions", k, name, 0);
                },
                () => RevertAll("suggestions")),
            new("background", Lang.T("برامج المتجر في الخلفية"), Lang.T("برامج متجر مايكروسوفت ما تشتغل في الخلفية. ممكن توقف إشعارات بعضها (مثل واتساب من المتجر)"), "", false,
                () =>
                {
                    SetDword("background", @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1);
                    SetDword("background", @"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 0);
                },
                () => RevertAll("background")),
        };

        /// <summary>Wibn turned this one on (and keeps what was there before).</summary>
        public static bool IsOn(Tweak t)
        {
            using var k = Registry.CurrentUser.OpenSubKey(Root);
            return k?.GetValue("on_" + t.Id) is int v && v != 0;
        }

        /// <summary>Turns it on or off; false when it couldn't (what it did is put back).</summary>
        public static bool Set(Tweak t, bool on)
        {
            if (on == IsOn(t)) return true;
            try
            {
                if (on)
                {
                    try { t.Apply(); }
                    catch { try { t.Revert(); } catch { } ClearBackup(t.Id); throw; }
                    using var k = Registry.CurrentUser.CreateSubKey(Root);
                    k.SetValue("on_" + t.Id, 1, RegistryValueKind.DWord);
                }
                else
                {
                    t.Revert();
                    ClearBackup(t.Id);
                    using var k = Registry.CurrentUser.CreateSubKey(Root);
                    k.DeleteValue("on_" + t.Id, false);
                }
                return true;
            }
            catch (Exception ex)
            {
                CrashLog.Write("lighten " + t.Id, ex);
                return false;
            }
        }

        /// <summary>Everything Wibn lightened goes back (the "put back" button, and Wibn being uninstalled). False if one couldn't.</summary>
        public static bool UndoAll()
        {
            bool ok = true;
            foreach (var t in All) ok &= Set(t, false);
            return ok;
        }

        // ---- Registry values, with what was there before ----------------------------------------------

        private static void SetDword(string id, string keyPath, string name, int value)
        {
            using (var b = Registry.CurrentUser.CreateSubKey(Root + "\\" + id))
            {
                string slot = keyPath + "|" + name;
                if (b.GetValue(slot) == null)   // the first change keeps the original (never what Wibn set)
                {
                    using var k0 = Registry.CurrentUser.OpenSubKey(keyPath);
                    object? old = k0?.GetValue(name);
                    if (old is int i) b.SetValue(slot, i.ToString(), RegistryValueKind.String);
                    else b.SetValue(slot, Missing, RegistryValueKind.String);
                }
            }
            using var k = Registry.CurrentUser.CreateSubKey(keyPath);
            k.SetValue(name, value, RegistryValueKind.DWord);
        }

        /// <summary>Every value this one changed goes back to what it was.</summary>
        private static void RevertAll(string id)
        {
            using var b = Registry.CurrentUser.OpenSubKey(Root + "\\" + id);
            if (b == null) return;
            foreach (var slot in b.GetValueNames())
            {
                int bar = slot.LastIndexOf('|');
                if (bar <= 0) continue;
                string keyPath = slot[..bar], name = slot[(bar + 1)..];
                string? old = b.GetValue(slot) as string;
                using var k = Registry.CurrentUser.CreateSubKey(keyPath);
                if (old == null || old == Missing) k.DeleteValue(name, false);
                else if (int.TryParse(old, out int v)) k.SetValue(name, v, RegistryValueKind.DWord);
            }
        }

        private static void ClearBackup(string id)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(Root + "\\" + id, false); } catch { }
        }

        private static string? Backup(string id, string slot)
        {
            using var b = Registry.CurrentUser.OpenSubKey(Root + "\\" + id);
            return b?.GetValue(slot) as string;
        }

        private static void KeepBackup(string id, string slot, string value)
        {
            using var b = Registry.CurrentUser.CreateSubKey(Root + "\\" + id);
            if (b.GetValue(slot) == null) b.SetValue(slot, value, RegistryValueKind.String);
        }

        // ---- Power mode (the "Power mode" in Settings → Power) ---------------------------------------

        private static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

        private static void ApplyPower()
        {
            if (PowerGetEffectiveOverlayScheme(out Guid now) != 0) now = Guid.Empty;   // balanced
            KeepBackup("power", "overlay", now.ToString());
            if (PowerSetActiveOverlayScheme(BestPerformance) != 0) throw new InvalidOperationException("power mode not changed");
        }

        private static void RevertPower()
        {
            var old = Backup("power", "overlay");
            PowerSetActiveOverlayScheme(old != null && Guid.TryParse(old, out var g) ? g : Guid.Empty);
        }

        // ---- Small animations and shadows (not the minimize one: Wibn's own minimize animation owns it) ---

        private static readonly (string Name, uint Get, uint Set)[] Effects =
        {
            ("ClientAreaAnimation", 0x1042, 0x1043),
            ("MenuAnimation", 0x1002, 0x1003),
            ("ComboBoxAnimation", 0x1004, 0x1005),
            ("ListBoxSmoothScrolling", 0x1006, 0x1007),
            ("SelectionFade", 0x1014, 0x1015),
            ("ToolTipAnimation", 0x1016, 0x1017),
            ("CursorShadow", 0x101A, 0x101B),
            ("DropShadow", 0x1024, 0x1025),
        };

        private static void ApplyAnimations()
        {
            foreach (var (name, get, set) in Effects)
            {
                int was = 0;
                SystemParametersInfoGet(get, 0, ref was, 0);
                KeepBackup("animations", "spi_" + name, was != 0 ? "1" : "0");
                SystemParametersInfoSet(set, 0, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            }
        }

        private static void RevertAnimations()
        {
            foreach (var (name, _, set) in Effects)
            {
                var old = Backup("animations", "spi_" + name);
                if (old == null) continue;
                SystemParametersInfoSet(set, 0, old == "1" ? new IntPtr(1) : IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            }
        }

        private static void Broadcast(string area)
        {
            SendMessageTimeout(new IntPtr(0xFFFF) /* HWND_BROADCAST */, 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, area, 0x2 /* ABORTIFHUNG */, 1000, out _);
        }

        // ---- Native ---------------------------------------------------------------------------------

        private const uint SPIF_UPDATEINIFILE = 0x1, SPIF_SENDCHANGE = 0x2;

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static extern bool SystemParametersInfoGet(uint action, uint uiParam, ref int pvParam, uint winIni);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static extern bool SystemParametersInfoSet(uint action, uint uiParam, IntPtr pvParam, uint winIni);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid overlay);
        [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
    }
}
