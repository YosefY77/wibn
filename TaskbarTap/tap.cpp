// Entry point of the DLL. It runs inside explorer.exe (taskbar, tray overflow)
// and inside StartMenuExperienceHost.exe (Start menu).
//
// The WPF app loads this DLL with InitializeXamlDiagnosticsEx, the official
// XAML diagnostics API (the same one Visual Studio's Live Visual Tree uses).
// Windows then creates our "TAP" object and hands it the XAML diagnostics
// interface, which reports every XAML element. We react to the few elements
// we care about. No function hooks, no symbol downloads.
//
// Settings come from HKCU\Software\WinPlus\TaskbarScroll. We watch that key
// with RegNotifyChangeKeyValue, which also works inside the Start menu's
// sandbox (named events from the app aren't visible there).

#include "common.h"
#include "scroll.h"
#include "color.h"

#include <winrt/Windows.UI.Xaml.Automation.h>
#include <winrt/Windows.UI.Xaml.Data.h>
#include <winrt/Windows.UI.Xaml.Interop.h>

#include <mutex>
#include <set>
#include <thread>
#include <vector>

using namespace winrt::Windows::UI::Xaml;

namespace {

struct ControllerEntry {
    std::shared_ptr<ScrollController> controller;
    winrt::Windows::System::DispatcherQueue queue{nullptr};
};

std::mutex g_mutex;
std::vector<ControllerEntry> g_controllers;
std::once_flag g_initOnce;
bool g_isExplorer = false;

std::wstring ProcessName() {
    wchar_t path[MAX_PATH];
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    std::wstring s = path;
    auto slash = s.find_last_of(L"\\/");
    if (slash != std::wstring::npos) s = s.substr(slash + 1);
    for (auto& c : s) c = towlower(c);
    return s;
}

void PushSettings() {
    Settings settings = LoadSettings();
    Log(L"Settings: scroll=%d appActive=%d step=%d smooth=%d color=%d #%06X",
        settings.enabled, settings.appActive, settings.scrollStep,
        settings.smooth, settings.colorEnabled, settings.color & 0xFFFFFF);

    ApplyColorToAll(settings);

    std::lock_guard lock(g_mutex);
    for (auto& entry : g_controllers) {
        auto controller = entry.controller;
        entry.queue.TryEnqueue([controller, settings] {
            try {
                controller->Apply(settings);
            } catch (...) {
                Log(L"Apply failed");
            }
        });
    }
}

// Background thread: wakes up whenever the settings key changes.
void SettingsWatchThread() {
    HANDLE changed = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    while (true) {
        HKEY key = nullptr;
        if (RegOpenKeyExW(HKEY_CURRENT_USER, kSettingsKey, 0, KEY_NOTIFY | KEY_READ,
                          &key) != ERROR_SUCCESS) {
            Sleep(5000);
            continue;
        }
        while (RegNotifyChangeKeyValue(key, FALSE, REG_NOTIFY_CHANGE_LAST_SET,
                                       changed, TRUE) == ERROR_SUCCESS) {
            if (WaitForSingleObject(changed, INFINITE) != WAIT_OBJECT_0) break;
            Sleep(30);  // let a burst of writes finish (e.g. while picking a color)
            PushSettings();
        }
        RegCloseKey(key);
        Sleep(1000);
    }
}

void AttachToRepeater(FrameworkElement const& repeater) {
    std::shared_ptr<ScrollController> controller;
    {
        std::lock_guard lock(g_mutex);

        std::erase_if(g_controllers, [](const ControllerEntry& e) {
            return !e.controller->IsAlive();
        });

        for (auto& entry : g_controllers) {
            if (entry.controller->Owns(repeater)) {
                return;  // already attached
            }
        }

        controller = std::make_shared<ScrollController>(repeater);
        g_controllers.push_back(
            {controller, winrt::Windows::System::DispatcherQueue::GetForCurrentThread()});
        Log(L"Attached to a taskbar (%zu total)", g_controllers.size());
    }
    controller->Apply(LoadSettings());
}

// ---- Diagnostic (temporary): how does Windows order the task buttons? ----
// Written once per taskbar into the log, so we can decide whether running
// apps can be moved first through the real list (menus stay correct) or not.

void LogDescendants(const DependencyObject& parent, int depth, int& budget) {
    if (depth > 9 || budget <= 0) return;
    int n = Media::VisualTreeHelper::GetChildrenCount(parent);
    for (int i = 0; i < n && budget > 0; i++) {
        auto child = Media::VisualTreeHelper::GetChild(parent, i);
        if (auto fe = child.try_as<FrameworkElement>()) {
            auto name = fe.Name();
            if (!name.empty()) {
                budget--;
                Log(L"      %*s%s #%s vis=%d op=%.2f size=%.0fx%.0f", depth * 2, L"",
                    winrt::get_class_name(fe).c_str(), name.c_str(),
                    fe.Visibility() == Visibility::Visible, fe.Opacity(),
                    fe.ActualWidth(), fe.ActualHeight());
            }
        }
        LogDescendants(child, depth + 1, budget);
    }
}

void LogCollection(const wchar_t* what, const winrt::Windows::Foundation::IInspectable& obj) {
    if (!obj) {
        Log(L"    %s: null", what);
        return;
    }
    std::wstring info = winrt::get_class_name(obj).c_str();
    if (auto v = obj.try_as<winrt::Windows::Foundation::Collections::IObservableVector<
                     winrt::Windows::Foundation::IInspectable>>()) {
        info += L" [IObservableVector size=" + std::to_wstring(v.Size()) + L"]";
    } else if (auto v2 = obj.try_as<winrt::Windows::Foundation::Collections::IVector<
                             winrt::Windows::Foundation::IInspectable>>()) {
        info += L" [IVector size=" + std::to_wstring(v2.Size()) + L"]";
    }
    if (obj.try_as<winrt::Windows::UI::Xaml::Interop::IBindableObservableVector>()) {
        info += L" [IBindableObservableVector]";
    }
    if (auto bv = obj.try_as<winrt::Windows::UI::Xaml::Interop::IBindableVector>()) {
        info += L" [IBindableVector size=" + std::to_wstring(bv.Size()) + L"]";
    }
    if (obj.try_as<winrt::Windows::UI::Xaml::Data::ICollectionView>()) {
        info += L" [ICollectionView]";
    }
    Log(L"    %s: %s", what, info.c_str());
}

void DiagnoseOrdering(IXamlDiagnostics* diag, InstanceHandle handle) {
    static int count = 0;
    if (count++ >= 2) return;
    Log(L"--- ordering diagnostic ---");

    // Properties of the repeater (ItemsSource, Layout, ...), via the same
    // diagnostics API Visual Studio uses.
    winrt::com_ptr<IVisualTreeService> service;
    if (SUCCEEDED(diag->QueryInterface(IID_PPV_ARGS(service.put())))) {
        unsigned int sourceCount = 0, propCount = 0;
        PropertyChainSource* sources = nullptr;
        PropertyChainValue* values = nullptr;
        HRESULT hr = service->GetPropertyValuesChain(handle, &sourceCount, &sources,
                                                     &propCount, &values);
        Log(L"  GetPropertyValuesChain: 0x%08X (%u values)", hr, propCount);
        if (SUCCEEDED(hr)) {
            for (unsigned int i = 0; i < propCount; i++) {
                auto& v = values[i];
                std::wstring prop = v.PropertyName ? v.PropertyName : L"";
                bool interesting = prop.find(L"Source") != std::wstring::npos ||
                                   prop.find(L"Layout") != std::wstring::npos ||
                                   prop.find(L"Template") != std::wstring::npos;
                if (interesting && !v.Overridden) {
                    Log(L"    prop %s (%s) = %s [type %s]", prop.c_str(),
                        v.DeclaringType ? v.DeclaringType : L"", v.Value ? v.Value : L"",
                        v.ValueType ? v.ValueType : L"");
                    if ((v.MetadataBits & IsValueHandle) && v.Value) {
                        winrt::Windows::Foundation::IInspectable obj;
                        InstanceHandle h = (InstanceHandle)_wcstoui64(v.Value, nullptr, 10);
                        if (SUCCEEDED(diag->GetIInspectableFromHandle(
                                h, reinterpret_cast<::IInspectable**>(winrt::put_abi(obj))))) {
                            LogCollection(prop.c_str(), obj);
                        }
                    }
                }
                SysFreeString(v.Type); SysFreeString(v.DeclaringType);
                SysFreeString(v.ValueType); SysFreeString(v.ItemType);
                SysFreeString(v.Value); SysFreeString(v.PropertyName);
            }
            for (unsigned int i = 0; i < sourceCount; i++) {
                SysFreeString(sources[i].TargetType); SysFreeString(sources[i].Name);
                SysFreeString(sources[i].SrcInfo.FileName);
                SysFreeString(sources[i].SrcInfo.Hash);
            }
            CoTaskMemFree(values);
            CoTaskMemFree(sources);
        }
    }

    // Each task button: its data object and the named parts inside it
    // (looking for what tells "running" from "pinned only").
    winrt::Windows::Foundation::IInspectable inspectable;
    if (FAILED(diag->GetIInspectableFromHandle(
            handle, reinterpret_cast<::IInspectable**>(winrt::put_abi(inspectable))))) {
        return;
    }
    auto repeater = inspectable.try_as<FrameworkElement>();
    if (!repeater) return;
    Log(L"  repeater class: %s", winrt::get_class_name(repeater).c_str());
    int n = Media::VisualTreeHelper::GetChildrenCount(repeater);
    int detailed = 0;
    for (int i = 0; i < n; i++) {
        auto c = Media::VisualTreeHelper::GetChild(repeater, i).try_as<FrameworkElement>();
        if (!c) continue;
        auto slot = Controls::Primitives::LayoutInformation::GetLayoutSlot(c);
        auto dc = c.DataContext();
        std::wstring name = Automation::AutomationProperties::GetName(c).c_str();
        Log(L"  child %d: %s slot=(%.0f,%.0f) name='%s' data=%s", i,
            winrt::get_class_name(c).c_str(), slot.X, slot.Y, name.c_str(),
            dc ? winrt::get_class_name(dc).c_str() : L"null");
        if (detailed < 3 && slot.X > -5000 && slot.Y > -5000) {
            detailed++;
            int budget = 40;
            LogDescendants(c, 0, budget);
        }
    }
    Log(L"--- end ordering diagnostic ---");
}

class VisualTreeWatcher
    : public winrt::implements<VisualTreeWatcher, IVisualTreeServiceCallback2,
                               winrt::non_agile> {
   public:
    explicit VisualTreeWatcher(winrt::com_ptr<IXamlDiagnostics> diagnostics)
        : m_diagnostics(std::move(diagnostics)) {}

    void Start() {
        // AdviseVisualTreeChange must not be called on the thread that called
        // SetSite, otherwise it deadlocks.
        winrt::com_ptr<VisualTreeWatcher> self;
        self.copy_from(this);
        std::thread([self] {
            HRESULT hr = self->m_diagnostics.as<IVisualTreeService3>()
                             ->AdviseVisualTreeChange(self.get());
            Log(L"AdviseVisualTreeChange: 0x%08X", hr);
        }).detach();
    }

    HRESULT STDMETHODCALLTYPE OnVisualTreeChange(
        ParentChildRelation, VisualElement element,
        VisualMutationType mutationType) noexcept override {
        try {
            if (mutationType != Add || !element.Name || !*element.Name) {
                return S_OK;
            }
            const wchar_t* name = element.Name;

            enum class What { None, Repeater, TaskbarFill, BorderColor, BorderHide, Icon, Date, Indicator };
            What what = What::None;
            if (g_isExplorer && wcscmp(name, L"TaskbarFrameRepeater") == 0) {
                what = What::Repeater;
            } else if (g_isExplorer && wcscmp(name, L"BackgroundFill") == 0) {
                what = What::TaskbarFill;
            } else if (wcscmp(name, L"OverflowFlyoutBackgroundBorder") == 0 ||
                       wcscmp(name, L"AcrylicBorder") == 0) {
                what = What::BorderColor;  // tray overflow / Start menu
            } else if (g_isExplorer && wcscmp(name, L"DateInnerTextBlock") == 0) {
                what = What::Date;         // taskbar clock: date line
            } else if (g_isExplorer && wcscmp(name, L"Icon") == 0) {
                what = What::Icon;         // taskbar button icon (checked below)
            } else if (g_isExplorer && wcscmp(name, L"RunningIndicator") == 0) {
                what = What::Indicator;    // running-app line under a task button
            } else if (wcscmp(name, L"AcrylicOverlay") == 0) {
                what = What::BorderHide;   // Start menu tint layer
            }

            // Names of indicator-like parts, once (to see what Windows calls them).
            static int indicatorNames = 0;
            if (g_isExplorer && what == What::None && indicatorNames < 8 && wcsstr(name, L"Indicator")) {
                indicatorNames++;
                Log(L"  element: %s #%s", element.Type ? element.Type : L"?", name);
            }
            if (what == What::None) {
                LogInteresting(element);
                return S_OK;
            }

            winrt::Windows::Foundation::IInspectable inspectable;
            winrt::check_hresult(m_diagnostics->GetIInspectableFromHandle(
                element.Handle,
                reinterpret_cast<::IInspectable**>(winrt::put_abi(inspectable))));
            auto fe = inspectable.try_as<FrameworkElement>();
            if (!fe) return S_OK;

            switch (what) {
                case What::Repeater: {
                    AttachToRepeater(fe);
                    // Buttons and ItemsSource arrive a bit later.
                    auto diag = m_diagnostics;
                    InstanceHandle handle = element.Handle;
                    auto timer = winrt::Windows::System::DispatcherQueue::GetForCurrentThread()
                                     .CreateTimer();
                    timer.Interval(std::chrono::seconds(4));
                    timer.IsRepeating(false);
                    timer.Tick([diag, handle](auto const& t, auto const&) {
                        try {
                            DiagnoseOrdering(diag.get(), handle);
                        } catch (...) {
                            Log(L"ordering diagnostic failed: 0x%08X", winrt::to_hresult());
                        }
                    });
                    timer.Start();
                    m_timers.push_back(timer);
                    break;
                }
                case What::TaskbarFill:
                    TrackColorTarget(fe, ColorTargetKind::RectFill,
                                     L"Taskbar.TaskbarBackground");
                    break;
                case What::BorderColor:
                    TrackColorTarget(fe, ColorTargetKind::BorderBackground, nullptr);
                    break;
                case What::BorderHide:
                    TrackColorTarget(fe, ColorTargetKind::Hide, nullptr);
                    break;
                case What::Date:
                    TrackColorTarget(fe, ColorTargetKind::DateYear, nullptr);
                    break;
                case What::Icon:
                    TrackColorTarget(fe, ColorTargetKind::IconSize, L"Taskbar.TaskListButton");
                    break;
                case What::Indicator:
                    TrackColorTarget(fe, ColorTargetKind::Indicator, L"Taskbar.TaskListButton");
                    break;
                default:
                    break;
            }
        } catch (...) {
            Log(L"OnVisualTreeChange failed: 0x%08X", winrt::to_hresult());
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE OnElementStateChanged(
        InstanceHandle, VisualElementState, LPCWSTR) noexcept override {
        return S_OK;
    }

   private:
    // Outside explorer, log background-looking elements once, so we can see
    // the Start menu's structure in the log if colors don't apply.
    void LogInteresting(const VisualElement& element) {
        if (g_isExplorer || m_logged.size() >= 60) return;
        std::wstring name = element.Name;
        if (name.find(L"Background") == std::wstring::npos &&
            name.find(L"Acrylic") == std::wstring::npos &&
            name.find(L"Border") == std::wstring::npos) {
            return;
        }
        if (!m_logged.insert(name).second) return;
        Log(L"  element: %s #%s", element.Type ? element.Type : L"?", name.c_str());
    }

    winrt::com_ptr<IXamlDiagnostics> m_diagnostics;
    std::set<std::wstring> m_logged;
    std::vector<winrt::Windows::System::DispatcherQueueTimer> m_timers;
};

class Tap : public winrt::implements<Tap, IObjectWithSite, winrt::non_agile> {
   public:
    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) noexcept override {
        try {
            m_site.copy_from(site);
            if (!site) {
                return S_OK;
            }

            std::call_once(g_initOnce, [] {
                std::wstring process = ProcessName();
                g_isExplorer = process == L"explorer.exe";
                Log(L"TAP loaded into %s (pid %lu)", process.c_str(), GetCurrentProcessId());
                std::thread(SettingsWatchThread).detach();
            });

            m_watcher = winrt::make_self<VisualTreeWatcher>(
                m_site.as<IXamlDiagnostics>());
            m_watcher->Start();
            return S_OK;
        } catch (...) {
            Log(L"SetSite failed: 0x%08X", winrt::to_hresult());
            return winrt::to_hresult();
        }
    }

    HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** ppv) noexcept override {
        if (!m_site) {
            *ppv = nullptr;
            return E_FAIL;
        }
        return m_site->QueryInterface(riid, ppv);
    }

   private:
    winrt::com_ptr<IUnknown> m_site;
    winrt::com_ptr<VisualTreeWatcher> m_watcher;
};

class TapFactory : public winrt::implements<TapFactory, IClassFactory> {
   public:
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid,
                                             void** ppv) noexcept override {
        *ppv = nullptr;
        if (outer) {
            return CLASS_E_NOAGGREGATION;
        }
        try {
            return winrt::make_self<Tap>()->QueryInterface(riid, ppv);
        } catch (...) {
            return winrt::to_hresult();
        }
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL) noexcept override { return S_OK; }
};

}  // namespace

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* ppv) {
    *ppv = nullptr;
    if (clsid != CLSID_WinPlusTap) {
        return CLASS_E_CLASSNOTAVAILABLE;
    }
    try {
        return winrt::make_self<TapFactory>()->QueryInterface(riid, ppv);
    } catch (...) {
        return winrt::to_hresult();
    }
}

// Stay loaded: XAML keeps references to our event handlers.
STDAPI DllCanUnloadNow() {
    return S_FALSE;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
