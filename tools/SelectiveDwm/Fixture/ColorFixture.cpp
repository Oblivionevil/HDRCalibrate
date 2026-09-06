// Own-process DX11 color/presentation fixture. It never opens another process,
// captures the desktop, installs hooks, or changes display/profile settings.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <d3d11_1.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <DirectXPackedVector.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <exception>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
using DirectX::PackedVector::XMConvertHalfToFloat;
static_assert(sizeof(void*) == 8, "Build the fixture for x64.");

namespace {
constexpr wchar_t kWindowClass[] = L"HDRCalibrate.SelectiveDwm.ColorFixture.v1";
constexpr UINT kColumns = 8, kRows = 3, kPatchCount = kColumns * kRows;
constexpr DWORD kMaximumDurationMs = 600000;
LONGLONG gFrequency = 0;

LONGLONG Qpc() noexcept {
    LARGE_INTEGER value{};
    QueryPerformanceCounter(&value);
    return value.QuadPart;
}

LONGLONG Ticks(DWORD milliseconds) noexcept {
    return static_cast<LONGLONG>(milliseconds) * gFrequency / 1000;
}

const char* JsonBool(bool value) noexcept { return value ? "true" : "false"; }

void Log(const char* event, const char* format = nullptr, ...) {
    std::fprintf(stdout, "{\"event\":\"%s\",\"qpc_ticks\":%lld", event, Qpc());
    if (format != nullptr) {
        va_list arguments;
        va_start(arguments, format);
        std::vfprintf(stdout, format, arguments);
        va_end(arguments);
    }
    std::fputs("}\n", stdout);
    std::fflush(stdout);
}

std::string JsonString(const wchar_t* input) {
    const int size = WideCharToMultiByte(CP_UTF8, 0, input, -1, nullptr, 0, nullptr, nullptr);
    if (size <= 0) return "\"conversion_error\"";
    std::vector<char> utf8(static_cast<size_t>(size));
    WideCharToMultiByte(CP_UTF8, 0, input, -1, utf8.data(), size, nullptr, nullptr);
    std::string result = "\"";
    for (const char* cursor = utf8.data(); *cursor; ++cursor) {
        const unsigned char value = static_cast<unsigned char>(*cursor);
        if (value == '\\' || value == '"') { result += '\\'; result += *cursor; }
        else if (value < 0x20) {
            char escaped[7]{};
            std::snprintf(escaped, sizeof(escaped), "\\u%04x", static_cast<unsigned int>(value));
            result += escaped;
        } else result += *cursor;
    }
    return result + '"';
}

void Require(HRESULT result, const char* operation) {
    if (FAILED(result)) {
        Log("error", ",\"operation\":\"%s\",\"hresult\":%ld,\"hresult_hex\":\"0x%08lx\"",
            operation, result, static_cast<unsigned long>(result));
        throw std::runtime_error(operation);
    }
}

class Handle final {
public:
    explicit Handle(HANDLE value = nullptr) noexcept : value_(value) {}
    ~Handle() { if (value_) CloseHandle(value_); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    HANDLE Get() const noexcept { return value_; }
    void Reset(HANDLE value) noexcept { if (value_) CloseHandle(value_); value_ = value; }
private:
    HANDLE value_;
};

// The deadline also covers a stalled driver/Present or blocked output pipe.
// ExitProcess only terminates this fixture; no foreign process handle exists.
class Watchdog final {
public:
    Watchdog() : deadline_(Qpc() + Ticks(15000)), done_(CreateEventW(nullptr, TRUE, FALSE, nullptr)) {
        if (done_.Get()) thread_.Reset(CreateThread(nullptr, 0, Run, this, 0, nullptr));
        if (!done_.Get() || !thread_.Get()) throw std::runtime_error("watchdog_creation");
    }
    ~Watchdog() {
        SetEvent(done_.Get());
        if (WaitForSingleObject(thread_.Get(), 1000) != WAIT_OBJECT_0) ExitProcess(124);
    }
    void Arm(LONGLONG deadline) noexcept { deadline_.store(deadline, std::memory_order_release); }
private:
    static DWORD WINAPI Run(void* pointer) noexcept {
        auto* self = static_cast<Watchdog*>(pointer);
        while (WaitForSingleObject(self->done_.Get(), 50) == WAIT_TIMEOUT) {
            if (Qpc() >= self->deadline_.load(std::memory_order_acquire)) ExitProcess(124);
        }
        return 0;
    }
    std::atomic<LONGLONG> deadline_;
    Handle done_, thread_;
};

enum class Mode { Sdr, ScRgb, Pq };
const char* ModeName(Mode mode) noexcept {
    return mode == Mode::Sdr ? "sdr" : mode == Mode::ScRgb ? "scrgb" : "pq";
}
DXGI_FORMAT Format(Mode mode) noexcept {
    return mode == Mode::Sdr ? DXGI_FORMAT_R8G8B8A8_UNORM
        : mode == Mode::ScRgb ? DXGI_FORMAT_R16G16B16A16_FLOAT : DXGI_FORMAT_R10G10B10A2_UNORM;
}
const char* FormatName(Mode mode) noexcept {
    return mode == Mode::Sdr ? "R8G8B8A8_UNORM"
        : mode == Mode::ScRgb ? "R16G16B16A16_FLOAT" : "R10G10B10A2_UNORM";
}
DXGI_COLOR_SPACE_TYPE ColorSpace(Mode mode) noexcept {
    return mode == Mode::Sdr ? DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709
        : mode == Mode::ScRgb ? DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709 : DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
}
const char* ColorSpaceName(DXGI_COLOR_SPACE_TYPE colorSpace) noexcept {
    switch (colorSpace) {
    case DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709: return "RGB_FULL_G22_NONE_P709";
    case DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709: return "RGB_FULL_G10_NONE_P709";
    case DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020: return "RGB_FULL_G2084_NONE_P2020";
    default: return "other";
    }
}

struct Options {
    Mode mode = Mode::Sdr;
    DWORD durationMs = 10000;
    UINT width = 960, height = 540;
    int x = 64, y = 64;
    double fps = 60.0, whiteNits = 80.0;
    bool noActivate = false, hidden = false, borderless = false, warp = false;
    bool animateMarker = false;
    bool selfTest = false, gpuSelfTest = false, help = false;
};

bool Integer(const wchar_t* text, long long minimum, long long maximum, long long& value) noexcept {
    if (!text || !*text) return false;
    const wchar_t* cursor = text;
    bool negative = false;
    if (*cursor == L'-') { negative = true; ++cursor; }
    if (!*cursor) return false;
    unsigned long long magnitude = 0;
    for (; *cursor; ++cursor) {
        if (*cursor < L'0' || *cursor > L'9') return false;
        const unsigned int digit = static_cast<unsigned int>(*cursor - L'0');
        if (magnitude > (static_cast<unsigned long long>(LLONG_MAX) - digit) / 10) return false;
        magnitude = magnitude * 10 + digit;
    }
    value = negative ? -static_cast<long long>(magnitude) : static_cast<long long>(magnitude);
    return value >= minimum && value <= maximum;
}

bool Decimal(const wchar_t* text, double minimum, double maximum, double& value) noexcept {
    // Deliberately reject locale separators, hexadecimal, nan, inf and exponents.
    bool digit = false, dot = false;
    for (const wchar_t* cursor = text; cursor && *cursor; ++cursor) {
        if (*cursor >= L'0' && *cursor <= L'9') digit = true;
        else if (*cursor == L'.' && !dot) dot = true;
        else return false;
    }
    if (!digit) return false;
    wchar_t* end = nullptr;
    value = std::wcstod(text, &end);
    return end && !*end && std::isfinite(value) && value >= minimum && value <= maximum;
}

bool Parse(int argc, const wchar_t* const* argv, Options& options, const char*& reason) {
    std::vector<std::wstring> seen;
    for (int index = 1; index < argc; ++index) {
        const std::wstring argument = argv[index];
        if (std::find(seen.begin(), seen.end(), argument) != seen.end()) { reason = "duplicate_argument"; return false; }
        seen.push_back(argument);
        if (argument == L"--no-activate") options.noActivate = true;
        else if (argument == L"--hidden") options.hidden = true;
        else if (argument == L"--borderless") options.borderless = true;
        else if (argument == L"--animate-marker") options.animateMarker = true;
        else if (argument == L"--warp") options.warp = true;
        else if (argument == L"--self-test") options.selfTest = true;
        else if (argument == L"--gpu-self-test") options.gpuSelfTest = true;
        else if (argument == L"--help") options.help = true;
        else {
            if (argument != L"--mode" && argument != L"--duration-ms" && argument != L"--fps"
                && argument != L"--width" && argument != L"--height" && argument != L"--x"
                && argument != L"--y" && argument != L"--reference-white-nits") { reason = "unknown_argument"; return false; }
            if (++index >= argc) { reason = "missing_argument_value"; return false; }
            const wchar_t* value = argv[index];
            long long number = 0;
            if (argument == L"--mode") {
                if (!std::wcscmp(value, L"sdr")) options.mode = Mode::Sdr;
                else if (!std::wcscmp(value, L"scrgb")) options.mode = Mode::ScRgb;
                else if (!std::wcscmp(value, L"pq")) options.mode = Mode::Pq;
                else { reason = "invalid_mode"; return false; }
            } else if (argument == L"--duration-ms") {
                if (!Integer(value, 1, kMaximumDurationMs, number)) { reason = "invalid_duration"; return false; }
                options.durationMs = static_cast<DWORD>(number);
            } else if (argument == L"--width" || argument == L"--height") {
                if (!Integer(value, 64, 4096, number)) { reason = "invalid_dimension"; return false; }
                (argument == L"--width" ? options.width : options.height) = static_cast<UINT>(number);
            } else if (argument == L"--x" || argument == L"--y") {
                if (!Integer(value, -32768, 32767, number)) { reason = "invalid_position"; return false; }
                (argument == L"--x" ? options.x : options.y) = static_cast<int>(number);
            } else if (argument == L"--fps") {
                if (!Decimal(value, 1, 1000, options.fps)) { reason = "invalid_fps"; return false; }
            } else if (!Decimal(value, 1, 1000, options.whiteNits)) { reason = "invalid_reference_white"; return false; }
        }
    }
    if (options.selfTest && (argc != 2 || options.gpuSelfTest)) { reason = "self_test_is_exclusive"; return false; }
    if (options.help && argc != 2) { reason = "help_is_exclusive"; return false; }
    if (options.gpuSelfTest && seen.size() != (options.warp ? 2U : 1U)) { reason = "gpu_self_test_only_allows_warp"; return false; }
    return true;
}

void Usage() {
    std::fputs(
        "ColorFixture --mode sdr|scrgb|pq [--duration-ms 1..600000] [--fps 1..1000]\n"
        "  [--width 64..4096] [--height 64..4096] [--x -32768..32767] [--y -32768..32767]\n"
        "  [--reference-white-nits 1..1000] [--no-activate] [--borderless] [--hidden] [--warp] [--animate-marker]\n"
        "ColorFixture --self-test\nColorFixture --gpu-self-test [--warp]\n"
        "Default: SDR, 10000 ms, 60 FPS, 960x540 client pixels, (64,64), 80-nit reference.\n"
        "JSONL stdout: identity, Windows output/HDR state, patch metadata, frame counters.\n"
        "GPU self-test reads only generated offscreen textures in this process.\n"
        "No desktop capture, foreign-process access, hooks, input injection, display or profile changes.\n"
        "QPC/Present timing is CPU submission timing, not end-to-end latency or proof of VRR.\n"
        "Exit 0=completed/passed, 2=invalid/error, 3=requested color space unsupported, 124=own watchdog.\n", stderr);
}

double SrgbDecode(double code) noexcept {
    return code <= 0.04045 ? code / 12.92 : std::pow((code + 0.055) / 1.055, 2.4);
}

double PqEncode(double nits) noexcept {
    constexpr double m1 = 2610.0 / 16384.0, m2 = 2523.0 / 32.0;
    constexpr double c1 = 3424.0 / 4096.0, c2 = 2413.0 / 128.0, c3 = 2392.0 / 128.0;
    const double power = std::pow(std::clamp(nits / 10000.0, 0.0, 1.0), m1);
    return std::pow((c1 + c2 * power) / (1.0 + c3 * power), m2);
}

double PqDecode(double code) noexcept {
    constexpr double m1 = 2610.0 / 16384.0, m2 = 2523.0 / 32.0;
    constexpr double c1 = 3424.0 / 4096.0, c2 = 2413.0 / 128.0, c3 = 2392.0 / 128.0;
    const double power = std::pow(std::clamp(code, 0.0, 1.0), 1.0 / m2);
    return 10000.0 * std::pow(std::max(power - c1, 0.0) / (c2 - c3 * power), 1.0 / m1);
}

using Rgb = std::array<double, 3>;
Rgb Rec709To2020(const Rgb& rgb) noexcept {
    return {0.627403895934699 * rgb[0] + 0.329283038377883 * rgb[1] + 0.043313065687418 * rgb[2],
        0.069097289358232 * rgb[0] + 0.919540395075459 * rgb[1] + 0.011362315566309 * rgb[2],
        0.016391438875150 * rgb[0] + 0.088013307877226 * rgb[1] + 0.895595253247624 * rgb[2]};
}

struct Patch {
    const char* kind = "sdr_reference";
    Rgb code{}; // Encoded source sRGB, not shader-linear values.
    Rgb linearNits{}; // Rec.709 RGB channels in nominal nits before gamut conversion.
    std::array<float, 4> write{};
};

std::array<Patch, kPatchCount> Patches(Mode mode, double whiteNits) {
    constexpr std::array<UINT, 8> gray{0, 16, 32, 64, 128, 192, 235, 255};
    constexpr std::array<UINT, 8> dark{1, 2, 4, 8, 12, 24, 48, 96};
    constexpr std::array<Rgb, 8> colors{{{1,0,0}, {0,1,0}, {0,0,1}, {1,1,0},
        {0,1,1}, {1,0,1}, {0.5,0.5,0.5}, {0.75,0.5,0.25}}};
    constexpr std::array<double, 8> hdr{0, 80, 160, 320, 500, 600, 800, 1000};
    std::array<Patch, kPatchCount> patches{};
    for (UINT index = 0; index < kPatchCount; ++index) {
        Patch& patch = patches[index];
        const UINT column = index % kColumns, row = index / kColumns;
        if (row == 1) patch.code = colors[column];
        else {
            const double code = (row == 0 ? gray[column] : dark[column]) / 255.0;
            patch.code = {code, code, code};
        }
        for (size_t channel = 0; channel < 3; ++channel) patch.linearNits[channel] = SrgbDecode(patch.code[channel]) * whiteNits;
        if (row == 2 && mode != Mode::Sdr) {
            patch.kind = "absolute_hdr_neutral";
            patch.code = {0, 0, 0}; // No corresponding bounded SDR code.
            patch.linearNits = {hdr[column], hdr[column], hdr[column]};
        }
        const Rgb rgb2020 = Rec709To2020(patch.linearNits);
        for (size_t channel = 0; channel < 3; ++channel) {
            patch.write[channel] = static_cast<float>(mode == Mode::Sdr ? patch.code[channel]
                : mode == Mode::ScRgb ? patch.linearNits[channel] / 80.0 : PqEncode(rgb2020[channel]));
        }
        patch.write[3] = 1.0f;
    }
    return patches;
}

D3D11_RECT PatchRect(UINT index, UINT width, UINT height) noexcept {
    const UINT column = index % kColumns, row = index / kColumns;
    return {static_cast<LONG>(column * width / kColumns), static_cast<LONG>(row * height / kRows),
        static_cast<LONG>((column + 1) * width / kColumns), static_cast<LONG>((row + 1) * height / kRows)};
}

struct Marker {
    D3D11_RECT rect{};
    UINT phase = 0;
    Rgb sourceCode{};
    std::array<float, 4> write{};
};

Marker MovingMarker(unsigned long long frameIndex, UINT width, const std::array<Patch, kPatchCount>& patches) noexcept {
    // Source heights are at least 24 (GPU self-test) or 64 (window). The first
    // patch row's centre is therefore at least y=4, outside this y=[0,2) strip.
    constexpr LONG markerWidth = 8, markerHeight = 2;
    const UINT travel = width - static_cast<UINT>(markerWidth) + 1;
    const LONG left = static_cast<LONG>(((frameIndex % travel) * 7) % travel);
    Marker marker{};
    marker.rect = {left, 0, left + markerWidth, markerHeight};
    marker.phase = static_cast<UINT>(frameIndex % 2);
    // Magenta then cyan, using the same SDR/scRGB/PQ encoding as row 1.
    const auto& source = patches[marker.phase == 0 ? 13 : 12];
    marker.sourceCode = source.code;
    marker.write = source.write;
    return marker;
}

bool InsideMarker(const Marker& marker, LONG x, LONG y) noexcept {
    return x >= marker.rect.left && x < marker.rect.right && y >= marker.rect.top && y < marker.rect.bottom;
}

void LogPatches(const Options& options) {
    Log("pattern", ",\"name\":\"constant_patches_v1\",\"mode\":\"%s\",\"columns\":%u,\"rows\":%u,"
        "\"reference_white_nits\":%.9g,\"pixel_values_are_source_only\":true,\"actual_display_luminance_measured\":false,"
        "\"animate_marker\":%s,\"marker_exclusion_top_rows\":%u,\"all_patch_centres_excluded_from_marker\":true",
        ModeName(options.mode), kColumns, kRows, options.whiteNits, JsonBool(options.animateMarker), options.animateMarker ? 2U : 0U);
    const auto patches = Patches(options.mode, options.whiteNits);
    for (UINT index = 0; index < kPatchCount; ++index) {
        const auto& patch = patches[index];
        const auto rect = PatchRect(index, options.width, options.height);
        Log("patch", ",\"index\":%u,\"kind\":\"%s\",\"row\":%u,\"column\":%u,\"rect\":[%ld,%ld,%ld,%ld],"
            "\"source_srgb_code\":[%.12g,%.12g,%.12g],\"linear_rec709_nits\":[%.12g,%.12g,%.12g],"
            "\"written_rgba\":[%.12g,%.12g,%.12g,%.12g]",
            index, patch.kind, index / kColumns, index % kColumns, rect.left, rect.top, rect.right, rect.bottom,
            patch.code[0], patch.code[1], patch.code[2], patch.linearNits[0], patch.linearNits[1], patch.linearNits[2],
            static_cast<double>(patch.write[0]), static_cast<double>(patch.write[1]),
            static_cast<double>(patch.write[2]), static_cast<double>(patch.write[3]));
    }
    for (UINT code : {0U, 1U, 16U, 32U, 64U, 128U, 192U, 235U, 255U}) {
        const double value = code / 255.0;
        Log("gamma_reference", ",\"source_u8\":%u,\"source_code\":%.12g,\"srgb_decode\":%.12g,"
            "\"power_2_0\":%.12g,\"power_2_2\":%.12g,\"power_2_4\":%.12g,\"mathematical_only\":true",
            code, value, SrgbDecode(value), std::pow(value, 2.0), std::pow(value, 2.2), std::pow(value, 2.4));
    }
}

struct Device {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext1> context;
    ComPtr<IDXGIAdapter1> adapter;
    ComPtr<IDXGIFactory2> factory;
    bool tearing = false;

