#pragma once
#include "process-support.h"
#include <winternl.h>
#include <array>
#include <string>
#include <vector>
#include <memory>

namespace dbe {
// Shared only by a dedicated launcher and its suspended owned compiler.
// No global DOS-device changes.
class ProcessPaths {
    Handle directory;
    std::vector<std::unique_ptr<Handle>> links;
    using CreateDirectory = NTSTATUS(NTAPI*)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES);
    using CreateLink = NTSTATUS(NTAPI*)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, PUNICODE_STRING);
    using SetInfo = NTSTATUS(NTAPI*)(HANDLE, ULONG, PVOID, ULONG);
    using StatusError = ULONG(NTAPI*)(NTSTATUS);
    static UNICODE_STRING unicode(std::wstring& value) {
        return {static_cast<USHORT>(value.size() * sizeof(wchar_t)),
            static_cast<USHORT>((value.size() + 1) * sizeof(wchar_t)), value.data()};
    }
    static bool status(NTSTATUS value) {
        if (value >= 0) return true;
        auto error = reinterpret_cast<StatusError>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlNtStatusToDosError"));
        SetLastError(error ? error(value) : ERROR_GEN_FAILURE);
        return false;
    }

public:
    bool install(HANDLE child) {
        auto set = reinterpret_cast<SetInfo>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtSetInformationProcess"));
        HANDLE handle = directory.get();
        if (!set || !handle) { SetLastError(ERROR_NOT_SUPPORTED); return false; }
        return status(set(child, 23, &handle, sizeof(handle)));
    }
    bool initialize(const wchar_t* mapping) {
        HMODULE nt = GetModuleHandleW(L"ntdll.dll");
        auto createDirectory = reinterpret_cast<CreateDirectory>(GetProcAddress(nt, "NtCreateDirectoryObject"));
        auto createLink = reinterpret_cast<CreateLink>(GetProcAddress(nt, "NtCreateSymbolicLinkObject"));
        if (!createDirectory || !createLink) { SetLastError(ERROR_NOT_SUPPORTED); return false; }
        std::array<std::wstring, 26> targets;
        for (int i = 0; i < 26; i++) {
            wchar_t drive[] = {static_cast<wchar_t>(L'A' + i), L':', 0}, target[32768];
            if (QueryDosDeviceW(drive, target, 32768)) targets[i] = target;
        }
        std::wstring text(mapping);
        size_t start = 0;
        std::array<bool, 26> seen{};
        while (start < text.size()) {
            size_t end = text.find(L'\n', start);
            std::wstring row = text.substr(start, end == std::wstring::npos ? end : end - start);
            // X=C:\owned\snapshot. Resolve the physical volume before installing the map.
            if (row.size() < 6 || row[0] < L'A' || row[0] > L'Z' || row[1] != L'=' ||
                row[2] < L'A' || row[2] > L'Z' || row[3] != L':' || row[4] != L'\\' || seen[row[0] - L'A']) {
                SetLastError(ERROR_INVALID_PARAMETER); return false;
            }
            wchar_t drive[] = {row[2], L':', 0}, volume[32768];
            if (!QueryDosDeviceW(drive, volume, 32768)) return false;
            targets[row[0] - L'A'] = std::wstring(volume) + row.substr(4);
            seen[row[0] - L'A'] = true;
            if (end == std::wstring::npos) break;
            start = end + 1;
        }
        OBJECT_ATTRIBUTES attributes;
        InitializeObjectAttributes(&attributes, nullptr, 0, nullptr, nullptr);
        HANDLE handle = nullptr;
        if (!status(createDirectory(&handle, 0xF000F, &attributes))) return false;
        directory.reset(handle);
        // Register the directory as a device map before inserting drive links,
        // so Windows maintains its drive lookup bitmap for existing letters.
        if (!install(GetCurrentProcess())) return false;
        for (int i = 0; i < 26; i++) {
            if (targets[i].empty()) continue;
            std::wstring name = {static_cast<wchar_t>(L'A' + i), L':'};
            auto n = unicode(name), target = unicode(targets[i]);
            InitializeObjectAttributes(&attributes, &n, OBJ_CASE_INSENSITIVE, directory.get(), nullptr);
            if (!status(createLink(&handle, 0xF0001, &attributes, &target))) return false;
            links.push_back(std::make_unique<Handle>(handle));
        }
        std::wstring global = L"GLOBALROOT", root = L"\\";
        auto gn = unicode(global), rn = unicode(root);
        InitializeObjectAttributes(&attributes, &gn, OBJ_CASE_INSENSITIVE, directory.get(), nullptr);
        if (!status(createLink(&handle, 0xF0001, &attributes, &rn))) return false;
        links.push_back(std::make_unique<Handle>(handle));
        return true;
    }
};
}
