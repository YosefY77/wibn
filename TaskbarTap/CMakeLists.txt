cmake_minimum_required(VERSION 3.20)
project(TaskbarTap LANGUAGES CXX)

if(NOT MSVC)
    message(FATAL_ERROR "Build this with Visual Studio (MSVC).")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Static CRT, so explorer doesn't need any extra runtime installed.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_library(TaskbarTap SHARED
    common.cpp
    color.cpp
    scroll.cpp
    tap.cpp
    TaskbarTap.def
)

# C++/WinRT headers ship with the Windows SDK (Include/<version>/cppwinrt).
get_filename_component(WINSDK_ROOT
    "[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows Kits\\Installed Roots;KitsRoot10]"
    ABSOLUTE)
set(WINSDK_VER "${CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION}")
if(NOT WINSDK_VER)
    set(WINSDK_VER "${CMAKE_SYSTEM_VERSION}")
endif()
if(EXISTS "${WINSDK_ROOT}/Include/${WINSDK_VER}/cppwinrt")
    target_include_directories(TaskbarTap PRIVATE "${WINSDK_ROOT}/Include/${WINSDK_VER}/cppwinrt")
endif()

target_compile_definitions(TaskbarTap PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(TaskbarTap PRIVATE /EHsc /permissive- /bigobj /utf-8 /W3)
target_link_libraries(TaskbarTap PRIVATE windowsapp ole32 shell32 advapi32)
