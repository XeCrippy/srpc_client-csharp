using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SRPC.Internal;

namespace SRPC;

public sealed partial class SrpcClient : IDisposable
{
    private const uint JrpcResolveFunction = 9, JrpcCpuKey = 10, JrpcShutdown = 11,
        JrpcXNotify = 12, JrpcKernelVersion = 13, JrpcSetLeds = 14,
        JrpcConstantMemorySet = 18, JrpcSrpcTunnel = 100;
    private const int MaximumMultilineSize = 16 * 1024 * 1024;

    private readonly object _sync = new();
    private readonly ClientOptions _options;
    private readonly XbdmConnection _connection = new();
    private Protocol _selectedProtocol;
    private bool _binaryTransferActive;
    private bool _disposed;

    public string Host { get; }
    public bool Connected { get { lock (_sync) return !_disposed && _connection.Connected; } }

    public SrpcClient(string host, ClientOptions? options = null)
    {
        _options = (options ?? new ClientOptions()).Copy();
        ValidateOptions(host, _options);
        Host = host;
        _selectedProtocol = _options.Protocol;
    }

    private static void ValidateOptions(string host, ClientOptions options)
    {
        if (string.IsNullOrEmpty(host)) throw new ProtocolException("Console host cannot be empty.");
        ValidateText(host, "Console host");
        if (options.Port == 0) throw new ProtocolException("Console port cannot be zero.");
        if (options.ConnectTimeout <= TimeSpan.Zero || options.IoTimeout <= TimeSpan.Zero ||
            options.RpcTimeout <= TimeSpan.Zero || options.PollInterval <= TimeSpan.Zero)
            throw new ProtocolException("All client timeouts and polling intervals must be positive.");
    }

