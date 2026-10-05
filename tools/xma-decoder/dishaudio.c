// dishaudio.c -- native bridge for The Dishwasher public build.
//
//   * XMA/XMA2 decode -> PCM16, using a minimal dynamically-linked FFmpeg
//     (libavcodec + libavutil, LGPL v2.1+, built with --disable-everything
//     --enable-decoder=xma1,xma2; see build_ffmpeg.sh).
//   * Output writer: PCM16 WAV (short cues) or OGG Vorbis (long music/solo
//     tracks), the latter via statically-linked libvorbis/libogg (BSD).
//
// The C# importer parses the .xwb/.xsb containers and hands raw XMA packets
// here; all format-specific work stays in one small, reviewable place.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

#include <libavcodec/avcodec.h>
#include <libavutil/channel_layout.h>
#include <libavutil/frame.h>
#include <libavutil/mem.h>

#include <vorbis/vorbisenc.h>
#include <ogg/ogg.h>

#ifdef __ANDROID__
#include <android/log.h>
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, "DishwasherAudio", __VA_ARGS__)
#else
#define LOGI(...) do { fprintf(stderr, __VA_ARGS__); fprintf(stderr, "\n"); } while (0)
#endif

#define EXPORT __attribute__((visibility("default")))

// ------------------------------------------------------------------ helpers

static void put_u16le(uint8_t* p, uint16_t v) { p[0]=(uint8_t)v; p[1]=(uint8_t)(v>>8); }
static void put_u32le(uint8_t* p, uint32_t v) { p[0]=(uint8_t)v; p[1]=(uint8_t)(v>>8); p[2]=(uint8_t)(v>>16); p[3]=(uint8_t)(v>>24); }

// ---- OGG Vorbis writer ---------------------------------------------------

static int write_ogg(const int16_t* pcm, long samples, int channels, int rate, const char* path)
{
    FILE* f = fopen(path, "wb");
    if (!f) return -1;

    vorbis_info vi;  vorbis_info_init(&vi);
    // VBR quality ~0.4 (roughly q4 / ~128 kbps at 44.1k stereo), matching the
    // internal build's `ffmpeg -q:a 4` music set.
    if (vorbis_encode_init_vbr(&vi, channels, rate, 0.4f) != 0) { vorbis_info_clear(&vi); fclose(f); return -2; }

    vorbis_comment vc; vorbis_comment_init(&vc);
    vorbis_comment_add_tag(&vc, "ENCODER", "TheDishwasher public import");

    vorbis_dsp_state vd;  vorbis_analysis_init(&vd, &vi);
    vorbis_block vb;      vorbis_block_init(&vd, &vb);

    ogg_stream_state os;  ogg_stream_init(&os, 0x0d15ea5e);

    ogg_packet header, header_comm, header_code;
    vorbis_analysis_headerout(&vd, &vc, &header, &header_comm, &header_code);
    ogg_stream_packetin(&os, &header);
    ogg_stream_packetin(&os, &header_comm);
    ogg_stream_packetin(&os, &header_code);

    ogg_page og;
    while (ogg_stream_flush(&os, &og)) {
        if (fwrite(og.header, 1, og.header_len, f) != (size_t)og.header_len ||
            fwrite(og.body,   1, og.body_len,   f) != (size_t)og.body_len) { goto fail; }
    }

    {
        long pos = 0;
        const int CHUNK = 4096;
        while (pos < samples) {
            int n = (int)((samples - pos) < CHUNK ? (samples - pos) : CHUNK);
            float** buffer = vorbis_analysis_buffer(&vd, n);
            for (int i = 0; i < n; i++)
                for (int c = 0; c < channels; c++)
                    buffer[c][i] = pcm[(pos + i) * channels + c] / 32768.0f;
            vorbis_analysis_wrote(&vd, n);
            pos += n;

            while (vorbis_analysis_blockout(&vd, &vb) == 1) {
                vorbis_analysis(&vb, NULL);
                vorbis_bitrate_addblock(&vb);
                ogg_packet op;
                while (vorbis_bitrate_flushpacket(&vd, &op)) {
                    ogg_stream_packetin(&os, &op);
                    while (ogg_stream_pageout(&os, &og)) {
                        if (fwrite(og.header, 1, og.header_len, f) != (size_t)og.header_len ||
                            fwrite(og.body,   1, og.body_len,   f) != (size_t)og.body_len) { goto fail; }
                    }
                }
            }
        }
        // flush
        vorbis_analysis_wrote(&vd, 0);
        while (vorbis_analysis_blockout(&vd, &vb) == 1) {
            vorbis_analysis(&vb, NULL);
            vorbis_bitrate_addblock(&vb);
            ogg_packet op;
            while (vorbis_bitrate_flushpacket(&vd, &op)) {
                ogg_stream_packetin(&os, &op);
                while (ogg_stream_pageout(&os, &og)) {
                    if (fwrite(og.header, 1, og.header_len, f) != (size_t)og.header_len ||
                        fwrite(og.body,   1, og.body_len,   f) != (size_t)og.body_len) { goto fail; }
                }
            }
        }
        while (ogg_stream_flush(&os, &og)) {
            if (fwrite(og.header, 1, og.header_len, f) != (size_t)og.header_len ||
                fwrite(og.body,   1, og.body_len,   f) != (size_t)og.body_len) { goto fail; }
        }
    }

    ogg_stream_clear(&os);
    vorbis_block_clear(&vb);
    vorbis_dsp_clear(&vd);
    vorbis_comment_clear(&vc);
    vorbis_info_clear(&vi);
    fclose(f);
    return 0;

fail:
    ogg_stream_clear(&os);
    vorbis_block_clear(&vb);
    vorbis_dsp_clear(&vd);
    vorbis_comment_clear(&vc);
    vorbis_info_clear(&vi);
    fclose(f);
    return -3;
}

