// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;

namespace NetworkingTelemetry
{
    public abstract class EventSourceListener : EventListener
    {
        public ILogger<EventSourceListener>? Logger { get; set; }

        public abstract void SetMetricsInterval(TimeSpan interval);
        public abstract bool TryAdd(object consumer);
        public abstract bool TryRemove(object consumer);
    }

    public abstract class EventSourceListener<TTelemetryConsumer, TMetrics> : EventSourceListener
        where TMetrics : class, new()
    {
        protected abstract string EventSourceName { get; }
        protected abstract int NumberOfMetrics { get; }
        protected abstract void OnEvent(TTelemetryConsumer[] consumers, EventWrittenEventArgs eventData);
        protected abstract bool TrySaveMetric(TMetrics metrics, string name, double value);

        private TTelemetryConsumer[]? _telemetryConsumers;
        private IMetricsConsumer<TMetrics>[]? _metricsConsumers;

        private int _metricsCount;
        private TMetrics? _previousMetrics;
        private TMetrics? _currentMetrics;
        private bool _trySaveMetricThrewAnException;

        private EventSource? _eventSource;
        private EventLevel _eventLevel;
        private TimeSpan? _metricsInterval;
        private TimeSpan _targetMetricsInterval = Telemetry.DefaultMetricsInterval;
        private readonly object _lockObject = new();

        protected sealed override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId <= 0)
            {
                OnNonUserEvent(eventData);
            }
            else if (_telemetryConsumers is TTelemetryConsumer[] consumers)
            {
                OnEvent(consumers, eventData);
            }
        }

        private void OnNonUserEvent(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId == -1)
            {
                if (_trySaveMetricThrewAnException)
                {
                    // TrySaveMetric threw before - metric data may be useless at this point
                    return;
                }

                // Throwing an exception here would crash the process
                if (eventData.EventName != "EventCounters" ||
                    eventData.Payload?.Count != 1 ||
                    eventData.Payload[0] is not IDictionary<string, object> counters ||
                    !counters.TryGetValue("Name", out object? nameObject) ||
                    nameObject is not string name ||
                    !(counters.TryGetValue("Mean", out var valueObj) || counters.TryGetValue("Increment", out valueObj)) ||
                    valueObj is not double value)
                {
                    Logger?.LogDebug("Failed to parse EventCounters event");
                    return;
                }

                TMetrics metrics = _currentMetrics ??= new();

                try
                {
                    if (!TrySaveMetric(metrics, name, value))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _trySaveMetricThrewAnException = true;
                    Logger?.LogError(ex, $"{nameof(TrySaveMetric)} threw an exception for EventSource {EventSourceName}");
                    return;
                }

                if (++_metricsCount == NumberOfMetrics)
                {
                    _metricsCount = 0;

                    TMetrics? previous = _previousMetrics;
                    _previousMetrics = metrics;
                    _currentMetrics = null;

                    if (previous is null)
                    {
                        return;
                    }

                    if (_metricsConsumers is IMetricsConsumer<TMetrics>[] consumers)
                    {
                        foreach (IMetricsConsumer<TMetrics> consumer in consumers)
                        {
                            try
                            {
                                consumer.OnMetrics(previous, metrics);
                            }
                            catch (Exception ex)
                            {
                                Logger?.LogError(ex, $"Uncaught exception occured while processing metrics for EventSource {EventSourceName}");
                            }
                        }
                    }
                }
            }
            else if (eventData.EventId == 0)
            {
                Logger?.LogError($"Received an error message from EventSource {EventSourceName}: {eventData.Message}");
            }
            else
            {
                Logger?.LogInformation($"Received an unknown event from EventSource {EventSourceName}: {eventData.EventId}");
            }
        }

        protected sealed override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == EventSourceName)
            {
                lock (_lockObject)
                {
                    _eventSource = eventSource;
                    UpdateEventSource();
                }
            }
        }

        private void UpdateEventSource(TimeSpan? metricsInterval = null)
        {
            lock (_lockObject)
            {
                if (metricsInterval.HasValue)
                {
                    _targetMetricsInterval = metricsInterval.Value;
                }

                if (_eventSource is null)
                {
                    return;
                }

                EventLevel eventLevel = _telemetryConsumers is null ? EventLevel.Critical : EventLevel.Verbose;
                metricsInterval = _metricsConsumers is null ? null : _targetMetricsInterval;

                if (_eventLevel == eventLevel && _metricsInterval == metricsInterval)
                {
                    return;
                }

                _eventLevel = eventLevel;
                _metricsInterval = metricsInterval;

                EnableEvents(
                    eventSource: _eventSource,
                    level: eventLevel,
                    matchAnyKeyword: EventKeywords.None,
                    arguments: metricsInterval.HasValue ? new Dictionary<string, string?> { { "EventCounterIntervalSec", metricsInterval.Value.TotalSeconds.ToString() } } : null);

                Logger?.LogDebug($"Enabled EventSource {EventSourceName} with EventLevel {eventLevel}{(metricsInterval.HasValue ? $" and EventCounterInterval {metricsInterval}" : "")}");
            }
        }

        public sealed override void SetMetricsInterval(TimeSpan interval) => UpdateEventSource(interval);

        public sealed override bool TryAdd(object consumer)
        {
            bool added = false;

            if (consumer is TTelemetryConsumer telemetryConsumer)
            {
                added |= Add(_lockObject, ref _telemetryConsumers, telemetryConsumer);
                Logger?.LogDebug($"Added a telemetry consumer {telemetryConsumer} to EventSource {EventSourceName}");
            }

            if (consumer is IMetricsConsumer<TMetrics> metricsConsumer)
            {
                added |= Add(_lockObject, ref _metricsConsumers, metricsConsumer);
                Logger?.LogDebug($"Added a metrics consumer {metricsConsumer} to EventSource {EventSourceName}");
            }

            if (added)
            {
                UpdateEventSource();
            }

            return added;

            static bool Add<T>(object lockObject, ref T[]? consumers, T consumer)
            {
                lock (lockObject)
                {
                    if (consumers is null || Array.IndexOf(consumers, consumer) < 0)
                    {
                        var newConsumers = new T[(consumers?.Length ?? 0) + 1];
                        newConsumers[^1] = consumer;
                        consumers = newConsumers;
                        return true;
                    }
                    return false;
                }
            }
        }

        public sealed override bool TryRemove(object consumer)
        {
            bool removed = false;

            if (consumer is TTelemetryConsumer telemetryConsumer)
            {
                removed |= Remove(_lockObject, ref _telemetryConsumers, telemetryConsumer);
                Logger?.LogDebug($"Removed a telemetry consumer {telemetryConsumer} from EventSource {EventSourceName}");
            }

            if (consumer is IMetricsConsumer<TMetrics> metricsConsumer)
            {
                removed |= Remove(_lockObject, ref _metricsConsumers, metricsConsumer);
                Logger?.LogDebug($"Removed a metrics consumer {metricsConsumer} from EventSource {EventSourceName}");
            }

            if (removed)
            {
                UpdateEventSource();
            }

            return removed;

            static bool Remove<T>(object lockObject, ref T[]? consumers, T consumer)
            {
                lock (lockObject)
                {
                    if (consumers is not null)
                    {
                        int index = Array.IndexOf(consumers, consumer);
                        if (index >= 0)
                        {
                            if (consumers.Length == 1)
                            {
                                consumers = null;
                            }
                            else
                            {
                                var newConsumers = new T[consumers.Length - 1];
                                Array.Copy(consumers, 0, newConsumers, 0, index);
                                if (index != newConsumers.Length)
                                {
                                    Array.Copy(consumers, index + 1, newConsumers, index, newConsumers.Length - index);
                                }
                                consumers = newConsumers;
                            }
                            return true;
                        }
                    }
                    return false;
                }
            }
        }
    }
}
