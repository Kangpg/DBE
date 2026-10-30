#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <string>
int wmain(int argc, wchar_t** argv) {
    if (argc > 1 && wcscmp(argv[1], L"--args") == 0) {
        for (int i = 2; i < argc; ++i) {
            for (const wchar_t* p = argv[i]; *p; ++p) std::printf("%04x", static_cast<unsigned int>(*p));
            std::printf("\n");
        }
        return 0;
    }
    if (argc > 1 && wcscmp(argv[1], L"--leaf") == 0) { Sleep(60000); return 0; }
    if (argc > 1 && wcscmp(argv[1], L"--exit") == 0) ExitProcess(0xC000013A);
    if (argc > 1 && wcscmp(argv[1], L"--priority") == 0) { std::printf("%lu", GetPriorityClass(GetCurrentProcess())); return 0; }
    if (argc > 1 && wcscmp(argv[1], L"--flood") == 0) {
        char bytes[4096]{}; DWORD sent;
        for (int i = 0; i < 600; ++i) WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), bytes, sizeof(bytes), &sent, nullptr);
        return 0;
    }
    if (argc != 3 || wcscmp(argv[1], L"--linger") != 0) return 9;
    wchar_t own[32768];
    if (!GetModuleFileNameW(nullptr, own, 32768)) return 10;
    std::wstring command = L"\"" + std::wstring(own) + L"\" --leaf";
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION child{};
    if (!CreateProcessW(own, command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &child)) return 11;
    FILE* marker = nullptr;
    std::wstring pending = std::wstring(argv[2]) + L".pending";
    if (_wfopen_s(&marker, pending.c_str(), L"wb") || !marker) { TerminateProcess(child.hProcess, 12); return 12; }
    std::fprintf(marker, "%lu %lu", GetCurrentProcessId(), child.dwProcessId);
    std::fclose(marker);
    if (!MoveFileExW(pending.c_str(), argv[2], MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) { TerminateProcess(child.hProcess, 13); return 13; }
    CloseHandle(child.hThread);
    WaitForSingleObject(child.hProcess, INFINITE);
    CloseHandle(child.hProcess);
    return 0;
}
