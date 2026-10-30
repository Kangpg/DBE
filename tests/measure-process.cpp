// Windows-only benchmark driver. Accounting comes from an outer Job Object,
// including nested wrapper/compiler descendants; memory is peak committed bytes.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <string>
#include <vector>

struct Handle {
    HANDLE value;
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};
std::wstring setting(const wchar_t* name) {
    DWORD size = GetEnvironmentVariableW(name, nullptr, 0);
    if (!size) return {};
    std::wstring value(size, L'\0');
    DWORD written = GetEnvironmentVariableW(name, value.data(), size);
    if (!written || written >= size) return {};
    value.resize(written);
    return value;
}
int wmain() {
    auto executable = setting(L"DBE_MEASURE_EXE");
    auto arguments = setting(L"DBE_MEASURE_ARGUMENTS");
    auto report = setting(L"DBE_MEASURE_REPORT");
    if (executable.empty() || report.empty()) return 90;
    DWORD timeout = 30000;
    auto timeoutText = setting(L"DBE_MEASURE_TIMEOUT_MS");
    if (!timeoutText.empty()) {
        try {
            size_t used = 0;
            auto value = std::stoull(timeoutText, &used);
            if (used != timeoutText.size() || !value || value >= INFINITE) return 90;
            timeout = static_cast<DWORD>(value);
        } catch (...) { return 90; }
    }
    Handle job{CreateJobObjectW(nullptr, nullptr)};
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!job.value || !SetInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) return 91;
    SIZE_T bytes = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes);
    std::vector<unsigned char> storage(bytes);
    auto attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
    if (!InitializeProcThreadAttributeList(attributes, 1, 0, &bytes)) return 92;
    if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, &job.value, sizeof(HANDLE), nullptr, nullptr)) {
        DeleteProcThreadAttributeList(attributes); return 93;
    }
    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup);
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    startup.StartupInfo.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
    startup.StartupInfo.hStdError = GetStdHandle(STD_ERROR_HANDLE);
    startup.lpAttributeList = attributes;
    std::wstring command = L"\"" + executable + L"\" " + arguments;
    LARGE_INTEGER frequency{}, start{}, end{};
    QueryPerformanceFrequency(&frequency); QueryPerformanceCounter(&start);
    PROCESS_INFORMATION child{};
    BOOL launched = CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, TRUE,
                                  EXTENDED_STARTUPINFO_PRESENT, nullptr, nullptr, &startup.StartupInfo, &child);
    DeleteProcThreadAttributeList(attributes);
    if (!launched) return 94;
    Handle process{child.hProcess}, thread{child.hThread};
    if (WaitForSingleObject(process.value, timeout) != WAIT_OBJECT_0) return 95;
    // Build completion may leave owned compiler service children (MSPDBSRV).
    // Stop only this measured family before collecting complete accounting.
    if (!TerminateJobObject(job.value, ERROR_CANCELLED)) return 96;
    // A completed root is insufficient when a descendant is still alive.
    JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting{};
    for (int i = 0; i < 500; ++i) {
        if (!QueryInformationJobObject(job.value, JobObjectBasicAccountingInformation, &accounting, sizeof(accounting), nullptr)) return 96;
        if (!accounting.ActiveProcesses) break;
        Sleep(10);
    }
    if (accounting.ActiveProcesses) return 97;
    QueryPerformanceCounter(&end);
    if (!QueryInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits), nullptr)) return 98;
    DWORD code = 0;
    if (!GetExitCodeProcess(process.value, &code)) return 99;
    FILE* file = nullptr;
    if (_wfopen_s(&file, report.c_str(), L"wb") != 0) return 100;
    std::fprintf(file, "{\"exitCode\":%lu,\"elapsedMs\":%.3f,\"cpuMs\":%.3f,\"peakFamilyCommitBytes\":%llu,\"processCount\":%lu}\n",
        code, (end.QuadPart - start.QuadPart) * 1000.0 / frequency.QuadPart,
        (accounting.TotalUserTime.QuadPart + accounting.TotalKernelTime.QuadPart) / 10000.0,
        static_cast<unsigned long long>(limits.PeakJobMemoryUsed), accounting.TotalProcesses);
    std::fclose(file);
    return code == 0 ? 0 : 101;
}
