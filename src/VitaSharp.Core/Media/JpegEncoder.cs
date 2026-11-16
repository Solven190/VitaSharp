namespace VitaSharp.Core.Media;

/// <summary>
/// Managed baseline JPEG encoder (8-bit, Huffman with the typical Annex K tables) from planar YCbCr: 4:4:4, 4:2:2,
/// 4:2:0 (any 1/2/4 divisors) or grayscale. Edge MCUs replicate the last row/column.
/// </summary>
public static class JpegEncoder
{
    /// <summary>Quantization tables for a Vita compression ratio (0..255, higher = smaller file): the ratio maps to a
    /// scale 1..16 (as Vita3K's FFmpeg qscale) and the Annex K tables are multiplied by scale / 8, so the default ratio
    /// 64 (scale 4) is the classic "quality 75" table set.</summary>
    public static (byte[] Luma, byte[] Chroma) TablesForRatio(int ratio)
    {
        int scale = 1 + Math.Clamp(ratio, 0, 255) * 15 / 255;
        return (Scale(JpegTables.StdLumaQuant, scale), Scale(JpegTables.StdChromaQuant, scale));
    }

    private static byte[] Scale(byte[] t, int scale)
    {
        byte[] o = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            o[i] = (byte)Math.Clamp((t[i] * scale + 4) / 8, 1, 255);
        }

