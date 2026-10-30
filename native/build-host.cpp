#include "process-support.h"
#include <cstdio>
#include <filesystem>
#include <string>
#include <thread>

// The parent owns stdin's write end. Closing it (cancel or owner death) requests
// graceful cancellation before this host terminates its atomically owned job.
int wmain(int argc, wchar_t** argv) {
    if (argc != 4) { std::fprintf(stderr, "DBE host: expected MSBuild, response file, cancellation event\n"); return 125; }
    try {
        for (int i : {1, 2}) if (!std::filesystem::path(argv[i]).is_absolute() || wcschr(argv[i], L'"')) return 125;
        const std::wstring name = argv[3], prefix = L"Local\\DBE.Build.";
        if (name.size() != prefix.size() + 32 || name.compare(0, prefix.size(), prefix) != 0 ||
            name.find_first_not_of(L"0123456789abcdef", prefix.size()) != std::wstring::npos) return 125;
        dbe::Handle cancellation(CreateEventW(nullptr, TRUE, FALSE, name.c_str()));
        if (!cancellation.valid() || GetLastError() == ERROR_ALREADY_EXISTS) return 125;
        if (!SetEnvironmentVariableW(L"DBE_BUILD_CANCEL_EVENT", name.c_str())) return 125;
        dbe::Handle job(CreateJobObjectW(nullptr, nullptr));
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
        limits.BasicLimitInformation.ActiveProcessLimit = 128;
        if (!job.valid() || !SetInformationJobObject(job.get(), JobObjectExtendedLimitInformation, &limits, sizeof(limits))) return 125;
        SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
        dbe::Handle input(CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, &security, OPEN_EXISTING, 0, nullptr));
        dbe::Attributes attributes;
        HANDLE jobValue = job.get();
        HANDLE inherited[] = {input.get(), GetStdHandle(STD_OUTPUT_HANDLE), GetStdHandle(STD_ERROR_HANDLE)};
        if (!input.valid() || !attributes.initialize(2) ||
            !UpdateProcThreadAttribute(attributes.get(), 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, &jobValue, sizeof(jobValue), nullptr, nullptr) ||
            !UpdateProcThreadAttribute(attributes.get(), 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited, sizeof(inherited), nullptr, nullptr)) return 125;
        std::wstring command = L"\"" + std::wstring(argv[1]) + L"\" @\"" + argv[2] + L"\"";
        if (command.size() >= 32767) return 125;
        STARTUPINFOEXW startup{};startup.StartupInfo.cb = sizeof(startup);
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.StartupInfo.hStdInput = input.get();startup.StartupInfo.hStdOutput = inherited[1];startup.StartupInfo.hStdError = inherited[2];
        startup.lpAttributeList = attributes.get();
        PROCESS_INFORMATION child{};
        if (!CreateProcessW(argv[1], command.data(), nullptr, nullptr, TRUE,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS, nullptr, nullptr, &startup.StartupInfo, &child)) return 125;
        dbe::Handle process(child.hProcess), thread(child.hThread);
        std::thread owner([&] { char byte;DWORD count=0;ReadFile(GetStdHandle(STD_INPUT_HANDLE), &byte, 1, &count, nullptr);SetEvent(cancellation.get()); });
        HANDLE waits[] = {process.get(), cancellation.get()};
        DWORD wait = WaitForMultipleObjects(2, waits, FALSE, 2 * 60 * 60 * 1000);
        bool cancelled = wait != WAIT_OBJECT_0;
        if (cancelled) {SetEvent(cancellation.get());WaitForSingleObject(process.get(), 30000);}
        DWORD code = 125;
        if (WaitForSingleObject(process.get(), 0) == WAIT_OBJECT_0) GetExitCodeProcess(process.get(), &code);
        // Retry cancellation if the reader thread had not entered ReadFile yet.
        HANDLE reader = static_cast<HANDLE>(owner.native_handle());
        const ULONGLONG readerDeadline = GetTickCount64() + 10000;
        while (WaitForSingleObject(reader, 20) == WAIT_TIMEOUT) {
            CancelSynchronousIo(reader);
            if (GetTickCount64() >= readerDeadline) {std::fprintf(stderr, "DBE host: owner monitor cleanup unconfirmed\n");ExitProcess(125);}
        }
        owner.join();
        bool cleaned = TerminateJobObject(job.get(), ERROR_CANCELLED) != FALSE;
        const ULONGLONG deadline = GetTickCount64() + 10000;
        while (cleaned) {
            JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info{};
            if (!QueryInformationJobObject(job.get(), JobObjectBasicAccountingInformation, &info, sizeof(info), nullptr)) {cleaned=false;break;}
            if (!info.ActiveProcesses) break;
            if (GetTickCount64() >= deadline) {cleaned=false;break;}
            Sleep(20);
        }
        if (!cleaned) {std::fprintf(stderr, "DBE host: process-family cleanup unconfirmed; retain leases\n");return 125;}
        if (cancelled) {std::fprintf(stderr, "DBE host: cancelled; process family stopped\n");return 130;}
        return static_cast<int>(code);
    } catch (const std::exception& error) {std::fprintf(stderr, "DBE host: %s\n", error.what());return 125;}
}
