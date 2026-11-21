add_executable(CleanTrayHook.Tests
    main.cpp
    TrayPayloadTests.cpp
    AppKeyTests.cpp
    RulesViewTests.cpp
    CallbacksTests.cpp
    WireTests.cpp
    CoalescerTests.cpp
    TrayInterceptorTests.cpp)

target_link_libraries(CleanTrayHook.Tests PRIVATE CleanTrayHook.Core)
target_compile_options(CleanTrayHook.Tests PRIVATE /utf-8)
target_compile_definitions(CleanTrayHook.Tests PRIVATE
    CT_FIXTURES_DIR="${CMAKE_SOURCE_DIR}/tests/fixtures")
set_target_properties(CleanTrayHook.Tests PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_test(NAME CleanTrayHook.Tests COMMAND CleanTrayHook.Tests)
