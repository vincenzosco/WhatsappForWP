/*
 * waopus.c - the Opus calls this app needs: an encoder, and a decoder.
 *
 * Why the wrapper and not libopus' own API straight from C#: P/Invoke cannot
 * carry a `va_list`, and `opus_encoder_ctl` is a vararg. Everything the app sets
 * is set here, once, at creation, so the managed side has three calls and no
 * option plumbing at all.
 *
 * Why both directions: WhatsApp voice notes are Ogg/Opus and this phone cannot
 * make or read Opus (WP8.1 plays MP3/AAC/AMR/WAV and nothing else). One goes out
 * as Ogg/Opus, and one that arrives is decoded to a WAV this phone plays.
 *
 * The build is a static CRT (/MT), so the DLL imports nothing from the C runtime
 * and cannot disagree with the one the phone ships. That alone was not enough:
 * on 4 November 2026 the phone answered "Unable to load DLL 'waopus.dll'"
 * (0x8007007E) for a file that was in the package and imported only KERNEL32.
 *
 * The first explanation was the header - a process inside an app container
 * loads only an image that declares
 * `IMAGE_DLLCHARACTERISTICS_APPCONTAINER` - and the link line passed
 * `/APPCONTAINER` for that reason. The phone still said 0x8007007E on 5 November
 * 2026, so the header was not the answer. Comparing this image with the three
 * native ones the phone does load (`WhatsAppNative.dll`,
 * `WhatsAppProxyStub.dll`, `dbghelp.dll`) said what was left: none of them
 * carries the app container bit, and none imports a single console or argv
 * function - while this one pulled in `WriteConsoleW`, `GetStdHandle`,
 * `SetStdHandle`, `GetCommandLineA` and `GetEnvironmentStringsW` from the
 * default `libcmt` entry point.
 *
 * The rebuild did both halves of that plan and measured them: the link no longer
 * passes `/APPCONTAINER` and takes `/ENTRY:WaDllMain` (see `waentry.c`), so
 * `dllCharacteristics` is now 0x0140 like the images that load - and the import
 * table is unchanged, `KERNEL32.dll` and its nine console and argv functions
 * included, because that entry forwards to `_DllMainCRTStartup` and `malloc`
 * needs the heap it initialises. What decides the surface is the CRT the toolset
 * links, so what is left is a toolset whose import libraries name the
 * `api-ms-win-core-*` sets. `node tools/check-waopus.js` reads the committed DLL
 * and refuses it until then; the table is in
 * `docs/superpowers/specs/2026-11-04-calls-audio-feasibility.md`.
 *
 * License: libopus itself is BSD (tools/opus/LICENSE, fetched with the sources);
 * this file is part of this repository.
 */

#include <string.h>
#include "opus.h"

#ifdef _WIN32
#define WA_EXPORT __declspec(dllexport)
#else
#define WA_EXPORT
#endif

/* The shape of this DLL, so the managed side can refuse a stranger. */
#define WA_OPUS_API 1

/* 20 ms is the frame WhatsApp's own recorder writes, and what its player expects
 * to find in the first packets of a note. */
#define WA_FRAME_MS 20

WA_EXPORT int wa_opus_api(void)
{
    return WA_OPUS_API;
}

/*
 * A fresh encoder for a recording, or 0 when the phone asked for a rate or a
 * channel count Opus does not have. No exception travels across this boundary:
 * the caller distinguishes "no encoder" from "this frame failed".
 */
WA_EXPORT void* wa_opus_create(int sample_rate, int channels, int bitrate)
{
    OpusEncoder* enc = 0;
    int error = OPUS_OK;

    if (sample_rate != 8000 && sample_rate != 12000 && sample_rate != 16000
        && sample_rate != 24000 && sample_rate != 48000)
    {
        return 0;
    }

    if (channels != 1 && channels != 2) return 0;

    /* Voice and not music: the silence suppression and the pitch post-filter are
     * the difference between a note of thirty seconds and the same note at half
     * the bytes. */
    enc = opus_encoder_create(sample_rate, channels, OPUS_APPLICATION_VOIP, &error);
    if (error != OPUS_OK || enc == 0) return 0;

    opus_encoder_ctl(enc, OPUS_SET_BITRATE(bitrate > 0 ? bitrate : (channels == 1 ? 24000 : 32000)));

    /* Five and not ten: this is a 512 MB phone recording while the app is also
     * holding a conversation. The difference in quality at 24 kbit/s is small
     * and the difference in cycles is not. */
    opus_encoder_ctl(enc, OPUS_SET_COMPLEXITY(5));
    opus_encoder_ctl(enc, OPUS_SET_SIGNAL(OPUS_SIGNAL_VOICE));
    opus_encoder_ctl(enc, OPUS_SET_VBR(1));

    return enc;
}

/*
 * One packed Opus frame, or a negative Opus error. `samples_per_channel` is one
 * channel's worth (320 at 16 kHz for 20 ms), `out` must hold at least
 * `capacity` bytes - 1275 is the format's own ceiling and what the caller uses.
 */
WA_EXPORT int wa_opus_frame(void* handle, const short* pcm, int samples_per_channel,
    unsigned char* out, int capacity)
{
    OpusEncoder* enc = (OpusEncoder*)handle;

    if (enc == 0 || pcm == 0 || out == 0 || capacity <= 0) return OPUS_BAD_ARG;

    return opus_encode(enc, pcm, samples_per_channel, out, capacity);
}

WA_EXPORT void wa_opus_destroy(void* handle)
{
    if (handle != 0) opus_encoder_destroy((OpusEncoder*)handle);
}

/*
 * The decoder: a note that arrives is Ogg/Opus, and this phone has no Opus
 * decoder either. `src/opus_decoder.c` is already in the DLL (the encoder's
 * packet arithmetic lives there), so the other direction costs these three
 * functions and nothing else.
 *
 * The rate is asked for, not inherited: decoding 48 kHz opus to 16 kHz is
 * libopus resampling on the fly, and a WAV of a thirty-second note is then
 * 960 KB instead of 2.9 MB on a phone with 512 MB.
 */
WA_EXPORT void* wa_opus_decode_create(int sample_rate, int channels)
{
    OpusDecoder* dec = 0;
    int error = OPUS_OK;

    if (sample_rate != 8000 && sample_rate != 12000 && sample_rate != 16000
        && sample_rate != 24000 && sample_rate != 48000)
    {
        return 0;
    }

    if (channels != 1 && channels != 2) return 0;

    dec = opus_decoder_create(sample_rate, channels, &error);
    if (error != OPUS_OK || dec == 0) return 0;

    return dec;
}

/*
 * One packet, decoded into `pcm`, which must hold `capacity_per_channel`
 * samples for every channel. The answer is the samples per channel written, or
 * a negative Opus error.
 */
WA_EXPORT int wa_opus_decode(void* handle, const unsigned char* packet, int length,
    short* pcm, int capacity_per_channel)
{
    OpusDecoder* dec = (OpusDecoder*)handle;

    if (dec == 0 || packet == 0 || length <= 0 || pcm == 0 || capacity_per_channel <= 0)
    {
        return OPUS_BAD_ARG;
    }

    return opus_decode(dec, packet, length, pcm, capacity_per_channel, 0);
}

WA_EXPORT void wa_opus_decode_destroy(void* handle)
{
    if (handle != 0) opus_decoder_destroy((OpusDecoder*)handle);
}

/* What one frame is, so the managed side does not restate the constant. */
WA_EXPORT int wa_opus_frame_ms(void)
{
    return WA_FRAME_MS;
}
