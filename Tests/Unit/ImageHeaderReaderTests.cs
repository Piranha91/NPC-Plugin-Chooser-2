using System.IO;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="ImageHeaderReader"/>'s hand-written PNG/JPEG header parsing, driven with synthetic
/// byte streams so every branch is reachable without real image files. Encoded-image round trips
/// through WIC (and the non-PNG/JPEG fallback) live in Integration/ImageWicRoundTripTests.
/// </summary>
public class ImageHeaderReaderTests
{
    private static byte[] PngHeader(uint width, uint height)
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        b.AddRange(new byte[] { 0, 0, 0, 13 });             // IHDR length
        b.AddRange("IHDR"u8.ToArray());
        b.AddRange(BE32(width));
        b.AddRange(BE32(height));
        b.AddRange(new byte[] { 8, 6, 0, 0, 0 });           // depth, colour type, compression, filter, interlace
        b.AddRange(new byte[] { 0, 0, 0, 0 });              // CRC (not checked by a header reader)
        return b.ToArray();
    }

    private static byte[] BE32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] Segment(byte marker, int payloadLength, byte fill = 0)
    {
        int len = payloadLength + 2;
        var s = new byte[2 + len];
        s[0] = 0xFF; s[1] = marker; s[2] = (byte)(len >> 8); s[3] = (byte)len;
        for (int i = 4; i < s.Length; i++) s[i] = fill;
        return s;
    }

    private static byte[] Sof(byte marker, int width, int height) => new byte[]
    {
        0xFF, marker, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
        0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
    };

    private static byte[] Jpeg(params byte[][] parts)
    {
        var b = new List<byte> { 0xFF, 0xD8 };
        foreach (var p in parts) b.AddRange(p);
        b.AddRange(new byte[] { 0xFF, 0xD9 });
        return b.ToArray();
    }

    [Theory]
    [InlineData(750u, 750u)]
    [InlineData(1u, 65537u)]
    [InlineData(2560u, 1440u)]
    public void Png_ReadsIhdrDimensions(uint w, uint h)
    {
        ImageHeaderReader.TryGetDimensions(PngHeader(w, h), out int width, out int height).Should().BeTrue();
        (width, height).Should().Be(((int)w, (int)h));
    }

    [Fact]
    public void Png_ZeroOrOverflowingDimensions_AreRejected()
    {
        ImageHeaderReader.TryGetDimensions(PngHeader(0, 10), out _, out _).Should().BeFalse();
        ImageHeaderReader.TryGetDimensions(PngHeader(0x8000_0000, 10), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Jpeg_SkipsAppSegmentsToBaselineFrame()
    {
        // APP0 (JFIF) + a large APP1 (EXIF with thumbnail) before the frame header, as cameras write.
        var data = Jpeg(Segment(0xE0, 14), Segment(0xE1, 60000, fill: 0xFF), Sof(0xC0, 795, 640));

        ImageHeaderReader.TryGetDimensions(data, out int w, out int h).Should().BeTrue();
        (w, h).Should().Be((795, 640));
    }

    [Theory]
    [InlineData(0xC1)] // extended sequential
    [InlineData(0xC2)] // progressive
    [InlineData(0xC3)] // lossless
    public void Jpeg_ReadsOtherFrameTypes(byte sofMarker)
    {
        var data = Jpeg(Segment(0xE0, 14), Sof(sofMarker, 616, 616));

        ImageHeaderReader.TryGetDimensions(data, out int w, out int h).Should().BeTrue();
        (w, h).Should().Be((616, 616));
    }

    [Fact]
    public void Jpeg_DhtAndDqtMarkersInTheSofRangeAreNotMistakenForFrames()
    {
        // 0xC4 (DHT) sits inside 0xC0-0xCF but carries no dimensions; its payload here would read as 0x4142 x 0x4344.
        var dht = new byte[] { 0xFF, 0xC4, 0x00, 0x07, 0x00, 0x41, 0x42, 0x43, 0x44 };
        var data = Jpeg(Segment(0xDB, 65), dht, Sof(0xC0, 100, 200));

        ImageHeaderReader.TryGetDimensions(data, out int w, out int h).Should().BeTrue();
        (w, h).Should().Be((100, 200));
    }

    [Fact]
    public void Jpeg_FillBytesBeforeAMarkerAreSkipped()
    {
        var data = Jpeg(new byte[] { 0xFF, 0xFF, 0xFF }, Sof(0xC0, 320, 240)[1..]);
        // ^ three 0xFF fill bytes then the SOF marker byte: FF FF FF C0 ...

        ImageHeaderReader.TryGetDimensions(data, out int w, out int h).Should().BeTrue();
        (w, h).Should().Be((320, 240));
    }

    [Fact]
    public void Jpeg_ScanBeforeAnyFrameHeader_IsRejected()
    {
        var data = Jpeg(Segment(0xE0, 14), Segment(0xDA, 10));
        ImageHeaderReader.TryGetDimensions(data, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Jpeg_SegmentLengthRunningPastTheEnd_IsRejected()
    {
        var data = new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0xFF, 0xF0, 0x00, 0x00 };
        ImageHeaderReader.TryGetDimensions(data, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x89, 0x50 })]
    [InlineData(new byte[] { 0x74, 0x65, 0x78, 0x74, 0x20, 0x6E, 0x6F, 0x74, 0x20, 0x61, 0x6E, 0x20, 0x69, 0x6D, 0x61, 0x67, 0x65 })]
    public void NonImageOrTruncatedData_ReturnsFalseWithZeros(byte[] data)
    {
        ImageHeaderReader.TryGetDimensions(data, out int w, out int h).Should().BeFalse();
        (w, h).Should().Be((0, 0));
    }

    [Fact]
    public void FilePath_ReadsHeaderFromDisk_AndMissingFileIsFalse()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("head.png");
        File.WriteAllBytes(path, PngHeader(640, 480));

        ImageHeaderReader.TryGetDimensions(path, out int w, out int h).Should().BeTrue();
        (w, h).Should().Be((640, 480));
        ImageHeaderReader.TryGetDimensions(tmp.Combine("missing.png"), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void DetectExtension_RecognizesPngAndJpegSignatures()
    {
        ImageHeaderReader.DetectExtension(PngHeader(1, 1)).Should().Be("png");
        ImageHeaderReader.DetectExtension(Jpeg(Sof(0xC0, 1, 1))).Should().Be("jpg");
    }

    [Fact]
    public void DetectExtension_UnrecognizedData_ReturnsNull()
    {
        ImageHeaderReader.DetectExtension("<html>404</html>"u8.ToArray()).Should().BeNull();
        ImageHeaderReader.DetectExtension(Array.Empty<byte>()).Should().BeNull();
    }
}
