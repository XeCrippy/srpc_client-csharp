using SRPC;

try
{
    string host;
    if (args.Length > 0)
    {
        host = args[0];
    }
    else
    {
        Console.WriteLine("Searching for an Xbox 360 running XBDM...");
        var discovered = ConsoleDiscovery.Discover();
        if (discovered is null)
        {
            Console.Error.WriteLine(
                "No console was discovered. Pass an IP address or hostname as the first argument.");
            return 1;
        }

        host = discovered.Host;
        Console.WriteLine(
            $"Found {(string.IsNullOrEmpty(discovered.Name) ? "console" : discovered.Name)} " +
            $"at {discovered.Host}:{discovered.Port}");
    }

    using var xbox = new SrpcClient(host);
    xbox.Connect();
    Console.WriteLine($"Console: {xbox.ConsoleName()}");
    Console.WriteLine($"Title ID: 0x{xbox.TitleId():X8}");
    Console.WriteLine($"Value: 0x{xbox.Read<uint>(0x82000000):X8}");
}
catch (SrpcException error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

return 0;
