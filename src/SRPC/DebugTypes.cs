namespace SRPC;

public enum BreakType { Read, Write, Execute }
public enum DebugEventType { Unknown, Execution, Breakpoint, DataBreak, SingleStep, Exception, Assertion, Rip, DebugString }
public sealed class PpcContext
{
    public bool Valid { get; internal set; }
    public uint Iar { get; set; } public uint Msr { get; set; } public uint Cr { get; set; } public uint Xer { get; set; } public uint Lr { get; set; }
    public ulong Ctr { get; set; }
    public ulong[] Gpr { get; } = new ulong[32]; public ulong[] Fpr { get; } = new ulong[32]; public ulong Fpscr { get; set; }
    public ulong Argument(int index) => index is >= 0 and < 8 ? Gpr[3 + index] : 0;
    public double FloatArgument(int index) => index is >= 0 and < 8 ? BitConverter.UInt64BitsToDouble(Fpr[1 + index]) : 0;
}
public sealed class ContextFlags
{
    public bool Control { get; set; } = true; public bool Integer { get; set; } = true; public bool Floating { get; set; } public bool Vector { get; set; }
}
public sealed record ThreadInfo(uint Id, uint SuspendCount, uint Priority, uint TlsBase, uint StackBase, uint StackLimit, uint StartAddress, bool Stopped);
public sealed record DebugEvent(DebugEventType Type, uint ThreadId, uint Address, uint DataAddress, uint ExceptionCode, BreakType? Access, bool FirstChance, string Text, string Raw)
{
    public bool HaltsTitle => Type is DebugEventType.Breakpoint or DebugEventType.DataBreak or DebugEventType.SingleStep or DebugEventType.Exception or DebugEventType.Assertion or DebugEventType.Rip;
}
