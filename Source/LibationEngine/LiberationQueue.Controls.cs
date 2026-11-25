using System;
using System.Linq;
using System.Threading.Tasks;

namespace LibationEngine;

public enum CancelJobOutcome
{
	NotFound,
	Removed,
	Cancelling
}

public enum MoveJobOutcome
{
	NotFound,
	NotQueued,
	Moved
}

public sealed partial class LiberationQueue
{
	private volatile bool _dispatchPaused;
	private DateTimeOffset? _runStartedAt;

	public QueueHistory History { get; } = new();

	/// <summary>Set by the user; stops new books starting while running ones finish. Survives runs.</summary>
	public bool DispatchPaused => _dispatchPaused;

	public event EventHandler? DispatchPauseChanged;

	/// <summary>False when it was already in that state.</summary>
	public bool SetDispatchPaused(bool paused)
	{
		lock (_gate)
		{
			if (_dispatchPaused == paused)
				return false;
			_dispatchPaused = paused;
		}

		DispatchPauseChanged?.Invoke(this, EventArgs.Empty);
		SignalEnqueued();
		return true;
	}

	public QueueStats Stats()
	{
		var active = Jobs.GetActive();
		var finished = Jobs.GetCompleted();
		var queued = Jobs.GetAllItems().Except(finished).Except(active).Count();
		return new(
			queued,
			finished.Count(j => j.Result is ProcessBookResult.Success),
			finished.Count(j => QueueStats.IsError(j.Result)),
			Running ? _runStartedAt : null);
	}

	/// <summary>Drops a queued job, or cancels a running one as the desktop cancel button does.</summary>
	public async Task<CancelJobOutcome> CancelJobAsync(JobId id)
	{
		if (FindJob(id) is not { } job || Jobs.GetCompleted().Contains(job))
			return CancelJobOutcome.NotFound;

		if (Jobs.RemoveQueued(job))
		{
			await job.CancelAsync();
			return CancelJobOutcome.Removed;
		}

		await job.CancelAsync();
		return CancelJobOutcome.Cancelling;
	}

	public MoveJobOutcome MoveJob(JobId id, QueuePosition position)
	{
		if (FindJob(id) is not { } job)
			return MoveJobOutcome.NotFound;
		return Jobs.MoveQueuePosition(job, position) ? MoveJobOutcome.Moved : MoveJobOutcome.NotQueued;
	}

	/// <summary>Forgets finished jobs and their history, as the desktop Clear Finished does.</summary>
	public void ClearFinished()
	{
		Jobs.ClearCompleted();
		History.Clear();
	}
}
