using System.Globalization;

namespace SRPC.Internal;

internal static class DebugProtocol
{
    internal static string BreakName(BreakType type) => type switch { BreakType.Read => "read", BreakType.Write => "write", BreakType.Execute => "execute", _ => "read" };
    internal static ulong? ParseNumber(string text)
    {
        if (text.Length == 0) return null; var style = NumberStyles.AllowHexSpecifier;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.StartsWith("0q", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        else if (text.All(char.IsAsciiDigit)) style = NumberStyles.None;
        return text.Length != 0 && ulong.TryParse(text, style, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
    private sealed class Fields
    {
        internal string Leading = ""; internal readonly List<(string Key, string Value)> Pairs = new();
        internal string? Value(string key) => Pairs.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is var p && p.Key is not null ? p.Value : null;
        internal ulong? Number(string key) => Value(key) is { } value ? ParseNumber(value) : null;
        internal uint NumberOr(string key, uint fallback = 0) => Number(key) is ulong value ? (uint)value : fallback;
        internal bool Has(string key) => Pairs.Any(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }
    private static Fields ParseFields(string line)
    {
        var result = new Fields(); var i = 0; void Skip() { while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++; } Skip();
        if (i < line.Length) { var start = i; var scan = i; while (scan < line.Length && line[scan] != ' ' && line[scan] != '=') scan++; if (scan >= line.Length || line[scan] != '=') { result.Leading = line[start..scan]; i = scan; } }
        while (i < line.Length)
        {
            Skip(); if (i >= line.Length) break; var start = i; while (i < line.Length && line[i] != '=' && line[i] != ' ') i++;
            if (i >= line.Length || line[i] != '=') { if (i > start) result.Pairs.Add((line[start..i], "")); continue; }
            var key = line[start..i++]; string value;
            if (i < line.Length && line[i] == '"') { var valueStart = ++i; while (i < line.Length && line[i] != '"') i++; value = line[valueStart..i]; if (i < line.Length) i++; }
            else { var valueStart = i; while (i < line.Length && line[i] != ' ') i++; value = line[valueStart..i]; }
            result.Pairs.Add((key, value));
        }
        return result;
    }
    internal static string ThreadCommand(string verb, uint id) => $"{verb} thread=0x{id:x8}";
    internal static string Continue(uint id, bool deliver) => ThreadCommand("continue", id) + (deliver ? " exception" : "");
    internal static string SetBreakpoint(uint address) => $"break addr=0x{address:x8}";
    internal static string ClearBreakpoint(uint address) => $"break addr=0x{address:x8} clear";
    internal static string IsBreakpoint(uint address) => $"isbreak addr=0x{address:x8}";
    internal static string DataBreakpoint(uint address, uint size, BreakType type, bool clear) => $"break {BreakName(type)}=0x{address:x8} size={size}" + (clear ? " clear" : "");
    internal static string GetContext(uint id, ContextFlags flags) => ThreadCommand("getcontext", id) + (flags.Control ? " control" : "") + (flags.Integer ? " int" : "") + (flags.Floating ? " fp" : "") + (flags.Vector ? " vr" : "");
    internal static string SetContext(uint id, PpcContext c, ContextFlags flags)
    {
        var text = ThreadCommand("setcontext", id);
        if (flags.Control) text += $" Iar=0x{c.Iar:x8} Msr=0x{c.Msr:x8} Cr=0x{c.Cr:x8} Xer=0x{c.Xer:x8} Lr=0x{c.Lr:x8} Ctr=0q{c.Ctr:x16}";
        if (flags.Integer) for (var i = 0; i < 32; i++) text += $" Gpr{i}=0q{c.Gpr[i]:x16}";
        if (flags.Floating) { for (var i = 0; i < 32; i++) text += $" Fpr{i}=0q{c.Fpr[i]:x16}"; text += $" Fpscr=0q{c.Fpscr:x16}"; }
        return text;
    }
    internal static PpcContext ParseContext(IReadOnlyList<string> lines)
    {
        var c = new PpcContext();
        foreach (var line in lines) foreach (var (key, text) in ParseFields(line).Pairs)
        {
            if (ParseNumber(text) is not ulong value) continue; var lower = key.ToLowerInvariant();
            if (lower.StartsWith("gpr") && int.TryParse(lower[3..], out var gi) && gi is >= 0 and < 32) c.Gpr[gi] = value;
            else if (lower.StartsWith("fpr") && int.TryParse(lower[3..], out var fi) && fi is >= 0 and < 32) c.Fpr[fi] = value;
            else switch (lower) { case "iar": c.Iar = (uint)value; break; case "msr": c.Msr = (uint)value; break; case "cr": c.Cr = (uint)value; break; case "xer": c.Xer = (uint)value; break; case "lr": c.Lr = (uint)value; break; case "ctr": c.Ctr = value; break; case "fpscr": c.Fpscr = value; break; default: continue; }
            c.Valid = true;
        }
        return c;
    }
    internal static uint[] ParseThreadIds(IReadOnlyList<string> lines) => lines.Select(ProtocolCodec.TrimAscii).Where(s => s.Length != 0).Select(text =>
    {
        if (text[0] == '-' && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed)) return (uint?)unchecked((uint)signed);
        return ParseNumber(text) is ulong value ? (uint?)value : null;
    }).Where(id => id.HasValue).Select(id => id!.Value).ToArray();
    internal static ThreadInfo ParseThreadInfo(string line)
    {
        var f = ParseFields(line); return new ThreadInfo(f.NumberOr("thread"), f.NumberOr("suspend"), f.NumberOr("priority"), f.NumberOr("tlsbase"), f.NumberOr("base"), f.NumberOr("limit"), f.NumberOr("start"), false);
    }
    internal static DebugEvent ParseEvent(string line)
    {
        var f = ParseFields(line); var type = f.Leading.ToLowerInvariant() switch { "break" => DebugEventType.Breakpoint, "databreak" => DebugEventType.DataBreak, "singlestep" => DebugEventType.SingleStep, "exception" => DebugEventType.Exception, "assert" or "ntassert" => DebugEventType.Assertion, "rip" => DebugEventType.Rip, "debugstr" => DebugEventType.DebugString, "execution" => DebugEventType.Execution, _ => DebugEventType.Unknown };
        BreakType? access = null; uint data = f.NumberOr("data");
        foreach (var item in new[] { BreakType.Read, BreakType.Write, BreakType.Execute }) if (f.Number(BreakName(item)) is ulong address) { access = item; data = (uint)address; if (type == DebugEventType.Unknown) type = DebugEventType.DataBreak; break; }
        var text = f.Value("string") ?? ""; if (type == DebugEventType.Execution && text.Length == 0) text = f.Pairs.FirstOrDefault(p => p.Value.Length == 0).Key ?? "";
        return new DebugEvent(type, f.NumberOr("thread"), f.NumberOr("addr"), data, f.NumberOr("code"), access, f.Has("first"), text, line);
    }
}
