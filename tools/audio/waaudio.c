/*
 * waaudio.c - the question the call path cannot answer from managed code:
 * does Media Foundation on this phone expose a capture device, and in what
 * format does it hand samples over?
 *
 * Why a native DLL and not MediaCapture: the managed `MediaCapture` of WP8.1
 * records to a file and nothing else. There is no managed callback that carries
 * PCM, `Microsoft.Xna.Framework.dll` on this SDK has no `Microphone` class, and
 * `audioclient.h` is not in the phone's SDK. Media Foundation is the one audio
 * surface that is both present on the phone (WhatsAppNative.dll imports it) and
 * callable from a DLL this app ships.
 *
 * Why it only reports: the decision the code needs is "is a capture device there
 * at all, and at what rate and channel count". Guessing it - hard-coding 16 kHz
 * mono, wiring an encoder behind it - would produce a call that is silent on the
 * device and impossible to tell from a signalling bug. So this probe starts
 * Media Foundation, enumerates the audio capture devices, opens the first one
 * through a source reader, reads the native media type and writes what it found
 * into a caller-supplied buffer. `WaAudioProbe` returns 0 only when every one of
 * those steps succeeded; otherwise it returns the number of the step that failed
 * and the buffer carries the HRESULT that failed with it.
 *
 * What it deliberately does not do: no `ReadSample`, no buffer allocation, no
 * thread. A probe that reads samples would need a lifetime and a queue, and the
 * only question here is reachability.
 *
 * Linking: same constraints as waopus.dll - the phone's loader refuses an image
 * that names desktop modules, so the entry point is this package's own
 * (`tools/opus/waentry.c`), the CRT is resolved through the API-set import
 * libraries, and the Media Foundation imports name the modules the phone ships
 * (`MFPlat.dll`, `Mfreadwrite.dll`) rather than a desktop-only surface.
 *
 * Build: tools\build-waudio-arm.bat. The managed side calls this from
 * `WhatsappApp/Services/CallAudioProbe.cs`.
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>

/*
 * Two declarations the Kits 10 headers only make for a desktop target: the
 * attribute identifiers and the enumerator that reads them sit behind the
 * desktop partition, and this image is built with
 * `WINAPI_FAMILY=WINAPI_FAMILY_APP`. Their values are not guessed - they are the
 * ones the header carries, copied with the argument order of `EXTERN_GUID` -
 * and the symbols come from the import libraries under `tools/audio/*.def`.
 */
static const GUID WaDeviceSourceType =
    { 0xc60ac5fe, 0x252a, 0x478f, { 0xa0, 0xef, 0xbc, 0x8f, 0xa5, 0xf7, 0xca, 0xd3 } };

static const GUID WaDeviceSourceTypeAudioCapture =
    { 0x14dd9a1c, 0x7cff, 0x41be, { 0xb1, 0xb9, 0xba, 0x1a, 0xc6, 0xec, 0xb5, 0x71 } };

extern "C" HRESULT MFEnumDeviceSources(
    IMFAttributes* attributes,
    IMFActivate*** devices,
    UINT32* count);

/* The buffer is wide text because the managed side logs it as a string, and the
 * diagnostic pipeline is UTF-16 end to end. */
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

/* Decimal without a CRT: the probe reports a sample rate and a channel count, and
 * neither is worth pulling printf into the image for. */
static int WaWriteNumber(wchar_t* buffer, int capacity, int at, unsigned long value)
{
    wchar_t digits[12];
    int count = 0;
    int index;

    if (value == 0)
    {
        digits[count++] = L'0';
    }
    else
    {
        while (value != 0 && count < 11)
        {
            digits[count++] = (wchar_t)(L'0' + (value % 10));
            value = value / 10;
        }
    }

    for (index = count - 1; index >= 0; index--)
    {
        if (at >= capacity - 1) return at;

        buffer[at++] = digits[index];
        buffer[at] = 0;
    }

    return at;
}

