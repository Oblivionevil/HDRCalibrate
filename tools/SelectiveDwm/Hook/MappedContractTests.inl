// Included inside HookSelfTest's namespace after Check(). All mutations and
// MinHook preparations below concern memory owned by this test process only.
// A read-only SEC_IMAGE_NO_EXECUTE view checks the current Windows file without
// LoadLibrary, DLL initialization, or access to another process.
struct ContractTestSpan {
    const char* name;
    std::uint32_t rva;
    const std::uint8_t* bytes;
    std::size_t length;
};
#define TEST_SPAN(name, rva) {#name, contract::rva, contract::name, sizeof(contract::name)}
constexpr ContractTestSpan kMappedTestSpans[] = {
    TEST_SPAN(kCandidatePrologue, kCandidateRva),
    TEST_SPAN(kIndependentFlipPrologue, kIndependentFlipRva),
    TEST_SPAN(kVisualGetHwnd, kVisualGetHwndRva),
    TEST_SPAN(kNodeGetHwnd, kNodeGetHwndRva),
    TEST_SPAN(kVisualGetPid, kVisualGetPidRva),
    TEST_SPAN(kNodeGetPid, kNodeGetPidRva),
    TEST_SPAN(kTopLevelWindow, kTopLevelWindowRva),
    TEST_SPAN(kRealizationPixelFormatThunk, kRealizationPixelFormatThunkRva),
    TEST_SPAN(kSurfacePixelFormatGetter, kSurfacePixelFormatGetterRva),
    TEST_SPAN(kD2DBitmapPixelFormatGetter, kD2DBitmapPixelFormatGetterRva),
    TEST_SPAN(kSurfaceGetRealization, kSurfaceGetRealizationRva),
    TEST_SPAN(kSurfaceGetBind, kSurfaceGetBindRva),
#if HDR_SELECTIVE_DWMCORE_BUILD == 261009278
    TEST_SPAN(kPixelFormatOuter, kPixelFormatOuterRva),
    TEST_SPAN(kPlaneConstructor, kPlaneConstructorRva),
#endif
};
#undef TEST_SPAN

