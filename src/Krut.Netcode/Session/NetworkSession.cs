using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
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

        private bool _writerInUse;
        private bool _inUpdate;

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

        /// <summary>Cached so that <see cref="Update"/> does not allocate a delegate for <see cref="NetworkDelayQueue.FlushDue"/>.</summary>
        private readonly Action<NetworkError> _reportSafely;

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
            _reportSafely = ReportSafely;

            DatagramChain = Config.DatagramLayer ?? NetworkDatagramChain.DefaultFactory?.Invoke();
            _manager = new NetManager(_listener, DatagramChain!);
            _manager.ChannelsCount = NetworkChannels.Count;
            _manager.IPv6Enabled = Config.IPv6Support;
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

        /// <summary>Number of inbound payload events currently queued for draining.</summary>
        public int PendingEventCount => _pendingPayloadEventCount;

        /// <summary>Peak number of queued inbound payload events observed in this session.</summary>
        public int PeakPendingEventCount { get; private set; }

        /// <summary>Total payload bytes currently held in queued inbound packets.</summary>
        public long PendingEventBytes => _pendingEventBytes;

        /// <summary>Peak queued inbound payload bytes observed in this session.</summary>
        public long PeakPendingEventBytes { get; private set; }

        /// <summary>Number of inbound events dropped or evicted due to queue, byte, packet-size, or per-peer burst limits.</summary>
        public long DroppedInboundEventCount { get; private set; }

        /// <summary>Total payload bytes of inbound events dropped or evicted due to overload limits.</summary>
        public long DroppedInboundBytes { get; private set; }

        /// <summary>Number of unhandled exceptions caught from application callbacks during event draining.</summary>
        public long CallbackExceptionCount { get; private set; }

        /// <summary>Number of events dispatched in the most recent <see cref="Update"/> drain.</summary>
        public int LastDrainedEventCount { get; private set; }

        /// <summary>Number of whole ticks released during the most recent <see cref="Update"/>.</summary>
        public int LastReleasedTickCount => _clock.LastReleasedTickCount;

        /// <summary>Number of excess ticks discarded by catch-up clamping during the most recent <see cref="Update"/>.</summary>
        public int LastClampedTickCount => _clock.LastClampedTickCount;

        /// <summary>Total ticks discarded by catch-up clamping across the session lifetime.</summary>
        public long TotalClampedTicks => _clock.TotalClampedTicks;

        /// <summary>Number of <see cref="Update"/> frames that hit <see cref="NetworkConfig.MaxCatchUpTicks"/>.</summary>
        public long CatchUpClampCount => _clock.CatchUpClampCount;

        /// <summary>Wall-clock duration in milliseconds of the most recent <see cref="Update"/> call.</summary>
        public double LastUpdateDurationMs { get; private set; }

        /// <summary>Wall-clock duration in milliseconds spent executing fixed ticks in the most recent <see cref="Update"/>.</summary>
        public double LastTickDurationMs { get; private set; }

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
        }

        /// <summary>Switches off an active simulation.</summary>
        public void ClearSimulation()
        {
            _simulation = null;
            if (DatagramChain is { } chain)
                chain.Simulation = null;

            _delayQueue.SendAll(_reportSafely);
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

        /// <summary>
        /// A message arrived. The pooled <see cref="NetworkEnvelope.Message"/> is reset and recycled as soon as
        /// all handlers return — copy any values you need to keep and do not retain the message reference.
        /// </summary>
        public event Action<NetworkEnvelope>? MessageReceived;

        /// <summary>Network errors, broken messages and rejected connections are reported here (no logging in the library - the game decides).</summary>
        public event Action<NetworkError>? Error;

        /// <summary>The list that <see cref="NetworkListener"/> writes to and <see cref="Update"/> drains.</summary>
        internal List<NetworkEvent> PendingEvents => _pending;

        private const int PooledPacketBufferSize = 2048;
        private const int PooledPacketBufferCapacity = 256;
        private readonly byte[]?[] _packetBuffers = new byte[]?[PooledPacketBufferCapacity];
        private int _packetBufferCount;
        private int _pendingPayloadEventCount;
        private long _pendingEventBytes;
        private int _pendingEvictCursor;
        private int _pollEpoch;
        private int _unconnectedEventsInPoll;
        private int _unknownPeerEventsInPoll;
        private int _lastOverloadErrorEpoch = -1;

        private byte[] RentPacketBuffer(int length)
        {
            if (length <= PooledPacketBufferSize)
            {
                if (_packetBufferCount > 0)
                {
                    byte[] buffer = _packetBuffers[--_packetBufferCount]!;
                    _packetBuffers[_packetBufferCount] = null;
                    return buffer;
                }

                return new byte[PooledPacketBufferSize];
            }

            return ArrayPool<byte>.Shared.Rent(length);
        }

        private void ReturnPacketBuffer(byte[] buffer)
        {
            if (buffer.Length == PooledPacketBufferSize)
            {
                if (_packetBufferCount < PooledPacketBufferCapacity)
                    _packetBuffers[_packetBufferCount++] = buffer;
                return;
            }

            ArrayPool<byte>.Shared.Return(buffer);
        }

        private void ReportInboundOverloadOnce(string reason, int peerId = -1)
        {
            if (_lastOverloadErrorEpoch == _pollEpoch)
                return;

            _lastOverloadErrorEpoch = _pollEpoch;
            ReportSafely(new NetworkError(NetworkErrorCode.InboundOverload, reason, peerId));
        }

        private bool TryEvictOneUnreliableGlobal()
        {
            while (_pendingEvictCursor < _pending.Count)
            {
                var ev = _pending[_pendingEvictCursor];
                if (ev.Kind == NetworkEventKind.Unconnected
                    || (ev.Kind == NetworkEventKind.Received && ev.Delivery == NetworkDelivery.Unreliable))
                {
                    EvictPendingAt(_pendingEvictCursor);
                    _pendingEvictCursor++;
                    return true;
                }

                _pendingEvictCursor++;
            }

            return false;
        }

        private bool TryEvictOneUnreliableForPeer(NetworkPeerState peerState)
        {
            if (peerState.InboundUnreliableInPoll <= 0)
                return false;

            while (peerState.InboundEvictCursor < _pending.Count)
            {
                var ev = _pending[peerState.InboundEvictCursor];
                if (ev.Kind == NetworkEventKind.Received
                    && ev.Delivery == NetworkDelivery.Unreliable
                    && ev.Peer is not null
                    && ev.Peer.Id == peerState.Id)
                {
                    EvictPendingAt(peerState.InboundEvictCursor);
                    peerState.InboundEvictCursor++;
                    return true;
                }

                peerState.InboundEvictCursor++;
            }

            return false;
        }

        private void EvictPendingAt(int index)
        {
            var ev = _pending[index];
            _pending[index] = NetworkEvent.Dropped();
            if (ev.Data is { } data)
                ReturnPacketBuffer(data);

            if (_pendingPayloadEventCount > 0)
                _pendingPayloadEventCount--;
            _pendingEventBytes = Math.Max(0, _pendingEventBytes - ev.Length);
            DroppedInboundEventCount++;
            DroppedInboundBytes += ev.Length;

            if (ev.Kind == NetworkEventKind.Unconnected)
            {
                if (_unconnectedEventsInPoll > 0)
                    _unconnectedEventsInPoll--;
            }
            else if (ev.Kind == NetworkEventKind.Received && ev.Peer is not null && _peers.TryGetValue(ev.Peer.Id, out var evictedPeer))
            {
                evictedPeer.DroppedInboundEvents++;
                if (evictedPeer.InboundPollEpoch == _pollEpoch)
                {
                    if (evictedPeer.InboundEventsInPoll > 0)
                        evictedPeer.InboundEventsInPoll--;
                    if (ev.Delivery == NetworkDelivery.Unreliable && evictedPeer.InboundUnreliableInPoll > 0)
                        evictedPeer.InboundUnreliableInPoll--;
                }
            }
        }

        /// <summary>
        /// Called by <see cref="NetworkListener"/> for every received packet: enforces size, per-peer burst, and
        /// global queue limits (evicting older unreliable packets before reliable ones), then copies bytes into a
        /// pooled buffer for <see cref="DrainEvents"/>.
        /// </summary>
        internal void EnqueueReceived(NetPeer peer, NetDataReader reader, NetworkDelivery delivery)
        {
            int length = reader.AvailableBytes;
            _peers.TryGetValue(peer.Id, out var peerState);
            if (peerState is not null && peerState.InboundPollEpoch != _pollEpoch)
            {
                peerState.InboundPollEpoch = _pollEpoch;
                peerState.InboundEventsInPoll = 0;
                peerState.InboundUnreliableInPoll = 0;
                peerState.InboundEvictCursor = 0;
            }

            if (length > Config.MaxReceivedPacketBytes || length > Config.MaxPendingEventBytes)
            {
                DroppedInboundEventCount++;
                DroppedInboundBytes += length;
                if (peerState is not null)
                    peerState.DroppedInboundEvents++;
                ReportInboundOverloadOnce(
                    $"Received packet of {length} bytes exceeds inbound packet/byte limit ({Config.MaxReceivedPacketBytes}/{Config.MaxPendingEventBytes}).",
                    peer.Id);
                return;
            }

            bool isUnreliable = delivery == NetworkDelivery.Unreliable;

            if (peerState is not null)
            {
                while (peerState.InboundEventsInPoll >= Config.MaxEventsPerPeerPerPoll)
                {
                    if (isUnreliable || !TryEvictOneUnreliableForPeer(peerState))
                    {
                        DroppedInboundEventCount++;
                        DroppedInboundBytes += length;
                        peerState.DroppedInboundEvents++;
                        ReportInboundOverloadOnce(
                            $"Peer {peer.Id} exceeded MaxEventsPerPeerPerPoll ({Config.MaxEventsPerPeerPerPoll}).",
                            peer.Id);
                        return;
                    }
                }
            }
            else if (_unknownPeerEventsInPoll >= Config.MaxEventsPerPeerPerPoll)
            {
                DroppedInboundEventCount++;
                DroppedInboundBytes += length;
                ReportInboundOverloadOnce(
                    $"Unattached peer {peer.Id} exceeded MaxEventsPerPeerPerPoll ({Config.MaxEventsPerPeerPerPoll}).",
                    peer.Id);
                return;
            }

            while (_pendingPayloadEventCount >= Config.MaxPendingEvents
                || _pendingEventBytes + length > Config.MaxPendingEventBytes)
            {
                if (isUnreliable || !TryEvictOneUnreliableGlobal())
                {
                    DroppedInboundEventCount++;
                    DroppedInboundBytes += length;
                    if (peerState is not null)
                        peerState.DroppedInboundEvents++;
                    ReportInboundOverloadOnce(
                        $"Inbound queue exceeded limit ({Config.MaxPendingEvents} events / {Config.MaxPendingEventBytes} bytes).",
                        peer.Id);
                    return;
                }
            }

            byte[] buffer = RentPacketBuffer(length);
            reader.GetRemainingBytesSpan().CopyTo(buffer);
            _pending.Add(NetworkEvent.Received(peer, buffer, length, delivery));
            _pendingPayloadEventCount++;
            _pendingEventBytes += length;
            if (_pendingPayloadEventCount > PeakPendingEventCount)
                PeakPendingEventCount = _pendingPayloadEventCount;
            if (_pendingEventBytes > PeakPendingEventBytes)
                PeakPendingEventBytes = _pendingEventBytes;

            if (peerState is not null)
            {
                peerState.InboundEventsInPoll++;
                if (isUnreliable)
                    peerState.InboundUnreliableInPoll++;
            }
            else
            {
                _unknownPeerEventsInPoll++;
            }
        }

        /// <summary>Maximum allowed size in bytes for an unconnected datagram.</summary>
        public const int MaxUnconnectedMessageBytes = 1500;

        /// <summary>Copies an unconnected datagram into a pooled buffer and queues it for <see cref="DrainEvents"/>.</summary>
        internal void EnqueueUnconnected(System.Net.IPEndPoint endPoint, NetDataReader reader)
        {
            if (UnconnectedReceived is null)
                return;

            int length = reader.AvailableBytes;
            if (length <= 0 || length > MaxUnconnectedMessageBytes)
            {
                MalformedMessageCount++;
                return;
            }

            if (_unconnectedEventsInPoll >= Config.MaxUnconnectedEventsPerPoll
                || _pendingPayloadEventCount >= Config.MaxPendingEvents
                || _pendingEventBytes + length > Config.MaxPendingEventBytes)
            {
                DroppedInboundEventCount++;
                DroppedInboundBytes += length;
                ReportInboundOverloadOnce("Inbound unconnected queue limit exceeded.");
                return;
            }

            byte[] buffer = RentPacketBuffer(length);
            reader.GetRemainingBytesSpan().CopyTo(buffer);
            _pending.Add(NetworkEvent.Unconnected(endPoint, buffer, length));
            _unconnectedEventsInPoll++;
            _pendingPayloadEventCount++;
            _pendingEventBytes += length;
            if (_pendingPayloadEventCount > PeakPendingEventCount)
                PeakPendingEventCount = _pendingPayloadEventCount;
            if (_pendingEventBytes > PeakPendingEventBytes)
                PeakPendingEventBytes = _pendingEventBytes;
        }

        /// <summary>Queues a socket error up to <see cref="NetworkConfig.MaxPendingEvents"/>.</summary>
        internal void EnqueueSocketError(System.Net.IPEndPoint endPoint, SocketError error)
        {
            if (_pending.Count >= Config.MaxPendingEvents)
            {
                DroppedInboundEventCount++;
                return;
            }

            _pending.Add(NetworkEvent.Socket(endPoint, error));
        }

        /// <summary>Gives the pooled receive buffers of queued events back to the pool.</summary>
        private void ReleaseBuffers(List<NetworkEvent> events) => ReleaseBuffersFrom(events, 0);

        private void ReleaseBuffersFrom(List<NetworkEvent> events, int startIndex)
        {
            for (int i = startIndex; i < events.Count; i++)
            {
                var networkEvent = events[i];
                if ((networkEvent.Kind == NetworkEventKind.Received || networkEvent.Kind == NetworkEventKind.Unconnected)
                    && networkEvent.Data is { } data)
                {
                    ReturnPacketBuffer(data);
                }
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

            if (_inUpdate)
            {
                CallbackExceptionCount++;
                ReportSafely(new NetworkError(
                    NetworkErrorCode.CallbackException,
                    "Re-entrant call to NetworkSession.Update() from inside a callback was ignored."));
                return;
            }

            _inUpdate = true;
            long updateStart = Stopwatch.GetTimestamp();
            try
            {
                // Sent packets whose delay has expired go out BEFORE PollEvents, so that they get away in the same
                // frame.
                if (_delayQueue.FlushDue(_reportSafely))
                    _unflushed = true;
                Flush();

                _pollEpoch++;
                _unconnectedEventsInPoll = 0;
                _unknownPeerEventsInPoll = 0;
                _manager.PollEvents();
                DrainEvents();

                if (!_disposed && IsRunning)
                {
                    long tickStart = Stopwatch.GetTimestamp();
                    _clock.Advance(deltaSeconds, _onTick);
                    LastTickDurationMs = (Stopwatch.GetTimestamp() - tickStart) * 1000.0 / Stopwatch.Frequency;
                    Flush();
                }
                else
                {
                    LastTickDurationMs = 0d;
                }
            }
            finally
            {
                LastUpdateDurationMs = (Stopwatch.GetTimestamp() - updateStart) * 1000.0 / Stopwatch.Frequency;
                _inUpdate = false;
            }
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
        /// <see cref="GetWireTypeByte"/>). Ownership of a rented message transfers to the session and it is
        /// reset and recycled immediately (even if <paramref name="peerId"/> is unknown) — never access it
        /// afterwards or pass the same rented instance to multiple <see cref="Send"/> calls (use
        /// <see cref="Broadcast"/>, rent a fresh instance per call, or construct with <c>new</c>).
        /// </summary>
        public void Send(int peerId, INetworkMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            if (!_peers.TryGetValue(peerId, out var peer))
            {
                Report(new NetworkError(NetworkErrorCode.PeerUnknown, $"Peer {peerId} does not exist (connection closed?).", peerId));
                RecycleSent(message);
                return;
            }

            SendInternal(peer, message);
        }

        /// <summary>
        /// Sends a message to everyone except <paramref name="exceptPeerId"/> (-1 = everyone). Ownership of a
        /// rented message transfers to the session and it is reset and recycled immediately once serialization
        /// completes (including when no peers are targeted) — never access it afterwards.
        /// </summary>
        public void Broadcast(INetworkMessage message, int exceptPeerId = -1)
        {
            ArgumentNullException.ThrowIfNull(message);

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
            {
                if (peer.Id != exceptPeerId)
                    targets[n++] = peer;
            }

            if (n == 0)
            {
                _broadcastTargets = targets;
                RecycleSent(message);
                return;
            }

            byte wireType = 0;
            NetDataWriter? writer = null;
            try
            {
                writer = AcquireWriter();
                wireType = GetWireTypeByte(message);
                writer.Put(wireType);
                message.Write(writer);

                byte channel = message.Channel;
                var delivery = message.Delivery;
                var deliveryMethod = NetworkChannels.ToDeliveryMethod(delivery);
                bool simActive = _simulation is { IsActive: true };

                for (int i = 0; i < n; i++)
                {
                    var peer = targets[i];
                    targets[i] = null!;
                    SendWriterToPeer(peer, writer, delivery, deliveryMethod, channel, message.Type, simActive);
                }
            }
            finally
            {
                if (writer is not null)
                    ReleaseWriter(writer);
                Array.Clear(targets, 0, n);
                _broadcastTargets = targets;
                NetworkMessages.Recycle(message, wireType != 0 ? wireType : (message.Type != NetworkMessageType.None ? (byte)message.Type : message.WireType));
            }
        }

        private NetworkPeerState[]? _broadcastTargets;

        /// <summary>Hands a message that was sent (or not delivered at all) back to its pool; it belongs to the pool afterwards.</summary>
        private static void RecycleSent(INetworkMessage message)
        {
            byte wireType = message.Type != NetworkMessageType.None ? (byte)message.Type : message.WireType;
            NetworkMessages.Recycle(message, wireType);
        }

        /// <summary>
        /// Rents a message from the per-type pool. Ownership transfers back to the library as soon as the
        /// instance is passed to <see cref="Send"/> or <see cref="Broadcast"/>; do not read or mutate it
        /// afterwards.
        /// </summary>
        public static T Rent<T>()
            where T : class, INetworkMessage, new()
            => NetworkMessagePool<T>.Rent();

        /// <summary>
        /// Stops the network without marking the session as permanently disposed, so a subclass's <c>Stop()</c>
        /// allows a later <c>Start()</c> or <c>Connect()</c>.
        /// </summary>
        protected void ShutdownSession()
        {
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
            _pendingPayloadEventCount = 0;
            _pendingEventBytes = 0;
            _pendingEvictCursor = 0;
            _unconnectedEventsInPoll = 0;
            _unknownPeerEventsInPoll = 0;
            _writerInUse = false;

            // Queued packets are released: they would otherwise hold references to peers and byte arrays that
            // no longer mean anything.
            _delayQueue.Clear();
            IsRunning = false;
            _unflushed = false;
        }

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

            ShutdownSession();
            _disposed = true;
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

        /// <summary>
        /// Sends one unconnected datagram from this session's own socket to <paramref name="to"/> - the same
        /// socket the peers use, which is the point for NAT punch-through.
        /// </summary>
        public bool SendUnconnected(System.Net.IPEndPoint to, ReadOnlySpan<byte> data)
            => IsRunning && _manager.SendUnconnectedMessage(data, to);

        /// <summary>Starts listening on <paramref name="port"/>.</summary>
        protected bool StartListener(int port)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _manager.Start(port);
        }

        /// <summary>
        /// Starts listening/dialing on a specific IPv4 address (e.g. <c>127.0.0.1</c> for a local server, or a
        /// specific NIC).
        /// </summary>
        protected bool StartListener(string? address, int port)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (address is null)
                return _manager.Start(port);

            var ipv4 = System.Net.IPAddress.Parse(address);
            return _manager.Start(ipv4, System.Net.IPAddress.IPv6Any, port);
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

        /// <summary>Updates the session's tick rate (clamped to 1..240 Hz), e.g. from <see cref="WelcomeMessage.TickRate"/>.</summary>
        internal void SetTickRate(int tickRate)
        {
            Config.TickRate = Math.Clamp(tickRate, 1, 240);
            _clock.SetTickRate(Config.TickRate);
        }

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
            _delayQueue.RemovePeer(peerId);
            return _peers.Remove(peerId, out var peer) ? peer : null;
        }

        /// <summary>Reports an error to the game (the library doesn't log network errors itself).</summary>
        internal void Report(NetworkError error) => Error?.Invoke(error);

        private void ReportSafely(NetworkError error)
        {
            try
            {
                Error?.Invoke(error);
            }
            catch
            {
                // Never allow a faulty Error handler to abort queuing or event draining.
            }
        }

        private void ReportCallbackException(Exception exception, int peerId = -1)
        {
            CallbackExceptionCount++;
            ReportSafely(new NetworkError(
                NetworkErrorCode.CallbackException,
                $"Unhandled exception in session callback: {exception.Message}",
                peerId));
        }

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
        internal void PumpDisconnectNotices()
        {
            // TriggerUpdate wakes LiteNetLib's update thread immediately so queued disconnect packets leave the
            // socket before Stop() closes it.
            _manager.TriggerUpdate();
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

        private NetDataWriter AcquireWriter()
        {
            if (!_writerInUse)
            {
                _writerInUse = true;
                _writer.Reset();
                return _writer;
            }

            return new NetDataWriter(true, 256);
        }

        private void ReleaseWriter(NetDataWriter writer)
        {
            if (ReferenceEquals(writer, _writer))
                _writerInUse = false;
        }

        /// <summary>Writes and sends a message, and hands it back to the pool (it belongs to the session afterwards).</summary>
        private void SendInternal(NetworkPeerState peer, INetworkMessage message, bool recycle = true)
        {
            // The wire byte routes a game message back to its own pool; with 0 it would never be recycled.
            byte wireType = 0;
            NetDataWriter? writer = null;
            try
            {
                writer = AcquireWriter();
                wireType = GetWireTypeByte(message);
                writer.Put(wireType);
                message.Write(writer);

                byte channel = message.Channel;
                var delivery = message.Delivery;
                var deliveryMethod = NetworkChannels.ToDeliveryMethod(delivery);
                bool simActive = _simulation is { IsActive: true };
                SendWriterToPeer(peer, writer, delivery, deliveryMethod, channel, message.Type, simActive);
            }
            finally
            {
                if (writer is not null)
                    ReleaseWriter(writer);
                if (recycle)
                    NetworkMessages.Recycle(message, wireType != 0 ? wireType : (message.Type != NetworkMessageType.None ? (byte)message.Type : message.WireType));
            }
        }

        private void SendWriterToPeer(
            NetworkPeerState peer,
            NetDataWriter writer,
            NetworkDelivery delivery,
            DeliveryMethod deliveryMethod,
            byte channel,
            NetworkMessageType messageType,
            bool simActive)
        {
            try
            {
                // The packets being sent are the delayed ones, on both roles (see ApplySimulation). The bytes
                // are copied first: _writer is reused by the next Send, so that a delayed packet cannot be
                // overwritten by a later one.
                if (simActive)
                {
                    _delayQueue.Enqueue(_simulation!, peer, writer, delivery, channel);
                    return;
                }

                peer.Peer.Send(writer, channel, deliveryMethod);
                _unflushed = true;
            }
            catch (TooBigPacketException)
            {
                // Common cause: someone sends a message on the unreliable channel that doesn't fit in one UDP
                // packet (the MTU). The error must not crash the game - but it must be visible, otherwise the
                // player silently loses motion.
                Report(new NetworkError(NetworkErrorCode.PacketTooLarge,
                    $"Message {messageType} did not fit in a packet - split it up or use a reliable channel.", peer.Id));
            }
        }

        /// <summary>Moves the queue of incoming events so that they can be handled without the list changing during iteration.</summary>
        private void DrainEvents()
        {
            if (_pending.Count == 0)
            {
                LastDrainedEventCount = 0;
                return;
            }

            (_draining, _pending) = (_pending, _draining);
            _pendingPayloadEventCount = 0;
            _pendingEventBytes = 0;
            _pendingEvictCursor = 0;

            int nextIndex = 0;
            int drained = 0;
            int budget = Config.MaxEventsPerDrain;
            try
            {
                while (nextIndex < _draining.Count)
                {
                    if (_disposed || !IsRunning)
                        break;

                    var networkEvent = _draining[nextIndex];
                    if (networkEvent.Kind == NetworkEventKind.Dropped)
                    {
                        nextIndex++;
                        continue;
                    }

                    if (budget > 0 && drained >= budget)
                        break;

                    // Advance before dispatching so HandleMessage/HandleUnconnected exclusively own buffer return
                    // for this entry while finally only cleans up un-dispatched entries.
                    nextIndex++;
                    drained++;
                    DispatchEventSafely(networkEvent);
                }
            }
            finally
            {
                LastDrainedEventCount = drained;

                if (_disposed || !IsRunning)
                {
                    ReleaseBuffersFrom(_draining, nextIndex);
                    _draining.Clear();
                }
                else if (nextIndex < _draining.Count)
                {
                    // MaxEventsPerDrain reached: preserve deferred events in FIFO order ahead of any events
                    // appended to _pending during callbacks.
                    _draining.RemoveRange(0, nextIndex);

                    int deferredPayloadCount = 0;
                    long deferredBytes = 0;
                    for (int i = 0; i < _draining.Count; i++)
                    {
                        var deferred = _draining[i];
                        if (deferred.Kind == NetworkEventKind.Received || deferred.Kind == NetworkEventKind.Unconnected)
                        {
                            deferredPayloadCount++;
                            deferredBytes += deferred.Length;
                        }
                    }

                    _pendingPayloadEventCount += deferredPayloadCount;
                    _pendingEventBytes += deferredBytes;
                    if (_pendingPayloadEventCount > PeakPendingEventCount)
                        PeakPendingEventCount = _pendingPayloadEventCount;
                    if (_pendingEventBytes > PeakPendingEventBytes)
                        PeakPendingEventBytes = _pendingEventBytes;

                    if (_pending.Count > 0)
                    {
                        _draining.AddRange(_pending);
                        _pending.Clear();
                    }

                    (_pending, _draining) = (_draining, _pending);
                    _pendingEvictCursor = 0;
                }
                else
                {
                    _draining.Clear();
                }
            }
        }

        private void DispatchEventSafely(in NetworkEvent networkEvent)
        {
            switch (networkEvent.Kind)
            {
                case NetworkEventKind.PeerConnected:
                {
                    // If the peer is already in the list (the server put it there during the handshake) it
                    // is reused, so that the peer object and the name stay together.
                    var peer = _peers.TryGetValue(networkEvent.Peer!.Id, out var existing) ? existing : AddPeer(networkEvent.Peer!);
                    InvokePeerConnectedIsolated(peer);
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
                    try
                    {
                        OnPeerDisconnectedEvent(peer, networkEvent.Disconnect);
                    }
                    catch (Exception exception)
                    {
                        ReportCallbackException(exception, peer.Id);
                    }

                    if (!_disposed)
                    {
                        try
                        {
                            PeerDisconnected?.Invoke(peer, networkEvent.Disconnect);
                        }
                        catch (Exception exception)
                        {
                            ReportCallbackException(exception, peer.Id);
                        }
                    }
                    break;
                }
                case NetworkEventKind.Received:
                    // Received packets are always handled directly: the simulation delays what a session
                    // SENDS (see ApplySimulation), so a packet that arrives here has already waited out its
                    // delay on the other side.
                    HandleMessage(networkEvent);
                    break;
                case NetworkEventKind.Unconnected:
                    HandleUnconnected(networkEvent);
                    break;
                case NetworkEventKind.SocketError:
                    try
                    {
                        Report(new NetworkError(NetworkErrorCode.Socket, $"Network error: {networkEvent.Error}.", endPoint: networkEvent.EndPoint));
                    }
                    catch (Exception exception)
                    {
                        ReportCallbackException(exception);
                    }
                    break;
                case NetworkEventKind.Dropped:
                    break;
            }
        }

        private void HandleUnconnected(NetworkEvent networkEvent)
        {
            try
            {
                var handler = UnconnectedReceived;
                if (handler is null)
                    return;

                byte[] copy = new byte[networkEvent.Length];
                Buffer.BlockCopy(networkEvent.Data!, 0, copy, 0, networkEvent.Length);
                try
                {
                    handler.Invoke(networkEvent.EndPoint!, copy);
                }
                catch (Exception exception)
                {
                    ReportCallbackException(exception);
                }
            }
            finally
            {
                ReturnPacketBuffer(networkEvent.Data!);
            }
        }

        private void HandleMessage(NetworkEvent networkEvent)
        {
            try
            {
                DecodeAndDeliver(networkEvent);
            }
            finally
            {
                ReturnPacketBuffer(networkEvent.Data!);
            }
        }

        private void InvokePeerConnectedIsolated(NetworkPeerState peer)
        {
            try
            {
                OnPeerConnectedEvent(peer);
            }
            catch (Exception exception)
            {
                ReportCallbackException(exception, peer.Id);
            }

            if (!_disposed && IsRunning)
            {
                try
                {
                    PeerConnected?.Invoke(peer);
                }
                catch (Exception exception)
                {
                    ReportCallbackException(exception, peer.Id);
                }
            }
        }

        private void DecodeAndDeliver(NetworkEvent networkEvent)
        {
            var reader = _reader;
            reader.SetSource(networkEvent.Data!, 0, networkEvent.Length);

            byte typeByte;
            if (!reader.TryGetByte(out typeByte))
            {
                MalformedMessageCount++;
                ReportSafely(new NetworkError(NetworkErrorCode.MalformedMessage, "Empty message received.", networkEvent.Peer!.Id));
                return;
            }

            DecodeMessageBody(networkEvent, reader, typeByte);
        }

        private void DecodeMessageBody(NetworkEvent networkEvent, NetDataReader reader, byte typeByte)
        {

            // Look it up by the RAW byte, not by the enum: a game project has registered its own message types
            // on bytes 16-255 (see NetworkMessages.RegisterGameMessage), and they have no values in
            // NetworkMessageType. That is the only difference from the earlier Create(type) call, and it is the
            // whole point: the library must not need to know a single game type for game traffic to be
            // delivered.
            var message = NetworkMessages.CreateFromWire(typeByte, out _);
            if (message is null)
            {
                // Either a newer-version client or a broken header. It is reported, but the connection is kept:
                // a message this side doesn't understand must not kick a player out of a match in progress.
                UnknownMessageCount++;
                ReportSafely(new NetworkError(NetworkErrorCode.UnknownMessageType, $"Unknown message type {typeByte}.", networkEvent.Peer!.Id));
                return;
            }

            if (IsServer && (message.Type == NetworkMessageType.Welcome
                || message.Type == NetworkMessageType.SystemChat
                || message.Type == NetworkMessageType.PlayerInfo))
            {
                NetworkMessages.Recycle(message, typeByte);
                MalformedMessageCount++;
                ReportSafely(new NetworkError(NetworkErrorCode.MalformedMessage, $"Client sent a server-only message type {typeByte}.", networkEvent.Peer!.Id));
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
                ReportSafely(new NetworkError(NetworkErrorCode.MalformedMessage, $"Could not decode message type {typeByte} - packet too short.", networkEvent.Peer!.Id));
                return;
            }

            if (reader.AvailableBytes != 0)
            {
                NetworkMessages.Recycle(message, typeByte);
                MalformedMessageCount++;
                ReportSafely(new NetworkError(NetworkErrorCode.MalformedMessage, $"Could not decode message type {typeByte} - {reader.AvailableBytes} trailing bytes remained.", networkEvent.Peer!.Id));
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
            catch (Exception exception)
            {
                ReportCallbackException(exception, networkEvent.Peer!.Id);
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
