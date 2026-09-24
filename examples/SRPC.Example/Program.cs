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
    int[] offsets = new int[] { 0, 0, 4, 8, 8, 88, 128, 52, 16 };
    Console.WriteLine($"Console: {xbox.ConsoleName()}");
    Console.WriteLine($"Title ID: 0x{xbox.TitleId():X8}");
    Console.WriteLine($"Value at 0x82000000: 0x{xbox.Read<uint>(0x82000000):X8}");

    // Write a pointer with offsets
    if (xbox.TitleId() == 0x555308B7) // Watchdogs
    {
        xbox.WritePointer<float>(0x84129D78, 50.0f, offsets);
        Thread.Sleep(250);
    }

    // Read a pointer with offsets
    // Watchdogs health latest TU
    if (xbox.TitleId() == 0x555308B7) // Watchdogs
        Console.WriteLine($"Health: {xbox.ReadPointer<float>(0x84129D78, offsets)}");
}
catch (SrpcException error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

return 0;
