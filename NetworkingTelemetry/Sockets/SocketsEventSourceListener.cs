// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net.Sockets;

namespace NetworkingTelemetry
{
    internal sealed class SocketsEventSourceListener : EventSourceListener<ISocketsTelemetryConsumer, SocketsMetrics>
    {
        protected override string EventSourceName => "System.Net.Sockets";

        protected override int NumberOfMetrics => 6;

        protected override void OnEvent(ISocketsTelemetryConsumer[] consumers, EventWrittenEventArgs eventData)
        {
            ReadOnlyCollection<object> payload = eventData.Payload!;

            switch (eventData.EventId)
            {
                case 1:
                    Debug.Assert(eventData.EventName == "ConnectStart" && payload.Count == 1);
                    {
                        var address = (string)payload[0];
                        foreach (var consumer in consumers)
                        {
                            consumer.OnConnectStart(eventData.TimeStamp, address);
                        }
                    }
                    break;

                case 2:
                    Debug.Assert(eventData.EventName == "ConnectStop" && payload.Count == 0);
                    {
                        foreach (var consumer in consumers)
                        {
                            consumer.OnConnectStop(eventData.TimeStamp);
                        }
                    }
                    break;

                case 3:
                    Debug.Assert(eventData.EventName == "ConnectFailed" && payload.Count == 2);
                    {
                        var error = (SocketError)payload[0];
                        var exceptionMessage = (string)payload[1];
                        foreach (var consumer in consumers)
                        {
                            consumer.OnConnectFailed(eventData.TimeStamp, error, exceptionMessage);
                        }
                    }
                    break;
            }
        }

        protected override bool TrySaveMetric(SocketsMetrics metrics, string name, double value)
        {
            long longValue = (long)value;

            switch (name)
            {
                case "outgoing-connections-established":
                    metrics.OutgoingConnectionsEstablished = longValue;
                    break;

                case "incoming-connections-established":
                    metrics.IncomingConnectionsEstablished = longValue;
                    break;

                case "bytes-received":
                    metrics.BytesReceived = longValue;
                    break;

                case "bytes-sent":
                    metrics.BytesSent = longValue;
                    break;

                case "datagrams-received":
                    metrics.DatagramsReceived = longValue;
                    break;

                case "datagrams-sent":
                    metrics.DatagramsSent = longValue;
                    break;

                default:
                    return false;
            }

            return true;
        }
    }
}
