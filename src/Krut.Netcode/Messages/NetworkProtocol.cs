namespace Krut.Netcode
{
    /// <summary>
    /// How a message travels over the network. Chosen per message (see <see cref="INetworkMessage.Delivery"/>)
    /// - not per session - because the requirements of the game logic differ: a transform update per tick is
    /// worthless if it arrives late (the next tick sends a fresh one), while a spawn/chat must never be
    /// dropped. Making the whole session unreliable is worse in the other direction: an unreliable spawn means
    /// the client and the server permanently disagree about what exists in the world, and no amount of later
    /// traffic repairs that. The library never recovers it on its own (see
    /// <see cref="NetworkSession.UnknownMessageCount"/> for the one case it deliberately tolerates - an unknown
    /// message, which is ignored and counted).
    /// </summary>
    public enum NetworkDelivery
    {
        /// <summary>
        /// Sent without acknowledgement - losing a packet is fine because the next tick sends a new one.
        /// Reliable here costs head-of- line blocking (see above): the game freezes visibly every time a packet
        /// is lost on a bad connection, which is exactly when the player can least afford it. Unreliable here
        /// costs state: a spawn that is dropped leaves the client without an entity the server believes exists,
        /// and since nothing re-sends a spawn, that car stays invisible for the rest of the session - a
        /// permanent desync, not a stutter.
        /// </summary>
        Unreliable,

        /// <summary>
        /// Delivered in exactly order, with acknowledgement and resend. These are rare (a spawn, a lap, a chat
        /// line), so the head-of-line blocking they can cause is bounded by the number of such messages in
        /// flight, not by the tick rate - and in exchange every one of them arrives, exactly once, in the order
        /// it was sent. Unreliable for the same sequence loses events outright, with no resend and no error:
        /// also a permanent desync, and a quieter one, because nothing is reported.
        /// </summary>
        ReliableOrdered,

        /// <summary>
        /// Delivered with acknowledgement but in any order - for large blobs (e.g. tracks/maps) where an older
        /// piece must not block a newer one. Reliable unordered has no cheap failure mode, which is exactly why
        /// the library'ships it unused: a message split across packets is reassembled by LiteNetLib per message,
        /// so it only helps payloads that are individually complete - a whole track, a whole replay, or
        /// anything the game assembles itself on arrival.
        /// </summary>
        ReliableUnordered,
    }

    /// <summary>
    /// Which message it is. The value is part of the protocol: add new types
    /// last (never in the middle - then an old client would interpret a new
    /// message as something else) - and add them in
    /// <see cref="NetworkMessages.Create"/> so both sides can create it.
    ///
    /// <para>
    /// <b>The numbering is a wire contract.</b> The number goes out as the
    /// first byte of every packet (see <c>NetworkSession.GetWireTypeByte</c>)
    /// and comes back as the only thing that tells the receiver which concrete
    /// type to build. There is no version field, no name negotiation and no
    /// fallback - the byte <i>is</i> the type. So the numbering is append-only:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Appending is safe.</b> A client built before the new value
    /// exists returns <c>null</c> from <see cref="NetworkMessages.Create"/>,
    /// <see cref="NetworkSession.UnknownMessageCount"/> goes up, an
    /// <see cref="NetworkErrorCode.UnknownMessageType"/> error is reported and
    /// the connection is kept. A client on a newer build simply doesn't see
    /// the older messages it was never told about.</item>
    /// <item><b>Inserting in the middle is not.</b> Shift every later value and
    /// every old client now decodes the wrong message as the wrong type - and
    /// silently, because nothing detects it: the packet is well-formed, the
    /// type byte is known, and <c>Read</c> just pulls the wrong fields out of
    /// the right bytes. Chat shows up as a peer join, a spawn reads a
    /// position out of a name. Renaming a member is safe; changing its number
    /// is not.</item>
    /// <item><b>Changing a field's order is not either.</b> The enum value and
    /// <see cref="INetworkMessage.Write"/>/<see cref="INetworkMessage.Read"/>
    /// are the same contract - both peers read the bytes in the order the
    /// message wrote them.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Why the low bytes are reserved.</b> 0 is
    /// <see cref="NetworkMessageType.None"/> and 1-15 are the library's (see
    /// <see cref="NetworkMessages.GameTypeBase"/>). The
    /// gap exists so that a game registering types for itself can never
    /// collide with an built-in type added by a later version of the library -
    /// the game owns 16-255 outright and the library never takes any of it.
    /// That is what keeps the two layers separate; see
    /// <see cref="NetworkMessages.GameTypeBase"/> for the argument in full.
    /// </para>
    /// </summary>
    public enum NetworkMessageType : byte
    {
        /// <summary>No message - 0 is reserved so that a corrupt header never looks like a valid message.</summary>
        None = 0,

        /// <summary>Server -&gt; client. Sent immediately once the connection is up: your peer id, the tick rate, the server's tick and the name you chose.</summary>
        Welcome = 1,

        /// <summary>Server -&gt; client. System message ("X joined", "Y left", banned, server news).</summary>
        SystemChat = 2,

        /// <summary>Both directions. A player's chat line.</summary>
        Chat = 3,

        /// <summary>Server -&gt; everyone. Peer id, name and when the connection came up - so a lobby can show who is in it.</summary>
        PlayerInfo = 4,
    }

    /// <summary>
    /// LiteNetLib's channels - one queue per channel, so that a lost input packet doesn't have to wait behind a
    /// chat line, and so that a large reliable string doesn't delay a lap count.
    /// </summary>
    public static class NetworkChannels
    {
        /// <summary>Unreliable - the client's input per tick, sent redundantly in the packet.</summary>
        public const byte Input = 0;

        /// <summary>Unreliable - the server's snapshots (delta against the latest acked).</summary>
        public const byte Snapshot = 1;

        /// <summary>Reliable, ordered - lap count, lap time, checkpoint, penalty, goal, chat, disconnect.</summary>
        public const byte Events = 2;

        /// <summary>Reliable - handshake, clock sync, time dilation, kick.</summary>
        public const byte Control = 3;

        /// <summary>How many channels are registered on the NetManager (out of 1-32).</summary>
        public const byte Count = 4;

        /// <summary>
        /// Which channel a message with the given delivery belongs on. Routing
        /// <see cref="NetworkDelivery.Unreliable"/> to <see cref="Input"/> instead of <see cref="Snapshot"/>
        /// would be equally correct today - what matters is that unreliable traffic is never on the reliable
        /// queue.
        /// </summary>
        public static byte For(NetworkDelivery delivery) => delivery == NetworkDelivery.Unreliable ? Snapshot : Events;

        /// <summary>
        /// The corresponding <c>DeliveryMethod</c> in LiteNetLib, i.e. how the packet is actually sent.
        /// LiteNetLib requires both to be given on every send (<c>NetPeer.Send(byte[], byte,
        /// DeliveryMethod)</c>), so <see cref="NetworkDelivery"/> alone would not be enough - which is why the
        /// two live next to each other and are always used as a pair in <c>NetworkSession.SendInternal</c>.
        /// </summary>
        public static LiteNetLib.DeliveryMethod ToDeliveryMethod(NetworkDelivery delivery) => delivery switch
        {
            NetworkDelivery.Unreliable => LiteNetLib.DeliveryMethod.Unreliable,
            NetworkDelivery.ReliableUnordered => LiteNetLib.DeliveryMethod.ReliableUnordered,
            _ => LiteNetLib.DeliveryMethod.ReliableOrdered,
        };

        /// <summary>The inverse of <see cref="ToDeliveryMethod"/>: what LiteNetLib actually delivered the packet with.</summary>
        public static NetworkDelivery FromDeliveryMethod(LiteNetLib.DeliveryMethod delivery) => delivery switch
        {
            LiteNetLib.DeliveryMethod.Unreliable => NetworkDelivery.Unreliable,
            LiteNetLib.DeliveryMethod.ReliableUnordered => NetworkDelivery.ReliableUnordered,
            _ => NetworkDelivery.ReliableOrdered,
        };
    }
}
