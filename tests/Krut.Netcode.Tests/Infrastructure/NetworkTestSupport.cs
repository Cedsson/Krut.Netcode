using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Krut.Netcode;
using LiteNetLib.Utils;
using System.Numerics;
using Xunit;


namespace Krut.Netcode.Tests
{
    /// <summary>
    /// Runtime tests for the networking layer: a server and clients inside the same process, with real UDP
    /// traffic over loopback. That is how bugs in a networking layer actually show up: not as a function
    /// returning the wrong value, but as a connection that never becomes ready, a kick that never arrives, or a
    /// delay that makes everything arrive in one clump. Both sides therefore share one thread of control and
    /// one set of process-wide environment variables, which is exactly why the suites are serial rather than
    /// parallel - see <see cref="NetworkTestCollection"/>. A real Wi-Fi link cannot produce 300 ms of latency
    /// on demand, and it cannot produce it deterministically, so the tests that care about delay and loss use
    /// <see cref="NetworkSimulation"/> instead: the engine holds the outgoing packet until its due time and
    /// then drops it with the configured probability.
    /// </summary>
    internal static class NetworkTestSupport
    {
        /// <summary>
        /// Runs the sessions for <paramref name="seconds"/> of wall-clock time. LiteNetLib's timeouts, pings and
        /// resends are measured against the real clock, so the loop must not count iterations as seconds.
        /// </summary>
        internal static void Pump(float seconds, params NetworkSession[] sessions) => PumpUntil(seconds, () => false, sessions);

        /// <summary>Pumps until <paramref name="until"/> is true or <paramref name="seconds"/> of wall-clock time have passed.</summary>
        internal static bool PumpUntil(float seconds, Func<bool> until, params NetworkSession[] sessions)
        {
            // A long stall (debugger, loaded CI runner) is passed on as at most this much time, so that a session
            // never sees one huge step.
            const double maxStep = 0.1;
            var clock = Stopwatch.StartNew();
            double last = 0;
            while (true)
            {
                double now = clock.Elapsed.TotalSeconds;
                float dt = (float)Math.Min(now - last, maxStep);
                last = now;
                foreach (var session in sessions)
                    session.Update(dt);

                if (until())
                    return true;
                if (now >= seconds)
                    return false;

                Thread.Sleep(1);
            }
        }

        /// <summary>Runs <paramref name="action"/> with the given current culture, then restores the thread's culture.</summary>
        internal static void WithCulture(string cultureName, Action action)
        {
            var previous = CultureInfo.CurrentCulture;
            var previousUi = CultureInfo.CurrentUICulture;
            try
            {
                var culture = new CultureInfo(cultureName);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                action();
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
                CultureInfo.CurrentUICulture = previousUi;
            }
        }

        /// <summary>
        /// Requires a condition to hold, otherwise throws with text saying what was expected and what actually
        /// happened.
        /// </summary>
        internal static void Require(bool condition, string what, string? detail = null)
            => Assert.True(condition, detail is null ? what : $"{what} ({detail})");

        /// <summary>A port that is free right now.</summary>
        internal static int FreeUdpPort()
        {
            using var socket = new System.Net.Sockets.UdpClient(0);
            return ((System.Net.IPEndPoint)socket.Client.LocalEndPoint!).Port;
        }

        /// <summary>
        /// Standard config for the tests - port and app id are set here, the rest is the engine's default.
        /// </summary>
        internal static NetworkConfig Config(int port, int maxClients = 4) => new()
        {
            ApplicationId = "krut.netcode.test",
            ProtocolVersion = 7,
            Port = port,
            TickRate = 60,
            MaxClients = maxClients,
            // The statistics are read by the diagnostics test, and the cost is
            // negligible in a test process.
            EnableStatistics = true,
            // MTU discovery is a pointless round trip in a test process on loopback.
            MtuDiscovery = false,
        };

        internal static NetDataReader ReaderFor(NetDataWriter writer) => new(writer.Data, 0, writer.Length);

        /// <summary>
        /// Creates a <see cref="CountingSession"/> with <paramref name="tickRate"/> already set in the config.
        /// </summary>
        internal static CountingSession NewCountingSession(int tickRate = 30)
        {
            var config = Config(FreeUdpPort());
            config.TickRate = tickRate;
            return new CountingSession(config);
        }

        /// <summary>
        /// A counter that becomes true once the client's <see cref="NetworkClient.SessionReady"/> has fired.
        /// </summary>
        internal static void TrackReady(NetworkClient client, out Func<bool> isReady)
        {
            bool ready = false;
            client.SessionReady += () => ready = true;
            isReady = () => ready;
        }

        /// <summary>Pumps until <paramref name="client"/> has got its welcome message.</summary>
        internal static bool WaitUntilReady(
            float seconds,
            NetworkClient client,
            params NetworkSession[] sessions)
        {
            TrackReady(client, out var isReady);
            return PumpUntil(seconds, isReady, sessions);
        }

        /// <summary>
        /// Connects several clients and waits until ALL of them have got their welcome message. That is an
        /// important detail: two <c>WaitUntilReady</c> calls in a row (as in <c>a &amp;&amp; b</c>) would both
        /// hook a separate <c>SessionReady</c> counter onto the same client, and the second would never become
        /// true - it only listens for an event that has already happened.
        /// </summary>
        internal static bool WaitUntilAllReady(
            float seconds,
            NetworkClient[] clients,
            params NetworkSession[] sessions)
        {
            var ready = new Func<bool>[clients.Length];
            for (int i = 0; i < clients.Length; i++)
                TrackReady(clients[i], out ready[i]);

            Func<bool> all = () =>
            {
                foreach (var isReady in ready)
                {
                    if (!isReady())
                        return false;
                }

                return true;
            };

            return PumpUntil(seconds, all, sessions);
        }

        /// <summary>
        /// Sends a chat message and returns the stopwatch from just before the send, so that the test can
        /// measure the delay itself.
        /// </summary>
        internal static Stopwatch SendChat(NetworkClient client, string text)
        {
            var watch = Stopwatch.StartNew();
            var chat = NetworkClient.Rent<ChatMessage>();
            chat.Text = text;
            client.Send(client.ServerPeerId, chat);
            return watch;
        }

    }

        /// <summary>
        /// A <see cref="NetworkSession"/> that only counts ticks, so the clock can be tested without opening a
        /// socket.
        /// </summary>
        internal sealed class CountingSession : NetworkSession
        {
            public CountingSession(NetworkConfig config)
                : base(config, isServer: true)
            {
            }

            /// <summary>How many whole ticks have been released so far.</summary>
            public int TickCount { get; private set; }

            /// <summary>
            /// Seconds per tick for a given rate. Used by the tests so that they send exactly one tick interval
            /// at a time - six calls with one interval each is stable, while one call with six intervals
            /// becomes 5.9999... ticks in binary floating point and gives a flake.
            /// </summary>
            public static float TickIntervalSeconds(int tickRate) => 1f / tickRate;

            /// <summary>Starts the clock without opening any socket.</summary>
            public void Begin() => SetRunning(true);

            /// <summary>Counts through <see cref="OnTick"/> instead of with a clock of its own.</summary>
            protected override void OnTick() => TickCount++;

            /// <inheritdoc/>
            internal override void HandleConnectionRequest(LiteNetLib.ConnectionRequest request) =>
                request.Reject();
        }
}
