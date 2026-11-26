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

public sealed record BookStarted(string JobId, string Asin, string Title) : EngineEvent;
public sealed record BookProgress(string JobId, string Asin, int Percent) : EngineEvent;
public sealed record BookFinished(string JobId, string Asin, string Title, string Result, string Status) : EngineEvent;
public sealed record QueueChanged(QueueDto Queue) : EngineEvent;
public sealed record QueuePaused(PendingDecisionDto Pause) : EngineEvent;
public sealed record QueueResumed(long PauseId, string Decision) : EngineEvent;
public sealed record QueueWaiting(QueueWaitDto Wait) : EngineEvent;
public sealed record QueueWaitEnded(long PauseId, string Reason) : EngineEvent;
public sealed record QueueDispatchPaused : EngineEvent;
public sealed record QueueDispatchResumed : EngineEvent;
public sealed record BookLog(string JobId, string Level, string Message) : EngineEvent;

public sealed record AccountNeedsLogin(string AccountId) : EngineEvent;
public sealed record AccountSignedIn(string AccountId, string? Locale) : EngineEvent;

public sealed record LocateScanProgress(LocateScanDto Scan) : EngineEvent;
public sealed record QualityScanProgress(QualityScanDto Scan) : EngineEvent;
public sealed record InactiveScanProgress(InactiveScanDto Scan) : EngineEvent;
public sealed record BooksRemovedFromAudible(IReadOnlyList<string> Asins) : EngineEvent;
