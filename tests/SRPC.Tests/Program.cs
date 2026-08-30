using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SRPC;
using SRPC.Internal;

var tests = new (string Name, Action Run)[]
{
    ("protocol", ProtocolTests),
    ("debug protocol", DebugProtocolTests),
    ("PNG", PngTests),
    ("screenshot", ScreenshotTests),
    ("client integration", ClientIntegrationTests),
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex}"); }
}
return failed == 0 ? 0 : 1;

static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }

static void ProtocolTests()
{
    var response = ProtocolCodec.ParseResponseLine("201- connected\r\n"); Require(response.StatusCode == 201 && response.Message == "connected" && response.IsSuccess, "response parse");
    Require(ProtocolCodec.HexEncode(new byte[] { 0, 0x12, 0xab, 0xff }) == "0012ABFF", "hex encode");
    Require(ProtocolCodec.HexDecode("00 12\r\nABff").SequenceEqual(new byte[] { 0, 0x12, 0xab, 0xff }), "hex decode");
    Throws<ProtocolException>(() => ProtocolCodec.HexDecode("123"));
    var command = ProtocolCodec.BuildJrpcCommand(1, 0x82345678, new RpcArgument[] { 5u, 7u });
    Require(command == "consolefeatures ver=2 type=1 system as=0 params=\"A\\82345678\\A\\2\\1\\5\\1\\7\\\"", "JRPC command");
    Require(ProtocolCodec.BuildNativeCall(0x82345678, new RpcArgument[] { 1u, 1f, "A" }) == "s360 82345678 1 flt:3F800000 str:41", "native command");
    Require(ProtocolCodec.ParseNativeWord("00000010") == 0x10, "native result is hex");
    Require(ProtocolCodec.ParseJrpcResult("1A,2B,3C;", ReturnType.Int32Array).As<uint[]>().SequenceEqual(new uint[] { 0x1a, 0x2b, 0x3c }), "JRPC array");
    Throws<ProtocolException>(() => ProtocolCodec.ValidateCommand("getpid\r\nbye"));
}

static void DebugProtocolTests()
{
    Require(DebugProtocol.ParseNumber("0qFFFFFFFFFFFFFFFF") == ulong.MaxValue, "0q number");
    Require(DebugProtocol.DataBreakpoint(0x82a00000, 4, BreakType.Read, false) == "break read=0x82a00000 size=4", "data breakpoint");
    var context = DebugProtocol.ParseContext(new[] { "Iar=0x82123456 Gpr3=0q0000000082a00000 Fpr1=0q3ff0000000000000" });
    Require(context.Valid && context.Iar == 0x82123456 && context.Argument(0) == 0x82a00000 && context.FloatArgument(0) == 1, "context parse");
    Require(DebugProtocol.ParseThreadIds(new[] { "-83886016" })[0] == 0xfb000040, "signed thread id");
    var evt = DebugProtocol.ParseEvent("databreak read=0x82a00000 addr=0x82123456 thread=0x4");
    Require(evt.Type == DebugEventType.DataBreak && evt.Access == BreakType.Read && evt.DataAddress == 0x82a00000 && evt.HaltsTitle, "debug event");
}

static void PngTests()
{
    var pixels = new byte[] { 0x10, 0x20, 0x30, 0x40 };
    var png = ImageCodec.EncodePng(new ImageView(pixels, 1, 1, 4));
    Require(png.Take(8).SequenceEqual(new byte[] { 0x89, 80, 78, 71, 13, 10, 26, 10 }), "PNG signature");
    Require(Encoding.ASCII.GetString(png, 12, 4) == "IHDR" && BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)) == 1, "PNG header");
    Throws<ProtocolException>(() => ImageCodec.EncodePng(new ImageView(pixels, 2, 1, 4)));
}

static void ScreenshotTests()
{
    var tiled = new byte[0x1000]; tiled[0xffc] = 0x11; tiled[0xffd] = 0x22; tiled[0xffe] = 0x33; tiled[0xfff] = 0x44;
    var metadata = new ScreenshotMetadata { PitchBytes = 0x80, Width = 0x20, Height = 0x20, FramebufferSize = 0x1000, DisplayWidth = 0x20, DisplayHeight = 0x20 };
    var image = ScreenshotCodec.Decode(new RawScreenshot(metadata, tiled));
    Require(image.Width == 32 && image.Height == 32 && image.Bgra.AsSpan(0xfbc, 4).SequenceEqual(new byte[] { 0x11, 0x22, 0x33, 0xff }), "Xenos untile");
}

