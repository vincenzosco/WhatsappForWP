/*
 * waaudio.c - what Media Foundation on this phone actually is.
 *
 * Version 1 of this file enumerated capture devices through
 * `MFEnumDeviceSources` and opened one with `IMFSourceReader`. On 6 November 2026
 * the phone answered `0x8007007F` - "the specified procedure could not be found" -
 * which is a different sentence from the `0x8007007E` of 4-5 November: the module
 * was found, a procedure inside it was not. `WhatsAppNative.dll`, which is the only
 * proof of what this phone ships, imports exactly seven functions from
 * `MFPlat.DLL`:
 *
 *   MFStartup, MFShutdown, MFCreateSample, MFCreateMediaBufferWrapper,
 *   MFCreateAttributes, MFCreateMediaType, MFCreateMFByteStreamOnStream
 *
 * and it does not import `Mfreadwrite.dll` at all. So `MFEnumDeviceSources` and
 * `MFCreateSourceReaderFromMediaSource` are the two names that cannot be resolved
 * on this device, and the version 1 design was built on both.
 *
 * Version 2 asks only what the phone can answer: does Media Foundation initialise,
 * and are its object factories there. It calls `MFStartup`, `MFCreateAttributes`,
 * `MFCreateMediaType` and `MFCreateSample` and reports which one failed, with the
 * HRESULT. It does not read audio, and it does not pretend to: a probe that cannot
 * enumerate a device must say so rather than report success.
 *
 * The return value is the first step that failed, or 0 when all of them worked:
 *
 *   0  every step returned S_OK
 *   1  MFStartup failed
 *   2  MFCreateAttributes failed
 *   3  MFCreateMediaType failed
 *   4  MFCreateSample failed
 *
 * Linking: the same constraints as waopus.dll - the entry point is this package's
 * own (tools/opus/waentry.c), the CRT is reached through the API-set import
 * libraries, and the Media Foundation import names `MFPlat.dll`, the module the
 * phone ships for these calls.
 *
 * Build: tools/build-waudio-arm.bat. Managed side: CallAudioProbe.cs.
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfobjects.h>

/* The buffer is wide text because the diagnostic pipeline is UTF-16 end to end. */
static int WaWrite(wchar_t* buffer, int capacity, int at, const wchar_t* text)
{
    int index = 0;

    while (text[index] != 0 && at + index < capacity - 1)
    {
        buffer[at + index] = text[index];
        index++;
    }

    buffer[at + index] = 0;

    return at + index;
}

/* The HRESULT is the interesting half of every failure: it names what the phone
 * refused rather than only which call noticed. */
static int WaWriteCode(wchar_t* buffer, int capacity, int at, long code)
{
    static const wchar_t hex[] = L"0123456789abcdef";
    wchar_t text[11];
    unsigned long value = (unsigned long)code;
    int index;

    text[0] = L'0';
    text[1] = L'x';

    for (index = 0; index < 8; index++)
    {
        text[2 + index] = hex[(value >> (28 - 4 * index)) & 0xf];
    }

    text[10] = 0;

    return WaWrite(buffer, capacity, at, text);
}

extern "C" __declspec(dllexport) int WaAudioProbe(wchar_t* buffer, int capacity)
{
    HRESULT result;
    IMFAttributes* attributes = 0;
    IMFMediaType* mediaType = 0;
    IMFSample* sample = 0;
    int at = 0;

    if (buffer == 0 || capacity < 16) return -1;

    buffer[0] = 0;

    result = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(result))
    {
        at = WaWrite(buffer, capacity, at, L"mfstartup ");
        WaWriteCode(buffer, capacity, at, result);

        return 1;
    }

    at = WaWrite(buffer, capacity, at, L"mfstartup ok");

    result = MFCreateAttributes(&attributes, 1);
    if (FAILED(result) || attributes == 0)
    {
        at = WaWrite(buffer, capacity, at, L"; attributes ");
        WaWriteCode(buffer, capacity, at, result);
        MFShutdown();

        return 2;
    }

    at = WaWrite(buffer, capacity, at, L"; attributes ok");

    result = MFCreateMediaType(&mediaType);
    if (FAILED(result) || mediaType == 0)
    {
        at = WaWrite(buffer, capacity, at, L"; media type ");
        WaWriteCode(buffer, capacity, at, result);
        attributes->Release();
        MFShutdown();

        return 3;
    }

    at = WaWrite(buffer, capacity, at, L"; media type ok");

    result = MFCreateSample(&sample);
    if (FAILED(result) || sample == 0)
    {
        at = WaWrite(buffer, capacity, at, L"; sample ");
        WaWriteCode(buffer, capacity, at, result);
        mediaType->Release();
        attributes->Release();
        MFShutdown();

        return 4;
    }

    at = WaWrite(buffer, capacity, at, L"; sample ok");

    sample->Release();
    mediaType->Release();
    attributes->Release();
    MFShutdown();

    return 0;
}

/* Read by the managed side before it trusts the string: a stale DLL in the package
 * would answer with an older sentence. Version 2 is the surface above - the seven
 * functions the phone proves it has, not the enumerator it does not. */
extern "C" __declspec(dllexport) int WaAudioProbeVersion(void)
{
    return 2;
}
