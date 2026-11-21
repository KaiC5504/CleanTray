#pragma once
#include <windows.h>

#include <cstdio>
#include <fstream>
#include <iterator>
#include <sstream>
#include <string>
#include <type_traits>
#include <vector>

namespace ct_test {

struct TestCase {
    const char* name;
    void (*fn)();
};

inline std::vector<TestCase>& Tests() {
    static std::vector<TestCase> tests;
    return tests;
}

inline int& Failures() {
    static int failures = 0;
    return failures;
}

struct Registrar {
    Registrar(const char* name, void (*fn)()) { Tests().push_back({name, fn}); }
};

inline std::string Narrow(const std::wstring& s) {
    if (s.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, s.data(), (int)s.size(), nullptr, 0, nullptr, nullptr);
    std::string out((size_t)n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, s.data(), (int)s.size(), out.data(), n, nullptr, nullptr);
    return out;
}

template <class T>
std::string Str(const T& v) {
    if constexpr (std::is_same_v<T, std::wstring>) {
        return "\"" + Narrow(v) + "\"";
    } else if constexpr (std::is_same_v<T, std::string>) {
        return "\"" + v + "\"";
    } else if constexpr (std::is_enum_v<T>) {
        return std::to_string((long long)v);
    } else if constexpr (std::is_arithmetic_v<T>) {
        std::ostringstream os;
        os << +v;
        return os.str();
    } else if constexpr (std::is_same_v<typename std::remove_cv<T>::type, std::vector<unsigned long long>>) {
        std::string s = "{";
        for (size_t i = 0; i < v.size(); ++i) s += (i ? "," : "") + std::to_string(v[i]);
        return s + "}";
    } else {
        return "<value>";
    }
}

inline void Fail(const char* file, int line, const std::string& msg) {
    ++Failures();
    std::fprintf(stderr, "%s:%d: FAILED %s\n", file, line, msg.c_str());
}

template <class A, class B>
void CheckEq(const char* file, int line, const char* ea, const char* eb, const A& a, const B& b) {
    if (!(a == b))
        Fail(file, line, std::string(ea) + " == " + eb + "  (" + Str(a) + " vs " + Str(b) + ")");
}

// Whitespace-separated hex bytes; '#' starts a comment that runs to end of line.
inline std::vector<BYTE> LoadHex(const char* name, const char* file, int line) {
    std::ifstream in(std::string(CT_FIXTURES_DIR) + "/" + name);
    if (!in) {
        Fail(file, line, std::string("cannot open fixture ") + name);
        return {};
    }
    std::vector<BYTE> out;
    std::string ln;
    while (std::getline(in, ln)) {
        if (auto p = ln.find('#'); p != std::string::npos) ln.resize(p);
        std::istringstream ls(ln);
        std::string tok;
        while (ls >> tok) out.push_back((BYTE)std::stoul(tok, nullptr, 16));
    }
    return out;
}

}  // namespace ct_test

#define CT_CAT2(a, b) a##b
#define CT_CAT(a, b) CT_CAT2(a, b)

#define TEST(name)                                                              \
    static void name();                                                         \
    static ::ct_test::Registrar CT_CAT(reg_, name)(#name, &name);               \
    static void name()

#define CHECK(expr) \
    do { if (!(expr)) ::ct_test::Fail(__FILE__, __LINE__, #expr); } while (0)

#define CHECK_EQ(a, b) ::ct_test::CheckEq(__FILE__, __LINE__, #a, #b, (a), (b))

#define LOAD_HEX(name) ::ct_test::LoadHex(name, __FILE__, __LINE__)
