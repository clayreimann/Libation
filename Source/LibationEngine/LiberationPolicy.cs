namespace LibationEngine;

public enum DiskFullPolicy
{
	/// <summary>Pause and let the caller choose Resume or Stop.</summary>
	Ask,
	/// <summary>Clear the queue and stop.</summary>
	Stop,
	/// <summary>Keep the queue and poll free space; see <see cref="QueueWait"/>.</summary>
	WaitForSpace
}

public enum DailyLimitPolicy
{
	/// <summary>Hold dispatch until the limit lifts; see <see cref="QueueWait"/>.</summary>
	Wait,
	/// <summary>Leave blocked books un-liberated for the next run.</summary>
	Skip
}

public sealed record LiberationPolicy
{
	public required Func<int> MaxConcurrentDownloads { get; init; }
	public DiskFullPolicy OnDiskFull { get; init; } = DiskFullPolicy.Ask;
	public DailyLimitPolicy OnDailyLimit { get; init; } = DailyLimitPolicy.Wait;

	/// <summary>
	/// Given to every job the queue runs that has no gate of its own. Unattended queues set it so one throttled
	/// title cannot switch settings and send an account back to sign-in; null recovers at once, as the desktop app does.
	/// </summary>
	public ThrottleRecoveryGate? ThrottleRecovery { get; init; }
}
