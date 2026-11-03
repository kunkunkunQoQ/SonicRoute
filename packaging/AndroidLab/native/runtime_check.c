#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>

static int decode(enum AVCodecID id, const char *path, int image) {
    const AVCodec *codec = avcodec_find_decoder(id);
    AVCodecContext *ctx = codec ? avcodec_alloc_context3(codec) : NULL;
    AVPacket *packet = av_packet_alloc();
    AVFrame *frame = av_frame_alloc();
    AVFormatContext *format = NULL;
    FILE *input = NULL;
    int ok = 0;
    if (!ctx || !packet || !frame) goto done;
    ctx->thread_count = 1;
    if (image) {
        if (avformat_open_input(&format, path, NULL, NULL) < 0 || avformat_find_stream_info(format, NULL) < 0
            || avcodec_parameters_to_context(ctx, format->streams[0]->codecpar) < 0
            || av_read_frame(format, packet) < 0) goto done;
    } else {
        input = fopen(path, "rb");
        if (!input || fseek(input, 0, SEEK_END) != 0) goto done;
        long size = ftell(input);
        if (size <= 0 || size > 1024 * 1024 || fseek(input, 0, SEEK_SET) != 0 || av_new_packet(packet, (int)size) < 0
            || fread(packet->data, 1, (size_t)size, input) != (size_t)size) goto done;
    }
    if (avcodec_open2(ctx, codec, NULL) < 0 || avcodec_send_packet(ctx, packet) < 0) goto done;
    int result = avcodec_receive_frame(ctx, frame);
    if (result == AVERROR(EAGAIN)) {
        if (avcodec_send_packet(ctx, NULL) < 0) goto done;
        result = avcodec_receive_frame(ctx, frame);
    }
    if (result < 0 || frame->width <= 0 || frame->height <= 0) goto done;
    if (!image && (frame->width != 64 || frame->height != 128)) goto done;
    printf("PASS %s decodes %dx%d, format=%d\n", codec->name, frame->width, frame->height, frame->format);
    ok = 1;
done:
    if (input) fclose(input);
    avformat_close_input(&format);
    av_frame_free(&frame);
    av_packet_free(&packet);
    avcodec_free_context(&ctx);
    return ok;
}
int main(void) {
    if (!decode(AV_CODEC_ID_H264, "sample.h264", 0) || !decode(AV_CODEC_ID_HEVC, "sample.hevc", 0)
        || !decode(AV_CODEC_ID_PNG, "icon.png", 1)) return 1;
    printf("PASS dynamically loaded codec license: %s\n", avcodec_license());
    return 0;
}
