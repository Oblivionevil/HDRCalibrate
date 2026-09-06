// Standalone, temporary DWM-composition experiment. This does NOT force DWM
// composition and does NOT establish that color management is applied.
// Build from an x64 Native Tools command prompt, in a separate output directory:
// cl /nologo /std:c++17 /EHsc /W4 /WX /O2 /MT CompositionProbe.cpp /link user32.lib gdi32.lib dwmapi.lib
// No injection, hooks, capture, input filtering, foreign-window writes, display
// changes, registry changes, or interaction with the target process are used.
// The only process access is QUERY_LIMITED_INFORMATION | SYNCHRONIZE.
// Black alpha=1 is NOT color-neutral. Its blending attenuation is 1/255;
// this is not a measured percentage of HDR luminance. Overlaps compound it.
// A visible marker can also change presentation behavior and latency.

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif
#include <windows.h>
#include <dwmapi.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cwchar>
#include <exception>
#include <iterator>
#include <string>

static_assert(sizeof(void*) == 8, "Build CompositionProbe for x64 only.");

namespace {

constexpr DWORD kMaximumDurationMs = 5000;
constexpr DWORD kMaximumDelayMs = 60000;
constexpr DWORD kPollMs = 50;
constexpr int kWatchdogExitCode = 124;
constexpr LONG kMaximumDimension = 16384;
constexpr LONGLONG kMaximumArea = 67108864;
constexpr wchar_t kClassName[] = L"HDRCalibrate.CompositionProbe.IsolatedWindow.v1";
LONGLONG gQpcFrequency = 0;

LONGLONG QpcNow() noexcept {
    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    return now.QuadPart;
}

LONGLONG MillisecondsToTicks(DWORD milliseconds) noexcept {
    return static_cast<LONGLONG>(milliseconds) * gQpcFrequency / 1000;
}

void LogAt(LONGLONG ticks, const char* event, const char* format = nullptr, ...) {
    std::fprintf(stdout, "{\"event\":\"%s\",\"qpc_ticks\":%lld", event, ticks);
    if (format != nullptr) {
        va_list arguments;
        va_start(arguments, format);
        std::vfprintf(stdout, format, arguments);
        va_end(arguments);
    }
    std::fputs("}\n", stdout);
    std::fflush(stdout);
}

void LogError(const char* operation, DWORD error = GetLastError()) {
    LogAt(QpcNow(), "error", ",\"operation\":\"%s\",\"win32_error\":%lu", operation, error);
}

unsigned long long HwndValue(HWND hwnd) noexcept {
    return static_cast<unsigned long long>(reinterpret_cast<ULONG_PTR>(hwnd));
}

class Handle final {
public:
    explicit Handle(HANDLE value = nullptr) noexcept : value_(value) {}
    ~Handle() { if (value_ != nullptr && value_ != INVALID_HANDLE_VALUE) CloseHandle(value_); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    HANDLE Get() const noexcept { return value_; }
    bool Valid() const noexcept { return value_ != nullptr && value_ != INVALID_HANDLE_VALUE; }
    void Reset(HANDLE value) noexcept {
        if (Valid()) CloseHandle(value_);
        value_ = value;
    }
private:
    HANDLE value_;
};

enum class Mode { Marker, Clear, Faint, Alpha, Multi };

const char* ModeName(Mode mode) noexcept {
    switch (mode) {
    case Mode::Marker: return "marker";
    case Mode::Clear: return "clear";
    case Mode::Faint: return "faint";
    case Mode::Alpha: return "alpha";
    case Mode::Multi: return "multi";
    }
    return "invalid";
}

BYTE ModeAlpha(Mode mode) noexcept {
    return mode == Mode::Clear ? static_cast<BYTE>(0)
        : mode == Mode::Marker ? static_cast<BYTE>(254) : static_cast<BYTE>(1);
}

struct Options {
    DWORD pid = 0;
    DWORD durationMs = kMaximumDurationMs;
    DWORD delayMs = 0;
    Mode mode = Mode::Marker;
    bool inspect = false;
};

bool ParseNumber(const wchar_t* text, DWORD minimum, DWORD maximum, DWORD& result) noexcept {
    if (text == nullptr || *text == L'\0') return false;
    DWORD value = 0;
    for (const wchar_t* cursor = text; *cursor != L'\0'; ++cursor) {
        if (*cursor < L'0' || *cursor > L'9') return false;
        const DWORD digit = static_cast<DWORD>(*cursor - L'0');
        if (value > maximum / 10 || (value == maximum / 10 && digit > maximum % 10)) return false;
        value = value * 10 + digit;
    }
    if (value < minimum) return false;
    result = value;
    return true;
}

bool ParseOptions(int argc, const wchar_t* const* argv, Options& options, const char*& reason) noexcept {
    unsigned int seen = 0;
    for (int index = 1; index < argc; ++index) {
        const wchar_t* argument = argv[index];
        unsigned int bit = 0;
        if (std::wcscmp(argument, L"--pid") == 0) bit = 1;
        else if (std::wcscmp(argument, L"--duration-ms") == 0) bit = 2;
        else if (std::wcscmp(argument, L"--delay-ms") == 0) bit = 4;
        else if (std::wcscmp(argument, L"--mode") == 0) bit = 8;
        else if (std::wcscmp(argument, L"--inspect") == 0) bit = 16;
        else { reason = "unknown_argument"; return false; }
        if ((seen & bit) != 0) { reason = "duplicate_argument"; return false; }
        seen |= bit;
        if (bit == 16) { options.inspect = true; continue; }
        if (++index >= argc) { reason = "missing_argument_value"; return false; }
        const wchar_t* value = argv[index];
        if (bit == 1 && !ParseNumber(value, 1, MAXDWORD, options.pid)) {
            reason = "invalid_pid"; return false;
        }
        if (bit == 2 && !ParseNumber(value, 1, kMaximumDurationMs, options.durationMs)) {
            reason = "invalid_duration"; return false;
        }
        if (bit == 4 && !ParseNumber(value, 0, kMaximumDelayMs, options.delayMs)) {
            reason = "invalid_delay"; return false;
        }
        if (bit == 8) {
            if (std::wcscmp(value, L"marker") == 0) options.mode = Mode::Marker;
            else if (std::wcscmp(value, L"clear") == 0) options.mode = Mode::Clear;
            else if (std::wcscmp(value, L"faint") == 0) options.mode = Mode::Faint;
            else if (std::wcscmp(value, L"alpha") == 0) options.mode = Mode::Alpha;
            else if (std::wcscmp(value, L"multi") == 0) options.mode = Mode::Multi;
            else { reason = "invalid_mode"; return false; }
        }
    }
    if (options.pid == 0) { reason = "pid_required"; return false; }
    if (options.inspect && (seen & (2U | 4U | 8U)) != 0) {
        reason = "inspect_cannot_have_experiment_options"; return false;
    }
    return true;
}

void Usage() {
    std::fputs(
        "CompositionProbe --pid POSITIVE_PID [--duration-ms 1..5000] "
        "[--delay-ms 0..60000] [--mode marker|clear|faint|alpha|multi]\n"
        "CompositionProbe --pid POSITIVE_PID --inspect\n"
        "CompositionProbe --self-test\n"
        "Defaults: marker, 5000 ms, no delay. Delay precedes the foreground check.\n"
        "Only HeroesOfTheStorm_x64.exe or HDRCalibrate.CompositionCanary.exe is allowed.\n"
        "clear: 8x8 alpha0 negative control; faint: 8x8 alpha1; marker: 8x8 alpha254.\n"
        "alpha: client-sized black alpha1 (non-neutral tint; blend space matters).\n"
        "multi: four overlapping black alpha1 rectangles (compounded tint).\n"
        "None of these modes guarantees composition. Presentation/latency can change.\n"
        "No global input hooks or capture. Only the helper's own windows are changed.\n"
        "Exit: 0 completed/inspected/tested, 2 invalid/error, 3 safety abort, "
        "124 watchdog self-exit. Windows scheduling is not hard real time.\n", stderr);
}

struct Target {
    DWORD pid = 0;
    Handle process;
    unsigned long long creationTime = 0;
    bool canary = false;

