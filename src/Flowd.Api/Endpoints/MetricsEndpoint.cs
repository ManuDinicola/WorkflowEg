using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Flowd.Application.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Flowd.Api.Endpoints;

/// <summary>Prometheus text exposition of the <c>Flowd</c> meter (FR-OBS-001, API-013). No external exporter package is needed.</summary>
public sealed class PrometheusMetrics : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<(string Name, string Labels), double> _sums = new();
    private readonly ConcurrentDictionary<(string Name, string Labels), long> _counts = new();
    private readonly ConcurrentDictionary<string, bool> _gauges = new(StringComparer.Ordinal);

    public PrometheusMetrics()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == EngineTelemetry.Name)
            {
                if (instrument.GetType().Name.StartsWith("UpDownCounter", StringComparison.Ordinal))
                {
                    _gauges[instrument.Name] = true;
                }

                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    public string Render()
    {
        var text = new StringBuilder();
        foreach (var group in _sums.OrderBy(p => p.Key.Name, StringComparer.Ordinal).ThenBy(p => p.Key.Labels, StringComparer.Ordinal).GroupBy(p => p.Key.Name, StringComparer.Ordinal))
        {
            var histogram = group.Key.EndsWith("_seconds", StringComparison.Ordinal);
            var type = histogram ? "summary" : "counter";
            if (_gauges.ContainsKey(group.Key))
            {
                type = "gauge";
            }

            text.Append("# TYPE ").Append(group.Key).Append(' ').Append(type).Append('\n');
            foreach (var ((name, labels), sum) in group)
            {
                var braces = labels.Length > 0 ? "{" + labels + "}" : string.Empty;
                text.Append(name).Append(histogram ? "_sum" : string.Empty).Append(braces).Append(' ').Append(sum.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                if (histogram)
                {
                    text.Append(name).Append("_count").Append(braces).Append(' ').Append(_counts.GetValueOrDefault((name, labels)).ToString(CultureInfo.InvariantCulture)).Append('\n');
                }
            }
        }

        if (EngineTelemetry.OutboxPending is { } pending)
        {
            text.Append("# TYPE flowd_outbox_pending gauge\nflowd_outbox_pending ").Append(pending().ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return text.ToString();
    }

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var labels = string.Join(',', tags.ToArray().OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}=\"{t.Value}\""));
        var key = (instrument.Name, labels);
        _sums.AddOrUpdate(key, value, (_, current) => current + value);
        _counts.AddOrUpdate(key, 1, (_, current) => current + 1);
    }
}

public static class MetricsEndpoint
{
    public static IEndpointRouteBuilder MapMetricsEndpoint(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/metrics", (PrometheusMetrics metrics) => Results.Text(metrics.Render(), "text/plain; version=0.0.4"));
        return routes;
    }
}