    explicit Device(bool warp) {
        constexpr D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        D3D_FEATURE_LEVEL selected{};
        ComPtr<ID3D11DeviceContext> baseContext;
        Require(D3D11CreateDevice(nullptr, warp ? D3D_DRIVER_TYPE_WARP : D3D_DRIVER_TYPE_HARDWARE,
            nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, static_cast<UINT>(std::size(levels)),
            D3D11_SDK_VERSION, &device, &selected, &baseContext), "D3D11CreateDevice");
        Require(baseContext.As(&context), "QueryInterface_ID3D11DeviceContext1");
        ComPtr<IDXGIDevice> dxgiDevice;
        Require(device.As(&dxgiDevice), "QueryInterface_IDXGIDevice");
        ComPtr<IDXGIAdapter> baseAdapter;
        Require(dxgiDevice->GetAdapter(&baseAdapter), "GetAdapter");
        Require(baseAdapter.As(&adapter), "QueryInterface_IDXGIAdapter1");
        Require(adapter->GetParent(IID_PPV_ARGS(&factory)), "GetParent_IDXGIFactory2");
        DXGI_ADAPTER_DESC1 description{};
        Require(adapter->GetDesc1(&description), "Adapter_GetDesc1");
        ComPtr<IDXGIFactory5> factory5;
        HRESULT tearingResult = factory.As(&factory5);
        BOOL supported = FALSE;
        if (SUCCEEDED(tearingResult)) tearingResult = factory5->CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING, &supported, sizeof(supported));
        tearing = SUCCEEDED(tearingResult) && supported != FALSE;
        Log("device", ",\"adapter\":%s,\"vendor_id\":%u,\"device_id\":%u,\"adapter_luid_high\":%ld,"
            "\"adapter_luid_low\":%lu,\"feature_level\":%u,\"warp\":%s,\"tearing_supported\":%s,\"tearing_query_hresult\":%ld",
            JsonString(description.Description).c_str(), description.VendorId, description.DeviceId,
            description.AdapterLuid.HighPart, description.AdapterLuid.LowPart, static_cast<UINT>(selected),
            JsonBool(warp), JsonBool(tearing), tearingResult);
    }

