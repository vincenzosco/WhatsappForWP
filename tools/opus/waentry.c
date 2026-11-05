/*
 * waentry.c - the entry point waopus.dll declares on its own.
 *
 * Why this file exists: on 5 November 2026 the phone answered 0x8007007E to a
 * waopus.dll that was in the installed package, and comparing it with the three
 * ARM modules that phone does load (`WhatsAppNative.dll`,
 * `WhatsAppProxyStub.dll`, `dbghelp.dll`) left two differences. Those declare
 * `dllCharacteristics` 0x0140 and import `api-ms-win-core-*`; this DLL declared
 * `IMAGE_DLLCHARACTERISTICS_APPCONTAINER` and imported `KERNEL32.dll`, console
 * functions included. This file removes the first difference, and the link line
 * stops asking for the bit.
 *
 * The entry point is this DLL's own so the loader calls a function of ours
 * instead of the CRT's own start-up, and ours forwards to `_DllMainCRTStartup`.
 * The forward is not cosmetic: it is what initialises the heap, and libopus
 * allocates. A build with an entry point that *does not* forward produces a DLL
 * that loads and then answers "no encoder" for every recording, because `malloc`
 * has no heap to allocate from - a silent failure, which is worse than a file
 * that does not load at all.
 *
 * What the rebuild measured, read back with `node tools/check-waopus.js` and
 * `node .tools/pe-scan.js WhatsappApp/waopus.dll`: the entry point moved to
 * `WaDllMain`, `dllCharacteristics` went from 0x1140 to 0x0140 - the same flags
 * as the modules that load - and the import table still names `KERNEL32.dll`
 * with its console functions, because those come from the CRT objects this
 * toolset links and not from the entry point. Removing them needs a CRT built
 * against the phone's API sets, which is a different toolset, not a different
 * entry point.
 */

#include <windows.h>

/* The CRT's own start-up: declared here because the alternative is a DLL with
 * no heap. */
BOOL WINAPI _DllMainCRTStartup(HINSTANCE module, DWORD reason, LPVOID reserved);

/*
 * The loader's entry for this DLL. It exists so the image declares an entry
 * point of its own - the loader reads `AddressOfEntryPoint` before it resolves a
 * single import, and the value it finds is now a function in this repository
 * rather than a name in whatever CRT the toolset happened to bring.
 */
BOOL WINAPI WaDllMain(HINSTANCE module, DWORD reason, LPVOID reserved)
{
    return _DllMainCRTStartup(module, reason, reserved);
}
