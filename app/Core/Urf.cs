using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Oplus.Core;

/// <summary>One decoded page of an Apple raster (image/urf) document.</summary>
public sealed class UrfPage : IDisposable
{
    public required Bitmap Image;
    public int Dpi;
    public int Duplex;   // 1 = one side, 2/3 = two sides
    public void Dispose() => Image.Dispose();
}

/// <summary>
/// Reads "UNIRAST" documents: an 8-byte magic, a page count, then for each page a 32-byte header
/// (bits per pixel, colour space, duplex, quality, width, height, dpi) followed by run-length
/// compressed lines.
/// </summary>
public static class UrfDecoder
{
    public static bool IsUrf(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && data[..7].SequenceEqual("UNIRAST"u8);

    public static IEnumerable<UrfPage> Pages(Stream s)
    {
        var magic = ReadExact(s, 8);
        if (magic == null || !IsUrf(magic)) throw new InvalidDataException("Not an image/urf document");
        ReadExact(s, 4); // page count (not always reliable)
        while (true)
        {
            var hdr = ReadExact(s, 32);
            if (hdr == null) yield break;
            int bpp = hdr[0];
            int duplex = hdr[2];
            int width = (int)BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(12));
            int height = (int)BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(16));
            int dpi = (int)BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(20));
            if (width <= 0 || height <= 0 || width > 20000 || height > 30000 || (bpp != 8 && bpp != 24 && bpp != 32 && bpp != 16 && bpp != 48))
                throw new InvalidDataException($"Bad URF page header {width}x{height} {bpp} bpp");
            yield return new UrfPage { Image = ReadPage(s, width, height, bpp), Dpi = dpi, Duplex = duplex };
        }
    }

    static Bitmap ReadPage(Stream s, int width, int height, int bpp)
    {
        int px = bpp / 8;
        var line = new byte[width * px];
        var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        var row = new byte[bd.Stride];
        try
        {
            int y = 0;
            while (y < height)
            {
                int repeat = ReadByte(s) + 1;
                // decode one line
                int x = 0;
                while (x < width)
                {
                    int code = ReadByte(s);
                    if (code == 0x80)
                    {
                        // rest of the line is white
                        Array.Fill(line, (byte)0xFF, x * px, (width - x) * px);
                        x = width;
                    }
                    else if (code < 0x80)
                    {
                        int count = code + 1;
                        var pix = ReadExact(s, px) ?? throw new EndOfStreamException();
                        for (int i = 0; i < count && x < width; i++, x++) Buffer.BlockCopy(pix, 0, line, x * px, px);
                    }
                    else
                    {
                        int count = 257 - code;
                        var pix = ReadExact(s, count * px) ?? throw new EndOfStreamException();
                        int n = Math.Min(count, width - x);
                        Buffer.BlockCopy(pix, 0, line, x * px, n * px);
                        x += n;
                    }
                }
                // to BGR
                for (int i = 0; i < width; i++)
                {
                    byte r, g, b;
                    if (px == 1) { r = g = b = line[i]; }
                    else if (px == 2) { r = g = b = line[i * 2]; }       // 16-bit grey: high byte
                    else if (px == 6) { r = line[i * 6]; g = line[i * 6 + 2]; b = line[i * 6 + 4]; }
                    else { r = line[i * px]; g = line[i * px + 1]; b = line[i * px + 2]; }
                    row[i * 3] = b; row[i * 3 + 1] = g; row[i * 3 + 2] = r;
                }
                for (int k = 0; k < repeat && y < height; k++, y++)
                    Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, bd.Stride);
            }
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    static int ReadByte(Stream s) { int b = s.ReadByte(); if (b < 0) throw new EndOfStreamException(); return b; }

    static byte[]? ReadExact(Stream s, int n)
    {
        var b = new byte[n]; int got = 0;
        while (got < n) { int r = s.Read(b, got, n - got); if (r <= 0) return got == 0 ? null : throw new EndOfStreamException(); got += r; }
        return b;
    }
}
