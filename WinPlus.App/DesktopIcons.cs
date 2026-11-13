using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WinPlus
{
    /// <summary>
    /// Reads and sets where an icon sits on the desktop, so a converted file
    /// (new name = new icon) lands exactly where the old one was instead of in
    /// the first free spot. Uses the desktop's own shell view (IFolderView), the
    /// same thing Explorer uses when you drag icons around.
    /// Must be called on an STA thread (the UI thread).
    /// </summary>
    internal static class DesktopIcons
    {
        public static bool IsOnDesktop(string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            return string.Equals(dir, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dir, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The icon's position, or null if not on the desktop / auto-arranged.</summary>
        public static POINT? GetPosition(string path)
        {
            if (!IsOnDesktop(path)) return null;
            try
            {
                var view = DesktopView();
                if (view == null || view.GetAutoArrange() == 0 /* S_OK = on */) return null;
                IntPtr pidl = ChildPidl(view, Path.GetFileName(path));
                if (pidl == IntPtr.Zero) return null;
                try
                {
                    view.GetItemPosition(pidl, out POINT pt);
                    return pt;
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
            catch { return null; }
        }

        /// <summary>Puts the icon at that position. False if the icon isn't there yet.</summary>
        public static bool SetPosition(string path, POINT pt)
        {
            try
            {
                var view = DesktopView();
                if (view == null) return false;
                IntPtr pidl = ChildPidl(view, Path.GetFileName(path));
                if (pidl == IntPtr.Zero) return false;
                try
                {
                    // Only once the desktop actually shows the new file.
                    if (view.GetItemPosition(pidl, out _) != 0) return false;
                    view.SelectAndPositionItems(1, new[] { pidl }, new[] { pt }, 0x80 /* SVSI_POSITIONITEM */);
                    return true;
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
            catch { return false; }
        }

        /// <summary>Where one desktop icon sits (desktop list coordinates, real pixels), to
        /// find how the icon grid is shifted. Null if the desktop has no icons.</summary>
        public static POINT? AnyItemPosition()
        {
            try
            {
                var view = DesktopView();
                if (view == null) return null;
                view.ItemCount(2 /* SVGIO_ALLVIEW */, out int count);
                if (count <= 0) return null;
                view.Item(0, out IntPtr pidl);
                if (pidl == IntPtr.Zero) return null;
                try { return view.GetItemPosition(pidl, out POINT pt) == 0 ? pt : null; }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
            catch { return null; }
        }

        /// <summary>The size of one icon cell on the desktop (real pixels), or null.</summary>
        public static POINT? Spacing()
        {
            try
            {
                var view = DesktopView();
                if (view == null) return null;
                var pt = new POINT();
                view.GetSpacing(ref pt);
                return pt.X > 0 && pt.Y > 0 ? pt : null;
            }
            catch { return null; }
        }

        /// <summary>The desktop's shell view itself (for other interfaces on it, like IFolderView2), or null.</summary>
        internal static object? ViewObject()
        {
            try { return DesktopView(); } catch { return null; }
        }

        /// <summary>
        /// A file just made on the desktop: put it where the mouse was (screen pixels) once the
        /// desktop shows it, and start typing its name like Windows does. Tries for a few seconds.
        /// </summary>
        internal static void PlaceNew(string path, int screenX, int screenY)
        {
            int tries = 0;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += (_, _) =>
            {
                if (++tries > 25) { timer.Stop(); return; }
                try
                {
                    var view = DesktopView();
                    if (view == null) return;
                    IntPtr pidl = ChildPidl(view, Path.GetFileName(path));
                    if (pidl == IntPtr.Zero) return;
                    try
                    {
                        if (view.GetItemPosition(pidl, out _) != 0) return;   // not shown yet
                        timer.Stop();
                        // Desktop list coordinates: from the list window's corner.
                        var list = FindDesktopList();
                        int ox = 0, oy = 0;
                        if (list != IntPtr.Zero && GetWindowRect(list, out var r)) { ox = r.Left; oy = r.Top; }
                        var pt = new POINT { X = screenX - ox, Y = screenY - oy };
                        uint flags = 0x3 /* SVSI_EDIT */ | 0x4 /* DESELECTOTHERS */ | 0x8 /* ENSUREVISIBLE */;
                        if (view.GetAutoArrange() != 0) flags |= 0x80;   // SVSI_POSITIONITEM (not when auto-arranged)
                        if (list != IntPtr.Zero) SetForegroundWindow(list);
                        view.SelectAndPositionItems(1, new[] { pidl }, new[] { pt }, flags);
                    }
                    finally { Marshal.FreeCoTaskMem(pidl); }
                }
                catch { timer.Stop(); }
            };
            timer.Start();
        }

        /// <summary>The window that shows the desktop icons (SysListView32 inside Progman / WorkerW).</summary>
        internal static IntPtr FindDesktopList()
        {
            IntPtr found = IntPtr.Zero;
            foreach (var top in new[] { "Progman", "WorkerW" })
            {
                IntPtr h = IntPtr.Zero;
                while (found == IntPtr.Zero && (h = FindWindowEx(IntPtr.Zero, h, top, null)) != IntPtr.Zero)
                {
                    IntPtr def = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (def != IntPtr.Zero) found = FindWindowEx(def, IntPtr.Zero, "SysListView32", null);
                }
            }
            return found;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        private static IFolderView? DesktopView()
        {
            var windows = (IShellWindows)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!)!;
            object loc = 0 /* CSIDL_DESKTOP */, root = null!;  // empty VARIANT
            object? disp = windows.FindWindowSW(ref loc, ref root, 8 /* SWC_DESKTOP */, out _, 1 /* SWFO_NEEDDISPATCH */);
            if (disp is not IServiceProvider sp) return null;
            var sidTop = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var iidBrowser = typeof(IShellBrowser).GUID;
            sp.QueryService(ref sidTop, ref iidBrowser, out object browserObj);
            var browser = (IShellBrowser)browserObj;
            browser.QueryActiveShellView(out object viewObj);
            return viewObj as IFolderView;
        }

        private static IntPtr ChildPidl(IFolderView view, string name)
        {
            var iid = typeof(IShellFolder).GUID;
            view.GetFolder(ref iid, out object folderObj);
            var folder = (IShellFolder)folderObj;
            uint attrs = 0;
            return folder.ParseDisplayName(IntPtr.Zero, IntPtr.Zero, name, out _, out IntPtr pidl, ref attrs) == 0
                ? pidl : IntPtr.Zero;
        }

        // ---- COM ---------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IShellWindows
        {
            int Count { get; }
            [return: MarshalAs(UnmanagedType.IDispatch)] object Item(object index);
            [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
            int Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass);
            int RegisterPending(int threadId, ref object loc, ref object locRoot, int swClass);
            void Revoke(int cookie);
            void OnNavigate(int cookie, ref object loc);
            void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object FindWindowSW(ref object loc, ref object locRoot, int swClass, out int hwnd, int options);
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider
        {
            void QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        }

        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowser
        {
            void GetWindow(out IntPtr hwnd);
            void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
            void InsertMenusSB(IntPtr menu, IntPtr widths);
            void SetMenuSB(IntPtr menu, IntPtr olemenu, IntPtr active);
            void RemoveMenusSB(IntPtr menu);
            void SetStatusTextSB(IntPtr text);
            void EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enable);
            void TranslateAcceleratorSB(IntPtr msg, ushort id);
            void BrowseObject(IntPtr pidl, uint flags);
            void GetViewStateStream(uint mode, out IntPtr stream);
            void GetControlWindow(uint id, out IntPtr hwnd);
            void SendControlMsg(uint id, uint msg, IntPtr w, IntPtr l, out IntPtr result);
            void QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object view);
        }

        [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFolderView
        {
            void GetCurrentViewMode(out uint mode);
            void SetCurrentViewMode(uint mode);
            void GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object folder);
            void Item(int index, out IntPtr pidl);
            void ItemCount(uint flags, out int count);
            void Items(uint flags, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object items);
            void GetSelectionMarkedItem(out int item);
            void GetFocusedItem(out int item);
            [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT pt);
            void GetSpacing(ref POINT pt);
            void GetDefaultSpacing(out POINT pt);
            [PreserveSig] int GetAutoArrange();
            void SelectItem(int item, uint flags);
            void SelectAndPositionItems(uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls,
                                        [MarshalAs(UnmanagedType.LPArray)] POINT[] points, uint flags);
        }

        [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellFolder
        {
            [PreserveSig]
            int ParseDisplayName(IntPtr hwnd, IntPtr bindCtx, [MarshalAs(UnmanagedType.LPWStr)] string name,
                                 out uint eaten, out IntPtr pidl, ref uint attributes);
        }
    }
}
