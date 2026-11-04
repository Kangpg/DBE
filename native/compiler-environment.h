#pragma once
#include <windows.h>
#include <objbase.h>
#include <algorithm>
#include <string>
#include <vector>
#pragma comment(lib, "Ole32.lib")

namespace dbe {
// A shared MSPDBSRV can outlive its compiler and serve another Job. Closing
// the first Job would then kill the second Job's PDB service. Give every owned
// execution a private endpoint, shared by its /MP descendants only.
inline bool compilerEnvironment(const wchar_t* supplied, std::vector<wchar_t>& block) {
    struct Inherited {
        wchar_t* value = nullptr;
        ~Inherited() { if (value) FreeEnvironmentStringsW(value); }
    } inherited;
    if (!supplied) {
        inherited.value = GetEnvironmentStringsW();
        if (!inherited.value) return false;
        supplied = inherited.value;
    }
    GUID id{};
    HRESULT created = CoCreateGuid(&id);
    if (FAILED(created)) { SetLastError(static_cast<DWORD>(created)); return false; }
    wchar_t identifier[40]{};
    if (!StringFromGUID2(id, identifier, 40)) { SetLastError(ERROR_INVALID_DATA); return false; }
    constexpr wchar_t name[] = L"_MSPDBSRV_ENDPOINT_";
    constexpr size_t nameLength = (sizeof(name) / sizeof(wchar_t)) - 1;
    std::vector<std::wstring> entries;
    for (const wchar_t* entry = supplied; *entry; entry += wcslen(entry) + 1) {
        if (_wcsnicmp(entry, name, nameLength) == 0 && entry[nameLength] == L'=') continue;
        // Keep all other entries byte-for-byte, including Windows '=C:' drive
        // state. Do not round-trip the block through a managed dictionary.
        entries.emplace_back(entry);
    }
    entries.emplace_back(std::wstring(name) + L"=DBE.Job." + identifier);
    std::sort(entries.begin(), entries.end(), [](const auto& a, const auto& b) {
        return CompareStringOrdinal(a.c_str(), -1, b.c_str(), -1, TRUE) == CSTR_LESS_THAN;
    });
    block.clear();
    for (const auto& entry : entries) {
        block.insert(block.end(), entry.begin(), entry.end());
        block.push_back(L'\0');
    }
    block.push_back(L'\0');
    return true;
}
}
