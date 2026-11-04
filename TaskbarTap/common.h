#pragma once

// unknwn.h must come before winrt/base.h so that C++/WinRT can implement
// classic COM interfaces (IObjectWithSite, IClassFactory, ...).
#include <windows.h>
#include <unknwn.h>
#include <ocidl.h>
#include <xamlOM.h>

// windows.h defines GetCurrentTime as a macro, which breaks C++/WinRT headers.
#undef GetCurrentTime

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.Numerics.h>
#include <winrt/Windows.UI.Composition.h>
#include <winrt/Windows.UI.Xaml.Hosting.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.UI.h>
#include <winrt/Windows.UI.Input.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Controls.Primitives.h>
#include <winrt/Windows.UI.Xaml.Input.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.Shapes.h>

#include <string>

// {CEAE01AC-5556-49A0-BF40-57309780A14B}
// Must match TapClsid in WinPlus.App/TaskbarInjector.cs
inline constexpr CLSID CLSID_WinPlusTap = {
    0xceae01ac, 0x5556, 0x49a0, {0xbf, 0x40, 0x57, 0x30, 0x97, 0x80, 0xa1, 0x4b}};

// Names shared with the WPF app.
inline constexpr wchar_t kSettingsKey[] = L"Software\\WinPlus\\TaskbarScroll";
inline constexpr wchar_t kLoadedMutexName[] = L"Local\\WinPlus.TaskbarScroll.Loaded";
inline constexpr wchar_t kRefreshEventName[] = L"Local\\WinPlus.TaskbarScroll.Refresh";

struct Settings {
    bool enabled = true;       // user toggle
    bool appActive = true;     // false when the WPF app exits
    int scrollStep = 1;        // icons per wheel notch
    bool reverse = false;      // invert wheel direction
    bool smooth = true;        // animate scrolling
    bool colorEnabled = false; // exact taskbar color
    uint32_t color = 0xFF202020; // ARGB
    int iconSize = 0;          // taskbar icon size in px, 0 = Windows default
    bool showYear = false;     // add the year to the taskbar date
    bool runningFirst = false; // running apps before pinned-only ones (drawn moved)
    int taskbarStyle = 0;      // 0 = solid color, 1 = glass (blur), 2 = see-through
    int taskbarOpacity = 60;   // 0-100: how much color over the glass / see-through
    bool floating = false;     // gap around the taskbar + rounded corners
    bool magnify = false;      // icons grow under the mouse (like a dock)
    bool indicatorColor = false; // running line takes the app icon's color
    int extraSpace = 20000;    // DIPs added to the task list so it never overflows
    bool debugDump = true;     // write the taskbar tree to the log once

    bool IsOn() const { return enabled && appActive; }
};

Settings LoadSettings();

void Log(const wchar_t* fmt, ...);
