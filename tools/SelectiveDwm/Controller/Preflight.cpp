#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <tlhelp32.h>
#include <bcrypt.h>
#include <cstdio>
#include <string>
#include <vector>
#include <sstream>
#include <iomanip>
#include "../Hook/BuildContract.h"

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "bcrypt.lib")

namespace {
struct Handle {
    HANDLE value = nullptr;
    explicit Handle(HANDLE v = nullptr) : value(v) {}
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};
struct DebugPrivilege {
    Handle token;
    TOKEN_PRIVILEGES previous{};
    bool changed = false;
    DebugPrivilege() {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES, &token.value)) return;
        TOKEN_PRIVILEGES requested{};
        requested.PrivilegeCount = 1;
        if (!LookupPrivilegeValueW(nullptr, SE_DEBUG_NAME, &requested.Privileges[0].Luid)) return;
        requested.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
        DWORD length = sizeof(previous);
        SetLastError(ERROR_SUCCESS);
        changed = AdjustTokenPrivileges(token.value, FALSE, &requested, sizeof(previous), &previous, &length)
            && GetLastError() == ERROR_SUCCESS;
    }
    ~DebugPrivilege() { if (changed) AdjustTokenPrivileges(token.value, FALSE, &previous, 0, nullptr, nullptr); }
};
std::wstring FileHash(const std::wstring& path) {
    Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr));
    if (file.value == INVALID_HANDLE_VALUE) return {};
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return {};
    DWORD objectLength = 0, actual = 0;
    if (BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&objectLength), sizeof(objectLength), &actual, 0) < 0) {
        BCryptCloseAlgorithmProvider(algorithm, 0); return {};
    }
    std::vector<UCHAR> object(objectLength), buffer(64 * 1024);
    BCRYPT_HASH_HANDLE hash = nullptr;
    bool ok = BCryptCreateHash(algorithm, &hash, object.data(), objectLength, nullptr, 0, 0) >= 0;
    DWORD read = 0;
    while (ok) {
        if (!ReadFile(file.value, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr)) { ok = false; break; }
        if (!read) break;
        ok = BCryptHashData(hash, buffer.data(), read, 0) >= 0;
    }
    UCHAR digest[32]{};
    if (ok) ok = BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0;
    if (hash) BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    if (!ok) return {};
    std::wostringstream text;
    text << std::hex << std::uppercase << std::setfill(L'0');
    for (UCHAR byte : digest) text << std::setw(2) << static_cast<unsigned int>(byte);
    return text.str();
}
unsigned long long CreationTime(HANDLE process) {
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(process, &created, &exited, &kernel, &user)) return 0;
    return (static_cast<unsigned long long>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
}
}

