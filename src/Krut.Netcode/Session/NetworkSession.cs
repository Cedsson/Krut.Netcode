using System;
using System.Buffers;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Krut.Netcode
{
    /// <summary>
    /// Shared base for <see cref="NetworkServer"/> and <see cref="NetworkClient"/>: owns LiteNetLib's
    /// <see cref="NetManager"/>, holds the peer and message lists, drives the tick clock and reports everything
    /// that happens to the game through events. The session has no graphics or engine dependency, so
    /// a dedicated server (no window) can run exactly the same code. The packet layout. A packet is a type byte
    /// followed by the message's own fields, and nothing else. Byte 0 is the type byte: the
    /// <see cref="NetworkMessageType"/> value for an built-in message, the registered
    /// <see cref="INetworkMessage.WireType"/> for a message a game added. Bytes 1 through n are whatever
    /// <see cref="INetworkMessage.Write"/> wrote, in the order that type chose. There is no length prefix and
    /// no header of our own - the sequence numbers, the acknowledgements and the four channel queues all live
    /// in the LiteNetLib header underneath this byte, which is why the library has no say in them (see
    /// <see cref="NetworkChannels"/>). Why that layout. The size of a state packet can be computed up front,
    /// which is what lets it fit in one UDP datagram and travel <see cref="NetworkDelivery.Unreliable"/> - a
    /// fragmented packet would have to be either reliable or lost, and reliability is exactly what a per-tick
    /// state update must not pay for. And an unrecognised type byte can be detected and reported without having
    /// read a single field, so a peer running a different build is tolerated and counted instead of silently
    /// desynced (see <see cref="HandleMessage"/>). LiteNetLib's own event queue runs after <see cref="Update"/>
    /// has fetched the network packets, so game logic never ends up inside the UDP code (see
    /// <see cref="NetworkListener"/> for why).
    /// </summary>
    public abstract class NetworkSession : IDisposable
    {
        private readonly NetManager _manager;
        private readonly NetworkListener _listener;
        private readonly NetworkClock _clock;

        /// <summary>Something was handed to LiteNetLib since the last <see cref="Flush"/>.</summary>
        private bool _unflushed;

        /// <summary>Reused for sending - no allocations per packet (see <see cref="SendInternal"/>). The parameter is the initial size.</summary>
        private readonly NetDataWriter _writer = new(true, 256);

        /// <summary>
        /// Keyed by peer id, not by peer object: a message handler routinely wants to find a peer from an id in
        /// an incoming payload, and the id is what a game puts in its own messages (see
        /// <see cref="PlayerInfoMessage.PeerId"/>).
        /// </summary>
        private readonly Dictionary<int, NetworkPeerState> _peers = new();

        /// <summary>Events waiting to be handled (see <see cref="NetworkListener"/>).</summary>
        private List<NetworkEvent> _pending = new(16);

        /// <summary>
        /// The buffer <see cref="DrainEvents"/> iterates. The two lists are swapped rather than copied, so a
        /// handler that causes a disconnect (which appends to <see cref="_pending"/>) cannot invalidate the
        /// iteration in progress.
        /// </summary>
        private List<NetworkEvent> _draining = new(16);

        /// <summary>Reused for decoding every received message (the session is single-threaded, see <see cref="Update"/>).</summary>
        private readonly NetDataReader _reader = new();

        /// <summary>Cached so that <see cref="Update"/> does not create a delegate per frame.</summary>
        private readonly Action _onTick;

        private bool _disposed;

        /// <summary>
        /// The active simulation, or null when none is switched on. Null is the common case (see
        /// <see cref="NetworkSimulation.FromEnvironment"/>), and then both delay queues are empty and the code
        /// path is exactly the production one.
        /// </summary>
        private NetworkSimulation? _simulation;

        /// <summary>This session's sent packets waiting out their simulated delay, and the packet-loss draw (see <see cref="SendInternal"/>); a separate random source per session.</summary>
        private readonly NetworkDelayQueue _delayQueue = new();

        protected NetworkSession(NetworkConfig config, bool isServer)
        {
            Config = (config ?? new NetworkConfig()).Clone();
            Config.Validate();
            IsServer = isServer;
            _clock = new NetworkClock(Config.TickRate, Config.MaxCatchUpTicks);
            _listener = new NetworkListener(this);
            _onTick = OnTick;

            // The second parameter to NetManager is an extra layer on top of UDP (encryption/checksum). null =
            // plain UDP, i.e. LiteNetLib's own (sequence, acks) is enough - encryption is not the library's job.
            DatagramChain = Config.DatagramLayer ?? NetworkDatagramChain.DefaultFactory?.Invoke();
            _manager = new NetManager(_listener, DatagramChain!);
            _manager.ChannelsCount = NetworkChannels.Count;
            // IPv6 is not controlled through an instance property in LiteNetLib 2.x but through which address
            // you bind in StartListener (null = dual-stack, see StartListener(string?, int)), so
            // Config.IPv6Support is applied there instead of here.
            _manager.EnableStatistics = Config.EnableStatistics;
            _manager.MtuDiscovery = Config.MtuDiscovery;
            if (Config.MtuOverride > 0)
                _manager.MtuOverride = Config.MtuOverride;
            _manager.PingInterval = Config.PingIntervalMs;
            _manager.DisconnectTimeout = Config.DisconnectTimeoutMs;
            // LiteNetLib's ReconnectDelay/MaxConnectAttempts are the RESEND of the connection request inside
            // one attempt, not a reconnect after a lost connection (that stays at library level, NetworkClient,
            // so the game can show "reconnecting...").
            _manager.ReconnectDelay = Config.ConnectRequestIntervalMs;
            _manager.MaxConnectAttempts = Config.ConnectRequestAttempts;
            _manager.UnconnectedMessagesEnabled = Config.UnconnectedMessages;
            // Events run synchronously in PollEvents (on our thread) - that is
            // the whole point of draining the queue in Update.
            _manager.UnsyncedEvents = false;
            // LiteNetLib otherwise reuses the peer object when a connection closes, which would make
            // NetworkPeerState.Tag (the game's data) spill over to the next one.
            _manager.AutoRecycle = false;
        }

        /// <summary>The settings the session runs with (copied from the <see cref="NetworkConfig"/> that was created).</summary>
        public NetworkConfig Config { get; }

        /// <summary>True for the server, false for the client.</summary>
        protected bool IsServer { get; }

        /// <summary>Is the network up right now (a server that listens, a client that is connected)?</summary>
        public bool IsRunning { get; private set; }

        /// <summary>
        /// Number of whole ticks since the session started - the number that travels in replicated messages, so
        /// that the receiver knows which update it is looking at.
        /// </summary>
        public int CurrentTick => _clock.Tick;

        /// <summary>Where in the ongoing tick we are (0-1) - for interpolating motion between ticks.</summary>
        public float TickProgress => _clock.TickProgress;

        /// <summary>The session's time in seconds (comparable between server and client).</summary>
        public double Time => _clock.Time;

        /// <summary>All connected peers (on the server: the clients, on the client: the server).</summary>
        public IReadOnlyCollection<NetworkPeerState> Peers => _peers.Values;

        /// <summary>Number of connected peers.</summary>
        public int ConnectedPeerCount => _peers.Count;

        /// <summary>The peer id this side has in the session.</summary>
        public int LocalPeerId { get; private set; } = -1;

        /// <summary>The remote party as a peer object (on the client: the server).</summary>
        public NetworkPeerState? RemotePeer
        {
            get
            {
                foreach (var peer in _peers.Values)
                    return peer;

                return null;
            }
        }

        /// <summary>The peer with the given id, or false. If the peer doesn't exist (wrong id, or the connection just closed) it isn't there.</summary>
        public bool TryGetPeer(int peerId, out NetworkPeerState peer) => _peers.TryGetValue(peerId, out peer!);

        /// <summary>
        /// Traffic figures for the session (zero if <see cref="NetworkConfig.EnableStatistics"/> is off). They
        /// also do not see anything the library dropped before it reached the socket - the simulated packet loss
        /// counts as <see cref="DroppedPacketCount"/>, and a message too large for a datagram never becomes a
        /// packet at all.
        /// </summary>
        public NetworkStatistics Statistics => new(
            _manager.Statistics.PacketsSent,
            _manager.Statistics.PacketsReceived,
            _manager.Statistics.BytesSent,
            _manager.Statistics.BytesReceived,
            _manager.Statistics.PacketLossPercent);

        /// <summary>
        /// Number of messages with a type this side doesn't recognise - most common when a client runs a newer
        /// build.
        /// </summary>
        public int UnknownMessageCount { get; private set; }

        /// <summary>Number of messages that could not be decoded (too short a packet, broken header).</summary>
        public int MalformedMessageCount { get; private set; }

        /// <summary>Number of packets the simulation has thrown away.</summary>
        public int DroppedPacketCount => _delayQueue.DroppedCount;

        /// <summary>
        /// The packet-layer chain this session passes to LiteNetLib: <see cref="NetworkConfig.DatagramLayer"/>, else
        /// whatever <see cref="NetworkDatagramChain.DefaultFactory"/> returned for this session, else null.
        /// </summary>
        public NetworkDatagramChain? DatagramChain { get; }

        /// <summary>Counters of the simulation (drops by cause, duplicates, reorders, queue delay). The same instance every call.</summary>
        public NetworkSimulationStats SimulationStats => _delayQueue.GetStats();

        /// <summary>The simulation that is switched on right now, or null (see <see cref="ApplySimulation"/>).</summary>
        public NetworkSimulation? Simulation => _simulation;

        /// <summary>Switches on a network simulation (see <see cref="NetworkSimulation"/>).</summary>
        public void ApplySimulation(NetworkSimulation simulation)
        {
            if (simulation is null)
                throw new ArgumentNullException(nameof(simulation));

            // Before any state changes: a datagram-level simulation without the layer would silently do nothing.
            if (DatagramChain is { } chain)
                chain.Simulation = simulation;
            else if (simulation.DatagramLevel)
                throw new InvalidOperationException("The simulation is datagram-level but the session has no datagram chain (NetworkConfig.DatagramLayer or NetworkDatagramChain.DefaultFactory).");

            _simulation = simulation;

            // LiteNetLib's own simulation fields are set as well, so that a build with SIMULATE_NETWORK defined
            // (or a debug-built copy of the library) gets its own simulation on top. In a normal release build
            // of the package the fields are trimmed away (the methods that read them are
            // [Conditional("DEBUG")]), which is exactly why the library has its own queue below.
            int min = Math.Max(0, simulation.LatencyMs - simulation.JitterMs) * 2;
            int max = Math.Max(0, simulation.LatencyMs + simulation.JitterMs) * 2;
            _manager.SimulateLatency = simulation.LatencyMs > 0;
            _manager.SimulationMinLatency = min;
            _manager.SimulationMaxLatency = max;
            _manager.SimulatePacketLoss = simulation.LossChance > 0f;
            // LiteNetLib counts loss in whole percent (1-100), while
            // NetworkSimulation keeps it as a fraction 0.0-1.0.
            _manager.SimulationPacketLossChance = (int)MathF.Round(simulation.LossChance * 100f);
        }

        /// <summary>Switches off an active simulation.</summary>
        public void ClearSimulation()
        {
            _simulation = null;
            if (DatagramChain is { } chain)
                chain.Simulation = null;
            _manager.SimulateLatency = false;
            _manager.SimulationMaxLatency = 0;
            _manager.SimulationMinLatency = 0;
            _manager.SimulatePacketLoss = false;
            _manager.SimulationPacketLossChance = 0;

            _delayQueue.SendAll();
        }

        /// <summary>
        /// One line of English text about the session's network state: role, state, tick and peer count. Meant
        /// for the console and the debug overlay - see <see cref="NetworkDiagnostics"/> for the multi-line
        /// version with peer details and traffic figures.
        /// </summary>
        public string Describe()
        {
            string role = IsServer ? "server" : "client";
            string state = !IsRunning ? "off" : $"{CurrentTick} @ {Config.TickRate} Hz, {ConnectedPeerCount} peers";
            return $"{role}: {state}";
        }

        /// <summary>A peer connected.</summary>
        public event Action<NetworkPeerState>? PeerConnected;

        /// <summary>A peer disconnected - the peer object is still readable (name, tag) but must not be reused.</summary>
        public event Action<NetworkPeerState, DisconnectInfo>? PeerDisconnected;

        /// <summary>A message arrived.</summary>
        public event Action<NetworkEnvelope>? MessageReceived;

        /// <summary>Network errors, broken messages and rejected connections are reported here (no logging in the library - the game decides).</summary>
        public event Action<NetworkError>? Error;

        /// <summary>The list that <see cref="NetworkListener"/> writes to and <see cref="Update"/> drains.</summary>
        internal List<NetworkEvent> PendingEvents => _pending;

        /// <summary>
        /// Called by <see cref="NetworkListener"/> for every received packet: copies the bytes into a pooled
        /// buffer (LiteNetLib reuses its own) and queues them for <see cref="DrainEvents"/>.
        /// </summary>
        internal void EnqueueReceived(NetPeer peer, NetPacketReader reader, NetworkDelivery delivery)
        {
            int length = reader.AvailableBytes;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
            reader.GetRemainingBytesSpan().CopyTo(buffer);
            _pending.Add(NetworkEvent.Received(peer, buffer, length, delivery));
        }

        /// <summary>Gives the pooled receive buffers of queued events back to the pool.</summary>
        private static void ReleaseBuffers(List<NetworkEvent> events)
        {
            foreach (var networkEvent in events)
            {
                if (networkEvent.Kind == NetworkEventKind.Received && networkEvent.Data is { } data)
                    ArrayPool<byte>.Shared.Return(data);
            }
        }

        /// <summary>
        /// Runs one frame of networking: fetches packets, handles peer and
        /// message events and releases one <see cref="OnTick"/> per whole tick
        /// that has passed. Called by the game once per frame.
        /// <para>
        /// <b>The frame-order contract.</b> The five steps below are in this
        /// order for a reason, and the order is the contract a subclass
        /// inherits:
        /// <list type="number">
        /// <item>Simulated outbound packets whose delay has expired are
        /// released <i>before</i> <c>PollEvents</c>, so they get away in the
        /// same frame instead of waiting a frame each.</item>
        /// <item><c>PollEvents</c> fills the pending queue. Nothing is
        /// delivered yet - <see cref="NetworkListener"/> only appends.</item>
        /// <item><see cref="DrainEvents"/> delivers: peer connect and
        /// disconnect first, in arrival order, then the messages of that same
        /// queue. A message that arrives in the same batch as a peer
        /// disconnect therefore sees the peer already removed.</item>
        /// <item>Simulated inbound packets whose delay has expired are
        /// handled, so a delayed message is delivered in the frame its delay
        /// ends rather than one later.</item>
        /// <item><see cref="NetworkClock.Advance"/> releases whole ticks and
        /// calls <see cref="OnTick"/> - last, so that everything the network
        /// brought in this frame is already in place before the game
        /// simulates, and a snapshot sent from <c>OnTick</c> describes the
        /// state at the end of the tick rather than the state before the
        /// incoming messages were applied.</item>
        /// </list>
        /// </para>
        /// <para>
        /// <b>What must not be active during those steps.</b> The session is
        /// single-threaded by design: a handler that blocks, throws on
        /// another thread, or re-enters <c>Update</c> corrupts the queue
        /// swap in <see cref="DrainEvents"/> (the two lists are the same
        /// objects on the next frame). A handler may however freely send,
        /// disconnect and broadcast - the peer list is copied before
        /// iteration for exactly that reason.
        /// </para>
        /// </summary>
        public virtual void Update(float deltaSeconds)
        {
            if (_disposed || !IsRunning)
                return;

            // Sent packets whose delay has expired go out BEFORE PollEvents, so that they get away in the same
            // frame.
            if (_delayQueue.FlushDue())
                _unflushed = true;
            Flush();
            _manager.PollEvents();
            DrainEvents();
            _clock.Advance(deltaSeconds, _onTick);
            Flush();
        }

        /// <summary>
        /// Wakes LiteNetLib's logic thread when something has been sent since the last flush, so the packets
        /// leave now instead of on its next timed update (<c>UpdateTime</c>, ~15 ms, and the timer granularity
        /// on top).
        /// </summary>
        public void Flush()
        {
            if (!_unflushed || _disposed || !IsRunning)
                return;

            _unflushed = false;
            _manager.TriggerUpdate();
        }

        /// <summary>
        /// Called once per whole tick (see <see cref="NetworkConfig.TickRate"/>), after everything else in
        /// <see cref="Update"/>. Override it to send replicated state - that is the natural place to send a
        /// state packet, because that is exactly one tick at a time.
        /// </summary>
        protected virtual void OnTick()
        {
        }

        /// <summary>A peer connected.</summary>
        protected virtual void OnPeerConnectedEvent(NetworkPeerState peer)
        {
        }

        /// <summary>A peer disconnected.</summary>
        protected virtual void OnPeerDisconnectedEvent(NetworkPeerState peer, DisconnectInfo info)
        {
        }

        /// <summary>
        /// Sends a message to a peer. Throws <see cref="InvalidOperationException"/> if a game message carries
        /// a <see cref="INetworkMessage.WireType"/> that was never registered (see
        /// <see cref="GetWireTypeByte"/>).
        /// </summary>
        public void Send(int peerId, INetworkMessage message)
        {
            if (!_peers.TryGetValue(peerId, out var peer))
            {
                Report(new NetworkError(NetworkErrorCode.PeerUnknown, $"Peer {peerId} does not exist (connection closed?).", peerId));
                RecycleSent(message);
                return;
            }

            SendInternal(peer, message);
        }

        /// <summary>Sends a message to everyone except <paramref name="exceptPeerId"/> (-1 = everyone).</summary>
        public void Broadcast(INetworkMessage message, int exceptPeerId = -1)
        {
            int count = _peers.Count;
            if (count == 0)
            {
                RecycleSent(message);
                return;
            }

            // The peers are copied into a reused array: the dictionary does not allow changes during iteration,
            // and a send can cause someone to disconnect. A nested Broadcast finds the array taken and allocates.
            var targets = _broadcastTargets ?? new NetworkPeerState[Math.Max(count, 8)];
            _broadcastTargets = null;
            if (targets.Length < count)
                targets = new NetworkPeerState[count * 2];

            int n = 0;
            foreach (var peer in _peers.Values)
                targets[n++] = peer;

            for (int i = 0; i < n; i++)
            {
                if (targets[i].Id != exceptPeerId)
                    SendInternal(targets[i], message, recycle: false);

                targets[i] = null!;
            }

            _broadcastTargets = targets;
            RecycleSent(message);
        }

        private NetworkPeerState[]? _broadcastTargets;

        /// <summary>Hands a message that was sent (or not delivered at all) back to its pool; it belongs to the pool afterwards.</summary>
        private static void RecycleSent(INetworkMessage message)
        {
            byte wireType = message.Type != NetworkMessageType.None ? (byte)message.Type : message.WireType;
            NetworkMessages.Recycle(message, wireType);
        }

        /// <summary>Rents a message from the pool (merges with the session's own handling - see <see cref="INetworkMessage"/>).</summary>
        public static T Rent<T>()
            where T : class, INetworkMessage, new()
            => NetworkMessagePool<T>.Rent();

        /// <summary>
        /// Stops the network and releases resources. LiteNetLib sends the final packet only on the next
        /// <c>PollEvents</c>, so without a pump loop here the explanation never reaches the client - the server
        /// just closes the socket, and the client gets nothing but a silent loss that it later mistakenly
        /// blames on its own network.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            OnStopping();
            if (IsRunning)
            {
                foreach (var peer in new List<NetworkPeerState>(_peers.Values))
                    peer.Disconnect(null);

                // The final packets must get out of the queue before the socket is closed.
                PumpDisconnectNotices();

                _manager.Stop();
            }

            _peers.Clear();
            ReleaseBuffers(_pending);
            _pending.Clear();

            // Queued packets are released: they would otherwise hold references to peers and byte arrays that
            // no longer mean anything.
            _delayQueue.Clear();
            IsRunning = false;
            GC.SuppressFinalize(this);
        }

        /// <summary>Called by <see cref="NetworkServer"/> to answer an incoming connection request - see <see cref="NetworkListener.OnConnectionRequest"/>.</summary>
        internal abstract void HandleConnectionRequest(ConnectionRequest request);

        /// <summary>The UDP port this session's socket is bound to (LiteNetLib's LocalPort); 0 before it starts.</summary>
        protected int BoundPort => _manager.LocalPort;

        /// <summary>
        /// An unconnected datagram arrived (only with <see cref="NetworkConfig.UnconnectedMessages"/> on): who
        /// sent it and its bytes.
        /// </summary>
        public event Action<System.Net.IPEndPoint, byte[]>? UnconnectedReceived;

        internal void RaiseUnconnected(System.Net.IPEndPoint from, byte[] data) => UnconnectedReceived?.Invoke(from, data);

        /// <summary>
        /// Sends one unconnected datagram from this session's own socket to <paramref name="to"/> - the same
        /// socket the peers use, which is the point for NAT punch-through.
        /// </summary>
        public bool SendUnconnected(System.Net.IPEndPoint to, ReadOnlySpan<byte> data)
            => IsRunning && _manager.SendUnconnectedMessage(data.ToArray(), to);

        /// <summary>Starts listening on <paramref name="port"/>.</summary>
        protected bool StartListener(int port) => _manager.Start(port);

        /// <summary>
        /// Starts listening/dialing on a specific IPv4 address (e.g. <c>127.0.0.1</c> for a local server, or a
        /// specific NIC).
        /// </summary>
        protected bool StartListener(string? address, int port)
        {
            // LiteNetLib 2.x requires non-null addresses (unlike 1.x, where null meant "all"), so "all
            // addresses" becomes explicitly IPAddress.Any.
            var ipv4 = address is null ? System.Net.IPAddress.Any : System.Net.IPAddress.Parse(address);
            return _manager.Start(ipv4, Config.IPv6Support ? System.Net.IPAddress.IPv6Any : ipv4, port);
        }

        /// <summary>
        /// Closes the socket (peers are disconnected by the caller - the server preferably sends "The server is
        /// shutting down" first).
        /// </summary>
        protected void StopListener()
        {
            _manager.Stop();
            _peers.Clear();
        }

        /// <summary>
        /// Sends a connection request to <paramref name="endPoint"/> with <paramref name="connectData"/> as the
        /// handshake (the server reads it in <see cref="HandleConnectionRequest"/>).
        /// </summary>
        protected NetPeer? ConnectTo(System.Net.IPEndPoint endPoint, ReadOnlySpan<byte> connectData) => _manager.Connect(endPoint, connectData);

        /// <summary>Adds a peer to the list without any event being raised.</summary>
        protected NetworkPeerState AddPeerDirect(NetPeer peer) => AddPeer(peer);

        /// <summary>Called by <see cref="NetworkServer"/>/<see cref="NetworkClient"/> when the network has actually started/stopped.</summary>
        internal void SetRunning(bool running) => IsRunning = running;

        /// <summary>Marks the peer id this side has (the client, from <see cref="NetworkMessageType.Welcome"/>).</summary>
        internal void SetLocalPeerId(int peerId) => LocalPeerId = peerId;

        /// <summary>
        /// Jumps the clock to <paramref name="serverTick"/>, which a client does when its
        /// <see cref="NetworkTimeSync"/> has estimated where the server is. Had it been <c>internal</c>, every
        /// game would have needed its own public surface on top - which is exactly what leads to two games
        /// doing time sync in different ways.
        /// </summary>
        public void ResetClockTo(int serverTick) => _clock.ResetTo(serverTick);

        /// <summary>Adds a peer to the list - called when <see cref="NetworkListener"/> has seen a connected peer.</summary>
        internal NetworkPeerState AddPeer(NetPeer peer)
        {
            var state = new NetworkPeerState(peer);
            _peers[peer.Id] = state;
            return state;
        }

        /// <summary>Removes a peer from the list and returns it (the object is still readable by the listeners).</summary>
        internal NetworkPeerState? RemovePeer(int peerId)
        {
            if (!_peers.Remove(peerId, out var peer))
                return null;

            return peer;
        }

        /// <summary>Reports an error to the game (the library doesn't log network errors itself).</summary>
        internal void Report(NetworkError error) => Error?.Invoke(error);

        /// <summary>Sets the server's own values on a new peer (name from the handshake) - used by <see cref="NetworkServer"/>.</summary>
        internal void DescribePeer(NetworkPeerState peer, string name, int joinTick)
        {
            peer.Name = name;
            peer.JoinTick = joinTick;
        }

        /// <summary>Runs before <see cref="Dispose"/> stops the network - this is where e.g. a shutdown message is disconnected to all peers.</summary>
        protected virtual void OnStopping()
        {
        }

        /// <summary>
        /// Pumps the network briefly so that the final packets get sent. Nothing happens in
        /// <see cref="DrainEvents"/> here - the peer objects have already been removed from the list above, so
        /// the events cannot reach the game (and should not: a session that is shutting down must not get "peer
        /// joined" back after it said goodbye).
        /// </summary>
        private void PumpDisconnectNotices()
        {
            // Four rounds is enough: a final packet is sent in the first round and a peer leaves the shutdown
            // list right after. The loop runs a fixed number of times (and not until the list is empty) so that
            // it can never hang on an uncooperative peer.
            for (int i = 0; i < 4; i++)
                _manager.PollEvents();
        }

        /// <summary>
        /// Which byte to write first in the packet. built-in messages use the enum value; game messages use the
        /// byte they registered. Sending 0 would be correct but unusable: the receiving side looks the byte up
        /// in the registry, and 0 isn't there. <see cref="INetworkMessage.WireType"/> therefore carries the
        /// byte, and it is checked against the registry before sending - an unassigned value must not be able
        /// to make a message disappear silently. <c>Type</c> says "who am I" for code that can see the enum -
        /// which side of the wire a message belongs to, and which pool it comes from. <c>WireType</c> says
        /// "which byte identifies me" for code that must not know the enum at all: a game type has no enum
        /// value, and a byte is the only thing that survives the wire.
        /// </summary>
        private static byte GetWireTypeByte(INetworkMessage message)
        {
            if (message.Type != NetworkMessageType.None)
                return (byte)message.Type;

            byte wireType = message.WireType;
            if (wireType < NetworkMessages.GameTypeBase)
            {
                throw new InvalidOperationException(
                    $"Message type {message.GetType().Name} has Type = None but WireType = {wireType}. " +
                    $"Both Type and WireType must be set: Type = None means \"a game message\", " +
                    $"and WireType is the byte {nameof(NetworkMessages)} registered the type under.");
            }

            if (!NetworkMessages.IsRegistered(wireType))
            {
                throw new InvalidOperationException(
                    $"Message type {message.GetType().Name} reports WireType = {wireType}, which is not " +
                    $"registered. Call {nameof(NetworkMessages)}.{nameof(NetworkMessages.RegisterGameMessage)}<T>({wireType}) " +
                    "before anything is sent, otherwise the receiver cannot decode the packet.");
            }

            return wireType;
        }

        /// <summary>Writes and sends a message, and hands it back to the pool (it belongs to the session afterwards).</summary>
        private void SendInternal(NetworkPeerState peer, INetworkMessage message, bool recycle = true)
        {
            // The wire byte routes a game message back to its own pool; with 0 it would never be recycled.
            byte wireType = 0;
            try
            {
                _writer.Reset();
                wireType = GetWireTypeByte(message);
                _writer.Put(wireType);
                message.Write(_writer);

                // The packets being sent are the delayed ones, on both roles (see ApplySimulation). The bytes
                // are copied first: _writer is reused by the next Send, so that a delayed packet cannot be
                // overwritten by a later one.
                if (_simulation is { IsActive: true })
                {
                    _delayQueue.Enqueue(_simulation, peer, _writer, message.Delivery);
                    if (recycle)
                        NetworkMessages.Recycle(message, wireType);
                    return;
                }

                peer.Peer.Send(_writer, NetworkChannels.For(message.Delivery), NetworkChannels.ToDeliveryMethod(message.Delivery));
                _unflushed = true;
            }
            catch (TooBigPacketException)
            {
                // Common cause: someone sends a message on the unreliable channel that doesn't fit in one UDP
                // packet (the MTU). The error must not crash the game - but it must be visible, otherwise the
                // player silently loses motion.
                Report(new NetworkError(NetworkErrorCode.PacketTooLarge,
                    $"Message {message.Type} did not fit in a packet - split it up or use a reliable channel.", peer.Id));
            }

            if (recycle)
                NetworkMessages.Recycle(message, wireType);
        }

        /// <summary>Moves the queue of incoming events so that they can be handled without the list changing during iteration.</summary>
        private void DrainEvents()
        {
            if (_pending.Count == 0)
                return;

            (_draining, _pending) = (_pending, _draining);
            foreach (var networkEvent in _draining)
            {
                switch (networkEvent.Kind)
                {
                    case NetworkEventKind.PeerConnected:
                    {
                        // If the peer is already in the list (the server put it there during the handshake) it
                        // is reused, so that the peer object and the name stay together.
                        var peer = _peers.TryGetValue(networkEvent.Peer!.Id, out var existing) ? existing : AddPeer(networkEvent.Peer!);
                        OnPeerConnectedEvent(peer);
                        PeerConnected?.Invoke(peer);
                        break;
                    }
                    case NetworkEventKind.PeerDisconnected:
                    {
                        // A REJECTED connection arrives here without ever having been in the list: Skipping it
                        // then would silence exactly what players need to hear - the server's explanation ("the
                        // lobby is full", "update the game") is sent in that very event, and without it the
                        // client stays in "connecting" until it times out. The name and id are filled in by the
                        // server in the welcome message, which never came, so they are empty - and that is
                        // correct: the connection never got that far.
                        var peer = RemovePeer(networkEvent.Peer!.Id)
                            ?? new NetworkPeerState(networkEvent.Peer);
                        OnPeerDisconnectedEvent(peer, networkEvent.Disconnect);
                        PeerDisconnected?.Invoke(peer, networkEvent.Disconnect);
                        break;
                    }
                    case NetworkEventKind.Received:
                        // Received packets are always handled directly: the simulation delays what a session
                        // SENDS (see ApplySimulation), so a packet that arrives here has already waited out its
                        // delay on the other side.
                        HandleMessage(networkEvent);
                        break;
                    case NetworkEventKind.SocketError:
                        Report(new NetworkError(NetworkErrorCode.Socket, $"Network error: {networkEvent.Error}.", endPoint: networkEvent.EndPoint));
                        break;
                }
            }

            _draining.Clear();
        }

        /// <summary>
        /// Decodes a message and forwards it to the game - including
        /// protection against broken packets.
        /// <para>
        /// <b>How a packet becomes a pooled message.</b> The type byte is
        /// looked up by its raw value (see the note below), the factory rents
        /// an instance from that type's pool, the fields are read into it, and
        /// it is handed to <see cref="MessageReceived"/> inside a
        /// <see cref="NetworkEnvelope"/>. The instance goes back to the pool
        /// in the <c>finally</c> below - <i>after</i> the handler returns, and
        /// whether it returned normally or threw. So a handler that throws
        /// cannot leak a pooled instance, and a handler that keeps the
        /// reference gets the next message of that type instead, which is the
        /// ownership rule spelled out on <see cref="MessageReceived"/>.
        /// </para>
        /// <para>
        /// <b>What happens to a malformed or unknown message.</b> Neither
        /// case ends the connection, and that is the whole design: a packet
        /// this build cannot read is a protocol mismatch, not an attack, and
        /// the rest of the session is still perfectly playable.
        /// <list type="bullet">
        /// <item><b>Empty</b> - the type byte could not be read at all.
        /// Reported as <see cref="NetworkErrorCode.MalformedMessage"/>, counted
        /// in <see cref="MalformedMessageCount"/>.</item>
        /// <item><b>Unknown type byte</b> - reported as
        /// <see cref="NetworkErrorCode.UnknownMessageType"/>, counted in
        /// <see cref="UnknownMessageCount"/>, and the connection is kept. A
        /// message this side doesn't understand must not kick a player out of
        /// a match in progress.</item>
        /// <item><b>Truncated body</b> - <c>Read</c> ran past the end of the
        /// packet, i.e. the sender wrote more than it sent (bit error, or a
        /// client on a different protocol build). The instance is recycled
        /// <i>before</i> the report, so the pool stays balanced even on this
        /// path, and the rest of the batch is unaffected: the next packet in
        /// the same frame is decoded normally.</item>
        /// <item><b>No handler</b> - the game didn't subscribe to
        /// <see cref="MessageReceived"/>. The instance is recycled and
        /// nothing is reported; an unobserved message is a game bug, not a
        /// network fault.</item>
        /// </list>
        /// </para>
        /// </summary>
        private void HandleMessage(NetworkEvent networkEvent)
        {
            try
            {
                DecodeAndDeliver(networkEvent);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(networkEvent.Data!);
            }
        }

        private void DecodeAndDeliver(NetworkEvent networkEvent)
        {
            var reader = _reader;
            reader.SetSource(networkEvent.Data!, 0, networkEvent.Length);

            byte typeByte;
            if (!reader.TryGetByte(out typeByte))
            {
                Report(new NetworkError(NetworkErrorCode.MalformedMessage, "Empty message received.", networkEvent.Peer!.Id));
                return;
            }

            // Look it up by the RAW byte, not by the enum: a game project has registered its own message types
            // on bytes 16-255 (see NetworkMessages.RegisterGameMessage), and they have no values in
            // NetworkMessageType. That is the only difference from the earlier Create(type) call, and it is the
            // whole point: the library must not need to know a single game type for game traffic to be
            // delivered.
            var message = NetworkMessages.CreateFromWire(typeByte, out var registeredDelivery);
            if (message is null)
            {
                // Either a newer-version client or a broken header. It is reported, but the connection is kept:
                // a message this side doesn't understand must not kick a player out of a match in progress.
                UnknownMessageCount++;
                Report(new NetworkError(NetworkErrorCode.UnknownMessageType, $"Unknown message type {typeByte}.", networkEvent.Peer!.Id));
                return;
            }

            try
            {
                message.Read(reader);
            }
            catch (Exception)
            {
                // GetInt/GetString throw when the data runs out - i.e. the packet is shorter than what the
                // sender wrote (bit error or a client with a different protocol build).
                NetworkMessages.Recycle(message, typeByte);
                MalformedMessageCount++;
                Report(new NetworkError(NetworkErrorCode.MalformedMessage, $"Could not decode message type {typeByte} - packet too short.", networkEvent.Peer!.Id));
                return;
            }

            var handler = MessageReceived;
            if (handler is null)
            {
                NetworkMessages.Recycle(message, typeByte);
                return;
            }

            try
            {
                handler.Invoke(new NetworkEnvelope(networkEvent.Peer!.Id, message, networkEvent.Delivery, Time));
            }
            finally
            {
                // The instance returns to the pool immediately - see the documentation on MessageReceived about
                // copying what you want to keep.
                NetworkMessages.Recycle(message, typeByte);
            }
        }
    }
}