void MappedContractTests() {
    constexpr std::size_t imageSize = 0x443000;
    auto* image = static_cast<std::uint8_t*>(VirtualAlloc(nullptr, imageSize,
        MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    Check("mapped-contract-owned-allocation", image != nullptr);
    if (!image) return;
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(image);
    dos->e_magic = IMAGE_DOS_SIGNATURE;
    dos->e_lfanew = 0x100;
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(image + dos->e_lfanew);
    nt->Signature = IMAGE_NT_SIGNATURE;
    nt->FileHeader.Machine = IMAGE_FILE_MACHINE_AMD64;
    nt->FileHeader.SizeOfOptionalHeader = sizeof(IMAGE_OPTIONAL_HEADER64);
    nt->OptionalHeader.Magic = IMAGE_NT_OPTIONAL_HDR64_MAGIC;
    nt->OptionalHeader.SizeOfImage = static_cast<DWORD>(imageSize);
    for (const auto& span : kMappedTestSpans)
        std::memcpy(image + span.rva, span.bytes, span.length);
    auto* beforeBase = g_dwmcore;
    const auto beforeSize = g_imageSize;
    Check("real-mapped-verifier-accepts-complete-selected-contract", VerifyMappedImageContract(image, imageSize));
    Check("mapped-verifier-rejects-null", !VerifyMappedImageContract(nullptr, imageSize));
    Check("mapped-verifier-rejects-short-header", !VerifyMappedImageContract(image, sizeof(IMAGE_DOS_HEADER) - 1));
    Check("mapped-verifier-rejects-oversized-image", !VerifyMappedImageContract(image, 129u * 1024u * 1024u));
    Check("mapped-verifier-rejects-inaccessible-header", !VerifyMappedImageContract(reinterpret_cast<const std::uint8_t*>(1), imageSize));
    dos->e_magic = 0;
    Check("mapped-verifier-rejects-DOS-signature", !VerifyMappedImageContract(image, imageSize));
    dos->e_magic = IMAGE_DOS_SIGNATURE;
    constexpr LONG invalidOffsets[] = { -1L, 0L, 0x100001L, static_cast<LONG>(imageSize - 1) };
    for (LONG offset : invalidOffsets) {
        dos->e_lfanew = offset;
        Check("mapped-verifier-rejects-invalid-NT-offset", !VerifyMappedImageContract(image, imageSize));
    }
    dos->e_lfanew = 0x100;
    Check("mapped-verifier-bounds-NT-header", !VerifyMappedImageContract(image, 0x180));
    nt->Signature = 0;
    Check("mapped-verifier-rejects-PE-signature", !VerifyMappedImageContract(image, imageSize));
    nt->Signature = IMAGE_NT_SIGNATURE;
    nt->FileHeader.Machine = IMAGE_FILE_MACHINE_I386;
    Check("mapped-verifier-rejects-x86-machine", !VerifyMappedImageContract(image, imageSize));
    nt->FileHeader.Machine = IMAGE_FILE_MACHINE_AMD64;
    nt->FileHeader.SizeOfOptionalHeader = 1;
    Check("mapped-verifier-rejects-optional-header-size", !VerifyMappedImageContract(image, imageSize));
    nt->FileHeader.SizeOfOptionalHeader = sizeof(IMAGE_OPTIONAL_HEADER64);
    nt->OptionalHeader.Magic = IMAGE_NT_OPTIONAL_HDR32_MAGIC;
    Check("mapped-verifier-rejects-PE32-magic", !VerifyMappedImageContract(image, imageSize));
    nt->OptionalHeader.Magic = IMAGE_NT_OPTIONAL_HDR64_MAGIC;
    nt->OptionalHeader.SizeOfImage = static_cast<DWORD>(imageSize - 0x1000);
    Check("mapped-verifier-rejects-module-header-size-disagreement", !VerifyMappedImageContract(image, imageSize));
#if HDR_SELECTIVE_DWMCORE_BUILD == 261009278
    Check("mapped-verifier-pins-9278-image-size", !VerifyMappedImageContract(image, imageSize - 0x1000));
#endif
    nt->OptionalHeader.SizeOfImage = static_cast<DWORD>(imageSize);
    std::size_t mutatedBytes = 0;
    for (const auto& span : kMappedTestSpans) {
        bool rejected = true;
        for (std::size_t index = 0; index < span.length; ++index) {
            image[span.rva + index] ^= 1;
            rejected = !VerifyMappedImageContract(image, imageSize) && rejected;
            image[span.rva + index] ^= 1;
            ++mutatedBytes;
        }
        char label[160]{};
        sprintf_s(label, "real-mapped-verifier-rejects-every-byte-mutation-%s", span.name);
        Check(label, rejected && VerifyMappedImageContract(image, imageSize));
    }
    Check("mapped-code-bounds-rejects-overflowing-RVA", !CodeEquals(image, imageSize, 0xFFFFFFFFu, contract::kCandidatePrologue, sizeof(contract::kCandidatePrologue)));
    Check("mapped-code-bounds-rejects-cross-end-span", !CodeEquals(image, imageSize, static_cast<std::uint32_t>(imageSize - 1), contract::kCandidatePrologue, sizeof(contract::kCandidatePrologue)));
    DWORD oldProtection = 0;
    const auto guardedPage = contract::kCandidateRva & ~std::uint32_t(0xFFF);
    const bool madeUnreadable = VirtualProtect(image + guardedPage, 0x1000, PAGE_NOACCESS, &oldProtection) != FALSE;
    Check("mapped-code-read-fault-fails-closed", madeUnreadable && !VerifyMappedImageContract(image, imageSize));
    DWORD ignoredProtection = 0;
    const bool restored = madeUnreadable && VirtualProtect(image + guardedPage, 0x1000, oldProtection, &ignoredProtection);
    Check("mapped-code-readable-again-validates", restored && VerifyMappedImageContract(image, imageSize));
    Check("mapped-verifier-never-publishes-global-image", g_dwmcore == beforeBase && g_imageSize == beforeSize);
    std::printf("Mapped contract mutation coverage: %zu bytes in %zu spans.\n", mutatedBytes, sizeof(kMappedTestSpans) / sizeof(kMappedTestSpans[0]));
    VirtualFree(image, 0, MEM_RELEASE);

    wchar_t corePath[MAX_PATH]{};
    const UINT systemLength = GetSystemDirectoryW(corePath, MAX_PATH);
    const bool gotPath = systemLength && systemLength < MAX_PATH - 14 && wcscat_s(corePath, L"\\dwmcore.dll") == 0;
    HANDLE file = gotPath ? CreateFileW(corePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr) : INVALID_HANDLE_VALUE;
    HANDLE mapping = file != INVALID_HANDLE_VALUE ? CreateFileMappingW(file, nullptr,
        PAGE_READONLY | SEC_IMAGE_NO_EXECUTE, 0, 0, nullptr) : nullptr;
    const auto* view = mapping ? static_cast<const std::uint8_t*>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0)) : nullptr;
    Check("system-file-no-execute-view-created", view != nullptr);
    std::uint8_t digest[32]{};
    const bool selectedFile = gotPath && Sha256File(corePath, digest) &&
        std::memcmp(digest, contract::kDwmcoreSha256, sizeof(digest)) == 0;
#if HDR_SELECTIVE_DWMCORE_BUILD == 261009278
    Check("candidate-test-system-file-is-exact-9278", selectedFile);
    Check("real-mapped-verifier-accepts-actual-9278-no-execute-view",
        selectedFile && view && VerifyMappedImageContract(view, contract::kMappedImageSize));
#else
    if (!selectedFile)
        Check("default-contract-rejects-different-system-file", view && !VerifyMappedImageContract(view, imageSize));
    else
        std::printf("SKIP actual 8875 image-size acceptance: no independent 8875 size contract.\n");
#endif
    if (view) UnmapViewOfFile(view);
    if (mapping) CloseHandle(mapping);
    if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
}

