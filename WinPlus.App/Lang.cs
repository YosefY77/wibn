using System;
using System.Windows;
using System.Windows.Markup;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Arabic (the default) or English.
    ///
    /// The app is written in Arabic. In English, every Arabic text gets swapped for
    /// its English from <see cref="En"/> (Lang.En.cs):
    ///   * text set in code goes through <see cref="T"/> / <see cref="F"/>;
    ///   * text written in XAML is {local:Tr 'نص'} (<see cref="TrExtension"/>), and
    ///     right-to-left is {x:Static local:TrExtension.Flow}.
    /// Changing the language restarts the app.
    /// </summary>
    internal static partial class Lang
    {
        private const string Key = @"Software\WinPlus";

        public static readonly bool English = ReadEnglish();

        private static bool ReadEnglish()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(Key);
                return key?.GetValue("Language") as string == "en";
            }
            catch { return false; }
        }

        /// <summary>Takes effect on the next start.</summary>
        public static void Save(bool english)
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue("Language", english ? "en" : "ar");
        }

        /// <summary>The text in the app's language.</summary>
        public static string T(string arabic) =>
            English && arabic != null && En.TryGetValue(arabic, out var en) ? en : arabic!;

        /// <summary>Like string.Format, with the format in the app's language.</summary>
        public static string F(string arabic, params object?[] args)
        {
            try { return string.Format(T(arabic), args); }
            catch (FormatException) { return string.Format(arabic, args); }
        }

        public static FlowDirection Flow => English ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;

        public static MessageBoxOptions MsgOptions =>
            English ? MessageBoxOptions.None : MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign;

        // ---- XAML text ------------------------------------------------------------------------

        /// <summary>Call once at start, before any window.</summary>
        public static void Init(Application app)
        {
            // The on/off label inside every switch.
            app.Resources["SwitchOn"] = T("تشغيل");
            app.Resources["SwitchOff"] = T("إيقاف");
        }
    }
}

namespace WinPlus
{
    /// <summary>XAML: Text="{local:Tr 'نص'}" — the text in the app's language.</summary>
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class TrExtension : MarkupExtension
    {
        public TrExtension() { }
        public TrExtension(string text) { Text = text; }

        [ConstructorArgument("text")]
        public string Text { get; set; } = "";

        public override object ProvideValue(IServiceProvider serviceProvider) => Lang.T(Text);

        /// <summary>Right-to-left in Arabic, left-to-right in English.</summary>
        public static FlowDirection Flow => Lang.Flow;
    }
}
