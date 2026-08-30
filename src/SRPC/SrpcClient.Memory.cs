using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SRPC.Internal;

namespace SRPC;

public sealed partial class SrpcClient
{
    private static void ValidateMemoryRange(uint address, ulong size, string operation = "Memory operation")
    {
        if (size > uint.MaxValue) throw new ProtocolException($"{operation} cannot exceed the 32-bit address space.");
        if (size != 0 && size - 1 > uint.MaxValue - (ulong)address) throw new ProtocolException($"{operation} crosses the 32-bit address boundary.");
    }

    private static string MemoryCommand(string name, uint address, int size) => $"{name} addr=0x{address:X} length={size}";

    public byte[] ReadMemory(uint address, int size)
    {
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        ValidateMemoryRange(address, (ulong)size);
        if (size == 0) return Array.Empty<byte>();
        lock (_sync)
        {
            var response = SendCommandRawLocked(MemoryCommand("getmemex", address, size));
            if (response.StatusCode == 203) return ReadGetMemExBody(address, size);
            if (response.StatusCode == 202)
            {
                var text = string.Concat(ReadMultilineLocked());
                try { var bytes = ProtocolCodec.HexDecode(text); if (bytes.Length == size) return bytes; } catch (ProtocolException) { }
                return ReadMemoryTextLocked(address, size);
            }
            if (response.StatusCode == 407) return ReadMemoryTextLocked(address, size);
            ProtocolCodec.ThrowIfCommandFailed(response);
            throw new ProtocolException($"getmemex returned unexpected status {response.StatusCode}.");
        }
    }

    private byte[] ReadGetMemExBody(uint address, int size)
    {
        using var output = new MemoryStream(size);
        while (output.Length < size)
        {
            var header = _connection.ReadExact(2);
            var count = header[0] | header[1] << 8;
            var last = (count & 0x8000) != 0;
            var length = count & 0x7fff;
            if (length > size - output.Length) throw new ProtocolException($"getmemex announced a {length} byte block that overruns the {size} bytes requested.");
            if (length != 0) output.Write(_connection.ReadExact(length));
            if (last || length == 0) break;
        }
        if (output.Length != size)
            throw new ProtocolException($"getmemex returned {output.Length} of {size} bytes from 0x{address:X}; memory is unmapped at 0x{address + (uint)output.Length:X}.");
        return output.ToArray();
    }

    private byte[] ReadMemoryTextLocked(uint address, int size)
    {
        var response = SendCommandRawLocked(MemoryCommand("getmem", address, size));
        ProtocolCodec.ThrowIfCommandFailed(response);
        if (response.StatusCode != 202) throw new ProtocolException($"getmem returned status {response.StatusCode} instead of 202.");
        var bytes = ProtocolCodec.HexDecode(string.Concat(ReadMultilineLocked()));
        if (bytes.Length != size) throw new ProtocolException($"getmem returned {bytes.Length} bytes; expected {size}.");
        return bytes;
    }

    public void WriteMemory(uint address, ReadOnlySpan<byte> data)
    {
        ValidateMemoryRange(address, (ulong)data.Length);
        if (data.IsEmpty) return;
        lock (_sync)
        {
            for (var offset = 0; offset < data.Length; offset += ProtocolCodec.SetMemoryChunkSize)
            {
                var count = Math.Min(ProtocolCodec.SetMemoryChunkSize, data.Length - offset);
                var response = SendCommandLocked($"setmem addr=0x{address + (uint)offset:X} data={ProtocolCodec.HexEncode(data.Slice(offset, count), false)}");
                if (response.StatusCode != 200) throw new ProtocolException($"setmem returned status {response.StatusCode} instead of 200.");
            }
        }
    }

