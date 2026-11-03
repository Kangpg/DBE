#include <windows.h>
#include <dia2.h>
#include <stdio.h>
#include <string>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")

template<class T> struct Com {
    T* p = nullptr;
    ~Com() { if (p) p->Release(); }
    T** out() { return &p; }
    T* operator->() const { return p; }
};
static std::string json(const wchar_t* value) {
    int length = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    std::string utf8(static_cast<size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value, -1, utf8.data(), length, nullptr, nullptr);
    std::string result = "\"";
    for (int i = 0; i < length - 1; i++) {
        unsigned char c = static_cast<unsigned char>(utf8[static_cast<size_t>(i)]);
        if (c == '\\' || c == '"') result += '\\';
        if (c < 32) { char escaped[7]; sprintf_s(escaped, "\\u%04x", c); result += escaped; }
        else result += static_cast<char>(c);
    }
    return result + "\"";
}
int wmain(int argc, wchar_t** argv) {
    if (argc < 4 || FAILED(CoInitializeEx(nullptr, COINIT_MULTITHREADED))) return 2;
    HMODULE library = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!library) return 3;
    auto create = reinterpret_cast<HRESULT(WINAPI*)(REFCLSID, REFIID, LPVOID*)>(GetProcAddress(library, "DllGetClassObject"));
    Com<IClassFactory> factory; Com<IDiaDataSource> source; Com<IDiaSession> session; Com<IDiaSymbol> global;
    if (!create || FAILED(create(__uuidof(DiaSource), IID_IClassFactory, reinterpret_cast<void**>(factory.out()))) ||
        FAILED(factory->CreateInstance(nullptr, __uuidof(IDiaDataSource), reinterpret_cast<void**>(source.out()))) ||
        FAILED(source->loadDataFromPdb(argv[2])) || FAILED(source->openSession(session.out())) ||
        FAILED(session->get_globalScope(global.out()))) return 4;
    GUID guid{}; DWORD age = 0; wchar_t guidText[64]{};
    global->get_guid(&guid); global->get_age(&age); StringFromGUID2(guid, guidText, 64);
    printf("{\"guid\":%s,\"age\":%lu,\"types\":[", json(guidText).c_str(), age);
    bool first = true;
    for (int i = 3; i < argc; i++) {
        Com<IDiaEnumSymbols> types;
        if (FAILED(global->findChildren(SymTagUDT, argv[i], nsCaseSensitive, types.out()))) return 5;
        ULONG count = 0; bool found = false;
        while (true) {
            Com<IDiaSymbol> type;
            if (types->Next(1, type.out(), &count) != S_OK || count != 1) break;
            ULONGLONG length = 0; type->get_length(&length);
            if (!length) continue;
            found = true;
            if (!first) printf(","); first = false;
            printf("{\"name\":%s,\"length\":%llu,\"members\":[", json(argv[i]).c_str(), length);
            Com<IDiaEnumSymbols> members;
            if (FAILED(type->findChildren(SymTagData, nullptr, nsNone, members.out()))) return 6;
            bool firstMember = true;
            while (true) {
                Com<IDiaSymbol> member;
                if (members->Next(1, member.out(), &count) != S_OK || count != 1) break;
                BSTR name = nullptr; LONG offset = 0; Com<IDiaSymbol> memberType; ULONGLONG size = 0;
                member->get_name(&name); member->get_offset(&offset);
                if (SUCCEEDED(member->get_type(memberType.out()))) memberType->get_length(&size);
                if (!firstMember) printf(","); firstMember = false;
                printf("{\"name\":%s,\"offset\":%ld,\"length\":%llu}", json(name ? name : L"").c_str(), offset, size);
                SysFreeString(name);
            }
            printf("]}");
        }
        if (!found) return 7;
    }
    printf("]}\n");
    return 0;
}
