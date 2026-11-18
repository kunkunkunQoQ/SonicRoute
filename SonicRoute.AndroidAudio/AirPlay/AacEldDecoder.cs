using System.Runtime.CompilerServices;

namespace SonicRoute.AirPlay;

/// <summary>Decodes stereo ER AAC ELD access units without LD-SBR or resilience tools.</summary>
internal sealed class AacEldDecoder
{
    private const int Noise = 13, IntensityNegative = 14, IntensityPositive = 15;
    private const int MaxTnsOrder = 12;
    private static readonly Huffman[] Codebooks = CreateCodebooks();
    private static readonly float[] SmallMagnitudes = Enumerable.Range(0, 128).Select(Power43).ToArray();
    private static readonly float[] ScaleFactors = Enumerable.Range(-100, 256).Select(x => MathF.Pow(2, x * 0.25f)).ToArray();
    private readonly int _frames, _tnsBands;
    private readonly AacEldFilterbank _filter;
    private readonly float[] _left, _right, _leftHistory, _rightHistory;
    private int _noiseState;

    internal AacEldDecoder(AirPlayFormat format)
    {
        _frames = format.Frames;
        _tnsBands = format.SampleRate == 44100 ? 32 : 31;
        _filter = new AacEldFilterbank(_frames);
        _left = new float[_frames];
        _right = new float[_frames];
        _leftHistory = new float[_frames * 3];
        _rightHistory = new float[_frames * 3];
        Flush();
    }

    private ReadOnlySpan<ushort> Bands => _frames == 480 ? AacEldTables.Bands480 : AacEldTables.Bands512;

    internal int Decode(ReadOnlySpan<byte> packet, Span<float> pcm)
    {
        if (pcm.Length < _frames * 2) return 0;
        var bits = new BitReader(packet);
        try
        {
            int count = bits.Read(6);
            if (count >= Bands.Length) throw new InvalidDataException();
            int stereoMode = bits.Read(2);
            ulong midSide = stereoMode switch
            {
                0 => 0,
                1 => ReadStereoMask(ref bits, count),
                2 => ulong.MaxValue,
                _ => throw new InvalidDataException()
            };

            Span<byte> leftBooks = stackalloc byte[count], rightBooks = stackalloc byte[count];
            Span<float> gains = stackalloc float[count];
            Span<TnsFilter> leftFilters = stackalloc TnsFilter[3], rightFilters = stackalloc TnsFilter[3];
            Span<float> leftCoefficients = stackalloc float[3 * MaxTnsOrder], rightCoefficients = stackalloc float[3 * MaxTnsOrder];
            int leftCount = ReadChannel(ref bits, _left, leftBooks, gains, leftFilters, leftCoefficients);
            int rightCount = ReadChannel(ref bits, _right, rightBooks, gains, rightFilters, rightCoefficients);
            ApplyStereo(leftBooks, rightBooks, gains, midSide);
            ApplyTns(_left, leftFilters[..leftCount], leftCoefficients);
            ApplyTns(_right, rightFilters[..rightCount], rightCoefficients);
            _filter.Synthesize(_left, _leftHistory, pcm, 0);
            _filter.Synthesize(_right, _rightHistory, pcm, 1);
            return _frames * 2;
        }
        catch (InvalidDataException)
        {
            // Losing a packet does not stop the video or retain its preceding audio overlap.
            Flush();
            return 0;
        }
    }

    internal void Flush()
    {
        Array.Clear(_leftHistory);
        Array.Clear(_rightHistory);
        _noiseState = 0x1f2e3d4c;
    }

    private static ulong ReadStereoMask(ref BitReader bits, int count)
    {
        ulong mask = 0;
        for (int i = 0; i < count; i++) mask |= (ulong)bits.Read(1) << i;
        return mask;
    }