    void Draw(ID3D11RenderTargetView* view, const std::array<Patch, kPatchCount>& patches, UINT width, UINT height) {
        for (UINT index = 0; index < kPatchCount; ++index) {
            const auto rectangle = PatchRect(index, width, height);
            context->ClearView(view, patches[index].write.data(), &rectangle, 1);
        }
    }

    void DrawMarker(ID3D11RenderTargetView* view, const Marker& marker) {
        context->ClearView(view, marker.write.data(), &marker.rect, 1);
    }
};

void LogAdvancedColor(const DXGI_OUTPUT_DESC1& output) {
    UINT pathCount = 0, modeCount = 0;
    LONG result = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount);
    std::vector<DISPLAYCONFIG_PATH_INFO> paths;
    std::vector<DISPLAYCONFIG_MODE_INFO> modes;
    for (int attempt = 0; result == ERROR_SUCCESS && attempt < 3; ++attempt) {
        paths.resize(pathCount); modes.resize(modeCount);
        result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pathCount, paths.data(), &modeCount, modes.data(), nullptr);
        if (result != ERROR_INSUFFICIENT_BUFFER) break;
        result = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount);
        if (attempt == 2 && result == ERROR_SUCCESS) result = ERROR_INSUFFICIENT_BUFFER;
    }
    if (result == ERROR_SUCCESS) {
        for (UINT index = 0; index < pathCount; ++index) {
            const auto& path = paths[index];
            DISPLAYCONFIG_SOURCE_DEVICE_NAME source{};
            source.header = {DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, sizeof(source), path.sourceInfo.adapterId, path.sourceInfo.id};
            if (DisplayConfigGetDeviceInfo(&source.header) != ERROR_SUCCESS || std::wcscmp(source.viewGdiDeviceName, output.DeviceName)) continue;
            DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO color{};
            color.header = {DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO, sizeof(color), path.targetInfo.adapterId, path.targetInfo.id};
            const LONG colorResult = DisplayConfigGetDeviceInfo(&color.header);
            DISPLAYCONFIG_SDR_WHITE_LEVEL white{};
            white.header = {DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL, sizeof(white), path.targetInfo.adapterId, path.targetInfo.id};
            const LONG whiteResult = DisplayConfigGetDeviceInfo(&white.header);
            Log("windows_advanced_color", ",\"device_name\":%s,\"query_result\":%ld,\"advanced_color_supported\":%s,"
                "\"advanced_color_enabled\":%s,\"wide_color_enforced\":%s,\"advanced_color_force_disabled\":%s,"
                "\"bits_per_color_channel\":%u,\"sdr_white_query_result\":%ld,\"sdr_white_level\":%lu,"
                "\"sdr_white_nits\":%.9g,\"hdr_active_by_output_colorspace\":%s,\"query_succeeded\":%s",
                JsonString(output.DeviceName).c_str(), colorResult, JsonBool(color.advancedColorSupported != 0),
                JsonBool(color.advancedColorEnabled != 0), JsonBool(color.wideColorEnforced != 0),
                JsonBool(color.advancedColorForceDisabled != 0), color.bitsPerColorChannel, whiteResult,
                white.SDRWhiteLevel, white.SDRWhiteLevel * 80.0 / 1000.0,
                JsonBool(output.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020), JsonBool(colorResult == ERROR_SUCCESS));
            return;
        }
    }
    Log("windows_advanced_color_unavailable", ",\"query_result\":%ld,\"device_name\":%s", result, JsonString(output.DeviceName).c_str());
}

