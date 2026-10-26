using System;

namespace Krut.Netcode
{
    /// <summary>
    /// All the settings a network session needs - data-driven, no hard-coded values in the library. The game
    /// sets what is game-specific (app id, port, version number, max players) and leaves the rest at the
    /// default. The values are clamped to valid ranges in <see cref="Validate"/> (which
    /// <see cref="NetworkSession"/> runs in its constructor), so a broken value from a settings file or a
    /// console command cannot make the network layer refuse to start. The distinction the library does draw is
    /// between user data and programmer data: everything here is user data and is clamped, while a wrong tick
    /// rate passed to <see cref="NetworkClock"/>'s constructor is a programming error and is also clamped - but
    /// a wrong message type byte (see <see cref="INetworkMessage.WireType"/>) throws, because no clamping could
    /// make a message with the wrong identity decodable.
    /// </summary>
    public sealed class NetworkConfig
    {
        /// <summary>Default port the library listens/dials on if the game doesn't specify anything else.</summary>
        public const int DefaultPort = 25555;

        /// <summary>
        /// Identifies the game over the network - sent in the handshake and rejects connections to another app
        /// on the same port (e.g. two different games on the same port).
        /// </summary>
        public string ApplicationId { get; set; } = "krut.netcode";

        /// <summary>The protocol version.</summary>
        public int ProtocolVersion { get; set; } = 1;

        /// <summary>
        /// The UDP port to bind (server) or dial (client). 0 = any port (client only). Valid range 0-65535;
        /// anything outside is reset to <see cref="DefaultPort"/>. On the server the port must be free, and a
        /// taken port is one of the two ways <see cref="NetworkServer.Start"/> returns false (see the error
        /// table in <c>docs/guide.md</c>).
        /// </summary>
        public int Port { get; set; } = DefaultPort;

        /// <summary>
        /// Fixed tick rate for the session's clock (see <see cref="NetworkSession.CurrentTick"/>). Game
        /// replication normally sends one state packet per tick, so the value determines both the bandwidth and
        /// how smoothly motion is interpolated. 30 Hz is enough for most games; 60 if the interpolation is
        /// jerky. Valid range 1-240 Hz; outside that it resets to 30. The tick rate should equal the physics
        /// timestep, not be chosen freely - reconciliation replays inputs and is only correct if a step is
        /// reproducible, which needs both sides to take the same number of steps. <c>docs/guide.md</c>
        /// section 5 works through it; the short version is that <c>NetworkTimeSync</c> compares ticks, not
        /// seconds, so a client at a different tick rate is measuring the wrong unit.
        /// </summary>
        public int TickRate { get; set; } = 30;

        /// <summary>Max number of simultaneous clients on the server.</summary>
        public int MaxClients { get; set; } = 16;

        /// <summary>
        /// Count bytes/packets/loss per peer - costs a little, but gives numbers in the console and the debug
        /// overlay. Turning this off makes every one of those read 0, which looks exactly like a perfect
        /// network - so the status line hides the traffic row entirely rather than printing zeros (see
        /// <see cref="NetworkDiagnostics"/>).
        /// </summary>
        public bool EnableStatistics { get; set; } = true;

        /// <summary>Search for the largest MTU that works against each client.</summary>
        public bool MtuDiscovery { get; set; } = true;

        /// <summary>A fixed MTU (bytes of UDP payload) for every peer, or 0 to leave it to LiteNetLib.</summary>
        public int MtuOverride { get; set; }

        /// <summary>
        /// The packet-layer chain handed to LiteNetLib (probe, simulation, later the secure layer), or null for
        /// plain UDP. A chain belongs to one session, so a config shared by several sessions must leave this null
        /// and use <see cref="NetworkDatagramChain.DefaultFactory"/> instead. Null costs nothing; a chain makes
        /// LiteNetLib copy every outbound packet.
        /// </summary>
        public NetworkDatagramChain? DatagramLayer { get; set; }

        /// <summary>
        /// How often (ms) LiteNetLib measures ping against each peer - the value shows up in
        /// <see cref="NetworkPeerState.Ping"/>. Minimum 100 ms; below that it resets to 100. 1000 ms is once
        /// per second, which is enough to notice a peer that has gone bad without spending measurable
        /// bandwidth: a ping probe is a handful of bytes, and at one per second per peer that is invisible next
        /// to a state packet at 30-60 Hz.
        /// </summary>
        public int PingIntervalMs { get; set; } = 1000;