/* The HRESULT is the interesting half of every failure: it names the interface
 * that was missing rather than only the step that noticed. */
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

/*
 * 0  every step succeeded, and the buffer says rate and channels
 * 1  MFStartup failed
 * 2  MFCreateAttributes failed
 * 3  MFEnumDeviceSources failed
 * 4  no capture device was enumerated
 * 5  the device did not activate as a media source
 * 6  MFCreateSourceReaderFromMediaSource failed
 * 7  the device has no native audio media type to report
 */
extern "C" __declspec(dllexport) int WaAudioProbe(wchar_t* buffer, int capacity)
{
    HRESULT result;
    IMFAttributes* attributes = 0;
    IMFActivate** devices = 0;
    UINT32 deviceCount = 0;
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

    result = MFCreateAttributes(&attributes, 2);
    if (FAILED(result) || attributes == 0)
    {
        at = WaWrite(buffer, capacity, at, L"; attributes ");
        WaWriteCode(buffer, capacity, at, result);
        MFShutdown();
        return 2;
    }

    attributes->SetGUID(WaDeviceSourceType, WaDeviceSourceTypeAudioCapture);

    result = MFEnumDeviceSources(attributes, &devices, &deviceCount);
    if (FAILED(result))
    {
        at = WaWrite(buffer, capacity, at, L"; enumerate ");
        WaWriteCode(buffer, capacity, at, result);
        attributes->Release();
        MFShutdown();
        return 3;
    }

    at = WaWrite(buffer, capacity, at, L"; devices ");
    at = WaWriteNumber(buffer, capacity, at, deviceCount);

    if (deviceCount == 0 || devices == 0)
    {
        attributes->Release();
        CoTaskMemFree(devices);
        MFShutdown();
        return 4;
    }

    {
        IMFMediaSource* source = 0;
        IMFSourceReader* reader = 0;
        IMFMediaType* nativeType = 0;
        UINT32 rate = 0;
        UINT32 channels = 0;

        result = devices[0]->ActivateObject(__uuidof(IMFMediaSource), (void**)&source);
        if (FAILED(result) || source == 0)
        {
            at = WaWrite(buffer, capacity, at, L"; activate ");
            WaWriteCode(buffer, capacity, at, result);
            devices[0]->Release();
            CoTaskMemFree(devices);
            attributes->Release();
            MFShutdown();
            return 5;
        }

        result = MFCreateSourceReaderFromMediaSource(source, 0, &reader);
        if (FAILED(result) || reader == 0)
        {
            at = WaWrite(buffer, capacity, at, L"; reader ");
            WaWriteCode(buffer, capacity, at, result);
            source->Release();
            devices[0]->Release();
            CoTaskMemFree(devices);
            attributes->Release();
            MFShutdown();
            return 6;
        }

        result = reader->GetNativeMediaType(
            MF_SOURCE_READER_FIRST_AUDIO_STREAM,
            0,
            &nativeType);

        if (FAILED(result) || nativeType == 0)
        {
            at = WaWrite(buffer, capacity, at, L"; native type ");
            WaWriteCode(buffer, capacity, at, result);
            reader->Release();
            source->Release();
            devices[0]->Release();
            CoTaskMemFree(devices);
            attributes->Release();
            MFShutdown();
            return 7;
        }

        nativeType->GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, &rate);
        nativeType->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &channels);

        at = WaWrite(buffer, capacity, at, L"; rate ");
        at = WaWriteNumber(buffer, capacity, at, rate);
        at = WaWrite(buffer, capacity, at, L"; channels ");
        at = WaWriteNumber(buffer, capacity, at, channels);

        nativeType->Release();
        reader->Release();
        source->Release();
        devices[0]->Release();
        CoTaskMemFree(devices);
        attributes->Release();
        MFShutdown();

        return 0;
    }
}

/* Read by the managed side before it trusts the string: a stale DLL in the
 * package would answer with an older layout. */
extern "C" __declspec(dllexport) int WaAudioProbeVersion(void)
{
    return 1;
}