void LogOutput(IDXGISwapChain3* swapChain) {
    ComPtr<IDXGIOutput> output;
    HRESULT result = swapChain->GetContainingOutput(&output);
    ComPtr<IDXGIOutput6> output6;
    if (SUCCEEDED(result)) result = output.As(&output6);
    DXGI_OUTPUT_DESC1 description{};
    if (SUCCEEDED(result)) result = output6->GetDesc1(&description);
    if (FAILED(result)) { Log("output_unavailable", ",\"get_desc1_hresult\":%ld", result); return; }
    Log("output", ",\"get_desc1_hresult\":%ld,\"device_name\":%s,\"color_space\":%u,\"color_space_name\":\"%s\","
        "\"bits_per_color\":%u,\"min_luminance_nits\":%.9g,\"max_luminance_nits\":%.9g,\"max_full_frame_luminance_nits\":%.9g,"
        "\"desktop_rect\":[%ld,%ld,%ld,%ld],\"hdr_active_by_output_colorspace\":%s,\"luminance_source\":\"DXGI_metadata_not_measurement\"",
        result, JsonString(description.DeviceName).c_str(), static_cast<UINT>(description.ColorSpace), ColorSpaceName(description.ColorSpace),
        description.BitsPerColor, static_cast<double>(description.MinLuminance), static_cast<double>(description.MaxLuminance),
        static_cast<double>(description.MaxFullFrameLuminance), description.DesktopCoordinates.left, description.DesktopCoordinates.top,
        description.DesktopCoordinates.right, description.DesktopCoordinates.bottom,
        JsonBool(description.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020));
    LogAdvancedColor(description);
}

struct WindowState {
    bool closed = false, noActivate = false, outputDirty = false;
};

LRESULT CALLBACK WindowProcedure(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
    auto* state = reinterpret_cast<WindowState*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        state = static_cast<WindowState*>(reinterpret_cast<CREATESTRUCTW*>(lParam)->lpCreateParams);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(state));
    }
    if (state) {
        if (message == WM_CLOSE) { state->closed = true; DestroyWindow(hwnd); return 0; }
        if (message == WM_DESTROY) { state->closed = true; PostQuitMessage(0); return 0; }
        if (message == WM_MOUSEACTIVATE && state->noActivate) return MA_NOACTIVATE;
        if (message == WM_ERASEBKGND) return 1;
        if (message == WM_MOVE || message == WM_DISPLAYCHANGE) state->outputDirty = true;
        if (message == WM_PAINT) { PAINTSTRUCT paint{}; BeginPaint(hwnd, &paint); EndPaint(hwnd, &paint); return 0; }
    }
    return DefWindowProcW(hwnd, message, wParam, lParam);
}

