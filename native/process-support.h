#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <vector>

namespace dbe {
class Handle {
public:
    explicit Handle(HANDLE value = nullptr) : value_(value) {}
    ~Handle() { close(); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    HANDLE get() const { return value_; }
    bool valid() const { return value_ && value_ != INVALID_HANDLE_VALUE; }
    void close() { if (valid()) CloseHandle(value_); value_ = nullptr; }
    void reset(HANDLE value) { close(); value_ = value; }
private:
    HANDLE value_;
};

class Attributes {
public:
    ~Attributes() { if (initialized_) DeleteProcThreadAttributeList(get()); }
    bool initialize(DWORD count) {
        SIZE_T bytes = 0;
        InitializeProcThreadAttributeList(nullptr, count, 0, &bytes);
        if (!bytes) return false;
        storage_.resize(bytes);
        initialized_ = InitializeProcThreadAttributeList(get(), count, 0, &bytes) != FALSE;
        return initialized_;
    }
    LPPROC_THREAD_ATTRIBUTE_LIST get() {
        return reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage_.data());
    }
private:
    std::vector<unsigned char> storage_;
    bool initialized_ = false;
};
}
