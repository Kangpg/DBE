// Test-only process used for contracts which a fast real compiler cannot
// deterministically exercise (binary I/O, DWORD exit status, live descendants).
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <string>
#include <vector>

void write(HANDLE output, const void* bytes, DWORD length) {
    DWORD done = 0;
    if (!WriteFile(output, bytes, length, &done, nullptr) || done != length) ExitProcess(90);
}
void wide(const wchar_t* value) {
    write(GetStdHandle(STD_OUTPUT_HANDLE), value, static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t)));
}
int wmain(int argc, wchar_t** argv) {
    if (argc > 1 && wcscmp(argv[1], L"--leaf") == 0) { Sleep(30000); return 0; }
    if (argc > 1 && wcscmp(argv[1], L"--linger") == 0) {
        std::vector<wchar_t> own(32768);
        if (!GetModuleFileNameW(nullptr, own.data(), static_cast<DWORD>(own.size()))) return 91;
        std::wstring command = L"\"" + std::wstring(own.data()) + L"\" --leaf";
        STARTUPINFOW startup{}; startup.cb = sizeof(startup);
        PROCESS_INFORMATION child{};
        if (!CreateProcessW(own.data(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &child)) return 92;
        std::printf("%lu %lu\n", GetCurrentProcessId(), child.dwProcessId);
        std::fflush(stdout);
        CloseHandle(child.hThread);
        WaitForSingleObject(child.hProcess, 30000);
        CloseHandle(child.hProcess);
        return 0;
    }
    if (argc > 1 && wcscmp(argv[1], L"--exit") == 0) ExitProcess(0xC000013A);
    // Direct and wrapped calls should have identical raw command lines after
    // substituting the compiler executable, as well as identical parsed args.
    wide(GetCommandLineW());
    for (int i = 1; i < argc; ++i) wide(argv[i]);
    std::vector<wchar_t> cwd(32768);
    if (!GetCurrentDirectoryW(static_cast<DWORD>(cwd.size()), cwd.data())) return 93;
    wide(cwd.data());
    wchar_t sentinel[64]{};
    if (!GetEnvironmentVariableW(L"DBE_TEST_SENTINEL", sentinel, 64)) return 94;
    wide(sentinel);
    char buffer[4096]; DWORD count = 0;
    while (ReadFile(GetStdHandle(STD_INPUT_HANDLE), buffer, sizeof(buffer), &count, nullptr) && count)
        write(GetStdHandle(STD_OUTPUT_HANDLE), buffer, count);
    const unsigned char diagnostic[] = {0, 255, 13, 10, 'E', 'R', 'R'};
    write(GetStdHandle(STD_ERROR_HANDLE), diagnostic, sizeof(diagnostic));
    return 37;
}