class Window final {
public:
    WindowState state;
    HWND hwnd = nullptr;
    explicit Window(const Options& options) {
        state.noActivate = options.noActivate;
        WNDCLASSW windowClass{};
        windowClass.lpfnWndProc = WindowProcedure;
        windowClass.hInstance = GetModuleHandleW(nullptr);
        windowClass.lpszClassName = kWindowClass;
        windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        if (!RegisterClassW(&windowClass)) throw std::runtime_error("RegisterClass");
        const DWORD style = options.borderless ? WS_POPUP : WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
        const DWORD extended = options.noActivate ? WS_EX_NOACTIVATE : 0;
        RECT rectangle{0, 0, static_cast<LONG>(options.width), static_cast<LONG>(options.height)};
        if (!AdjustWindowRectEx(&rectangle, style, FALSE, extended)) throw std::runtime_error("AdjustWindowRectEx");
        const std::wstring title = std::wstring(L"HDRCalibrate ColorFixture - ")
            + (options.mode == Mode::Sdr ? L"SDR" : options.mode == Mode::ScRgb ? L"scRGB HDR" : L"PQ HDR10");
        hwnd = CreateWindowExW(extended, kWindowClass, title.c_str(), style, options.x, options.y,
            rectangle.right - rectangle.left, rectangle.bottom - rectangle.top, nullptr, nullptr, windowClass.hInstance, &state);
        if (!hwnd) throw std::runtime_error("CreateWindowEx");
        LogIdentity("window_created", options);
    }
    ~Window() { if (hwnd && IsWindow(hwnd)) DestroyWindow(hwnd); UnregisterClassW(kWindowClass, GetModuleHandleW(nullptr)); }
    Window(const Window&) = delete;
    Window& operator=(const Window&) = delete;
    void LogIdentity(const char* event, const Options& options) const {
        RECT client{}; GetClientRect(hwnd, &client);
        POINT origin{}; ClientToScreen(hwnd, &origin);
        Log(event, ",\"pid\":%lu,\"hwnd\":%llu,\"hwnd_hex\":\"0x%llx\",\"mode\":\"%s\",\"hidden\":%s,\"no_activate\":%s,"
            "\"client_origin\":[%ld,%ld],\"client_width\":%ld,\"client_height\":%ld,\"visible\":%s,\"dpi\":%u",
            GetCurrentProcessId(), static_cast<unsigned long long>(reinterpret_cast<ULONG_PTR>(hwnd)),
            static_cast<unsigned long long>(reinterpret_cast<ULONG_PTR>(hwnd)), ModeName(options.mode),
            JsonBool(options.hidden), JsonBool(options.noActivate), origin.x, origin.y, client.right, client.bottom,
            JsonBool(IsWindowVisible(hwnd) != FALSE), GetDpiForWindow(hwnd));
    }
};

void Pump(WindowState& state) {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
        if (message.message == WM_QUIT) { state.closed = true; break; }
        TranslateMessage(&message); DispatchMessageW(&message);
    }
}

class FrameTimer final {
public:
    Handle handle;
    FrameTimer() {
        handle.Reset(CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_MODIFY_STATE | SYNCHRONIZE));
        const bool highResolution = handle.Get() != nullptr;
        const DWORD initialError = highResolution ? ERROR_SUCCESS : GetLastError();
        if (!highResolution) handle.Reset(CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_MODIFY_STATE | SYNCHRONIZE));
        if (!handle.Get()) throw std::runtime_error("CreateWaitableTimerEx_own_timer");
        Log("frame_timer", ",\"high_resolution\":%s,\"high_resolution_creation_error\":%lu,\"global_timer_resolution_changed\":false",
            JsonBool(highResolution), initialError);
    }
};

