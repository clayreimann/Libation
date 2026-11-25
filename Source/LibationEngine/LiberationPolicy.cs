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

public sealed record LiberationPolicy
{
	public required Func<int> MaxConcurrentDownloads { get; init; }
	public DiskFullPolicy OnDiskFull { get; init; } = DiskFullPolicy.Ask;
}
