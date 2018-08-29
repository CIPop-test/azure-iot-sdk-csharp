// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Azure.Devices.Client.Exceptions;
using Microsoft.Azure.Devices.Common;
using Microsoft.Azure.EventHubs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Azure.Devices.E2ETests
{
    // EventHubListener for .NET Core.
    public class EventHubTestListener
    {
        private PartitionReceiver _partitionReceiver;
        private static TestLogging s_log = TestLogging.GetInstance();

        private EventHubTestListener(PartitionReceiver receiver)
        {
            _partitionReceiver = receiver;
        }

        #region PAL
        public static async Task<EventHubTestListener> CreateListener(string deviceName)
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
                    s_log.WriteLine($"{nameof(EventHubTestListener)}.{nameof(CreateListener)}: Cannot create receiver: {ex}");
                }
            }

            sw.Stop();

            return new EventHubTestListener(eventHubReceiver);
        }

        public async Task<bool> WaitForMessage(string deviceId, string payload, string p1Value)
        {
            bool isReceived = false;
            Stopwatch sw = new Stopwatch();
            sw.Start();
            while (!isReceived && sw.Elapsed.Minutes < 1)
            {
                var events = await _partitionReceiver.ReceiveAsync(int.MaxValue, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                isReceived = VerifyTestMessage(events, deviceId, payload, p1Value);
            }

            sw.Stop();

            return isReceived;
        }

        public Task CloseAsync()
        {
            return _partitionReceiver.CloseAsync();
        }

        private bool VerifyTestMessage(IEnumerable<EventData> events, string deviceName, string payload, string p1Value)
        {
            if (events == null)
            {
                s_log.WriteLine($"{nameof(EventHubTestListener)}.{nameof(VerifyTestMessage)}: no events received.");
                return false;
            }

            s_log.WriteLine($"{nameof(EventHubTestListener)}.{nameof(VerifyTestMessage)}: {events.Count()} events received.");

            foreach (var eventData in events)
            {
                try
                {
                    var data = Encoding.UTF8.GetString(eventData.Body.ToArray());
                    s_log.WriteLine($"{nameof(EventHubTestListener)}.{nameof(VerifyTestMessage)}: event data: '{data}'");

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
                    s_log.WriteLine($"{nameof(EventHubTestListener)}.{nameof(VerifyTestMessage)}: Cannot read eventData: {ex}");
                }
            }

            s_log.WriteLine($"{nameof(EventHubTestListener)}.{nameof(VerifyTestMessage)}: none of the messages matched the expected payload '{payload}'.");

            return false;
        }

        private bool VerifyKeyValue(string checkForKey, string checkForValue, IDictionary<string, object> properties)
        {
            foreach (var key in properties.Keys)
            {
                if (checkForKey == key)
                {
                    if ((string)properties[checkForKey] == checkForValue)
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
