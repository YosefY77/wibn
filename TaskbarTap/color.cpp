#include "color.h"

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/Windows.UI.Xaml.Media.Imaging.h>

#include <algorithm>
#include <cwctype>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

using namespace winrt::Windows::UI::Xaml;

namespace {

struct Target {
    winrt::weak_ref<FrameworkElement> element;
    void* abi = nullptr;
    ColorTargetKind kind = ColorTargetKind::RectFill;
    std::wstring requiredAncestor;
    winrt::Windows::System::DispatcherQueue queue{nullptr};
    bool verified = false;   // passed the ancestor check
    bool applied = false;    // our value is on
    bool applying = false;   // ignore our own property change
    winrt::Windows::Foundation::IInspectable original{nullptr};  // brush or boxed Visibility
    double originalOpacity = 1.0;
    int64_t token = 0;
    winrt::event_token loadedToken{};
    std::weak_ptr<Target> self;
    // Taskbar background: floating look.
    bool floatApplied = false;
    Thickness originalMargin{};
    double originalRadiusX = 0, originalRadiusY = 0;
    std::vector<std::pair<winrt::weak_ref<UIElement>, double>> hiddenSiblings;  // the thin border line etc.
    // Running indicator: color taken from the app icon.
    uint32_t indicator = 0;
    bool hasColor = false;
    bool computing = false;
    void* iconSource = nullptr;
    // Taskbar background: our own layer just above Windows' (see EnsureLayer).
    winrt::weak_ref<Shapes::Rectangle> layer;
};

std::mutex g_targetsMutex;
std::vector<std::shared_ptr<Target>> g_targets;

bool HasAncestorClass(DependencyObject element, const std::wstring& className) {
    auto node = Media::VisualTreeHelper::GetParent(element);
    while (node) {
        if (winrt::get_class_name(node) == className) return true;
        node = Media::VisualTreeHelper::GetParent(node);
    }
    return false;
}

winrt::Windows::UI::Color ToColor(uint32_t argb) {
    return winrt::Windows::UI::Color{(uint8_t)(argb >> 24), (uint8_t)(argb >> 16),
                                     (uint8_t)(argb >> 8), (uint8_t)argb};
}

DependencyProperty WatchedProperty(const FrameworkElement& el, ColorTargetKind kind) {
    switch (kind) {
        case ColorTargetKind::RectFill:
            return Shapes::Shape::FillProperty();
        case ColorTargetKind::Hide:
            return UIElement::VisibilityProperty();
        case ColorTargetKind::IconSize:
            return FrameworkElement::WidthProperty();
        case ColorTargetKind::DateYear:
            return Controls::TextBlock::TextProperty();
        default:
            if (el.try_as<Shapes::Shape>()) return Shapes::Shape::FillProperty();
            if (el.try_as<Controls::Border>()) return Controls::Border::BackgroundProperty();
            if (el.try_as<Controls::Panel>()) return Controls::Panel::BackgroundProperty();
            if (el.try_as<Controls::Control>()) return Controls::Control::BackgroundProperty();
            return nullptr;
    }
}


winrt::Windows::Foundation::IInspectable GetValue(const FrameworkElement& el, ColorTargetKind kind) {
    if (kind == ColorTargetKind::Hide) return winrt::box_value(el.Visibility());
    if (kind == ColorTargetKind::IconSize) return winrt::box_value(el.Width());
    if (kind == ColorTargetKind::DateYear) {
        auto tb = el.try_as<Controls::TextBlock>();
        return winrt::box_value(tb ? tb.Text() : winrt::hstring{});
    }
    auto dp = WatchedProperty(el, kind);
    return dp ? el.GetValue(dp) : nullptr;
}

void SetValue(const FrameworkElement& el, ColorTargetKind kind,
              winrt::Windows::Foundation::IInspectable const& value) {
    if (kind == ColorTargetKind::Hide) {
        el.Visibility(value ? winrt::unbox_value<Visibility>(value) : Visibility::Visible);
        return;
    }
    if (kind == ColorTargetKind::DateYear) {
        if (auto tb = el.try_as<Controls::TextBlock>()) {
            tb.Text(winrt::unbox_value_or<winrt::hstring>(value, L""));
        }
        return;
    }
    if (kind == ColorTargetKind::IconSize) {
        double size = value ? winrt::unbox_value<double>(value) : std::numeric_limits<double>::quiet_NaN();
        el.Width(size);
        el.Height(size);
        return;
    }
    auto dp = WatchedProperty(el, kind);
    if (!dp) return;
    if (value) el.SetValue(dp, value);
    else el.ClearValue(dp);
}

// "01/11" -> "01/11/2026" (or the year on its own line on a vertical taskbar,
// where there's no room for a longer line). Leaves it alone if it already has
// the year.
std::wstring WithYear(const std::wstring& text, const FrameworkElement& el) {
    SYSTEMTIME now;
    GetLocalTime(&now);
    std::wstring year = std::to_wstring(now.wYear);
    if (text.empty() || text.find(year) != std::wstring::npos) return text;

    bool vertical = false;
    if (auto root = el.XamlRoot()) {
        auto size = root.Size();
        vertical = size.Height > size.Width;
    }
    if (vertical) return text + L"\n" + year;

    // Use the same separator as the date itself ("/", "-", "." ...), skipping
    // digits, spaces and invisible direction marks.
    wchar_t sep = L'/';
    for (auto it = text.rbegin(); it != text.rend(); ++it) {
        wchar_t c = *it;
        if (iswdigit(c) || iswspace(c) || c == 0x200E || c == 0x200F || c == 0x061C) continue;
        sep = c;
        break;
    }
    return text + sep + year;
}

void ApplyToTarget(Target& t, const Settings& s);


FrameworkElement FindByName(DependencyObject root, const wchar_t* name, int depth = 0) {
    if (depth > 12) return nullptr;
    int n = Media::VisualTreeHelper::GetChildrenCount(root);
    for (int i = 0; i < n; i++) {
        auto child = Media::VisualTreeHelper::GetChild(root, i);
        if (auto fe = child.try_as<FrameworkElement>(); fe && fe.Name() == name) return fe;
        if (auto found = FindByName(child, name, depth + 1)) return found;
    }
    return nullptr;
}

// The icon's main color: average of its colorful pixels (grey/transparent ones
// count little), a bit brightened so the line stays visible on a dark taskbar.
winrt::fire_and_forget ComputeIndicatorColor(std::weak_ptr<Target> weak) {
    auto t = weak.lock();
    if (!t || t->computing) co_return;
    auto el = t->element.get();
    if (!el) co_return;
    t->computing = true;
    struct Done { Target* t; ~Done() { t->computing = false; } } done{t.get()};
    try {
        DependencyObject node = Media::VisualTreeHelper::GetParent(el);
        while (node && winrt::get_class_name(node) != L"Taskbar.TaskListButton")
            node = Media::VisualTreeHelper::GetParent(node);
        if (!node) co_return;
        auto icon = FindByName(node, L"Icon");
        auto image = icon ? icon.try_as<Controls::Image>() : nullptr;
        if (!image || icon.ActualWidth() < 1) co_return;
        void* source = winrt::get_abi(image.Source());
        if (t->hasColor && source == t->iconSource) co_return;

        Media::Imaging::RenderTargetBitmap rtb;
        co_await rtb.RenderAsync(icon);
        auto buffer = co_await rtb.GetPixelsAsync();
        const uint8_t* px = buffer.data();
        uint32_t len = buffer.Length();
        double r = 0, g = 0, b = 0, w = 0, ar = 0, ag = 0, ab = 0, aw = 0;
        for (uint32_t i = 0; i + 3 < len; i += 4) {
            double a = px[i + 3] / 255.0;
            if (a < 0.5) continue;
            double B = px[i] / a, G = px[i + 1] / a, R = px[i + 2] / a;   // un-premultiply
            double mx = std::max({R, G, B}), mn = std::min({R, G, B});
            double sat = mx > 0 ? (mx - mn) / mx : 0;
            double weight = a * sat * sat;
            r += R * weight; g += G * weight; b += B * weight; w += weight;
            ar += R; ag += G; ab += B; aw += 1;
        }
        if (aw == 0) co_return;
        if (w < aw * 0.02) { r = ar; g = ag; b = ab; w = aw; }   // grey icon: its average
        r /= w; g /= w; b /= w;
        double mx = std::max({r, g, b, 1.0});
        double boost = mx < 170 ? 170 / mx : 1;                    // keep it bright enough
        auto clamp = [](double v) { return (uint32_t)std::min(255.0, std::max(0.0, v)); };
        t->indicator = 0xFF000000u | (clamp(r * boost) << 16) | (clamp(g * boost) << 8) | clamp(b * boost);
        t->hasColor = true;
        t->iconSource = source;
        ApplyToTarget(*t, LoadSettings());
    } catch (...) {
        Log(L"Indicator color failed: 0x%08X", winrt::to_hresult());
    }
}

// The taskbar's color layer: the chosen color, solid; or, as glass, see-through
// over Windows' own blurred background.
Media::Brush TaskbarBrush(const Settings& s) {
    uint32_t c = (s.colorEnabled ? s.color : 0xFF202020u) & 0x00FFFFFFu;
    uint32_t alpha = s.taskbarStyle == 1 ? (uint32_t)(s.taskbarOpacity * 255 / 100) : 255u;
    return Media::SolidColorBrush(ToColor((alpha << 24) | c));
}

constexpr const wchar_t* kLayerName = L"WibnFill";

// Newer Windows builds keep putting their own brush on BackgroundFill (our color
// never showed), so the color goes on a rectangle of our own, right above it, in
// the same place. Windows doesn't know about it and leaves it alone.
Shapes::Rectangle EnsureLayer(Target& t, const FrameworkElement& el) {
    if (auto layer = t.layer.get()) return layer;
    auto panel = Media::VisualTreeHelper::GetParent(el).try_as<Controls::Panel>();
    if (!panel) return nullptr;
    Shapes::Rectangle layer;
    layer.Name(kLayerName);
    layer.IsHitTestVisible(false);
    layer.HorizontalAlignment(el.HorizontalAlignment());
    layer.VerticalAlignment(el.VerticalAlignment());
    layer.Width(el.Width());
    layer.Height(el.Height());
    Controls::Grid::SetRow(layer, Controls::Grid::GetRow(el));
    Controls::Grid::SetColumn(layer, Controls::Grid::GetColumn(el));
    Controls::Grid::SetRowSpan(layer, Controls::Grid::GetRowSpan(el));
    Controls::Grid::SetColumnSpan(layer, Controls::Grid::GetColumnSpan(el));
    uint32_t index = 0;
    if (panel.Children().IndexOf(el, index)) panel.Children().InsertAt(index + 1, layer);
    else panel.Children().Append(layer);
    t.layer = winrt::make_weak(layer);
    Log(L"Taskbar color layer added");
    return layer;
}

void RemoveLayer(Target& t) {
    if (auto layer = t.layer.get()) {
        if (auto panel = Media::VisualTreeHelper::GetParent(layer).try_as<Controls::Panel>()) {
            uint32_t index = 0;
            if (panel.Children().IndexOf(layer, index)) panel.Children().RemoveAt(index);
        }
    }
    t.layer = nullptr;
}

// UI thread.
void ApplyToTarget(Target& t, const Settings& s) {
    auto el = t.element.get();
    if (!el) return;

    if (!t.verified) {
        if (!t.requiredAncestor.empty() && !HasAncestorClass(el, t.requiredAncestor)) return;
        t.verified = true;
        Log(L"Color target: %s #%s", winrt::get_class_name(el).c_str(), el.Name().c_str());
    }

    bool want = t.kind == ColorTargetKind::IconSize   ? (s.appActive && s.iconSize > 0)
              : t.kind == ColorTargetKind::DateYear ? (s.appActive && s.showYear)
              : t.kind == ColorTargetKind::RectFill ? (s.appActive && (s.colorEnabled || s.floating || s.taskbarStyle == 1))
              : t.kind == ColorTargetKind::Indicator ? (s.appActive && s.indicatorColor && t.hasColor)
                                                    : (s.appActive && s.colorEnabled);
    if (t.kind == ColorTargetKind::Indicator && s.appActive && s.indicatorColor) {
        ComputeIndicatorColor(t.self);  // first time / the button now shows another app
    }
    t.applying = true;
    try {
        if (want) {
            if (!t.applied) {
                t.original = GetValue(el, t.kind);
                t.originalOpacity = el.Opacity();
                t.applied = true;
            }
            if (t.kind == ColorTargetKind::Hide) {
                el.Visibility(Visibility::Collapsed);
            } else if (t.kind == ColorTargetKind::DateYear) {
                auto original = winrt::unbox_value_or<winrt::hstring>(t.original, L"");
                SetValue(el, t.kind, winrt::box_value(winrt::hstring(WithYear(original.c_str(), el))));
            } else if (t.kind == ColorTargetKind::IconSize) {
                SetValue(el, t.kind, winrt::box_value((double)s.iconSize));
            } else if (t.kind == ColorTargetKind::Indicator) {
                SetValue(el, t.kind, Media::SolidColorBrush(ToColor(t.indicator)));
            } else if (t.kind == ColorTargetKind::RectFill) {
                // Windows' own brush stays on BackgroundFill (it's the blur under the glass).
                // Floating: a gap all around and round corners. The taskbar window is
                // see-through outside the rectangle, so the desktop shows there.
                auto rect = el.try_as<Shapes::Rectangle>();
                if (s.floating && rect) {
                    if (!t.floatApplied) {
                        t.originalMargin = el.Margin();
                        t.originalRadiusX = rect.RadiusX();
                        t.originalRadiusY = rect.RadiusY();
                        t.floatApplied = true;
                        // The other layers next to the background (Windows' thin
                        // border line…) would show as a straight edge: hide them.
                        if (auto parent = Media::VisualTreeHelper::GetParent(el)) {
                            int n = Media::VisualTreeHelper::GetChildrenCount(parent);
                            for (int i = 0; i < n; i++) {
                                auto sib = Media::VisualTreeHelper::GetChild(parent, i).try_as<FrameworkElement>();
                                if (!sib || sib == el || sib.Name() == kLayerName) continue;
                                Log(L"Floating: hiding %s #%s", winrt::get_class_name(sib).c_str(), sib.Name().c_str());
                                t.hiddenSiblings.emplace_back(winrt::make_weak<UIElement>(sib), sib.Opacity());
                                sib.Opacity(0);
                            }
                        }
                    }
                    // Only a small gap along the thin side, so the running-app
                    // lines stay on the background; more at both ends.
                    bool vertical = false;
                    if (auto root = el.XamlRoot()) vertical = root.Size().Height > root.Size().Width;
                    el.Margin(vertical ? Thickness{2, 8, 2, 8} : Thickness{8, 2, 8, 2});
                    rect.RadiusX(8);
                    rect.RadiusY(8);
                } else if (t.floatApplied && rect) {
                    el.Margin(t.originalMargin);
                    rect.RadiusX(t.originalRadiusX);
                    rect.RadiusY(t.originalRadiusY);
                    for (auto& [weak, opacity] : t.hiddenSiblings)
                        if (auto sib = weak.get()) sib.Opacity(opacity);
                    t.hiddenSiblings.clear();
                    t.floatApplied = false;
                }
                // The color layer: a custom color, or glass. Floating alone keeps Windows' look.
                if (s.colorEnabled || s.taskbarStyle == 1) {
                    if (auto layer = EnsureLayer(t, el)) {
                        layer.Fill(TaskbarBrush(s));
                        layer.Margin(el.Margin());
                        if (rect) {
                            layer.RadiusX(rect.RadiusX());
                            layer.RadiusY(rect.RadiusY());
                        }
                    }
                } else {
                    RemoveLayer(t);
                }
            } else {
                SetValue(el, t.kind, Media::SolidColorBrush(ToColor(s.color)));
            }
        } else if (t.applied) {
            if (t.kind == ColorTargetKind::RectFill) RemoveLayer(t);   // its brush was never changed
            else SetValue(el, t.kind, t.original);
            el.Opacity(t.originalOpacity);
            t.applied = false;
            t.original = nullptr;
            if (t.floatApplied) {
                if (auto rect = el.try_as<Shapes::Rectangle>()) {
                    el.Margin(t.originalMargin);
                    rect.RadiusX(t.originalRadiusX);
                    rect.RadiusY(t.originalRadiusY);
                }
                for (auto& [weak, opacity] : t.hiddenSiblings)
                    if (auto sib = weak.get()) sib.Opacity(opacity);
                t.hiddenSiblings.clear();
                t.floatApplied = false;
            }
        }
    } catch (...) {
        Log(L"Color apply failed: 0x%08X", winrt::to_hresult());
    }
    t.applying = false;
}

}  // namespace

