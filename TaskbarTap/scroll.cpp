#include "scroll.h"
#include "color.h"

#include <winrt/Windows.UI.Xaml.Automation.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cwctype>
#include <limits>
#include <vector>

using namespace winrt::Windows::UI::Xaml;
namespace wf = winrt::Windows::Foundation;
using winrt::Windows::Foundation::Point;
using winrt::Windows::Foundation::Rect;
using winrt::Windows::Foundation::Numerics::float3;

namespace {

constexpr wchar_t kTaskButtonClass[] = L"Taskbar.TaskListButton";
constexpr wchar_t kTrayFrameClass[] = L"SystemTray.SystemTrayFrame";

// Breadth-first search for a descendant with the given class name.
FrameworkElement FindDescendantByClass(DependencyObject root,
                                       const wchar_t* className,
                                       int maxDepth) {
    std::vector<DependencyObject> level{root};
    for (int depth = 0; depth < maxDepth && !level.empty(); depth++) {
        std::vector<DependencyObject> next;
        for (auto& node : level) {
            int count = Media::VisualTreeHelper::GetChildrenCount(node);
            for (int i = 0; i < count; i++) {
                auto child = Media::VisualTreeHelper::GetChild(node, i);
                if (winrt::get_class_name(child) == className) {
                    return child.try_as<FrameworkElement>();
                }
                next.push_back(child);
            }
        }
        level = std::move(next);
    }
    return nullptr;
}

// Windows 11 "Taskbar alignment": centered (the default) or left. Read at most
// once a second (it's checked on every layout pass).
bool IsCenteredAlignment() {
    static ULONGLONG lastCheck = 0;
    static bool centered = true;
    ULONGLONG now = GetTickCount64();
    if (now - lastCheck > 1000) {
        lastCheck = now;
        DWORD value = 1, size = sizeof(value);
        if (RegGetValueW(HKEY_CURRENT_USER,
                         L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced",
                         L"TaskbarAl", RRF_RT_REG_DWORD, nullptr, &value, &size) != ERROR_SUCCESS) {
            value = 1;
        }
        centered = value != 0;
    }
    return centered;
}

bool IsVerticalTaskbar(const FrameworkElement& repeater) {
    if (auto xamlRoot = repeater.XamlRoot()) {
        auto size = xamlRoot.Size();
        if (size.Width > 0 && size.Height > 0) {
            return size.Height > size.Width;
        }
    }
    return false;
}

// Bounds of an element in XamlRoot coordinates (nullptr = the root).
Rect BoundsInRoot(const UIElement& element, Rect local) {
    return element.TransformToVisual(nullptr).TransformBounds(local);
}

// Where the task list must stop, in XamlRoot coordinates along the taskbar
// axis: the start of the system tray (clock, arrow, ...), or the end of the
// taskbar.
double ComputeVisibleEnd(const FrameworkElement& repeater, bool vertical,
                         double visibleStart) {
    auto xamlRoot = repeater.XamlRoot();
    if (!xamlRoot) return std::numeric_limits<double>::quiet_NaN();

    if (auto content = xamlRoot.Content()) {
        if (auto tray = FindDescendantByClass(content, kTrayFrameClass, 4)) {
            double w = tray.ActualWidth(), h = tray.ActualHeight();
            if (w > 0 && h > 0) {
                Rect b = BoundsInRoot(tray, Rect{0, 0, (float)w, (float)h});
                double end = vertical ? b.Y : b.X;
                if (end > visibleStart) {
                    return end;
                }
            }
        }
    }

    auto size = xamlRoot.Size();
    double end = vertical ? size.Height : size.Width;
    return end > visibleStart ? end : std::numeric_limits<double>::quiet_NaN();
}

}  // namespace

ScrollController::ScrollController(FrameworkElement repeater)
    : m_repeater(winrt::make_weak(repeater)),
      m_repeaterAbi(winrt::get_abi(repeater)) {}

bool ScrollController::IsAlive() const {
    return m_repeater.get() != nullptr;
}

bool ScrollController::Owns(const FrameworkElement& repeater) const {
    return winrt::get_abi(repeater) == m_repeaterAbi && IsAlive();
}

void ScrollController::Apply(const Settings& settings) {
    m_settings = settings;
    if (!settings.IsOn()) {
        Disable();
        return;
    }
    if (!m_enabled) {
        Enable();
    } else {
        if (auto repeater = m_repeater.get()) {
            ApplyMargin(repeater, m_vertical);
        }
        Update();
    }
}

