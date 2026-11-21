#include "Check.h"
#include "core/AppKey.h"
#include "core/TrayPayload.h"

#include <algorithm>
#include <iterator>
#include <vector>

using namespace ct;

TEST(AppKey_StripsVersionedAppFolder) {
    CHECK_EQ(NormalizeAppKey(L"C:\\Users\\User\\AppData\\Local\\Discord\\app-1.0.9175\\Discord.exe", L""),
             std::wstring(L"c:\\users\\user\\appdata\\local\\discord\\discord.exe"));
}

TEST(AppKey_RewritesMsixPackageFolder) {
    CHECK_EQ(NormalizeAppKey(L"C:\\Program Files\\WindowsApps\\Microsoft.YourPhone_1.24.1.0_x64__8wekyb3d8bbwe\\PhoneExperienceHost.exe", L""),
             std::wstring(L"c:\\program files\\windowsapps\\microsoft.yourphone_8wekyb3d8bbwe\\phoneexperiencehost.exe"));
}

TEST(AppKey_ReplacesPureVersionSegment) {
    CHECK_EQ(NormalizeAppKey(L"C:\\Apps\\Tool\\2.3.1\\tool.exe", L""),
             std::wstring(L"c:\\apps\\tool\\*\\tool.exe"));
}

TEST(AppKey_HostExeAppendsScriptArgument) {
    CHECK_EQ(NormalizeAppKey(L"C:\\Java\\bin\\javaw.exe", L"\"C:\\Java\\bin\\javaw.exe\" -Xmx1g -jar \"D:\\T\\app-2.1.jar\" --x"),
             std::wstring(L"c:\\java\\bin\\javaw.exe|d:\\t\\app-2.1.jar"));
    CHECK_EQ(NormalizeAppKey(L"C:\\Windows\\System32\\rundll32.exe", L"rundll32.exe C:\\X\\y.dll,Entry 1"),
             std::wstring(L"c:\\windows\\system32\\rundll32.exe|c:\\x\\y.dll"));
}

TEST(AppKey_HostExeWithoutScriptHasNoSuffix) {
    CHECK_EQ(NormalizeAppKey(L"C:\\Python\\pythonw.exe", L"pythonw.exe -u"),
             std::wstring(L"c:\\python\\pythonw.exe"));
}

TEST(AppKey_NonHostIgnoresCommandLine) {
    CHECK_EQ(NormalizeAppKey(L"C:\\T\\tool.exe", L"tool.exe x.jar"), std::wstring(L"c:\\t\\tool.exe"));
}

TEST(CopyAppKey_WritesTheKeyAndATerminator) {
    const wchar_t* path = L"C:\\Java\\bin\\javaw.exe";
    const wchar_t* cl = L"javaw -jar D:\\T\\app.jar";
    std::wstring expected = NormalizeAppKey(path, cl);
    wchar_t buf[64];
    std::fill(std::begin(buf), std::end(buf), L'x');
    buf[63] = L'\0';
    CHECK_EQ(CopyAppKey(path, cl, buf, 63), (int)expected.size());
    CHECK_EQ(std::wstring(buf), expected);
}

TEST(CopyAppKey_ExactFitSucceeds) {
    const wchar_t* path = L"C:\\T\\tool.exe";
    int len = (int)NormalizeAppKey(path, L"").size();
    std::vector<wchar_t> buf((size_t)len + 2, L'x');
    buf.back() = L'\0';
    CHECK_EQ(CopyAppKey(path, L"", buf.data(), len + 1), len);
    CHECK_EQ(std::wstring(buf.data()), std::wstring(L"c:\\t\\tool.exe"));
}

TEST(CopyAppKey_TooSmallReturnsTheSizeNeededAndAnEmptyString) {
    const wchar_t* path = L"C:\\T\\tool.exe";
    int len = (int)NormalizeAppKey(path, L"").size();
    std::vector<wchar_t> buf((size_t)len, L'x');
    CHECK_EQ(CopyAppKey(path, L"", buf.data(), len), len + 1);
    CHECK_EQ(buf[0], L'\0');
    CHECK_EQ(buf[1], L'x');
}

TEST(CopyAppKey_NullBufferAsksForTheSize) {
    CHECK_EQ(CopyAppKey(L"C:\\T\\tool.exe", L"", nullptr, 0), (int)std::wstring(L"c:\\t\\tool.exe").size() + 1);
}

TEST(CopyAppKey_NullCommandLineMeansNone) {
    wchar_t buf[64] = {};
    CHECK_EQ(CopyAppKey(L"C:\\Python\\pythonw.exe", nullptr, buf, 64), (int)std::wstring(L"c:\\python\\pythonw.exe").size());
    CHECK_EQ(std::wstring(buf), std::wstring(L"c:\\python\\pythonw.exe"));
}

TEST(CopyAppKey_EmptyPathIsAnEmptyKey) {
    wchar_t buf[4] = {L'x', L'x', L'x', L'x'};
    CHECK_EQ(CopyAppKey(L"", L"", buf, 4), 0);
    CHECK_EQ(buf[0], L'\0');
}

TEST(CopyAppKey_RejectsBadArguments) {
    wchar_t buf[8];
    CHECK_EQ(CopyAppKey(nullptr, L"", buf, 8), -1);
    CHECK_EQ(CopyAppKey(L"C:\\a.exe", L"", buf, -1), -1);
    CHECK_EQ(CopyAppKey(L"C:\\a.exe", L"", nullptr, 8), -1);
}

TEST(IconPart_GuidAndUid) {
    TrayMessage m{};
    m.uid = 7;
    CHECK_EQ(IconPart(m), std::wstring(L"uid:7"));
    m.flags = NIF_GUID;
    m.guid = {0x3b3905d1, 0xafd0, 0xca7e, {0x3c, 0xa2, 0xc9, 0xda, 0x57, 0x91, 0xfd, 0x04}};
    CHECK_EQ(IconPart(m), std::wstring(L"{3b3905d1-afd0-ca7e-3ca2-c9da5791fd04}"));
}
