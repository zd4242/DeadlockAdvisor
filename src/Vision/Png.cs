using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DeadlockAdvisor.Vision;

/// <summary>
/// PNG in and out, as RGB. Decoding does what Pillow's <c>convert("RGB")</c> does: alpha is dropped,
/// not composited or premultiplied, and no gamma or colour profile is applied, so the pixels match
/// the ones the Python app matched against; <see cref="DecodeWithAlpha"/> hands the alpha back
/// separately. Every colour type, bit depth and interlacing is read.
/// </summary>
public static class Png
{
    private static readonly byte[] _signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] _crcTable = BuildCrcTable();

    public static RgbImage Load(string path) => Decode(File.ReadAllBytes(path));

    public static RgbImage Decode(ReadOnlySpan<byte> data) => Decode(data, withAlpha: false).Image;

    /// <summary>
    /// The pixels as <see cref="Decode(ReadOnlySpan{byte})"/> gives them, plus each one's opacity (0-255):
    /// from an alpha channel, a palette's transparency, or a transparent colour key.
    /// </summary>
    public static (RgbImage Image, byte[] Alpha) DecodeWithAlpha(ReadOnlySpan<byte> data)
    {
        var (image, alpha) = Decode(data, withAlpha: true);
        return (image, alpha!);
    }

    private static (RgbImage Image, byte[]? Alpha) Decode(ReadOnlySpan<byte> data, bool withAlpha)
    {
        if (data.Length < 8 || !data[..8].SequenceEqual(_signature))
            throw new InvalidDataException("Not a PNG file.");

        int width = 0, height = 0, depth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var compressed = new MemoryStream();
        var position = 8;
        while (position + 8 <= data.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            var kind = Encoding.ASCII.GetString(data.Slice(position + 4, 4));
            if (position + 12 + length > data.Length)
                throw new InvalidDataException("Truncated PNG chunk.");
            var body = data.Slice(position + 8, length);
            switch (kind)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                    depth = body[8];
                    colorType = body[9];
                    interlace = body[12];
                    break;
                case "PLTE":
                    palette = body.ToArray();
                    break;
                case "tRNS":
                    transparency = body.ToArray();
                    break;
                case "IDAT":
                    compressed.Write(body);
                    break;
            }
            position += 12 + length;
            if (kind == "IEND")
                break;
        }
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("PNG has no image header.");

        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException($"Unknown PNG colour type {colorType}."),
        };
        if (colorType == 3 && palette is null)
            throw new InvalidDataException("Palette PNG without a palette.");

        compressed.Position = 0;
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflater.CopyTo(raw);
        var bytes = raw.GetBuffer().AsSpan(0, (int)raw.Length);

        var image = new RgbImage(width, height);
        byte[]? alpha = null;
        if (withAlpha)
        {
            alpha = new byte[width * height];
            Array.Fill(alpha, (byte)255);
        }
        var format = new Format(channels, depth, colorType, palette, transparency);
        if (interlace == 0)
        {
            Unpack(bytes, image, alpha, format, 0, 0, 1, 1, width, height);
        }
        else
        {
            // Adam7: seven passes over a sparser and sparser grid.
            (int X, int Y, int Dx, int Dy)[] passes = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
            var offset = 0;
            foreach (var (x0, y0, dx, dy) in passes)
            {
                var passWidth = (width - x0 + dx - 1) / dx;
                var passHeight = (height - y0 + dy - 1) / dy;
                if (passWidth <= 0 || passHeight <= 0)
                    continue;
                offset += Unpack(bytes[offset..], image, alpha, format, x0, y0, dx, dy, passWidth, passHeight);
            }
        }
        return (image, alpha);
    }

    /// <param name="Transparency">The tRNS chunk: an alpha per palette entry, or the one colour that's transparent.</param>
    private sealed record Format(int Channels, int Depth, int ColorType, byte[]? Palette, byte[]? Transparency)
    {
        public int BitsPerPixel => Channels * Depth;
        public int FilterStride => Math.Max(1, BitsPerPixel / 8);
    }

    /// <summary>Unfilter one (sub)image's scanlines and write its pixels into place. Returns the bytes consumed.</summary>
    private static int Unpack(Span<byte> bytes, RgbImage image, byte[]? alpha, Format format, int x0, int y0, int dx, int dy, int width, int height)
    {
        var stride = (width * format.BitsPerPixel + 7) / 8;
        var previous = new byte[stride];
        var current = new byte[stride];
        for (var row = 0; row < height; row++)
        {
            var line = bytes.Slice(row * (stride + 1), stride + 1);
            line[1..].CopyTo(current);
            Unfilter(line[0], current, previous, format.FilterStride);
            for (var column = 0; column < width; column++)
            {
                var (x, y) = (x0 + column * dx, y0 + row * dy);
                WritePixel(image, x, y, current, column, format);
                if (alpha is not null)
                    alpha[y * image.Width + x] = AlphaOf(current, column, format);
            }
            (previous, current) = (current, previous);
        }
        return height * (stride + 1);
    }

    private static byte AlphaOf(byte[] line, int column, Format format)
    {
        switch (format.ColorType)
        {
            case 4 or 6:
                return (byte)Sample(line, column * format.Channels + format.Channels - 1, format.Depth);
            case 3:
            {
                var bit = column * format.Depth;
                var index = format.Depth == 8 ? line[column] : (line[bit / 8] >> (8 - format.Depth - bit % 8)) & ((1 << format.Depth) - 1);
                return format.Transparency is { } alphas && index < alphas.Length ? alphas[index] : (byte)255;
            }
            default:
            {
                // A colour key: two bytes per channel, whatever the depth, holding a sample at that depth.
                if (format.Transparency is not { } key || key.Length < 2 * format.Channels)
                    return 255;
                for (var c = 0; c < format.Channels; c++)
                {
                    var wanted = BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(c * 2));
                    if (RawSample(line, column * format.Channels + c, format.Depth) != wanted)
                        return 255;
                }
                return 0;
            }
        }
    }

    /// <summary>A sample at its own depth, unscaled.</summary>
    private static int RawSample(byte[] line, int index, int depth) => depth switch
    {
        8 => line[index],
        16 => BinaryPrimitives.ReadUInt16BigEndian(line.AsSpan(index * 2)),
        _ => (line[index * depth / 8] >> (8 - depth - index * depth % 8)) & ((1 << depth) - 1),
    };

    private static void Unfilter(byte filter, byte[] line, byte[] previous, int bpp)
    {
        for (var i = 0; i < line.Length; i++)
        {
            int left = i >= bpp ? line[i - bpp] : 0;
            int up = previous[i];
            int upLeft = i >= bpp ? previous[i - bpp] : 0;
            line[i] = filter switch
            {
                0 => line[i],
                1 => (byte)(line[i] + left),
                2 => (byte)(line[i] + up),
                3 => (byte)(line[i] + (left + up) / 2),
                4 => (byte)(line[i] + Paeth(left, up, upLeft)),
                _ => throw new InvalidDataException($"Unknown PNG filter {filter}."),
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>A sample scaled to 8 bits: 16-bit keeps its high byte, low depths spread over 0-255.</summary>
    private static int Sample(byte[] line, int index, int depth)
    {
        switch (depth)
        {
            case 8:
                return line[index];
            case 16:
                return line[index * 2];
            default:
                var bit = index * depth;
                var value = (line[bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1);
                return value * 255 / ((1 << depth) - 1);
        }
    }

    private static void WritePixel(RgbImage image, int x, int y, byte[] line, int column, Format format)
    {
        var at = (y * image.Width + x) * 3;
        var pixels = image.Pixels;
        switch (format.ColorType)
        {
            case 3:
            {
                var bit = column * format.Depth;
                var index = format.Depth == 8 ? line[column] : (line[bit / 8] >> (8 - format.Depth - bit % 8)) & ((1 << format.Depth) - 1);
                var palette = format.Palette!;
                if (index * 3 + 2 < palette.Length)
                {
                    pixels[at] = palette[index * 3];
                    pixels[at + 1] = palette[index * 3 + 1];
                    pixels[at + 2] = palette[index * 3 + 2];
                }
                break;
            }
            case 0 or 4:
            {
                var gray = (byte)Sample(line, column * format.Channels, format.Depth);
                pixels[at] = pixels[at + 1] = pixels[at + 2] = gray;
                break;
            }
            default:
                for (var c = 0; c < 3; c++)
                    pixels[at + c] = (byte)Sample(line, column * format.Channels + c, format.Depth);
                break;
        }
    }

    // -- writing --------------------------------------------------------------------

    public static void Save(RgbImage image, string path) => File.WriteAllBytes(path, Encode(image));

    /// <summary>8-bit RGB, unfiltered: the pixels read back exactly, in this app and in Pillow.</summary>
    public static byte[] Encode(RgbImage image)
    {
        using var output = new MemoryStream();
        output.Write(_signature);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)image.Height);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(output, "IHDR", header);

        using (var body = new MemoryStream())
        {
            using (var deflater = new ZLibStream(body, CompressionLevel.Optimal, leaveOpen: true))
            {
                var stride = image.Width * 3;
                for (var y = 0; y < image.Height; y++)
                {
                    deflater.WriteByte(0);
                    deflater.Write(image.Pixels, y * stride, stride);
                }
            }
            WriteChunk(output, "IDAT", body.ToArray());
        }
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string kind, byte[] body)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)body.Length);
        output.Write(number);
        var type = Encoding.ASCII.GetBytes(kind);
        output.Write(type);
        output.Write(body);
        var crc = Crc(Crc(0xFFFFFFFFu, type), body) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    private static uint Crc(uint crc, byte[] bytes)
    {
        foreach (var b in bytes)
            crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