void ScrollController::Enable() {
    auto repeater = m_repeater.get();
    if (!repeater) return;

    m_enabled = true;
    m_vertical = IsVerticalTaskbar(repeater);
    Log(L"Enable: vertical=%d size=%.0fx%.0f", m_vertical,
        repeater.ActualWidth(), repeater.ActualHeight());

    DumpTree(repeater, L"enable");
    ApplyMargin(repeater, m_vertical);

    std::weak_ptr<ScrollController> weak = weak_from_this();

    m_wheelHandler = winrt::box_value(Input::PointerEventHandler(
        [weak](wf::IInspectable const&, Input::PointerRoutedEventArgs const& args) {
            if (auto self = weak.lock()) {
                try {
                    self->OnWheel(args);
                } catch (...) {
                    Log(L"OnWheel failed: 0x%08X", winrt::to_hresult());
                }
            }
        }));
    // handledEventsToo = true: task buttons may mark the wheel as handled.
    repeater.AddHandler(UIElement::PointerWheelChangedEvent(), m_wheelHandler,
                        true);

    // Magnify: follow the mouse over the task list.
    m_moveHandler = winrt::box_value(Input::PointerEventHandler(
        [weak](wf::IInspectable const&, Input::PointerRoutedEventArgs const& args) {
            if (auto self = weak.lock()) {
                auto p = args.GetCurrentPoint(nullptr).Position();
                double axis = self->m_vertical ? p.Y : p.X;
                // Pressed and moved: a button is being dragged to a new place.
                if (self->m_pressed && !self->m_dragging && std::abs(axis - self->m_pressAxis) > 3) {
                    self->m_dragging = true;
                    self->BeginDrag();
                }
                if (!self->m_settings.magnify) return;
                self->m_pointerAxis = axis;
                self->m_pointerInside = true;
                self->Settle(250);
            }
        }));
    m_exitHandler = winrt::box_value(Input::PointerEventHandler(
        [weak](wf::IInspectable const&, Input::PointerRoutedEventArgs const&) {
            if (auto self = weak.lock()) {
                self->m_pointerInside = false;
                self->Settle(400);
            }
        }));
    // Dragging a button to reorder it: Windows moves it with the same
    // Translation property we use, so we must not touch it until it's dropped.
    m_pressHandler = winrt::box_value(Input::PointerEventHandler(
        [weak](wf::IInspectable const&, Input::PointerRoutedEventArgs const& args) {
            if (auto self = weak.lock()) {
                auto p = args.GetCurrentPoint(nullptr).Position();
                self->m_pressed = true;
                self->m_dragging = false;
                self->m_pressAxis = self->m_vertical ? p.Y : p.X;
            }
        }));
    m_releaseHandler = winrt::box_value(Input::PointerEventHandler(
        [weak](wf::IInspectable const&, Input::PointerRoutedEventArgs const&) {
            if (auto self = weak.lock()) {
                if (self->m_dragging) self->m_dropUntil = GetTickCount64() + 700;
                self->m_pressed = false;
                self->m_dragging = false;
                self->Settle(800);  // put everything right after the drop
            }
        }));
    repeater.AddHandler(UIElement::PointerPressedEvent(), m_pressHandler, true);
    repeater.AddHandler(UIElement::PointerReleasedEvent(), m_releaseHandler, true);
    repeater.AddHandler(UIElement::PointerCaptureLostEvent(), m_releaseHandler, true);
    repeater.AddHandler(UIElement::PointerMovedEvent(), m_moveHandler, true);
    repeater.AddHandler(UIElement::PointerExitedEvent(), m_exitHandler, true);

    m_layoutToken = repeater.LayoutUpdated(
        [weak](wf::IInspectable const&, wf::IInspectable const&) {
            if (auto self = weak.lock()) {
                try {
                    self->Update();
                    // Windows animates buttons into place after a layout
                    // change; keep the clips in sync while that runs.
                    self->Settle(700);
                } catch (...) {
                    Log(L"Update failed: 0x%08X", winrt::to_hresult());
                }
            }
        });

    Update();
}

void ScrollController::Disable() {
    if (!m_enabled) return;
    m_enabled = false;
    StopAnimation();
    StopFrameLoop();
    m_scroll = 0;
    m_targetScroll = 0;
    m_maxScroll = 0;

    auto repeater = m_repeater.get();
    if (repeater) {
        if (m_wheelHandler) {
            repeater.RemoveHandler(UIElement::PointerWheelChangedEvent(),
                                   m_wheelHandler);
        }
        if (m_moveHandler) repeater.RemoveHandler(UIElement::PointerMovedEvent(), m_moveHandler);
        if (m_pressHandler) repeater.RemoveHandler(UIElement::PointerPressedEvent(), m_pressHandler);
        if (m_releaseHandler) {
            repeater.RemoveHandler(UIElement::PointerReleasedEvent(), m_releaseHandler);
            repeater.RemoveHandler(UIElement::PointerCaptureLostEvent(), m_releaseHandler);
        }
        if (m_exitHandler) repeater.RemoveHandler(UIElement::PointerExitedEvent(), m_exitHandler);
        repeater.LayoutUpdated(m_layoutToken);
        RestoreMargin(repeater);
        ClearTaskbarFit(repeater, m_settings);
        if (m_repeaterClipApplied) {
            if (repeater.Clip() == m_repeaterClip) repeater.Clip(m_originalRepeaterClip);
            m_repeaterClipApplied = false;
        }
        repeater.InvalidateMeasure();
    }
    m_wheelHandler = nullptr;
    m_moveHandler = nullptr;
    m_exitHandler = nullptr;
    m_pressHandler = nullptr;
    m_releaseHandler = nullptr;
    m_layoutToken = {};
    m_repeaterClip = nullptr;
    m_originalRepeaterClip = nullptr;

    for (auto& [abi, item] : m_items) {
        ResetItem(item);
    }
    m_items.clear();
    Log(L"Disabled");
}

