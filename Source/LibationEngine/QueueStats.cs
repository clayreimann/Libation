using System;

namespace LibationEngine;

/// <summary>The counters beside the desktop queue: waiting, finished well, finished badly, and how long the run has gone.</summary>
public sealed record QueueStats(int Queued, int Completed, int Errors, DateTimeOffset? RunningSince)
{
	public static bool IsError(ProcessBookResult result)
		=> result is ProcessBookResult.FailedAbort
			or ProcessBookResult.FailedSkip
			or ProcessBookResult.FailedRetry
			or ProcessBookResult.ValidationFail
			or ProcessBookResult.WidevineRecommended
			or ProcessBookResult.DiskFull;
}