static void ClientIntegrationTests()
{
    var payload = Enumerable.Range(0, 0x900).Select(i => (byte)(i * 7 + 1)).ToArray();
    using var server = new ScriptedServer(stream =>
    {
        Send(stream, "201- connected\r\n");
        Require(ReadLine(stream) == "dbgname", "dbgname command"); Send(stream, "200- Development Kit\r\n");
        Require(ReadLine(stream) == "getmemex addr=0x82000000 length=2304", "getmemex command"); Send(stream, "203- binary response follows\r\n");
        for (var offset = 0; offset < payload.Length; offset += 0x400)
        {
            var count = Math.Min(0x400, payload.Length - offset); var header = count | (offset + count == payload.Length ? 0x8000 : 0);
            stream.WriteByte((byte)header); stream.WriteByte((byte)(header >> 8)); stream.Write(payload, offset, count);
        }
        Require(ReadLine(stream) == "setmem addr=0x82001000 data=deadbeef", "setmem command"); Send(stream, "200- OK\r\n");
        Require(ReadLine(stream) == "getfile name=\"Hdd:\\sample.bin\"", "getfile command"); Send(stream, "203- binary response follows\r\n");
        Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(size, 3); stream.Write(size); stream.Write(new byte[] { 1, 2, 3 });
        Require(ReadLine(stream) == "sendfile name=\"Hdd:\\upload.bin\" length=0x3", "sendfile command"); Send(stream, "204- send binary data\r\n"); Require(ReadExact(stream, 3).SequenceEqual(new byte[] { 4, 5, 6 }), "upload body"); Send(stream, "200- OK\r\n");
        var rpc = ReadLine(stream); Require(rpc.StartsWith("consolefeatures ver=2 type=1 system as=0", StringComparison.Ordinal), "JRPC command"); Send(stream, "200- 89ABCDEF\r\n");
    });
    var options = new ClientOptions { Port = server.Port, Protocol = Protocol.Jrpc2, ConnectTimeout = TimeSpan.FromSeconds(2), IoTimeout = TimeSpan.FromSeconds(2) };
    using var client = new SrpcClient("127.0.0.1", options); client.Connect(); Require(client.ConsoleName() == "Development Kit", "console name");
    Require(client.ReadMemory(0x82000000, payload.Length).SequenceEqual(payload), "framed memory read"); client.WriteMemory(0x82001000, new byte[] { 0xde, 0xad, 0xbe, 0xef });
    Require(client.DownloadFile("Hdd:\\sample.bin").SequenceEqual(new byte[] { 1, 2, 3 }), "file download"); client.UploadFile("Hdd:\\upload.bin", new byte[] { 4, 5, 6 });
    Require(client.CallUInt32(0x82345678, new RpcArgument[] { 5u }) == 0x89abcdef, "JRPC return"); server.Finish();
}

static void Send(Stream stream, string text) => stream.Write(Encoding.ASCII.GetBytes(text));
static string ReadLine(Stream stream)
{
    var bytes = new List<byte>(); while (true) { var value = stream.ReadByte(); if (value < 0) throw new EndOfStreamException(); if (value == '\n') break; if (value != '\r') bytes.Add((byte)value); } return Encoding.ASCII.GetString(bytes.ToArray());
}
static byte[] ReadExact(Stream stream, int size) { var result = new byte[size]; stream.ReadExactly(result); return result; }

sealed class ScriptedServer : IDisposable
{
    private readonly TcpListener _listener; private readonly Task _task; public ushort Port { get; }
    public ScriptedServer(Action<NetworkStream> script)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0); _listener.Start(); Port = (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;
        _task = Task.Run(() => { using var socket = _listener.AcceptTcpClient(); using var stream = socket.GetStream(); script(stream); });
    }
    public void Finish() { if (!_task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Scripted server did not finish."); _task.GetAwaiter().GetResult(); }
    public void Dispose() { _listener.Stop(); if (_task.IsFaulted) _task.Exception?.Handle(_ => true); }
}
