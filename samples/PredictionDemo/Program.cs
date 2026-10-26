using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Krut.Netcode;
using PredictionDemo;

// Usage: PredictionDemo [--profile gate|stress|bad|lan|clean|...] [--seconds 10]
// The default profile "gate" is a 100 ms round trip with 2 % loss per direction.
string profileName = "gate";
double seconds = 10;
for (int i = 0; i + 1 < args.Length; i += 2)
{
    if (args[i] == "--profile")
        profileName = args[i + 1];
    else if (args[i] == "--seconds")
        seconds = double.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
}

if (!NetworkSimulationProfile.TryGet(profileName, out var profile))
{
    Console.Error.WriteLine($"Unknown profile '{profileName}'. Known: {string.Join(", ", NetworkSimulationProfile.Names)}");
    return 2;
}

System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
Wire.Register();

int port;
using (var probe = new UdpClient(0))
    port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

NetworkConfig MakeConfig() => new()
{
    ApplicationId = "krut.netcode.prediction-demo",
    ProtocolVersion = 1,
    Port = port,
    TickRate = UnitSim.TickRate,
    MaxClients = 2,
    EnableStatistics = true,
    MtuDiscovery = false,
};

var server = new DemoServer(MakeConfig());
if (!server.Start())
{
    Console.Error.WriteLine("The server could not open its port.");
    return 1;
}

server.Net.ApplySimulation(profile.Resolve(NetworkSimulationSide.Server, NetworkSimulation.DefaultSeed));
Console.WriteLine($"Link: {profile.Name}, {profile.RttMs:0} ms RTT +/-{profile.RttJitterMs:0}, {profile.LossChance * 100:0.#} % loss per direction");

var clients = new[] { new DemoClient(MakeConfig(), 0), new DemoClient(MakeConfig(), 1) };
var clock = Stopwatch.StartNew();
double last = 0;

bool Pump(Func<bool> until, double timeoutSeconds)
{
    double deadline = clock.Elapsed.TotalSeconds + timeoutSeconds;
    while (clock.Elapsed.TotalSeconds < deadline)
    {
        double now = clock.Elapsed.TotalSeconds;
        float dt = (float)(now - last);
        last = now;
        server.Update(dt);
        foreach (var client in clients)
            client.Update(dt);

        if (until())
            return true;

        Thread.Sleep(1);
    }

    return until();
}

// Clients connect one after the other so that client N is also the server's slot N.
for (int i = 0; i < clients.Length; i++)
{
    var client = clients[i];
    client.Net.ApplySimulation(profile.Resolve(NetworkSimulationSide.Client, NetworkSimulation.DefaultSeed + (ulong)i));
    if (!client.Net.Connect("127.0.0.1", port, $"Player{i}") || !Pump(() => client.Net.IsSessionReady, 10))
    {
        Console.Error.WriteLine($"Client {i} did not get its welcome.");
        return 1;
    }
}

if (!Pump(() => clients[0].FirstTick > 0 && clients[1].FirstTick > 0, 10))
{
    Console.Error.WriteLine("Time sync never converged.");
    return 1;
}

Console.WriteLine($"Running {seconds:0.#} s of play ...");
double playStart = clock.Elapsed.TotalSeconds;
Pump(() => false, seconds);
double playSeconds = clock.Elapsed.TotalSeconds - playStart;

var report = Report.Build(server, clients, playSeconds, clock.Elapsed.TotalSeconds);
report.Print();

foreach (var client in clients)
    client.Net.Stop();
server.Net.Stop();

return report.Failure is null ? 0 : Fail(report.Failure);

static int Fail(string reason)
{
    Console.Error.WriteLine("FAILED: " + reason);
    return 1;
}