    bool Alive() const noexcept {
        return process.Valid() && WaitForSingleObject(process.Get(), 0) == WAIT_TIMEOUT;
    }
};

bool OpenTarget(DWORD pid, Target& target) {
    target.pid = pid;
    target.process.Reset(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, pid));
    if (!target.process.Valid()) { LogError("OpenProcess_limited_query"); return false; }
    std::array<wchar_t, 32768> path{};
    DWORD length = static_cast<DWORD>(path.size());
    if (!QueryFullProcessImageNameW(target.process.Get(), 0, path.data(), &length)) {
        LogError("QueryFullProcessImageName"); return false;
    }
    const wchar_t* slash = std::wcsrchr(path.data(), L'\\');
    const wchar_t* basename = slash == nullptr ? path.data() : slash + 1;
    target.canary = _wcsicmp(basename, L"HDRCalibrate.CompositionCanary.exe") == 0;
    if (!target.canary && _wcsicmp(basename, L"HeroesOfTheStorm_x64.exe") != 0) {
        LogAt(QpcNow(), "aborted", ",\"reason\":\"executable_not_allowlisted\",\"target_pid\":%lu", pid);
        return false;
    }
    FILETIME creation{}, exit{}, kernel{}, user{};
    if (!GetProcessTimes(target.process.Get(), &creation, &exit, &kernel, &user)) {
        LogError("GetProcessTimes"); return false;
    }
    target.creationTime = (static_cast<unsigned long long>(creation.dwHighDateTime) << 32)
        | static_cast<unsigned long long>(creation.dwLowDateTime);
    if (GetProcessId(target.process.Get()) != pid || !target.Alive()) {
        LogAt(QpcNow(), "aborted", ",\"reason\":\"target_exited_or_identity_invalid\"");
        return false;
    }
    LogAt(QpcNow(), "target_identity", ",\"target_pid\":%lu,\"process_creation_filetime\":%llu,"
        "\"executable\":\"%s\",\"process_access\":\"QUERY_LIMITED_INFORMATION|SYNCHRONIZE\"",
        pid, target.creationTime, target.canary ? "HDRCalibrate.CompositionCanary.exe" : "HeroesOfTheStorm_x64.exe");
    return true;
}

struct Snapshot {
    LONGLONG ticks = 0;
    HWND hwnd = nullptr;
    DWORD pid = 0;
    HWND foreground = nullptr;
    DWORD foregroundPid = 0;
    RECT client{};
    UINT dpi = 0;
    DWORD cloaked = 0;
    HRESULT cloakResult = E_FAIL;
    bool exists = false;
    bool visible = false;
    bool iconic = false;
    bool validClient = false;
};

Snapshot ReadWindow(HWND hwnd) noexcept {
    Snapshot snapshot{};
    snapshot.ticks = QpcNow();
    snapshot.hwnd = hwnd;
    snapshot.foreground = GetForegroundWindow();
    GetWindowThreadProcessId(snapshot.foreground, &snapshot.foregroundPid);
    snapshot.exists = hwnd != nullptr && IsWindow(hwnd) != FALSE;
    if (!snapshot.exists) return snapshot;
    GetWindowThreadProcessId(hwnd, &snapshot.pid);
    snapshot.visible = IsWindowVisible(hwnd) != FALSE;
    snapshot.iconic = IsIconic(hwnd) != FALSE;
    snapshot.dpi = GetDpiForWindow(hwnd);
    snapshot.cloakResult = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED,
        &snapshot.cloaked, static_cast<DWORD>(sizeof(snapshot.cloaked)));
    RECT client{};
    if (GetClientRect(hwnd, &client)) {
        POINT topLeft{client.left, client.top};
        POINT bottomRight{client.right, client.bottom};
        if (ClientToScreen(hwnd, &topLeft) && ClientToScreen(hwnd, &bottomRight)) {
            snapshot.client = RECT{topLeft.x, topLeft.y, bottomRight.x, bottomRight.y};
            snapshot.validClient = bottomRight.x > topLeft.x && bottomRight.y > topLeft.y;
        }
    }
    return snapshot;
}

