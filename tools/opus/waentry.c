/*
 * waentry.c - the loader glue for waopus.dll: its own entry point, its own
 * allocator, and no CRT start-up at all.
 *
 * History, because each step here was paid for:
 *
 * On 5 November 2026 the phone answered 0x8007007E to a waopus.dll that was in
 * the installed package, and comparing it with the three ARM modules that phone
 * does load (`WhatsAppNative.dll`, `WhatsAppProxyStub.dll`, `dbghelp.dll`) left
 * one difference that matters: they import `api-ms-win-core-*`, while this DLL
 * named `KERNEL32.dll` - the desktop surface - console functions included.
 *
 * A build with `/APPCONTAINER` off and `/ENTRY:WaDllMain` forwarding to
 * `_DllMainCRTStartup` changed `dllCharacteristics` to 0x0140 and left the
 * import table untouched, because the table is decided by the CRT objects the
 * toolset links, not by the entry point. The toolset on this machine has no
 * import libraries for the API sets (Kits 10 for ARM carries only
 * `api-ms-win-core-slapi-l1-1-0.lib`), so the way out is to stop needing the
 * CRT rather than to relink it:
 *
 *  - libopus is built with `FIXED_POINT`, which is what mobile builds of Opus
 *    use anyway. The float maths (`sin`, `cos`, `exp`, `log`, `pow`, `atan`)
 *    leave the image with the floating-point path.
 *  - the allocator below is `HeapAlloc` on the process heap, reached through the
 *    API set `api-ms-win-core-heap-l1-2-0.dll` - the same module and the same
 *    four functions `WhatsAppNative.dll` uses.
 *  - the string routines libopus needs are here, because the compiler emits
 *    calls to them and the CRT's versions would pull the CRT in.
 *  - the entry point does no CRT initialisation, so stdio, argv, the locale and
 *    the CRT heap are never touched, and their imports never appear.
 *
 * What this file exists to prevent: an entry point that forwards to
 * `_DllMainCRTStartup` keeps `malloc` working and the console imports with it;
 * an entry point that does nothing leaves `malloc` with no CRT heap, so the DLL
 * loads and answers "no encoder" for every recording - a silent failure. Read
 * with `node tools/check-waopus.js`, this image imports one API set and nothing
 * else.
 */

#include <windows.h>
#include <stddef.h>

/*
 * The compiler treats these as intrinsics and refuses a definition with C2169.
 * `function` turns the substitution off for them, which is what makes a local,
 * CRT-free implementation possible at all.
 */
#pragma function(memcpy, memset, memcmp, strlen, floor, log, log10, sin, cos)

void* memcpy(void* destination, const void* source, size_t count);
void* memmove(void* destination, const void* source, size_t count);
void* memset(void* destination, int value, size_t count);
int memcmp(const void* left, const void* right, size_t count);
size_t strlen(const char* text);

/* WaDllMain is the entry the link line names with /ENTRY. */
BOOL WINAPI WaDllMain(HINSTANCE module, DWORD reason, LPVOID reserved);

/*
 * The heap libopus allocates from. `GetProcessHeap` needs no initialisation -
 * that is the point of using it instead of the CRT's private heap - so a DLL
 * with no CRT start-up still has working allocations.
 */
void* malloc(size_t size)
{
    if (size == 0) size = 1;

    return HeapAlloc(GetProcessHeap(), 0, size);
}

void free(void* block)
{
    if (block != 0) HeapFree(GetProcessHeap(), 0, block);
}

void* realloc(void* block, size_t size)
{
    if (block == 0) return malloc(size);
    if (size == 0)
    {
        free(block);
        return 0;
    }

    return HeapReAlloc(GetProcessHeap(), 0, block, size);
}

void* calloc(size_t count, size_t size)
{
    size_t total = count * size;
    void* block = malloc(total);

    if (block != 0) memset(block, 0, total);

    return block;
}

/* The string routines the compiler emits calls to, and libopus uses by name. No
 * width tricks: the codec spends its cycles in CELT and SILK, not here. */
