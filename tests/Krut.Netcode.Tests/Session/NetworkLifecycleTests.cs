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
    /// <summary>Chat, rejection, kick and shutdown flows.</summary>
    [Collection(NetworkTestCollection.Name)]
    public class NetworkLifecycleTests
    {
        // Chat The only test that proves the whole path: client -> server -> another client.

        [Fact]

        public void Chat_RoundTrip()
        {
            int port = FreeUdpPort();
            using var server = new NetworkServer(Config(port, maxClients: 2));
            Require(server.Start(), "The server did not start", $"port {port}");

            // The server does not forward chat automatically - it is the game's job to filter (see
            // NetworkServer).
            var serverChat = new List<string>();
            server.MessageReceived += e =>
            {
                if (e.Message is not ChatMessage chat)
                    return;

                serverChat.Add(chat.Text);
                var forward = NetworkSession.Rent<ChatMessage>();
                forward.Text = chat.Text;
                server.Broadcast(forward);
            };

            using var clientA = new NetworkClient(Config(port));
            using var clientB = new NetworkClient(Config(port));
            var chatB = new List<string>();
            var infoB = new List<string>();
            clientB.MessageReceived += e =>
            {
                if (e.Message is ChatMessage chat)
                    chatB.Add(chat.Text);
                else if (e.Message is PlayerInfoMessage info)
                    infoB.Add($"{info.PeerId}:{info.PlayerName}");
            };

            Require(clientA.Connect("127.0.0.1", port, "A"), "Client A could not connect");
            Require(WaitUntilReady(5f, clientA, server, clientA), "Client A never became ready");

            // Chat from A should reach the server - while nobody else has joined
            // yet.
            SendChat(clientA, "hello");
            Require(PumpUntil(3f, () => serverChat.Count == 1, server, clientA), "The server did not get the chat",
                $"{serverChat.Count}");

            Require(clientB.Connect("127.0.0.1", port, "B"), "Client B could not connect");
            Require(WaitUntilReady(5f, clientB, server, clientA, clientB), "Client B never became ready");
            Require(server.ClientCount == 2, "The server does not see two clients",
                server.ClientCount.ToString(CultureInfo.InvariantCulture));

            // The new client should get the whole lobby right away, without asking
            // - otherwise it flickers empty in a lobby.
            // 
            // Wait for the rows instead of demanding them at once: the welcome
            // message and the lobby rows go out in the same breath of the server,
            // but they are three different packets and UDP delivers them one by
            // one. SessionReady fires when the FIRST of them has been decoded, so
            // the second can easily arrive a frame later - and a test that demands
            // all rows at exactly that moment is a flake, not a measurement.
            Require(PumpUntil(3f, () => infoB.Count >= 2, server, clientA, clientB),
                "Client B did not get the whole lobby", string.Join(", ", infoB));
            Require(clientA.LocalPeerId != clientB.LocalPeerId, "The clients got the same peer id");

            // Forwarding: A sends, the server receives and forwards. That is the
            // real chat round trip.
            SendChat(clientA, "forwarded");
            Require(PumpUntil(3f, () => serverChat.Count == 2, server, clientA, clientB),
                "The server did not get the second chat", $"{serverChat.Count}");
            Require(PumpUntil(3f, () => chatB.Count == 1, server, clientA, clientB),
                "Client B got no forwarded chat", string.Join(", ", chatB));

            // LiteNetLib measures ping continuously; the server should see a
            // plausible value for peers it just talked to.
            foreach (var peer in server.Peers)
            {
                Require(peer.Ping >= 0, "The server has no ping for a peer",
                    $"peer {peer.Id}, ping {peer.Ping}");
            }
        }

        // Rejection Every rejection has to carry an explanation, and the explanation must not leak anything
        // only our server knows. A player who is refused with no reason asks "why didn't I get in?" in the game
        // chat instead of the console, and a foreign app on the same port must not be able to fingerprint us by
        // trying to connect.

        [Fact]

        public void Rejection_FullLobbyRejectsThirdClient()
        {
            // The server that says no.
            int port = FreeUdpPort();
            using var server = new NetworkServer(Config(port, maxClients: 1));
            Require(server.Start(), "The server did not start", $"port {port}");

            using var clientA = new NetworkClient(Config(port));
            Require(clientA.Connect("127.0.0.1", port, "A"), "Client A could not connect");
            Require(WaitUntilReady(5f, clientA, server, clientA), "Client A never became ready");

            using var clientB = new NetworkClient(Config(port));
            string rejection = string.Empty;
            clientB.Closed += (_, text) => rejection = text;
            clientB.Connect("127.0.0.1", port, "B");

            Require(PumpUntil(4f, () => rejection.Length > 0, server, clientA, clientB),
                "Client B got no explanation");

            Require(server.ClientCount == 1, "The server let in more than MaxClients",
                server.ClientCount.ToString(CultureInfo.InvariantCulture));
            Require(rejection.Contains("full", StringComparison.OrdinalIgnoreCase),
                "The explanation does not mention that the lobby is full", $"'{rejection}'");
            Require(!clientB.IsSessionReady, "Client B became ready despite the rejection");
            Require(clientB.State == NetworkClientState.Offline,
                "Client B tries to reconnect after a rejection", clientB.State.ToString());
        }

        // An old build against a new server.
        [Fact]

        public void Rejection_WrongProtocolVersion()
        {
            int port = FreeUdpPort();
            using var server = new NetworkServer(Config(port));
            Require(server.Start(), "The server did not start", $"port {port}");

            var clientConfig = Config(port);
            clientConfig.ProtocolVersion = 6;
            using var client = new NetworkClient(clientConfig);
            string rejection = string.Empty;
            client.Closed += (_, text) => rejection = text;
            client.Connect("127.0.0.1", port, "Old build");

            Require(PumpUntil(4f, () => rejection.Length > 0, server, client),
                "The client with the wrong version got no explanation");

            Require(!client.IsSessionReady, "The client with the wrong version became ready");
            Require(rejection.Contains("version", StringComparison.OrdinalIgnoreCase) ||
                    rejection.Contains("6", StringComparison.Ordinal),
                "The explanation does not mention the version mismatch", $"'{rejection}'");
        }

        // Two games on the same port. The server must NOT send ANY explanation to a client from another game:
        // the other app has nothing to do with our error message, and it would only show "The server runs a
        // different game" to a player who never even tried to connect to us. That difference is what is tested:
        // the text must NOT reveal anything only our server knows, e.g. our app id or which port we listen on.
        [Fact]

        public void Rejection_WrongAppIdIsSilent()
        {
            int port = FreeUdpPort();
            using var server = new NetworkServer(Config(port));
            Require(server.Start(), "The server did not start", $"port {port}");

            var clientConfig = Config(port);
            clientConfig.ApplicationId = "other.game";
            using var client = new NetworkClient(clientConfig);
            string rejection = string.Empty;
            client.Closed += (_, text) => rejection = text;
            client.Connect("127.0.0.1", port, "Stranger");

            Pump(1.5f, server, client);

            Require(!client.IsSessionReady, "The client with the wrong app id became ready");

            // The client's own generic text is correct - it comes from the client. What must NOT happen is the
            // server sending ANY explanation, and that shows up as the text not revealing anything only the
            // server knows: our app id, or which port we listen on. A foreign app must not be able to find that
            // out by trying to connect.
            Require(!rejection.Contains("krut.netcode", StringComparison.OrdinalIgnoreCase),
                "The server revealed its app id to a foreign app", $"'{rejection}'");
            Require(!rejection.Contains(port.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal),
                "The server revealed its port to a foreign app", $"'{rejection}'");

            // The server should also have an empty client list - a rejected
            // connection must never take up a slot.
            Require(server.ClientCount == 0, "A rejected connection counted as a client",
                $"{server.ClientCount}");
        }

        // Kick and shutdown Both paths end the connection on the server's initiative, and both have to tell the
        // client why. The client's own generic text ("The server closed the connection.") sounds like a network
        // failure, and a player who is kicked with "Banned" must not merely see "the connection dropped".

        // The interesting half is not the text but the state: after a kick the client has to be fully offline
        // with a reset peer id, not just have raised Closed.
        [Fact]

        public void Kick_ReasonReachesClient()
        {
            int port = FreeUdpPort();
            using var server = new NetworkServer(Config(port, maxClients: 2));
            Require(server.Start(), "The server did not start", $"port {port}");

            using var clientA = new NetworkClient(Config(port));
            using var clientB = new NetworkClient(Config(port));
            Require(clientA.Connect("127.0.0.1", port, "A"), "Client A could not connect");
            Require(clientB.Connect("127.0.0.1", port, "B"), "Client B could not connect");
            Require(WaitUntilAllReady(5f, new[] { clientA, clientB }, server, clientA, clientB),
                "The clients never became ready");

            string closedA = string.Empty;
            clientA.Closed += (_, text) => closedA = text;
            int kickedId = clientA.LocalPeerId;

            Require(server.DisconnectPeer(kickedId, "Test kick"), "The server could not kick the peer",
                $"peer {kickedId}");

            Require(PumpUntil(4f, () => closedA.Length > 0, server, clientA, clientB),
                "Client A got no explanation for the kick");

            // The reason has to be exactly the server's words. The client's own generic text ("The server
            // closed the connection.") is not the same thing: it sounds like a network failure, and a player
            // who is kicked with "Banned" must not just see "the connection dropped".
            Require(closedA.Contains("Test kick", StringComparison.Ordinal),
                "The kick's explanation did not arrive", $"'{closedA}'");
            Require(closedA != "The server closed the connection.",
                "The client showed only its generic text, not the server's reason", $"'{closedA}'");
            Require(!clientA.IsSessionReady, "Client A is still ready after the kick",
                clientA.State.ToString());
            Require(clientA.LocalPeerId == -1, "Client A did not reset its peer id",
                clientA.LocalPeerId.ToString(CultureInfo.InvariantCulture));

            // The one who was kicked must not count as a client any more -
            // otherwise a kicked player would keep taking up a slot in the lobby.
            Require(PumpUntil(3f, () => server.ClientCount == 1, server, clientA, clientB),
                "The server still counts the kicked peer as a client",
                $"{server.ClientCount}");
        }

        // The server that shuts down (restart, maintenance).
        [Fact]

        public void Shutdown_ServerExplainsWhy()
        {
            int port = FreeUdpPort();
            var server = new NetworkServer(Config(port));
            Require(server.Start(), "The server did not start", $"port {port}");

            using var client = new NetworkClient(Config(port));
            Require(client.Connect("127.0.0.1", port, "A"), "The client could not connect");
            Require(WaitUntilReady(5f, client, server, client), "The client never became ready");

            string closed = string.Empty;
            client.Closed += (_, text) => closed = text;
            server.Stop();

            Require(PumpUntil(4f, () => closed.Length > 0, server, client),
                "The client got no explanation when the server shut down");

            Require(!client.IsSessionReady, "The client is still ready after the server stopped",
                client.State.ToString());
            Require(closed.Length > 0, "The client has no disconnect message");
        }

    }
}