bool WaitUntil(LONGLONG target, WindowState& state, FrameTimer& timer) {
    while (!state.closed) {
        Pump(state);
        const LONGLONG remaining = target - Qpc();
        if (remaining <= 0) return !state.closed;
        LARGE_INTEGER due{};
        due.QuadPart = -std::max(1LL, static_cast<LONGLONG>(std::ceil(remaining * 10000000.0 / gFrequency)));
        if (!SetWaitableTimer(timer.handle.Get(), &due, 0, nullptr, nullptr, FALSE)) throw std::runtime_error("SetWaitableTimer_own_timer");
        HANDLE waitHandle = timer.handle.Get();
        const DWORD result = MsgWaitForMultipleObjectsEx(1, &waitHandle, 50, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        if (result == WAIT_FAILED) throw std::runtime_error("MsgWait_frame_schedule");
    }
    return false;
}

int Run(const Options& options, Watchdog& watchdog) {
    if (!SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) throw std::runtime_error("SetThreadDpiAwarenessContext_own_thread");
    Window window(options);
    Device device(options.warp);
    DXGI_SWAP_CHAIN_DESC1 description{};
    description.Width = options.width; description.Height = options.height;
    description.Format = Format(options.mode); description.SampleDesc.Count = 1;
    description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; description.BufferCount = 2;
    description.Scaling = DXGI_SCALING_STRETCH; description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    description.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    description.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT
        | (device.tearing ? DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0);
    ComPtr<IDXGISwapChain1> baseSwapChain;
    Require(device.factory->CreateSwapChainForHwnd(device.device.Get(), window.hwnd, &description,
        nullptr, nullptr, &baseSwapChain), "CreateSwapChainForHwnd");
    Require(device.factory->MakeWindowAssociation(window.hwnd, DXGI_MWA_NO_ALT_ENTER), "MakeWindowAssociation_own_window");
    ComPtr<IDXGISwapChain3> swapChain;
    Require(baseSwapChain.As(&swapChain), "QueryInterface_IDXGISwapChain3");
    Require(swapChain->SetMaximumFrameLatency(1), "SetMaximumFrameLatency");
    Handle latency(swapChain->GetFrameLatencyWaitableObject());
    if (!latency.Get()) throw std::runtime_error("GetFrameLatencyWaitableObject");
    UINT support = 0;
    const HRESULT supportResult = swapChain->CheckColorSpaceSupport(ColorSpace(options.mode), &support);
    Log("color_space_support", ",\"mode\":\"%s\",\"color_space\":%u,\"color_space_name\":\"%s\","
        "\"check_hresult\":%ld,\"support_flags\":%u,\"present_supported\":%s",
        ModeName(options.mode), static_cast<UINT>(ColorSpace(options.mode)), ColorSpaceName(ColorSpace(options.mode)),
        supportResult, support, JsonBool(SUCCEEDED(supportResult) && (support & DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT) != 0));
    LogOutput(swapChain.Get());
    if (FAILED(supportResult) || !(support & DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT)) {
        Log("unsupported", ",\"reason\":\"requested_color_space_cannot_present\",\"no_mode_fallback\":true"); return 3;
    }
    Require(swapChain->SetColorSpace1(ColorSpace(options.mode)), "SetColorSpace1");
    UINT latencyValue = 0; Require(swapChain->GetMaximumFrameLatency(&latencyValue), "GetMaximumFrameLatency");
    DXGI_SWAP_CHAIN_DESC1 actual{}; Require(swapChain->GetDesc1(&actual), "SwapChain_GetDesc1");
    Log("swap_chain", ",\"format\":%u,\"format_name\":\"%s\",\"color_space\":%u,\"color_space_name\":\"%s\","
        "\"width\":%u,\"height\":%u,\"buffer_count\":%u,\"swap_effect\":%u,\"flags\":%u,\"maximum_frame_latency\":%u,"
        "\"waitable_object\":true,\"sync_interval\":%u,\"present_flags\":%u,\"explicit_hdr_metadata\":false,\"exclusive_fullscreen\":false",
        static_cast<UINT>(actual.Format), FormatName(options.mode), static_cast<UINT>(ColorSpace(options.mode)), ColorSpaceName(ColorSpace(options.mode)),
        actual.Width, actual.Height, actual.BufferCount, static_cast<UINT>(actual.SwapEffect), actual.Flags, latencyValue,
        device.tearing ? 0U : 1U, device.tearing ? DXGI_PRESENT_ALLOW_TEARING : 0U);
    ComPtr<ID3D11Texture2D> buffer;
    Require(swapChain->GetBuffer(0, IID_PPV_ARGS(&buffer)), "GetBuffer");
    ComPtr<ID3D11RenderTargetView> view;
    Require(device.device->CreateRenderTargetView(buffer.Get(), nullptr, &view), "CreateRenderTargetView");
    const auto patches = Patches(options.mode, options.whiteNits);
    LogPatches(options);
    FrameTimer frameTimer;
    if (!options.hidden) ShowWindow(window.hwnd, options.noActivate ? SW_SHOWNOACTIVATE : SW_SHOW);
    window.LogIdentity("ready", options);
    const LONGLONG start = Qpc(), deadline = start + Ticks(options.durationMs);
    watchdog.Arm(deadline + Ticks(2000));
    const LONGLONG period = std::max(1LL, static_cast<LONGLONG>(gFrequency / options.fps));
    LONGLONG scheduled = start, priorPresent = 0;
    unsigned long long frames = 0, occluded = 0, waitsTimedOut = 0, missedSlots = 0;
    bool failed = false;
    while (!window.state.closed && Qpc() < deadline) {
        if (!WaitUntil(std::min(scheduled, deadline), window.state, frameTimer) || Qpc() >= deadline) break;
        if (window.state.outputDirty) { LogOutput(swapChain.Get()); window.state.outputDirty = false; }
        const LONGLONG waitBegin = Qpc();
        bool available = false;
        while (!window.state.closed && Qpc() < deadline) {
            HANDLE waitHandle = latency.Get();
            const DWORD result = MsgWaitForMultipleObjectsEx(1, &waitHandle, 20, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (result == WAIT_OBJECT_0) { available = true; break; }
            if (result == WAIT_OBJECT_0 + 1) Pump(window.state);
            else if (result == WAIT_TIMEOUT) ++waitsTimedOut;
            else throw std::runtime_error("wait_for_swap_chain");
        }
        if (!available || window.state.closed || Qpc() >= deadline) break;
        const LONGLONG renderBegin = Qpc();
        const UINT bufferIndex = swapChain->GetCurrentBackBufferIndex();
        device.Draw(view.Get(), patches, options.width, options.height);
        const Marker marker = options.animateMarker ? MovingMarker(frames, options.width, patches) : Marker{};
        if (options.animateMarker) device.DrawMarker(view.Get(), marker);
        const LONGLONG presentBegin = Qpc();
        const HRESULT presented = swapChain->Present(device.tearing ? 0 : 1, device.tearing ? DXGI_PRESENT_ALLOW_TEARING : 0);
        const LONGLONG presentEnd = Qpc();
        ++frames;
        if (presented == DXGI_STATUS_OCCLUDED) ++occluded;
        Log("frame", ",\"pid\":%lu,\"frame\":%llu,\"scheduled_qpc\":%lld,\"wait_begin_qpc\":%lld,\"render_begin_qpc\":%lld,"
            "\"present_begin_qpc\":%lld,\"present_end_qpc\":%lld,\"present_interval_ticks\":%lld,\"present_hresult\":%ld,"
            "\"back_buffer_index\":%u,\"occluded\":%s,\"cpu_submission_only\":true,\"animate_marker\":%s,"
            "\"marker_phase\":%u,\"marker_rect\":[%ld,%ld,%ld,%ld],\"marker_source_srgb_code\":[%.9g,%.9g,%.9g],"
            "\"marker_written_rgba\":[%.9g,%.9g,%.9g,%.9g]",
            GetCurrentProcessId(), frames, scheduled, waitBegin, renderBegin, presentBegin, presentEnd,
            priorPresent ? presentBegin - priorPresent : 0, presented, bufferIndex, JsonBool(presented == DXGI_STATUS_OCCLUDED),
            JsonBool(options.animateMarker), marker.phase, marker.rect.left, marker.rect.top, marker.rect.right, marker.rect.bottom,
            marker.sourceCode[0], marker.sourceCode[1], marker.sourceCode[2], static_cast<double>(marker.write[0]),
            static_cast<double>(marker.write[1]), static_cast<double>(marker.write[2]), static_cast<double>(marker.write[3]));
        priorPresent = presentBegin;
        if (FAILED(presented)) {
            Log("device_failure", ",\"present_hresult\":%ld,\"device_removed_reason\":%ld", presented, device.device->GetDeviceRemovedReason());
            failed = true; break;
        }
        scheduled += period;
        if (scheduled < presentEnd - period) {
            const auto skipped = static_cast<unsigned long long>((presentEnd - scheduled) / period);
            missedSlots += skipped; scheduled += static_cast<LONGLONG>(skipped) * period;
        }
    }
    if (IsWindow(window.hwnd)) ShowWindow(window.hwnd, SW_HIDE);
    const LONGLONG finished = Qpc();
    device.context->ClearState();
    Log("complete", ",\"pid\":%lu,\"frames\":%llu,\"occluded_presents\":%llu,\"wait_timeouts_20ms\":%llu,"
        "\"missed_schedule_slots\":%llu,\"start_qpc\":%lld,\"end_qpc\":%lld,\"elapsed_ms\":%.9g,\"reason\":\"%s\","
        "\"failed\":%s,\"actual_vrr_measured\":false,\"actual_display_luminance_measured\":false,\"latency_measured\":false",
        GetCurrentProcessId(), frames, occluded, waitsTimedOut, missedSlots, start, finished,
        (finished - start) * 1000.0 / gFrequency, failed ? "present_failure" : window.state.closed ? "own_window_closed" : "duration_elapsed",
        JsonBool(failed));
    return failed ? 2 : 0;
}

int SelfTest() {
    unsigned int checks = 0, failures = 0;
    auto check = [&](bool pass, const char* name) { ++checks; if (!pass) { ++failures; Log("test_failure", ",\"name\":\"%s\"", name); } };
    check(std::abs(std::pow(0.5, 2.0) - 0.25) < 1e-12, "gamma_2_anchor");
    check(std::abs(std::pow(0.5, 2.2) - 0.217637640824031) < 1e-12, "gamma_22_anchor");
    check(std::abs(std::pow(0.5, 2.4) - 0.189464570813800) < 1e-12, "gamma_24_anchor");
    check(std::abs(SrgbDecode(0.5) - 0.214041140482233) < 1e-12, "srgb_midpoint_anchor");
    check(SrgbDecode(0) == 0 && SrgbDecode(1) == 1, "srgb_endpoints");
    check(std::abs(PqEncode(100) - 0.508078421517399) < 1e-11, "pq_100_nits_anchor");
    check(std::abs(PqEncode(1000) - 0.751827096247041) < 1e-11, "pq_1000_nits_anchor");
    check(std::abs(PqEncode(10000) - 1.0) < 1e-12, "pq_10000_nits_anchor");
    for (double nits : {0.0, 0.001, 0.1, 1.0, 80.0, 160.0, 500.0, 1000.0, 10000.0})
        check(std::abs(PqDecode(PqEncode(nits)) - nits) < 1e-6, "pq_roundtrip");
    const auto white2020 = Rec709To2020({1, 1, 1});
    check(std::abs(white2020[0] - 1) < 1e-12 && std::abs(white2020[1] - 1) < 1e-12
        && std::abs(white2020[2] - 1) < 1e-12, "gamut_matrix_preserves_neutral");
    const auto sdr = Patches(Mode::Sdr, 80);
    const auto scrgb = Patches(Mode::ScRgb, 80);
    const auto pq = Patches(Mode::Pq, 80);
    check(std::abs(sdr[4].write[0] - 128.0 / 255) < 1e-7 && sdr[7].write[0] == 1, "sdr_unorm_source_code");
    check(scrgb[7].write[0] == 1 && scrgb[23].write[0] == 12.5f, "scrgb_80_and_1000_nits");
    check(std::abs(pq[23].write[0] - 0.751827096247041) < 1e-7, "pq_hdr_reference");
    check(pq[8].write[0] > pq[8].write[1] && pq[8].write[1] > pq[8].write[2], "pq_red_gamut_conversion");
    for (const auto dimensions : {std::array<UINT,2>{64,64}, {961,541}, {4096,4096}}) {
        unsigned long long area = 0;
        for (UINT index = 0; index < kPatchCount; ++index) {
            const auto rect = PatchRect(index, dimensions[0], dimensions[1]);
            check(rect.left >= 0 && rect.top >= 0 && rect.right <= static_cast<LONG>(dimensions[0])
                && rect.bottom <= static_cast<LONG>(dimensions[1]) && rect.right > rect.left && rect.bottom > rect.top, "patch_in_bounds");
            area += static_cast<unsigned long long>(rect.right - rect.left) * (rect.bottom - rect.top);
        }
        check(area == static_cast<unsigned long long>(dimensions[0]) * dimensions[1], "patches_cover_entire_surface");
        for (unsigned long long frame : {0ULL, 1ULL, 19ULL, 1234ULL, ULLONG_MAX}) {
            const Marker marker = MovingMarker(frame, dimensions[0], sdr);
            check(marker.rect.left >= 0 && marker.rect.right <= static_cast<LONG>(dimensions[0])
                && marker.rect.top == 0 && marker.rect.bottom == 2 && marker.rect.right - marker.rect.left == 8, "marker_bounded");
            for (UINT index = 0; index < kPatchCount; ++index) {
                const auto rect = PatchRect(index, dimensions[0], dimensions[1]);
                check(!InsideMarker(marker, (rect.left + rect.right) / 2, (rect.top + rect.bottom) / 2), "marker_excludes_patch_centre");
            }
        }
    }
    const Marker marker0 = MovingMarker(0, 64, sdr), marker1 = MovingMarker(1, 64, sdr);
    check(marker0.phase == 0 && marker1.phase == 1 && marker0.rect.left == 0 && marker1.rect.left == 7, "marker_motion_phase");
    check(marker0.write == sdr[13].write && marker1.write == sdr[12].write, "marker_encoded_source_colours");
    check(!Options{}.animateMarker, "default_pattern_has_no_marker");
    struct ParseCase { std::vector<const wchar_t*> arguments; bool valid; };
    const ParseCase cases[] = {
        {{L"fixture"}, true}, {{L"fixture",L"--mode",L"scrgb",L"--fps",L"59.94",L"--x",L"-1920",L"--hidden"}, true},
        {{L"fixture",L"--duration-ms",L"600000"}, true}, {{L"fixture",L"--duration-ms",L"600001"}, false},
        {{L"fixture",L"--duration-ms",L"0"}, false}, {{L"fixture",L"--duration-ms",L"-1"}, false},
        {{L"fixture",L"--width",L"99999999999999999999999999999"}, false},
        {{L"fixture",L"--mode",L"sdr",L"--mode",L"pq"}, false}, {{L"fixture",L"--unknown"}, false},
        {{L"fixture",L"--fps",L"nan"}, false}, {{L"fixture",L"--fps",L"1e3"}, false},
        {{L"fixture",L"--fps",L"59,94"}, false}, {{L"fixture",L"--fps"}, false},
        {{L"fixture",L"--mode",L"hdr"}, false}, {{L"fixture",L"--width",L"63"}, false},
        {{L"fixture",L"--self-test"}, true}, {{L"fixture",L"--self-test",L"--hidden"}, false},
        {{L"fixture",L"--gpu-self-test",L"--warp"}, true}, {{L"fixture",L"--gpu-self-test",L"--mode",L"pq"}, false},
        {{L"fixture",L"--animate-marker",L"--fps",L"137"}, true},
        {{L"fixture",L"--animate-marker",L"--animate-marker"}, false},
        {{L"fixture",L"--gpu-self-test",L"--animate-marker"}, false},
        {{L"fixture",L"--x",L"-32769"}, false}, {{L"fixture",L"--reference-white-nits",L"0"}, false}
    };
    for (const auto& test : cases) {
        Options options{}; const char* reason = nullptr;
        check(Parse(static_cast<int>(test.arguments.size()), test.arguments.data(), options, reason) == test.valid, "cli_parse");
    }
    Log("self_test_complete", ",\"checks\":%u,\"failures\":%u,\"created_windows\":false,\"created_gpu_device\":false,\"mathematical_only\":true", checks, failures);
    return failures ? 2 : 0;
}

int GpuSelfTest(bool warp) {
    Device device(warp);
    unsigned long long checks = 0, failures = 0;
    constexpr UINT width = 64, height = 24;
    for (Mode mode : {Mode::Sdr, Mode::ScRgb, Mode::Pq}) {
        D3D11_TEXTURE2D_DESC description{};
        description.Width = width; description.Height = height; description.MipLevels = 1; description.ArraySize = 1;
        description.Format = Format(mode); description.SampleDesc.Count = 1; description.Usage = D3D11_USAGE_DEFAULT;
        description.BindFlags = D3D11_BIND_RENDER_TARGET;
        ComPtr<ID3D11Texture2D> texture;
        Require(device.device->CreateTexture2D(&description, nullptr, &texture), "CreateTexture2D_own_offscreen");
        ComPtr<ID3D11RenderTargetView> view;
        Require(device.device->CreateRenderTargetView(texture.Get(), nullptr, &view), "CreateRenderTargetView_own_offscreen");
        const auto patches = Patches(mode, 80);
        description.Usage = D3D11_USAGE_STAGING; description.BindFlags = 0; description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> staging;
        Require(device.device->CreateTexture2D(&description, nullptr, &staging), "CreateTexture2D_own_staging");
        for (UINT testCase = 0; testCase < 3; ++testCase) {
            const bool animate = testCase != 0;
            const Marker marker = animate ? MovingMarker(testCase - 1, width, patches) : Marker{};
            // Repaint the same resource before each phase. Checking every pixel
            // also verifies that a prior marker position is fully restored.
            device.Draw(view.Get(), patches, width, height);
            if (animate) device.DrawMarker(view.Get(), marker);
            device.context->CopyResource(staging.Get(), texture.Get());
            D3D11_MAPPED_SUBRESOURCE mapped{};
            Require(device.context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped), "Map_own_generated_texture");
            unsigned long long modeFailures = 0;
            double maxError = 0;
            for (UINT y = 0; y < height; ++y) {
                const auto* row = static_cast<const unsigned char*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch;
                for (UINT x = 0; x < width; ++x) {
                    const bool marked = animate && InsideMarker(marker, static_cast<LONG>(x), static_cast<LONG>(y));
                    const auto& expected = marked ? marker.write : patches[(y / 8) * kColumns + x / 8].write;
                    std::array<double, 4> actual{};
                    if (mode == Mode::Sdr) for (size_t c = 0; c < 4; ++c) actual[c] = row[static_cast<size_t>(x) * 4 + c] / 255.0;
                    else if (mode == Mode::ScRgb) {
                        std::array<std::uint16_t, 4> half{};
                        std::memcpy(half.data(), row + static_cast<size_t>(x) * 8, 8);
                        for (size_t c = 0; c < 4; ++c) actual[c] = XMConvertHalfToFloat(half[c]);
                    } else {
                        std::uint32_t packed = 0; std::memcpy(&packed, row + static_cast<size_t>(x) * 4, 4);
                        for (size_t c = 0; c < 3; ++c) actual[c] = ((packed >> (10 * c)) & 1023U) / 1023.0;
                        actual[3] = (packed >> 30) / 3.0;
                    }
                    for (size_t c = 0; c < 4; ++c) {
                        const double error = std::abs(actual[c] - expected[c]);
                        const double tolerance = mode == Mode::Sdr ? 0.501 / 255.0
                            : mode == Mode::ScRgb ? std::max(1e-7, std::abs(static_cast<double>(expected[c])) / 1024.0)
                            : c == 3 ? 0.501 / 3.0 : 0.501 / 1023.0;
                        ++checks; maxError = std::max(maxError, error);
                        if (!std::isfinite(actual[c]) || error > tolerance) ++modeFailures;
                    }
                }
            }
            device.context->Unmap(staging.Get(), 0);
            failures += modeFailures;
            Log(animate ? "gpu_marker_test" : "gpu_texture_test", ",\"mode\":\"%s\",\"format_name\":\"%s\",\"width\":%u,\"height\":%u,"
                "\"channel_checks\":%u,\"failures\":%llu,\"max_absolute_storage_error\":%.12g,\"own_generated_texture_only\":true,\"display_output_test\":false,"
                "\"animate_marker\":%s,\"marker_phase\":%u,\"marker_rect\":[%ld,%ld,%ld,%ld]",
                ModeName(mode), FormatName(mode), width, height, width * height * 4, modeFailures, maxError,
                JsonBool(animate), marker.phase, marker.rect.left, marker.rect.top, marker.rect.right, marker.rect.bottom);
        }
    }
    Log("gpu_self_test_complete", ",\"checks\":%llu,\"failures\":%llu,\"warp\":%s,\"created_windows\":false,"
        "\"created_swap_chain\":false,\"desktop_capture\":false,\"display_output_test\":false,\"marker_frames_per_mode\":2", checks, failures, JsonBool(warp));
    return failures ? 2 : 0;
}
} // namespace