    private int ReadChannel(ref BitReader bits, scoped Span<float> spectral, scoped Span<byte> books, scoped Span<float> gains,
        scoped Span<TnsFilter> filters, scoped Span<float> filterCoefficients)
    {
        int globalGain = bits.Read(8);
        for (int band = 0; band < books.Length;)
        {
            int book = bits.Read(4), end = band, increment;
            if (book == 12) throw new InvalidDataException();
            do
            {
                increment = bits.Read(5);
                end += increment;
                if (end > books.Length) throw new InvalidDataException();
            } while (increment == 31);
            if (end <= band) throw new InvalidDataException();
            books[band..end].Fill((byte)book);
            band = end;
        }

        int noise = globalGain - 90, intensity = 0;
        bool firstNoise = true;
        for (int band = 0; band < books.Length; band++)
        {
            switch (books[band])
            {
                case 0:
                    gains[band] = 0;
                    break;
                case Noise:
                    noise += firstNoise ? bits.Read(9) - 256 : Codebooks[0].Read(ref bits) - 60;
                    firstNoise = false;
                    gains[band] = -ScaleFactors[Math.Clamp(noise + 100, 0, 255)];
                    break;
                case IntensityPositive:
                case IntensityNegative:
                    intensity += Codebooks[0].Read(ref bits) - 60;
                    gains[band] = ScaleFactors[Math.Clamp(100 - intensity, 0, 255)];
                    break;
                default:
                    globalGain += Codebooks[0].Read(ref bits) - 60;
                    if ((uint)globalGain > 255) throw new InvalidDataException();
                    gains[band] = -ScaleFactors[globalGain];
                    break;
            }
        }

        int filterCount = bits.Read(1) != 0 ? ReadTns(ref bits, books.Length, filters, filterCoefficients) : 0;
        var offsets = Bands;
        spectral.Clear();
        for (int band = 0; band < books.Length; band++)
        {
            var values = spectral[offsets[band]..offsets[band + 1]];
            int book = books[band];
            if (book == Noise) ReadNoise(values, gains[band]);
            else if (book is >= 1 and <= 11) ReadSpectrum(ref bits, values, book, gains[band]);
        }
        return filterCount;
    }

    private void ReadNoise(Span<float> values, float gain)
    {
        float energy = 0;
        for (int i = 0; i < values.Length; i++)
        {
            _noiseState = unchecked(_noiseState * 1664525 + 1013904223);
            float value = _noiseState;
            values[i] = value;
            energy += value * value;
        }
        float scale = gain / MathF.Sqrt(energy);
        for (int i = 0; i < values.Length; i++) values[i] *= scale;
    }

    private static void ReadSpectrum(ref BitReader bits, scoped Span<float> output, int book, float gain)
    {
        int dimension = book <= 4 ? 4 : 2;
        int radix = book switch { <= 4 => 3, <= 6 => 9, <= 8 => 8, <= 10 => 13, _ => 17 };
        int bias = book is 1 or 2 ? 1 : book is 5 or 6 ? 4 : 0;
        Span<int> tuple = stackalloc int[4];
        var huffman = Codebooks[book];
        for (int offset = 0; offset < output.Length; offset += dimension)
        {
            int symbol = huffman.Read(ref bits);
            for (int i = dimension - 1; i >= 0; --i)
            {
                tuple[i] = symbol % radix - bias;
                symbol /= radix;
            }
            if (bias == 0)
                for (int i = 0; i < dimension; i++)
                    if (tuple[i] != 0 && bits.Read(1) != 0) tuple[i] = -tuple[i];
            for (int i = 0; i < dimension; i++)
            {
                int value = tuple[i], magnitude = Math.Abs(value);
                if (book == 11 && magnitude == 16)
                {
                    int width = 4;
                    while (bits.Read(1) != 0)
                        if (++width > 12) throw new InvalidDataException();
                    magnitude = (1 << width) | bits.Read(width);
                }
                float sample = magnitude < SmallMagnitudes.Length ? SmallMagnitudes[magnitude] : Power43(magnitude);
                output[offset + i] = (value < 0 ? -sample : sample) * gain;
            }
        }
    }

    private static float Power43(int n) => MathF.Pow(n, 4f / 3);

    private void ApplyStereo(ReadOnlySpan<byte> leftBooks, ReadOnlySpan<byte> rightBooks, ReadOnlySpan<float> gains, ulong midSide)
    {
        var offsets = Bands;
        for (int i = 0; i < rightBooks.Length; i++)
        {
            bool useMidSide = ((midSide >> i) & 1) != 0;
            int stop = offsets[i + 1];
            if (rightBooks[i] is IntensityPositive or IntensityNegative)
            {
                float scale = (rightBooks[i] == IntensityPositive ^ useMidSide) ? gains[i] : -gains[i];
                for (int k = offsets[i]; k < stop; k++) _right[k] = _left[k] * scale;
            }
            else if (useMidSide && leftBooks[i] < Noise && rightBooks[i] < Noise)
            {
                for (int k = offsets[i]; k < stop; k++)
                {
                    float mid = _left[k], side = _right[k];
                    _left[k] = mid + side;
                    _right[k] = mid - side;
                }
            }
        }
    }