void ScrollController::ApplyMargin(const FrameworkElement& repeater,
                                   bool vertical) {
    if (m_marginApplied && m_marginVertical != vertical) {
        RestoreMargin(repeater);
    }

    if (!m_marginApplied) {
        m_originalMargin = repeater.Margin();
        m_marginApplied = true;
        m_marginVertical = vertical;
    }

    // The scroll moves the task list's real layout (not just its pixels), so
    // Windows puts jump lists and thumbnails next to the right button.
    double shift = std::round(m_scroll);
    Thickness margin = m_originalMargin;
    if (vertical) {
        margin.Top -= shift;
        margin.Bottom -= m_settings.extraSpace;
    } else {
        margin.Left -= shift;
        margin.Right -= m_settings.extraSpace;
        if (m_centered) margin.Left += std::round(m_centerOffset);
    }
    m_appliedScroll = shift;

    auto current = repeater.Margin();
    if (current.Left != margin.Left || current.Top != margin.Top ||
        current.Right != margin.Right || current.Bottom != margin.Bottom) {
        repeater.Margin(margin);
    }
}

void ScrollController::RestoreMargin(const FrameworkElement& repeater) {
    if (!m_marginApplied) return;
    repeater.Margin(m_originalMargin);
    m_marginApplied = false;
    m_appliedScroll = 0;
}

