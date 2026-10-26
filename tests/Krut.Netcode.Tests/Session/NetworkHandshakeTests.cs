using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Krut.Netcode;
using LiteNetLib.Utils;
using System.Numerics;
using Xunit;

using static Krut.Netcode.Tests.NetworkTestSupport;

namespace Krut.Netcode.Tests
{
    /// <summary>Connection handshake and unconnected punch packets.</summary>
    [Collection(NetworkTestCollection.Name)]
    public class NetworkHandshakeTests
    {
        // Handshake The welcome message is the one message that MUST arrive before everything else: without it
        // the client has no peer id, no server tick and no player name, and the game's startup hangs on
        // "connecting" forever. The trailing-space regression below is a real hang that shipped once
        // (SanitizeName never cleared its flag), and because sanitizing runs on both sides of the handshake it
        // hung the whole startup, not one test line.

        // The player name is set before Connect on purpose: the handshake is what carries it, so a name set
        // afterwards would only show up in the next lobby update.
        [Fact]

        public void Handshake_ConnectsOverDelayedPath()
        {
            // The handshake itself over a real round trip: a UDP proxy between client and server holds every
            // datagram in both directions.
            foreach (int rttMs in new[] { 120, 400 })
            {
                int port = FreeUdpPort();
                using var server = new NetworkServer(Config(port, maxClients: 1));
                Require(server.Start(), "The server did not start", $"port {port}");
                using var proxy = new UdpDelayProxy(port, rttMs / 2);

                using var client = new NetworkClient(Config(proxy.Port));
                var watch = Stopwatch.StartNew();
                Require(client.Connect("127.0.0.1", proxy.Port, "Far away"), "The client could not send its request");
                Require(WaitUntilReady(8f, client, server, client),
                    $"The client never became ready over a {rttMs} ms round trip",
                    $"state {client.State} after {watch.ElapsedMilliseconds} ms, {proxy.Forwarded} datagrams forwarded");
                Require(watch.ElapsedMilliseconds >= rttMs, "The proxy did not delay the handshake",
                    $"ready after {watch.ElapsedMilliseconds} ms");
            }
        }

        [Fact]

        public void Unconnected_PunchPacketsAndFixedLocalPort()
        {
            // NAT punch-through needs two things from the session: a client that sends from a port chosen in
            // advance (the one whose public mapping the game looked up), and a server that can send a datagram
            // to an address that is not a peer yet, from its own socket.
            int serverPort = FreeUdpPort();
            var serverConfig = Config(serverPort, maxClients: 1);
            serverConfig.UnconnectedMessages = true;
            using var server = new NetworkServer(serverConfig);
            Require(server.Start(), "The server did not start", $"port {serverPort}");

            int clientPort = FreeUdpPort();
            var clientConfig = Config(serverPort);
            clientConfig.UnconnectedMessages = true;
            using var client = new NetworkClient(clientConfig);
            var toClient = new List<(System.Net.IPEndPoint From, byte[] Data)>();
            var toServer = new List<(System.Net.IPEndPoint From, byte[] Data)>();
            client.UnconnectedReceived += (from, data) => toClient.Add((from, data));
            server.UnconnectedReceived += (from, data) => toServer.Add((from, data));

            Require(client.Connect("127.0.0.1", serverPort, "Puncher", localPort: clientPort), "The client could not connect");
            Require(client.LocalPort == clientPort, "The client did not send from the chosen port", $"{client.LocalPort} vs {clientPort}");
            Require(WaitUntilReady(5f, client, server, client), "The client never became ready from a fixed port");

            // The server punches towards the client's address before anything else would reach it.
            byte[] punch = { 0x4B, 0x52, 0x54, 0x50 };
            var clientAddress = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, clientPort);
            Require(server.SendUnconnected(clientAddress, punch), "The server could not send an unconnected datagram");
            Require(PumpUntil(3f, () => toClient.Count > 0, server, client), "The punch datagram never arrived");
            Require(toClient[0].Data.AsSpan().SequenceEqual(punch) && toClient[0].From.Port == serverPort,
                "The punch datagram arrived changed or from the wrong port", $"{toClient[0].Data.Length} B from {toClient[0].From}");

