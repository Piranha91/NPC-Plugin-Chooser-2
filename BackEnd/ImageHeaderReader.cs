using System.IO;
using System.Windows.Media.Imaging;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Reads image dimensions and container format without decoding pixels.
/// PNG and JPEG (every mugshot NPC2 produces or downloads) are parsed by hand: PNG's IHDR is
/// required to be the first chunk, and a JPEG's frame size sits in its first SOFn segment.
/// Anything else falls back to the Windows Imaging Component decoder, which reads only the
/// header when created with <see cref="BitmapCreateOptions.DelayCreation"/>.
/// This replaced SixLabors.ImageSharp's Image.Identify / Image.DetectFormat; on a 600-file
/// mugshot sample all three agreed on every dimension and format (tools/imagebench).
/// </summary>
public static class ImageHeaderReader
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Dimensions of the image file at <paramref name="path"/>; false if unreadable or not an image.</summary>
    public static bool TryGetDimensions(string path, out int width, out int height)
    {
        width = height = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            return TryGetDimensions(fs, out width, out height);
        }
        catch (Exception)
        {
            width = height = 0;
            return false;
        }
    }

    /// <summary>Dimensions of an encoded image held in memory; false if not an image.</summary>
    public static bool TryGetDimensions(byte[] data, out int width, out int height)
    {
        width = height = 0;
        if (data == null || data.Length == 0) return false;
        using var ms = new MemoryStream(data, writable: false);
        return TryGetDimensions(ms, out width, out height);
    }

    /// <summary>
    /// Dimensions from a readable, seekable stream positioned at the start of the image.
    /// The stream position is not preserved.
    /// </summary>
    public static bool TryGetDimensions(Stream stream, out int width, out int height)
    {
        width = height = 0;
        long start = stream.Position;
        try
        {
            if (TryParsePngOrJpeg(stream, out width, out height)) return true;

            // Not PNG/JPEG (or a malformed header): let WIC try. DelayCreation keeps it to the header.
            stream.Position = start;
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            return width > 0 && height > 0;
        }
        catch (Exception)
        {
            width = height = 0;
            return false;
        }
    }

    /// <summary>
    /// File extension (without dot, lower case) for the encoded image: "png", "jpg", or for other
    /// formats WIC's first registered extension (e.g. "bmp"). Null if the data is not a recognized image.
    /// </summary>
    public static string? DetectExtension(byte[] data)
    {
        if (data == null || data.Length == 0) return null;
        if (IsPng(data)) return "png";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "jpg";
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            // FileExtensions is a comma-separated list such as ".tiff,.tif".
            var first = decoder.CodecInfo?.FileExtensions?.Split(',')[0].Trim().TrimStart('.').ToLowerInvariant();
            return string.IsNullOrEmpty(first) ? null : first;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsPng(ReadOnlySpan<byte> b) => b.Length >= 8 && b[..8].SequenceEqual(PngSignature);

    private static bool TryParsePngOrJpeg(Stream s, out int width, out int height)
    {
        width = height = 0;
        Span<byte> head = stackalloc byte[24];
        int got = ReadUpTo(s, head);
        if (got < 4) return false;

        if (got >= 24 && IsPng(head) &&
            head[12] == (byte)'I' && head[13] == (byte)'H' && head[14] == (byte)'D' && head[15] == (byte)'R')
        {
            long w = ReadBE32(head, 16), h = ReadBE32(head, 20);
            if (w is <= 0 or > int.MaxValue || h is <= 0 or > int.MaxValue) return false;
            width = (int)w;
            height = (int)h;
            return true;
        }

        if (head[0] == 0xFF && head[1] == 0xD8)
        {
            s.Position -= got - 2; // resume right after SOI
            return TryParseJpegFrame(s, out width, out height);
        }
        return false;
    }

    /// <summary>Walks JPEG marker segments to the first start-of-frame (SOF0-SOF15 except DHT/JPG/DAC).</summary>
    private static bool TryParseJpegFrame(Stream s, out int width, out int height)
    {
        width = height = 0;
        Span<byte> buf = stackalloc byte[7];
        while (true)
        {
            int b = s.ReadByte();
            if (b != 0xFF) return false;
            int marker;
            do { marker = s.ReadByte(); } while (marker == 0xFF); // fill bytes
            if (marker < 0) return false;
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue; // no length field
            if (marker == 0xD9 || marker == 0xDA) return false; // EOI / start of scan before any frame header

            if (ReadUpTo(s, buf[..2]) < 2) return false;
            int length = (buf[0] << 8) | buf[1];
            if (length < 2) return false;

            bool isFrame = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isFrame)
            {
                // precision(1) height(2) width(2)
                if (ReadUpTo(s, buf[..5]) < 5) return false;
                height = (buf[1] << 8) | buf[2];
                width = (buf[3] << 8) | buf[4];
                return width > 0 && height > 0;
            }

            long next = s.Position + length - 2;
            if (next > s.Length) return false;
            s.Position = next;
        }
    }

    private static int ReadUpTo(Stream s, Span<byte> dest)
    {
        int total = 0;
        while (total < dest.Length)
        {
            int n = s.Read(dest[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static long ReadBE32(ReadOnlySpan<byte> b, int o) =>
        ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];
}
