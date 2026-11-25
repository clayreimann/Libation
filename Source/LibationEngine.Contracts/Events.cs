namespace LibationEngine.Contracts;

public abstract record EngineEvent
{
	public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record StateChanged(string State) : EngineEvent;
public sealed record ScanStarted : EngineEvent;
public sealed record ScanFinished(int ExitCode) : EngineEvent;
public sealed record LiberateStarted : EngineEvent;
public sealed record LiberateFinished(int ExitCode) : EngineEvent;
public sealed record CycleFinished(int ExitCode) : EngineEvent;
public sealed record NextRunScheduled(DateTimeOffset? NextRun) : EngineEvent;