void LogSnapshot(const char* event, const Snapshot& snapshot) {
    LogAt(snapshot.ticks, event,
        ",\"hwnd\":%llu,\"pid\":%lu,\"foreground_hwnd\":%llu,\"foreground_pid\":%lu,"
        "\"exists\":%s,\"visible\":%s,\"iconic\":%s,\"cloaked\":%lu,\"cloak_hresult\":%ld,"
        "\"dpi\":%u,\"client_valid\":%s,\"client_left\":%ld,\"client_top\":%ld,"
        "\"client_right\":%ld,\"client_bottom\":%ld",
        HwndValue(snapshot.hwnd), snapshot.pid, HwndValue(snapshot.foreground), snapshot.foregroundPid,
        snapshot.exists ? "true" : "false", snapshot.visible ? "true" : "false", snapshot.iconic ? "true" : "false",
        snapshot.cloaked, snapshot.cloakResult, snapshot.dpi, snapshot.validClient ? "true" : "false",
        snapshot.client.left, snapshot.client.top, snapshot.client.right, snapshot.client.bottom);
}

const char* UnsafeReason(const Target& target, const Snapshot& snapshot) noexcept {
    // The retained process handle refers to the original process object. A PID
    // reused after that process exits can never pass this liveness check.
    if (!target.Alive()) return "target_exited";
    if (!snapshot.exists || snapshot.pid != target.pid) return "target_window_identity_changed";
    if (snapshot.foreground != snapshot.hwnd || snapshot.foregroundPid != target.pid) return "foreground_lost";
    if (!snapshot.visible || snapshot.iconic) return "target_not_visible";
    if (FAILED(snapshot.cloakResult)) return "cloaking_status_unavailable";
    if (snapshot.cloaked != 0) return "target_cloaked";
    if (!snapshot.validClient || snapshot.dpi == 0) return "target_geometry_unavailable";
    return nullptr;
}

