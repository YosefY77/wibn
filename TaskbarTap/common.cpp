#include "common.h"

#include <shlobj.h>

#include <cstdarg>
#include <cstdio>
#include <mutex>

namespace {

DWORD ReadDword(HKEY key, const wchar_t* name, DWORD fallback) {
    DWORD value = 0;
    DWORD size = sizeof(value);
    if (RegGetValueW(key, nullptr, name, RRF_RT_REG_DWORD, nullptr, &value,
                     &size) == ERROR_SUCCESS) {
        return value;
    }
    return fallback;
}

std::wstring LogPath() {
    PWSTR localAppData = nullptr;
    std::wstring path;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr,
                                       &localAppData))) {
        path = localAppData;
        path += L"\\WinPlus";
        CreateDirectoryW(path.c_str(), nullptr);
        path += L"\\taskbar.log";
    }
    CoTaskMemFree(localAppData);
    return path;
}

}  // namespace

Settings LoadSettings() {
    Settings s;
    HKEY key;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kSettingsKey, 0, KEY_READ, &key) ==
        ERROR_SUCCESS) {
        s.enabled = ReadDword(key, L"Enabled", 1) != 0;
        s.appActive = ReadDword(key, L"AppActive", 1) != 0;
        s.scrollStep = (int)ReadDword(key, L"ScrollStep", 1);
        s.reverse = ReadDword(key, L"Reverse", 0) != 0;
        s.smooth = ReadDword(key, L"Smooth", 1) != 0;
        s.colorEnabled = ReadDword(key, L"ColorEnabled", 0) != 0;
        s.color = ReadDword(key, L"Color", 0xFF202020);
        s.iconSize = (int)ReadDword(key, L"IconSize", 0);
        s.showYear = ReadDword(key, L"ShowYear", 0) != 0;
        s.runningFirst = ReadDword(key, L"RunningFirst", 0) != 0;
        s.taskbarStyle = (int)ReadDword(key, L"TaskbarStyle", 0);
        s.taskbarOpacity = (int)ReadDword(key, L"TaskbarOpacity", 60);
        s.floating = ReadDword(key, L"Floating", 0) != 0;
        s.fitApps = ReadDword(key, L"FitApps", 0) != 0;
        s.magnify = ReadDword(key, L"Magnify", 0) != 0;
        s.indicatorColor = ReadDword(key, L"IndicatorColor", 0) != 0;
        s.extraSpace = (int)ReadDword(key, L"ExtraSpace", 20000);
        s.debugDump = ReadDword(key, L"DebugDump", 1) != 0;
        RegCloseKey(key);
    }
    if (s.taskbarStyle < 0 || s.taskbarStyle > 2) s.taskbarStyle = 0;
    if (s.taskbarOpacity < 0) s.taskbarOpacity = 0;
    if (s.taskbarOpacity > 100) s.taskbarOpacity = 100;
    if (s.scrollStep < 1) s.scrollStep = 1;
    if (s.scrollStep > 10) s.scrollStep = 10;
    if (s.iconSize != 0 && (s.iconSize < 12 || s.iconSize > 40)) s.iconSize = 0;
    if (s.extraSpace < 1000) s.extraSpace = 1000;
    if (s.extraSpace > 100000) s.extraSpace = 100000;
    return s;
}

void Log(const wchar_t* fmt, ...) {
    static std::mutex mutex;
    static const std::wstring path = LogPath();
    if (path.empty()) return;

    wchar_t buffer[1024];
    va_list args;
    va_start(args, fmt);
    _vsnwprintf_s(buffer, _TRUNCATE, fmt, args);
    va_end(args);

    std::lock_guard lock(mutex);

    // Keep the log small: start over once it passes 1 MB.
    WIN32_FILE_ATTRIBUTE_DATA attr;
    const wchar_t* mode = L"a, ccs=UTF-8";
    if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attr) &&
        attr.nFileSizeLow > 1024 * 1024) {
        mode = L"w, ccs=UTF-8";
    }

    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), mode) == 0 && f) {
        SYSTEMTIME t;
        GetLocalTime(&t);
        fwprintf(f, L"%02d:%02d:%02d.%03d [%lu] %s\n", t.wHour, t.wMinute,
                 t.wSecond, t.wMilliseconds, GetCurrentThreadId(), buffer);
        fclose(f);
    }
}
