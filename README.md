# srpc_client-csharp

A managed **Xbox 360 XBDM / SRPC / JRPC2 client** for .NET 8.

`SrpcClient` speaks the XBDM debug-monitor protocol over TCP (port 730 by default) and layers
remote procedure calls on top of it, so you can call functions on a running console, read and
write its memory, move files, capture the framebuffer, and drive a debug session — all from C#.

- **Package id:** `SRPC.Client`
- **Target framework:** `net8.0` (nullable enabled, C# 12)
- **Dependencies:** none — the PNG encoder, framebuffer untiler, and network layer are all in-box.

---

## Contents

| Path | What it is |
| --- | --- |
| [src/SRPC/](src/SRPC/) | The library (`SRPC.csproj`) |
| [examples/SRPC.Example/](examples/SRPC.Example/) | Console app: discover a console and read a word |
| [tests/SRPC.Tests/](tests/SRPC.Tests/) | Self-contained test runner (no test framework needed) |

---

## Requirements

- .NET 8 SDK
- An Xbox 360 running **XBDM** (devkit, testkit, or a JTAG/RGH console with the debug monitor).
- For anything beyond stock XBDM commands, the console needs the **SRPC plugin** or **JRPC2**
  loaded. Stock XBDM alone covers console info, filesystem, memory, and debugging; the RPC
  `Call` family and helpers like `Gamertag()`, `TitleId()`, and `Temperature()` need a plugin.

## Build and run

```sh
dotnet build SRPC.sln -c Release

# Run the example against a specific console, or omit the argument to auto-discover.
dotnet run --project examples/SRPC.Example -- 192.168.1.50

# Run the tests.
dotnet run --project tests/SRPC.Tests -c Release
```

The test runner prints `PASS`/`FAIL` per suite and exits non-zero on failure, so it drops
straight into CI. It covers protocol codec parsing, the debug protocol, the PNG encoder,
screenshot untiling, and client integration against an in-process fake console — none of it
requires real hardware.

---

## Quick start

```csharp
using SRPC;

using var xbox = new SrpcClient("192.168.1.50");
xbox.Connect();

Console.WriteLine(xbox.ConsoleName());          // "MyDevkit"
Console.WriteLine($"0x{xbox.TitleId():X8}");    // running title id

uint value = xbox.Read<uint>(0x82000000);       // big-endian by default
xbox.Write(0x82000000u, value + 1);
```

Finding a console on the LAN, rather than hard-coding an address:

```csharp
var found = ConsoleDiscovery.Discover();        // scans local adapters, first hit wins
if (found is not null)
{
    using var xbox = new SrpcClient(found.Host, new ClientOptions { Port = found.Port });
}

// Or enumerate everything, or probe one host directly:
IReadOnlyList<DiscoveredConsole> all = ConsoleDiscovery.DiscoverAll();
DiscoveredConsole? one = ConsoleDiscovery.Probe("192.168.1.50");
```

---

## Protocols

`Protocol.Automatic` (the default) probes the console on first use: it sends `s360`, and if that
is not answered it tries a JRPC2 `kernelversion` call. The result is cached until you
`Close()` / `Reconnect()`.

| `Protocol` | Meaning |
| --- | --- |
| `Automatic` | Detect native SRPC, else JRPC2, else throw `RpcException` |
| `NativeSrpc` | Force the native SRPC plugin |
| `Jrpc2` | Force JRPC2 |

`PluginAvailable()` returns `false` instead of throwing when no plugin answers, and
`GetProtocol()` returns whichever was detected.

**Native SRPC is more limited than JRPC2**, and the client enforces this rather than failing
silently — it returns only the 32-bit `r3` word, so `Void`, `Int32`, `Byte`, and `Float32` work
while strings, `UInt64`, and arrays throw `ProtocolException`. Thread selection
(`CallOptions.SystemThread = false`), VM calls, and array sizing also require JRPC2.

## Calling functions

```csharp
var options = new ClientOptions { Protocol = Protocol.Jrpc2, RpcTimeout = TimeSpan.FromSeconds(30) };
using var xbox = new SrpcClient("192.168.1.50", options);
xbox.Connect();

// By address.
uint result = xbox.CallUInt32(0x82451234, new RpcArgument[] { 1, 2.5f, "text" });

// By module + ordinal — resolved on the console.
var value = xbox.Call("xam.xex", ordinal: 12, ReturnType.String);
string text = value.As<string>();

// Array returns need an explicit size.
var floats = xbox.Call(0x82451234, ReturnType.FloatArray,
    options: new CallOptions { ArraySize = 16 }).As<float[]>();
```

Arguments are implicit conversions — `bool`, `int`, `uint`, `long`, `ulong`, `float`, `string`,
and `byte[]` all convert to `RpcArgument`, so you can pass an array literal directly. Convenience
wrappers `CallUInt32`, `CallUInt64`, `CallFloat`, `CallString`, and `CallVoid` skip the
`RpcValue.As<T>()` step.

Long-running calls return `pending` and the client polls for you at `ClientOptions.PollInterval`
until `RpcTimeout` elapses, then throws `SrpcTimeoutException`.

## Memory

```csharp
byte[] data = xbox.ReadMemory(0x82000000, 0x1000);
xbox.WriteMemory(0x82000000, data);

// Typed access; big-endian by default, which is what the console uses.
uint  word = xbox.Read<uint>(0x82000000);
float f    = xbox.Read<float>(0x82000000, Endian.Little);
int[] ints = xbox.ReadArray<int>(0x82000000, count: 64);

// Strings.
string ansi = xbox.ReadCString(0x82000000);
string wide = xbox.ReadUtf16String(0x82000000);

// Bulk / unmapped-tolerant reads.
byte[] big    = xbox.ReadMemoryChunked(0x82000000, 0x400000);
byte[] sparse = xbox.ReadMemorySparse(0x82000000, 0x400000);   // zero-fills unreadable pages

// Patching.
xbox.WriteBranch(0x82451234, 0x82455678);          // b / bl
xbox.WriteJump(0x82451234, 0x82455678);            // long jump via a scratch register
xbox.FillMemory(0x82000000, 0x100, 0x00);
```

`MemoryRegions()` enumerates mapped regions and `IsValidAddress()` tests a single address —
`ReadMemorySparse` uses these to skip holes instead of throwing.

## Files

```csharp
byte[] bytes = xbox.DownloadFile(@"Hdd:\file.bin");
xbox.UploadFile(@"Hdd:\file.bin", bytes, overwrite: true);

// Streaming transfers with progress and cancellation.
var result = xbox.DownloadFileTo(@"Hdd:\big.bin", @"C:\big.bin", new FileTransferOptions
{
    ExistingFile = ExistingFilePolicy.Overwrite,
    Progress = p =>
    {
        Console.Write($"\r{p.FileBytesTransferred}/{p.FileBytesTotal}");
        return TransferControl.Continue;   // return Cancel to stop
    }
});

// Whole directory trees, recursively.
xbox.DownloadDirectoryTo(@"Hdd:\folder", @"C:\folder");
xbox.UploadDirectoryFrom(@"C:\folder", @"Hdd:\folder");
```

Transfers are bounded on purpose: `MaximumFileSize` (512 MiB default), `MaximumDepth`,
`MaximumEntries`, and `MaximumTotalSize` all guard against a malformed or hostile directory
listing, and `RemovePartialFile` cleans up a cancelled or failed download.

Directory operations: `DirectoryContents`, `IsDirectory`, `CreateDirectory`, `DeleteFile`,
`DeleteDirectory`, `RenamePath`, `Drives`.

## Screenshots

```csharp
xbox.SaveScreenshot("shot.png");

// Or work with the pixels.
ScreenshotImage image = xbox.CaptureScreenshot();
ImageView view = image.View;                  // BGRA8
byte[] png = ImageCodec.EncodePng(view);

// Raw, still tiled, if you want to untile it yourself.
RawScreenshot raw = xbox.CaptureRawScreenshot();
```

The framebuffer comes back in the GPU's tiled layout; `ScreenshotCodec.Decode` untiles it.
`ScreenshotOptions` controls `UntileMode` (`Xenos` or `Morton`), `ComposeDisplaySurface` for
cropping to the display rectangle, `PreserveAlpha`, and `Packed10BitEndian` for
`A2R10G10B10` framebuffers. PNG encoding is built in — no `System.Drawing`, no ImageSharp.

## Console control and info

```csharp
xbox.ConsoleName(); xbox.ConsoleId(); xbox.ConsoleType(); xbox.IsDevkit();
xbox.CpuKey(); xbox.DmVersion(); xbox.KernelVersion(); xbox.MotherboardType();
xbox.Gamertag(); xbox.TitleId(); xbox.TitlePath(); xbox.SignInState();
xbox.Temperature(TemperatureSensor.Cpu);

xbox.ModuleList();      // parsed ModuleInfo records
xbox.TitleModule();     // the running title's module
xbox.ModuleHandle("xam.xex");
xbox.LoadModule(@"Hdd:\plugin.xex");
xbox.UnloadModule("plugin.xex");

xbox.Notify("Hello", NotificationType.Achievement);
xbox.SetLeds(LedColor.Green, LedColor.Green, LedColor.Off, LedColor.Off);
xbox.SynchronizeTime();
xbox.Reboot();          // magicboot cold
xbox.Shutdown();
xbox.LaunchXex(@"Hdd:\game\default.xex", @"Hdd:\game\");
```

Two footguns are deliberately made explicit. `ResetExecutablePool` takes an
`ExecutablePoolReset.ConfirmLiveAllocationsMayBeOverwritten` argument so you cannot call it by
accident, and `ConstantMemorySet` takes optional `ifValue` / `titleId` guards so a poke only
applies to the title you meant.

For anything not wrapped, drop to the raw protocol: `SendCommand`, `SendCommandRaw`,
`SendMultilineCommand`, and `SendSrpc`.

## Debugging

`DebugSession` opens a second connection as a notification channel and delivers events to a
callback:

```csharp
using var session = new DebugSession("192.168.1.50");
session.Attach(evt =>
{
    if (evt.HaltsTitle)
        Console.WriteLine($"{evt.Type} on thread {evt.ThreadId} at 0x{evt.Address:X8}");
});

session.SetBreakpoint(0x82451234);
session.SetDataBreakpoint(0x82500000, 4, BreakType.Write);

foreach (uint id in session.ThreadIds())
{
    ThreadInfo info = session.GetThreadInfo(id);
    if (info.Stopped)
    {
        PpcContext ctx = session.GetContext(id);
        Console.WriteLine($"IAR=0x{ctx.Iar:X8} r3=0x{ctx.Gpr[3]:X16} arg0={ctx.Argument(0)}");
        session.ContinueThread(id);
    }
}
```

`PpcContext` exposes the full PowerPC register file (`Gpr`, `Fpr`, `Iar`, `Msr`, `Cr`, `Xer`,
`Lr`, `Ctr`, `Fpscr`) plus `Argument(i)` / `FloatArgument(i)` helpers that follow the PPC
calling convention. `ContextFlags` selects which register groups to fetch — control and integer
by default, with floating and vector opt-in since they are expensive.

`ReleaseStoppedThreads()` is the escape hatch when you have left threads halted and want the
title running again.

---

## Error handling

Everything derives from `SrpcException`, so one `catch` covers the library:

| Exception | Raised when |
| --- | --- |
| `ConnectionException` | Socket failure, or an operation while not connected |
| `ProtocolException` | Malformed input, or a request the selected protocol cannot serve |
| `CommandException` | XBDM returned a failure status (carries `StatusCode`) |
| `RpcException` | The RPC itself failed, or no plugin is loaded |
| `SrpcTimeoutException` | `RpcTimeout` elapsed while polling a pending call |

## Thread safety and lifetime

`SrpcClient` guards every operation with an internal lock, so it is safe to share across
threads; calls serialize onto the single console connection. It implements `IDisposable` —
use `using`. Reentrancy from inside a transfer `Progress` callback is rejected with a
`ProtocolException` rather than corrupting the in-flight binary stream.

`ClientOptions` tunes the rest:

| Option | Default |
| --- | --- |
| `Protocol` | `Automatic` |
| `Port` | `730` |
| `ConnectTimeout` | 5 s |
| `IoTimeout` | 5 s |
| `RpcTimeout` | 10 s |
| `PollInterval` | 50 ms |

Options are copied at construction, so mutating the instance afterwards has no effect.

---

## License

**GNU General Public License, version 3.** The full text is in [LICENSE.txt](LICENSE.txt).

This is a copyleft license: if you distribute this library, or a program that links against it,
you must make the corresponding source available under the GPLv3 as well. That applies to the
`SRPC.Client` NuGet package too — it ships the same license.
