/*
 * waopus.c - the one Opus call this app needs: an encoder, and nothing else.
 *
 * Why the wrapper and not libopus' own API straight from C#: P/Invoke cannot
 * carry a `va_list`, and `opus_encoder_ctl` is a vararg. Everything the app sets
 * is set here, once, at creation, so the managed side has three calls and no
 * option plumbing at all.
 *
 * Why an encoder only: WhatsApp voice notes are Ogg/Opus and this phone cannot
 * make Opus (WP8.1 has no encoder), while it plays back through its own player.
 * The decoder, the multistream API and the projection are not compiled in - see
 * tools/build-opus-arm.bat for the file list, which is the encoder's half.
 *
 * The build is a static CRT (/MT), so the DLL imports nothing from the C
 * runtime and cannot disagree with the one the phone ships.
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

/* What one frame is, so the managed side does not restate the constant. */
WA_EXPORT int wa_opus_frame_ms(void)
{
    return WA_FRAME_MS;
}
