using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace Krut.Netcode
{
    /// <summary>
    /// The server's handshake: sent as soon as the connection is up, before anything else. Why this must be
    /// first, and reliable ordered.
    /// </summary>
    public sealed class WelcomeMessage : INetworkMessage
    {
        /// <inheritdoc/>
        public NetworkMessageType Type => NetworkMessageType.Welcome;

        /// <inheritdoc/>
        public NetworkDelivery Delivery => NetworkDelivery.ReliableOrdered;

        /// <summary>The peer id the server assigned the client (fixed for the lifetime of the connection).</summary>
        public int PeerId { get; set; }

        /// <summary>The session's tick rate - the client switches to it immediately, whatever its own config said.</summary>
        public int TickRate { get; set; }

        /// <summary>The server's tick right now - the client adjusts its clock after it.</summary>
        public int ServerTick { get; set; }

        /// <summary>The sanitized name the client chose (the server has already rejected empty/too long names).</summary>
        public string PlayerName { get; set; } = "Player";

        /// <inheritdoc/>
        public void Write(NetDataWriter writer)
        {
            writer.Put(PeerId);
            writer.Put(TickRate);
            writer.Put(ServerTick);
            NetworkCodec.WriteName(writer, PlayerName);
        }

        /// <inheritdoc/>
        public void Read(NetDataReader reader)
        {
            PeerId = reader.GetInt();
            TickRate = reader.GetInt();
            ServerTick = reader.GetInt();
            PlayerName = NetworkCodec.ReadName(reader);
        }
    }

    /// <summary>A system message from the server: "Anka joined", "You are banned", server news.</summary>
    public sealed class SystemChatMessage : INetworkMessage
    {
        /// <inheritdoc/>
        public NetworkMessageType Type => NetworkMessageType.SystemChat;

        /// <inheritdoc/>
        public NetworkDelivery Delivery => NetworkDelivery.ReliableOrdered;

        /// <summary>The message text.</summary>
        public string Text { get; set; } = string.Empty;

        /// <inheritdoc/>
        public void Write(NetDataWriter writer) => NetworkCodec.WriteText(writer, Text);

        /// <inheritdoc/>
        public void Read(NetDataReader reader) => Text = reader.GetString();
    }

    /// <summary>A chat line from a player.</summary>
    public sealed class ChatMessage : INetworkMessage
    {
        /// <inheritdoc/>
        public NetworkMessageType Type => NetworkMessageType.Chat;

        /// <inheritdoc/>
        public NetworkDelivery Delivery => NetworkDelivery.ReliableOrdered;

        /// <summary>The message text.</summary>
        public string Text { get; set; } = string.Empty;

        /// <inheritdoc/>
        public void Write(NetDataWriter writer) => NetworkCodec.WriteText(writer, Text);

        /// <inheritdoc/>
        public void Read(NetDataReader reader) => Text = reader.GetString();
    }

    /// <summary>
    /// Who is connected: peer id, name and the moment the connection came up. The message carries a single peer
    /// rather than the whole roster, so a lobby's list is built by applying entries in arrival order - which is
    /// only correct if entries arrive in order and none is lost, and that is exactly what reliable ordered
    /// buys. A dropped join means the lobby never shows that player; a dropped leave means it shows them for
    /// the rest of the session. The payload is about 50 bytes and only travels when somebody joins or leaves,
    /// so there was nothing to gain by trading correctness for a smaller packet.
    /// </summary>
    public sealed class PlayerInfoMessage : INetworkMessage
    {
        /// <inheritdoc/>
        public NetworkMessageType Type => NetworkMessageType.PlayerInfo;

        /// <inheritdoc/>
        public NetworkDelivery Delivery => NetworkDelivery.ReliableOrdered;

        /// <summary>The peer id it applies to.</summary>
        public int PeerId { get; set; }

        /// <summary>The player name (sanitized).</summary>
        public string PlayerName { get; set; } = "Player";

        /// <summary>The server's tick when the connection came up.</summary>
        public int JoinTick { get; set; }

        /// <inheritdoc/>
        public void Write(NetDataWriter writer)
        {
            writer.Put(PeerId);
            writer.Put(JoinTick);
            NetworkCodec.WriteName(writer, PlayerName);
        }

        /// <inheritdoc/>
        public void Read(NetDataReader reader)
        {
            PeerId = reader.GetInt();
            JoinTick = reader.GetInt();
            PlayerName = NetworkCodec.ReadName(reader);
        }
    }

    /// <summary>
    /// Creates and hands back message instances.
    ///
    /// <para>
    /// Messages are rented from a small pool per type (no allocations per
    /// packet) and handed back as soon as <see cref="NetworkSession"/> has
    /// processed them. The pools are static, i.e. shared between sessions -
    /// that is deliberate: a client and a server in the same process (a common
    /// test setup) then share the same pool at zero extra cost.
    /// </para>
    ///
    /// <para>
    /// <b>No reflection:</b> <see cref="Create"/> is a switch over
    /// <see cref="NetworkMessageType"/>, so Native AOT can see all types
    /// statically. A new message MUST be added here (and in
    /// <see cref="Recycle"/>) - otherwise it is accepted but not decoded.
    /// </para>
    ///
    /// <para>
    /// <b>How a game registers its own type.</b> The library's
    /// <see cref="NetworkMessageType"/> is closed, so a game does not extend the
    /// enum - it claims a type byte at or above
    /// <see cref="GameTypeBase"/> with
    /// <see cref="RegisterGameMessage{T}"/> and reports it through
    /// <see cref="INetworkMessage.WireType"/>. <see cref="CreateFromWire"/>
    /// then finds the game type by that raw byte, the same way it finds the
    /// built-in types by their enum value. That is the whole extension point:
    /// </para>
    /// <list type="number">
    /// <item>Pick a byte in 16-255 and keep it stable forever - it is wire
    /// format, like a port number, and two different messages sharing a byte is
    /// a decoding bug that nothing detects.</item>
    /// <item>Implement <see cref="INetworkMessage"/> with
    /// <c>Type =&gt; NetworkMessageType.None</c> and
    /// <c>WireType =&gt; your byte</c>.</item>
    /// <item>Call
    /// <c>NetworkMessages.RegisterGameMessage&lt;YourMessage&gt;(yourByte, delivery)</c>
    /// once at startup.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Why the type byte has to be unique.</b> The byte is the only thing
    /// that comes back on receive: <see cref="CreateFromWire"/> looks it up in a
    /// flat array and calls the registered factory. If two types shared it,
    /// whichever registered last would win and the other would be decoded into
    /// the wrong class - with no exception, because both classes read a valid
    /// (but wrong) field sequence. <see cref="RegisterGameMessage{T}"/> refuses
    /// the second registration for that reason; see the comment there.
    /// </para>
    /// </summary>
    public static class NetworkMessages
    {

        /// <summary>
        /// Factory and recycler for message types that are NOT part of the library, registered by a game project
        /// (see <see cref="RegisterGameMessage{T}"/>). Indexed by the raw type byte, not by
        /// <see cref="NetworkMessageType"/> - because the game's own types do not (and must not) live in the
        /// built-in enum. An array of 256 entries rather than a dictionary: the type byte is what gets looked
        /// up when every packet is received, and an array gives a bounds-checked index with no hashing at all.
        /// 256 references is negligible (2 KB) compared to every sent packet having to go through a hash table.
        /// </summary>
        private static readonly Func<INetworkMessage>?[] GameFactories = new Func<INetworkMessage>?[256];

        private static readonly Action<INetworkMessage>?[] GameRecyclers = new Action<INetworkMessage>?[256];

        /// <summary>
        /// Locks a message into the built-in type <see cref="NetworkMessageType.None"/> (byte 0) so that a game
        /// can define its own message types without the library knowing anything about them. Either every game
        /// message gets an built-in value, which requires <see cref="NetworkMessageType"/> and
        /// <see cref="Create"/> to know the field order of every game - i.e. library and game become one shared
        /// repository, which is exactly the split this library exists to avoid. The reserved bytes. A
        /// game gets 16-255 - 240 values, far more than any game needs, and the gap means that a library
        /// version which later adds a new type cannot collide with a game that already took 16.
        /// <see cref="GameTypeBase"/> is the lowest usable byte.
        /// </summary>
        public const byte GameTypeBase = 16;

        /// <summary>
        /// Registers a message the game wants to send and receive, with a type byte at or above
        /// <see cref="GameTypeBase"/>. Reading a registered type goes through <see cref="NetworkSession"/>: the
        /// type byte is taken from the packet's first byte, and the message's own type is identified by the
        /// registered index, not by the enum. The registration is per process and deliberately static, exactly
        /// like the built-in types' pools: a client and a server in the same process (a common test setup) then
        /// share the same message code at zero extra cost.
        /// </summary>
        /// <param name="typeByte">A unique byte in the range 16-255, stable over time - it is wire format.</param>
        public static void RegisterGameMessage<T>(byte typeByte, NetworkDelivery delivery = NetworkDelivery.ReliableOrdered)
            where T : class, INetworkMessage, new()
        {
            if (typeByte < GameTypeBase)
            {
                throw new ArgumentOutOfRangeException(nameof(typeByte), typeByte,
                    $"Bytes below {GameTypeBase} are reserved for the built-in message types.");
            }

            // Two types on one byte would decode into each other without any error: both are valid
            // INetworkMessage implementations and the fields are read positionally.
            if (GameFactories[typeByte] is not null)
            {
                throw new InvalidOperationException(
                    $"Type byte {typeByte} is already registered ({typeof(T).Name}). Two types must not share a byte - " +
                    "a message would be decoded as the wrong type without anything being thrown.");
            }

            // The recycler knows its own T, so it can do the cast; the factory is a closure-free static lambda
            // so that a burst of packets on one byte allocates no delegates.
            GameRecyclers[typeByte] = static message => NetworkMessagePool<T>.Recycle((T)message);
            GameFactories[typeByte] = static () => NetworkMessagePool<T>.Rent();
            GameDeliveries[typeByte] = delivery;
        }

        /// <summary>Delivery for a registered game message, or <c>null</c> if the byte isn't registered.</summary>
        private static readonly NetworkDelivery?[] GameDeliveries = new NetworkDelivery?[256];

        /// <summary>Has a game project registered this byte? Used to validate before sending.</summary>
        public static bool IsRegistered(byte typeByte)
            => typeByte < GameTypeBase ? (byte)typeByte > 0 : GameFactories[typeByte] is not null;

        /// <summary>
        /// Creates (or rents) an empty message of the given type, or null if the type is unknown. The caller
        /// counts it, reports a <see cref="NetworkErrorCode.UnknownMessageType"/> and keeps the connection - a
        /// message this side does not understand must not kick a player out of a match in progress.
        /// </summary>
        public static INetworkMessage? Create(NetworkMessageType type) => type switch
        {
            NetworkMessageType.Welcome => NetworkMessagePool<WelcomeMessage>.Rent(),
            NetworkMessageType.SystemChat => NetworkMessagePool<SystemChatMessage>.Rent(),
            NetworkMessageType.Chat => NetworkMessagePool<ChatMessage>.Rent(),
            NetworkMessageType.PlayerInfo => NetworkMessagePool<PlayerInfoMessage>.Rent(),
            _ => null,
        };

        /// <summary>
        /// Creates a message from the raw type byte in the packet's first byte - the thing that actually
        /// decides what <see cref="NetworkSession"/> decodes. Looking up on the RAW byte rather than on the
        /// enum is the whole point: the game's own types have no values in the built-in enum, but they do have
        /// bytes on the wire, and that byte is all the decoding needs. It also means the type must be
        /// registered on both sides: a client that forgets <see cref="RegisterGameMessage{T}"/> gets
        /// <c>null</c> and counts an unknown message, which looks exactly like a version mismatch in the status
        /// text - see <see cref="NetworkDiagnostics"/>.
        /// </summary>
        /// <param name="typeByte">The byte read out of the packet.</param>
        /// <param name="delivery">The delivery registered for the byte, or null if it is an built-in type.</param>
        public static INetworkMessage? CreateFromWire(byte typeByte, out NetworkDelivery? delivery)
        {
            delivery = null;
            if (typeByte < GameTypeBase)
                return Create((NetworkMessageType)typeByte);

            var factory = GameFactories[typeByte];
            if (factory is null)
                return null;

            delivery = GameDeliveries[typeByte];
            return factory();
        }

        /// <summary>
        /// Hands a message back to the pool so the next send/receive doesn't allocate.
        /// <paramref name="typeByte"/> is needed for game types: built-in types and game types cannot be told
        /// apart by <c>typeof</c> (both are just <see cref="INetworkMessage"/>), so without the byte the
        /// instance would have no path back to the right pool. Requiring the caller to pass the byte is cheaper
        /// than guessing - and guessing is exactly the kind of bug that silently leaks memory over a long run.
        /// <see cref="NetworkMessagePool{T}"/> keeps the same property: the pool never clears a returned
        /// instance's fields, so a message that was not fully written before being sent would read back the
        /// previous borrower's values.
        /// </summary>
        /// <param name="typeByte">The byte the packet carried, or 0 for built-in types.</param>
        public static void Recycle(INetworkMessage? message, byte typeByte = 0)
        {
            if (message is null)
                return;

            // Game type: the recycler knows its own type and can do the cast. No registered recycler means an
            // unregistered byte, so there is no pool to hand it back to - dropping it is all there is to do,
            // and the garbage collector takes it.
            if (typeByte >= GameTypeBase)
            {
                var gameRecycler = GameRecyclers[typeByte];
                if (gameRecycler is not null)
                {
                    gameRecycler(message);
                    return;
                }

                return;
            }

            // Built-in types: the concrete class is known here, so the pool is
            // reachable from the type alone and no byte is needed.
            switch (message)
            {
                case WelcomeMessage welcome:
                    NetworkMessagePool<WelcomeMessage>.Recycle(welcome);
                    break;
                case SystemChatMessage system:
                    NetworkMessagePool<SystemChatMessage>.Recycle(system);
                    break;
                case ChatMessage chat:
                    NetworkMessagePool<ChatMessage>.Recycle(chat);
                    break;
                case PlayerInfoMessage info:
                    NetworkMessagePool<PlayerInfoMessage>.Recycle(info);
                    break;
            }
        }

        /// <summary>
        /// Throws away all of the game's registrations. It exists so that a game project restarting in the same
        /// process (a test, a hot reload) doesn't build on top of old registrations - otherwise a second call
        /// to <see cref="RegisterGameMessage{T}"/> with the same byte would throw, even though it's the same
        /// type. Built-in types are untouched: they are never registered in the first place, so there is nothing
        /// to clear.
        /// </summary>
        public static void ClearGameMessages()
        {
            Array.Clear(GameFactories);
            Array.Clear(GameRecyclers);
            Array.Clear(GameDeliveries);
        }
    }

    /// <summary>
    /// Pool for ONE message type. Only an instance that <see cref="Rent"/> handed out can be recycled: a message
    /// the game created with <c>new</c> (or kept after a send) is never taken over by the pool, so existing call
    /// sites that read a message after sending it, or send one instance to several peers, keep working. A rented
    /// instance is recycled at most once. Thread-safe: the host's server thread and game thread share this pool.
    /// </summary>
    public static class NetworkMessagePool<T>
        where T : class, INetworkMessage, new()
    {
        /// <summary>Most idle instances kept per type.</summary>
        public const int Capacity = 64;

        /// <summary>Most rented-out instances tracked; beyond it the oldest marks are forgotten and those instances become garbage.</summary>
        private const int MaxTracked = 4096;

        private static readonly T?[] Items = new T?[Capacity];
        private static readonly HashSet<T> RentedOut = new(ReferenceEqualityComparer.Instance);
        private static int _count;

        /// <summary>Takes an instance out of the pool, or creates a new one if the pool is empty. The caller owns it until it is sent or recycled.</summary>
        public static T Rent()
        {
            lock (Items)
            {
                T message;
                if (_count > 0)
                {
                    message = Items[--_count]!;
                    Items[_count] = null;
                }
                else
                {
                    message = new T();
                }

                if (RentedOut.Count >= MaxTracked)
                    RentedOut.Clear();
                RentedOut.Add(message);
                return message;
            }
        }

        /// <summary>
        /// Puts a rented instance back (does not clear the fields - they are overwritten next time). Ignored when
        /// the instance was not rented from this pool or was already recycled. The caller must not touch the
        /// instance afterwards.
        /// </summary>
        public static void Recycle(T message)
        {
            lock (Items)
            {
                if (!RentedOut.Remove(message) || _count >= Capacity)
                    return;

                Items[_count++] = message;
            }
        }
    }
}