            Require(client.SendUnconnected(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, serverPort), punch),
                "The client could not send an unconnected datagram");
            Require(PumpUntil(3f, () => toServer.Count > 0, server, client), "The client's datagram never reached the server");
        }

        /// <summary>
        /// A real UDP hop with a fixed one-way delay in each direction: the client sends to <see cref="Port"/>,
        /// the proxy holds the datagram and passes it to the server from its own socket, and holds the server's
        /// reply the same way.
        /// </summary>
        private sealed class UdpDelayProxy : IDisposable
        {
            private readonly System.Net.Sockets.Socket _front;
            private readonly System.Net.Sockets.Socket _back;
            private readonly System.Net.IPEndPoint _server;
            private readonly int _delayMs;
            private readonly Thread _thread;
            private volatile bool _stop;
            private System.Net.EndPoint? _client;
            private readonly List<(long DueMs, bool ToServer, byte[] Data)> _held = new();
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private int _forwarded;

            public int Port { get; }
            public int Forwarded => Volatile.Read(ref _forwarded);

            public UdpDelayProxy(int serverPort, int oneWayDelayMs)
            {
                _server = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, serverPort);
                _delayMs = oneWayDelayMs;
                _front = Bind();
                _back = Bind();
                Port = ((System.Net.IPEndPoint)_front.LocalEndPoint!).Port;
                _thread = new Thread(Run) { IsBackground = true, Name = "UdpDelayProxy" };
                _thread.Start();
            }

            private static System.Net.Sockets.Socket Bind()
            {
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp) { Blocking = false };
                if (OperatingSystem.IsWindows()) socket.IOControl(-1744830452, new byte[] { 0 }, null); // SIO_UDP_CONNRESET off
                socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
                return socket;
            }

            private void Run()
            {
                var buffer = new byte[2048];
                while (!_stop)
                {
                    long now = _clock.ElapsedMilliseconds;
                    System.Net.EndPoint from = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                    while (_front.Available > 0)
                    {
                        int read = _front.ReceiveFrom(buffer, ref from);
                        _client = from;
                        _held.Add((now + _delayMs, true, buffer.AsSpan(0, read).ToArray()));
                    }
                    while (_back.Available > 0)
                    {
                        int read = _back.ReceiveFrom(buffer, ref from);
                        _held.Add((now + _delayMs, false, buffer.AsSpan(0, read).ToArray()));
                    }
                    for (int i = _held.Count - 1; i >= 0; i--)
                    {
                        if (_held[i].DueMs > now) continue;
                        var item = _held[i];
                        _held.RemoveAt(i);
                        try
                        {
                            if (item.ToServer) _back.SendTo(item.Data, _server);
                            else if (_client is not null) _front.SendTo(item.Data, _client);
                            Interlocked.Increment(ref _forwarded);
                        }
                        catch (System.Net.Sockets.SocketException) { }
                    }
                    Thread.Sleep(1);
                }
            }

            public void Dispose()
            {
                _stop = true;
                _thread.Join(1000);
                _front.Dispose();
                _back.Dispose();
            }
        }

        [Fact]

        public void Handshake_ServerAndClientInOneProcess()
        {
            int port = FreeUdpPort();
            using var server = new NetworkServer(Config(port));
            Require(server.Start(), "The server did not start", $"port {port}");
            Require(server.Port == port, "The server does not listen on the chosen port",
                server.Port.ToString(CultureInfo.InvariantCulture));

            var connected = new List<int>();
            server.PeerConnected += peer => connected.Add(peer.Id);

            using var client = new NetworkClient(Config(port));
            int ready = 0;
            var errors = new List<string>();
            client.SessionReady += () => ready++;
            client.Error += e => errors.Add(e.ToString());
            client.SetPlayerName("Anka");

            Require(client.Connect("127.0.0.1", port), "The client could not send its connection request");

            // The connection is not ready immediately - LiteNetLib's "connected" only means the UDP reply
            // arrived.
            Require(!client.IsSessionReady, "The client was ready immediately after Connect");

            // The welcome message is what makes the client "ready": it sets the peer id the game needs for its
            // own entities. Hooking SessionReady (not IsSessionReady) is therefore deliberate - the two differ
            // in time, and that difference is exactly what the game must respect.
            Require(PumpUntil(5f, () => ready > 0, server, client),
                "SessionReady never fired - the welcome message did not arrive");

            Require(client.IsSessionReady, "The client is not ready after SessionReady",
                client.State.ToString());
            Require(ready == 1, "SessionReady fired the wrong number of times",
                ready.ToString(CultureInfo.InvariantCulture));
            Require(client.LocalPeerId >= 0, "The client has no peer id");
            Require(client.ServerPeerId >= 0, "The client does not see the server as a peer");
            Require(server.ClientCount == 1, "The server does not see exactly one client",
                server.ClientCount.ToString(CultureInfo.InvariantCulture));
            Require(connected.Count == 1, "PeerConnected fired the wrong number of times",
                connected.Count.ToString(CultureInfo.InvariantCulture));
            Require(server.ClientCount == server.ConnectedPeerCount,
                "ClientCount and ConnectedPeerCount differ");

            // The server's tick is sent in the welcome message and the client adjusts its clock to it -
            // otherwise the two sides count in different time bases and all interpolation is wrong.
            PumpUntil(2f, () => client.CurrentTick > 0, server, client);
            Require(client.CurrentTick > 0, "The client did not sync its clock to the server",
                $"tick {client.CurrentTick}");

            Require(errors.Count == 0, "The client reported errors", string.Join(" | ", errors));
        }

        [Fact]

        public void Handshake_NameSanitized()
        {
            // The name is sent in the handshake and used in the lobby and chat lists.
            int port = FreeUdpPort();
            using var server = new NetworkServer(Config(port));
            Require(server.Start(), "The server did not start", $"port {port}");

            using var client = new NetworkClient(Config(port));

            // The name ends with a control character (a newline), which sanitizing turns into a space. That is
            // exactly the case that hung the game before the fix: SanitizeName used a while loop to drop a
            // trailing space, but never cleared the flag - so every name ending in a space spun forever, and it
            // took the handshake (that is, the game's startup) with it.
            client.SetPlayerName("  A n a m n  with  control chars\n");

            // The name is capped at 32 characters (NetworkCodec.MaxNameLength), so
            // the sanitized "A n a m n with control chars" fits without being cut.
            Require(client.PlayerName == "A n a m n with control chars",
                "The client did not sanitize its own name", $"'{client.PlayerName}'");

            Require(client.Connect("127.0.0.1", port), "The client could not connect");
            Require(WaitUntilReady(5f, client, server, client),
                "The connection never came up");

            int inspected = 0;
            foreach (var peer in server.Peers)
            {
                inspected++;
                Require(peer.Name == "A n a m n with control chars",
                    "The server did not set the sanitized name", $"'{peer.Name}'");
            }

            Require(inspected == 1, "The server had the wrong number of peers to check",
                $"{inspected}");
        }

        [Fact]

        /// <summary>
        /// The pure-function half of name sanitizing: no socket, no session, just
        /// <see cref="NetworkCodec.SanitizeName"/> called directly. The bug this group exists for was an
        /// infinite loop, not a wrong result: <c>SanitizeName</c> used a while loop to drop a trailing space
        /// but never cleared the flag it was looping on, so every name ending in a space spun forever. These
        /// calls have no timeout of their own, so if that bug ever comes back the test HANGS rather than
        /// reporting a clean "never returned" - which is the point. The whitespace-only cases at the end are
        /// the reason the "Player" fallback exists at all: sanitizing must never produce an empty name, because
        /// an empty name breaks the lobby list and the chat prefix.
        /// </summary>
        public void Handshake_TrailingSpaceNameDoesNotHang()
        {
            // Trailing whitespace, one form at a time. Each of these used to be
            // able to hang the process rather than return a wrong value.
            Require(NetworkCodec.SanitizeName("Anka\n") == "Anka",
                "A name ending in a newline was not sanitized",
                $"'{NetworkCodec.SanitizeName("Anka\n")}'");
            Require(NetworkCodec.SanitizeName("Anka ") == "Anka",
                "A name ending in a space was not sanitized",
                $"'{NetworkCodec.SanitizeName("Anka ")}'");
            Require(NetworkCodec.SanitizeName("Anka  ") == "Anka",
                "Double trailing spaces were not sanitized",
                $"'{NetworkCodec.SanitizeName("Anka  ")}'");
            Require(NetworkCodec.SanitizeName("  Anka  ") == "Anka",
                "Surrounding whitespace was not sanitized",
                $"'{NetworkCodec.SanitizeName("  Anka  ")}'");

            // A name that is nothing but whitespace or control characters must still yield the "Player"
            // fallback rather than an empty string - that is the whole reason the fallback exists.
            Require(NetworkCodec.SanitizeName("   ") == "Player",
                "A name of only spaces did not become \"Player\"",
                $"'{NetworkCodec.SanitizeName("   ")}'");
            Require(NetworkCodec.SanitizeName("\n\t") == "Player",
                "A name of only control characters did not become \"Player\"",
                $"'{NetworkCodec.SanitizeName("\n\t")}'");
        }

    }
}