struct Enumeration {
    DWORD pid;
    unsigned int count = 0;
};

BOOL CALLBACK EnumerateWindow(HWND hwnd, LPARAM parameter) {
    auto* enumeration = reinterpret_cast<Enumeration*>(parameter);
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid == enumeration->pid) {
        ++enumeration->count;
        LogSnapshot("inspect_window", ReadWindow(hwnd));
    }
    return TRUE;
}

int Inspect(const Target& target) {
    LogSnapshot("foreground_state", ReadWindow(GetForegroundWindow()));
    Enumeration enumeration{target.pid};
    if (!EnumWindows(EnumerateWindow, reinterpret_cast<LPARAM>(&enumeration))) {
        LogError("EnumWindows"); return 2;
    }
    LogAt(QpcNow(), "inspect_complete", ",\"target_pid\":%lu,\"window_count\":%u,\"target_alive\":%s",
        target.pid, enumeration.count, target.Alive() ? "true" : "false");
    return target.Alive() ? 0 : 3;
}

bool EffectiveClient(const RECT& client, RECT& effective) noexcept {
    const LONGLONG width = static_cast<LONGLONG>(client.right) - client.left;
    const LONGLONG height = static_cast<LONGLONG>(client.bottom) - client.top;
    if (width <= 0 || height <= 0 || width > kMaximumDimension || height > kMaximumDimension
        || width * height > kMaximumArea) return false;
    const LONG virtualLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
    const LONG virtualTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
    const LONG virtualWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    const LONG virtualHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);
    if (virtualWidth <= 0 || virtualHeight <= 0) return false;
    const RECT desktop{virtualLeft, virtualTop, virtualLeft + virtualWidth, virtualTop + virtualHeight};
    return IntersectRect(&effective, &client, &desktop) != FALSE;
}

std::array<RECT, 4> MakeRectangles(const RECT& client, Mode mode, unsigned int& count) noexcept {
    std::array<RECT, 4> rectangles{};
    const LONG width = client.right - client.left;
    const LONG height = client.bottom - client.top;
    count = mode == Mode::Multi ? 4U : 1U;
    if (mode == Mode::Alpha) {
        rectangles[0] = client;
    } else if (mode == Mode::Multi) {
        const LONG rectangleWidth = std::max(1L, width * 3 / 4);
        const LONG rectangleHeight = std::max(1L, height * 3 / 4);
        rectangles[0] = RECT{client.left, client.top, client.left + rectangleWidth, client.top + rectangleHeight};
        rectangles[1] = RECT{client.right - rectangleWidth, client.top, client.right, client.top + rectangleHeight};
        rectangles[2] = RECT{client.left, client.bottom - rectangleHeight, client.left + rectangleWidth, client.bottom};
        rectangles[3] = RECT{client.right - rectangleWidth, client.bottom - rectangleHeight, client.right, client.bottom};
    } else {
        rectangles[0] = RECT{client.left, client.top, client.left + std::min(8L, width), client.top + std::min(8L, height)};
    }
    return rectangles;
}

class Watchdog final {
public:
    Watchdog() : deadline_(QpcNow() + MillisecondsToTicks(7000)), done_(CreateEventW(nullptr, TRUE, FALSE, nullptr)) {
        if (done_.Valid()) thread_.Reset(CreateThread(nullptr, 0, ThreadEntry, this, 0, nullptr));
    }
    ~Watchdog() {
        if (done_.Valid()) SetEvent(done_.Get());
        if (thread_.Valid() && WaitForSingleObject(thread_.Get(), 2000) != WAIT_OBJECT_0) {
            // Do not let a worker retain a pointer to a destroyed object.
            ExitProcess(kWatchdogExitCode);
        }
    }
    Watchdog(const Watchdog&) = delete;
    Watchdog& operator=(const Watchdog&) = delete;
    bool Valid() const noexcept { return done_.Valid() && thread_.Valid(); }
    void Arm(LONGLONG deadline) noexcept { deadline_.store(deadline, std::memory_order_release); }
    void Hidden() noexcept { SetEvent(done_.Get()); }
private:
    static DWORD WINAPI ThreadEntry(void* parameter) noexcept {
        auto* watchdog = static_cast<Watchdog*>(parameter);
        for (;;) {
            const LONGLONG remaining = watchdog->deadline_.load(std::memory_order_acquire) - QpcNow();
            if (remaining <= 0) {
                if (WaitForSingleObject(watchdog->done_.Get(), 0) == WAIT_OBJECT_0) return 0;
                // No logging, UI call, or sleep before self-exit. Windows removes
                // this process's own windows even if its UI thread is stalled.
                ExitProcess(kWatchdogExitCode);
            }
            const DWORD waitMs = static_cast<DWORD>(std::clamp<LONGLONG>(remaining * 1000 / gQpcFrequency, 1, kPollMs));
            if (WaitForSingleObject(watchdog->done_.Get(), waitMs) == WAIT_OBJECT_0) return 0;
        }
    }
    std::atomic<LONGLONG> deadline_;
    Handle done_;
    Handle thread_;
};

