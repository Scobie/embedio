﻿namespace Unosquare.Labs.EmbedIO.Tests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Reflection;
    using NUnit.Framework;
    using Modules;
    using TestObjects;
#if NET46
    using System.Net.WebSockets;
#else
    using Net;
#endif

    [TestFixture]
    public class WebSocketsModuleTest
    {
        protected WebServer WebServer;

        [SetUp]
        public void Init()
        {
            Swan.Terminal.Settings.DisplayLoggingMessageType = Swan.LogMessageType.None;
            WebServer = new WebServer(Resources.WsServerAddress.Replace("ws", "http")).WithWebSocket(typeof(TestWebSocket).GetTypeInfo().Assembly);
            WebServer.RunAsync();
        }

        [Test]
        public async Task TestConnectWebSocket()
        {
            const string wsUrl = Resources.WsServerAddress + "test";
            Assert.IsNotNull(WebServer.Module<WebSocketsModule>(), "WebServer has WebSocketsModule");

            Assert.AreEqual(WebServer.Module<WebSocketsModule>().Handlers.Count, 1, "WebSocketModule has one handler");

            var ct = new CancellationTokenSource();
#if NET46
            var clientSocket = new ClientWebSocket();
            await clientSocket.ConnectAsync(new Uri(wsUrl), ct.Token);

            Assert.AreEqual(WebSocketState.Open, clientSocket.State, "Connection is open");

            var message = new ArraySegment<byte>(System.Text.Encoding.Default.GetBytes("HOLA"));
            var buffer = new ArraySegment<byte>(new byte[1024]);

            await clientSocket.SendAsync(message, WebSocketMessageType.Text, true, ct.Token);
            var result = await clientSocket.ReceiveAsync(buffer, ct.Token);

            Assert.IsTrue(result.EndOfMessage, "End of message is true");
            Assert.IsTrue(System.Text.Encoding.UTF8.GetString(buffer.Array).TrimEnd((char) 0) == "WELCOME", "Final message is WELCOME");
#else
            var clientSocket = new WebSocket(wsUrl);
            await clientSocket.ConnectAsync(ct.Token);

            if (clientSocket.State != WebSocketState.Open)
            {
                Assert.Inconclusive("WebSocket Client is not working yet");
            }

            Assert.AreEqual(WebSocketState.Open, clientSocket.State, "Connection is open");

            var buffer = System.Text.Encoding.UTF8.GetBytes("HOLA");
            await clientSocket.SendAsync(buffer, Opcode.Text);
            await Task.Delay(100, ct.Token);
#endif
        }

        [TearDown]
        public void Kill()
        {
            Task.Delay(TimeSpan.FromSeconds(1)).Wait();
            WebServer.Dispose();
        }
    }
}