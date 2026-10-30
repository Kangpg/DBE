#include <array>
#include "value.h"
#if __has_include("optional.h")
#include "optional.h"
#else
#define DBE_OPTIONAL 0
#endif
static_assert(sizeof(DBE_TEXT) == 10);
int remote_value() {
    std::array<int, 1> value{DBE_VALUE};
    return value[0] + DBE_DELTA + DBE_OPTIONAL;
}