struct OverlayState {
    std::array<HWND, 4> windows{};
    unsigned int count = 0;
    Watchdog* watchdog = nullptr;
    const char* abortReason = nullptr;
    bool beganShowing = false;
    bool hiddenLogged = false;

    void Hide(const char* reason) noexcept {
        const LONGLONG hideStart = QpcNow();
        bool allHidden = true;
        for (unsigned int index = 0; index < count; ++index) {
            if (IsWindow(windows[index])) {
                ShowWindow(windows[index], SW_HIDE);
                if (IsWindowVisible(windows[index])) allHidden = false;
            }
        }
        const LONGLONG hideComplete = QpcNow();
        // Do not disarm here: Hide can run reentrantly inside ShowWindow.
        // The independent watchdog remains armed until all owned windows have
        // been destroyed and its scope ends, even if a log call stalls.
        if (beganShowing && !hiddenLogged) {
            hiddenLogged = true;
            LogAt(hideStart, "hidden", ",\"hide_complete_qpc_ticks\":%lld,\"reason\":\"%s\",\"all_hidden\":%s",
                hideComplete, reason, allHidden ? "true" : "false");
        }
    }

    void Abort(const char* reason) noexcept {
        if (abortReason == nullptr) abortReason = reason;
        Hide(reason);
    }
};

LRESULT CALLBACK WindowProcedure(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
    auto* state = reinterpret_cast<OverlayState*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        const auto* creation = reinterpret_cast<const CREATESTRUCTW*>(lParam);
        state = static_cast<OverlayState*>(creation->lpCreateParams);
        // This HWND was just created by this process. No foreign HWND is written.
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(state));
    }
    switch (message) {
    case WM_NCHITTEST: return HTTRANSPARENT;
    case WM_MOUSEACTIVATE: return MA_NOACTIVATE;
    case WM_PAINT: {
        PAINTSTRUCT paint{};
        const HDC dc = BeginPaint(hwnd, &paint);
        if (dc != nullptr) FillRect(dc, &paint.rcPaint, static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH)));
        EndPaint(hwnd, &paint);
        return 0;
    }
    case WM_CLOSE:
        if (state != nullptr) state->Abort("overlay_close_requested");
        return 0;
    case WM_DISPLAYCHANGE:
        if (state != nullptr) state->Abort("display_changed");
        return 0;
    case WM_DPICHANGED:
        if (state != nullptr) state->Abort("dpi_changed");
        return 0;
    case WM_SETFOCUS:
        if (state != nullptr) state->Abort("unexpected_overlay_focus");
        return 0;
    case WM_ACTIVATE:
        if (LOWORD(wParam) != WA_INACTIVE && state != nullptr) state->Abort("unexpected_overlay_activation");
        return 0;
    default: break;
    }
    return DefWindowProcW(hwnd, message, wParam, lParam);
}

class OverlayWindows final {
public:
    explicit OverlayWindows(Watchdog& watchdog) noexcept { state_.watchdog = &watchdog; }
    ~OverlayWindows() {
        state_.Hide("cleanup");
        for (unsigned int index = state_.count; index > 0; --index) {
            const HWND hwnd = state_.windows[index - 1];
            if (IsWindow(hwnd)) DestroyWindow(hwnd);
        }
        if (registered_) UnregisterClassW(kClassName, GetModuleHandleW(nullptr));
        LogAt(QpcNow(), "cleanup_complete", ",\"own_window_count\":%u", state_.count);
    }
    OverlayWindows(const OverlayWindows&) = delete;
    OverlayWindows& operator=(const OverlayWindows&) = delete;

