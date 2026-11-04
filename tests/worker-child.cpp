#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <cstdio>
#include <string>
#include "../native/process-support.h"
namespace {
DWORD compile(const wchar_t* compiler, const wchar_t* sources, HANDLE job) {
    std::wstring command = L"\"" + std::wstring(compiler) + L"\" /nologo /c /Zi /FS /MP2 /GS- /X /Fdcompile.pdb " + sources;
    dbe::Attributes attributes;
    if (!attributes.initialize(1) || !UpdateProcThreadAttribute(attributes.get(), 0,
        PROC_THREAD_ATTRIBUTE_JOB_LIST, &job, sizeof(job), nullptr, nullptr)) return 20;
    STARTUPINFOEXW startup{}; startup.StartupInfo.cb = sizeof(startup);
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    startup.StartupInfo.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
    startup.StartupInfo.hStdError = GetStdHandle(STD_ERROR_HANDLE);
    startup.lpAttributeList = attributes.get();
    PROCESS_INFORMATION child{};
    if (!CreateProcessW(compiler, command.data(), nullptr, nullptr, TRUE, EXTENDED_STARTUPINFO_PRESENT,
        nullptr, nullptr, &startup.StartupInfo, &child)) return 21;
    CloseHandle(child.hThread);
    WaitForSingleObject(child.hProcess, INFINITE);
    DWORD code = 22; GetExitCodeProcess(child.hProcess, &code); CloseHandle(child.hProcess);
    return code;
}
int pdbSession(const wchar_t* compiler, const wchar_t* marker, const wchar_t* release) {
    dbe::Handle job(CreateJobObjectW(nullptr, nullptr));
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!job.valid() || !SetInformationJobObject(job.get(), JobObjectExtendedLimitInformation, &limits, sizeof(limits))) return 19;
    DWORD code = compile(compiler, L"first0.cpp first1.cpp", job.get());
    if (code) return static_cast<int>(code);
    // Find only a service in this process's Job; never capture/kill an ambient
    // user's service by name. The harness retains the returned process handle.
    DWORD broker = 0; ULONGLONG created = 0;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry);
    if (snapshot == INVALID_HANDLE_VALUE) return 23;
    for (BOOL more = Process32FirstW(snapshot, &entry); more; more = Process32NextW(snapshot, &entry)) {
        if (_wcsicmp(entry.szExeFile, L"mspdbsrv.exe") != 0) continue;
        HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ProcessID);
        if (!process) continue;
        BOOL owned = FALSE; FILETIME start{}, exit{}, kernel{}, user{};
        if (IsProcessInJob(process, job.get(), &owned) && owned && GetProcessTimes(process, &start, &exit, &kernel, &user)) {
            broker = entry.th32ProcessID;
            created = (static_cast<ULONGLONG>(start.dwHighDateTime) << 32) | start.dwLowDateTime;
        }
        CloseHandle(process);
    }
    CloseHandle(snapshot);
    wchar_t endpoint[256]{}; GetEnvironmentVariableW(L"_MSPDBSRV_ENDPOINT_", endpoint, 256);
    FILE* file = nullptr;
    const std::wstring pending = std::wstring(marker) + L".pending";
    if (_wfopen_s(&file, pending.c_str(), L"wb") || !file) return 24;
    std::fprintf(file, "%lu %llu\n%ls", broker, created, endpoint); std::fclose(file);
    if (!MoveFileExW(pending.c_str(), marker, MOVEFILE_WRITE_THROUGH)) return 25;
    const ULONGLONG until = GetTickCount64() + 30000;
    while (GetFileAttributesW(release) == INVALID_FILE_ATTRIBUTES) {
        if (GetTickCount64() >= until) return 26;
        Sleep(10);
    }
    return static_cast<int>(compile(compiler, L"second0.cpp second1.cpp", job.get()));
}
}
int wmain(int argc, wchar_t** argv) {
    if (argc == 5 && wcscmp(argv[1], L"--pdb-session") == 0) return pdbSession(argv[2], argv[3], argv[4]);
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
    if (argc > 1 && wcscmp(argv[1], L"--many") == 0) {
        wchar_t own[32768]; if (!GetModuleFileNameW(nullptr, own, 32768)) return 10;
        for (int i = 0; i < 12; ++i) {
            std::wstring command = L"\"" + std::wstring(own) + L"\" --leaf";
            STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION child{};
            if (!CreateProcessW(own, command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &child)) return 8;
            CloseHandle(child.hThread); CloseHandle(child.hProcess);
        }
        return 0; // The supervisor still has to join all twelve owned leaves.
    }
    if (argc > 1 && wcscmp(argv[1], L"--commit") == 0) {
        void* block = VirtualAlloc(nullptr, static_cast<SIZE_T>(384) * 1024 * 1024, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (!block) return ERROR_NOT_ENOUGH_MEMORY;
        VirtualFree(block, 0, MEM_RELEASE);
        return 0;
    }
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
