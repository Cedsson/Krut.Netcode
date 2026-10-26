using System;

namespace Krut.Netcode
{
    /// <summary>A received message together with who sent it.</summary>
    public readonly struct NetworkEnvelope
    {
        public NetworkEnvelope(int peerId, INetworkMessage message, NetworkDelivery delivery, double time)
        {
            PeerId = peerId;
            Message = message;
            Delivery = delivery;
            Time = time;
        }

        /// <summary>Peer id of the sender - the server's peer id for whoever received a message on the client.</summary>
        public int PeerId { get; }

        /// <summary>The message (valid only during the event it was delivered in).</summary>
        public INetworkMessage Message { get; }

        /// <summary>
        /// How the message was actually delivered - reliable, ordered or not. Read from the delivery method
        /// LiteNetLib handed over with the packet, i.e. from the wire rather than from
        /// <see cref="INetworkMessage.Delivery"/>: on a well-behaved peer the two always agree, but the
        /// envelope is built from what happened, so a handler can assert on the delivery it really got.
        /// </summary>
        public NetworkDelivery Delivery { get; }

        /// <summary>The session's time (seconds) when the message was received - used to timestamp chat events.</summary>
        public double Time { get; }

        /// <summary>The message as the specific type, or false if it was a different kind of message.</summary>
        public bool Is<T>(out T message) where T : INetworkMessage
        {
            message = Message is T typed ? typed : default!;
            return Message is T;
        }
    }
}
