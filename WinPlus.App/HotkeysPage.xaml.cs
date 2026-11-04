using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinPlus
{
    /// <summary>Row shown in the shortcut list.</summary>
    public sealed class HotkeyRow
    {
        public HotkeyRow(HotkeyItem item, bool failed)
        {
            Item = item;
            KeyText = HotkeyManager.KeyText(item.Modifiers, item.Key);
            ActionText = HotkeyManager.ActionLabel(item.Action);
            Detail = failed ? Lang.T("برنامج ثاني حاجز هالأزرار، اختر غيرها")
                            : item.Action == "open" ? item.Target : "";
            DetailBrush = failed ? new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80))
                                 : new SolidColorBrush(Color.FromRgb(0xA0, 0xA3, 0xAA));
        }

        public HotkeyItem Item { get; }
        public string KeyText { get; }
        public string ActionText { get; }
        public string Detail { get; }
        public Brush DetailBrush { get; }
    }

    public partial class HotkeysPage : UserControl
    {
        private List<HotkeyItem> _items;
        private uint _mods, _vk;

        public HotkeysPage()
        {
            InitializeComponent();
            foreach (var (id, label) in HotkeyManager.Actions)
            {
                ActionBox.Items.Add(new ComboBoxItem { Content = label, Tag = id });
            }
            ActionBox.SelectedIndex = 0;
            KeyBox.Text = Lang.T("اضغط هنا ثم اضغط الأزرار");
            _items = HotkeyManager.Load();
            Refresh(HotkeyManager.Apply(_items));
        }

        private void Refresh(List<HotkeyItem> failed)
        {
            HotkeyList.ItemsSource = _items.Select(i => new HotkeyRow(i, failed.Contains(i))).ToList();
            EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- Key capture ------------------------------------------------------

        private void OnKeyBoxFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (_vk == 0) KeyBox.Text = Lang.T("اضغط الأزرار الحين…");
        }

        private void OnKeyBoxBlur(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (_vk == 0) KeyBox.Text = Lang.T("اضغط هنا ثم اضغط الأزرار");
        }

        private void OnKeyCapture(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift
                    or Key.RightShift or Key.LWin or Key.RWin or Key.None or Key.Tab)
            {
                return;  // wait for the actual key
            }

            uint mods = 0;
            var m = Keyboard.Modifiers;
            if (m.HasFlag(ModifierKeys.Control)) mods |= HotkeyManager.MOD_CONTROL;
            if (m.HasFlag(ModifierKeys.Alt)) mods |= HotkeyManager.MOD_ALT;
            if (m.HasFlag(ModifierKeys.Shift)) mods |= HotkeyManager.MOD_SHIFT;
            if (m.HasFlag(ModifierKeys.Windows) || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
                mods |= HotkeyManager.MOD_WIN;

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            bool isFunctionKey = key >= Key.F1 && key <= Key.F24;
            if (mods == 0 && !isFunctionKey)
            {
                KeyBox.Text = Lang.T("لازم معه Ctrl أو Alt أو Shift أو Win");
                _vk = 0;
                return;
            }

            _mods = mods;
            _vk = vk;
            KeyBox.Text = HotkeyManager.KeyText(mods, vk);
            AddStatus.Text = "";
        }

        // ---- Add / remove -------------------------------------------------------

        private string SelectedAction => (ActionBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "open";

        private void OnActionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TargetRow == null) return;
            var vis = SelectedAction == "open" ? Visibility.Visible : Visibility.Collapsed;
            TargetRow.Visibility = vis;
            TargetLabel.Visibility = vis;
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Lang.T("اختر البرنامج أو الملف"),
                Filter = Lang.T("البرامج|*.exe;*.lnk;*.bat;*.cmd|كل الملفات|*.*"),
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            {
                TargetBox.Text = dialog.FileName;
            }
        }

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            if (_vk == 0)
            {
                AddStatus.Text = Lang.T("اختر الأزرار أول");
                return;
            }
            if (SelectedAction == "open" && string.IsNullOrWhiteSpace(TargetBox.Text))
            {
                AddStatus.Text = Lang.T("اكتب مسار البرنامج أو الرابط");
                return;
            }
            if (_items.Any(i => i.Modifiers == _mods && i.Key == _vk))
            {
                AddStatus.Text = Lang.T("هالأزرار مستخدمة في اختصار ثاني");
                return;
            }

            var item = new HotkeyItem
            {
                Modifiers = _mods,
                Key = _vk,
                Action = SelectedAction,
                Target = SelectedAction == "open" ? TargetBox.Text.Trim().Trim('"') : "",
            };
            _items.Add(item);
            HotkeyManager.Save(_items);
            var failed = HotkeyManager.Apply(_items);
            Refresh(failed);
            AddStatus.Text = failed.Contains(item) ? Lang.T("انضاف، بس برنامج ثاني حاجز هالأزرار") : "";

            _vk = 0;
            _mods = 0;
            KeyBox.Text = Lang.T("اضغط هنا ثم اضغط الأزرار");
            TargetBox.Text = "";
        }

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is HotkeyRow row)
            {
                _items.Remove(row.Item);
                HotkeyManager.Save(_items);
                Refresh(HotkeyManager.Apply(_items));
            }
        }
    }
}
