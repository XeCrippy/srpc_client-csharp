using System.Globalization;
using System.Text;

namespace SRPC.Internal;

internal static class ProtocolCodec
{
    internal const int MaxCommandLength = 480;
    internal const int SetMemoryChunkSize = 128;

    internal static string TrimAscii(string text) => text.Trim(' ', '\t', '\r', '\n', '\v', '\f');

    internal static Response ParseResponseLine(string line)
    {
        line = line.TrimEnd('\r', '\n');
        if (line.Length < 4 || !char.IsAsciiDigit(line[0]) || !char.IsAsciiDigit(line[1]) ||
            !char.IsAsciiDigit(line[2]) || (line[3] != '-' && line[3] != ' '))
            throw new ProtocolException($"Malformed XBDM response line: {line}");
        return new Response((line[0] - '0') * 100 + (line[1] - '0') * 10 + line[2] - '0', TrimAscii(line[4..]));
    }

    internal static void ValidateCommand(string command)
    {
        if (command.Length == 0) throw new ProtocolException("Command cannot be empty.");
        if (command.Length > MaxCommandLength)
            throw new ProtocolException($"Command is {command.Length} characters; XBDM's safe limit is {MaxCommandLength}.");
        if (command.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            throw new ProtocolException("Commands cannot contain CR, LF, or NUL characters.");
    }

    internal static string QuoteArgument(string value, string description)
    {
        if (value.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
            throw new ProtocolException($"{description} cannot contain a quote, CR, LF, or NUL character.");
        return $"\"{value}\"";
    }

    internal static void ThrowIfCommandFailed(Response response)
    {
        if (!response.IsSuccess) throw new CommandException(response.StatusCode, response.Message);
    }

    internal static string NormalizeSrpcCommand(string command)
    {
        var cleaned = TrimAscii(command);
        if (cleaned.Length == 0) return "s360";
        if (!(cleaned.StartsWith("s360", StringComparison.OrdinalIgnoreCase) &&
              (cleaned.Length == 4 || char.IsWhiteSpace(cleaned[4]))))
            cleaned = "s360 " + cleaned;
        ValidateCommand(cleaned);
        return cleaned;
    }

    internal static string HexEncode(ReadOnlySpan<byte> bytes, bool upper = true)
    {
        var value = Convert.ToHexString(bytes);
        return upper ? value : value.ToLowerInvariant();
    }

    internal static byte[] HexDecode(string text)
    {
        var compact = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if ((compact.Length & 1) != 0) throw new ProtocolException("Hex response has an odd number of digits.");
        try { return Convert.FromHexString(compact); }
        catch (FormatException ex) { throw new ProtocolException("Hex response contains a non-hexadecimal character.", ex); }
    }

    internal static string BuildJrpcCommand(uint type, uint address, IReadOnlyList<RpcArgument> arguments,
        CallOptions? options = null, (string Module, uint Ordinal)? target = null)
    {
        options ??= new CallOptions();
        if (arguments.Count > 37) throw new ProtocolException("JRPC2 supports at most 37 arguments.");
        if (target is { } t)
        {
            ValidateCommand(t.Module);
            if (t.Module.Contains('"')) throw new ProtocolException("JRPC2 module names cannot contain quotes.");
        }
        var result = new StringBuilder("consolefeatures ver=2 type=").Append(type);
        if (options.SystemThread) result.Append(" system");
        if (target is { } module) result.Append(" module=\"").Append(module.Module).Append("\" ord=").Append(module.Ordinal);
        if (options.VirtualMachine) result.Append(" VM");
        result.Append(" as=").Append(options.ArraySize).Append(" params=\"A\\")
            .Append(address.ToString("X", CultureInfo.InvariantCulture)).Append("\\A\\").Append(arguments.Count).Append('\\');
        foreach (var argument in arguments) result.Append(EncodeJrpcArgument(argument));
        result.Append('"');
        var command = result.ToString();
        ValidateCommand(command);
        return command;
    }

    private static string EncodeJrpcArgument(RpcArgument argument)
    {
        return argument.Value switch
        {
            bool value => $"1\\{(value ? 1 : 0)}\\",
            int value => $"1\\{unchecked((uint)value)}\\",
            uint value => $"1\\{value}\\",
            long value => $"8\\{value}\\",
            ulong value => $"8\\{unchecked((long)value)}\\",
            float value when float.IsFinite(value) => $"3\\{value.ToString("R", CultureInfo.InvariantCulture)}\\",
            float => throw new ProtocolException("JRPC2 floating arguments must be finite."),
            string value => $"2/{Encoding.UTF8.GetByteCount(value)}\\{HexEncode(Encoding.UTF8.GetBytes(value))}\\",
            byte[] value => $"7/{value.Length}\\{HexEncode(value)}\\",
            _ => throw new ProtocolException("Unsupported RPC argument type.")
        };
    }

    internal static string BuildJrpcPollCommand(uint address) => $"consolefeatures buf_addr=0x{address:X8}";

    internal static uint? ParseJrpcPendingAddress(string response)
    {
        const string marker = "buf_addr=";
        var index = response.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;
        var value = response[(index + marker.Length)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        return ParseHexUInt32(value);
    }

    internal static void ThrowIfJrpcFailed(string response)
    {
        var cleaned = TrimAscii(response);
        if (cleaned.StartsWith("error=", StringComparison.Ordinal)) throw new RpcException(TrimAscii(cleaned[6..]));
    }

    internal static RpcValue ParseJrpcResult(string response, ReturnType type)
    {
        ThrowIfJrpcFailed(response);
        var text = TrimAscii(response);
        string[] Values() => text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return type switch
        {
            ReturnType.Void => new RpcValue(null),
            ReturnType.Int32 or ReturnType.Byte => new RpcValue(ParseHexUInt32(text)),
            ReturnType.UInt64 => new RpcValue(ParseHexUInt64(text)),
            ReturnType.Float32 => new RpcValue(ParseFloat(text)),
            ReturnType.String => new RpcValue(text),
            ReturnType.Int32Array => new RpcValue(Values().Select(ParseHexUInt32).ToArray()),
            ReturnType.FloatArray => new RpcValue(Values().Select(ParseFloat).ToArray()),
            ReturnType.ByteArray => new RpcValue(Values().Select(value =>
            {
                var parsed = ParseDecimalUInt32(value);
                if (parsed > byte.MaxValue) throw new ProtocolException("JRPC2 byte-array element is outside 0..255.");
                return (byte)parsed;
            }).ToArray()),
            _ => throw new ProtocolException("Unknown JRPC2 return type.")
        };
    }

    internal static string BuildNativeCall(uint address, IReadOnlyList<RpcArgument> arguments)
    {
        var command = new StringBuilder($"s360 {address:X8}");
        foreach (var argument in arguments) command.Append(' ').Append(EncodeNativeArgument(argument));
        ValidateCommand(command.ToString());
        return command.ToString();
    }

    private static string EncodeNativeArgument(RpcArgument argument) => argument.Value switch
    {
        bool value => value ? "1" : "0",
        int value => unchecked((uint)value).ToString(CultureInfo.InvariantCulture),
        uint value => value.ToString(CultureInfo.InvariantCulture),
        long or ulong => throw new ProtocolException("Native SRPC only supports 32-bit integer arguments."),
        float value => $"flt:{BitConverter.SingleToUInt32Bits(value):X8}",
        string value => "str:" + HexEncode(Encoding.UTF8.GetBytes(value)),
        byte[] value => "str:" + HexEncode(value),
        _ => throw new ProtocolException("Unsupported RPC argument type.")
    };

    internal static uint ParseNativeWord(string response)
    {
        var text = TrimAscii(response);
        if (text == "idle") return 0;
        if (text == "pending") throw new ProtocolException("Native SRPC call is still pending.");
        if (text.StartsWith("err=", StringComparison.Ordinal))
            throw new RpcException($"Remote function returned error 0x{ParseHexUInt32(text[4..]):X8}");
        return ParseHexUInt32(text);
    }

    internal static uint ParseHexUInt32(string text) => ParseUInt32(text, NumberStyles.AllowHexSpecifier, "32-bit hexadecimal value");
    internal static ulong ParseHexUInt64(string text)
    {
        text = TrimAscii(text);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.StartsWith("0q", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (!ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            throw new ProtocolException($"Invalid 64-bit hexadecimal value: {text}");
        return value;
    }
    internal static uint ParseDecimalUInt32(string text) => ParseUInt32(text, NumberStyles.None, "unsigned decimal value");
    private static uint ParseUInt32(string text, NumberStyles styles, string description)
    {
        text = TrimAscii(text);
        if (styles == NumberStyles.AllowHexSpecifier && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (!uint.TryParse(text, styles, CultureInfo.InvariantCulture, out var value))
            throw new ProtocolException($"Invalid {description}: {text}");
        return value;
    }
    internal static float ParseFloat(string text)
    {
        if (!float.TryParse(TrimAscii(text), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
            throw new ProtocolException($"Invalid floating-point value: {text}");
        return value;
    }
}