void* memcpy(void* destination, const void* source, size_t count)
{
    unsigned char* to = (unsigned char*)destination;
    const unsigned char* from = (const unsigned char*)source;

    while (count-- != 0) *to++ = *from++;

    return destination;
}

void* memmove(void* destination, const void* source, size_t count)
{
    unsigned char* to = (unsigned char*)destination;
    const unsigned char* from = (const unsigned char*)source;

    if (to < from)
    {
        while (count-- != 0) *to++ = *from++;
    }
    else
    {
        to += count;
        from += count;
        while (count-- != 0) *--to = *--from;
    }

    return destination;
}

void* memset(void* destination, int value, size_t count)
{
    unsigned char* to = (unsigned char*)destination;

    while (count-- != 0) *to++ = (unsigned char)value;

    return destination;
}

int memcmp(const void* left, const void* right, size_t count)
{
    const unsigned char* a = (const unsigned char*)left;
    const unsigned char* b = (const unsigned char*)right;

    while (count-- != 0)
    {
        if (*a != *b) return (int)*a - (int)*b;
        a++;
        b++;
    }

    return 0;
}

size_t strlen(const char* text)
{
    size_t length = 0;

    while (text[length] != 0) length++;

    return length;
}

/*
 * The little libm the encoder's analysis still wants. `FIXED_POINT` removes the
 * floating-point work from CELT and SILK, but `analysis.c` and the KISS FFT it
 * uses are float by nature, so `floor`, `sin`, `cos`, `log` and `log10` remain
 * in the image. These are deliberate approximations - a Taylor series for the
 * trig functions, an atanh series for the logarithm - and they are good to about
 * ten significant digits over the ranges the analysis feeds them. That is enough
 * for heuristics that choose a bitrate; it would not be enough to call the codec
 * bit-exact, and it is not meant to be. The alternative was linking the CRT's
 * libm, which brings back the whole desktop surface.
 */
double floor(double value)
{
    __int64 whole;

    if (!(value > -4.0e15 && value < 4.0e15)) return value;

    whole = (__int64)value;
    if ((double)whole > value) whole -= 1;

    return (double)whole;
}

double log(double value)
{
    union { double number; unsigned __int64 bits; } split;
    double mantissa;
    double scale;
    double square;
    double series;
    int exponent;
    int k;

    if (value <= 0) return -700.0;

    split.number = value;
    exponent = (int)((split.bits >> 52) & 0x7ff) - 1022;
    split.bits = (split.bits & 0x000fffffffffffffULL) | 0x3fe0000000000000ULL;
    mantissa = split.number;

    scale = (mantissa - 1.0) / (mantissa + 1.0);
    square = scale * scale;
    series = 0.0;

    for (k = 11; k >= 0; k--) series = series * square + 1.0 / (2 * k + 1);

    return 2.0 * scale * series + exponent * 0.6931471805599453;
}

double log10(double value)
{
    return log(value) / 2.302585092994046;
}

/* One turn, so the series below only ever sees [-pi, pi]. */
static double wrap(double radians)
{
    const double twoPi = 6.283185307179586;

    while (radians > 3.141592653589793) radians -= twoPi;
    while (radians < -3.141592653589793) radians += twoPi;

    return radians;
}

double sin(double radians)
{
    double x;
    double squared;
    double term;
    double sum;
    int n;

    x = wrap(radians);
    squared = x * x;
    term = x;
    sum = x;

    for (n = 1; n < 12; n++)
    {
        term = -term * squared / (double)((2 * n) * (2 * n + 1));
        sum += term;
    }

    return sum;
}

double cos(double radians)
{
    return sin(radians + 1.5707963267948966);
}

/*
 * The loader's entry. No forwarding to `_DllMainCRTStartup`: that call is what
 * puts stdio, argv and the console into the import table. Nothing here needs
 * per-thread or per-process initialisation - the heap is the process's own.
 */
BOOL WINAPI WaDllMain(HINSTANCE module, DWORD reason, LPVOID reserved)
{
    (void)module;
    (void)reason;
    (void)reserved;

    return TRUE;
}
