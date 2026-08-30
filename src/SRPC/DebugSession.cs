using System.Text;
using SRPC.Internal;

namespace SRPC;

public sealed class DebugSession : IDisposable
{
    private readonly string _host; private readonly ClientOptions _options; private readonly SrpcClient _command; private readonly object _sync = new();
    private XbdmConnection? _notify; private Thread? _worker; private volatile bool _running, _stopping; private Action<DebugEvent>? _onEvent; private Action<string>? _onLog;
    public bool Attached { get; private set; } public bool IsDebuggerOwner { get; private set; }
    public DebugSession(string host, ClientOptions? options = null) { _host = host; _options = (options ?? new ClientOptions()).Copy(); _command = new SrpcClient(host, _options); }
    public void Attach(Action<DebugEvent> onEvent, Action<string>? onLog = null)
    {
        _onEvent = onEvent ?? throw new ArgumentNullException(nameof(onEvent)); _onLog = onLog; _command.Connect();
        try { Send("debugger connect"); IsDebuggerOwner = true; } catch (CommandException ex) { IsDebuggerOwner = false; Log("Not the debug session owner: " + ex.Message); }
        try { StartNotifications(); } catch (Exception ex) { Log("Notifications unavailable: " + ex.Message); }
        Attached = true;
    }
    private Response Send(string command) { lock (_sync) return _command.SendCommand(command); }
    private IReadOnlyList<string> SendMultiline(string command) { lock (_sync) return _command.SendMultilineCommand(command); }
    private void Quiet(string command) { try { Send(command); } catch { } }
    private void Log(string text) { try { _onLog?.Invoke(text); } catch { } }
    private void StartNotifications()
    {
        var notify = new XbdmConnection(); notify.Connect(_host, _options.Port, _options.ConnectTimeout, TimeSpan.FromHours(24));
        var greeting = ProtocolCodec.ParseResponseLine(notify.ReadLine()); if (!greeting.IsSuccess) throw new ProtocolException("Notification channel refused the connection: " + greeting.Message);
        notify.SendAll(Encoding.ASCII.GetBytes("notify\r\n")); var ack = ProtocolCodec.ParseResponseLine(notify.ReadLine()); if (!ack.IsSuccess) { notify.Close(); throw new ProtocolException("Console refused the notification channel: " + ack.Message); }
        _notify = notify; _stopping = false; _running = true; _worker = new Thread(NotificationLoop) { IsBackground = true, Name = "SRPC XBDM notifications" }; _worker.Start();
    }
    private void NotificationLoop()
    {
        while (_running)
        {
            string line; try { line = _notify!.ReadLine(); } catch (Exception ex) { if (!_stopping) Log("Notification channel closed: " + ex.Message); break; }
            if (line.Length == 0) continue; var evt = DebugProtocol.ParseEvent(ProtocolCodec.TrimAscii(line)); if (evt.Type == DebugEventType.Unknown) Log("Undecoded notification: " + evt.Raw);
            try { _onEvent?.Invoke(evt); } catch (Exception ex) { Log("Event handler threw: " + ex.Message); }
        }
    }
    public void Detach()
    {
        if (!Attached) { StopNotifications(); return; } Attached = false; StopNotifications(); Quiet("break clearall"); try { ReleaseStoppedThreads(); } catch { } Quiet("go"); if (IsDebuggerOwner) { Quiet("debugger disconnect"); IsDebuggerOwner = false; } _command.Close();
    }
    private void StopNotifications() { _stopping = true; _running = false; _notify?.Close(); if (_worker is { IsAlive: true } && Thread.CurrentThread != _worker) _worker.Join(); _notify?.Dispose(); _notify = null; _worker = null; }
    public void Stop() => Send("stop"); public void Go() => Send("go");
    public void ContinueThread(uint id, bool deliverToTitle = false) => Send(DebugProtocol.Continue(id, deliverToTitle));
    public void HaltThread(uint id) => Send(DebugProtocol.ThreadCommand("halt", id)); public void ResumeThread(uint id) => Send(DebugProtocol.ThreadCommand("resume", id));
    public bool ThreadStopped(uint id) { try { return !Send(DebugProtocol.ThreadCommand("isstopped", id)).Message.Contains("not stopped", StringComparison.Ordinal); } catch (CommandException) { return false; } }
    public int ReleaseStoppedThreads() { var count = 0; foreach (var id in ThreadIds()) if (ThreadStopped(id)) try { ContinueThread(id); count++; } catch (SrpcException) { } return count; }
    public void SetBreakpoint(uint address) => Send(DebugProtocol.SetBreakpoint(address)); public void ClearBreakpoint(uint address) => Send(DebugProtocol.ClearBreakpoint(address));
    public bool IsBreakpoint(uint address) { try { return !Send(DebugProtocol.IsBreakpoint(address)).Message.Contains("not", StringComparison.Ordinal); } catch (CommandException) { return false; } }
    public void SetDataBreakpoint(uint address, uint size, BreakType type) { ValidateDataSize(size); Send(DebugProtocol.DataBreakpoint(address, size, type, false)); }
    public void ClearDataBreakpoint(uint address, uint size, BreakType type) { ValidateDataSize(size); Send(DebugProtocol.DataBreakpoint(address, size, type, true)); }
    private static void ValidateDataSize(uint size) { if (size is not (1 or 2 or 4 or 8)) throw new ProtocolException("Data breakpoint size must be 1, 2, 4, or 8 bytes."); }
    public void ClearAllBreakpoints() => Send("break clearall"); public IReadOnlyList<uint> ThreadIds() => DebugProtocol.ParseThreadIds(SendMultiline("threads"));
    public ThreadInfo GetThreadInfo(uint id) { var lines = SendMultiline(DebugProtocol.ThreadCommand("threadinfo", id)); var info = lines.Count == 0 ? new ThreadInfo(id, 0, 0, 0, 0, 0, 0, false) : DebugProtocol.ParseThreadInfo(lines[0]) with { Id = id }; return info with { Stopped = ThreadStopped(id) }; }
    public PpcContext GetContext(uint id, ContextFlags? flags = null) => DebugProtocol.ParseContext(SendMultiline(DebugProtocol.GetContext(id, flags ?? new ContextFlags())));
    public void SetContext(uint id, PpcContext context, ContextFlags? flags = null)
    {
        flags ??= new ContextFlags(); var command = DebugProtocol.SetContext(id, context, flags); if (command.Length <= ProtocolCodec.MaxCommandLength) { Send(command); return; }
        if (flags.Control) Send(DebugProtocol.SetContext(id, context, new ContextFlags { Control = true, Integer = false }));
        void Batches(string prefix, ulong[] values) { for (var first = 0; first < 32; first += 8) { var batch = DebugProtocol.ThreadCommand("setcontext", id); for (var i = first; i < first + 8; i++) batch += $" {prefix}{i}=0q{values[i]:x16}"; Send(batch); } }
        if (flags.Integer) Batches("Gpr", context.Gpr); if (flags.Floating) Batches("Fpr", context.Fpr);
    }
    public Response SendCommand(string command) => Send(command);
    public void Dispose() { Detach(); _command.Dispose(); }
}
