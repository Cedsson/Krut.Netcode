using Xunit;

namespace Krut.Netcode.Tests
{
    public class NetworkEnvelopeTests
    {
        [Fact]
        public void Is_ReturnsFalseInsteadOfThrowing_ForAnotherMessageType()
        {
            var envelope = new NetworkEnvelope(1, new ChatMessage(), NetworkDelivery.ReliableOrdered, 0);

            Assert.False(envelope.Is(out WelcomeMessage welcome));
            Assert.Null(welcome);
            Assert.True(envelope.Is(out ChatMessage chat));
            Assert.Same(envelope.Message, chat);
        }
    }
}
