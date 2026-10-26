using System.Collections.Generic;
using Krut.Netcode;
using Xunit;

using static Krut.Netcode.Tests.NetworkTestSupport;

namespace Krut.Netcode.Tests
{
    [Collection(NetworkTestCollection.Name)]
    public class NetworkReconnectLimitTests
    {
        private static NetworkConfig ReconnectingConfig(int port)
        {
            var config = Config(port);
            config.AutoReconnect = true;
            config.MaxConnectAttempts = 2;
            config.ReconnectDelayMs = 100;
            config.DisconnectTimeoutMs = 1000;
            config.ConnectRequestIntervalMs = 50;
            config.ConnectRequestAttempts = 2;
            return config;
        }

        [Fact]
        public void NoServer_GivesUpAfterMaxConnectAttempts_AndStaysOffline()
        {
            int port = FreeUdpPort();
            using var client = new NetworkClient(ReconnectingConfig(port));
            var errors = new List<NetworkError>();
            client.Error += e => errors.Add(e);

            Require(client.Connect("127.0.0.1", port, "Tester"), "the connection request could not be sent");

            Require(PumpUntil(20f, () => client.State == NetworkClientState.Offline, client),
                "the client never gave up", $"state {client.State}, attempts {client.ReconnectAttempts}");
            Require(client.ReconnectAttempts == 2, "the retry limit was not enforced",
                $"attempts {client.ReconnectAttempts}");
            Require(errors.Exists(e => e.Code == NetworkErrorCode.NotConnected), "no NotConnected error was reported",
                string.Join("; ", errors));

            // Nothing may bring it back: no further retry, and no Reconnecting state.
            for (int i = 0; i < 30; i++)
            {
                Pump(0.1f, client);
                Require(client.State == NetworkClientState.Offline, "the client left Offline after giving up",
                    $"state {client.State}");
            }
            Require(client.ReconnectAttempts == 2, "the counter moved after giving up", $"attempts {client.ReconnectAttempts}");
        }

        [Fact]
        public void ReconnectCounter_StartsAtZeroAgain_AfterASessionWasLost()
        {
            int serverPort = FreeUdpPort();
            using var server = new NetworkServer(Config(serverPort));
            Require(server.Start(), "The server did not start", $"port {serverPort}");
            using var client = new NetworkClient(ReconnectingConfig(FreeUdpPort()));

            Require(client.Connect("127.0.0.1", serverPort, "Tester"), "The client could not connect");
            Require(WaitUntilReady(5f, client, server, client), "The client never became ready");

            // A ready session that is lost starts a new outage: the counter must start from zero.
            server.Stop();

            Require(PumpUntil(10f, () => client.State == NetworkClientState.Reconnecting, client),
                "the client did not start reconnecting after the server stopped", $"state {client.State}");
            Require(client.ReconnectAttempts == 0, "the counter did not start at zero after a lost session",
                $"attempts {client.ReconnectAttempts}");
        }
    }
}
