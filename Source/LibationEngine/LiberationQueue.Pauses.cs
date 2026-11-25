using ApplicationServices;
using DataLayer;
using FileLiberator;
using LibationFileManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LibationEngine;

public sealed partial class LiberationQueue
{
	private static readonly IReadOnlySet<DecisionKind> NoDecisions = new HashSet<DecisionKind>();
	private static readonly IReadOnlySet<DecisionKind> ContinueOrStop = new HashSet<DecisionKind> { DecisionKind.Continue, DecisionKind.Stop };
	private static readonly IReadOnlySet<DecisionKind> ResumeOrStop = new HashSet<DecisionKind> { DecisionKind.Resume, DecisionKind.Stop };
	private static readonly IReadOnlySet<DecisionKind> BadBookOnly = new HashSet<DecisionKind> { DecisionKind.BadBook };

	private static readonly TimeSpan DiskPollInterval = TimeSpan.FromMinutes(1);
	private static readonly TimeSpan[] DiskProbeBackoff = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60)];

	private sealed record PauseEntry(PendingDecision Info);

	private readonly object _gate = new();
	private readonly List<PauseEntry> _pending = [];
	private readonly Queue<Func<Task>> _settledWork = new();
	private readonly HashSet<PauseReason> _shownThisRun = [];
	private readonly HashSet<LiberationJob> _diskFullVictims = [];
	private readonly List<LiberationJob> _unanswered = [];
	private QueueWait? _waiting;
	private bool _diskFullEpisode;
	private int _probeFailures;
	private long _pauseSeq;

	public event EventHandler<PendingDecision>? QueuePaused;
	public event EventHandler<PauseResolved>? QueueResumed;
	public event EventHandler<QueueWait>? QueueWaiting;
	public event EventHandler<QueueWait>? QueueWaitEnded;

	/// <summary>Every unanswered pause, oldest first.</summary>
	public IReadOnlyList<PendingDecision> Pending
	{
		get { lock (_gate) return _pending.Select(e => e.Info).ToList(); }
	}

	/// <summary>The hold the queue will release by itself, if any.</summary>
	public QueueWait? Waiting
	{
		get { lock (_gate) return _waiting; }
	}

	/// <summary>Internal: free space on the backup volumes, or null when none can be read.</summary>
	internal Func<IReadOnlyList<VolumeSpace>?> SpaceProbe { get; set; } = ProbeBackupVolumes;

	private static IReadOnlyList<VolumeSpace>? ProbeBackupVolumes()
	{
		var drives = DiskSpaceHelper.GetBackupDriveSpaces(Configuration.Instance, 1);
		if (drives.All(d => d.AvailableBytes is null))
			return null;

		return drives
			.Select(d => new VolumeSpace(
				d.DriveRoot,
				d.AvailableBytes,
				Math.Max(d.RequiredBytes, DiskSpaceHelper.GetCriticalFreeBytesForDriveUsage(d.Usage))))
			.ToList();
	}

	private PauseId NewPauseId() => new(Interlocked.Increment(ref _pauseSeq));

	private bool HasPendingDecision
	{
		get { lock (_gate) return _pending.Count > 0; }
	}

	private bool CanFinishRun()
	{
		lock (_gate)
			return _pending.Count == 0 && _settledWork.Count == 0;
	}

	private void BeginRun()
	{
		lock (_gate)
		{
			_aborted = false;
			_pending.Clear();
			_settledWork.Clear();
			_shownThisRun.Clear();
			_diskFullVictims.Clear();
			_unanswered.Clear();
			_waiting = null;
			_diskFullEpisode = false;
			_probeFailures = 0;
		}
	}

	/// <summary>Answers a pause; false when the id is stale or the decision is not one it allows.</summary>
	public bool Resume(PauseId id, Decision decision)
	{
		var resolved = new List<PauseResolved>();
		QueueWait? endedWait = null;

		lock (_gate)
		{
			var index = _pending.FindIndex(e => e.Info.Id == id);
			if (index < 0)
			{
				if (_waiting is not { } wait || wait.Id != id || !wait.Allowed.Contains(decision.Kind))
					return false;

				endedWait = TakeWaitLocked();
				if (decision.Kind == DecisionKind.Stop)
					_settledWork.Enqueue(() => TeardownAsync(null));
			}
			else
			{
				var entry = _pending[index];
				if (!entry.Info.Allowed.Contains(decision.Kind))
					return false;

				_pending.RemoveAt(index);
				Settle(entry, decision);

				if (entry.Info.Reason == PauseReason.DiskFull)
					_diskFullEpisode = false;

				if (decision is BadBookDecision { Action: var action } bad && (bad.ApplyToAll || action == Configuration.BadBookAction.Abort))
				{
					BadBookSession.Override = action;
					var followers = _pending.Where(e => e.Info.Reason == PauseReason.BadBook).ToList();
					foreach (var follower in followers)
					{
						_pending.Remove(follower);
						Settle(follower, Decision.BadBook(action, applyToAll: true));
					}
				}
			}
		}

		if (endedWait is not null)
			QueueWaitEnded?.Invoke(this, endedWait);
		foreach (var r in resolved)
			QueueResumed?.Invoke(this, r);

		SignalEnqueued();
		return true;

		void Settle(PauseEntry entry, Decision d)
		{
			resolved.Add(new PauseResolved(entry.Info.Id, d));
			_settledWork.Enqueue(() => ApplyDecisionAsync(entry, d));
		}
	}

	private async Task ApplyDecisionAsync(PauseEntry entry, Decision decision)
	{
		switch (decision)
		{
			case BadBookDecision bad:
				if (entry.Info.Job is not { } jobId || FindJob(jobId) is not { } job)
					return;
				await ApplyBadBookAsync(job, bad);
				break;
			case StopDecision:
				await TeardownAsync(null);
				break;
		}
	}

	private Task ApplyBadBookAsync(LiberationJob job, BadBookDecision decision)
	{
		if (decision.Remember)
			job.Configuration.BadBook = decision.Action;

		var result = decision.Action switch
		{
			Configuration.BadBookAction.Abort => ProcessBookResult.FailedAbort,
			Configuration.BadBookAction.Ignore => ProcessBookResult.FailedSkip,
			_ => ProcessBookResult.FailedRetry
		};
		job.Result = result;
		return FinishJobAsync(job, result);
	}

	private async Task DrainSettledWorkAsync()
	{
		while (true)
		{
			Func<Task>? work;
			lock (_gate)
				work = _settledWork.Count > 0 ? _settledWork.Dequeue() : null;

			if (work is null)
				return;

			try
			{
				await work();
			}
			catch (Exception ex)
			{
				Serilog.Log.Logger.Error(ex, "Error applying a queue decision");
			}
		}
	}

	private void AddBadBookPause(LiberationJob book)
	{
		PendingDecision? pause = null;
		var cancelled = false;

		lock (_gate)
		{
			if (_aborted)
				cancelled = true;
			else if (BadBookSession.Override is { } action)
				_settledWork.Enqueue(() => ApplyBadBookAsync(book, new BadBookDecision(action, true, false)));
			else
			{
				pause = new PendingDecision(NewPauseId(), PauseReason.BadBook, book.Id, BadBookOnly);
				_pending.Add(new PauseEntry(pause));
			}
		}

		if (cancelled)
		{
			book.Result = ProcessBookResult.Cancelled;
			book.Status = ProcessBookStatus.Cancelled;
			Jobs.MarkCompleted(book);
			JobEnded?.Invoke(this, book);
		}
		else if (pause is not null)
			QueuePaused?.Invoke(this, pause);

		SignalEnqueued();
	}

	/// <summary>Once per run per kind, so several books hitting the same wall raise one pause.</summary>
	private void AddGuidancePause(PauseReason reason, LiberationJob book)
	{
		PendingDecision pause;
		lock (_gate)
		{
			if (_aborted || !_shownThisRun.Add(reason))
				return;
			pause = new PendingDecision(NewPauseId(), reason, book.Id, ContinueOrStop);
			_pending.Add(new PauseEntry(pause));
		}

		QueuePaused?.Invoke(this, pause);
	}

	private IReadOnlyList<LiberationJob> DrainUnansweredBooks()
	{
		lock (_gate)
		{
			var books = _unanswered.ToList();
			_unanswered.Clear();
			return books;
		}
	}

	/// <summary>Drops every hold; books still waiting on an answer are collected for <see cref="DrainUnansweredBooks"/>.</summary>
	private void ClearHolds()
	{
		List<PauseResolved> dropped;
		QueueWait? endedWait;

		lock (_gate)
		{
			dropped = _pending.Select(e => new PauseResolved(e.Info.Id, Decision.Stop)).ToList();
			foreach (var entry in _pending)
				if (entry.Info.Reason == PauseReason.BadBook && entry.Info.Job is { } id && FindJob(id) is { } job)
					_unanswered.Add(job);
			_pending.Clear();
			_diskFullVictims.Clear();
			_diskFullEpisode = false;
			endedWait = TakeWaitLocked();
		}

		if (endedWait is not null)
			QueueWaitEnded?.Invoke(this, endedWait);
		foreach (var r in dropped)
			QueueResumed?.Invoke(this, r);
	}

	private QueueWait? TakeWaitLocked()
	{
		var wait = _waiting;
		_waiting = null;
		if (wait?.Reason == WaitReason.DiskFull)
			_diskFullEpisode = false;
		return wait;
	}

	private void BeginWait(QueueWait wait)
	{
		lock (_gate)
			_waiting = wait;
		QueueWaiting?.Invoke(this, wait);
	}

	private void EndWait(WaitReason reason)
	{
		QueueWait? ended = null;
		lock (_gate)
		{
			if (_waiting?.Reason == reason)
				ended = TakeWaitLocked();
		}

		if (ended is not null)
			QueueWaitEnded?.Invoke(this, ended);
	}

	private bool TakeDiskFullVictim(LiberationJob book)
	{
		lock (_gate)
			return _diskFullVictims.Remove(book);
	}

	/// <summary>Cancels what is in flight but keeps the queue; every cancelled book goes back to the start.</summary>
	private async Task HandleDiskFullAsync(LiberationJob book)
	{
		Serilog.Log.Logger.Warning("Disk full while processing {libraryBook}", book.LibraryBook.LogFriendly());

		if (_policy.OnDiskFull == DiskFullPolicy.Stop)
		{
			Jobs.MarkCompleted(book);
			await TeardownAsync(book);
			JobEnded?.Invoke(this, book);
			return;
		}

		if (TakeDiskFullVictim(book))
		{
			Requeue(book);
			return;
		}

		PendingDecision? pause = null;
		QueueWait? wait = null;
		List<LiberationJob> others = [];
		var stopped = false;

		lock (_gate)
		{
			stopped = _aborted;
			if (!stopped && !_diskFullEpisode)
			{
				_diskFullEpisode = true;
				others = Jobs.GetActive()
					.Where(b => b != book && !_pending.Any(p => p.Info.Job == b.Id))
					.ToList();
				foreach (var other in others)
					_diskFullVictims.Add(other);

				if (_policy.OnDiskFull == DiskFullPolicy.Ask)
				{
					pause = new PendingDecision(NewPauseId(), PauseReason.DiskFull, book.Id, ResumeOrStop);
					_pending.Add(new PauseEntry(pause));
				}
				else
				{
					var now = Clock.GetUtcNow();
					wait = new QueueWait(NewPauseId(), WaitReason.DiskFull, now, now, book.Id, null, null, ResumeOrStop);
					_waiting = wait;
					_probeFailures = 0;
				}
			}
		}

		if (stopped)
		{
			book.Result = ProcessBookResult.Cancelled;
			book.Status = ProcessBookStatus.Cancelled;
			Jobs.MarkCompleted(book);
			JobEnded?.Invoke(this, book);
			return;
		}

		if (pause is not null)
			QueuePaused?.Invoke(this, pause);
		if (wait is not null)
			QueueWaiting?.Invoke(this, wait);

		await Task.WhenAll(others.Select(CancelBookAsync));
		Requeue(book);
	}

	private static async Task CancelBookAsync(LiberationJob book)
	{
		try
		{
			await book.CancelAsync();
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Error(ex, "Error while cancelling {Book}", book.LibraryBook.LogFriendly());
		}
	}

	/// <summary>True when the loop waited and should look again; false when dispatch may go ahead.</summary>
	private async Task<bool> HoldForSpaceAsync(Task wake)
	{
		QueueWait wait;
		lock (_gate)
		{
			if (_waiting is not { Reason: WaitReason.DiskFull } current)
				return false;
			wait = current;
		}

		var now = Clock.GetUtcNow();
		TimeSpan delay;

		if (wait.NextCheck is { } due && now < due)
			delay = due - now;
		else
		{
			var volumes = SpaceProbe();
			var known = volumes?.Where(v => v.FreeBytes is not null).ToList();

			if (known is { Count: > 0 } && known.All(v => v.FreeBytes >= v.RequiredBytes))
			{
				EndWait(WaitReason.DiskFull);
				return false;
			}

			delay = volumes is null
				? DiskProbeBackoff[Math.Min(_probeFailures++, DiskProbeBackoff.Length - 1)]
				: DiskPollInterval;
			if (volumes is not null)
				_probeFailures = 0;

			lock (_gate)
				if (_waiting?.Id == wait.Id)
					_waiting = wait with { Volumes = volumes, NextCheck = now + delay };
		}

		using var cts = new CancellationTokenSource();
		await Task.WhenAny(Task.Delay(delay, Clock, cts.Token), wake);
		cts.Cancel();
		return true;
	}
}