void TrackColorTarget(FrameworkElement const& element, ColorTargetKind kind,
                      const wchar_t* requiredAncestor) {
    auto target = std::make_shared<Target>();
    target->element = winrt::make_weak(element);
    target->abi = winrt::get_abi(element);
    target->kind = kind;
    target->requiredAncestor = requiredAncestor ? requiredAncestor : L"";
    target->queue = winrt::Windows::System::DispatcherQueue::GetForCurrentThread();
    target->self = target;

    {
        std::lock_guard lock(g_targetsMutex);
        std::erase_if(g_targets, [](auto const& t) { return !t->element.get(); });
        for (auto& t : g_targets) {
            if (t->abi == target->abi) return;  // already tracked
        }
        g_targets.push_back(target);
    }

    std::weak_ptr<Target> weak = target;

    // Windows puts its own brush back on theme / accent changes; re-apply.
    if (auto dp = WatchedProperty(element, kind)) {
        target->token = element.RegisterPropertyChangedCallback(
            dp, [weak](DependencyObject const&, DependencyProperty const&) {
                auto t = weak.lock();
                if (!t || t->applying || !t->applied) return;
                t->applied = false;  // capture Windows' new value as the original
                ApplyToTarget(*t, LoadSettings());
            });
    }

    // The element may not be in the tree yet when it's reported.
    target->loadedToken = element.Loaded([weak](auto&&, auto&&) {
        if (auto t = weak.lock()) {
            ApplyToTarget(*t, LoadSettings());
        }
    });

    ApplyToTarget(*target, LoadSettings());
}

void ApplyColorToAll(const Settings& settings) {
    std::lock_guard lock(g_targetsMutex);
    std::erase_if(g_targets, [](auto const& t) { return !t->element.get(); });
    for (auto& t : g_targets) {
        auto target = t;
        target->queue.TryEnqueue([target, settings] { ApplyToTarget(*target, settings); });
    }
}