        /// <summary>
        /// Time without packets (ms) before a connection is considered dead. A player who is genuinely still
        /// there but on a bad link must not be kicked, so this is deliberately far above the ping interval: it
        /// is a "this is over" value, not a "this link is bad" value.
        /// </summary>
        public int DisconnectTimeoutMs { get; set; } = 8000;

        /// <summary>
        /// Pause (ms) between reconnect attempts in the client (see <see cref="NetworkConfig.AutoReconnect"/>).
        /// </summary>
        public int ReconnectDelayMs { get; set; } = 500;

        /// <summary>Max number of reconnect attempts in the client before it gives up and reports an error.</summary>
        public int MaxConnectAttempts { get; set; } = 8;

        /// <summary>
        /// How often (ms) LiteNetLib re-sends the connection request while it waits for the server's answer,
        /// within ONE connection attempt. The window must be longer than the round trip. These were once 0 and
        /// 1 (the reconnect was meant to be the library's job alone), which gave every attempt a window of one
        /// LiteNetLib update, about 15-30 ms: the server's accept arrived after the client had already given
        /// up, so ANY path with more than ~30 ms RTT - the internet, a relay - could never connect, while LAN
        /// and loopback tests passed.
        /// </summary>
        public int ConnectRequestIntervalMs { get; set; } = 500;

        /// <summary>
        /// How many times LiteNetLib sends the connection request in one attempt before it gives up (see
        /// <see cref="ConnectRequestIntervalMs"/>).
        /// </summary>
        public int ConnectRequestAttempts { get; set; } = 10;

        /// <summary>
        /// Lets the session send and receive unconnected datagrams
        /// (<see cref="NetworkSession.SendUnconnected"/>, <see cref="NetworkSession.UnconnectedReceived"/>):
        /// small packets to an address that is not a peer.
        /// </summary>
        public bool UnconnectedMessages { get; set; }

        /// <summary>
        /// Should the client automatically try to connect again when the connection is lost or the server
        /// doesn't answer? At library level (not LiteNetLib's own reconnect) so that the game can show
        /// "Reconnecting..." in a menu. A connection rejected by the server never retries regardless of this
        /// setting - a rejection is an answer, not an outage.
        /// </summary>
        public bool AutoReconnect { get; set; }

        /// <summary>
        /// IPv6 is supported (bind/connect over both address families). False means a pure IPv4 bind, which is
        /// the fix on machines that have no IPv6 socket at all - a surprisingly common case inside corporate
        /// VPNs and containers, and the failure there is a silent bind that never receives anything.
        /// </summary>
        public bool IPv6Support { get; set; } = true;

        /// <summary>
        /// How many ticks <see cref="NetworkSession.Update"/> is allowed to catch up before it stops taking
        /// more - a frame that was 3 seconds long (alt-tab, loading) would otherwise send hundreds of state
        /// packets in a row. Minimum 1; below that it resets to 1. 5 is a little above the two ticks a single
        /// 144 Hz frame needs at 30 Hz, so ordinary frame jitter never hits the limit, while a real hitch does.
        /// This is the library's only defence against a spike becoming a packet flood, and the cost is a
        /// bounded, deliberate desynchronisation - see <see cref="NetworkClock.Advance"/> for exactly what a
        /// clamped frame means for determinism.
        /// </summary>
        public int MaxCatchUpTicks { get; set; } = 5;

        /// <summary>Clamps the values to valid intervals.</summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ApplicationId))
                ApplicationId = "krut.netcode";
            if (Port is < 0 or > 65535)
                Port = DefaultPort;
            if (ProtocolVersion <= 0)
                ProtocolVersion = 1;
            if (TickRate is < 1 or > 240)
                TickRate = 30;
            if (MaxClients < 1)
                MaxClients = 1;
            if (PingIntervalMs < 100)
                PingIntervalMs = 100;
            if (DisconnectTimeoutMs < 1000)
                DisconnectTimeoutMs = 1000;
            if (ReconnectDelayMs < 100)
                ReconnectDelayMs = 100;
            if (MaxConnectAttempts < 1)
                MaxConnectAttempts = 1;
            if (ConnectRequestIntervalMs < 50)
                ConnectRequestIntervalMs = 50;
            if (ConnectRequestAttempts < 1)
                ConnectRequestAttempts = 1;
            if (MaxCatchUpTicks < 1)
                MaxCatchUpTicks = 1;
        }

        /// <summary>Seconds per tick - the session's clock is stepped by this, not by the frame.</summary>
        public float TickInterval => 1f / TickRate;

        /// <summary>Copy of this setting (so that a session isn't affected if the game changes its own settings instance).</summary>
        public NetworkConfig Clone() => (NetworkConfig)MemberwiseClone();
    }
}
