// SPDX-License-Identifier: GPL-3.0-only
// The low-overlap window equations are adapted from FFmpeg's LGPL-2.1-or-later
// AAC decoder. See AacEld.NOTICE.md for authors, source, and license details.
using System.Numerics;

namespace SonicRoute.AirPlay;

internal sealed class AacEldFilterbank
{
    private readonly int _length;
    private readonly Complex[] _input, _spectrum, _preRotation, _postRotation, _roots;
    private readonly float[] _time;

    internal AacEldFilterbank(int length)
    {
        _length = length;
        int bins = length / 2;
        _input = new Complex[bins];
        _spectrum = new Complex[bins];
        _preRotation = new Complex[bins];
        _postRotation = new Complex[bins];
        _roots = new Complex[bins];
        _time = new float[length];
        for (int i = 0; i < bins; i++)
        {
            _preRotation[i] = Complex.FromPolarCoordinates(1, Math.PI * i / length);
            // The filterbank yields signed 16-bit amplitude units before PCM normalization.
            _postRotation[i] = Complex.FromPolarCoordinates(1.0 / (length * 32768), Math.PI * (i + 0.25) / length);
            _roots[i] = Complex.FromPolarCoordinates(1, 2 * Math.PI * i / bins);
        }
    }

    internal void Synthesize(ReadOnlySpan<float> spectral, Span<float> preceding, Span<float> pcm, int channel)
    {
        int n = _length, half = n / 2, quarter = n / 4;
        // Folding the alternating ELD spectrum into a DCT-IV needs N/2 complex bins.
        for (int i = 0; i < half; i++)
            _input[i] = new Complex(-spectral[n - 1 - 2 * i], -spectral[2 * i]) * _preRotation[i];
        InverseFft(_spectrum, 0, 1);
        for (int i = 0; i < half; i++)
        {
            var value = _spectrum[i] * _postRotation[i];
            _time[2 * i] = (float)-value.Imaginary;
            _time[n - 1 - 2 * i] = (float)value.Real;
        }

        // Each output sample is formed from the current transform and up to three preceding blocks.
        var window = n == 480 ? AacEldTables.Window480 : AacEldTables.Window512;
        for (int j = 0; j < quarter; j++)
        {
            float value = _time[quarter - 1 - j] * window[j] + preceding[3 * quarter + j] * window[n + j]
                - preceding[n + quarter - 1 - j] * window[2 * n + j] - preceding[2 * n + 3 * quarter + j] * window[3 * n + j];
            pcm[2 * j + channel] = Math.Clamp(value, -1f, 1f);
        }
        for (int i = 0; i < half; i++)
        {
            int j = quarter + i;
            float value = _time[i] * window[j] - preceding[n - 1 - i] * window[n + j]
                - preceding[n + i] * window[2 * n + j] + preceding[3 * n - 1 - i] * window[3 * n + j];
            pcm[2 * j + channel] = Math.Clamp(value, -1f, 1f);
        }
        for (int i = 0; i < quarter; i++)
        {
            int j = 3 * quarter + i;
            float value = _time[half + i] * window[j] - preceding[half - 1 - i] * window[n + j]
                - preceding[n + half + i] * window[2 * n + j];
            pcm[2 * j + channel] = Math.Clamp(value, -1f, 1f);
        }
        preceding[..(2 * n)].CopyTo(preceding[n..]);
        _time.AsSpan().CopyTo(preceding);
    }

    // Decimation in time with a reusable output buffer. 240 bins use radices 2, 3 and 5.
    private void InverseFft(Span<Complex> output, int start, int stride)
    {
        int count = output.Length;
        if (count == 1) { output[0] = _input[start]; return; }
        int radix = count % 2 == 0 ? 2 : count % 3 == 0 ? 3 : 5;
        int group = count / radix, rootStride = _roots.Length / count;
        for (int i = 0; i < radix; i++) InverseFft(output.Slice(i * group, group), start + i * stride, stride * radix);
        if (radix == 2)
        {
            for (int j = 0; j < group; j++)
            {
                var even = output[j];
                var odd = output[group + j] * _roots[j * rootStride];
                output[j] = even + odd;
                output[group + j] = even - odd;
            }
            return;
        }

        Span<Complex> values = stackalloc Complex[5];
        for (int j = 0; j < group; j++)
        {
            for (int i = 0; i < radix; i++) values[i] = output[i * group + j];
            for (int i = 0; i < radix; i++)
            {
                int k = i * group + j;
                var value = values[0];
                for (int b = 1; b < radix; b++) value += values[b] * _roots[b * k * rootStride % _roots.Length];
                output[k] = value;
            }
        }
    }
}
