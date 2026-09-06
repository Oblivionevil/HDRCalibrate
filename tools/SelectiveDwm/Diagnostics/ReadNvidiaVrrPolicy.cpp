// Public NVAPI SDK pin: a08f45f737bb142b070f1e23c58466ecbc82e398 (MIT).
// https://github.com/NVIDIA/nvapi/blob/a08f45f737bb142b070f1e23c58466ecbc82e398/nvapi.h
// Contracts: FindApplicationByName lines 25014-25035; setting origin/validity
// lines 24486-24514; NvApiDriverSettings.h lines 1157-1205. DEFAULT enum values
// are never evidence of current configuration and are never substituted here.
// Optional sanity: GetSettingNameFromId(NvU32, NvAPI_UnicodeString*) at
// nvapi.h line 25182, public query ID 0xd61cbe6e in nvapi_interface.h line 535.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <nvapi.h>
#include <NvApiDriverSettings.h>
#include <array>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <stdexcept>
#include <string>
#include <vector>
static_assert(sizeof(void*) == 8 && sizeof(wchar_t) == sizeof(NvU16), "Windows x64 only");
namespace {
constexpr char kPin[] = "a08f45f737bb142b070f1e23c58466ecbc82e398";
const char* Bool(bool value) { return value ? "true" : "false"; }
void Emit(const std::string& fields) { std::printf("{%s}\n", fields.c_str()); std::fflush(stdout); }
std::string Number(bool valid, NvU32 value) { return valid ? std::to_string(value) : "null"; }
std::string Quote(const wchar_t* value, size_t maximum = NVAPI_UNICODE_STRING_MAX) {
    const size_t count = wcsnlen_s(value, maximum);
    const int bytes = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, static_cast<int>(count), nullptr, 0, nullptr, nullptr);
    if (bytes <= 0 && count) return "null";
    std::string utf8(static_cast<size_t>(bytes), '\0'), result = "\"";
    if (bytes) WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, static_cast<int>(count), utf8.data(), bytes, nullptr, nullptr);
    for (unsigned char ch : utf8) {
        if (ch == '"' || ch == '\\') { result += '\\'; result += static_cast<char>(ch); }
        else if (ch < 32) { char escaped[7]{}; std::snprintf(escaped, sizeof(escaped), "\\u%04x", static_cast<unsigned int>(ch)); result += escaped; }
        else result += static_cast<char>(ch);
    }
    return result + '"';
}
std::string Text(const NvU16* value) { return Quote(reinterpret_cast<const wchar_t*>(value)); }
void Status(const char* call, NvAPI_Status status) {
    Emit(std::string("\"event\":\"call\",\"api\":\"") + call + "\",\"status\":" + std::to_string(status));
}
bool ValidPath(const std::wstring& path) {
    if (path.size() < 7 || path.size() >= NVAPI_UNICODE_STRING_MAX || _wcsicmp(path.c_str() + path.size() - 4, L".exe")) return false;
    for (wchar_t ch : path) if (ch < 32 || ch == L'"' || ch == L'*' || ch == L'?') return false;
    const bool drive = ((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z'))
        && path[1] == L':' && (path[2] == L'\\' || path[2] == L'/');
    const auto serverEnd = path.find(L'\\', 2);
    const bool unc = path[0] == L'\\' && path[1] == L'\\' && path[2] != L'.' && serverEnd > 2
        && serverEnd != std::wstring::npos && path.find(L'\\', serverEnd + 1) > serverEnd + 1
        && path.find(L'\\', serverEnd + 1) != std::wstring::npos;
    return drive || unc;
}
bool ParseLive(int argc, const wchar_t* const* argv, const wchar_t*& path, bool& diagnose) {
    path = nullptr; diagnose = false;
    if ((argc != 3 && argc != 4) || std::wcscmp(argv[1], L"--exe") || !ValidPath(argv[2])) return false;
    if (argc == 4 && std::wcscmp(argv[3], L"--diagnose-settings")) return false;
    path = argv[2]; diagnose = argc == 4; return true;
}
const char* Location(NVDRS_SETTING_LOCATION location) {
    switch (location) {
    case NVDRS_CURRENT_PROFILE_LOCATION: return "current_profile";
    case NVDRS_GLOBAL_PROFILE_LOCATION: return "global_profile";
    case NVDRS_BASE_PROFILE_LOCATION: return "base_profile";
    case NVDRS_DEFAULT_PROFILE_LOCATION: return "driver_default_location";
    default: return "unknown";
    }
}
struct Policy { NvU32 id; const char* name; };
constexpr Policy kPolicies[] = {{VRR_MODE_ID,"VRR_MODE"}, {VRRREQUESTSTATE_ID,"VRRREQUESTSTATE"},
    {VRR_APP_OVERRIDE_ID,"VRR_APP_OVERRIDE"}, {VRR_APP_OVERRIDE_REQUEST_STATE_ID,"VRR_APP_OVERRIDE_REQUEST_STATE"},
    {VSYNCVRRCONTROL_ID,"VSYNCVRRCONTROL"}};
constexpr Policy kSanityControl{VSYNCMODE_ID,"VSYNCMODE"};
const char* Meaning(NvU32 id, NvU32 value) {
    if (id == VRR_MODE_ID || id == VRRREQUESTSTATE_ID) {
        if (value == VRR_MODE_DISABLED) return "disabled";
        if (value == VRR_MODE_FULLSCREEN_ONLY) return "fullscreen_only";
        if (value == VRR_MODE_FULLSCREEN_AND_WINDOWED) return "fullscreen_and_windowed";
    } else if (id == VRR_APP_OVERRIDE_ID || id == VRR_APP_OVERRIDE_REQUEST_STATE_ID) {
        switch (value) {
        case VRR_APP_OVERRIDE_ALLOW: return "allow";
        case VRR_APP_OVERRIDE_FORCE_OFF: return "force_off";
        case VRR_APP_OVERRIDE_DISALLOW: return "disallow";
        case VRR_APP_OVERRIDE_ULMB: return "ulmb";
        case VRR_APP_OVERRIDE_FIXED_REFRESH: return "fixed_refresh";
        default: break;
        }
    } else if (id == VSYNCVRRCONTROL_ID) {
        if (value == VSYNCVRRCONTROL_DISABLE) return "disable";
        if (value == VSYNCVRRCONTROL_ENABLE) return "enable";
        if (value == VSYNCVRRCONTROL_NOTSUPPORTED) return "not_supported";
    } else if (id == VSYNCMODE_ID) {
        switch (value) {
        case VSYNCMODE_PASSIVE: return "passive";
        case VSYNCMODE_FORCEOFF: return "force_off";
        case VSYNCMODE_FORCEON: return "force_on";
        case VSYNCMODE_FLIPINTERVAL2: return "flip_interval_2";
        case VSYNCMODE_FLIPINTERVAL3: return "flip_interval_3";
        case VSYNCMODE_FLIPINTERVAL4: return "flip_interval_4";
        case VSYNCMODE_VIRTUAL: return "virtual";
        default: break;
        }
    }
    return "unknown";
}
std::string SettingFields(const Policy& policy, NvAPI_Status status, const NVDRS_SETTING& value) {
    const bool returned = status == NVAPI_OK, dword = returned && value.settingType == NVDRS_DWORD_TYPE;
    const bool predefined = dword && value.isPredefinedValid != 0;
    return "\"setting_symbol\":\"" + std::string(policy.name) + "\",\"setting_id\":" + std::to_string(policy.id)
        + ",\"status\":" + std::to_string(status) + ",\"setting_type\":" + Number(returned, value.settingType)
        + ",\"setting_location\":" + Number(returned, value.settingLocation) + ",\"setting_location_name\":\""
        + (returned ? Location(value.settingLocation) : "unknown") + "\",\"is_current_predefined\":" + Number(returned, value.isCurrentPredefined)
        + ",\"is_predefined_valid\":" + Number(returned, value.isPredefinedValid) + ",\"current_value_returned\":" + Bool(dword)
        + ",\"current_value\":" + Number(dword, dword ? value.u32CurrentValue : 0) + ",\"current_meaning\":\""
        + (dword ? Meaning(policy.id, value.u32CurrentValue) : "unknown") + "\",\"predefined_value\":" + Number(predefined, predefined ? value.u32PredefinedValue : 0)
        + ",\"predefined_meaning\":\"" + (predefined ? Meaning(policy.id, value.u32PredefinedValue) : "unknown") + '"';
}
std::string NameFields(const Policy& policy, bool queried, NvAPI_Status status, const NvAPI_UnicodeString& name) {
    const std::string text = queried && status == NVAPI_OK ? Text(name) : "null";
    return "\"setting_symbol\":\"" + std::string(policy.name) + "\",\"setting_id\":" + std::to_string(policy.id)
        + ",\"query_performed\":" + Bool(queried) + ",\"status\":" + (queried ? std::to_string(status) : "null")
        + ",\"name_known\":" + Bool(text != "null" && text != "\"\"") + ",\"reported_name\":" + text;
}
struct Api {
    HMODULE module = nullptr;
    using Query = void* (__cdecl*)(NvU32);
    Query query = nullptr;
    NvDRSSessionHandle session = nullptr;
    bool initialized = false, diagnoseSettings = false;
    decltype(&NvAPI_Initialize) initialize = nullptr;
    decltype(&NvAPI_Unload) unload = nullptr;
    decltype(&NvAPI_DRS_CreateSession) create = nullptr;
    decltype(&NvAPI_DRS_DestroySession) destroy = nullptr;
    decltype(&NvAPI_DRS_LoadSettings) load = nullptr;
    decltype(&NvAPI_DRS_GetBaseProfile) base = nullptr;
    decltype(&NvAPI_DRS_GetCurrentGlobalProfile) global = nullptr;
    decltype(&NvAPI_DRS_GetProfileInfo) info = nullptr;
    decltype(&NvAPI_DRS_FindApplicationByName) find = nullptr;
    decltype(&NvAPI_DRS_GetSetting) get = nullptr;
    decltype(&NvAPI_DRS_GetSettingNameFromId) nameOf = nullptr;
    ~Api() { Close(); if (module) FreeLibrary(module); }
    bool Close() {
        bool okay = true;
        if (session && destroy) { const auto s = destroy(session); Status("NvAPI_DRS_DestroySession", s); okay = s == NVAPI_OK; session = nullptr; }
        if (initialized && unload) { const auto s = unload(); Status("NvAPI_Unload", s); okay = okay && s == NVAPI_OK; initialized = false; }
        return okay;
    }
    template<class T> void Resolve(T& target, NvU32 id, const char* name, bool optional = false) {
        const auto address = query(id); static_assert(sizeof(address) == sizeof(target)); std::memcpy(&target, &address, sizeof(target));
        if (!target) { Emit(std::string("\"event\":\"api_unavailable\",\"api\":\"") + name + "\",\"query_id\":" + std::to_string(id)); if (!optional) throw std::runtime_error("public_api_unavailable"); }
    }
    void Open(bool diagnose) {
        diagnoseSettings = diagnose;
        std::array<wchar_t, 32768> system{};
        const UINT count = GetSystemDirectoryW(system.data(), static_cast<UINT>(system.size()));
        if (!count || count >= system.size()) throw std::runtime_error("system_directory_unavailable");
        const std::wstring path = std::wstring(system.data()) + L"\\nvapi64.dll";
        module = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!module) throw std::runtime_error("system_nvapi_load_failed");
        const auto symbol = GetProcAddress(module, "nvapi_QueryInterface"); static_assert(sizeof(symbol) == sizeof(query)); std::memcpy(&query, &symbol, sizeof(query));
        if (!query) throw std::runtime_error("nvapi_query_interface_missing");
        Emit("\"event\":\"library\",\"requested_system_path\":" + Quote(path.c_str()) + ",\"system32_search_only\":true");
        // IDs verified against this pin's nvapi_interface.h: ten normal, one opt-in.
        Resolve(initialize,0x0150e828,"NvAPI_Initialize"); Resolve(unload,0xd22bdd7e,"NvAPI_Unload");
        Resolve(create,0x0694d52e,"NvAPI_DRS_CreateSession"); Resolve(destroy,0xdad9cff8,"NvAPI_DRS_DestroySession");
        Resolve(load,0x375dbd6b,"NvAPI_DRS_LoadSettings"); Resolve(base,0xda8466a0,"NvAPI_DRS_GetBaseProfile");
        Resolve(global,0x617bff9f,"NvAPI_DRS_GetCurrentGlobalProfile"); Resolve(info,0x61cd6fd6,"NvAPI_DRS_GetProfileInfo");
        Resolve(find,0xeee566b2,"NvAPI_DRS_FindApplicationByName"); Resolve(get,0x73bf8338,"NvAPI_DRS_GetSetting");
        if (diagnoseSettings) Resolve(nameOf,0xd61cbe6e,"NvAPI_DRS_GetSettingNameFromId",true);
        auto status = initialize(); Status("NvAPI_Initialize", status); initialized = status == NVAPI_OK;
        if (!initialized) throw std::runtime_error("nvapi_initialize_failed");
        NvDRSSessionHandle created = nullptr;
        status = create(&created); Status("NvAPI_DRS_CreateSession", status);
        if (status != NVAPI_OK || !created) throw std::runtime_error("drs_session_unavailable");
        session = created; // Failed calls never authorize use of their out value.
        status = load(session); Status("NvAPI_DRS_LoadSettings", status);
        if (status != NVAPI_OK) throw std::runtime_error("drs_load_failed");
    }
    void ReadName(const Policy& policy) {
        NvAPI_UnicodeString name{};
        const auto status = nameOf ? nameOf(policy.id, &name) : NVAPI_ERROR;
        Emit("\"event\":\"setting_name\"," + NameFields(policy, nameOf != nullptr, status, name));
    }
    void ReadNames() {
        if (!diagnoseSettings) return;
        for (const auto& policy : kPolicies) ReadName(policy);
        ReadName(kSanityControl);
    }
    void ReadSetting(const char* scope, NvDRSProfileHandle profile, const Policy& policy) {
        NVDRS_SETTING value{}; value.version = NVDRS_SETTING_VER;
        const auto status = get(session, profile, policy.id, &value);
        Emit("\"event\":\"setting\",\"scope\":\"" + std::string(scope) + "\"," + SettingFields(policy, status, value));
    }
    void ReadProfile(const char* scope, NvAPI_Status status, NvDRSProfileHandle profile) {
        Emit("\"event\":\"profile_lookup\",\"scope\":\"" + std::string(scope) + "\",\"status\":" + std::to_string(status)
            + ",\"profile_returned\":" + Bool(status == NVAPI_OK && profile) + ",\"settings_state\":\""
            + (status == NVAPI_OK && profile ? "query_follows" : "unknown") + '"');
        if (status != NVAPI_OK || !profile) return;
        NVDRS_PROFILE description{}; description.version = NVDRS_PROFILE_VER;
        const auto result = info(session, profile, &description);
        Emit("\"event\":\"profile\",\"scope\":\"" + std::string(scope) + "\",\"status\":" + std::to_string(result)
            + ",\"profile_name\":" + (result == NVAPI_OK ? Text(description.profileName) : "null")
            + ",\"is_predefined\":" + Number(result == NVAPI_OK, description.isPredefined));
        for (const auto& policy : kPolicies) ReadSetting(scope, profile, policy);
        if (diagnoseSettings) ReadSetting(scope, profile, kSanityControl);
    }
};
DWORD WINAPI Timeout(void* event) { if (WaitForSingleObject(event, 15000) == WAIT_TIMEOUT) ExitProcess(124); return 0; }
struct Watchdog {
    HANDLE done = CreateEventW(nullptr, TRUE, FALSE, nullptr), thread = nullptr;
    Watchdog() { if (done) thread = CreateThread(nullptr, 0, Timeout, done, 0, nullptr); if (!done || !thread) throw std::runtime_error("watchdog_creation_failed"); }
    ~Watchdog() { SetEvent(done); if (WaitForSingleObject(thread, 1000) != WAIT_OBJECT_0) ExitProcess(124); CloseHandle(thread); CloseHandle(done); }
};
int SelfTest() {
    unsigned int checks = 0, failures = 0;
    auto test = [&](bool pass) { ++checks; if (!pass) ++failures; };
    for (const auto& path : {L"C:\\Example\\test.exe", L"C:/Example Folder/Test.EXE", L"\\\\server\\share\\test.exe"}) test(ValidPath(path));
    for (const auto& path : {L"test.exe", L"C:test.exe", L"C:\\test.dll", L"\\\\.\\test.exe", L"\\\\server\\test.exe", L"C:\\a\"b.exe"}) test(!ValidPath(path));
    test(!ValidPath(L"C:\\" + std::wstring(NVAPI_UNICODE_STRING_MAX, L'a') + L".exe"));
    test(!std::strcmp(Meaning(VRR_MODE_ID, 1), "fullscreen_only")); test(!std::strcmp(Meaning(VRRREQUESTSTATE_ID, 2), "fullscreen_and_windowed"));
    test(!std::strcmp(Meaning(VRR_APP_OVERRIDE_ID, 4), "fixed_refresh")); test(!std::strcmp(Meaning(VRR_MODE_ID, 999), "unknown"));
    test(VSYNCMODE_ID != VRR_APP_OVERRIDE_ID && VSYNCMODE_ID == 0x00A879CF);
    test(!std::strcmp(Meaning(VSYNCMODE_ID, VSYNCMODE_FORCEON), "force_on"));
    test(!std::strcmp(Meaning(VSYNCMODE_ID, 0), "unknown"));
    const wchar_t* plain[] = {L"probe",L"--exe",L"C:\\Example\\test.exe"};
    const wchar_t* extra[] = {L"probe",L"--exe",L"C:\\Example\\test.exe",L"--diagnose-settings"};
    const wchar_t* bad[] = {L"probe",L"--exe",L"relative.exe",L"--diagnose-settings"};
    const wchar_t* parsed = nullptr; bool diagnose = false;
    test(ParseLive(3, plain, parsed, diagnose) && !diagnose);
    test(ParseLive(4, extra, parsed, diagnose) && diagnose);
    test(!ParseLive(4, bad, parsed, diagnose));
    NvAPI_UnicodeString name{}; const wchar_t fake[] = L"Synthetic Vertical Sync"; std::memcpy(name, fake, sizeof(fake));
    auto nameFields = NameFields(kSanityControl, true, NVAPI_SETTING_NOT_FOUND, name);
    test(nameFields.find("\"reported_name\":null") != std::string::npos);
    Emit("\"event\":\"synthetic_setting_name\",\"case\":\"missing\"," + nameFields);
    nameFields = NameFields(kSanityControl, false, NVAPI_OK, name);
    test(nameFields.find("\"status\":null") != std::string::npos && nameFields.find("\"name_known\":false") != std::string::npos);
    Emit("\"event\":\"synthetic_setting_name\",\"case\":\"api_unavailable\"," + nameFields);
    nameFields = NameFields(kSanityControl, true, NVAPI_OK, name);
    test(nameFields.find("\"reported_name\":\"Synthetic Vertical Sync\"") != std::string::npos);
    Emit("\"event\":\"synthetic_setting_name\",\"case\":\"known\"," + nameFields);
    NVDRS_SETTING value{}; value.settingType = NVDRS_DWORD_TYPE; value.u32CurrentValue = 2; value.u32PredefinedValue = 1;
    auto fields = SettingFields(kPolicies[0], NVAPI_SETTING_NOT_FOUND, value);
    test(fields.find("\"current_value\":null") != std::string::npos && fields.find("\"setting_location\":null") != std::string::npos);
    Emit("\"event\":\"synthetic_setting\",\"case\":\"missing\"," + fields);
    fields = SettingFields(kPolicies[0], NVAPI_OK, value);
    test(fields.find("\"current_value\":2") != std::string::npos && fields.find("\"predefined_value\":null") != std::string::npos);
    Emit("\"event\":\"synthetic_setting\",\"case\":\"predefined_invalid\"," + fields);
    value.isPredefinedValid = 1; fields = SettingFields(kPolicies[0], NVAPI_OK, value);
    test(fields.find("\"predefined_value\":1") != std::string::npos); Emit("\"event\":\"synthetic_setting\",\"case\":\"predefined_valid\"," + fields);
    value.settingType = NVDRS_BINARY_TYPE; test(SettingFields(kPolicies[0], NVAPI_OK, value).find("\"current_value\":null") != std::string::npos);
    test(Quote(L"a\"b\\c\n") == "\"a\\\"b\\\\c\\u000a\"");
    Emit("\"event\":\"self_test_complete\",\"checks\":" + std::to_string(checks) + ",\"failures\":" + std::to_string(failures)
        + ",\"driver_loaded\":false,\"driver_queries\":0,\"created_windows\":false");
    return failures ? 2 : 0;
}
} // namespace
int wmain(int argc, wchar_t** argv) {
    if (argc == 2 && !std::wcscmp(argv[1], L"--self-test")) return SelfTest();
    const wchar_t* exePath = nullptr; bool diagnose = false;
    if (!ParseLive(argc, argv, exePath, diagnose)) {
        Emit("\"event\":\"error\",\"reason\":\"usage_exe_absolute_path_optional_diagnose_settings_or_self_test\",\"driver_loaded\":false"); return 2;
    }
    try {
        Watchdog watchdog;
        Emit("\"event\":\"config\",\"sdk_commit\":\"" + std::string(kPin) + "\",\"exe_path\":" + Quote(exePath)
            + ",\"read_only\":true,\"driver_policy_only\":true,\"live_vrr_measured\":false,\"timeout_ms\":15000,\"diagnose_settings\":" + Bool(diagnose));
        Api api; api.Open(diagnose); api.ReadNames();
        NvDRSProfileHandle profile = nullptr; auto status = api.base(api.session, &profile); api.ReadProfile("base", status, profile);
        profile = nullptr; status = api.global(api.session, &profile); api.ReadProfile("global", status, profile);
        NvAPI_UnicodeString path{}; std::memcpy(path, exePath, (std::wcslen(exePath) + 1) * sizeof(wchar_t));
        NVDRS_APPLICATION application{}; application.version = NVDRS_APPLICATION_VER;
        profile = nullptr; status = api.find(api.session, path, &profile, &application);
        Emit("\"event\":\"application\",\"status\":" + std::to_string(status) + ",\"name\":" + (status == NVAPI_OK ? Text(application.appName) : "null")
            + ",\"is_predefined\":" + Number(status == NVAPI_OK, application.isPredefined) + ",\"lookup_was_fully_qualified\":true");
        api.ReadProfile("application", status, profile);
        const bool cleanup = api.Close(); Emit(std::string("\"event\":\"complete\",\"cleanup_success\":") + Bool(cleanup) + ",\"unknown_values_not_substituted\":true");
        return cleanup ? 0 : 2;
    } catch (const std::exception& error) {
        Emit("\"event\":\"error\",\"reason\":\"" + std::string(error.what()) + "\",\"win32_last_error\":" + std::to_string(GetLastError())); return 2;
    }
}