// ---- PCM16 WAV writer ----------------------------------------------------

static int write_wav_pcm16(const int16_t* pcm, long samples, int channels, int rate, const char* path)
{
    FILE* f = fopen(path, "wb");
    if (!f) return -1;
    uint32_t dataBytes = (uint32_t)(samples * channels * 2);
    uint8_t hdr[44];
    memcpy(hdr + 0,  "RIFF", 4);
    put_u32le(hdr + 4,  36 + dataBytes);
    memcpy(hdr + 8,  "WAVE", 4);
    memcpy(hdr + 12, "fmt ", 4);
    put_u32le(hdr + 16, 16);
    put_u16le(hdr + 20, 1);                       // PCM
    put_u16le(hdr + 22, (uint16_t)channels);
    put_u32le(hdr + 24, (uint32_t)rate);
    put_u32le(hdr + 28, (uint32_t)(rate * channels * 2));
    put_u16le(hdr + 32, (uint16_t)(channels * 2));
    put_u16le(hdr + 34, 16);
    memcpy(hdr + 36, "data", 4);
    put_u32le(hdr + 40, dataBytes);
    if (fwrite(hdr, 1, 44, f) != 44) { fclose(f); return -2; }
    if (dataBytes && fwrite(pcm, 1, dataBytes, f) != dataBytes) { fclose(f); return -3; }
    fclose(f);
    return 0;
}

// ------------------------------------------------------------------ decode

// Build the 34-byte XMA2WAVEFORMATEX extradata FFmpeg's xma2 decoder expects.
static void build_xma2_extradata(uint8_t ed[34], int channels, int64_t num_samples,
                                 uint32_t block_size, uint32_t block_count)
{
    int streams = (channels + 1) / 2;
    uint32_t mask = channels == 1 ? 0x4 : channels == 2 ? 0x3 : 0;
    memset(ed, 0, 34);
    put_u16le(ed + 0, (uint16_t)streams);
    put_u32le(ed + 2, mask);
    put_u32le(ed + 6, (uint32_t)(num_samples * channels * 2));  // samples encoded (PCM bytes)
    put_u32le(ed + 10, block_size);                              // XMA block size (seeking only)
    ed[31] = 4;                                                  // encoder version
    put_u16le(ed + 32, (uint16_t)block_count);
}

