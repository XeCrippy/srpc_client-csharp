using System.Globalization;
using System.Text;
using SRPC.Internal;

namespace SRPC;

public sealed partial class SrpcClient
{
    private static string? FindField(string line, string key)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') { quoted = !quoted; continue; }
            if (quoted || (i != 0 && !char.IsWhiteSpace(line[i - 1])) ||
                i + key.Length > line.Length || !line.AsSpan(i, key.Length).Equals(key.AsSpan(), StringComparison.OrdinalIgnoreCase)) continue;
            var cursor = i + key.Length;
            while (cursor < line.Length && char.IsWhiteSpace(line[cursor])) cursor++;
            if (cursor == line.Length || line[cursor++] != '=') continue;
            while (cursor < line.Length && char.IsWhiteSpace(line[cursor])) cursor++;
            if (cursor < line.Length && line[cursor] == '"')
            {
                var end = line.IndexOf('"', cursor + 1);
                if (end < 0) throw new ProtocolException($"Unterminated quoted {key} field in XBDM response: {line}");
                return line[(cursor + 1)..end];
            }
            var stop = cursor;
            while (stop < line.Length && !char.IsWhiteSpace(line[stop])) stop++;
            return line[cursor..stop];
        }
        return null;
    }

    private static bool HasFlag(string line, string flag) => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(t => t.Equals(flag, StringComparison.OrdinalIgnoreCase));
    private static string RequireNonempty(string? value, string description)
    {
        value = ProtocolCodec.TrimAscii(value ?? "");
        return value.Length == 0 ? throw new ProtocolException($"XBDM did not return {description}.") : value;
    }
    private static ulong? CombineDwords(string line, string highKey, string lowKey, string description)
    {
        var high = FindField(line, highKey); var low = FindField(line, lowKey);
        if ((high is null) != (low is null)) throw new ProtocolException($"Incomplete {description} fields in XBDM response: {line}");
        return high is null ? null : ((ulong)ProtocolCodec.ParseHexUInt32(high) << 32) | ProtocolCodec.ParseHexUInt32(low!);
    }

    public string ConsoleName() => SendCommand("dbgname").Message;
    public string ConsoleId()
    {
        var response = SendCommand("getconsoleid");
        var value = ProtocolCodec.TrimAscii(FindField(response.Message, "consoleid") ?? response.Message);
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        if (value.Length == 0 || value.Any(c => !Uri.IsHexDigit(c))) throw new ProtocolException($"Invalid console ID returned by XBDM: {value}");
        return value;
    }
    public string ConsoleType()
    {
        var message = SendCommand("consoletype").Message;
        return RequireNonempty(FindField(message, "consoletype") ?? FindField(message, "type") ?? message, "a console type");
    }
    public bool IsDevkit() => ConsoleType() is var value && (value.Equals("devkit", StringComparison.OrdinalIgnoreCase) || value.Equals("testkit", StringComparison.OrdinalIgnoreCase));
    public string CpuKey()
    {
        lock (_sync)
        {
            var value = ProtocolCodec.TrimAscii(DetectProtocolLocked() == Protocol.Jrpc2
                ? JrpcCallLocked(JrpcCpuKey, 0, Array.Empty<RpcArgument>()) : SendSrpcLocked("s360 cpukey")).ToUpperInvariant();
            if (value.Length != 32 || value.Any(c => !Uri.IsHexDigit(c))) throw new ProtocolException("Console returned an invalid CPU key.");
            return value;
        }
    }
    public string Gamertag() { lock (_sync) return ProtocolCodec.TrimAscii(SendSrpcLocked("s360 gamertag")); }
    public uint TitleId() { lock (_sync) return ProtocolCodec.ParseHexUInt32(SendSrpcLocked("s360 titleid")); }
    public string TitlePath()
    {
        foreach (var line in SendMultilineCommand("xbeinfo name=")) if (FindField(line, "name") is { } name) return RequireNonempty(name, "a current title path");
        throw new ProtocolException("XBDM xbeinfo response did not contain a name field.");
    }
    public ushort KernelVersion()
    {
        lock (_sync)
        {
            var value = ProtocolCodec.ParseDecimalUInt32(SendSrpcLocked("s360 kernel"));
            return value <= ushort.MaxValue ? (ushort)value : throw new ProtocolException("Kernel version is outside the 16-bit range.");
        }
    }
    public string MotherboardType() { lock (_sync) return SendSrpcLocked("s360 motherboard"); }
    public float Temperature(TemperatureSensor sensor)
    {
        lock (_sync)
        {
            var value = ProtocolCodec.ParseFloat(SendSrpcLocked($"s360 temperature {(byte)sensor}"));
            return value == -2 ? throw new RpcException("Invalid temperature sensor.") : value < 0 ? throw new RpcException("Console did not report a temperature.") : value;
        }
    }
    public string DmVersion() => RequireNonempty(SendCommand("dmversion").Message, "a debug monitor version");
    public IReadOnlyList<string> Drives() => SendMultilineCommand("drivelist").Select(l => FindField(l, "drivename")).Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToArray();
    public IReadOnlyList<string> Modules() => SendMultilineCommand("modules");
    public IReadOnlyList<ModuleInfo> ModuleList()
    {
        uint Number(string line, string key) { try { return FindField(line, key) is { } v ? ProtocolCodec.ParseHexUInt32(v) : 0; } catch (SrpcException) { return 0; } }
        return SendMultilineCommand("modules").Where(l => !string.IsNullOrWhiteSpace(l)).Select(line =>
            new ModuleInfo(FindField(line, "name") ?? "", Number(line, "base"), Number(line, "size"), Number(line, "check"), Number(line, "timestamp"), HasFlag(line, "dll")))
            .Where(m => m.Base != 0 && m.Size != 0).ToArray();
    }
    public ModuleInfo? TitleModule() => ModuleList().FirstOrDefault(m => !m.IsDll && m.Base >= 0x82000000);
    public uint ModuleHandle(string moduleName)
    {
        if (moduleName.Length == 0) throw new ProtocolException("Module name cannot be empty.");
        var handle = CallUInt32(ResolveFunction("xam.xex", 1102), new RpcArgument[] { moduleName });
        return handle != 0 ? handle : throw new RpcException($"Module not found: {moduleName}");
    }
    public uint ProcessId()
    {
        var message = SendCommand("getpid").Message;
        return ProtocolCodec.ParseHexUInt32(FindField(message, "pid") ?? ProtocolCodec.TrimAscii(message));
    }
    public SignInState SignInState(uint userIndex = 0)
    {
        if (userIndex > 3) throw new ProtocolException("Xbox user index must be between 0 and 3.");
        var value = CallUInt32(ResolveFunction("xam.xex", 528), new RpcArgument[] { userIndex });
        return value <= (uint)SRPC.SignInState.GuestAccountXboxLive ? (SignInState)value : throw new ProtocolException($"Console returned an invalid sign-in state: {value}");
    }
    public SignInState GetSignInState(uint userIndex = 0) => SignInState(userIndex);

    public IReadOnlyList<DirectoryEntry> DirectoryContents(string remotePath)
    {
        var lines = SendMultilineCommand("dirlist name=" + ProtocolCodec.QuoteArgument(remotePath, "Remote path"));
        return lines.Where(l => l.Length != 0).Select(line =>
        {
            var name = FindField(line, "name");
            if (string.IsNullOrEmpty(name)) throw new ProtocolException($"Directory entry did not contain a valid name: {line}");
            return new DirectoryEntry(name, CombineDwords(line, "sizehi", "sizelo", "directory entry size") ?? 0,
                CombineDwords(line, "createhi", "createlo", "directory entry creation time"),
                CombineDwords(line, "changehi", "changelo", "directory entry change time"), HasFlag(line, "directory"));
        }).ToArray();
    }
    public bool IsDirectory(string remotePath) { try { DirectoryContents(remotePath); return true; } catch (CommandException ex) when (ex.StatusCode == 402) { return false; } }
    public void CreateDirectory(string remotePath) => SendCommand("mkdir name=" + ProtocolCodec.QuoteArgument(remotePath, "Remote path"));
    public void DeleteFile(string remotePath) => SendCommand("delete name=" + ProtocolCodec.QuoteArgument(remotePath, "Remote path"));
    public void DeleteDirectory(string remotePath) => SendCommand("delete name=" + ProtocolCodec.QuoteArgument(remotePath, "Remote path") + " dir");
    public void RenamePath(string oldPath, string newPath) => SendCommand($"rename name={ProtocolCodec.QuoteArgument(oldPath, "Old remote path")} newname={ProtocolCodec.QuoteArgument(newPath, "New remote path")}");

    public void DebugGo() => SendCommand("go");
    public void DebugStop() => SendCommand("stop");
    public string LoadModule(string remotePath)
    {
        if (remotePath.Length == 0) throw new ProtocolException("Remote module path cannot be empty.");
        if (Encoding.UTF8.GetByteCount(remotePath) >= 260) throw new ProtocolException("Remote module path exceeds the console's 259-byte limit.");
        var argument = remotePath.StartsWith("str:", StringComparison.OrdinalIgnoreCase) || remotePath.Any(char.IsWhiteSpace)
            ? "str:" + ProtocolCodec.HexEncode(Encoding.UTF8.GetBytes(remotePath)) : remotePath;
        return ProtocolCodec.TrimAscii(SendSrpc("s360 loadmod " + argument));
    }
    public void UnloadModule(string moduleName)
    {
        var handle = ModuleHandle(moduleName);
        if (handle > uint.MaxValue - 0x40) throw new ProtocolException("Module handle cannot be adjusted safely.");
        Write(handle + 0x40, (ushort)1, Endian.Big);
        CallUInt32(ResolveFunction("xboxkrnl.exe", 417), new RpcArgument[] { handle });
    }
    public void SetSystemTime(DateTimeOffset value)
    {
        var filetime = value.UtcDateTime.ToFileTimeUtc();
        SendSrpc($"s360 settime {(uint)((ulong)filetime >> 32):X8} {(uint)filetime:X8}");
    }
    public void SynchronizeTime() => SetSystemTime(DateTimeOffset.UtcNow);

    public uint AllocateExecutable(uint size)
    {
        if (size is 0 or > 0x10000) throw new ProtocolException("Executable allocation size must be between 1 and 0x10000 bytes.");
        string response;
        try { response = ProtocolCodec.TrimAscii(SendSrpc($"s360 alloc {size}")); }
        catch (CommandException ex) when (ex.StatusCode == 400) { throw new RpcException("Executable hook pool is full."); }
        if (response.Equals("pool_full", StringComparison.OrdinalIgnoreCase)) throw new RpcException("Executable hook pool is full.");
        var address = ProtocolCodec.ParseHexUInt32(response);
        return address != 0 && (address & 3) == 0 ? address : throw new ProtocolException("Plugin returned an invalid executable allocation address.");
    }
    public ExecutablePoolInfo ExecutablePoolInfo()
    {
        var response = SendSrpc("s360 poolinfo");
        uint Named(string name)
        {
            var marker = name + "="; var start = response.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) throw new ProtocolException($"Plugin response is missing the {name} field.");
            var text = response[(start + marker.Length)..]; if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            var count = 0; while (count < text.Length && Uri.IsHexDigit(text[count])) count++;
            return count > 0 ? ProtocolCodec.ParseHexUInt32(text[..count]) : throw new ProtocolException($"Plugin response contains an invalid {name} field.");
        }
        var result = new ExecutablePoolInfo(Named("used"), Named("free"));
        return (result.Used & 3) == 0 && result.Used <= 0x10000 && result.Free <= 0x10000 && result.Used + result.Free == 0x10000
            ? result : throw new ProtocolException("Plugin returned inconsistent executable pool usage.");
    }
    public void ResetExecutablePool(ExecutablePoolReset confirmation)
    {
        if (confirmation != SRPC.ExecutablePoolReset.ConfirmLiveAllocationsMayBeOverwritten) throw new ArgumentOutOfRangeException(nameof(confirmation));
        if (!ProtocolCodec.TrimAscii(SendSrpc("s360 poolreset")).Equals("ok", StringComparison.OrdinalIgnoreCase)) throw new ProtocolException("Plugin returned an unexpected pool-reset response.");
    }
    public void Reboot() { lock (_sync) SendCommandNoReplyLocked("magicboot cold"); }
    public void Shutdown()
    {
        lock (_sync)
        {
            if (DetectProtocolLocked() == Protocol.NativeSrpc) SendCommandNoReplyLocked("s360 shutdown");
            else SendCommandNoReplyLocked(ProtocolCodec.BuildJrpcCommand(JrpcShutdown, 0, Array.Empty<RpcArgument>()));
        }
    }
    public void LaunchXex(string titlePath, string workingDirectory)
    {
        if (titlePath.Length == 0 || workingDirectory.Length == 0) throw new ProtocolException("Title path and working directory cannot be empty.");
        lock (_sync) SendCommandNoReplyLocked($"magicboot title={ProtocolCodec.QuoteArgument(titlePath, "Title path")} directory={ProtocolCodec.QuoteArgument(workingDirectory, "Working directory")}");
    }
    public void ConstantMemorySet(uint address, uint value, uint? ifValue = null, uint? titleId = null)
    {
        lock (_sync) { DetectProtocolLocked(); JrpcCallLocked(JrpcConstantMemorySet, address, new RpcArgument[] { value, ifValue.HasValue ? 1u : 0u, ifValue ?? 0, titleId.HasValue ? 1u : 0u, titleId ?? 0 }); }
    }
    public void Notify(string text, uint type = 34)
    {
        ValidateText(text, "Notification text");
        lock (_sync) { DetectProtocolLocked(); JrpcCallLocked(JrpcXNotify, 0, new RpcArgument[] { text, type }); }
    }
    public void Notify(string text, NotificationType type) => Notify(text, (uint)type);
    public void SetLeds(LedColor q1, LedColor q2, LedColor q3, LedColor q4)
    {
        uint[] values = { (byte)q1, (byte)q2, (byte)q3, (byte)q4 };
        lock (_sync)
        {
            if (DetectProtocolLocked() == Protocol.Jrpc2) JrpcCallLocked(JrpcSetLeds, 0, values.Select(v => (RpcArgument)v).ToArray());
            else SendSrpcLocked($"s360 led {values[0]} {values[1]} {values[2]} {values[3]}");
        }
    }
}
