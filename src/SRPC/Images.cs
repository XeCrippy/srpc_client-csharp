using System.Buffers.Binary;

namespace SRPC;

public enum PixelFormat : byte { Rgba8, Bgra8 }
public readonly record struct ImageView(ReadOnlyMemory<byte> Pixels, uint Width, uint Height, int Stride, PixelFormat Format = PixelFormat.Rgba8);
public enum ScreenshotUntileMode : byte { Xenos, Morton }
public enum ScreenshotFormat : uint { A8R8G8B8 = 0x18280186, A2R10G10B10 = 0x182801B6 }

public sealed class ScreenshotMetadata
{
    public uint PitchBytes { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public uint FramebufferSize { get; init; }
    public uint? Format { get; init; }
    public uint DisplayWidth { get; init; }
    public uint DisplayHeight { get; init; }
    public uint OffsetX { get; init; }
    public uint OffsetY { get; init; }
    public uint? ColorSpace { get; init; }
}
public sealed record RawScreenshot(ScreenshotMetadata Metadata, byte[] TiledFramebuffer);
public sealed record ScreenshotImage(ScreenshotMetadata Source, uint Width, uint Height, byte[] Bgra)
{
    public ImageView View => new(Bgra, Width, Height, checked((int)Width * 4), PixelFormat.Bgra8);
}
public sealed class ScreenshotOptions
{
    public const int DefaultMaximumSize = 64 * 1024 * 1024;
    public ScreenshotUntileMode UntileMode { get; set; } = ScreenshotUntileMode.Xenos;
    public bool ComposeDisplaySurface { get; set; }
    public bool PreserveAlpha { get; set; }
    public Endian Packed10BitEndian { get; set; } = Endian.Little;
    public int MaximumFramebufferSize { get; set; } = DefaultMaximumSize;
    public int MaximumDecodedSize { get; set; } = DefaultMaximumSize;
}

public static class ImageCodec
{
    private const int StoredBlockSize = 65535, MaximumIdatChunkSize = 1024 * 1024;
    public static byte[] EncodePng(ImageView image)
    {
        if (image.Width == 0 || image.Height == 0 || image.Width > 0x7fffffff || image.Height > 0x7fffffff) throw new ProtocolException("PNG dimensions must be nonzero and fit the PNG 31-bit limit.");
        if (image.Format is not (PixelFormat.Rgba8 or PixelFormat.Bgra8)) throw new ProtocolException("Unsupported PNG pixel format.");
        var rowBytes = checked((int)image.Width * 4);
        if (image.Stride < rowBytes) throw new ProtocolException("PNG input stride is smaller than one pixel row.");
        var required = checked((int)(image.Height - 1) * image.Stride + rowBytes);
        if (image.Pixels.Length < required) throw new ProtocolException("PNG input buffer is smaller than its dimensions and stride.");
        var filtered = new byte[checked((rowBytes + 1) * (int)image.Height)];
        var input = image.Pixels.Span;
        for (var y = 0; y < image.Height; y++)
        {
            var destination = checked((int)y * (rowBytes + 1) + 1);
            var source = checked((int)y * image.Stride);
            if (image.Format == PixelFormat.Rgba8) input.Slice(source, rowBytes).CopyTo(filtered.AsSpan(destination));
            else for (var x = 0; x < rowBytes; x += 4)
            {
                filtered[destination + x] = input[source + x + 2]; filtered[destination + x + 1] = input[source + x + 1];
                filtered[destination + x + 2] = input[source + x]; filtered[destination + x + 3] = input[source + x + 3];
            }
        }
        var zlib = ZlibStore(filtered);
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, image.Width); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8; header[9] = 6;
        WriteChunk(png, "IHDR", header);
        for (var offset = 0; offset < zlib.Length; offset += MaximumIdatChunkSize)
            WriteChunk(png, "IDAT", zlib.AsSpan(offset, Math.Min(MaximumIdatChunkSize, zlib.Length - offset)));
        WriteChunk(png, "IEND", ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    public static void WritePng(string path, ImageView image)
    {
        if (string.IsNullOrEmpty(path)) throw new ProtocolException("PNG output path cannot be empty.");
        File.WriteAllBytes(path, EncodePng(image));
    }

    private static byte[] ZlibStore(ReadOnlySpan<byte> raw)
    {
        using var output = new MemoryStream(); output.WriteByte(0x78); output.WriteByte(0x01);
        for (var offset = 0; offset < raw.Length;)
        {
            var count = Math.Min(StoredBlockSize, raw.Length - offset); output.WriteByte(offset + count == raw.Length ? (byte)1 : (byte)0);
            output.WriteByte((byte)count); output.WriteByte((byte)(count >> 8)); var inverse = (ushort)~(ushort)count;
            output.WriteByte((byte)inverse); output.WriteByte((byte)(inverse >> 8)); output.Write(raw.Slice(offset, count)); offset += count;
        }
        Span<byte> checksum = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(checksum, Adler32(raw)); output.Write(checksum);
        return output.ToArray();
    }
    private static uint Adler32(ReadOnlySpan<byte> bytes)
    {
        const uint modulus = 65521; uint a = 1, b = 0; var offset = 0;
        while (offset < bytes.Length) { var count = Math.Min(5552, bytes.Length - offset); for (var i = 0; i < count; i++) { a += bytes[offset + i]; b += a; } a %= modulus; b %= modulus; offset += count; }
        return b << 16 | a;
    }
    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length); output.Write(number);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type); output.Write(typeBytes); output.Write(data);
        var combined = new byte[4 + data.Length]; typeBytes.CopyTo(combined, 0); data.CopyTo(combined.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(combined)); output.Write(number);
    }
    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var value in bytes) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = crc >> 1 ^ (0xedb88320u & (uint)-(int)(crc & 1)); }
        return ~crc;
    }
}