void NativeProloguePreparationTests() {
    // Never execute these copied bytes or enable a hook. Test the actual
    // MinHook instruction decoder/trampoline builder on both native prologues.
    auto* ownedCode = static_cast<std::uint8_t*>(VirtualAlloc(nullptr, 0x2000,
        MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    Check("native-prologue-owned-allocation", ownedCode != nullptr);
    if (!ownedCode) return;
    std::memcpy(ownedCode, contract::kCandidatePrologue, sizeof(contract::kCandidatePrologue));
    std::memcpy(ownedCode + 0x1000, contract::kIndependentFlipPrologue, sizeof(contract::kIndependentFlipPrologue));
    DWORD previous = 0;
    const bool executable = VirtualProtect(ownedCode, 0x2000, PAGE_EXECUTE_READ, &previous) != FALSE;
    const bool initialized = executable && MH_Initialize() == MH_OK;
    Check("native-prologue-local-Minhook-initialized", initialized);
    if (initialized) {
        void* candidateTrampoline = nullptr;
        void* flipTrampoline = nullptr;
        const auto candidate = MH_CreateHook(ownedCode, reinterpret_cast<void*>(&NativeCandidate), &candidateTrampoline);
        const auto flip = MH_CreateHook(ownedCode + 0x1000, reinterpret_cast<void*>(&NativeFlip), &flipTrampoline);
        Check("native-candidate-prologue-trampoline-prepared", candidate == MH_OK && candidateTrampoline &&
            std::memcmp(candidateTrampoline, contract::kCandidatePrologue, 7) == 0);
        Check("native-flip-prologue-trampoline-prepared", flip == MH_OK && flipTrampoline &&
            std::memcmp(flipTrampoline, contract::kIndependentFlipPrologue, 5) == 0);
        Check("native-prologue-preparation-does-not-enable-detours",
            std::memcmp(ownedCode, contract::kCandidatePrologue, sizeof(contract::kCandidatePrologue)) == 0 &&
            std::memcmp(ownedCode + 0x1000, contract::kIndependentFlipPrologue, sizeof(contract::kIndependentFlipPrologue)) == 0);
        Check("native-prologue-local-Minhook-cleanup", MH_Uninitialize() == MH_OK);
    }
    VirtualFree(ownedCode, 0, MEM_RELEASE);
}