        return o;
    }

    private sealed class HuffCode
    {
        public readonly ushort[] Code = new ushort[256];
        public readonly byte[] Size = new byte[256];

        public HuffCode(byte[] bits, byte[] vals)
        {
            int code = 0, k = 0;
            for (int len = 1; len <= 16; len++)
            {
                for (int i = 0; i < bits[len - 1]; i++, k++, code++)
                {
                    Code[vals[k]] = (ushort)code;
                    Size[vals[k]] = (byte)len;
                }
                code <<= 1;
            }
        }
    }

    private static readonly HuffCode s_dcY = new(JpegTables.DcLumaBits, JpegTables.DcLumaVals),
        s_acY = new(JpegTables.AcLumaBits, JpegTables.AcLumaVals),
        s_dcC = new(JpegTables.DcChromaBits, JpegTables.DcChromaVals),
        s_acC = new(JpegTables.AcChromaBits, JpegTables.AcChromaVals);

    internal sealed class Writer
    {
        public readonly MemoryStream Out = new();
        private ulong _acc;
        private int _bitCount;

        public ulong Accumulator => _acc;
        public int BitCount => _bitCount;

        /// <summary>Continues after a part whose last <paramref name="bitCount"/> bits (low bits of
        /// <paramref name="bits"/>) were not written.</summary>
        public void Resume(uint bits, int bitCount)
        {
            _acc = bits;
            _bitCount = bitCount;
        }

        public void Byte(int b) => Out.WriteByte((byte)b);
        public void Word(int w)
        {
            Byte(w >> 8);
            Byte(w & 0xFF);
        }

        public void Bits(int value, int count)
        {
            if (count == 0)
            {
                return;
            }

            _acc = (_acc << count) | ((uint)value & ((1u << count) - 1));
            _bitCount += count;
            while (_bitCount >= 8)
            {
                int b = (int)(_acc >> (_bitCount - 8)) & 0xFF;
                Out.WriteByte((byte)b);
                if (b == 0xFF)
                {
                    Out.WriteByte(0);
                }

                _bitCount -= 8;
            }
        }

        /// <summary>Pads the last byte with 1 bits.</summary>
        public void Flush()
        {
            if (_bitCount > 0)
            {
                Bits(0x7F, 8 - _bitCount);
            }

            _acc = 0;
            _bitCount = 0;
        }
    }

    /// <summary>Encodes the image. <paramref name="cb"/> empty = grayscale. Chroma planes are ceil(w / hdiv) x
    /// ceil(h / vdiv) with stride <paramref name="cStride"/>. <paramref name="writeHuffmanTables"/> = false omits APP0
    /// and DHT (MJPEG frame: decoders use the Annex K tables).</summary>
    public static byte[] Encode(ReadOnlySpan<byte> y, int yStride, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, int cStride,
        int width, int height, int hdiv, int vdiv, byte[] lumaQuant, byte[] chromaQuant, bool writeHuffmanTables = true,
        int restartInterval = 0)
    {
        bool gray = cb.IsEmpty;
        if (gray)
        {
            hdiv = vdiv = 1;
        }

        var stream = new Stream(width, height, hdiv, vdiv, lumaQuant, chromaQuant, gray);
        stream.WriteHeader(writeHuffmanTables, restartInterval);
        int cw = (width + hdiv - 1) / hdiv, ch = (height + vdiv - 1) / vdiv;
        int sinceRestart = 0, rst = 0;
        for (int my = 0; my < stream.McusY; my++)
        {
            for (int mx = 0; mx < stream.McusX; mx++)
            {
                if (restartInterval > 0 && sinceRestart == restartInterval)
                {
                    stream.Restart(rst++);
                    sinceRestart = 0;
                }
                sinceRestart++;
                stream.EncodeMcu(y, yStride, cb, cr, cStride, mx * stream.McuWidth, my * stream.McuHeight, width, height,
                    mx * 8, my * 8, cw, ch);
            }
        }

        stream.Finish();
        return stream.Writer.Out.ToArray();
    }

    /// <summary>
    /// A baseline JPEG written in parts: the header, then MCUs in raster order from planes given part by part, then
    /// the end. The entropy coder's state between parts (the bits not yet in a whole byte, the DC predictors) is
    /// <see cref="Bits"/>, <see cref="BitCount"/> and <see cref="Predictors"/>, so a part can be encoded long after
    /// the previous one (a fresh <see cref="Stream"/> resumed with <see cref="Resume"/>). The bytes of each part are
    /// taken with <see cref="Take"/>.
    /// </summary>
    public sealed class Stream
    {
        internal Writer Writer { get; } = new();
        private readonly int _width, _height, _hdiv, _vdiv;
        private readonly byte[] _lumaQuant, _chromaQuant;
        private readonly bool _gray;
        private int _predY, _predCb, _predCr;

        public Stream(int width, int height, int hdiv, int vdiv, byte[] lumaQuant, byte[] chromaQuant, bool gray = false)
        {
            if (width is < 1 or > 65535 || height is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            _width = width;
            _height = height;
            _hdiv = gray ? 1 : hdiv;
            _vdiv = gray ? 1 : vdiv;
            _lumaQuant = lumaQuant;
            _chromaQuant = chromaQuant;
            _gray = gray;
        }

        public int McuWidth => 8 * _hdiv;
        public int McuHeight => 8 * _vdiv;
        public int McusX => (_width + McuWidth - 1) / McuWidth;
        public int McusY => (_height + McuHeight - 1) / McuHeight;

        /// <summary>The bits of the last byte not written yet (low <see cref="BitCount"/> bits) and their count.</summary>
        public uint Bits => (uint)(Writer.Accumulator & 0xFF);
        public int BitCount => Writer.BitCount;
        public (int Y, int Cb, int Cr) Predictors => (_predY, _predCb, _predCr);

        /// <summary>Continues a stream whose state was saved after its last part.</summary>
        public void Resume(uint bits, int bitCount, (int Y, int Cb, int Cr) predictors)
        {
            Writer.Resume(bits, bitCount);
            (_predY, _predCb, _predCr) = predictors;
        }

        /// <summary>Bytes encoded and not yet taken.</summary>
        public int Pending => (int)Writer.Out.Length;

        /// <summary>Copies the encoded bytes into <paramref name="destination"/> (at least <see cref="Pending"/> long)
        /// and forgets them; returns how many.</summary>
        public int Take(Span<byte> destination)
        {
            int count = Pending;
            Writer.Out.GetBuffer().AsSpan(0, count).CopyTo(destination);
            Writer.Out.SetLength(0);
            return count;
        }

        /// <summary>SOI, APP0 (with the tables), DQT, SOF0, DRI (with a restart interval), DHT (with the tables), SOS.</summary>
        public void WriteHeader(bool writeHuffmanTables, int restartInterval = 0)
        {
            Writer w = Writer;
            w.Word(0xFFD8);
            if (writeHuffmanTables)
            {
                w.Word(0xFFE0);
                w.Word(16);
                foreach (char l in "JFIF")
                {
                    w.Byte(l);
                }

                w.Byte(0);
                w.Byte(1);
                w.Byte(1);
                w.Byte(0);
                w.Word(1);
                w.Word(1);
                w.Byte(0);
                w.Byte(0);
            }
            w.Word(0xFFDB);
            w.Word(2 + 65 * (_gray ? 1 : 2));
            w.Byte(0x00);
            for (int i = 0; i < 64; i++)
            {
                w.Byte(_lumaQuant[JpegTables.ZigZag[i]]);
            }

            if (!_gray)
            {
                w.Byte(0x01);
                for (int i = 0; i < 64; i++)
                {
                    w.Byte(_chromaQuant[JpegTables.ZigZag[i]]);
                }
            }
            int nc = _gray ? 1 : 3;
            w.Word(0xFFC0);
            w.Word(8 + 3 * nc);
            w.Byte(8);
            w.Word(_height);
            w.Word(_width);
            w.Byte(nc);
            w.Byte(1);
            w.Byte((_hdiv << 4) | _vdiv);
            w.Byte(0);
            if (!_gray)
            {
                w.Byte(2);
                w.Byte(0x11);
                w.Byte(1);
                w.Byte(3);
                w.Byte(0x11);
                w.Byte(1);
            }
            if (restartInterval > 0)
            {
                w.Word(0xFFDD);
                w.Word(4);
                w.Word(restartInterval);
            }
            if (writeHuffmanTables)
            {
                Dht(0x00, JpegTables.DcLumaBits, JpegTables.DcLumaVals);
                Dht(0x10, JpegTables.AcLumaBits, JpegTables.AcLumaVals);
                if (!_gray)
                {
                    Dht(0x01, JpegTables.DcChromaBits, JpegTables.DcChromaVals);
                    Dht(0x11, JpegTables.AcChromaBits, JpegTables.AcChromaVals);
                }
            }
            w.Word(0xFFDA);
            w.Word(6 + 2 * nc);
            w.Byte(nc);
            w.Byte(1);
            w.Byte(0x00);
            if (!_gray)
            {
                w.Byte(2);
                w.Byte(0x11);
                w.Byte(3);
                w.Byte(0x11);
            }
            w.Byte(0);
            w.Byte(63);
            w.Byte(0);
        }

        private void Dht(int cls, byte[] bits, byte[] vals)
        {
            Writer.Word(0xFFC4);
            Writer.Word(2 + 17 + vals.Length);
            Writer.Byte(cls);
            foreach (byte b in bits)
            {
                Writer.Byte(b);
            }

            foreach (byte v in vals)
            {
                Writer.Byte(v);
            }
        }

        /// <summary>RSTn (n = <paramref name="index"/> &amp; 7) and the predictors back to zero.</summary>
        public void Restart(int index)
        {
            Writer.Flush();
            Writer.Word(0xFFD0 + (index & 7));
            _predY = _predCb = _predCr = 0;
        }

        /// <summary>One MCU whose luma top-left is (<paramref name="lumaX"/>, <paramref name="lumaY"/>) in planes of
        /// <paramref name="lumaWidth"/> x <paramref name="lumaHeight"/> (chroma: the same with the chroma values);
        /// samples past the plane repeat its last row and column.</summary>
        public void EncodeMcu(ReadOnlySpan<byte> y, int yStride, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, int cStride,
            int lumaX, int lumaY, int lumaWidth, int lumaHeight, int chromaX, int chromaY, int chromaWidth, int chromaHeight)
        {
            Span<float> samples = stackalloc float[64];
            Span<float> coefs = stackalloc float[64];
            for (int v = 0; v < _vdiv; v++)
            {
                for (int h = 0; h < _hdiv; h++)
                {
                    Load(y, yStride, lumaWidth, lumaHeight, lumaX + h * 8, lumaY + v * 8, samples);
                    Block(Writer, samples, coefs, _lumaQuant, ref _predY, s_dcY, s_acY);
                }
            }

            if (_gray)
            {
                return;
            }

            Load(cb, cStride, chromaWidth, chromaHeight, chromaX, chromaY, samples);
            Block(Writer, samples, coefs, _chromaQuant, ref _predCb, s_dcC, s_acC);
            Load(cr, cStride, chromaWidth, chromaHeight, chromaX, chromaY, samples);
            Block(Writer, samples, coefs, _chromaQuant, ref _predCr, s_dcC, s_acC);
        }

        /// <summary>The last byte padded with 1 bits, then EOI.</summary>
        public void Finish()
        {
            Writer.Flush();
            Writer.Word(0xFFD9);
        }
    }

    private static void Load(ReadOnlySpan<byte> p, int stride, int pw, int ph, int x0, int y0, Span<float> dst)
    {
        for (int yy = 0; yy < 8; yy++)
        {
            int sy = Math.Min(y0 + yy, ph - 1) * stride;
            for (int xx = 0; xx < 8; xx++)
            {
                dst[yy * 8 + xx] = p[sy + Math.Min(x0 + xx, pw - 1)] - 128f;
            }
        }
    }

    private static int Category(int v)
    {
        v = Math.Abs(v);
        int n = 0;
        while (v != 0)
        {
            n++;
            v >>= 1;
        }
        return n;
    }

    private static void Block(Writer w, Span<float> samples, Span<float> coefs, byte[] q, ref int pred, HuffCode dc, HuffCode ac)
    {
        JpegTables.Fdct(samples, coefs);
        Span<int> zz = stackalloc int[64];
        for (int i = 0; i < 64; i++)
        {
            int n = JpegTables.ZigZag[i];
            zz[i] = (int)MathF.Round(coefs[n] / q[n], MidpointRounding.AwayFromZero);
        }
        zz[0] = Math.Clamp(zz[0], -2047, 2047);
        int diff = zz[0] - pred;
        pred = zz[0];
        int cat = Category(diff);
        w.Bits(dc.Code[cat], dc.Size[cat]);
        w.Bits(diff < 0 ? diff - 1 : diff, cat);
        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            int c = Math.Clamp(zz[k], -1023, 1023);
            if (c == 0)
            {
                run++;
                continue;
            }
            while (run > 15)
            {
                w.Bits(ac.Code[0xF0], ac.Size[0xF0]);
                run -= 16;
            }
            int s = Category(c), sym = (run << 4) | s;
            w.Bits(ac.Code[sym], ac.Size[sym]);
            w.Bits(c < 0 ? c - 1 : c, s);
            run = 0;
        }
        if (run > 0)
        {
            w.Bits(ac.Code[0x00], ac.Size[0x00]);
        }
    }
}
