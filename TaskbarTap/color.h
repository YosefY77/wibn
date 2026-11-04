#pragma once

#include "common.h"

// Also handles the taskbar icon size (same "change a property, keep the
// original, re-apply if Windows changes it" pattern).
//
// Exact color for the taskbar, the tray overflow flyout and the Start menu.
//
// Windows draws these with acrylic/mica and blends the accent color in, so the
// color you pick never shows up as-is. We replace their background with a plain
// SolidColorBrush of the exact color, and put Windows' own brush back when the
// feature is turned off. If Windows re-applies its brush (theme change), we
// re-apply ours.
//
//   Taskbar:        Rectangle#BackgroundFill inside Taskbar.TaskbarBackground
//   Tray overflow:  Border#OverflowFlyoutBackgroundBorder
//   Start menu:     Border#AcrylicBorder (+ Border#AcrylicOverlay hidden)

enum class ColorTargetKind {
    RectFill,          // Shapes::Rectangle.Fill
    BorderBackground,  // Border/Panel/Control Background
    Hide,              // tint layer on top: collapse it
    IconSize,          // taskbar icon Image: Width/Height
    DateYear,          // taskbar date TextBlock: add the year
    Indicator,         // running-app line under a task button: the icon's color
};

// UI thread. requiredAncestor: class name the element must be inside, or null.
void TrackColorTarget(winrt::Windows::UI::Xaml::FrameworkElement const& element,
                      ColorTargetKind kind, const wchar_t* requiredAncestor);

// Any thread: posts to each element's UI thread.
void ApplyColorToAll(const Settings& settings);