void ScrollController::Update() {
    if (!m_enabled || m_updating) return;
    auto repeater = m_repeater.get();
    if (!repeater || !repeater.XamlRoot()) return;

    m_updating = true;
    struct Reset {
        bool* flag;
        ~Reset() { *flag = false; }
    } reset{&m_updating};

    // Mouse button down on the task list = maybe dragging a button. The
    // release can be swallowed by the drag, so also ask the mouse itself.
    if (m_pressed && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0) {
        if (m_dragging) m_dropUntil = GetTickCount64() + 700;
        m_pressed = false;
        m_dragging = false;
        Settle(800);
    }
    // After a drop Windows slides the buttons into their new places with the
    // same Translation property; touching it mid-slide stacked them up. So
    // wait until it's done, then "running apps first" puts them in order again.
    m_dragFrozen = m_dragging || GetTickCount64() < m_dropUntil;

    bool vertical = IsVerticalTaskbar(repeater);
    if (vertical != m_vertical) {
        Log(L"Orientation changed: vertical=%d", vertical);
        m_vertical = vertical;
        StopAnimation();
        m_scroll = 0;
        m_targetScroll = 0;
        DumpTree(repeater, L"orientation changed");
    }

    struct Entry {
        FrameworkElement element{nullptr};
        double base, extent, cross;  // base = position when not scrolled (root coords)
        bool isTask;
        double shift = 0;            // drawn offset for "running apps first"
        double layoutBase = 0;       // like base, without Windows' animation offset
    };
    std::vector<Entry> entries;

    // Where the repeater's content starts when it isn't scrolled: its layout
    // slot in the parent plus its original margin. This doesn't move when we
    // change the margin to scroll.
    auto toRoot = repeater.TransformToVisual(nullptr);
    double originBase;
    {
        auto parent = Media::VisualTreeHelper::GetParent(repeater).try_as<UIElement>();
        Rect rs = Controls::Primitives::LayoutInformation::GetLayoutSlot(repeater);
        Point p = parent ? parent.TransformToVisual(nullptr).TransformPoint({rs.X, rs.Y})
                         : Point{0, 0};
        originBase = (vertical ? p.Y : p.X) + (vertical ? m_originalMargin.Top : m_originalMargin.Left);
    }
    Point actual = toRoot.TransformPoint({0, 0});
    // How far the layout is scrolled right now (the margin last laid out).
    double laidOutShift = originBase - (vertical ? actual.Y : actual.X);
    if (std::abs(laidOutShift) < 0.5) laidOutShift = 0;

    // Centered taskbar: Windows centers the task list itself, so its position
    // also depends on our margin; measuring against the slot doesn't work.
    // There, the scroll part of the margin is known (we set it) and the
    // unscrolled position is simply where it is now plus that.
    bool centered = !vertical && IsCenteredAlignment();
    if (centered != m_centered) {
        m_centered = centered;
        m_centerOffset = 0;
        m_haveCenterSample = false;
        m_centerGain = 1;
        m_centerHold = false;
        Log(L"Alignment: centered=%d", centered);
    }
    double centerWidth = 0, centerTarget = 0;   // for the overflow check further down
    if (centered) {
        laidOutShift = m_appliedScroll;
        originBase = actual.X + laidOutShift;
    }

    int count = Media::VisualTreeHelper::GetChildrenCount(repeater);
    for (int i = 0; i < count; i++) {
        auto child = Media::VisualTreeHelper::GetChild(repeater, i)
                         .try_as<FrameworkElement>();
        if (!child || child.Visibility() != Visibility::Visible) {
            continue;
        }
        Rect slot = Controls::Primitives::LayoutInformation::GetLayoutSlot(child);
        // ItemsRepeater parks recycled elements at about -10000 with size 0.
        if (slot.Width <= 0 || slot.Height <= 0 || slot.X < -5000 ||
            slot.Y < -5000) {
            continue;
        }
        bool isTask = winrt::get_class_name(child) == kTaskButtonClass;
        double layoutPos = originBase + (vertical ? slot.Y : slot.X);
        if (isTask) {
            // Windows may place buttons with a translate-only MatrixTransform
            // (e.g. while animating); include it.
            if (auto mt = child.RenderTransform().try_as<Media::MatrixTransform>()) {
                auto mx = mt.Matrix();
                if (mx.M11 == 1 && mx.M22 == 1 && mx.M12 == 0 && mx.M21 == 0) {
                    slot.X += (float)mx.OffsetX;
                    slot.Y += (float)mx.OffsetY;
                }
            }
        }
        entries.push_back({child, originBase + (vertical ? slot.Y : slot.X),
                           vertical ? slot.Height : slot.Width,
                           vertical ? slot.Width : slot.Height, isTask});
        entries.back().layoutBase = layoutPos;
    }

    // Centered: put the whole group (Start + apps) in the middle of the taskbar
    // like Windows does; once it doesn't fit, start it at the left edge and
    // scroll as usual.
    if (centered && !entries.empty()) {
        double groupStart = std::numeric_limits<double>::infinity(), groupEnd = -groupStart;
        for (auto& e : entries) {
            groupStart = std::min(groupStart, e.base);
            groupEnd = std::max(groupEnd, e.base + e.extent);
        }
        double width = groupEnd - groupStart;
        double rootWidth = repeater.XamlRoot().Size().Width;
        double regionEnd = ComputeVisibleEnd(repeater, false, 1);
        if (std::isnan(regionEnd)) regionEnd = rootWidth;
        bool wantCenter = width <= regionEnd - 1;
        if (m_centerHold) {
            // Back to the middle only once it's narrower by half a button (an app closed),
            // not on the few pixels that made it flip before.
            double slack = std::max(12.0, m_itemExtent / 2);
            if (wantCenter && width < m_centerHoldWidth - slack) {
                m_centerHold = false;
                Log(L"Centering: fits again (width %.0f), back to the middle", width);
            } else {
                wantCenter = false;
            }
        }
        double target = wantCenter
            ? std::clamp(rootWidth / 2 - width / 2, 0.0, std::max(0.0, regionEnd - width))
            : 0.0;
        centerWidth = width;
        centerTarget = target;
        double error = groupStart - target;

        ULONGLONG now = GetTickCount64();
        if (now - m_fixWindowStart > 1000) {
            m_fixWindowStart = now;
            m_fixCount = 0;
        }
        bool paused = now < m_centerPausedUntil;
        if (std::abs(error) > 0.5 && !paused && ++m_fixCount > 40) {
            m_centerPausedUntil = now + 5000;
            paused = true;
            Log(L"Centering: %d corrections in a second (error %.0f), pausing 5 s so the taskbar stays up",
                m_fixCount, error);
        }
        if (std::abs(error) > 0.5 && !paused) {
            // How much the group moves per pixel of margin (depends on how
            // Windows centers it); learned from the previous correction.
            if (m_haveCenterSample && std::abs(m_centerOffset - m_prevCenterOffset) > 0.5) {
                double gain = (groupStart - m_prevGroupStart) / (m_centerOffset - m_prevCenterOffset);
                if (gain > 0.2 && gain < 1.6) m_centerGain = gain;
            }
            m_prevCenterOffset = m_centerOffset;
            m_prevGroupStart = groupStart;
            m_haveCenterSample = true;
            m_centerOffset -= error / m_centerGain;
            m_centerOffset = std::clamp(m_centerOffset, -100000.0, 100000.0);
        }
    }

    // "Fit to the apps": tell the background where the apps and the tray are.
    if (!vertical && !entries.empty()) {
        if (m_settings.fitApps) {
            double appsStart = std::numeric_limits<double>::infinity(), appsEnd = -appsStart;
            for (auto& e : entries) {
                appsStart = std::min(appsStart, e.base);
                appsEnd = std::max(appsEnd, e.base + e.extent);
            }
            double trayStart = ComputeVisibleEnd(repeater, false, 1);
            if (std::isnan(trayStart)) trayStart = repeater.XamlRoot().Size().Width;
            appsEnd = std::min(appsEnd, trayStart);   // more than fits: it scrolls up to the tray
            ReportTaskbarFit(repeater, appsStart, appsEnd, trayStart, m_settings);
        } else {
            ClearTaskbarFit(repeater, m_settings);
        }
    }

    for (auto& [abi, item] : m_items) {
        item.seen = false;
    }

    bool anyTask = std::any_of(entries.begin(), entries.end(), [](auto& e) { return e.isTask; });
    if (anyTask) {
        double visibleStart = std::numeric_limits<double>::infinity();
        double lastEnd = 0;
        for (auto& e : entries) {
            if (!e.isTask) continue;
            visibleStart = std::min(visibleStart, e.base);
            lastEnd = std::max(lastEnd, e.base + e.extent);
            if (m_itemExtent == 0) m_itemExtent = e.extent;
        }

        double visibleEnd = ComputeVisibleEnd(repeater, vertical, visibleStart);
        if (std::isnan(visibleEnd)) {
            visibleEnd = std::numeric_limits<double>::infinity();
        }

        UpdateRepeaterClip(repeater, toRoot, vertical, visibleEnd);

        // "Running apps first": the buttons stay where Windows put them in the
        // layout; running ones are drawn at the front and the rest after them.
        if (m_settings.runningFirst) {
            std::vector<Entry*> tasks;
            for (auto& e : entries) {
                if (e.isTask) tasks.push_back(&e);
            }
            std::sort(tasks.begin(), tasks.end(),
                      [](Entry* a, Entry* b) { return a->layoutBase < b->layoutBase; });
            if (tasks.size() > 1) {
                // Windows describes running buttons as "... - 1 running window".
                auto isRunning = [](const FrameworkElement& el) {
                    std::wstring name{Automation::AutomationProperties::GetName(el)};
                    for (auto& c : name) c = towlower(c);
                    return name.find(L"running window") != std::wstring::npos ||
                           name.find(L"\u0642\u064A\u062F \u0627\u0644\u062A\u0634\u063A\u064A\u0644") != std::wstring::npos;
                };
                std::vector<bool> runs;
                for (auto* t : tasks) runs.push_back(isRunning(t->element));
                std::vector<Entry*> order;
                for (size_t k = 0; k < tasks.size(); k++) if (runs[k]) order.push_back(tasks[k]);
                size_t running = order.size();
                for (size_t k = 0; k < tasks.size(); k++) if (!runs[k]) order.push_back(tasks[k]);

                // The k-th button in the new order goes exactly where the k-th
                // button is in Windows' own layout. Positions come from the layout
                // only (not from Windows' running animations), so every spot is
                // used once: no gaps and nothing on top of anything else.
                for (size_t k = 0; k < order.size(); k++) {
                    order[k]->shift = std::round(tasks[k]->layoutBase - order[k]->layoutBase);
                }
                if (running != m_lastRunningCount) {
                    m_lastRunningCount = running;
                    Log(L"Running first: %zu of %zu buttons", running, tasks.size());
                }
            }
        }

        m_maxScroll = std::max(0.0, lastEnd - visibleEnd);
        // Centered, and that made it run past the tray: keep it at the edge from now on.
        if (centered && !m_centerHold && centerTarget > 0.5 && m_maxScroll > 0.5) {
            m_centerHold = true;
            m_centerHoldWidth = centerWidth;
            Log(L"Centering: overflows by %.0f at width %.0f, staying at the edge", m_maxScroll, centerWidth);
        }
        m_scroll = std::clamp(m_scroll, 0.0, m_maxScroll);
        m_targetScroll = std::clamp(m_targetScroll, 0.0, m_maxScroll);

        if (std::abs(visibleStart - m_lastLoggedStart) > 1 ||
            std::abs(visibleEnd - m_lastLoggedEnd) > 1 ||
            std::abs(lastEnd - m_lastLoggedLast) > 1) {
            m_lastLoggedStart = visibleStart;
            m_lastLoggedEnd = visibleEnd;
            m_lastLoggedLast = lastEnd;
            Log(L"Layout: vertical=%d start=%.0f end=%.0f last=%.0f maxScroll=%.0f",
                vertical, visibleStart, visibleEnd, lastEnd, m_maxScroll);
        }

        for (auto& e : entries) {
            if (e.isTask) {
                // Where it is on screen right now.
                ApplyItem(e.element, e.base - laidOutShift, e.extent, e.cross,
                          visibleStart, visibleEnd, e.shift);
            } else if (e.base < visibleStart) {
                // Start / search buttons move with the layout too; shift them
                // back so they stay put.
                Item& item = m_items[winrt::get_abi(e.element)];
                if (!item.element.get()) {
                    item = Item{};
                    item.element = winrt::make_weak(e.element);
                }
                item.seen = true;
                if (laidOutShift != 0 || item.mode != ShiftMode::None) {
                    SetShift(item, e.element, (float)laidOutShift);
                }
            }
        }
    } else {
        m_maxScroll = 0;
        m_scroll = 0;
        m_targetScroll = 0;
    }

    if (m_magnifyBusy) {
        m_magnifyBusy = false;
        Settle(100);  // keep easing next frame
    }

    // Scroll by moving the layout (applied after the positions above, so the
    // next layout pass picks it up).
    // While a button is dragged, the task list gets its normal size back:
    // Windows works out where the button lands from the list's size, and the
    // huge extra room the scroll needs made it drop everything at the end.
    if (m_dragFrozen) {
        RestoreMargin(repeater);
    } else {
        ApplyMargin(repeater, vertical);
    }

    // Buttons that are gone or recycled: undo what we did to them.
    for (auto it = m_items.begin(); it != m_items.end();) {
        if (!it->second.seen) {
            ResetItem(it->second);
            it = m_items.erase(it);
        } else {
            ++it;
        }
    }
}

