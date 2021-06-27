// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace NetworkingTelemetry
{
    public static class NetworkingTelemetry
    {
        private static readonly object _lock = new();
        private static ILogger<EventSourceListener>? _logger;
        private static TimeSpan _metricsInterval = TimeSpan.FromSeconds(1);

        private static readonly List<object> _consumers = new();

        public static ImmutableArray<EventSourceListener> Listeners { get; private set; } = ImmutableArray.Create<EventSourceListener>(
            new HttpEventSourceListener(),
            new KestrelEventSourceListener(),
            new NameResolutionEventSourceListener(),
            new NetSecurityEventSourceListener(),
            new SocketsEventSourceListener());

        public static void AddListener(EventSourceListener listener)
        {
            lock (_lock)
            {
                listener.Logger = _logger;
                listener.SetMetricsInterval(_metricsInterval);

                if (!Listeners.Contains(listener))
                {
                    Listeners = Listeners.Add(listener);
                    foreach (object consumer in _consumers)
                    {
                        listener.TryAdd(consumer);
                    }
                }
            }
        }

        public static bool AddConsumer(object consumer)
        {
            lock (_lock)
            {
                if (!_consumers.Contains(consumer))
                {
                    _consumers.Add(consumer);
                    foreach (EventSourceListener listener in Listeners)
                    {
                        listener.TryAdd(consumer);
                    }
                    return true;
                }
                return false;
            }
        }

        public static bool RemoveConsumer(object consumer)
        {
            lock (_lock)
            {
                if (_consumers.Remove(consumer))
                {
                    foreach (EventSourceListener listener in Listeners)
                    {
                        listener.TryRemove(consumer);
                    }
                    return true;
                }
                return false;
            }
        }

        public static void ConfigureLogger(ILogger<EventSourceListener> logger)
        {
            lock (_lock)
            {
                _logger = logger;
                foreach (EventSourceListener listener in Listeners)
                {
                    listener.Logger = logger;
                }
            }
        }

        public static void SetMetricsInterval(TimeSpan interval)
        {
            lock (_lock)
            {
                _metricsInterval = interval;
                foreach (EventSourceListener listener in Listeners)
                {
                    listener.SetMetricsInterval(interval);
                }
            }
        }
    }
}
