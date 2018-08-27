// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Client.Exceptions;
using Microsoft.Azure.Devices.Common;
using Microsoft.Azure.EventHubs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Azure.Devices.E2ETests
{
    public partial class MessageE2ETests
    {

        #region PAL
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
            TestDevice testDevice = await TestDevice.GetTestDeviceAsync(DevicePrefix).ConfigureAwait(false);

            PartitionReceiver eventHubReceiver = await CreateEventHubReceiver(testDevice.Id).ConfigureAwait(false);
            var deviceClient = DeviceClient.CreateFromConnectionString(testDevice.ConnectionString, transportSettings);

            try
            {
                await deviceClient.OpenAsync().ConfigureAwait(false);

                string payload;
                string p1Value;
                Client.Message testMessage = ComposeD2CTestMessage(out payload, out p1Value);
                await deviceClient.SendEventAsync(testMessage).ConfigureAwait(false);

                bool isReceived = false;
                Stopwatch sw = new Stopwatch();
                sw.Start();
                while (!isReceived && sw.Elapsed.Minutes < 1)
                {
                    var events = await eventHubReceiver.ReceiveAsync(int.MaxValue, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    isReceived = VerifyTestMessage(events, testDevice.Id, payload, p1Value);
                }
                sw.Stop();

                Assert.IsTrue(isReceived, "Message is not received.");
            }
            finally
            {
                await deviceClient.CloseAsync().ConfigureAwait(false);
                await eventHubReceiver.CloseAsync().ConfigureAwait(false);
            }
        }

        internal async Task SendMessageRecovery(Client.TransportType transport,
            string faultType, string reason, int delayInSec, int durationInSec = 0, int retryDurationInMilliSec = FaultInjection.RecoveryTimeMilliseconds)
        {
            TestDevice testDevice = await TestDevice.GetTestDeviceAsync(DevicePrefix).ConfigureAwait(false);

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
        #endregion

        #region Helper Functions
        private async Task<PartitionReceiver> CreateEventHubReceiver(string deviceName)
        {
            PartitionReceiver eventHubReceiver = null;
            Stopwatch sw = new Stopwatch();
            sw.Start();

            var builder = new EventHubsConnectionStringBuilder(Configuration.IoTHub.EventHubString)
            {
                EntityPath = Configuration.IoTHub.EventHubCompatibleName
            };

            EventHubClient eventHubClient = EventHubClient.CreateFromConnectionString(builder.ToString());
            var eventRuntimeInformation = await eventHubClient.GetRuntimeInformationAsync().ConfigureAwait(false);
            var eventHubPartitionsCount = eventRuntimeInformation.PartitionCount;
            string partition = EventHubPartitionKeyResolver.ResolveToPartition(deviceName, eventHubPartitionsCount);
            string consumerGroupName = Configuration.IoTHub.EventHubConsumerGroup;

            while (eventHubReceiver == null && sw.Elapsed.Minutes < 1)
            {
                try
                {
                    eventHubReceiver = eventHubClient.CreateReceiver(consumerGroupName, partition, DateTime.Now.AddMinutes(-5));
                }
                catch (QuotaExceededException ex)
                {
                    Debug.WriteLine(ex);
                }
            }

            sw.Stop();

            return eventHubReceiver;
        }

        private bool VerifyTestMessage(IEnumerable<EventData> events, string deviceName, string payload, string p1Value)
        {
            if (events == null)
            {
                _log.WriteLine($"{nameof(MessageE2ETests)}.{nameof(VerifyTestMessage)}: no events received.");
                return false;
            }

            _log.WriteLine($"{nameof(MessageE2ETests)}.{nameof(VerifyTestMessage)}: {events.Count()} events received.");

            foreach (var eventData in events)
            {
                try
                {
                    var data = Encoding.UTF8.GetString(eventData.Body.ToArray());
                    _log.WriteLine($"{nameof(MessageE2ETests)}.{nameof(VerifyTestMessage)}: event data: '{data}'");

                    if (data == payload)
                    {
                        var connectionDeviceId = eventData.Properties["iothub-connection-device-id"].ToString();
                        if (string.Equals(connectionDeviceId, deviceName, StringComparison.CurrentCultureIgnoreCase) &&
                            VerifyKeyValue("property1", p1Value, eventData.Properties))
                        {
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.WriteLine($"{nameof(MessageE2ETests)}.{nameof(VerifyTestMessage)}: Cannot read eventData: {ex}");
                }
            }

            _log.WriteLine($"{nameof(MessageE2ETests)}.{nameof(VerifyTestMessage)}: none of the messages matched the expected payload '{payload}'.");

            return false;
        }

        private bool VerifyKeyValue(string checkForKey, string checkForValue, IDictionary<string, object> properties)
        {
            foreach (var key in properties.Keys)
            {
                if (checkForKey == key)
                {
                    if((string)properties[checkForKey] == checkForValue)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        #endregion
    }
}