void ScrollController::SetShift(Item& item, const FrameworkElement& element,
                                float offset) {
    if (item.mode == ShiftMode::Failed) return;
    if (item.mode != ShiftMode::None && item.applied == offset) return;

    float3 value = item.baseTranslation;
    if (m_vertical) {
        value.y += offset;
    } else {
        value.x += offset;
    }

    // Preferred: the XAML Translation property (hit testing follows it).
    if (item.mode == ShiftMode::None || item.mode == ShiftMode::Facade) {
        try {
            if (item.mode == ShiftMode::None) {
                item.baseTranslation = element.Translation();
                value = item.baseTranslation;
                if (m_vertical) value.y += offset; else value.x += offset;
            }
            element.Translation(value);
            item.mode = ShiftMode::Facade;
            item.applied = offset;
            return;
        } catch (...) {
            static bool logged = false;
            if (!logged) {
                logged = true;
                Log(L"Translation property not allowed (0x%08X); using composition",
                    winrt::to_hresult());
            }
            item.mode = ShiftMode::None;
            item.baseTranslation = {};
        }
    }

    // Fallback: the composition visual's Translation property.
    try {
        if (!item.visual) {
            Hosting::ElementCompositionPreview::SetIsTranslationEnabled(element, true);
            item.visual = Hosting::ElementCompositionPreview::GetElementVisual(element);
        }
        float3 v{};
        if (m_vertical) v.y = offset; else v.x = offset;
        item.visual.Properties().InsertVector3(L"Translation", v);
        item.mode = ShiftMode::Composition;
        item.applied = offset;
    } catch (...) {
        Log(L"Could not shift a task button (0x%08X)", winrt::to_hresult());
        item.mode = ShiftMode::Failed;
    }
}

