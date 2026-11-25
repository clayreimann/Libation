using LibationFileManager;

namespace LibationEngine;

public readonly record struct JobId(string Value)
{
	public static JobId New() => new(Guid.NewGuid().ToString("N"));
	public override string ToString() => Value;
}

public readonly record struct PauseId(long Value)
{
	public override string ToString() => Value.ToString();
}

public enum PauseReason
{
	BadBook,
	LicenseGuidance,
	WidevineGuidance,
	DiskFull
}

public enum DecisionKind
{
	Continue,
	Stop,
	Resume,
	BadBook
}

/// <summary>What the queue is waiting to be told; answer it with <see cref="LiberationQueue.Resume"/>.</summary>
public sealed record PendingDecision(PauseId Id, PauseReason Reason, JobId? Job, IReadOnlySet<DecisionKind> Allowed);

public abstract record Decision
{
	public abstract DecisionKind Kind { get; }

	public static Decision Continue { get; } = new ContinueDecision();
	public static Decision Stop { get; } = new StopDecision();
	public static Decision Resume { get; } = new ResumeDecision();
	public static Decision BadBook(Configuration.BadBookAction action, bool applyToAll = false, bool remember = false)
		=> new BadBookDecision(action, applyToAll, remember);
}

public sealed record ContinueDecision : Decision
{
	public override DecisionKind Kind => DecisionKind.Continue;
}

public sealed record StopDecision : Decision
{
	public override DecisionKind Kind => DecisionKind.Stop;
}

public sealed record ResumeDecision : Decision
{
	public override DecisionKind Kind => DecisionKind.Resume;
}

public sealed record BadBookDecision(Configuration.BadBookAction Action, bool ApplyToAll, bool Remember) : Decision
{
	public override DecisionKind Kind => DecisionKind.BadBook;
}

public sealed record PauseResolved(PauseId Id, Decision Decision);

public enum WaitReason
{
	DailyLimit,
	DiskFull
}

public sealed record VolumeSpace(string Root, long? FreeBytes, long RequiredBytes);

/// <summary>A hold the queue releases itself; <see cref="Allowed"/> lists any early answer <see cref="LiberationQueue.Resume"/> accepts.</summary>
public sealed record QueueWait(
	PauseId Id,
	WaitReason Reason,
	DateTimeOffset Since,
	DateTimeOffset? NextCheck,
	JobId? Job,
	IReadOnlyList<VolumeSpace>? Volumes,
	DailyDownloadLimit.Allowance? Allowance,
	IReadOnlySet<DecisionKind> Allowed);