    private readonly record struct TnsFilter(int Lower, int Upper, int Direction, int Order);

    private int ReadTns(ref BitReader bits, int bands, scoped Span<TnsFilter> filters, scoped Span<float> coefficients)
    {
        int count = bits.Read(2);
        if (count == 0) return 0;
        int resolution = bits.Read(1);
        var offsets = Bands;
        int upper = offsets.Length - 1, limit = Math.Min(bands, _tnsBands);
        Span<float> previous = stackalloc float[MaxTnsOrder];
        for (int i = 0; i < count; i++)
        {
            int lower = Math.Max(0, upper - bits.Read(6));
            int order = bits.Read(5);
            if (order > MaxTnsOrder) throw new InvalidDataException();
            int direction = 1;
            if (order > 0)
            {
                direction = bits.Read(1) == 0 ? 1 : -1;
                int compression = bits.Read(1);
                int width = 3 + resolution - compression;
                var weights = AacEldTables.TnsCoefficients(resolution, compression);
                var lpc = coefficients.Slice(i * MaxTnsOrder, order);
                for (int j = 0; j < order; j++)
                {
                    float reflection = -weights[bits.Read(width)];
                    lpc[..j].CopyTo(previous);
                    for (int k = 0; k < j; k++) lpc[k] = previous[k] + reflection * previous[j - 1 - k];
                    lpc[j] = reflection;
                }
            }
            filters[i] = new(offsets[Math.Min(lower, limit)], offsets[Math.Min(upper, limit)], direction, order);
            upper = lower;
        }
        return count;
    }

    private static void ApplyTns(Span<float> values, ReadOnlySpan<TnsFilter> filters, ReadOnlySpan<float> coefficients)
    {
        for (int i = 0; i < filters.Length; i++)
        {
            var filter = filters[i];
            if (filter.Order == 0) continue;
            int index = filter.Direction > 0 ? filter.Lower : filter.Upper - 1;
            var lpc = coefficients.Slice(i * MaxTnsOrder, filter.Order);
            for (int sample = 0; sample < filter.Upper - filter.Lower; sample++, index += filter.Direction)
            {
                float value = values[index];
                for (int j = 0; j < Math.Min(sample, filter.Order); j++)
                    value -= values[index - (j + 1) * filter.Direction] * lpc[j];
                if (!float.IsFinite(value)) throw new InvalidDataException();
                values[index] = value;
            }
        }
    }

    private static Huffman[] CreateCodebooks()
    {
        var books = new Huffman[12];
        for (int i = 0; i < books.Length; i++) books[i] = new Huffman(AacEldTables.Codewords(i));
        return books;
    }

    private sealed class Huffman
    {
        private readonly short[] _children;

        internal Huffman(ReadOnlySpan<uint> entries)
        {
            _children = new short[2 * (entries.Length - 1)];
            int next = 2;
            for (int symbol = 0; symbol < entries.Length; ++symbol)
            {
                uint entry = entries[symbol];
                int node = 0, width = (int)(entry >> 24);
                for (int bit = width - 1; bit >= 0; bit--)
                {
                    int branch = node + (int)((entry >> bit) & 1);
                    if (bit == 0) _children[branch] = (short)~symbol;
                    else
                    {
                        if (_children[branch] == 0) { _children[branch] = (short)next; next += 2; }
                        node = _children[branch];
                    }
                }
            }
        }

        internal int Read(ref BitReader bits)
        {
            int node = 0;
            do { node = _children[node + bits.Read(1)]; } while (node > 0);
            return node < 0 ? ~node : throw new InvalidDataException();
        }
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _data = data;
        private uint _buffer;
        private int _available;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int Read(int width)
        {
            while (_available < width)
            {
                if (_data.IsEmpty) throw new InvalidDataException();
                _buffer = (_buffer << 8) | _data[0];
                _data = _data[1..];
                _available += 8;
            }
            _available -= width;
            return (int)((_buffer >> _available) & ((1u << width) - 1));
        }
    }
}
