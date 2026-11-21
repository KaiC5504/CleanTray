add_executable(CleanTrayHook.RuntimeTests main.cpp)

target_link_libraries(CleanTrayHook.RuntimeTests PRIVATE CleanTrayHook.Core user32)
target_compile_options(CleanTrayHook.RuntimeTests PRIVATE /utf-8)
target_compile_definitions(CleanTrayHook.RuntimeTests PRIVATE
    CT_HOOK_DLL=L"$<TARGET_FILE:CleanTrayHook>")
set_target_properties(CleanTrayHook.RuntimeTests PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_dependencies(CleanTrayHook.RuntimeTests CleanTrayHook)

add_test(NAME CleanTrayHook.RuntimeTests COMMAND CleanTrayHook.RuntimeTests)
set_tests_properties(CleanTrayHook.RuntimeTests PROPERTIES TIMEOUT 120)