    bool Create(const RECT& client, Mode mode) {
        WNDCLASSEXW windowClass{};
        windowClass.cbSize = static_cast<UINT>(sizeof(windowClass));
        windowClass.lpfnWndProc = WindowProcedure;
        windowClass.hInstance = GetModuleHandleW(nullptr);
        windowClass.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
        windowClass.lpszClassName = kClassName;
        if (RegisterClassExW(&windowClass) == 0) { LogError("RegisterClassEx"); return false; }
        registered_ = true;
        unsigned int count = 0;
        const auto rectangles = MakeRectangles(client, mode, count);
        constexpr DWORD extendedStyle = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOPMOST;
        for (unsigned int index = 0; index < count; ++index) {
            const RECT& rectangle = rectangles[index];
            const HWND hwnd = CreateWindowExW(extendedStyle, kClassName, L"Composition probe (temporary)", WS_POPUP,
                rectangle.left, rectangle.top, rectangle.right - rectangle.left, rectangle.bottom - rectangle.top,
                nullptr, nullptr, windowClass.hInstance, &state_);
            if (hwnd == nullptr) { LogError("CreateWindowEx"); return false; }
            // Register ownership immediately, before any later operation can fail.
            state_.windows[state_.count++] = hwnd;
            if (!SetLayeredWindowAttributes(hwnd, 0, ModeAlpha(mode), LWA_ALPHA)) {
                LogError("SetLayeredWindowAttributes_own_window"); return false;
            }
            if (!SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER)) {
                LogError("SetWindowPos_own_window"); return false;
            }
            LogAt(QpcNow(), "overlay_created", ",\"index\":%u,\"hwnd\":%llu,\"alpha\":%u,"
                "\"left\":%ld,\"top\":%ld,\"right\":%ld,\"bottom\":%ld,\"initially_hidden\":true",
                index, HwndValue(hwnd), static_cast<unsigned int>(ModeAlpha(mode)),
                rectangle.left, rectangle.top, rectangle.right, rectangle.bottom);
            if (state_.abortReason != nullptr) return false;
        }
        return true;
    }

    OverlayState& State() noexcept { return state_; }
private:
    OverlayState state_;
    bool registered_ = false;
};

