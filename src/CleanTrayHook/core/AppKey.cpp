#include "AppKey.h"

#include <algorithm>
#include <array>
#include <climits>
#include <cstdio>
#include <vector>

namespace ct {
namespace {

std::wstring Lower(std::wstring_view s) {
    std::wstring out(s);
    if (out.empty()) return out;
    int n = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, s.data(), (int)s.size(),
                          out.data(), (int)out.size(), nullptr, nullptr, 0);
    if (n != (int)s.size()) return std::wstring(s);
    return out;
}

// Number of dot-separated digit groups, or 0 if s is not purely digits and dots.
int DigitGroups(std::wstring_view s) {
    if (s.empty()) return 0;
    int groups = 1;
    bool prevDigit = false;
    for (wchar_t c : s) {
        if (c >= L'0' && c <= L'9') {
            prevDigit = true;
        } else if (c == L'.' && prevDigit) {
            ++groups;
            prevDigit = false;
        } else {
            return 0;
        }
    }
    return prevDigit ? groups : 0;
}

bool IsPureVersion(std::wstring_view s) {
    int g = DigitGroups(s);
    return g >= 2 && g <= 4;
}

bool IsAppVersionFolder(std::wstring_view s) {
    constexpr std::wstring_view kPrefix = L"app-";
    return s.size() > kPrefix.size() && s.substr(0, kPrefix.size()) == kPrefix &&
           DigitGroups(s.substr(kPrefix.size())) >= 1;
}

std::vector<std::wstring_view> Split(std::wstring_view s, wchar_t sep) {
    std::vector<std::wstring_view> parts;
    size_t start = 0;
    for (;;) {
        size_t p = s.find(sep, start);
        if (p == std::wstring_view::npos) {
            parts.push_back(s.substr(start));
            return parts;
        }
        parts.push_back(s.substr(start, p - start));
        start = p + 1;
    }
}

// name_version_arch_resourceId_publisherId -> name_publisherId
bool TryRewriteMsix(std::wstring_view seg, std::wstring& out) {
    auto parts = Split(seg, L'_');
    if (parts.size() != 5 || !IsPureVersion(parts[1])) return false;
    out.assign(parts[0]);
    out.push_back(L'_');
    out.append(parts[4]);
    return true;
}

std::wstring NormalizePath(std::wstring_view path) {
    std::wstring lowered = Lower(path);
    for (auto& c : lowered)
        if (c == L'/') c = L'\\';

    auto segs = Split(lowered, L'\\');
    std::wstring out;
    for (size_t i = 0; i < segs.size(); ++i) {
        std::wstring_view seg = segs[i];
        std::wstring rewritten;
        bool last = i + 1 == segs.size();
        // The final segment is the exe name; never treat it as a version folder.
        if (!last) {
            if (TryRewriteMsix(seg, rewritten)) {
                seg = rewritten;
            } else if (IsAppVersionFolder(seg)) {
                continue;
            } else if (IsPureVersion(seg)) {
                seg = L"*";
            }
        }
        if (!out.empty() || i > 0) out.push_back(L'\\');
        out.append(seg);
    }
    return out;
}

constexpr std::array<std::wstring_view, 12> kHostExes = {
    L"javaw", L"java", L"python", L"pythonw", L"autohotkey", L"autohotkey64",
    L"autohotkeyu64", L"rundll32", L"powershell", L"pwsh", L"wscript", L"cscript"};
constexpr std::wstring_view kNodeHost = L"node";

constexpr std::array<std::wstring_view, 8> kScriptExts = {
    L".jar", L".py", L".pyw", L".ahk", L".ps1", L".js", L".vbs", L".dll"};

bool IsHostExe(std::wstring_view lowerPath) {
    size_t slash = lowerPath.find_last_of(L'\\');
    std::wstring_view file = slash == std::wstring_view::npos ? lowerPath : lowerPath.substr(slash + 1);
    constexpr std::wstring_view kExe = L".exe";
    if (file.size() <= kExe.size() || file.substr(file.size() - kExe.size()) != kExe) return false;
    std::wstring_view stem = file.substr(0, file.size() - kExe.size());
    if (stem == kNodeHost) return true;
    for (auto h : kHostExes)
        if (stem == h) return true;
    return false;
}

std::vector<std::wstring> SplitCommandLine(std::wstring_view cl) {
    std::vector<std::wstring> args;
    std::wstring cur;
    bool inQuote = false, have = false;
    for (wchar_t c : cl) {
        if (c == L'"') {
            inQuote = !inQuote;
            have = true;
        } else if ((c == L' ' || c == L'\t') && !inQuote) {
            if (have) args.push_back(std::move(cur));
            cur.clear();
            have = false;
        } else {
            cur.push_back(c);
            have = true;
        }
    }
    if (have) args.push_back(std::move(cur));
    return args;
}

bool HasScriptExt(std::wstring_view s) {
    for (auto ext : kScriptExts)
        if (s.size() >= ext.size() && s.substr(s.size() - ext.size()) == ext) return true;
    return false;
}

std::wstring ScriptArgument(std::wstring_view commandLine) {
    auto args = SplitCommandLine(commandLine);
    for (size_t i = 1; i < args.size(); ++i) {
        std::wstring_view a = args[i];
        // rundll32 passes "file.dll,Entry"; the entry point is not part of the identity.
        a = a.substr(0, a.find(L','));
        std::wstring lowered = Lower(a);
        if (HasScriptExt(lowered)) return lowered;
    }
    return {};
}

}  // namespace

std::wstring NormalizeAppKey(std::wstring_view exePath, std::wstring_view commandLine) {
    std::wstring key = NormalizePath(exePath);
    if (IsHostExe(key)) {
        std::wstring script = ScriptArgument(commandLine);
        if (!script.empty()) {
            key.push_back(L'|');
            key.append(script);
        }
    }
    return key;
}

int CopyAppKey(const wchar_t* exePath, const wchar_t* commandLine, wchar_t* out, int outChars) noexcept {
    if (!exePath || outChars < 0 || (!out && outChars > 0)) return -1;
    std::wstring key;
    try {
        key = NormalizeAppKey(exePath, commandLine ? commandLine : L"");
    } catch (...) {
        return -1;
    }
    if (key.size() >= (size_t)INT_MAX) return -1;
    int len = (int)key.size();
    if (outChars <= len) {
        if (outChars > 0) out[0] = L'\0';
        return len + 1;
    }
    std::copy(key.begin(), key.end(), out);
    out[len] = L'\0';
    return len;
}

std::wstring IconPart(const TrayMessage& m) {
    wchar_t buf[48];
    if (m.flags & NIF_GUID) {
        const GUID& g = m.guid;
        swprintf_s(buf, L"{%08lx-%04x-%04x-%02x%02x-%02x%02x%02x%02x%02x%02x}", g.Data1, g.Data2,
                   g.Data3, g.Data4[0], g.Data4[1], g.Data4[2], g.Data4[3], g.Data4[4],
                   g.Data4[5], g.Data4[6], g.Data4[7]);
    } else {
        swprintf_s(buf, L"uid:%u", m.uid);
    }
    return buf;
}

}  // namespace ct