// A drag to reorder started. Windows decides where the button lands from the
// real layout, so put every button back where Windows has it first (once),
// then leave them to Windows until the drop. "Running apps first" comes back
// after the drop.
void ScrollController::BeginDrag() {
    if (auto repeater = m_repeater.get()) RestoreMargin(repeater);
    Log(L"Drag: task list back to normal size until the drop");
    for (auto& [abi, item] : m_items) {
        auto element = item.element.get();
        if (!element || item.applied == 0 || item.mode == ShiftMode::Failed) continue;
        SetTaskShift(item, element, 0);
    }
}

// "Running apps first" shift of a task button. Unlike SetShift, this sets the
// position absolutely and checks it every time: Windows animates task buttons
// with the same Translation property (when apps open/close), and adding our
// shift on top of a half-finished animation of theirs left buttons stacked on
// each other with gaps in between.
void ScrollController::SetTaskShift(Item& item, const FrameworkElement& element, float shift) {
    if (item.mode == ShiftMode::Failed) return;
    if (item.mode != ShiftMode::Composition) {
        try {
            auto t = element.Translation();
            float& axis = m_vertical ? t.y : t.x;
            float& other = m_vertical ? t.x : t.y;
            if (std::abs(axis - shift) > 0.5f || std::abs(other) > 0.5f) {
                axis = shift;
                other = 0;
                element.Translation(t);
            }
            item.baseTranslation = {};
            item.applied = shift;
            item.mode = shift == 0 ? ShiftMode::None : ShiftMode::Facade;
            return;
        } catch (...) {
            item.mode = ShiftMode::None;
        }
    }
    SetShift(item, element, shift);  // composition fallback
}

void ScrollController::ApplyItem(const FrameworkElement& element, double pos,
                                 double extent, double cross,
                                 double visibleStart, double visibleEnd,
                                 double shift) {
    Item& item = m_items[winrt::get_abi(element)];
    if (!item.element.get()) {
        item = Item{};
        item.element = winrt::make_weak(element);
    }
    item.seen = true;

    // Task buttons are scrolled by the layout itself. The only drawn shift is
    // "running apps first" (0 otherwise, which also undoes an older one).
    if (m_dragFrozen) {
        shift = 0;  // BeginDrag put every button back; Windows is moving them now
    } else if (shift != 0 || m_settings.runningFirst ||
               (item.mode != ShiftMode::None && item.mode != ShiftMode::Failed)) {
        // With "running apps first" every button's position is checked, not only
        // the ones we moved: the task list reuses buttons, and one reused after a
        // drag still carried the drag's offset (gaps and buttons on top of each
        // other). SetTaskShift only writes when the position is off.
        SetTaskShift(item, element, (float)shift);
    }
    if (item.mode == ShiftMode::Failed) shift = 0;

    // Magnify: buttons near the mouse grow (the closer, the bigger), easing
    // there frame by frame. Layout doesn't change, so nothing else moves.
    if (!m_scaleFailed && (m_settings.magnify || item.scale != 1)) {
        float target = 1;
        if (m_settings.magnify && m_pointerInside) {
            double center = pos + shift + extent / 2;
            double d = (m_pointerAxis - center) / (extent * 1.1);
            target = (float)(1 + 0.3 * std::exp(-d * d));
        }
        float next = item.scale + (target - item.scale) * 0.35f;
        if (std::abs(target - next) < 0.003f) next = target;
        else m_magnifyBusy = true;
        if (next != item.scale) {
            try {
                // Windows already drives these buttons' composition visuals, so the
                // XAML Scale property is refused; scale the visual itself.
                auto panel = item.scalePanel.get();
                if (!panel) {
                    // First child of the button: "IconPanel" (icon + highlight).
                    if (Media::VisualTreeHelper::GetChildrenCount(element) > 0)
                        panel = Media::VisualTreeHelper::GetChild(element, 0).try_as<FrameworkElement>();
                    if (!panel) throw winrt::hresult_error(E_FAIL);
                    item.scalePanel = winrt::make_weak(panel);
                    item.scaleTransform = Media::ScaleTransform();
                    panel.RenderTransformOrigin({0.5f, 0.5f});
                    panel.RenderTransform(item.scaleTransform);
                }
                item.scaleTransform.ScaleX(next);
                item.scaleTransform.ScaleY(next);
                item.scale = next;
            } catch (...) {
                m_scaleFailed = true;
                Log(L"Magnify: scaling failed (0x%08X)", winrt::to_hresult());
            }
        }
    }

    // Clip, in the element's own coordinates (they move with the shift).
    double visualPos = pos + shift;
    double from = std::clamp(visibleStart - visualPos, 0.0, extent);
    double to = std::clamp(visibleEnd - visualPos, from, extent);
    bool needClip = !m_dragFrozen && (from > 0.5 || to < extent - 0.5);

    if (!needClip) {
        if (item.clipped) {
            element.Clip(nullptr);
            item.clipped = false;
            item.geometry = nullptr;
        }
        return;
    }

    if (item.clipped && std::abs(item.clipFrom - from) < 0.25 &&
        std::abs(item.clipTo - to) < 0.25) {
        return;
    }

    if (!item.clipped && element.Clip()) {
        static bool logged = false;
        if (!logged) {
            logged = true;
            Log(L"A task button already has a Clip; replacing it");
        }
    }

    // Leave generous room on the cross axis for badges and indicators.
    Rect r = m_vertical
                 ? Rect{-500.0f, (float)from, (float)(cross + 1000), (float)(to - from)}
                 : Rect{(float)from, -500.0f, (float)(to - from), (float)(cross + 1000)};
    // Reuse the geometry while it's still ours (cheaper during animation).
    if (!item.geometry || element.Clip() != item.geometry) {
        item.geometry = Media::RectangleGeometry();
        item.geometry.Rect(r);
        element.Clip(item.geometry);
    } else {
        item.geometry.Rect(r);
    }
    item.clipped = true;
    item.clipFrom = from;
    item.clipTo = to;
}

