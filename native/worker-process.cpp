#include "process-support.h"
#include <algorithm>
#include <cstring>
#include <string>
#include <thread>

// Stable C ABI, uint32 fields on both sides. Kind: 0 exited, 1 launch/system
// failure, 2 cancelled, 3 timeout, 4 output limit, 5 cleanup not proven.
struct RunResult { DWORD kind, exitCode, error, processId, stdoutSize, stderrSize; };
namespace {
struct Owner {
    dbe::Handle job, outRead, outWrite, errRead, errWrite;
    std::thread outThread, errThread;
    ~Owner() {
        // Also used on exceptions during thread construction or process launch.
        job.close();
        outWrite.close(); errWrite.close();
        if (outThread.joinable()) outThread.join();
        if (errThread.joinable()) errThread.join();
    }
};
bool pipe(dbe::Handle& read, dbe::Handle& write) {
    SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
    HANDLE r = nullptr, w = nullptr;
    if (!CreatePipe(&r, &w, &security, 0)) return false;
    read.reset(r); write.reset(w);
    return SetHandleInformation(r, HANDLE_FLAG_INHERIT, 0) != FALSE;
}
void capture(HANDLE pipeHandle, unsigned char* output, DWORD capacity, DWORD* used, HANDLE overflow) {
    unsigned char buffer[16384]; DWORD received = 0;
    while (ReadFile(pipeHandle, buffer, sizeof(buffer), &received, nullptr) && received) {
        DWORD take = (std::min)(received, capacity - *used);
        if (take) std::memcpy(output + *used, buffer, take);
        *used += take;
        if (take != received) SetEvent(overflow);
    }
}
void execute(const wchar_t* exe, const wchar_t* cwd, const wchar_t* args, const wchar_t* environment,
             HANDLE cancel, DWORD timeoutMs, DWORD memoryMiB, unsigned char* out, unsigned char* err,
             DWORD capacity, RunResult& result) {
    result.kind = 1;
    auto failure = [&] { result.error = GetLastError(); };
    if (!exe || !cwd || !args || !out || !err || !capacity || !cancel) {
        result.error = ERROR_INVALID_PARAMETER; return;
    }
    if (WaitForSingleObject(cancel, 0) == WAIT_OBJECT_0) { result.kind = 2; return; }
    dbe::Handle overflow(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    Owner owner;
    owner.job.reset(CreateJobObjectW(nullptr, nullptr));
    if (!owner.job.valid()) { failure(); return; }
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE |
        JOB_OBJECT_LIMIT_JOB_MEMORY | JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
    limits.BasicLimitInformation.ActiveProcessLimit = 8;
    limits.JobMemoryLimit = static_cast<SIZE_T>(memoryMiB) * 1024 * 1024;
    if (!SetInformationJobObject(owner.job.get(), JobObjectExtendedLimitInformation, &limits, sizeof(limits)) ||
        !pipe(owner.outRead, owner.outWrite) || !pipe(owner.errRead, owner.errWrite)) { failure(); return; }
    SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
    dbe::Handle input(CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, &security, OPEN_EXISTING, 0, nullptr));
    if (!input.valid() || !overflow.valid()) { failure(); return; }
    dbe::Attributes attributes;
    HANDLE job = owner.job.get();
    HANDLE inherited[] = {input.get(), owner.outWrite.get(), owner.errWrite.get()};
    if (!attributes.initialize(2) ||
        !UpdateProcThreadAttribute(attributes.get(), 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, &job, sizeof(job), nullptr, nullptr) ||
        !UpdateProcThreadAttribute(attributes.get(), 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited, sizeof(inherited), nullptr, nullptr)) {
        failure(); return;
    }
    std::wstring command = L"\"" + std::wstring(exe) + L"\" " + args;
    if (command.size() >= 32767) { result.error = ERROR_FILENAME_EXCED_RANGE; return; }
    STARTUPINFOEXW startup{}; startup.StartupInfo.cb = sizeof(startup);
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput = input.get();
    startup.StartupInfo.hStdOutput = owner.outWrite.get();
    startup.StartupInfo.hStdError = owner.errWrite.get();
    startup.lpAttributeList = attributes.get();
    PROCESS_INFORMATION child{};
    if (!CreateProcessW(exe, command.data(), nullptr, nullptr, TRUE,
        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS,
        const_cast<wchar_t*>(environment), cwd, &startup.StartupInfo, &child)) { failure(); return; }
    dbe::Handle process(child.hProcess), thread(child.hThread);
    result.processId = child.dwProcessId;
    owner.outWrite.close(); owner.errWrite.close();
    owner.outThread = std::thread(capture, owner.outRead.get(), out, capacity, &result.stdoutSize, overflow.get());
    owner.errThread = std::thread(capture, owner.errRead.get(), err, capacity, &result.stderrSize, overflow.get());
    HANDLE waits[] = {cancel, overflow.get(), process.get()};
    DWORD wait = WaitForMultipleObjects(3, waits, FALSE, timeoutMs);
    if (wait == WAIT_OBJECT_0) result.kind = 2;
    else if (wait == WAIT_OBJECT_0 + 1) result.kind = 4;
    else if (wait == WAIT_TIMEOUT) result.kind = 3;
    else if (wait == WAIT_OBJECT_0 + 2) {
        if (GetExitCodeProcess(process.get(), &result.exitCode)) result.kind = 0;
        else failure();
    } else failure();
    // Success of the compiler is not proof that all descendants have exited.
    if (!TerminateJobObject(job, ERROR_CANCELLED)) { result.kind = 5; failure(); return; }
    const ULONGLONG deadline = GetTickCount64() + 10000;
    for (;;) {
        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info{};
        if (!QueryInformationJobObject(job, JobObjectBasicAccountingInformation, &info, sizeof(info), nullptr)) {
            result.kind = 5; failure(); return;
        }
        if (!info.ActiveProcesses) break;
        if (GetTickCount64() >= deadline) { result.kind = 5; result.error = WAIT_TIMEOUT; return; }
        Sleep(10);
    }
    owner.outThread.join(); owner.errThread.join();
    if (WaitForSingleObject(overflow.get(), 0) == WAIT_OBJECT_0) result.kind = 4;
}
}
extern "C" __declspec(dllexport) void __cdecl DbeRun(
    const wchar_t* exe, const wchar_t* cwd, const wchar_t* args, const wchar_t* environment,
    HANDLE cancel, DWORD timeoutMs, DWORD memoryMiB, unsigned char* out, unsigned char* err,
    DWORD capacity, RunResult* result) noexcept {
    if (!result) return;
    *result = {};
    try { execute(exe, cwd, args, environment, cancel, timeoutMs, memoryMiB, out, err, capacity, *result); }
    // Unexpected exceptions do not prove completion of every owned descendant.
    catch (...) { result->kind = 5; result->error = ERROR_NOT_ENOUGH_MEMORY; }
}
