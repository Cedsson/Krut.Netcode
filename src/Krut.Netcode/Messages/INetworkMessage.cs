using LiteNetLib.Utils;

namespace Krut.Netcode
{
    /// <summary>
    /// A message that is sent over the network. Every message writes itself
    /// with a <see cref="NetDataWriter"/> and reads itself with a
    /// <see cref="NetDataReader"/> - no reflection, no <c>NetSerializer</c>,
    /// no attributes. That is not only the AOT requirement: it also means an
    /// old client that receives a message it doesn't recognise can be handled
    /// (the message is sent in the order it was read), and that the size of a
    /// state packet is something that can be worked out in advance.
    /// <para>
    /// <b>The contract, member by member.</b> There are four of them and each
    /// has exactly one job:
    /// <list type="number">
    /// <item><see cref="Type"/> - which built-in message this is. It is written
    /// as the first byte of the packet, which is what lets the receiver decide
    /// what to decode before reading a single field. It is
    /// <see cref="NetworkMessageType.None"/> for a message a game added.</item>
    /// <item><see cref="Delivery"/> - how the message travels. Chosen per
    /// message, never per session, and it decides the channel too (see
    /// <see cref="NetworkChannels.For"/>).</item>
    /// <item><see cref="WireType"/> - the byte a game-registered message
    /// identifies itself by. built-in messages ignore it.</item>
    /// <item><see cref="Write"/>/<see cref="Read"/> - the fields, in the same
    /// order, in the same class.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>What the library does NOT do.</b> There is no dispatch the game has
    /// to register into, no per-message size table, no serialization context,
    /// and no partial-read recovery: if <see cref="Read"/> throws, the whole
    /// message is dropped. Pooling is <i>per type</i> and nothing more - a
    /// <see cref="NetworkMessagePool{T}"/> is one <c>Stack</c> per concrete
    /// type, so a game that rents a message must release it through the type
    /// it rented it as (see <see cref="NetworkMessages.Recycle"/>), and a
    /// type rented in a loop without being released grows the pool without
    /// bound. The library owns the pool and hands instances out through
    /// <see cref="NetworkSession.Rent{T}"/>; it does not know when a game's
    /// copy is finished.
    /// </para>
    /// <para>
    /// <b>So here is how a GAME adds a message of its own.</b> The library's
    /// <see cref="NetworkMessageType"/> is closed, so a game does not extend the
    /// enum - it registers a type byte of its own via
    /// <see cref="NetworkMessages.RegisterGameMessage{T}"/>:
    /// <list type="number">
    /// <item>Pick a byte in 16-255 (built-in types 1-15 are reserved) and keep it
    /// stable - it is wire format, like a message number.</item>
    /// <item>Write a class that implements this, with a parameterless
    /// constructor so that it can be rented from
    /// <see cref="NetworkMessagePool{T}"/>. Set
    /// <c>Type =&gt; NetworkMessageType.None</c> and <c>WireType =&gt; your
    /// byte</c>.</item>
    /// <item>Register the type once at startup:
    /// <c>NetworkMessages.RegisterGameMessage&lt;MyType&gt;(myByte, NetworkDelivery.Unreliable)</c>.
    /// The delivery is set per message - that is what lets 60 Hz input go
    /// unreliable while the handshake goes reliable ordered.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>The library's built-in messages</b> (Welcome, SystemChat, Chat, PlayerInfo) are
    /// instead switched in <see cref="NetworkMessages.Create"/>. A new built-in message therefore
    /// requires a change in the library source - deliberately, because built-in messages are a
    /// shared contract between all games and not something a game should be able to change.
    /// </para>
    /// </summary>
    public interface INetworkMessage
    {
        /// <summary>Which message type this is - written as the first byte in the packet.</summary>
        NetworkMessageType Type { get; }

        /// <summary>How it travels (reliable/unreliable, ordered).</summary>
        NetworkDelivery Delivery { get; }

        /// <summary>
        /// The byte key in the protocol for a message a GAME has registered,
        /// i.e. when <see cref="Type"/> is
        /// <see cref="NetworkMessageType.None"/>. built-in messages use the
        /// enum value and do not touch this.
        /// The default implementation returns 0, which makes a game message
        /// NOT sendable - the error is thrown clearly in the send path
        /// (see <see cref="NetworkSession.Send"/>). Override it like this:
        /// <code>
        /// public NetworkMessageType Type =&gt; NetworkMessageType.None;
        /// public byte WireType =&gt; ProtocolType.CarState;   // own constant, e.g. 16
        /// </code>
        /// and register the type once with
        /// <c>NetworkMessages.RegisterGameMessage&lt;CarStateMessage&gt;(ProtocolType.CarState, NetworkDelivery.Unreliable)</c>.
        /// Putting the byte in the interface (instead of deriving it from the
        /// type with reflection) is required for Native AOT. The cost is that it
        /// has to be set - but an unset value gives a thrown exception in the
        /// send path, never a message that silently disappears.
        /// </summary>
        byte WireType => 0;

        /// <summary>
        /// Writes the fields' values. The fields' ORDER must be identical to <see cref="Read"/> - which is why
        /// the logic lives in the same class on both sides instead of in two halves. A mismatch is a silent
        /// bug: <see cref="Read"/> does not know the packet ended, it just reads whatever bytes are next, so
        /// swapping two same-sized fields produces a message that decodes cleanly and means something else.
        /// </summary>
        void Write(NetDataWriter writer);

        /// <summary>
        /// Reads back what <see cref="Write"/> wrote. Must not read more than <see cref="Write"/> wrote, and
        /// must not read less either. Reading less leaves bytes in the packet that nobody consumes, and since
        /// one message is one packet here, that shows up as a decode failure on the next field instead, which
        /// is why the failure can look like it belongs to the wrong message.
        /// </summary>
        void Read(NetDataReader reader);
    }
}
