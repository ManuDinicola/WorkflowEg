using System.Diagnostics;
using System.Diagnostics.Metrics;
using Flowd.Engine.Effects;
using Flowd.Engine.State;
using Microsoft.Extensions.Logging;

namespace Flowd.Application.Observability;

/// <summary>
/// Metrics, traces and log scopes of the engine (section 19). Metric names follow FR-OBS-001; the meter and the activity source are
/// both called <c>Flowd</c> and are picked up by OpenTelemetry or by the built-in <c>/metrics</c> endpoint.
/// </summary>
public sealed class EngineTelemetry : IDisposable
{
    public const string Name = "Flowd";

    private static readonly ActivitySource Source = new(Name);

    private readonly Meter _meter = new(Name);
    private readonly Counter<long> _started;
    private readonly Counter<long> _completed;
    private readonly UpDownCounter<long> _active;
    private readonly Counter<long> _jobsCreated;
    private readonly Counter<long> _jobsActivated;
    private readonly Histogram<double> _activationLatency;
    private readonly Counter<long> _jobsTimedOut;
    private readonly UpDownCounter<long> _incidentsOpen;
    private readonly Histogram<double> _timerLag;
    private readonly Histogram<double> _commandDuration;
    private readonly Histogram<double> _lockWait;
    private readonly ILogger<EngineTelemetry> _logger;

    public EngineTelemetry(ILogger<EngineTelemetry> logger)
    {
        _logger = logger;
        _started = _meter.CreateCounter<long>("flowd_process_instances_started_total");
        _completed = _meter.CreateCounter<long>("flowd_process_instances_completed_total");
        _active = _meter.CreateUpDownCounter<long>("flowd_process_instances_active");
        _jobsCreated = _meter.CreateCounter<long>("flowd_jobs_created_total");
        _jobsActivated = _meter.CreateCounter<long>("flowd_jobs_activated_total");
        _activationLatency = _meter.CreateHistogram<double>("flowd_job_activation_latency_seconds", "s");
        _jobsTimedOut = _meter.CreateCounter<long>("flowd_jobs_timed_out_total");
        _incidentsOpen = _meter.CreateUpDownCounter<long>("flowd_incidents_open");
        _timerLag = _meter.CreateHistogram<double>("flowd_timers_due_lag_seconds", "s");
        _commandDuration = _meter.CreateHistogram<double>("flowd_command_duration_seconds", "s");
        _lockWait = _meter.CreateHistogram<double>("flowd_command_lock_wait_seconds", "s");
    }

    /// <summary>Set by the outbox publisher so the pending-events gauge can be read (FR-OBS-001).</summary>
    public static Func<long>? OutboxPending { get; set; }

    /// <summary>Starts a trace span, a log scope and a duration measurement for one command (FR-OBS-002, FR-OBS-003).</summary>
    public CommandScope StartCommand(string command, long instanceKey)
    {
        var activity = Source.StartActivity($"command {command}");
        activity?.SetTag("flowd.instanceKey", instanceKey);
        var scope = _logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal) { ["instanceKey"] = instanceKey, ["command"] = command, ["traceId"] = activity?.TraceId.ToString() ?? string.Empty });
        return new CommandScope(this, command, activity, scope);
    }

    public void LockWaited(TimeSpan waited) => _lockWait.Record(waited.TotalSeconds);

    public void JobsActivated(string type, int count, IEnumerable<TimeSpan> waited)
    {
        if (count == 0)
        {
            return;
        }

        _jobsActivated.Add(count, new KeyValuePair<string, object?>("type", type));
        foreach (var wait in waited)
        {
            _activationLatency.Record(Math.Max(wait.TotalSeconds, 0), new KeyValuePair<string, object?>("type", type));
        }
    }

    public void JobsTimedOut(int count)
    {
        if (count > 0)
        {
            _jobsTimedOut.Add(count);
        }
    }

    public void TimerFired(TimeSpan lag) => _timerLag.Record(Math.Max(lag.TotalSeconds, 0));

    /// <summary>Updates the counters from what a committed command did.</summary>
    public void Observe(IReadOnlyList<EngineEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case InstanceStarted:
                    _started.Add(1);
                    _active.Add(1);
                    break;
                case InstanceStateChanged { State: InstanceState.Completed }:
                    _completed.Add(1);
                    _active.Add(-1);
                    break;
                case InstanceStateChanged { State: InstanceState.Terminated }:
                    _active.Add(-1);
                    break;
                case HistoryAppended { EventType: HistoryEventTypes.JobCreated } created:
                    _jobsCreated.Add(1, new KeyValuePair<string, object?>("type", created.Payload?["type"]?.GetValue<string>() ?? string.Empty));
                    break;
                case HistoryAppended { EventType: HistoryEventTypes.IncidentCreated }:
                    _incidentsOpen.Add(1);
                    break;
                case HistoryAppended { EventType: HistoryEventTypes.IncidentResolved }:
                    _incidentsOpen.Add(-1);
                    break;
            }
        }
    }

    public void Dispose() => _meter.Dispose();

    internal void CommandFinished(string command, TimeSpan duration) =>
        _commandDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("command", command));

}

/// <summary>Ends the span, the log scope and the duration measurement of one command.</summary>
public sealed class CommandScope : IDisposable
{
    private readonly EngineTelemetry _owner;
    private readonly string _command;
    private readonly System.Diagnostics.Activity? _activity;
    private readonly IDisposable? _logScope;
    private readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();

    internal CommandScope(EngineTelemetry owner, string command, System.Diagnostics.Activity? activity, IDisposable? logScope)
    {
        _owner = owner;
        _command = command;
        _activity = activity;
        _logScope = logScope;
    }

    public void Dispose()
    {
        _owner.CommandFinished(_command, System.Diagnostics.Stopwatch.GetElapsedTime(_start));
        _logScope?.Dispose();
        _activity?.Dispose();
    }
}