void ScrollController::ResetItem(Item& item) {
    auto element = item.element.get();
    if (!element) return;
    try {
        if (item.mode == ShiftMode::Facade) {
            element.Translation(item.baseTranslation);
        } else if (item.mode == ShiftMode::Composition && item.visual) {
            item.visual.Properties().InsertVector3(L"Translation", float3{});
        }
        if (item.clipped) {
            element.Clip(nullptr);
        }
        if (auto panel = item.scalePanel.get(); panel && item.scaleTransform) {
            if (panel.RenderTransform() == item.scaleTransform) panel.RenderTransform(nullptr);
        }
    } catch (...) {
    }
    item.scale = 1;
    item.scalePanel = nullptr;
    item.scaleTransform = nullptr;
    item.mode = ShiftMode::None;
    item.visual = nullptr;
    item.geometry = nullptr;
    item.applied = 0;
    item.clipped = false;
}

void ScrollController::OnWheel(Input::PointerRoutedEventArgs const& args) {
    if (!m_enabled) return;

    auto repeater = m_repeater.get();
    if (!repeater) return;

    auto props = args.GetCurrentPoint(repeater).Properties();
    int delta = props.MouseWheelDelta();
    if (delta == 0) return;

    if (m_maxScroll <= 0) {
        static int logged = 0;
        if (logged++ < 3) Log(L"Wheel: nothing to scroll (maxScroll=0)");
        return;
    }

    double notches = delta / 120.0;
    if (m_settings.reverse) notches = -notches;

    double step = (m_itemExtent > 0 ? m_itemExtent : 44.0) * m_settings.scrollStep;
    // Successive notches add up from where the animation is heading.
    double base = m_animating ? m_targetScroll : m_scroll;
    double next = std::clamp(base - notches * step, 0.0, m_maxScroll);
    if (next == base) return;

    m_targetScroll = next;
    args.Handled(true);

    if (!m_settings.smooth) {
        m_scroll = next;
        Update();
        Settle(300);
        return;
    }
    StartAnimation();
}

void ScrollController::StartAnimation() {
    if (!m_animating) {
        m_animating = true;
        m_lastFrame = std::chrono::steady_clock::now();
    }
    EnsureFrameLoop();
}

void ScrollController::StopAnimation() {
    m_animating = false;
}

void ScrollController::Settle(int milliseconds) {
    if (!m_enabled) return;
    auto until = std::chrono::steady_clock::now() +
                 std::chrono::milliseconds(milliseconds);
    if (until > m_settleUntil) m_settleUntil = until;
    EnsureFrameLoop();
}

void ScrollController::EnsureFrameLoop() {
    if (m_frameLoop) return;
    m_frameLoop = true;
    m_lastFrame = std::chrono::steady_clock::now();

    std::weak_ptr<ScrollController> weak = weak_from_this();
    m_renderToken = Media::CompositionTarget::Rendering(
        [weak](wf::IInspectable const&, wf::IInspectable const&) {
            if (auto self = weak.lock()) {
                try {
                    self->OnFrame();
                } catch (...) {
                    Log(L"OnFrame failed: 0x%08X", winrt::to_hresult());
                    self->StopAnimation();
                    self->StopFrameLoop();
                }
            }
        });
}

void ScrollController::StopFrameLoop() {
    if (!m_frameLoop) return;
    m_frameLoop = false;
    Media::CompositionTarget::Rendering(m_renderToken);
    m_renderToken = {};
}

