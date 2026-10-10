// Header-only probe: prints the contract actually selected by the compiler.
// No Windows/process APIs, DLL loading, hook installation, or desktop access.
#include "Hook/BuildContract.h"
#include <cstdio>

namespace {
using namespace selective_dwm::contract;
struct Rva { const char* name; std::uint32_t value; };
struct Guard { const char* name; std::uint32_t rva; const std::uint8_t* bytes; std::size_t length; };
#define RVA(name) {#name, name}
constexpr Rva rvas[] = {
    RVA(kCandidateRva), RVA(kIndependentFlipRva), RVA(kVisualGetHwndRva), RVA(kNodeGetHwndRva),
    RVA(kVisualGetPidRva), RVA(kNodeGetPidRva), RVA(kTopLevelWindowRva), RVA(kRealizationPixelFormatThunkRva),
    RVA(kSurfacePixelFormatVtableRva), RVA(kSurfacePixelFormatGetterRva), RVA(kD2DBitmapPixelFormatVtableRva),
    RVA(kD2DBitmapPixelFormatGetterRva), RVA(kGlobalCompositionSurfaceVtableRva),
    RVA(kSurfaceGetRealizationRva), RVA(kSurfaceGetBindRva)
};
#undef RVA
#define GUARD(name, rva) {#name, rva, name, sizeof(name)}
constexpr Guard guards[] = {
    GUARD(kCandidatePrologue, kCandidateRva), GUARD(kIndependentFlipPrologue, kIndependentFlipRva),
    GUARD(kVisualGetHwnd, kVisualGetHwndRva), GUARD(kNodeGetHwnd, kNodeGetHwndRva),
    GUARD(kVisualGetPid, kVisualGetPidRva), GUARD(kNodeGetPid, kNodeGetPidRva),
    GUARD(kTopLevelWindow, kTopLevelWindowRva), GUARD(kRealizationPixelFormatThunk, kRealizationPixelFormatThunkRva),
    GUARD(kSurfacePixelFormatGetter, kSurfacePixelFormatGetterRva), GUARD(kD2DBitmapPixelFormatGetter, kD2DBitmapPixelFormatGetterRva),
    GUARD(kSurfaceGetRealization, kSurfaceGetRealizationRva), GUARD(kSurfaceGetBind, kSurfaceGetBindRva)
};
#undef GUARD
void Hex(const std::uint8_t* bytes, std::size_t length) {
    for (std::size_t index = 0; index < length; ++index) std::printf("%02X", static_cast<unsigned int>(bytes[index]));
}
}

int main() {
    std::printf("{\"build\":\"%ls\",\"sha256\":\"%ls\",\"sha256_bytes\":\"", kDwmcoreBuild, kDwmcoreSha256Hex);
    Hex(kDwmcoreSha256, sizeof(kDwmcoreSha256));
    std::printf("\",\"rvas\":{");
    bool first = true;
    for (const auto& entry : rvas) {
        std::printf("%s\"%s\":%u", first ? "" : ",", entry.name, entry.value);
        first = false;
    }
    std::printf("},\"guards\":[");
    first = true;
    for (const auto& entry : guards) {
        std::printf("%s{\"name\":\"%s\",\"rva\":%u,\"bytes\":\"", first ? "" : ",", entry.name, entry.rva);
        Hex(entry.bytes, entry.length);
        std::printf("\"}");
        first = false;
    }
    std::printf("]}\n");
    return 0;
}
