#include "process-paths.h"
#include <stdio.h>

// Launched inside the supervisor's owned Job. This process alone installs the
// private namespace and creates the compiler with the original working directory.
int wmain(int argc, wchar_t** argv) {
    if (argc != 5) return ERROR_INVALID_PARAMETER;
    dbe::ProcessPaths paths;
    if (!paths.initialize(argv[1]) || !SetCurrentDirectoryW(argv[2])) {
        fwprintf(stderr, L"DBE path initialization failed. Win32=%lu\n", GetLastError()); return 125;
    }
    std::wstring command = L"\"" + std::wstring(argv[3]) + L"\" " + argv[4];
    if (command.size() >= 32767) return ERROR_FILENAME_EXCED_RANGE;
    STARTUPINFOW startup{sizeof(startup)};
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    startup.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
    startup.hStdError = GetStdHandle(STD_ERROR_HANDLE);
    PROCESS_INFORMATION child{};
    if (!CreateProcessW(argv[3], command.data(), nullptr, nullptr, TRUE, CREATE_SUSPENDED | CREATE_NO_WINDOW,
        nullptr, argv[2], &startup, &child)) {
        fwprintf(stderr, L"DBE mapped compiler creation failed. Win32=%lu\n", GetLastError()); return 125;
    }
    dbe::Handle process(child.hProcess), thread(child.hThread);
    if (!paths.install(process.get()) || ResumeThread(thread.get()) == static_cast<DWORD>(-1)) {
        DWORD error = GetLastError();
        TerminateProcess(process.get(), 125);
        WaitForSingleObject(process.get(), INFINITE);
        fwprintf(stderr, L"DBE compiler path installation failed. Win32=%lu\n", error); return 125;
    }
    if (WaitForSingleObject(process.get(), INFINITE) != WAIT_OBJECT_0) return 125;
    DWORD code = 125;
    if (!GetExitCodeProcess(process.get(), &code)) return 125;
    ExitProcess(code);
}