void ScrollController::UpdateRepeaterClip(
    const FrameworkElement& repeater,
    const winrt::Windows::UI::Xaml::Media::GeneralTransform& toRoot,
    bool vertical, double visibleEnd) {
    // Hard stop at the system tray for the whole task list, so nothing can
    // ever be drawn over the clock, even mid-animation.
    if (!std::isfinite(visibleEnd)) {
        if (m_repeaterClipApplied) {
            if (repeater.Clip() == m_repeaterClip) repeater.Clip(m_originalRepeaterClip);
            m_repeaterClipApplied = false;
        }
        return;
    }

    Point origin = toRoot.TransformPoint({0, 0});
    double endLocal = visibleEnd - (vertical ? origin.Y : origin.X);
    double cross = vertical ? repeater.ActualWidth() : repeater.ActualHeight();
    Rect r = vertical
                 ? Rect{-1000.0f, -1000.0f, (float)(cross + 2000), (float)(endLocal + 1000)}
                 : Rect{-1000.0f, -1000.0f, (float)(endLocal + 1000), (float)(cross + 2000)};
    if (r.Width <= 0 || r.Height <= 0) return;

    if (!m_repeaterClipApplied || repeater.Clip() != m_repeaterClip) {
        if (!m_repeaterClipApplied) {
            m_originalRepeaterClip = repeater.Clip();
        }
        m_repeaterClip = Media::RectangleGeometry();
        m_repeaterClip.Rect(r);
        repeater.Clip(m_repeaterClip);
        m_repeaterClipApplied = true;
        return;
    }

    Rect current = m_repeaterClip.Rect();
    if (std::abs(current.Width - r.Width) > 0.25f ||
        std::abs(current.Height - r.Height) > 0.25f) {
        m_repeaterClip.Rect(r);
    }
}

// One frame of the smooth scroll: move a fraction of the remaining distance
// (exponential ease-out, independent of frame rate).
void ScrollController::OnFrame() {
    auto now = std::chrono::steady_clock::now();
    double dt = std::chrono::duration<double>(now - m_lastFrame).count();
    m_lastFrame = now;
    dt = std::clamp(dt, 0.0, 0.05);

    if (m_animating) {
        constexpr double kSpeed = 16.0;  // higher = snappier
        double remaining = m_targetScroll - m_scroll;
        if (std::abs(remaining) < 0.5) {
            m_scroll = m_targetScroll;
            StopAnimation();
            Settle(300);  // let the last clips catch up
        } else {
            m_scroll += remaining * (1.0 - std::exp(-kSpeed * dt));
        }
    }

    Update();

    if (!m_animating && now >= m_settleUntil) {
        StopFrameLoop();
    }
}

void ScrollController::DumpTree(const FrameworkElement& repeater,
                                const wchar_t* reason) {
    if (!m_settings.debugDump || m_dumpCount >= 4) return;
    m_dumpCount++;

    Log(L"--- Taskbar dump (%s) ---", reason);
    if (auto xamlRoot = repeater.XamlRoot()) {
        auto size = xamlRoot.Size();
        Log(L"XamlRoot size: %.0fx%.0f", size.Width, size.Height);
        if (auto content = xamlRoot.Content()) {
            int n = Media::VisualTreeHelper::GetChildrenCount(content);
            for (int i = 0; i < n; i++) {
                auto c = Media::VisualTreeHelper::GetChild(content, i)
                             .try_as<FrameworkElement>();
                if (!c) continue;
                Rect b = BoundsInRoot(c, Rect{0, 0, (float)c.ActualWidth(),
                                              (float)c.ActualHeight()});
                Log(L"  root child: %s name='%s' at (%.0f,%.0f) %.0fx%.0f",
                    winrt::get_class_name(c).c_str(), c.Name().c_str(), b.X,
                    b.Y, b.Width, b.Height);
            }
        }
    }
    auto m = repeater.Margin();
    Log(L"Repeater margin: %.0f,%.0f,%.0f,%.0f", m.Left, m.Top, m.Right, m.Bottom);
    int count = Media::VisualTreeHelper::GetChildrenCount(repeater);
    for (int i = 0; i < count && i < 40; i++) {
        auto c = Media::VisualTreeHelper::GetChild(repeater, i)
                     .try_as<FrameworkElement>();
        if (!c) continue;
        Rect s = Controls::Primitives::LayoutInformation::GetLayoutSlot(c);
        std::wstring transform = L"none";
        if (auto rt = c.RenderTransform()) {
            transform = winrt::get_class_name(rt).c_str();
            if (auto mt = rt.try_as<Media::MatrixTransform>()) {
                auto mx = mt.Matrix();
                wchar_t buf[128];
                swprintf_s(buf, L" [%.2f %.2f %.2f %.2f | %.1f %.1f]", mx.M11,
                           mx.M12, mx.M21, mx.M22, mx.OffsetX, mx.OffsetY);
                transform += buf;
            }
        }
        Log(L"  item %d: %s slot=(%.0f,%.0f %.0fx%.0f) transform=%s clip=%d", i,
            winrt::get_class_name(c).c_str(), s.X, s.Y, s.Width, s.Height,
            transform.c_str(), c.Clip() ? 1 : 0);
    }
    Log(L"--- end dump ---");
}
