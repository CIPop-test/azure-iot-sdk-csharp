// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Azure.Devices.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Azure.Devices.E2ETests
{
    [TestClass]
    [TestCategory("IoTHub-E2E")]
    public partial class MessageSendTests : IDisposable
    {
        private const string DevicePrefix = "E2E_Message_";
        private const string ModulePrefix = "E2E_Module_";
        private const string DevicePrefixTimeout = "E2E_Message_Timeout_";
        private static string ProxyServerAddress = Configuration.IoTHub.ProxyServerAddress;
        private static TestLogging _log = TestLogging.GetInstance();

        private readonly ConsoleEventListener _listener;

        public MessageSendTests()
        {
            _listener = new ConsoleEventListener("Microsoft-Azure-");
        }

        [TestMethod]
        public async Task Message_DeviceSendSingleMessage_Amqp()
        {
            await SendSingleMessage(TestAuthenticationType.Sasl, Client.TransportType.Amqp_Tcp_Only).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task Message_DeviceSendSingleMessage_AmqpWs()
        {
            await SendSingleMessage(TestAuthenticationType.Sasl, Client.TransportType.Amqp_WebSocket_Only).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task Message_DeviceSendSingleMessage_Mqtt()
        {
            await SendSingleMessage(TestAuthenticationType.Sasl, Client.TransportType.Mqtt_Tcp_Only).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task Message_DeviceSendSingleMessage_MqttWs()
        {
            await SendSingleMessage(TestAuthenticationType.Sasl, Client.TransportType.Mqtt_WebSocket_Only).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task Message_DeviceSendSingleMessage_Http()
        {
            await SendSingleMessage(TestAuthenticationType.Sasl, Client.TransportType.Http1).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("ProxyE2ETests")]
        public async Task Message_DeviceSendSingleMessage_Http_WithProxy()
        {
            Client.Http1TransportSettings httpTransportSettings = new Client.Http1TransportSettings();
            httpTransportSettings.Proxy = new WebProxy(ProxyServerAddress);
            ITransportSettings[] transportSettings = new ITransportSettings[] { httpTransportSettings };

            await SendSingleMessage(TestAuthenticationType.Sasl, transportSettings).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("ProxyE2ETests")]
        public async Task Message_DeviceSendSingleMessage_Http_WithCustomeProxy()
        {
            Http1TransportSettings httpTransportSettings = new Http1TransportSettings();
            CustomWebProxy proxy = new CustomWebProxy();
            httpTransportSettings.Proxy = proxy;
            ITransportSettings[] transportSettings = new ITransportSettings[] { httpTransportSettings };

            await SendSingleMessage(TestAuthenticationType.Sasl, transportSettings).ConfigureAwait(false);
            Assert.AreNotEqual(proxy.Counter, 0);
        }

        [TestMethod]
        [TestCategory("ProxyE2ETests")]
        public async Task Message_DeviceSendSingleMessage_AmqpWs_WithProxy()
        {
            Client.AmqpTransportSettings amqpTransportSettings = new Client.AmqpTransportSettings(Client.TransportType.Amqp_WebSocket_Only);
            amqpTransportSettings.Proxy = new WebProxy(ProxyServerAddress);
            ITransportSettings[] transportSettings = new ITransportSettings[] { amqpTransportSettings };

            await SendSingleMessage(TestAuthenticationType.Sasl, transportSettings).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("ProxyE2ETests")]
        public async Task Message_DeviceSendSingleMessage_MqttWs_WithProxy()
        {
            Client.Transport.Mqtt.MqttTransportSettings mqttTransportSettings = 
                new Client.Transport.Mqtt.MqttTransportSettings(Client.TransportType.Mqtt_WebSocket_Only);
            mqttTransportSettings.Proxy = new WebProxy(ProxyServerAddress);
            ITransportSettings[] transportSettings = new ITransportSettings[] { mqttTransportSettings };

            await SendSingleMessage(TestAuthenticationType.Sasl, transportSettings).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("ProxyE2ETests")]
        public async Task Message_ModuleSendSingleMessage_AmqpWs_WithProxy()
        {
            Client.AmqpTransportSettings amqpTransportSettings = new Client.AmqpTransportSettings(Client.TransportType.Amqp_WebSocket_Only);
            amqpTransportSettings.Proxy = new WebProxy(ProxyServerAddress);
            ITransportSettings[] transportSettings = new ITransportSettings[] { amqpTransportSettings };

            await SendSingleMessageModule(TestAuthenticationType.Sasl, transportSettings).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("ProxyE2ETests")]
        public async Task Message_ModuleSendSingleMessage_MqttWs_WithProxy()
        {
            Client.Transport.Mqtt.MqttTransportSettings mqttTransportSettings = 
                new Client.Transport.Mqtt.MqttTransportSettings(Client.TransportType.Mqtt_WebSocket_Only);
            mqttTransportSettings.Proxy = new WebProxy(ProxyServerAddress);
            ITransportSettings[] transportSettings = new ITransportSettings[] { mqttTransportSettings };

            await SendSingleMessageModule(TestAuthenticationType.Sasl, transportSettings).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        public async Task Message_TcpConnectionLossSendRecovery_Amqp()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_Tcp_Only,
                FaultInjection.FaultType_Tcp,
                FaultInjection.FaultCloseReason_Boom,
                FaultInjection.DefaultDelayInSec).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        public async Task Message_TcpConnectionLossSendRecovery_AmqpWs()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_WebSocket_Only,
                FaultInjection.FaultType_Tcp,
                FaultInjection.FaultCloseReason_Boom,
                FaultInjection.DefaultDelayInSec).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        public async Task Message_TcpConnectionLossSendRecovery_Mqtt()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Mqtt_Tcp_Only,
                FaultInjection.FaultType_Tcp,
                FaultInjection.FaultCloseReason_Boom,
                FaultInjection.DefaultDelayInSec).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        public async Task Message_TcpConnectionLossSendRecovery_MqttWs()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Mqtt_WebSocket_Only,
                FaultInjection.FaultType_Tcp,
                FaultInjection.FaultCloseReason_Boom,
                FaultInjection.DefaultDelayInSec).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        public async Task Message_AmqpConnectionLossSendRecovery_Amqp()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_Tcp_Only,
                FaultInjection.FaultType_AmqpConn,
                "",
                FaultInjection.DefaultDelayInSec).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        public async Task Message_AmqpConnectionLossSendRecovery_AmqpWs()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_WebSocket_Only,
                FaultInjection.FaultType_AmqpConn,
                "",
                FaultInjection.DefaultDelayInSec).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        public async Task Message_AmqpSessionLossSendRecovery_Amqp()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_Tcp_Only,
                FaultInjection.FaultType_AmqpSess,
                "",
                FaultInjection.DefaultDelayInSec).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        public async Task Message_AmqpSessionLossSendRecovery_AmqpWs()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_WebSocket_Only,
                FaultInjection.FaultType_AmqpSess,
                "",
                FaultInjection.DefaultDelayInSec).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        public async Task Message_AmqpD2CLinkDropSendRecovery_Amqp()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_Tcp_Only,
                FaultInjection.FaultType_AmqpD2C,
                FaultInjection.FaultCloseReason_Boom,
                FaultInjection.DefaultDelayInSec).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        public async Task Message_AmqpD2CLinkDropSendRecovery_AmqpWs()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_WebSocket_Only,
                FaultInjection.FaultType_AmqpD2C,
                FaultInjection.FaultCloseReason_Boom,
                FaultInjection.DefaultDelayInSec).ConfigureAwait(false);
        }

        [Ignore] // TODO #588 - Fails with InternalServerError intermittently.
        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        public async Task Message_ThrottledConnectionRecovery_Amqp()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_Tcp_Only,
                FaultInjection.FaultType_Throttle,
                FaultInjection.FaultCloseReason_Boom,
                FaultInjection.DefaultDelayInSec,
                FaultInjection.DefaultDurationInSec).ConfigureAwait(false);
        }

        [Ignore] // TODO #588 - Fails with InternalServerError intermittently.
        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        public async Task Message_ThrottledConnectionRecovery_AmqpWs()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_WebSocket_Only,
                FaultInjection.FaultType_Throttle,
                FaultInjection.FaultCloseReason_Boom,
                FaultInjection.DefaultDelayInSec,
                FaultInjection.DefaultDurationInSec).ConfigureAwait(false);
        }

        [Ignore] // TODO #588 - Fails with InternalServerError intermittently.
        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        [ExpectedException(typeof(System.TimeoutException))]
        public async Task Message_ThrottledConnectionLongTimeNoRecovery_Amqp()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_Tcp_Only,
                FaultInjection.FaultType_Throttle,
                FaultInjection.FaultCloseReason_Boom,
                FaultInjection.DefaultDelayInSec,
                FaultInjection.DefaultDurationInSec,
                FaultInjection.ShortRetryInMilliSec).ConfigureAwait(false);
        }

        [Ignore] // TODO #588 - Fails with InternalServerError intermittently.
        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        [ExpectedException(typeof(System.TimeoutException))]
        public async Task Message_ThrottledConnectionLongTimeNoRecovery_AmqpWs()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_WebSocket_Only,
                FaultInjection.FaultType_Throttle,
                FaultInjection.FaultCloseReason_Boom,
                FaultInjection.DefaultDelayInSec,
                FaultInjection.DefaultDurationInSec,
                FaultInjection.ShortRetryInMilliSec).ConfigureAwait(false);
        }

        [Ignore] // TODO #588 - Fails with InternalServerError intermittently.
        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        [ExpectedException(typeof(System.TimeoutException))]
        public async Task Message_ThrottledConnectionLongTimeNoRecovery_Http()
        {
            await SendMessageThrottledForHttp().ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        [ExpectedException(typeof(Client.Exceptions.DeviceMaximumQueueDepthExceededException))]
        public async Task Message_QuotaExceededRecovery_Amqp()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_Tcp_Only,
                FaultInjection.FaultType_QuotaExceeded,
                FaultInjection.FaultCloseReason_Boom,
                FaultInjection.DefaultDelayInSec,
                FaultInjection.DefaultDurationInSec).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        [ExpectedException(typeof(Client.Exceptions.DeviceMaximumQueueDepthExceededException))]
        public async Task Message_QuotaExceededRecovery_AmqpWs()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_WebSocket_Only,
                FaultInjection.FaultType_QuotaExceeded,
                FaultInjection.FaultCloseReason_Boom,
                FaultInjection.DefaultDelayInSec,
                FaultInjection.DefaultDurationInSec).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        [ExpectedException(typeof(Client.Exceptions.QuotaExceededException))]
        public async Task Message_QuotaExceededRecovery_Http()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Http1,
                FaultInjection.FaultType_QuotaExceeded,
                FaultInjection.FaultCloseReason_Boom,
                FaultInjection.DefaultDelayInSec,
                FaultInjection.DefaultDurationInSec).ConfigureAwait(false);
        }

        [Ignore] // 593 - Intermittently failing - no exception thrown.
        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        [ExpectedException(typeof(Client.Exceptions.UnauthorizedException))]
        public async Task Message_AuthenticationRecovery_Amqp()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_Tcp_Only,
                FaultInjection.FaultType_Auth,
                FaultInjection.FaultCloseReason_Boom,
                FaultInjection.DefaultDelayInSec,
                FaultInjection.DefaultDurationInSec).ConfigureAwait(false);
        }

        [Ignore] // 593 - Intermittently failing - no exception thrown.
        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        [ExpectedException(typeof(Client.Exceptions.UnauthorizedException))]
        public async Task Message_AuthenticationRecovery_AmqpWs()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_WebSocket_Only,
                FaultInjection.FaultType_Auth,
                FaultInjection.FaultCloseReason_Boom,
                FaultInjection.DefaultDelayInSec,
                FaultInjection.DefaultDurationInSec).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        [ExpectedException(typeof(Client.Exceptions.UnauthorizedException))]
        public async Task Message_AuthenticationRecovery_Http()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Http1,
                FaultInjection.FaultType_Auth,
                FaultInjection.FaultCloseReason_Boom,
                FaultInjection.DefaultDelayInSec,
                FaultInjection.DefaultDurationInSec).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        public async Task Message_SendGracefulShutdownSendRecovery_Amqp()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_Tcp_Only,
                FaultInjection.FaultType_GracefulShutdownAmqp,
                FaultInjection.FaultCloseReason_Bye,
                FaultInjection.DefaultDelayInSec).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        public async Task Message_GracefulShutdownSendRecovery_AmqpWs()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Amqp_WebSocket_Only,
                FaultInjection.FaultType_GracefulShutdownAmqp,
                FaultInjection.FaultCloseReason_Bye,
                FaultInjection.DefaultDelayInSec).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        public async Task Message_GracefulShutdownSendRecovery_Mqtt()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Mqtt_Tcp_Only,
                FaultInjection.FaultType_GracefulShutdownMqtt,
                FaultInjection.FaultCloseReason_Bye,
                FaultInjection.DefaultDelayInSec).ConfigureAwait(false);
        }

        [TestMethod]
        [TestCategory("IoTHub-FaultInjection")]
        public async Task Message_GracefulShutdownSendRecovery_MqttWs()
        {
            await SendMessageRecovery(
                TestAuthenticationType.Sasl,
                Client.TransportType.Mqtt_WebSocket_Only,
                FaultInjection.FaultType_GracefulShutdownMqtt,
                FaultInjection.FaultCloseReason_Bye,
                FaultInjection.DefaultDelayInSec).ConfigureAwait(false);
        }

        [TestMethod]
        [ExpectedException(typeof(TimeoutException))]
        public async Task Message_TimeOutReachedResponse()
        {
            await FastTimeout().ConfigureAwait(false);
        }

        [TestMethod]
        public async Task Message_NoTimeoutPassed()
        {
            await DefaultTimeout().ConfigureAwait(false);
        }

        [TestMethod]
        public async Task X509_DeviceSendSingleMessage_Amqp()
        {
            await SendSingleMessage(TestAuthenticationType.X509, Client.TransportType.Amqp_Tcp_Only).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task X509_DeviceSendSingleMessage_AmqpWs()
        {
            await SendSingleMessage(TestAuthenticationType.X509, Client.TransportType.Amqp_WebSocket_Only).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task X509_DeviceSendSingleMessage_Mqtt()
        {
            await SendSingleMessage(TestAuthenticationType.X509, Client.TransportType.Mqtt_Tcp_Only).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task X509_DeviceSendSingleMessage_MqttWs()
        {
            await SendSingleMessage(TestAuthenticationType.X509, Client.TransportType.Mqtt_WebSocket_Only).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task X509_DeviceSendSingleMessage_Http()
        {
            await SendSingleMessage(TestAuthenticationType.X509, Client.TransportType.Http1).ConfigureAwait(false);
        }

        private async Task DefaultTimeout()
        {
            TestDevice testDevice = await TestDevice.GetTestDeviceAsync(DevicePrefixTimeout).ConfigureAwait(false);
            ServiceClient sender = ServiceClient.CreateFromConnectionString(Configuration.IoTHub.ConnectionString);

            var deviceClient = DeviceClient.CreateFromConnectionString(testDevice.ConnectionString, Client.TransportType.Amqp);
            await sender.SendAsync(testDevice.Id, new Message(Encoding.ASCII.GetBytes("Dummy Message")), null).ConfigureAwait(false);
        }

        private async Task FastTimeout()
        {
            TestDevice testDevice = await TestDevice.GetTestDeviceAsync(DevicePrefixTimeout).ConfigureAwait(false);
            ServiceClient sender = ServiceClient.CreateFromConnectionString(Configuration.IoTHub.ConnectionString);

            var deviceClient = DeviceClient.CreateFromConnectionString(testDevice.ConnectionString, Client.TransportType.Amqp);
            await sender.SendAsync(testDevice.Id, new Message(Encoding.ASCII.GetBytes("Dummy Message")), TimeSpan.FromTicks(1)).ConfigureAwait(false);
        }

        private Client.Message ComposeD2CTestMessage(out string payload, out string p1Value)
        {
            payload = Guid.NewGuid().ToString();
            p1Value = Guid.NewGuid().ToString();

            _log.WriteLine($"{nameof(ComposeD2CTestMessage)}: payload='{payload}' p1Value='{p1Value}'");

            return new Client.Message(Encoding.UTF8.GetBytes(payload))
            {
                Properties = { ["property1"] = p1Value }
            };
        }

        private Message ComposeC2DTestMessage(out string payload, out string messageId, out string p1Value)
        {
            payload = Guid.NewGuid().ToString();
            messageId = Guid.NewGuid().ToString();
            p1Value = Guid.NewGuid().ToString();

            _log.WriteLine($"{nameof(ComposeC2DTestMessage)}: payload='{payload}' messageId='{messageId}' p1Value='{p1Value}'");

            return new Message(Encoding.UTF8.GetBytes(payload))
            {
                MessageId = messageId,
                Properties = { ["property1"] = p1Value }
            };
        }

        private async Task VerifyReceivedC2DMessage(Client.TransportType transport, DeviceClient dc, string payload, string p1Value)
        {
            var wait = true;

            Stopwatch sw = new Stopwatch();
            sw.Start();
            while (wait)
            {
                Client.Message receivedMessage;

                if (transport == Client.TransportType.Http1)
                {
                    // Long-polling is not supported in http
                    receivedMessage = await dc.ReceiveAsync().ConfigureAwait(false);
                }
                else
                {
                    receivedMessage = await dc.ReceiveAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }

                if (receivedMessage != null)
                {
                    string messageData = Encoding.ASCII.GetString(receivedMessage.GetBytes());
                    Assert.AreEqual(messageData, payload);

                    Assert.AreEqual(receivedMessage.Properties.Count, 1);
                    var prop = receivedMessage.Properties.Single();
                    Assert.AreEqual(prop.Key, "property1");
                    Assert.AreEqual(prop.Value, p1Value);

                    await dc.CompleteAsync(receivedMessage).ConfigureAwait(false);
                    wait = false;
                }

                if (sw.Elapsed.TotalSeconds > 5)
                {
                    throw new TimeoutException("Test is running longer than expected.");
                }
            }

            sw.Stop();
        }

        private async Task SendSingleMessage(TestAuthenticationType type, Client.TransportType transport)
        {
            TestDevice testDevice = await TestDevice.GetTestDeviceAsync(DevicePrefix, type).ConfigureAwait(false);
            DeviceClient deviceClient = testDevice.CreateDeviceClient(transport);

            await SendSingleMessage(deviceClient, testDevice.Id).ConfigureAwait(false);
        }

        private async Task SendSingleMessage(TestAuthenticationType type, ITransportSettings[] transportSettings)
        {
            TestDevice testDevice = await TestDevice.GetTestDeviceAsync(DevicePrefix).ConfigureAwait(false);
            DeviceClient deviceClient = DeviceClient.CreateFromConnectionString(testDevice.ConnectionString, transportSettings);

            await SendSingleMessage(deviceClient, testDevice.Id).ConfigureAwait(false);
        }
        
        private async Task SendSingleMessage(DeviceClient deviceClient, string deviceId)
        {
            EventHubTestListener eventTestListener = await EventHubTestListener.CreateListener(deviceId).ConfigureAwait(false);

            try
            {
                await deviceClient.OpenAsync().ConfigureAwait(false);

                string payload;
                string p1Value;
                Client.Message testMessage = ComposeD2CTestMessage(out payload, out p1Value);
                await deviceClient.SendEventAsync(testMessage).ConfigureAwait(false);

                bool isReceived = await eventTestListener.WaitForMessage(deviceId, payload, p1Value).ConfigureAwait(false);
                Assert.IsTrue(isReceived, "Message is not received.");
            }
            finally
            {
                await deviceClient.CloseAsync().ConfigureAwait(false);
                await eventTestListener.CloseAsync().ConfigureAwait(false);
            }
        }

        private async Task SendSingleMessageModule(TestAuthenticationType type, ITransportSettings[] transportSettings)
        {
            TestModule testModule = await TestModule.GetTestModuleAsync(DevicePrefix, ModulePrefix).ConfigureAwait(false);
            var moduleClient = ModuleClient.CreateFromConnectionString(testModule.ConnectionString, transportSettings);

            try
            {
                await moduleClient.OpenAsync().ConfigureAwait(false);

                string payload;
                string p1Value;
                Client.Message testMessage = ComposeD2CTestMessage(out payload, out p1Value);
                await moduleClient.SendEventAsync(testMessage).ConfigureAwait(false);
            }
            finally
            {
                await moduleClient.CloseAsync().ConfigureAwait(false);
            }
        }

        internal async Task SendMessageThrottledForHttp()
        {
            TestDevice testDevice = await TestDevice.GetTestDeviceAsync(DevicePrefix).ConfigureAwait(false);

            PartitionReceiver eventHubReceiver = await CreateEventHubReceiver(testDevice.Id).ConfigureAwait(false);

            var deviceClient = DeviceClient.CreateFromConnectionString(testDevice.ConnectionString, Client.TransportType.Http1);

            try
            {
                deviceClient.OperationTimeoutInMilliseconds = (uint)FaultInjection.ShortRetryInMilliSec;
                await deviceClient.OpenAsync().ConfigureAwait(false);

                string payload, p1Value;
                Client.Message testMessage = ComposeD2CTestMessage(out payload, out p1Value);
                await deviceClient.SendEventAsync(testMessage).ConfigureAwait(false);

                bool isReceived = false;
                Stopwatch sw = new Stopwatch();
                sw.Start();
                while (!isReceived && sw.Elapsed.Minutes < 1)
                {
                    var events = await eventHubReceiver.ReceiveAsync(int.MaxValue, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    isReceived = VerifyTestMessage(events, testDevice.Id, payload, p1Value);
                }
                sw.Stop();

                // Implementation of error injection of throttling on http is that it will throttle the
                // fault injection message itself only.  The duration of fault has no effect on http throttle.
                // Client is supposed to retry sending the throttling fault message until operation timeout.
                await deviceClient.SendEventAsync(FaultInjection.ComposeErrorInjectionProperties(FaultInjection.FaultType_Throttle,
                    FaultInjection.FaultCloseReason_Boom, FaultInjection.DefaultDelayInSec, FaultInjection.DefaultDurationInSec)).ConfigureAwait(false);
            }
            finally
            {
                await deviceClient.CloseAsync().ConfigureAwait(false);
                await eventHubReceiver.CloseAsync().ConfigureAwait(false);
            }
        }

        internal async Task SendSingleMessage(ITransportSettings[] transportSettings)
        {

        }

        internal async Task SendMessageRecovery(
            TestAuthenticationType type,
            Client.TransportType transport,
            string faultType, string reason, int delayInSec, int durationInSec = 0, int retryDurationInMilliSec = FaultInjection.RecoveryTimeMilliseconds)
        {
            TestDevice testDevice = await TestDevice.GetTestDeviceAsync(DevicePrefix, type).ConfigureAwait(false);

            var deviceClient = DeviceClient.CreateFromConnectionString(testDevice.ConnectionString, transport);
            PartitionReceiver eventHubReceiver = await CreateEventHubReceiver(testDevice.Id).ConfigureAwait(false);

            var watch = new Stopwatch();

            try
            {
                deviceClient.OperationTimeoutInMilliseconds = (uint)retryDurationInMilliSec;

                ConnectionStatus? lastConnectionStatus = null;
                ConnectionStatusChangeReason? lastConnectionStatusChangeReason = null;
                int setConnectionStatusChangesHandlerCount = 0;

                deviceClient.SetConnectionStatusChangesHandler((status, statusChangeReason) =>
                {
                    _log.WriteLine($"{nameof(ConnectionStatusChangesHandler)}: status={status} statusChangeReason={statusChangeReason} count={setConnectionStatusChangesHandlerCount}");
                    lastConnectionStatus = status;
                    lastConnectionStatusChangeReason = statusChangeReason;
                    setConnectionStatusChangesHandlerCount++;
                });

                await deviceClient.OpenAsync().ConfigureAwait(false);

                if (transport != Client.TransportType.Http1)
                {
                    Assert.AreEqual(1, setConnectionStatusChangesHandlerCount);
                    Assert.AreEqual(ConnectionStatus.Connected, lastConnectionStatus);
                    Assert.AreEqual(ConnectionStatusChangeReason.Connection_Ok, lastConnectionStatusChangeReason);
                }

                string payload, p1Value;
                Client.Message testMessage = ComposeD2CTestMessage(out payload, out p1Value);
                await deviceClient.SendEventAsync(testMessage).ConfigureAwait(false);


                bool isReceived = false;
                Stopwatch sw = new Stopwatch();
                sw.Start();
                while (!isReceived && sw.Elapsed.Minutes < 1)
                {
                    var events = await eventHubReceiver.ReceiveAsync(int.MaxValue, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    isReceived = VerifyTestMessage(events, testDevice.Id, payload, p1Value);
                }
                sw.Stop();

                _log.WriteLine($"Requesting fault injection type={faultType} reason={reason}, delay={delayInSec}s, duration={durationInSec}s");

                uint oldTimeout = deviceClient.OperationTimeoutInMilliseconds;

                try
                {
                    // For MQTT FaultInjection will terminate the connection prior to a PUBACK 
                    // which leads to an infinite loop trying to resend the FaultInjection message.
                    if (transport == Client.TransportType.Mqtt ||
                        transport == Client.TransportType.Mqtt_Tcp_Only ||
                        transport == Client.TransportType.Mqtt_WebSocket_Only)
                    {
                        deviceClient.OperationTimeoutInMilliseconds = 1000;
                    }

                    await deviceClient.SendEventAsync(FaultInjection.ComposeErrorInjectionProperties(faultType, reason,
                    delayInSec,
                    durationInSec)).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    _log.WriteLine($"{nameof(TimeoutException)}: {ex}");
                }
                finally
                {
                    deviceClient.OperationTimeoutInMilliseconds = oldTimeout;
                    _log.WriteLine($"Fault injection requested.");
                }

                await eventHubReceiver.ReceiveAsync(int.MaxValue, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                testMessage = ComposeD2CTestMessage(out payload, out p1Value);
                await deviceClient.SendEventAsync(testMessage).ConfigureAwait(false);

                sw.Reset();
                sw.Start();
                while (!isReceived && sw.Elapsed.Minutes < 1)
                {
                    var events = await eventHubReceiver.ReceiveAsync(int.MaxValue, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    isReceived = VerifyTestMessage(events, testDevice.Id, payload, p1Value);
                }
                sw.Stop();

                await deviceClient.CloseAsync().ConfigureAwait(false);

                if (transport != Client.TransportType.Http1)
                {
                    Assert.AreEqual(2, setConnectionStatusChangesHandlerCount);
                    Assert.AreEqual(ConnectionStatus.Disabled, lastConnectionStatus);
                    Assert.AreEqual(ConnectionStatusChangeReason.Client_Close, lastConnectionStatusChangeReason);
                }
            }
            finally
            {
                await deviceClient.CloseAsync().ConfigureAwait(false);
                await eventHubReceiver.CloseAsync().ConfigureAwait(false);

                watch.Stop();
                if (durationInSec > 0)
                {
                    int timeToFinishFaultInjection = durationInSec * 1000 - (int)watch.ElapsedMilliseconds;
                    _log.WriteLine($"Waiting {timeToFinishFaultInjection}ms to ensure that FaultInjection duration passed.");
                    await Task.Delay(timeToFinishFaultInjection).ConfigureAwait(false);
                }
            }
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _listener.Dispose();
            }
        }
    }
}