int RunSelectivePreflight() {
    DebugPrivilege debug;
    DWORD session = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &session)) return 3;
    wchar_t systemPath[MAX_PATH]{};
    if (!GetSystemDirectoryW(systemPath, MAX_PATH)) return 3;
    const std::wstring expectedImage = std::wstring(systemPath) + L"\\dwm.exe";
    const std::wstring corePath = std::wstring(systemPath) + L"\\dwmcore.dll";
    const auto hash = FileHash(corePath);
    const bool hashAllowed = hash == selective_dwm::contract::kDwmcoreSha256Hex;
    std::wprintf(L"{\"event\":\"binary\",\"dwmcore_sha256\":\"%ls\",\"allowlisted\":%ls,\"debug_privilege\":%ls}\n",
        hash.c_str(), hashAllowed ? L"true" : L"false", debug.changed ? L"true" : L"false");
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    if (snapshot.value == INVALID_HANDLE_VALUE) return 3;
    PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry);
    unsigned int matches = 0;
    bool eligible = false;
    if (Process32FirstW(snapshot.value, &entry)) do {
        if (_wcsicmp(entry.szExeFile, L"dwm.exe") != 0) continue;
        DWORD processSession = 0;
        if (!ProcessIdToSessionId(entry.th32ProcessID, &processSession) || processSession != session) continue;
        ++matches;
        Handle query(OpenProcess(PROCESS_QUERY_INFORMATION, FALSE, entry.th32ProcessID));
        const DWORD queryError = query.value ? 0 : GetLastError();
        if (!query.value) query.value = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ProcessID);
        if (!query.value) {
            std::printf("{\"event\":\"dwm\",\"pid\":%lu,\"query_error\":%lu,\"eligible\":false}\n", entry.th32ProcessID, queryError);
            continue;
        }
        wchar_t image[MAX_PATH]{}; DWORD imageLength = MAX_PATH;
        const bool exactImage = QueryFullProcessImageNameW(query.value, 0, image, &imageLength)
            && _wcsicmp(image, expectedImage.c_str()) == 0;
        PROCESS_PROTECTION_LEVEL_INFORMATION protection{};
        const BOOL gotProtection = GetProcessInformation(query.value, ProcessProtectionLevelInfo, &protection, sizeof(protection));
        const DWORD protectionError = gotProtection ? 0 : GetLastError();
        PROCESS_MITIGATION_BINARY_SIGNATURE_POLICY signature{};
        const BOOL gotSignature = GetProcessMitigationPolicy(query.value, ProcessSignaturePolicy, &signature, sizeof(signature));
        const DWORD signatureError = gotSignature ? 0 : GetLastError();
        PROCESS_MITIGATION_DYNAMIC_CODE_POLICY dynamic{};
        const BOOL gotDynamic = GetProcessMitigationPolicy(query.value, ProcessDynamicCodePolicy, &dynamic, sizeof(dynamic));
        const DWORD dynamicError = gotDynamic ? 0 : GetLastError();
        PROCESS_MITIGATION_CONTROL_FLOW_GUARD_POLICY cfg{};
        const BOOL gotCfg = GetProcessMitigationPolicy(query.value, ProcessControlFlowGuardPolicy, &cfg, sizeof(cfg));
        PROCESS_MITIGATION_USER_SHADOW_STACK_POLICY cet{};
        const BOOL gotCet = GetProcessMitigationPolicy(query.value, ProcessUserShadowStackPolicy, &cet, sizeof(cet));
        const bool policiesPermit = gotProtection && protection.ProtectionLevel == PROTECTION_LEVEL_NONE
            && gotSignature && (signature.Flags & 7u) == 0 && gotDynamic && !dynamic.ProhibitDynamicCode;
        // Probe ordinary access only. This executable never allocates/writes remote memory,
        // creates remote threads, changes a target policy, or bypasses a protection boundary.
        bool accessGranted = false; DWORD accessError = 0;
        if (exactImage && policiesPermit && hashAllowed) {
            Handle access(OpenProcess(PROCESS_CREATE_THREAD | PROCESS_DUP_HANDLE | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, entry.th32ProcessID));
            accessGranted = access.value != nullptr;
            accessError = accessGranted ? 0 : GetLastError();
        }
        eligible = exactImage && hashAllowed && policiesPermit && accessGranted;
        std::printf("{\"event\":\"dwm\",\"pid\":%lu,\"session\":%lu,\"creation_filetime\":%llu,\"exact_image\":%s,"
            "\"protection_known\":%s,\"protection_level\":%lu,\"protection_error\":%lu,"
            "\"signature_known\":%s,\"signature_flags\":%lu,\"signature_error\":%lu,"
            "\"dynamic_known\":%s,\"dynamic_flags\":%lu,\"dynamic_error\":%lu,"
            "\"cfg_known\":%s,\"cfg_flags\":%lu,\"cet_known\":%s,\"cet_flags\":%lu,"
            "\"ordinary_injection_access\":%s,\"access_error\":%lu,\"eligible\":%s}\n",
            entry.th32ProcessID, processSession, CreationTime(query.value), exactImage ? "true" : "false",
            gotProtection ? "true" : "false", protection.ProtectionLevel, protectionError,
            gotSignature ? "true" : "false", signature.Flags, signatureError,
            gotDynamic ? "true" : "false", dynamic.Flags, dynamicError,
            gotCfg ? "true" : "false", cfg.Flags, gotCet ? "true" : "false", cet.Flags,
            accessGranted ? "true" : "false", accessError, eligible ? "true" : "false");
    } while (Process32NextW(snapshot.value, &entry));
    eligible = matches == 1 && eligible;
    std::printf("{\"event\":\"preflight_complete\",\"matching_dwm_processes\":%u,\"eligible\":%s,\"target_writes\":0,\"scope\":\"access_and_binary_only_not_hook_safety_proof\"}\n", matches, eligible ? "true" : "false");
    return eligible ? 0 : 10;
}

#ifndef HDR_SELECTIVE_PREFLIGHT_LIBRARY
int wmain(int argc, wchar_t** argv) {
    if (argc != 1 && (argc != 2 || wcscmp(argv[1], L"--preflight") != 0)) {
        std::fprintf(stderr, "Usage: HDRCalibrate.SelectivePreflight.exe [--preflight]\n"); return 2;
    }
    return RunSelectivePreflight();
}
#endif