public static class ScreenshotCodec
{
    private const uint AlternateFormatMarker = 0x80000000;
    public static ScreenshotImage Decode(RawScreenshot screenshot, ScreenshotOptions? options = null)
    {
        options ??= new ScreenshotOptions();
        var visible = Untile(screenshot, options);
        if (!options.ComposeDisplaySurface) return visible;
        var width = screenshot.Metadata.DisplayWidth == 0 ? screenshot.Metadata.Width : screenshot.Metadata.DisplayWidth;
        var height = screenshot.Metadata.DisplayHeight == 0 ? screenshot.Metadata.Height : screenshot.Metadata.DisplayHeight;
        if (screenshot.Metadata.OffsetX != 0 || screenshot.Metadata.OffsetY != 0) return Compose(visible, width, height, screenshot.Metadata, options.MaximumDecodedSize);
        return width != visible.Width || height != visible.Height ? Resize(visible, width, height, options.MaximumDecodedSize) : visible;
    }
    private static ScreenshotImage Untile(RawScreenshot shot, ScreenshotOptions options)
    {
        var m = shot.Metadata; var format = m.Format.HasValue ? m.Format.Value & ~AlternateFormatMarker : (uint?)null;
        if (format.HasValue && format != (uint)ScreenshotFormat.A8R8G8B8 && format != (uint)ScreenshotFormat.A2R10G10B10) throw new ProtocolException($"Unsupported XBDM screenshot format {m.Format}.");
        if (m.PitchBytes == 0 || m.PitchBytes % 4 != 0 || m.Width == 0 || m.Height == 0 || m.PitchBytes / 4 < m.Width) throw new ProtocolException("Invalid screenshot dimensions or pitch.");
        if (m.FramebufferSize != shot.TiledFramebuffer.Length || m.FramebufferSize == 0 || m.FramebufferSize % 4 != 0) throw new ProtocolException("Screenshot framebuffer size does not match the captured byte count.");
        if (options.MaximumFramebufferSize <= 0 || shot.TiledFramebuffer.Length > options.MaximumFramebufferSize) throw new ProtocolException("Screenshot framebuffer exceeds the configured decode limit.");
        var output = new byte[ImageSize(m.Width, m.Height, options.MaximumDecodedSize)]; var pitch = m.PitchBytes / 4;
        var mortonWidth = options.UntileMode == ScreenshotUntileMode.Morton ? InferMortonWidth(pitch, m.Height, shot.TiledFramebuffer.Length) : 0u;
        for (uint y = 0; y < m.Height; y++) for (uint x = 0; x < m.Width; x++)
        {
            var source = options.UntileMode == ScreenshotUntileMode.Xenos ? XenosOffset(x, y, pitch) : MortonOffset(x, y, mortonWidth);
            if (source + 4 > (ulong)shot.TiledFramebuffer.Length) throw new ProtocolException("Screenshot framebuffer is too small for its tiled dimensions.");
            var destination = checked((int)((y * m.Width + x) * 4)); var input = shot.TiledFramebuffer.AsSpan((int)source, 4);
            if (!format.HasValue || format == (uint)ScreenshotFormat.A8R8G8B8) { input.CopyTo(output.AsSpan(destination)); if (!options.PreserveAlpha) output[destination + 3] = 255; }
            else
            {
                var packed = options.Packed10BitEndian == Endian.Little ? BinaryPrimitives.ReadUInt32LittleEndian(input) : BinaryPrimitives.ReadUInt32BigEndian(input);
                output[destination] = (byte)(((packed & 0x3ff) * 255 + 511) / 1023); output[destination + 1] = (byte)((((packed >> 10) & 0x3ff) * 255 + 511) / 1023);
                output[destination + 2] = (byte)((((packed >> 20) & 0x3ff) * 255 + 511) / 1023); output[destination + 3] = options.PreserveAlpha ? (byte)((((packed >> 30) & 3) * 255 + 1) / 3) : (byte)255;
            }
        }
        return new ScreenshotImage(m, m.Width, m.Height, output);
    }
    private static int ImageSize(uint width, uint height, int maximum)
    {
        if (width == 0 || height == 0 || maximum <= 0) throw new ProtocolException("Screenshot dimensions and size limit must be nonzero.");
        var size = checked((ulong)width * height * 4); return size <= (ulong)maximum && size <= int.MaxValue ? (int)size : throw new ProtocolException("Decoded screenshot exceeds the configured size limit.");
    }
    private static ulong Align32(ulong value) => value + 31 & ~31UL;
    private static ulong XenosOffset(uint x, uint y, uint pitch)
    {
        var outer = (((ulong)(y >> 5) * (Align32(pitch) >> 5)) + (x >> 5)) << 6; var inner = ((ulong)((y >> 1) & 7) << 3) | (x & 7); var bytes = (outer | inner) << 2;
        var bank = (ulong)((y >> 4) & 1); var pipe = (ulong)(((x >> 3) & 3) ^ (((y >> 3) & 1) << 1));
        return bytes & 15 | (ulong)(y & 1) << 4 | (bytes >> 4 & 1) << 5 | pipe << 6 | (bytes >> 5 & 7) << 8 | bank << 11 | bytes >> 8 << 12;
    }
    private static uint Spread(uint value) { value &= 0xffff; value = (value | value << 8) & 0x00ff00ff; value = (value | value << 4) & 0x0f0f0f0f; value = (value | value << 2) & 0x33333333; return (value | value << 1) & 0x55555555; }
    private static ulong MortonOffset(uint x, uint y, uint width) => ((ulong)(x >> 5) + (ulong)(y >> 5) * (width >> 5)) * 4096 + (Spread(x & 31) | Spread(y & 31) << 1) * 4UL;
    private static uint InferMortonWidth(uint pitch, uint height, int size)
    {
        var minWidth = Align32(pitch); var minHeight = Align32(height); var pixels = (ulong)size / 4;
        for (var candidate = minWidth; candidate <= minWidth + 32 * 63; candidate += 32) if (pixels % candidate == 0 && pixels / candidate >= minHeight && pixels / candidate % 32 == 0) return (uint)candidate;
        return (uint)minWidth;
    }
    private static ScreenshotImage Resize(ScreenshotImage source, uint width, uint height, int maximum)
    {
        var output = new byte[ImageSize(width, height, maximum)];
        for (uint y = 0; y < height; y++) for (uint x = 0; x < width; x++)
        { var sy = (uint)((ulong)y * source.Height / height); var sx = (uint)((ulong)x * source.Width / width); source.Bgra.AsSpan((int)((sy * source.Width + sx) * 4), 4).CopyTo(output.AsSpan((int)((y * width + x) * 4))); }
        return new ScreenshotImage(source.Source, width, height, output);
    }
    private static ScreenshotImage Compose(ScreenshotImage source, uint width, uint height, ScreenshotMetadata metadata, int maximum)
    {
        if ((ulong)metadata.OffsetX + source.Width > width || (ulong)metadata.OffsetY + source.Height > height) throw new ProtocolException("Screenshot visible rectangle does not fit its display surface.");
        var output = new byte[ImageSize(width, height, maximum)]; for (var i = 3; i < output.Length; i += 4) output[i] = 255;
        var row = checked((int)source.Width * 4); for (uint y = 0; y < source.Height; y++) source.Bgra.AsSpan((int)y * row, row).CopyTo(output.AsSpan((int)(((y + metadata.OffsetY) * width + metadata.OffsetX) * 4)));
        return new ScreenshotImage(source.Source, width, height, output);
    }
}