    internal static void ValidateText(string text, string description)
    {
        if (text.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            throw new ProtocolException($"{description} cannot contain CR, LF, or NUL characters.");
    }

    public void Connect()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_binaryTransferActive) throw new ProtocolException("Cannot connect from inside a file-transfer progress callback.");
            if (_connection.Connected) return;
            _selectedProtocol = _options.Protocol;
            try
            {
                _connection.Connect(Host, _options.Port, _options.ConnectTimeout, _options.IoTimeout);
                ProtocolCodec.ThrowIfCommandFailed(ProtocolCodec.ParseResponseLine(_connection.ReadLine()));
            }
            catch { _connection.Close(); throw; }
        }
    }

    public void Reconnect()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_binaryTransferActive) throw new ProtocolException("Cannot reconnect from inside a file-transfer progress callback.");
            _connection.Close();
            _selectedProtocol = _options.Protocol;
            Connect();
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            if (_binaryTransferActive) return;
            _connection.Close();
            _selectedProtocol = _options.Protocol;
        }
    }

    public Protocol GetProtocol() { lock (_sync) { ThrowIfDisposed(); return DetectProtocolLocked(); } }

    public bool PluginAvailable()
    {
        lock (_sync)
        {
            try
            {
                EnsureConnectedLocked();
                if (_options.Protocol == Protocol.NativeSrpc)
                {
                    var response = SendCommandRawLocked("s360");
                    if (response.StatusCode == 202) { ReadMultilineLocked(); return false; }
                    return response.StatusCode == 200;
                }
                if (_options.Protocol == Protocol.Jrpc2)
                {
                    ProtocolCodec.ParseDecimalUInt32(JrpcCallLocked(JrpcKernelVersion, 0, Array.Empty<RpcArgument>()));
                    return true;
                }
                DetectProtocolLocked();
                return true;
            }
            catch (SrpcException) { return false; }
        }
    }

    public Response SendCommand(string command) { lock (_sync) return SendCommandLocked(command); }
    public Response SendCommandRaw(string command) { lock (_sync) return SendCommandRawLocked(command); }
    public IReadOnlyList<string> SendMultilineCommand(string command)
    {
        lock (_sync)
        {
            var response = SendCommandRawLocked(command);
            ProtocolCodec.ThrowIfCommandFailed(response);
            return response.StatusCode == 202 ? ReadMultilineLocked() : Array.Empty<string>();
        }
    }
    public string SendSrpc(string command) { lock (_sync) return SendSrpcLocked(command); }

    private Response SendCommandRawLocked(string command)
    {
        ThrowIfDisposed();
        if (_binaryTransferActive) throw new ProtocolException("A progress callback cannot start another command during a binary transfer.");
        EnsureConnectedLocked();
        ProtocolCodec.ValidateCommand(command);
        _connection.SendAll(Encoding.UTF8.GetBytes(command + "\r\n"));
        return ProtocolCodec.ParseResponseLine(_connection.ReadLine());
    }

    private Response SendCommandLocked(string command)
    {
        var response = SendCommandRawLocked(command);
        ProtocolCodec.ThrowIfCommandFailed(response);
        return response;
    }

    private void SendCommandNoReplyLocked(string command)
    {
        ThrowIfDisposed();
        if (_binaryTransferActive) throw new ProtocolException("A progress callback cannot start another command during a binary transfer.");
        EnsureConnectedLocked();
        ProtocolCodec.ValidateCommand(command);
        try { _connection.SendAll(Encoding.UTF8.GetBytes(command + "\r\n")); }
        finally { _connection.Close(); _selectedProtocol = _options.Protocol; }
    }

    private List<string> ReadMultilineLocked()
    {
        var result = new List<string>();
        var total = 0;
        while (true)
        {
            var line = _connection.ReadLine();
            if (line == ".") return result;
            total = checked(total + line.Length);
            if (total > MaximumMultilineSize) { _connection.Close(); throw new ProtocolException("XBDM multiline response exceeded 16 MiB."); }
            result.Add(line);
        }
    }

    private string JrpcSendLocked(string command)
    {
        var response = SendCommandRawLocked(command);
        if (response.StatusCode == 202)
        {
            ReadMultilineLocked();
            throw new RpcException("consolefeatures returned a multiline response; the SRPC/JRPC2 plugin does not appear to be loaded.");
        }
        ProtocolCodec.ThrowIfCommandFailed(response);
        return response.Message;
    }

    private string JrpcCallLocked(uint type, uint address, IReadOnlyList<RpcArgument> arguments,
        CallOptions? options = null, (string Module, uint Ordinal)? target = null)
    {
        var response = JrpcSendLocked(ProtocolCodec.BuildJrpcCommand(type, address, arguments, options, target));
        var deadline = DateTime.UtcNow + _options.RpcTimeout;
        while (ProtocolCodec.ParseJrpcPendingAddress(response) is uint pending)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new SrpcTimeoutException($"JRPC2 call timed out after {_options.RpcTimeout.TotalMilliseconds:0} ms.");
            Thread.Sleep(remaining < _options.PollInterval ? remaining : _options.PollInterval);
            response = JrpcSendLocked(ProtocolCodec.BuildJrpcPollCommand(pending));
        }
        ProtocolCodec.ThrowIfJrpcFailed(response);
        return response;
    }

    private Protocol DetectProtocolLocked()
    {
        EnsureConnectedLocked();
        if (_selectedProtocol != Protocol.Automatic) return _selectedProtocol;
        var native = SendCommandRawLocked("s360");
        if (native.StatusCode == 200) return _selectedProtocol = Protocol.NativeSrpc;
        if (native.StatusCode == 202) ReadMultilineLocked();
        try { ProtocolCodec.ParseDecimalUInt32(JrpcCallLocked(JrpcKernelVersion, 0, Array.Empty<RpcArgument>())); }
        catch (SrpcException ex) { throw new RpcException($"Console answered neither native SRPC nor JRPC2; is the SRPC plugin loaded? Details: {ex.Message}", ex); }
        return _selectedProtocol = Protocol.Jrpc2;
    }

    private string SendSrpcLocked(string command)
    {
        var normalized = ProtocolCodec.NormalizeSrpcCommand(command);
        if (DetectProtocolLocked() == Protocol.NativeSrpc) return SendCommandLocked(normalized).Message;
        return ProtocolCodec.TrimAscii(JrpcCallLocked(JrpcSrpcTunnel, 0, new RpcArgument[] { normalized }));
    }

    public RpcValue Call(uint address, ReturnType returnType = ReturnType.Int32,
        IReadOnlyList<RpcArgument>? arguments = null, CallOptions? options = null)
    {
        lock (_sync) return CallLocked(address, returnType, arguments ?? Array.Empty<RpcArgument>(), options ?? new CallOptions(), null);
    }

    public RpcValue Call(string module, uint ordinal, ReturnType returnType = ReturnType.Int32,
        IReadOnlyList<RpcArgument>? arguments = null, CallOptions? options = null)
    {
        lock (_sync) return CallLocked(0, returnType, arguments ?? Array.Empty<RpcArgument>(), options ?? new CallOptions(), (module, ordinal));
    }

    private RpcValue CallLocked(uint address, ReturnType returnType, IReadOnlyList<RpcArgument> arguments,
        CallOptions options, (string Module, uint Ordinal)? target)
    {
        if (DetectProtocolLocked() == Protocol.NativeSrpc)
        {
            if (target is { } t) address = ResolveFunctionLocked(t.Module, t.Ordinal);
            if (returnType is not (ReturnType.Void or ReturnType.Int32 or ReturnType.Byte or ReturnType.Float32))
                throw new ProtocolException("Native SRPC only returns the 32-bit r3 word; strings, 64-bit values, and arrays require JRPC2.");
            if (!options.SystemThread || options.VirtualMachine || options.ArraySize != 0)
                throw new ProtocolException("Thread selection, VM calls, and array sizing require JRPC2.");
            var response = SendSrpcLocked(ProtocolCodec.BuildNativeCall(address, arguments));
            var deadline = DateTime.UtcNow + _options.RpcTimeout;
            while (ProtocolCodec.TrimAscii(response) == "pending")
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new SrpcTimeoutException($"Native SRPC call timed out after {_options.RpcTimeout.TotalMilliseconds:0} ms.");
                Thread.Sleep(remaining < _options.PollInterval ? remaining : _options.PollInterval);
                response = SendSrpcLocked("s360 poll");
            }
            var word = ProtocolCodec.ParseNativeWord(response);
            return new RpcValue(returnType switch { ReturnType.Void => null, ReturnType.Byte => word & 0xffu, ReturnType.Float32 => BitConverter.UInt32BitsToSingle(word), _ => word });
        }
        if (returnType is ReturnType.Int32Array or ReturnType.FloatArray or ReturnType.ByteArray && options.ArraySize == 0)
            throw new ProtocolException("JRPC2 array returns require CallOptions.ArraySize > 0.");
        return ProtocolCodec.ParseJrpcResult(JrpcCallLocked((uint)returnType, address, arguments, options, target), returnType);
    }

    public uint CallUInt32(uint address, IReadOnlyList<RpcArgument>? arguments = null, CallOptions? options = null) => Call(address, ReturnType.Int32, arguments, options).As<uint>();
    public ulong CallUInt64(uint address, IReadOnlyList<RpcArgument>? arguments = null, CallOptions? options = null) => Call(address, ReturnType.UInt64, arguments, options).As<ulong>();
    public float CallFloat(uint address, IReadOnlyList<RpcArgument>? arguments = null, CallOptions? options = null) => Call(address, ReturnType.Float32, arguments, options).As<float>();
    public string CallString(uint address, IReadOnlyList<RpcArgument>? arguments = null, CallOptions? options = null) => Call(address, ReturnType.String, arguments, options).As<string>();
    public void CallVoid(uint address, IReadOnlyList<RpcArgument>? arguments = null, CallOptions? options = null) => Call(address, ReturnType.Void, arguments, options);

    public uint ResolveFunction(string module, uint ordinal) { lock (_sync) return ResolveFunctionLocked(module, ordinal); }
    private uint ResolveFunctionLocked(string module, uint ordinal)
    {
        if (string.IsNullOrEmpty(module)) throw new ProtocolException("Module name cannot be empty.");
        ValidateText(module, "Module name");
        if (DetectProtocolLocked() == Protocol.Jrpc2)
            return ProtocolCodec.ParseHexUInt32(JrpcCallLocked(JrpcResolveFunction, 0, new RpcArgument[] { module, ordinal }));
        return ProtocolCodec.ParseHexUInt32(SendSrpcLocked($"s360 resolve str:{ProtocolCodec.HexEncode(Encoding.UTF8.GetBytes(module))} {ordinal}"));
    }

    private void EnsureConnectedLocked() { if (!_connection.Connected) throw new ConnectionException("Not connected to a console."); }
    private void AbandonBinaryLocked() { _binaryTransferActive = false; _connection.Close(); _selectedProtocol = _options.Protocol; }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(SrpcClient)); }
    public void Dispose() { lock (_sync) { if (_disposed) return; _disposed = true; _binaryTransferActive = false; _connection.Dispose(); } }
}
