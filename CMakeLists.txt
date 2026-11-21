cmake_minimum_required(VERSION 3.20)
project(CleanTray LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT so the hook DLL injected into explorer.exe drags in no runtime DLLs.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_compile_definitions(UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
add_compile_options(/W4 /permissive-)

enable_testing()

add_subdirectory(src/CleanTrayHook)
add_subdirectory(tests/CleanTrayHook.Tests)
add_subdirectory(tests/CleanTrayHook.RuntimeTests)
