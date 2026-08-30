using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using SRPC.Internal;

namespace SRPC;

public sealed record DiscoveredConsole(string Host, string Name, ushort Port = 730);
public sealed class DiscoveryOptions
{
    public ushort Port { get; set; } = 730;
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan IoTimeout { get; set; } = TimeSpan.FromMilliseconds(500);
    public int MaximumConcurrency { get; set; } = 32;
    public int MaximumCandidatesPerAdapter { get; set; } = 1024;
}

public static class ConsoleDiscovery
{
    private static void Validate(DiscoveryOptions options)
    {
        if (options.Port == 0 || options.ConnectTimeout <= TimeSpan.Zero || options.IoTimeout <= TimeSpan.Zero || options.MaximumConcurrency <= 0 || options.MaximumCandidatesPerAdapter <= 0)
            throw new ProtocolException("Discovery port, timeouts, concurrency, and candidate limit must be positive.");
    }
    public static DiscoveredConsole? Probe(string host, DiscoveryOptions? options = null)
    {
        options ??= new DiscoveryOptions(); Validate(options); if (string.IsNullOrEmpty(host)) return null;
        try
        {
            using var connection = new XbdmConnection(); connection.Connect(host, options.Port, options.ConnectTimeout, options.IoTimeout);
            var banner = ProtocolCodec.ParseResponseLine(connection.ReadLine());
            if (banner.StatusCode != 201 && !banner.Message.Contains("connected", StringComparison.OrdinalIgnoreCase)) return null;
            var name = "";
            try { connection.SendAll(Encoding.ASCII.GetBytes("dbgname\r\n")); var response = ProtocolCodec.ParseResponseLine(connection.ReadLine()); if (response.IsSuccess) name = response.Message; } catch (SrpcException) { }
            return new DiscoveredConsole(host, name, options.Port);
        }
        catch (Exception ex) when (ex is SrpcException or SocketException or IOException) { return null; }
    }
    public static IReadOnlyList<DiscoveredConsole> DiscoverAll(DiscoveryOptions? options = null)
    {
        options ??= new DiscoveryOptions(); Validate(options); var candidates = Candidates(options).ToArray(); var found = new ConcurrentBag<DiscoveredConsole>();
        Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = options.MaximumConcurrency }, host => { if (Probe(host, options) is { } console) found.Add(console); });
        return found.OrderBy(c => IPAddress.TryParse(c.Host, out var ip) ? BitConverter.ToUInt32(ip.GetAddressBytes().Reverse().ToArray()) : uint.MaxValue).ToArray();
    }
    public static DiscoveredConsole? Discover(DiscoveryOptions? options = null) => DiscoverAll(options).FirstOrDefault();
    public static DiscoveredConsole? Discover(string fallbackHost, DiscoveryOptions? options = null) => Discover(options) ?? Probe(fallbackHost, options);
    private static HashSet<string> Candidates(DiscoveryOptions options)
    {
        var result = new HashSet<string>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                var bytes = unicast.Address.GetAddressBytes(); var local = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
                var prefix = unicast.PrefixLength; if (prefix > 30) continue; ulong count = 1UL << (32 - prefix); ulong usable = count - 2; uint network;
                if (usable > (ulong)options.MaximumCandidatesPerAdapter) { network = local & 0xffffff00; usable = 254; }
                else { var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix); network = local & mask; }
                for (ulong i = 1; i <= Math.Min(usable, (ulong)options.MaximumCandidatesPerAdapter); i++)
                {
                    var address = network + (uint)i; if (address == local) continue; result.Add($"{address >> 24}.{address >> 16 & 255}.{address >> 8 & 255}.{address & 255}");
                }
            }
        }
        return result;
    }
}