    public byte[] ReadMemoryChunked(uint address, int size, int chunkSize = 1024 * 1024)
    {
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (chunkSize <= 0) throw new ProtocolException("Memory read chunk size must be greater than zero.");
        ValidateMemoryRange(address, (ulong)size, "Chunked memory read");
        var result = new byte[size];
        for (var offset = 0; offset < size; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, size - offset);
            ReadMemory(address + (uint)offset, count).CopyTo(result, offset);
        }
        return result;
    }

    public byte[] ReadMemorySparse(uint address, int size, IReadOnlyCollection<MemoryRegion> readableRegions,
        int chunkSize = 1024 * 1024, Func<int, int, bool>? progress = null)
    {
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (chunkSize <= 0) throw new ProtocolException("Sparse memory read chunk size must be greater than zero.");
        ValidateMemoryRange(address, (ulong)size, "Sparse memory read");
        var begin = (ulong)address;
        var end = begin + (ulong)size;
        var intervals = readableRegions.Select(r =>
        {
            if (r.End > 1UL << 32) throw new ProtocolException("Sparse memory read received a region outside the 32-bit address space.");
            return (Begin: Math.Max(begin, r.Base), End: Math.Min(end, r.End));
        }).Where(i => i.Begin < i.End).OrderBy(i => i.Begin).ThenBy(i => i.End).ToList();
        var normalized = new List<(ulong Begin, ulong End)>();
        foreach (var interval in intervals)
        {
            if (normalized.Count != 0 && interval.Begin < normalized[^1].End)
                normalized[^1] = (normalized[^1].Begin, Math.Max(normalized[^1].End, interval.End));
            else normalized.Add(interval);
        }
        var result = new byte[size];
        ulong cursor = begin;
        Report(0);
        void Report(int processed) { if (progress?.Invoke(processed, size) == false) throw new SrpcException("Sparse memory read was cancelled by its progress callback."); }
        void AdvanceGap(ulong gapEnd)
        {
            while (cursor < gapEnd) { cursor += Math.Min((ulong)chunkSize, gapEnd - cursor); Report((int)(cursor - begin)); }
        }
        foreach (var interval in normalized)
        {
            AdvanceGap(interval.Begin);
            while (cursor < interval.End)
            {
                var count = (int)Math.Min((ulong)chunkSize, interval.End - cursor);
                ReadMemory((uint)cursor, count).CopyTo(result, (int)(cursor - begin));
                cursor += (ulong)count;
                Report((int)(cursor - begin));
            }
        }
        AdvanceGap(end);
        return result;
    }

    public byte[] ReadMemorySparse(uint address, int size, int chunkSize = 1024 * 1024, Func<int, int, bool>? progress = null) =>
        ReadMemorySparse(address, size, MemoryRegions(), chunkSize, progress);

    public IReadOnlyList<MemoryRegion> MemoryRegions()
    {
        static uint? Field(string line, string name)
        {
            var marker = name + "=";
            var position = line.IndexOf(marker, StringComparison.Ordinal);
            if (position < 0) return null;
            var text = line[(position + marker.Length)..];
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            var length = 0;
            while (length < text.Length && Uri.IsHexDigit(text[length])) length++;
            if (length == 0) throw new ProtocolException($"walkmem returned an invalid {name} field.");
            return ProtocolCodec.ParseHexUInt32(text[..length]);
        }
        return SendMultilineCommand("walkmem").Select(line =>
        {
            var @base = Field(line, "base"); var regionSize = Field(line, "size");
            return @base.HasValue && regionSize.HasValue ? new MemoryRegion(@base.Value, regionSize.Value, Field(line, "protect") ?? 0) : null;
        }).Where(r => r is not null).Cast<MemoryRegion>().OrderBy(r => r.Base).ThenBy(r => r.Size).ThenBy(r => r.Protection).ToArray();
    }

    public bool IsValidAddress(uint address)
    {
        try { ReadMemory(address, 1); return true; }
        catch (CommandException ex) when (ex.StatusCode == 404) { return false; }
    }

    public void FillMemory(uint address, int size, byte value)
    {
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        ValidateMemoryRange(address, (ulong)size, "Memory fill");
        var block = Enumerable.Repeat(value, ProtocolCodec.SetMemoryChunkSize).ToArray();
        for (var offset = 0; offset < size; offset += block.Length) WriteMemory(address + (uint)offset, block.AsSpan(0, Math.Min(block.Length, size - offset)));
    }
    public void ZeroMemory(uint address, int size) => FillMemory(address, size, 0);

    public string ReadCString(uint address, int maximumLength = 256)
    {
        if (maximumLength < 0) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        var bytes = ReadMemory(address, maximumLength);
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }
    public void WriteCString(uint address, string value, bool nullTerminate = true)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (nullTerminate) Array.Resize(ref bytes, bytes.Length + 1);
        WriteMemory(address, bytes);
    }
    public string ReadUtf16String(uint address, int maximumCharacters = 256, Endian endian = Endian.Big)
    {
        if (maximumCharacters < 0) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        var bytes = ReadMemory(address, checked(maximumCharacters * 2));
        if ((endian == Endian.Big) == BitConverter.IsLittleEndian)
            for (var i = 0; i < bytes.Length; i += 2) (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
        var text = Encoding.Unicode.GetString(bytes);
        var end = text.IndexOf('\0');
        return end < 0 ? text : text[..end];
    }
    public void WriteUtf16String(uint address, string value, Endian endian = Endian.Big, bool nullTerminate = true)
    {
        var bytes = Encoding.Unicode.GetBytes(nullTerminate ? value + "\0" : value);
        if ((endian == Endian.Big) == BitConverter.IsLittleEndian)
            for (var i = 0; i < bytes.Length; i += 2) (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
        WriteMemory(address, bytes);
    }

    public T Read<T>(uint address, Endian endian = Endian.Big) where T : unmanaged
    {
        ValidateScalar<T>();
        var bytes = ReadMemory(address, Unsafe.SizeOf<T>());
        if (bytes.Length > 1 && (endian == Endian.Big) == BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return MemoryMarshal.Read<T>(bytes);
    }
    public void Write<T>(uint address, T value, Endian endian = Endian.Big) where T : unmanaged
    {
        ValidateScalar<T>();
        var bytes = new byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(bytes, in value);
        if (bytes.Length > 1 && (endian == Endian.Big) == BitConverter.IsLittleEndian) Array.Reverse(bytes);
        WriteMemory(address, bytes);
    }
    public T[] ReadArray<T>(uint address, int count, Endian endian = Endian.Big) where T : unmanaged
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        ValidateScalar<T>();
        var itemSize = Unsafe.SizeOf<T>();
        var bytes = ReadMemory(address, checked(count * itemSize));
        var result = new T[count];
        for (var i = 0; i < count; i++)
        {
            var item = bytes.AsSpan(i * itemSize, itemSize);
            if (itemSize > 1 && (endian == Endian.Big) == BitConverter.IsLittleEndian) item.Reverse();
            result[i] = MemoryMarshal.Read<T>(item);
        }
        return result;
    }
    public void WriteArray<T>(uint address, ReadOnlySpan<T> values, Endian endian = Endian.Big) where T : unmanaged
    {
        ValidateScalar<T>();
        var itemSize = Unsafe.SizeOf<T>();
        var bytes = new byte[checked(values.Length * itemSize)];
        MemoryMarshal.AsBytes(values).CopyTo(bytes);
        if (itemSize > 1 && (endian == Endian.Big) == BitConverter.IsLittleEndian)
            for (var i = 0; i < bytes.Length; i += itemSize) bytes.AsSpan(i, itemSize).Reverse();
        WriteMemory(address, bytes);
    }
    private static void ValidateScalar<T>() where T : unmanaged
    {
        var type = typeof(T);
        if (!(type.IsEnum || type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
              type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
              type == typeof(float) || type == typeof(double) || type == typeof(char)))
            throw new ProtocolException($"{type.Name} is not a supported scalar memory type.");
    }

    public void WriteBranch(uint address, uint destination, bool linked = false)
    {
        RequireAligned(address, "Branch address"); RequireAligned(destination, "Branch destination");
        var offset = (long)destination - address;
        if (offset is < -0x02000000L or > 0x01FFFFFCL) throw new ProtocolException("Relative branch destination is outside the signed 26-bit range.");
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 0x48000000u | (unchecked((uint)offset) & 0x03fffffcu) | (linked ? 1u : 0u));
        WriteMemory(address, bytes);
    }
    public void WriteJump(uint address, uint destination, byte scratchRegister = 11, bool linked = false)
    {
        RequireAligned(address, "Jump address"); RequireAligned(destination, "Jump destination");
        if (scratchRegister > 31) throw new ProtocolException("Jump scratch register must be in the range 0..31.");
        ValidateMemoryRange(address, 16, "Far jump write");
        var r = (uint)scratchRegister;
        uint[] words = { 0x3c000000u | r << 21 | destination >> 16, 0x60000000u | r << 21 | r << 16 | destination & 0xffff,
            31u << 26 | r << 21 | 9u << 16 | 467u << 1, 0x4e800420u | (linked ? 1u : 0u) };
        var bytes = new byte[16];
        for (var i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), words[i]);
        WriteMemory(address, bytes);
    }
    private static void RequireAligned(uint address, string description) { if ((address & 3) != 0) throw new ProtocolException($"{description} must be aligned to four bytes."); }
}
