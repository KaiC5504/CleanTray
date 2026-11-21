add_library(CleanTrayHook.Core STATIC
    core/TrayPayload.cpp
    core/AppKey.cpp
    core/RulesView.cpp
    core/Callbacks.cpp
    core/Wire.cpp
    core/Coalescer.cpp
    core/TrayInterceptor.cpp)

target_include_directories(CleanTrayHook.Core PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_compile_options(CleanTrayHook.Core PRIVATE /utf-8)
set_target_properties(CleanTrayHook.Core PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Loaded into explorer.exe: static CRT so it brings no runtime DLLs along.
add_library(CleanTrayHook SHARED
    runtime/dllmain.cpp
    runtime/Activation.cpp
    runtime/ShellHost.cpp
    runtime/SharedRules.cpp
    runtime/PipeWorker.cpp
    runtime/TestHooks.cpp
    runtime/CleanTrayHook.def)

target_link_libraries(CleanTrayHook PRIVATE CleanTrayHook.Core comctl32 user32 gdi32 kernel32)
target_compile_options(CleanTrayHook PRIVATE /utf-8 /EHsc)
set_target_properties(CleanTrayHook PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
