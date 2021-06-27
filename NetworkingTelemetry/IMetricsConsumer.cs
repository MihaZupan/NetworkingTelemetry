// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace NetworkingTelemetry
{
    public interface IMetricsConsumer<TMetrics>
    {
        void OnMetrics(TMetrics previous, TMetrics current);
    }
}
