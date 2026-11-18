# AirPlay AAC-ELD

SonicRoute's managed decoder uses MPEG-4 AAC codewords, band offsets, TNS
coefficients and ELD windows from FFmpeg n8.0's [`aactab.c`](https://github.com/FFmpeg/FFmpeg/blob/n8.0/libavcodec/aactab.c).
The ELD overlap equations in `AacEldFilterbank.cs` adapt the corresponding
filterbank in [`aacdec_dsp_template.c`](https://github.com/FFmpeg/FFmpeg/blob/n8.0/libavcodec/aac/aacdec_dsp_template.c).
The spectral decoding code and mixed-radix transform are implemented in C#.

Upstream copyright (c) 2005–2006 Oded Shimon, 2006–2007 Maxim Gavrilov,
2008–2013 Alex Converse, 2008–2010 Paul Kendall, 2010 Janne Grunau, and
2013 MIPS Technologies, Inc., California. The upstream source is distributed
without warranty under LGPL-2.1-or-later. Its complete license is in
[`FFmpeg-LGPL-2.1.txt`](FFmpeg-LGPL-2.1.txt).

The modified C# portions are provided under GPL-3.0-only pursuant to section 3
of the LGPL, with the [SonicRoute license](../../LICENSE). The Full application
ships the managed decoder and embeds this notice. It does not ship FFmpeg or
FDK AAC binaries for AirPlay. FDK AAC was used separately to encode synthetic
test signals and FFmpeg to independently decode the resulting fixtures.
