using System;
using System.Diagnostics;
using System.Threading;
using Krut.Netcode;

namespace NatRendezvousServer
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            var config = new NatRendezvousConfig();
            string? bindAddress = null;
            int statsIntervalSeconds = 0;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--help":
                    case "-h":
                        PrintUsage();
                        return 0;

                    case "--generate-key":
                        Console.WriteLine(Convert.ToBase64String(NetworkCrypto.GenerateKey()));
                        return 0;

                    case "--ipv4-only":
                        config.IPv6Support = false;
                        break;

                    case "--port":
                        if (!TryReadInt(args, ref i, out int port) || port is < 0 or > 65535)
                        {
                            Console.Error.WriteLine("Invalid --port value (expected 0..65535).");
                            return 1;
                        }
                        config.Port = port;
                        break;

                    case "--bind":
                        if (!TryReadString(args, ref i, out bindAddress))
                        {
                            Console.Error.WriteLine("Missing --bind IP address.");
                            return 1;
                        }
                        break;

                    case "--psk":
                        if (!TryReadString(args, ref i, out string pskBase64)
                            || !TryDecodeKey(pskBase64, out byte[] psk))
                        {
                            Console.Error.WriteLine("Invalid --psk value (expected Base64-encoded 32-byte key).");
                            return 1;
                        }
                        config.PreSharedKey = psk;
                        break;

                    case "--max-rooms":
                        if (!TryReadInt(args, ref i, out int maxRooms) || maxRooms < 1)
                        {
                            Console.Error.WriteLine("Invalid --max-rooms value (expected >= 1).");
                            return 1;
                        }
                        config.MaxRooms = maxRooms;
                        break;

                    case "--lease":
                        if (!TryReadInt(args, ref i, out int leaseSeconds) || leaseSeconds < 5)
                        {
                            Console.Error.WriteLine("Invalid --lease value (expected seconds >= 5).");
                            return 1;
                        }
                        config.RoomLeaseSeconds = leaseSeconds;
                        break;

                    case "--ip-limit":
                        if (!TryReadInt(args, ref i, out int ipLimit) || ipLimit < 1)
                        {
                            Console.Error.WriteLine("Invalid --ip-limit value (expected >= 1).");
                            return 1;
                        }
                        config.MaxPacketsPerIpPerSecond = ipLimit;
                        break;

                    case "--room-limit":
                        if (!TryReadInt(args, ref i, out int roomLimit) || roomLimit < 1)
                        {
                            Console.Error.WriteLine("Invalid --room-limit value (expected >= 1).");
                            return 1;
                        }
                        config.MaxIntroductionsPerRoomPerSecond = roomLimit;
                        break;

                    case "--stats-interval":
                        if (!TryReadInt(args, ref i, out statsIntervalSeconds) || statsIntervalSeconds < 0)
                        {
                            Console.Error.WriteLine("Invalid --stats-interval value (expected seconds >= 0).");
                            return 1;
                        }
                        break;

                    default:
                        Console.Error.WriteLine($"Unknown argument: {arg}");
                        PrintUsage();
                        return 1;
                }
            }

            if (config.PreSharedKey is null)
            {
                string? envPsk = Environment.GetEnvironmentVariable("KRUT_RENDEZVOUS_PSK");
                if (!string.IsNullOrWhiteSpace(envPsk))
                {
                    if (!TryDecodeKey(envPsk.Trim(), out byte[] psk))
                    {
                        Console.Error.WriteLine("Invalid KRUT_RENDEZVOUS_PSK value (expected Base64-encoded 32-byte key).");
                        return 1;
                    }

                    config.PreSharedKey = psk;
                }
            }

            using var server = new NetworkNatRendezvousServer(config);
            server.Error += err => Console.Error.WriteLine($"[ERROR] {err}");

            if (!server.Start(bindAddress, config.Port))
            {
                Console.Error.WriteLine($"Failed to start NAT rendezvous server on port {config.Port}.");
                return 1;
            }

            using var stopSignal = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stopSignal.Set();
            };

            string encryptionLabel = config.PreSharedKey is null ? "plaintext" : $"AES-256-GCM (KeyId={config.KeyId})";
            Console.WriteLine(
                $"Krut.Netcode NAT Rendezvous Server listening on {(bindAddress ?? "0.0.0.0")}:{server.Port} " +
                $"[{encryptionLabel}, MaxRooms={server.Config.MaxRooms}, Lease={server.Config.RoomLeaseSeconds}s]");
            Console.WriteLine("Press Ctrl+C to shut down.");

            long nextStatsTick = statsIntervalSeconds > 0
                ? Stopwatch.GetTimestamp() + Stopwatch.Frequency * statsIntervalSeconds
                : long.MaxValue;

            while (!stopSignal.IsSet)
            {
                server.Update(0.01f);

                if (statsIntervalSeconds > 0)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (now >= nextStatsTick)
                    {
                        Console.WriteLine(
                            $"[STATS] rooms={server.Stats.ActiveRooms} reg_ok={server.Stats.RegistrationsAccepted} " +
                            $"intros={server.Stats.IntroductionsSent} not_found={server.Stats.RoomNotFoundResponses} " +
                            $"rate_limited={server.Stats.RateLimitedPackets} malformed={server.Stats.MalformedPackets}");
                        nextStatsTick = now + Stopwatch.Frequency * statsIntervalSeconds;
                    }
                }

                Thread.Sleep(10);
            }

            server.Stop();
            Console.WriteLine("NAT Rendezvous Server stopped.");
            return 0;
        }

        private static bool TryReadString(string[] args, ref int index, out string value)
        {
            if (index + 1 >= args.Length)
            {
                value = string.Empty;
                return false;
            }

            index++;
            value = args[index];
            return true;
        }

        private static bool TryReadInt(string[] args, ref int index, out int value)
        {
            if (index + 1 >= args.Length)
            {
                value = 0;
                return false;
            }

            index++;
            return int.TryParse(args[index], out value);
        }

        private static bool TryDecodeKey(string base64, out byte[] key)
        {
            try
            {
                byte[] decoded = Convert.FromBase64String(base64);
                if (decoded.Length == NetworkCrypto.KeySizeBytes)
                {
                    key = decoded;
                    return true;
                }
            }
            catch (FormatException)
            {
            }

            key = Array.Empty<byte>();
            return false;
        }

        private static void PrintUsage()
        {
            Console.WriteLine(
                """
                Usage: NatRendezvousServer [options]
                  --port <int>             UDP port to bind (default: 26000)
                  --bind <ip>              Specific IPv4 address to bind
                  --psk <base64>           Base64 32-byte AES-256-GCM pre-shared key (or KRUT_RENDEZVOUS_PSK env)
                  --generate-key           Generate a fresh Base64 32-byte key and exit
                  --max-rooms <int>        Maximum active rooms (default: 4096)
                  --lease <seconds>        Room lease lifetime in seconds (default: 45)
                  --ip-limit <int>         Max packets per source IP per second (default: 30)
                  --room-limit <int>       Max introductions per room per second (default: 10)
                  --stats-interval <sec>   Log server stats every N seconds (default: 0 = off)
                  --ipv4-only              Disable dual-stack IPv6 binding
                  --help, -h               Show this help text
                """);
        }
    }
}