int RunExperiment(const Options& options, const Target& target) {
    if (options.delayMs != 0) {
        LogAt(QpcNow(), "delay_started", ",\"delay_ms\":%lu", options.delayMs);
        const DWORD waitResult = WaitForSingleObject(target.process.Get(), options.delayMs);
        if (waitResult != WAIT_TIMEOUT) {
            LogAt(QpcNow(), "aborted", ",\"reason\":\"target_exit_or_wait_failure_during_delay\"");
            return 3;
        }
        LogAt(QpcNow(), "delay_complete");
    }
    const Snapshot initial = ReadWindow(GetForegroundWindow());
    LogSnapshot("foreground_state", initial);
    if (const char* reason = UnsafeReason(target, initial)) {
        LogAt(QpcNow(), "aborted", ",\"reason\":\"%s\"", reason);
        return 3;
    }
    RECT effective{};
    if (!EffectiveClient(initial.client, effective)) {
        LogAt(QpcNow(), "aborted", ",\"reason\":\"unreasonable_or_offscreen_client_bounds\"");
        return 3;
    }

    Watchdog watchdog;
    if (!watchdog.Valid()) { LogError("CreateWatchdog"); return 2; }
    OverlayWindows overlays(watchdog);
    if (!overlays.Create(effective, options.mode)) return 2;
    OverlayState& state = overlays.State();
    Snapshot current = ReadWindow(initial.hwnd);
    const char* reason = UnsafeReason(target, current);
    if (reason == nullptr && (!EqualRect(&initial.client, &current.client) || initial.dpi != current.dpi)) {
        reason = "target_geometry_changed";
    }
    if (reason != nullptr) {
        state.Abort(reason);
        LogSnapshot("foreground_state", current);
        LogAt(QpcNow(), "aborted", ",\"reason\":\"%s\"", reason);
        return 3;
    }

    // Start timing BEFORE the first show request. A normal hide is scheduled a
    // few milliseconds early to let it finish before the independent watchdog.
    const LONGLONG visibleStart = QpcNow();
    const LONGLONG deadline = visibleStart + MillisecondsToTicks(options.durationMs);
    const DWORD hideMarginMs = std::min<DWORD>(25, options.durationMs / 10);
    const LONGLONG normalHideDeadline = deadline - MillisecondsToTicks(hideMarginMs);
    watchdog.Arm(deadline);
    state.beganShowing = true;
    for (unsigned int index = 0; index < state.count; ++index) {
        current = ReadWindow(initial.hwnd);
        reason = UnsafeReason(target, current);
        if (reason == nullptr && (!EqualRect(&initial.client, &current.client) || initial.dpi != current.dpi)) {
            reason = "target_geometry_changed";
        }
        if (reason != nullptr || QpcNow() >= normalHideDeadline || state.abortReason != nullptr) {
            state.Abort(reason != nullptr ? reason : "show_interrupted_or_deadline_reached");
            break;
        }
        ShowWindow(state.windows[index], SW_SHOWNOACTIVATE);
        if (!IsWindowVisible(state.windows[index])) {
            state.Abort("own_window_show_failed");
            break;
        }
        // Avoid a flush while visible until every own window has been shown.
        UpdateWindow(state.windows[index]);
    }
    const LONGLONG showComplete = QpcNow();
    if (state.abortReason == nullptr) {
        LogAt(visibleStart, "shown", ",\"show_complete_qpc_ticks\":%lld,\"intended_deadline_qpc_ticks\":%lld,"
            "\"normal_hide_deadline_qpc_ticks\":%lld,\"target_hwnd\":%llu,\"overlay_count\":%u,\"alpha\":%u,"
            "\"meaning\":\"own_HWND_visibility_not_proof_of_scanout_or_composition\"",
            showComplete, deadline, normalHideDeadline, HwndValue(initial.hwnd), state.count,
            static_cast<unsigned int>(ModeAlpha(options.mode)));
    }

    bool completed = false;
    while (state.abortReason == nullptr) {
        current = ReadWindow(initial.hwnd);
        reason = UnsafeReason(target, current);
        if (reason == nullptr && (!EqualRect(&initial.client, &current.client) || initial.dpi != current.dpi)) {
            reason = "target_geometry_changed";
        }
        if (reason != nullptr) { state.Abort(reason); break; }
        for (unsigned int index = 0; index < state.count; ++index) {
            if (!IsWindow(state.windows[index]) || !IsWindowVisible(state.windows[index])) {
                state.Abort("own_window_hidden_or_destroyed");
                break;
            }
        }
        if (state.abortReason != nullptr) break;
        const LONGLONG remaining = normalHideDeadline - QpcNow();
        if (remaining <= 0) { state.Hide("duration_elapsed"); completed = true; break; }
        const DWORD waitMs = static_cast<DWORD>(std::clamp<LONGLONG>(remaining * 1000 / gQpcFrequency, 1, kPollMs));
        const HANDLE process = target.process.Get();
        const DWORD waitResult = MsgWaitForMultipleObjectsEx(1, &process, waitMs, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        if (waitResult == WAIT_FAILED) { state.Abort("message_wait_failed"); break; }
        if (waitResult == WAIT_OBJECT_0) { state.Abort("target_exited"); break; }
        MSG message{};
        for (unsigned int messages = 0; messages < 64 && PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE); ++messages) {
            if (message.message == WM_QUIT) { state.Abort("own_quit_message"); break; }
            TranslateMessage(&message);
            DispatchMessageW(&message);
            if (state.abortReason != nullptr || QpcNow() >= normalHideDeadline) break;
        }
    }
    state.Hide(completed ? "duration_elapsed" : state.abortReason);
    LogSnapshot("foreground_state", ReadWindow(initial.hwnd));
    LogAt(QpcNow(), completed ? "completed" : "aborted", ",\"reason\":\"%s\",\"target_alive\":%s",
        completed ? "duration_elapsed" : state.abortReason, target.Alive() ? "true" : "false");
    return completed ? 0 : 3;
}

