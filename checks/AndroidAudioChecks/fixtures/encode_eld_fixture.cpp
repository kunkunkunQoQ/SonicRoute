#include "aacenc_lib.h"
#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <vector>

static void check(AACENC_ERROR result)
{
    if (result != AACENC_OK) {
        std::fprintf(stderr, "FDK error 0x%x\n", result);
        std::exit(1);
    }
}

int main(int argc, char **argv)
{
    if (argc != 5) return 2;
    const int rate = std::atoi(argv[1]), block = std::atoi(argv[2]), frames = std::atoi(argv[3]);
    FILE *file = std::fopen(argv[4], "wb");
    if (!file) return 3;
    HANDLE_AACENCODER encoder = nullptr;
    check(aacEncOpen(&encoder, 0, 2));
    check(aacEncoder_SetParam(encoder, AACENC_AOT, 39));
    check(aacEncoder_SetParam(encoder, AACENC_SAMPLERATE, rate));
    check(aacEncoder_SetParam(encoder, AACENC_GRANULE_LENGTH, block));
    check(aacEncoder_SetParam(encoder, AACENC_CHANNELMODE, MODE_2));
    check(aacEncoder_SetParam(encoder, AACENC_SBR_MODE, 0));
    check(aacEncoder_SetParam(encoder, AACENC_BITRATE, 144000));
    check(aacEncoder_SetParam(encoder, AACENC_TRANSMUX, TT_MP4_RAW));
    check(aacEncEncode(encoder, nullptr, nullptr, nullptr, nullptr));
    AACENC_InfoStruct info{};
    check(aacEncInfo(encoder, &info));
    if (info.frameLength != block) return 4;
    std::fputc(info.confSize, file);
    std::fwrite(info.confBuf, 1, info.confSize, file);
    std::vector<INT_PCM> pcm(block * 2);
    std::array<unsigned char, 4096> encoded{};
    for (int i = 0; i < frames; i++) {
        for (int j = 0; j < block; j++) {
            const double t = double(i * block + j) / rate;
            pcm[2*j] = short(6700 * std::sin(2 * 3.141592653589793 * 440 * t) + 2200 * std::sin(2 * 3.141592653589793 * 810 * t));
            pcm[2*j+1] = short(4800 * std::sin(2 * 3.141592653589793 * 660 * t) - 1500 * std::sin(2 * 3.141592653589793 * 1700 * t));
        }
        void *in = pcm.data(), *out = encoded.data();
        int inId = IN_AUDIO_DATA, outId = OUT_BITSTREAM_DATA;
        int inBytes = int(pcm.size() * sizeof(INT_PCM)), outBytes = int(encoded.size());
        int inSize = sizeof(INT_PCM), outSize = 1;
        AACENC_BufDesc input{1, &in, &inId, &inBytes, &inSize}, output{1, &out, &outId, &outBytes, &outSize};
        AACENC_InArgs inputArgs{block * 2, 0};
        AACENC_OutArgs outputArgs{};
        check(aacEncEncode(encoder, &input, &output, &inputArgs, &outputArgs));
        if (outputArgs.numOutBytes > 0) {
            std::uint16_t count = std::uint16_t(outputArgs.numOutBytes);
            std::fwrite(&count, sizeof count, 1, file);
            std::fwrite(encoded.data(), 1, count, file);
        }
    }
    aacEncClose(&encoder);
    std::fclose(file);
}
