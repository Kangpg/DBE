#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include <filesystem>
#include <string>
#include <variant>
#include <vector>
#include "process-support.h"

namespace {
using dbe::Handle;
using dbe::Attributes;

struct Failure { const char* stage; DWORD code; };
struct ExitStatus { DWORD code; };
using Result = std::variant<ExitStatus, Failure>;
Failure error(const char* stage) { return {stage, GetLastError()}; }

// argv[0] has the CRT's special quote rule. The remaining UTF-16 command
// line (including spacing, quoting and @response files) is never reparsed.
const wchar_t* argumentTail(const wchar_t* command) {
    bool quoted = false;
    while (*command) {
        if (*command == L'"') quoted = !quoted;
        else if (!quoted && (*command == L' ' || *command == L'\t')) break;
        ++command;
    }
    return command;
}

BOOL WINAPI consoleControl(DWORD event) {
    // The compiler receives the same console event. Keep the owner alive
    // while it exits; forced owner termination closes the kill-on-close job.
    return event == CTRL_C_EVENT || event == CTRL_BREAK_EVENT;
}

Result run() {
    const DWORD required = GetEnvironmentVariableW(L"DBE_REAL_CL", nullptr, 0);
    if (!required) return Failure{"DBE_REAL_CL must name an absolute compiler path", ERROR_BAD_ENVIRONMENT};
    std::wstring compiler(required, L'\0');
    const DWORD size = GetEnvironmentVariableW(L"DBE_REAL_CL", compiler.data(), required);
    if (!size || size >= required) return error("read DBE_REAL_CL");
    compiler.resize(size);
    if (!std::filesystem::path(compiler).is_absolute() || compiler.find(L'"') != std::wstring::npos)
        return Failure{"DBE_REAL_CL must be an unquoted absolute path", ERROR_BAD_PATHNAME};

    std::vector<wchar_t> ownPath(32768);
    const DWORD ownSize = GetModuleFileNameW(nullptr, ownPath.data(), static_cast<DWORD>(ownPath.size()));
    if (!ownSize || ownSize >= ownPath.size()) return error("resolve wrapper path");
    // Keep identity handles open through CreateProcess. Deny replacement while
    // checking; compare file IDs rather than case-folded paths or basenames.
    Handle self(CreateFileW(ownPath.data(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ,
                            nullptr, OPEN_EXISTING, 0, nullptr));
    if (!self.valid()) return error("open wrapper identity");
    Handle target(CreateFileW(compiler.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ,
                              nullptr, OPEN_EXISTING, 0, nullptr));
    if (!target.valid()) return error("open compiler identity");
    FILE_ID_INFO selfId{}, targetId{};
    if (!GetFileInformationByHandleEx(self.get(), FileIdInfo, &selfId, sizeof(selfId)) ||
        !GetFileInformationByHandleEx(target.get(), FileIdInfo, &targetId, sizeof(targetId)))
        return error("read compiler identity");
    if (selfId.VolumeSerialNumber == targetId.VolumeSerialNumber &&
        memcmp(&selfId.FileId, &targetId.FileId, sizeof(selfId.FileId)) == 0)
        return Failure{"compiler resolves to wrapper (recursive launch)", ERROR_INVALID_PARAMETER};

    Handle job(CreateJobObjectW(nullptr, nullptr));
    if (!job.valid()) return error("create compiler job");
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!SetInformationJobObject(job.get(), JobObjectExtendedLimitInformation, &limits, sizeof(limits)))
        return error("configure compiler job");

    // Preserve caller-selected inheritable handles, including MSVC's structured
    // diagnostics channel. Filtering to stdin/out/err silently loses compiler
    // errors under modern MSBuild. All wrapper-owned handles are non-inheritable.
    HANDLE jobValue = job.get();
    Attributes attributes;
    if (!attributes.initialize(1)) return error("initialize process attributes");
    // Atomic assignment avoids an unowned child if the wrapper is killed during launch.
    if (!UpdateProcThreadAttribute(attributes.get(), 0, PROC_THREAD_ATTRIBUTE_JOB_LIST,
                                    &jobValue, sizeof(jobValue), nullptr, nullptr))
        return error("attach compiler job attribute");
    const wchar_t* tail = argumentTail(GetCommandLineW());
    if (compiler.size() + 2 + wcslen(tail) >= 32767)
        return Failure{"compiler command exceeds Windows limit", ERROR_FILENAME_EXCED_RANGE};
    std::wstring command = L"\"" + compiler + L"\"" + tail;
    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup);
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    startup.StartupInfo.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
    startup.StartupInfo.hStdError = GetStdHandle(STD_ERROR_HANDLE);
    startup.lpAttributeList = attributes.get();
    if (!SetConsoleCtrlHandler(consoleControl, TRUE)) return error("register console control handler");
    PROCESS_INFORMATION child{};
    if (!CreateProcessW(compiler.c_str(), command.data(), nullptr, nullptr, TRUE,
                        EXTENDED_STARTUPINFO_PRESENT, nullptr, nullptr, &startup.StartupInfo, &child))
        return error("launch compiler");
    Handle process(child.hProcess), thread(child.hThread);
    if (WaitForSingleObject(process.get(), INFINITE) != WAIT_OBJECT_0) return error("wait for compiler");
    DWORD exitCode = 0;
    if (!GetExitCodeProcess(process.get(), &exitCode)) return error("read compiler exit code");
    return ExitStatus{exitCode};
}
}

int wmain() {
    DWORD code = 125;
    try {
        Result result = run();
        if (auto failure = std::get_if<Failure>(&result))
            std::fprintf(stderr, "DBE wrapper: %s (Win32 error %lu)\n", failure->stage, failure->code);
        else code = std::get<ExitStatus>(result).code;
    } catch (const std::exception& exception) {
        std::fprintf(stderr, "DBE wrapper: %s\n", exception.what());
    }
    // Preserve all 32 bits, including abnormal termination codes.
    ExitProcess(code);
}