int wmain(int argc, wchar_t** argv) {
    LARGE_INTEGER frequency{};
    if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0 || frequency.QuadPart > 1000000000LL) return 2;
    gFrequency = frequency.QuadPart;
    try {
        Options options{}; const char* reason = nullptr;
        if (!Parse(argc, argv, options, reason)) { Log("error", ",\"reason\":\"%s\"", reason); Usage(); return 2; }
        if (options.help) { Usage(); return 0; }
        if (options.selfTest) return SelfTest();
        Watchdog watchdog;
        if (options.gpuSelfTest) return GpuSelfTest(options.warp);
        Log("config", ",\"schema_version\":1,\"pid\":%lu,\"qpc_frequency\":%lld,\"mode\":\"%s\",\"duration_ms\":%lu,"
            "\"fps\":%.9g,\"width\":%u,\"height\":%u,\"x\":%d,\"y\":%d,\"reference_white_nits\":%.9g,\"hidden\":%s,\"animate_marker\":%s,"
            "\"no_activate\":%s,\"borderless\":%s,\"warp\":%s,\"watchdog_grace_ms\":2000,\"startup_watchdog_ms\":15000,"
            "\"owns_only_its_window\":true,\"desktop_capture\":false,\"foreign_process_access\":false,\"display_setting_changes\":false",
            GetCurrentProcessId(), gFrequency, ModeName(options.mode), options.durationMs, options.fps, options.width, options.height,
            options.x, options.y, options.whiteNits, JsonBool(options.hidden), JsonBool(options.animateMarker), JsonBool(options.noActivate), JsonBool(options.borderless), JsonBool(options.warp));
        return Run(options, watchdog);
    } catch (const std::exception& error) {
        // Every exception string is an internal fixed ASCII operation identifier.
        Log("fatal", ",\"reason\":\"%s\",\"win32_last_error\":%lu", error.what(), GetLastError());
        return 2;
    }
}