// Decode a raw XMA/XMA2 byte stream to interleaved s16. Caller frees *out.
static int xma_decode(const uint8_t* data, int size, int channels, int rate,
                      int64_t num_samples, int* out_samples, int16_t** out_pcm)
{
    *out_pcm = NULL; *out_samples = 0;
    const AVCodec* codec = avcodec_find_decoder(AV_CODEC_ID_XMA2);
    if (!codec) return -1;

    AVCodecContext* ctx = avcodec_alloc_context3(codec);
    if (!ctx) return -2;
    ctx->sample_rate = rate;
    av_channel_layout_default(&ctx->ch_layout, channels);
    ctx->extradata = (uint8_t*)av_mallocz(34 + AV_INPUT_BUFFER_PADDING_SIZE);
    if (!ctx->extradata) { avcodec_free_context(&ctx); return -3; }
    {
        uint32_t block_size = 0x10000;
        uint32_t block_count = (uint32_t)((size + block_size - 1) / block_size);
        build_xma2_extradata(ctx->extradata, channels, num_samples, block_size, block_count);
        ctx->extradata_size = 34;
    }
    if (avcodec_open2(ctx, codec, NULL) < 0) { avcodec_free_context(&ctx); return -4; }

    int16_t* pcm = NULL; long total = 0; long cap = 0;

    AVPacket* pkt = av_packet_alloc();
    if (!pkt) { avcodec_free_context(&ctx); return -5; }
    pkt->data = (uint8_t*)data;  // decode never writes to the input
    pkt->size = size;
    int ret = avcodec_send_packet(ctx, pkt);
    av_packet_free(&pkt);
    if (ret < 0) { avcodec_free_context(&ctx); return -6; }

    for (;;) {
        AVFrame* fr = av_frame_alloc();
        if (!fr) { free(pcm); avcodec_free_context(&ctx); return -7; }
        ret = avcodec_receive_frame(ctx, fr);
        if (ret == AVERROR(EAGAIN) || ret == AVERROR_EOF) { av_frame_free(&fr); break; }
        if (ret < 0) { av_frame_free(&fr); free(pcm); avcodec_free_context(&ctx); return -8; }

        int n = fr->nb_samples;
        int C = fr->ch_layout.nb_channels;
        if (C <= 0) C = channels;
        if (total + (long)n > cap) {
            cap = (total + n) * 2 + 8192;
            int16_t* np = (int16_t*)realloc(pcm, cap * sizeof(int16_t) * channels);
            if (!np) { av_frame_free(&fr); free(pcm); avcodec_free_context(&ctx); return -9; }
            pcm = np;
        }
        // decoder output is planar float (FLTP)
        for (int i = 0; i < n; i++) {
            for (int c = 0; c < channels; c++) {
                float v = 0.f;
                if (c < C && fr->extended_data && fr->extended_data[c])
                    v = ((float*)fr->extended_data[c])[i];
                int s = (int)lrintf(v * 32767.0f);
                if (s > 32767) s = 32767;
                if (s < -32768) s = -32768;
                pcm[(total + i) * channels + c] = (int16_t)s;
            }
        }
        total += n;
        av_frame_free(&fr);
    }
    // flush
    avcodec_send_packet(ctx, NULL);
    for (;;) {
        AVFrame* fr = av_frame_alloc();
        if (!fr) break;
        ret = avcodec_receive_frame(ctx, fr);
        if (ret == AVERROR(EAGAIN) || ret == AVERROR_EOF) { av_frame_free(&fr); break; }
        if (ret < 0) { av_frame_free(&fr); break; }
        int n = fr->nb_samples; int C = fr->ch_layout.nb_channels;
        if (C <= 0) C = channels;
        if (total + (long)n > cap) {
            cap = (total + n) * 2 + 8192;
            int16_t* np = (int16_t*)realloc(pcm, cap * sizeof(int16_t) * channels);
            if (!np) { av_frame_free(&fr); break; }
            pcm = np;
        }
        for (int i = 0; i < n; i++)
            for (int c = 0; c < channels; c++) {
                float v = (c < C && fr->extended_data && fr->extended_data[c]) ? ((float*)fr->extended_data[c])[i] : 0.f;
                int s = (int)lrintf(v * 32767.0f);
                if (s > 32767) s = 32767;
                if (s < -32768) s = -32768;
                pcm[(total + i) * channels + c] = (int16_t)s;
            }
        total += n;
        av_frame_free(&fr);
    }

    avcodec_free_context(&ctx);
    *out_pcm = pcm;
    *out_samples = (int)total;
    return 0;
}

// ------------------------------------------------------------------ public API

// Decode a raw XMA2 stream directly to an interleaved s16 buffer.
// Returns 0 on success; caller must release *out_pcm with dw_free().
EXPORT int dw_xma_decode(const uint8_t* data, int size, int channels, int rate,
                         long long num_samples, int* out_samples, int16_t** out_pcm)
{
    return xma_decode(data, size, channels, rate, (int64_t)num_samples, out_samples, out_pcm);
}

// Decode raw XMA2 and write the result as a playable file.
//   out_fmt: 0 = PCM16 WAV, 1 = OGG Vorbis
EXPORT int dw_xma_to_file(const uint8_t* data, int size, int channels, int rate,
                          long long num_samples, const char* out_path, int out_fmt)
{
    int samples = 0; int16_t* pcm = NULL;
    int r = xma_decode(data, size, channels, rate, (int64_t)num_samples, &samples, &pcm);
    if (r != 0) { LOGI("xma_decode failed: %d", r); return r; }
    if (!pcm || samples <= 0) { free(pcm); return -20; }

    r = (out_fmt == 1)
        ? write_ogg(pcm, samples, channels, rate, out_path)
        : write_wav_pcm16(pcm, samples, channels, rate, out_path);
    if (r != 0) LOGI("write failed (%d): %s", r, out_path);
    free(pcm);
    return r;
}

// Convert big-endian PCM16 (X360 XACT PCM waves) to a little-endian PCM16 WAV.
EXPORT int dw_pcm16be_to_wav(const uint8_t* data, int size, int channels, int rate, const char* out_path)
{
    long samples = size / (2 * channels);
    int16_t* pcm = (int16_t*)malloc(samples * channels * sizeof(int16_t));
    if (!pcm) return -1;
    for (long i = 0; i < samples * channels; i++)
        pcm[i] = (int16_t)((data[2*i] << 8) | data[2*i + 1]);  // BE -> host LE
    int r = write_wav_pcm16(pcm, samples, channels, rate, out_path);
    free(pcm);
    return r;
}

EXPORT void dw_free(void* p) { free(p); }

// Simple load probe (also lets C# log the bridge version).
EXPORT int dw_version(void) { return 1; }
