using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace WinPlus
{
    /// <summary>
    /// "Sign in" for downloads: a small browser window inside Wibn (Edge's WebView2).
    /// The user signs in to Instagram / Reddit / X / TikTok there once; when it's closed,
    /// that window's own cookies are saved as a cookies.txt in Wibn's folder, which the
    /// download tools use when a site wants a sign-in. Chrome's and Edge's own sign-ins
    /// are never touched (they are locked to those browsers on purpose).
    /// </summary>
    internal sealed class SignInWindow : Window
    {
        private static readonly (string Name, string Url, string[] Domains)[] Sites =
        {
            ("Instagram", "https://www.instagram.com/accounts/login/", new[] { "https://www.instagram.com" }),
            ("Reddit", "https://www.reddit.com/login/", new[] { "https://www.reddit.com", "https://old.reddit.com" }),
            ("X", "https://x.com/i/flow/login", new[] { "https://x.com", "https://twitter.com" }),
            ("TikTok", "https://www.tiktok.com/login", new[] { "https://www.tiktok.com" }),
            ("Facebook", "https://www.facebook.com/login", new[] { "https://www.facebook.com" }),
        };

        private static string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus");

        /// <summary>Where the saved sign-ins go.</summary>
        public static string CookiesPath => Path.Combine(Root, "cookies.txt");

        private readonly WebView2 _web = new();
        private readonly TextBlock _status;
        private bool _saved;

        public SignInWindow()
        {
            Title = Lang.T("تسجيل الدخول للتحميل");
            Width = 1000; Height = 760;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FlowDirection = Lang.Flow;
            Background = (Brush)Application.Current.FindResource("RowBg");
            try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/app.ico")); } catch { }

            var bar = new DockPanel { Margin = new Thickness(12, 10, 12, 10) };
            var done = new Button { Style = (Style)Application.Current.FindResource("AccentButton"), Content = Lang.T("خلصت، احفظ"), Padding = new Thickness(18, 6, 18, 6) };
            done.Click += async (_, _) => { await SaveAsync(); Close(); };
            DockPanel.SetDock(done, Dock.Right);
            bar.Children.Add(done);
            var sites = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var site in Sites)
            {
                var b = new Button { Style = (Style)Application.Current.FindResource("FlatButton"), Content = site.Name, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 6, 0) };
                string url = site.Url;
                b.Click += (_, _) => { if (_web.CoreWebView2 != null) _web.CoreWebView2.Navigate(url); };
                sites.Children.Add(b);
            }
            bar.Children.Add(sites);

            _status = new TextBlock
            {
                Margin = new Thickness(14, 0, 14, 8), TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.FindResource("Muted"),
                Text = Lang.T("سجّل دخولك في المواقع اللي تبي تحمّل منها، وبعدين اضغط \"خلصت، احفظ\". تسجيل الدخول يبقى في Wibn بس، وما يتشارك مع أي أحد."),
            };

            var layout = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            DockPanel.SetDock(_status, Dock.Top);
            layout.Children.Add(bar);
            layout.Children.Add(_status);
            layout.Children.Add(_web);
            Content = layout;

            Loaded += async (_, _) => await StartAsync();
            Closing += async (_, e) =>
            {
                if (_saved) return;
                e.Cancel = true;          // save first, then close for real
                await SaveAsync();
                _ = Dispatcher.BeginInvoke(new Action(Close));
            };
        }

        private async Task StartAsync()
        {
            try
            {
                // Its own profile, kept between runs: signed in once is signed in next time.
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Root, "signin"));
                await _web.EnsureCoreWebView2Async(env);
                _web.CoreWebView2.Navigate(Sites[0].Url);
            }
            catch (Exception ex)
            {
                CrashLog.Write("sign-in window", ex);
                _status.Text = Lang.T("ما قدرت أفتح نافذة تسجيل الدخول (يحتاج Microsoft Edge WebView2 اللي يجي مع ويندوز 11).");
            }
        }

        /// <summary>This window's cookies for the sites → cookies.txt (the format the tools read).</summary>
        private async Task SaveAsync()
        {
            if (_saved) return;
            _saved = true;
            try
            {
                if (_web.CoreWebView2 == null) return;
                var manager = _web.CoreWebView2.CookieManager;
                var seen = new HashSet<string>();
                var sb = new StringBuilder("# Netscape HTTP Cookie File\n# Saved by Wibn from its own sign-in window.\n\n");
                int count = 0;
                foreach (var url in Sites.SelectMany(s => s.Domains))
                {
                    foreach (var c in await manager.GetCookiesAsync(url))
                    {
                        if (!seen.Add(c.Domain + "\t" + c.Path + "\t" + c.Name)) continue;
                        string domain = c.Domain;
                        bool sub = domain.StartsWith(".");
                        long expires = c.IsSession ? 0 : new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds();
                        sb.Append(c.IsHttpOnly ? "#HttpOnly_" : "").Append(domain).Append('\t')
                          .Append(sub ? "TRUE" : "FALSE").Append('\t').Append(c.Path).Append('\t')
                          .Append(c.IsSecure ? "TRUE" : "FALSE").Append('\t').Append(expires).Append('\t')
                          .Append(c.Name).Append('\t').Append(c.Value).Append('\n');
                        count++;
                    }
                }
                Directory.CreateDirectory(Root);
                await File.WriteAllTextAsync(CookiesPath, sb.ToString(), new UTF8Encoding(false));
                VideoDownloader.CookiesFile = CookiesPath;
                CrashLog.Note($"sign-in window: saved {count} cookies");
            }
            catch (Exception ex)
            {
                CrashLog.Write("sign-in save", ex);
            }
        }

        /// <summary>Forget everything signed in here (the window's profile and the saved file).</summary>
        public static void SignOut()
        {
            Converter.TryDelete(CookiesPath);
            try { Directory.Delete(Path.Combine(Root, "signin"), true); } catch { }
            if (VideoDownloader.CookiesFile == null || string.Equals(VideoDownloader.CookiesFile, CookiesPath, StringComparison.OrdinalIgnoreCase))
                VideoDownloader.CookiesFile = null;
        }
    }
}
