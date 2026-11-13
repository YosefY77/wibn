#pragma once

#include "common.h"

#include <chrono>
#include <memory>
#include <unordered_map>

// Makes one taskbar's task list scrollable.
//
// How it works:
// 1. The task list (an ItemsRepeater named "TaskbarFrameRepeater") gets a big
//    negative margin along the taskbar axis. The layout then thinks there's
//    plenty of room, so it never moves icons into the "..." overflow menu.
// 2. Every task button is shifted by -scrollOffset (UIElement.Translation, or
//    the composition "Translation" property when that isn't allowed), and gets
//    a clip so only the part between the first task button and the system
//    tray is drawn and clickable.
// 3. Mouse wheel over the task list changes scrollOffset.
//
// Windows itself uses RenderTransform on the task buttons, so we never touch
// it. All positions are measured in XamlRoot coordinates from layout slots, so
// centering, negative margins and our own shift don't confuse the math.
//
// Every method must run on the taskbar's UI thread.
class ScrollController : public std::enable_shared_from_this<ScrollController> {
   public:
    explicit ScrollController(winrt::Windows::UI::Xaml::FrameworkElement repeater);

    void Apply(const Settings& settings);  // enable, update or disable
    void Disable();
    bool IsAlive() const;
    bool Owns(const winrt::Windows::UI::Xaml::FrameworkElement& repeater) const;

   private:
    enum class ShiftMode { None, Facade, Composition, Failed };

    struct Item {
        winrt::weak_ref<winrt::Windows::UI::Xaml::FrameworkElement> element;
        ShiftMode mode = ShiftMode::None;
        winrt::Windows::Foundation::Numerics::float3 baseTranslation{};
        winrt::Windows::UI::Composition::Visual visual{nullptr};
        winrt::Windows::UI::Xaml::Media::RectangleGeometry geometry{nullptr};
        float applied = 0;
        bool clipped = false;
        double clipFrom = -1;
        double clipTo = -1;
        bool seen = false;
        float scale = 1;             // "magnify": current size
        // The button's inner panel (icon + highlight) gets our ScaleTransform; the
        // button itself is animated by Windows (dragging, opening), so we never
        // touch its own transform or composition visual.
        winrt::weak_ref<winrt::Windows::UI::Xaml::FrameworkElement> scalePanel;
        winrt::Windows::UI::Xaml::Media::ScaleTransform scaleTransform{nullptr};
    };

    void Enable();
    void Update();
    void ApplyMargin(const winrt::Windows::UI::Xaml::FrameworkElement& repeater,
                     bool vertical);
    void RestoreMargin(const winrt::Windows::UI::Xaml::FrameworkElement& repeater);
    void ApplyItem(const winrt::Windows::UI::Xaml::FrameworkElement& element,
                   double pos, double extent, double cross, double visibleStart,
                   double visibleEnd, double shift);
    void SetShift(Item& item,
                  const winrt::Windows::UI::Xaml::FrameworkElement& element,
                  float offset);
    void BeginDrag();
    void SetTaskShift(Item& item,
                      const winrt::Windows::UI::Xaml::FrameworkElement& element,
                      float shift);
    void ResetItem(Item& item);
    void StartAnimation();
    void StopAnimation();
    void Settle(int milliseconds);
    void EnsureFrameLoop();
    void StopFrameLoop();
    void UpdateRepeaterClip(
        const winrt::Windows::UI::Xaml::FrameworkElement& repeater,
        const winrt::Windows::UI::Xaml::Media::GeneralTransform& toRoot,
        bool vertical, double visibleEnd);
    void OnFrame();
    void OnWheel(winrt::Windows::UI::Xaml::Input::PointerRoutedEventArgs const& args);
    void DumpTree(const winrt::Windows::UI::Xaml::FrameworkElement& repeater,
                  const wchar_t* reason);

    winrt::weak_ref<winrt::Windows::UI::Xaml::FrameworkElement> m_repeater;
    void* m_repeaterAbi = nullptr;
    Settings m_settings;
    bool m_enabled = false;
    bool m_updating = false;
    bool m_vertical = false;
    bool m_marginApplied = false;
    bool m_marginVertical = false;
    winrt::Windows::UI::Xaml::Thickness m_originalMargin{};
    double m_scroll = 0;
    double m_targetScroll = 0;
    bool m_animating = false;
    bool m_frameLoop = false;
    std::chrono::steady_clock::time_point m_settleUntil{};
    winrt::Windows::UI::Xaml::Media::RectangleGeometry m_repeaterClip{nullptr};
    winrt::Windows::UI::Xaml::Media::RectangleGeometry m_originalRepeaterClip{nullptr};
    bool m_repeaterClipApplied = false;
    std::chrono::steady_clock::time_point m_lastFrame{};
    winrt::event_token m_renderToken{};
    double m_maxScroll = 0;
    double m_itemExtent = 0;
    int m_dumpCount = 0;
    size_t m_lastRunningCount = (size_t)-1;
    // Magnify under the mouse.
    winrt::Windows::Foundation::IInspectable m_moveHandler{nullptr}, m_exitHandler{nullptr};
    double m_pointerAxis = 0;
    bool m_pointerInside = false;
    bool m_magnifyBusy = false;
    bool m_scaleFailed = false;
    // A button is being dragged (reordering): leave Windows alone meanwhile.
    winrt::Windows::Foundation::IInspectable m_pressHandler{nullptr}, m_releaseHandler{nullptr};
    bool m_pressed = false;
    bool m_dragging = false;          // pressed and moved: Windows is reordering
    double m_pressAxis = 0;
    bool m_dragFrozen = false;
    ULONGLONG m_dropUntil = 0;        // after a drop: let Windows finish its animation first
    // Centered taskbar support.
    bool m_centered = false;
    double m_appliedScroll = 0;       // scroll part of the margin we set
    double m_centerOffset = 0;        // extra margin that puts the group where Windows would
    double m_prevCenterOffset = 0, m_prevGroupStart = 0, m_centerGain = 1;
    bool m_haveCenterSample = false;
    // Centering that pushed the list past the tray (so it needed scrolling, which moved it
    // back to the edge, where it fit again, so it was centered again...): XAML calls that a
    // layout cycle and takes Explorer down. Once it happens, the group stays at the edge
    // until it's clearly narrower than when it happened.
    bool m_centerHold = false;
    double m_centerHoldWidth = 0;
    // Last line of defence against any other back-and-forth: too many corrections in a
    // second and centering pauses for a few seconds.
    ULONGLONG m_fixWindowStart = 0, m_centerPausedUntil = 0;
    int m_fixCount = 0;
    double m_lastLoggedStart = -1, m_lastLoggedEnd = -1, m_lastLoggedLast = -1;
    winrt::Windows::Foundation::IInspectable m_wheelHandler{nullptr};
    winrt::event_token m_layoutToken{};
    std::unordered_map<void*, Item> m_items;
};