int SelfTest() {
    struct NumberCase { const wchar_t* text; DWORD minimum; DWORD maximum; bool valid; };
    constexpr NumberCase cases[] = {
        {L"1", 1, MAXDWORD, true}, {L"4294967295", 1, MAXDWORD, true},
        {L"4294967296", 1, MAXDWORD, false}, {L"99999999999999999999", 1, MAXDWORD, false},
        {L"0", 1, MAXDWORD, false}, {L"-1", 1, MAXDWORD, false}, {L"+1", 1, MAXDWORD, false},
        {L" 1", 1, MAXDWORD, false}, {L"1x", 1, MAXDWORD, false}, {L"", 1, MAXDWORD, false},
        {L"5000", 1, kMaximumDurationMs, true}, {L"5001", 1, kMaximumDurationMs, false},
        {L"60000", 0, kMaximumDelayMs, true}, {L"60001", 0, kMaximumDelayMs, false}
    };
    unsigned int failures = 0;
    unsigned int checks = 0;
    for (const auto& test : cases) {
        DWORD value = 0;
        ++checks;
        if (ParseNumber(test.text, test.minimum, test.maximum, value) != test.valid) ++failures;
    }
    const wchar_t* const valid[] = {L"probe", L"--pid", L"12", L"--mode", L"clear", L"--duration-ms", L"5000", L"--delay-ms", L"60000"};
    const wchar_t* const invalid[] = {L"probe", L"--pid", L"12", L"--inspect", L"--mode", L"alpha"};
    const wchar_t* const duplicate[] = {L"probe", L"--pid", L"12", L"--pid", L"13"};
    const wchar_t* const missing[] = {L"probe", L"--duration-ms", L"5000"};
    Options options{};
    const char* reason = nullptr;
    ++checks;
    if (!ParseOptions(static_cast<int>(std::size(valid)), valid, options, reason)
        || options.pid != 12 || options.mode != Mode::Clear || options.durationMs != 5000 || options.delayMs != 60000) ++failures;
    options = Options{};
    ++checks;
    if (ParseOptions(static_cast<int>(std::size(invalid)), invalid, options, reason)) ++failures;
    options = Options{};
    ++checks;
    if (ParseOptions(static_cast<int>(std::size(duplicate)), duplicate, options, reason)) ++failures;
    options = Options{};
    ++checks;
    if (ParseOptions(static_cast<int>(std::size(missing)), missing, options, reason)) ++failures;
    for (const Mode mode : {Mode::Marker, Mode::Clear, Mode::Faint, Mode::Alpha, Mode::Multi}) {
        unsigned int count = 0;
        const RECT bounds{-100, -50, 1820, 1030};
        const auto rectangles = MakeRectangles(bounds, mode, count);
        for (unsigned int index = 0; index < count; ++index) {
            const RECT& rectangle = rectangles[index];
            ++checks;
            if (rectangle.left < bounds.left || rectangle.top < bounds.top || rectangle.right > bounds.right
                || rectangle.bottom > bounds.bottom || rectangle.left >= rectangle.right || rectangle.top >= rectangle.bottom) ++failures;
        }
    }
    LogAt(QpcNow(), "self_test_complete", ",\"checks\":%u,\"failures\":%u,\"created_windows\":false,\"opened_target_process\":false",
        checks, failures);
    return failures == 0 ? 0 : 2;
}

class ThreadDpiContext final {
public:
    ThreadDpiContext() noexcept : previous_(SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) {}
    ~ThreadDpiContext() { if (previous_ != nullptr) SetThreadDpiAwarenessContext(previous_); }
    bool Valid() const noexcept { return previous_ != nullptr; }
private:
    DPI_AWARENESS_CONTEXT previous_;
};

} // namespace

int wmain(int argc, wchar_t** argv) {
    LARGE_INTEGER frequency{};
    if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0 || frequency.QuadPart > 1000000000LL) return 2;
    gQpcFrequency = frequency.QuadPart;
    if (argc == 2 && std::wcscmp(argv[1], L"--help") == 0) { Usage(); return 0; }
    if (argc == 2 && std::wcscmp(argv[1], L"--self-test") == 0) return SelfTest();
    Options options{};
    const char* reason = nullptr;
    if (!ParseOptions(argc, argv, options, reason)) {
        LogAt(QpcNow(), "error", ",\"reason\":\"%s\"", reason);
        Usage();
        return 2;
    }
    try {
        LogAt(QpcNow(), "config", ",\"qpc_frequency\":%lld,\"helper_pid\":%lu,\"target_pid\":%lu,"
            "\"duration_ms\":%lu,\"delay_ms\":%lu,\"mode\":\"%s\",\"alpha\":%u,\"inspect\":%s,"
            "\"no_input_hooks\":true,\"no_capture\":true,\"no_foreign_window_writes\":true,\"watchdog_exit_code\":%d",
            gQpcFrequency, GetCurrentProcessId(), options.pid, options.durationMs, options.delayMs,
            ModeName(options.mode), static_cast<unsigned int>(ModeAlpha(options.mode)), options.inspect ? "true" : "false", kWatchdogExitCode);
        // This is confined to the helper's thread. It does not change monitor DPI
        // or any target window/process setting. Inspection creates no windows.
        ThreadDpiContext dpiContext;
        if (!dpiContext.Valid()) { LogError("SetThreadDpiAwarenessContext_own_thread"); return 2; }
        Target target;
        if (!OpenTarget(options.pid, target)) return 2;
        return options.inspect ? Inspect(target) : RunExperiment(options, target);
    } catch (const std::exception&) {
        LogAt(QpcNow(), "error", ",\"reason\":\"unhandled_cpp_exception_after_raii_cleanup\"");
        return 2;
    } catch (...) {
        LogAt(QpcNow(), "error", ",\"reason\":\"unhandled_exception_after_raii_cleanup\"");
        return 2;
    }
}
