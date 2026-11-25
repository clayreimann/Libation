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

/// <summary>Dispatches queued <see cref="LiberationJob"/>s, up to the concurrency limit, until the queue drains.</summary>
public sealed partial class LiberationQueue
{
	// Short and fixed so a changed setting or a rolled-over window is picked up promptly.
	private static readonly TimeSpan DailyLimitPollInterval = TimeSpan.FromSeconds(15);

	private readonly LiberationPolicy _policy;
	private int _queuedCount;
	private bool _aborted;
	private bool _announcedSkip;

	public TrackedQueue<LiberationJob> Jobs { get; } = new();
	public BadBookSessionContext BadBookSession { get; } = new();
	public Task? Runner { get; private set; }
	public bool Running => !Runner?.IsCompleted ?? false;

	/// <summary>Raised when the queue itself has something for the log, e.g. a daily-limit pause.</summary>
	public event EventHandler<string>? LogWritten;
	public event EventHandler<LiberationJob>? JobStarted;
	public event EventHandler<LiberationJob>? JobEnded;
	public event EventHandler? RunStarted;
	/// <summary>Only on the success path, after every book task has finished.</summary>
	public event EventHandler? RunCompleted;
	/// <summary>Always, from the run's finally.</summary>
	public event EventHandler? RunEnded;

	/// <summary>The single call the dispatch loop makes into a book; replaced by a fake in tests.</summary>
	internal Func<LiberationJob, Task<ProcessBookResult>> RunJob { get; set; } = job => job.ProcessOneAsync();

	/// <summary>Asked before each book starts; a message ends the run, dropping this book and every one still queued. The owner reports it.</summary>
	public Func<LiberationJob, string?>? StopBeforeDispatch { get; set; }

	internal TimeProvider Clock { get; set; } = TimeProvider.System;

	internal Func<DateTimeOffset, IReadOnlyList<DownloadHistoryEntry>> DownloadHistory { get; set; }
		= DownloadHistoryStore.GetCurrentWindow;

	/// <summary>Pinned by tests so they do not inherit the host's core count.</summary>
	internal int? MachineCeilingOverride { get; set; }

	/// <summary>What the user asked for, held down to what this machine can usefully manage.</summary>
	internal int EffectiveConcurrentDownloads
		=> Math.Clamp(
			_policy.MaxConcurrentDownloads(),
			Configuration.MinConcurrentDownloads,
			MachineCeilingOverride ?? Configuration.MaxAllowedConcurrentDownloads);

	/// <summary>
	/// The queued count as last delivered through <see cref="TrackedQueue{T}.NotificationInvoker"/>, not the
	/// live one - the rotation bound below is read from that posted mirror.
	/// </summary>
	private int QueuedCount => Volatile.Read(ref _queuedCount);

	public LiberationQueue(LiberationPolicy policy)
	{
		_policy = policy;
		Jobs.QueuedCountChanged += (_, count) => Volatile.Write(ref _queuedCount, count);
	}

	/// <summary>Wakes a running loop for newly enqueued jobs, or starts one.</summary>
	public void StartOrWake()
	{
		SignalEnqueued();
		if (!Running)
			Runner = Task.Run(QueueLoop);
	}

	/// <summary>Applies a new speed limit to every book already running.</summary>
	public void SetSpeedLimit(long bytesPerSecond)
	{
		// Over a copy: books start and finish on other threads while the UI changes the limit.
		foreach (var activeJob in Jobs.GetActive())
			activeJob.Configuration.DownloadSpeedLimit = bytesPerSecond;
	}

	public LiberationJob? FindJob(JobId id) => Jobs.FirstOrDefault(j => j is not null && j.Id == id);

	private TaskCompletionSource _enqueueSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

	private Task WaitForEnqueueAsync() => Volatile.Read(ref _enqueueSignal).Task;

	private void SignalEnqueued()
		=> Interlocked
			.Exchange(ref _enqueueSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
			.TrySetResult();

	private void WriteLog(string message) => LogWritten?.Invoke(this, message);

	#region Daily download limit

	private enum DailyLimitGate
	{
		Proceed,
		/// <summary>Limited, but something else in the queue is not; try it later.</summary>
		Defer,
		Skip,
		Cancelled
	}

	/// <summary>
	/// Runs immediately before a book downloads, never at queueing time, so a user can raise or turn off the
	/// limit mid-run. Every iteration re-reads the setting, history and clock.
	/// </summary>
	/// <param name="deferralsSoFar">Books already moved to the back for the limit this run; bounds the rotation.</param>
	private async Task<DailyLimitGate> WaitForDailyLimitAsync(LiberationJob nextBook, int deferralsSoFar)
	{
		if (!nextBook.IncludesBookDownload)
			return DailyLimitGate.Proceed;

		var paused = false;

		try
		{
			while (true)
			{
				// Per-book, not queue-wide: Cancel All reaches a parked book through CancelAsync like any active one.
				if (nextBook.CancellationRequested)
				{
					nextBook.StatusOverride = null;
					return DailyLimitGate.Cancelled;
				}

				var now = Clock.GetLocalNow();
				var allowance = DailyDownloadLimit.Evaluate(Configuration.Instance, DownloadHistory(now), now);

				if (!allowance.Blocks(nextBook.LibraryBook.IsAudiblePlus))
				{
					nextBook.StatusOverride = null;
					if (paused)
					{
						var resumed = $"Daily download limit: capacity is available again. Resuming with {nextBook.LibraryBook.Book.TitleWithSubtitle}.";
						Serilog.Log.Logger.Information("Daily download limit no longer blocks {libraryBook}. Resuming the queue.", nextBook.LibraryBook.LogFriendly());
						WriteLog(resumed);
					}
					return DailyLimitGate.Proceed;
				}

				if (_policy.OnDailyLimit == DailyLimitPolicy.Skip)
				{
					Serilog.Log.Logger.Information(
						"Daily download limit reached; skipping {libraryBook}. {@DebugInfo}",
						nextBook.LibraryBook.LogFriendly(),
						new { allowance.Scope, allowance.Unit, allowance.Quantity, allowance.UsedBooks, allowance.UsedBytes, allowance.NextCapacityAt });
					if (!_announcedSkip)
					{
						_announcedSkip = true;
						foreach (var line in DailyDownloadLimitUserMessage.BuildCliSkippedLines(allowance))
							WriteLog(line);
					}
					return DailyLimitGate.Skip;
				}

				// Under "Plus titles only" a mixed queue can keep going; do not stall owned titles behind a Plus title.
				if (deferralsSoFar < QueuedCount && AnyOtherQueuedBookAllowed(nextBook, allowance))
				{
					nextBook.StatusOverride = null;
					Serilog.Log.Logger.Information(
						"Daily download limit blocks {libraryBook}. Moving it to the end of the queue and continuing with titles the limit does not cover.",
						nextBook.LibraryBook.LogFriendly());
					WriteLog(DailyDownloadLimitUserMessage.BuildDeferredLogEntry(allowance, nextBook.LibraryBook.Book.TitleWithSubtitle));
					return DailyLimitGate.Defer;
				}

				if (!paused)
				{
					paused = true;
					Serilog.Log.Logger.Information(
						"Daily download limit reached; pausing the queue before {libraryBook}. {@DebugInfo}",
						nextBook.LibraryBook.LogFriendly(),
						new { allowance.Scope, allowance.Unit, allowance.Quantity, allowance.UsedBooks, allowance.UsedBytes, allowance.NextCapacityAt });
					WriteLog(DailyDownloadLimitUserMessage.BuildQueueLogEntry(allowance, nextBook.LibraryBook.Book.TitleWithSubtitle));
					BeginWait(new QueueWait(
						NewPauseId(), WaitReason.DailyLimit, Clock.GetUtcNow(), allowance.NextCapacityAt,
						nextBook.Id, null, allowance, NoDecisions));
				}

				nextBook.StatusOverride = DailyDownloadLimitUserMessage.BuildWaitingStatus(allowance);

				await Task.Delay(DailyLimitPollInterval, Clock);
			}
		}
		finally
		{
			if (paused)
				EndWait(WaitReason.DailyLimit);
		}
	}

	/// <summary>Moves the book being held back to the end of the queue without counting it as completed.</summary>
	private void RequeueLast(LiberationJob book)
	{
		// By identity: with several books in flight the first active one is some other book's download.
		Jobs.RemoveActive(book);
		Jobs.Enqueue([book]);
	}

	private bool AnyOtherQueuedBookAllowed(LiberationJob nextBook, DailyDownloadLimit.Allowance allowance)
		=> Jobs.Any(b =>
			b is not null
			&& !ReferenceEquals(b, nextBook)
			&& b.Status is ProcessBookStatus.Queued
			&& (!b.IncludesBookDownload || !allowance.Blocks(b.LibraryBook.IsAudiblePlus)));

	#endregion

	/// <summary>
	/// Clears the queue and cancels every running book, including one held at the daily limit.
	/// </summary>
	/// <remarks>
	/// Each cancellation is isolated: this runs from the loop's abort and disk full paths, and one book
	/// throwing would take the loop out and leave the rest running unsupervised.
	/// </remarks>
	/// <param name="except">A book calling this from its own completion path; it must not cancel itself.</param>
	public async Task CancelAllAsync(LiberationJob? except = null)
	{
		Jobs.ClearQueue();
		ClearHolds();

		// Snapshot before cancelling: Active is mutated as each book unwinds.
		var inFlight = Jobs.GetActive().Where(b => b != except).ToArray();

		await Task.WhenAll(inFlight.Select(CancelBookAsync));

		foreach (var book in DrainUnansweredBooks())
		{
			book.Result = ProcessBookResult.Cancelled;
			book.Status = ProcessBookStatus.Cancelled;
			Jobs.MarkCompleted(book);
			JobEnded?.Invoke(this, book);
		}

		SignalEnqueued();
	}

	/// <summary>True for the one caller that gets to tear the queue down; every later one is a no-op.</summary>
	private async Task<bool> TeardownAsync(LiberationJob? except)
	{
		lock (_gate)
		{
			if (_aborted)
				return false;
			_aborted = true;
		}

		await CancelAllAsync(except);
		SignalEnqueued();
		return true;
	}

	private async Task FinishJobAsync(LiberationJob book, ProcessBookResult result)
	{
		Serilog.Log.Logger.Information("Completed processing: '{item_LibraryBook}' result: {result}", book.LibraryBook, result);

		if (result == ProcessBookResult.ValidationFail)
		{
			Jobs.RemoveActive(book);
			JobEnded?.Invoke(this, book);
			return;
		}

		if (result is ProcessBookResult.Cancelled && TakeDiskFullVictim(book))
		{
			Requeue(book);
			return;
		}

		Jobs.MarkCompleted(book);

		if (result == ProcessBookResult.FailedAbort)
		{
			if (!await TeardownAsync(book))
			{
				book.Result = ProcessBookResult.Cancelled;
				book.Status = ProcessBookStatus.Cancelled;
			}
		}
		else if (result == ProcessBookResult.FailedSkip)
		{
			await book.LibraryBook.UpdateBookStatusAsync(LiberatedStatus.Error);
		}
		else if (result == ProcessBookResult.LicenseDeniedPossibleOutage
			|| result == ProcessBookResult.LicenseDeniedThrottled
			|| result == ProcessBookResult.LicenseDeniedThrottledRecovered
			|| (result == ProcessBookResult.LicenseDenied && book.LibraryBook.IsAudiblePlus))
		{
			AddGuidancePause(PauseReason.LicenseGuidance, book);
		}
		else if (result == ProcessBookResult.WidevineRecommended)
		{
			AddGuidancePause(PauseReason.WidevineGuidance, book);
		}

		JobEnded?.Invoke(this, book);
	}

	/// <summary>Back to the start of its steps, at the end of the queue; a book that never reached completion.</summary>
	private void Requeue(LiberationJob book)
	{
		book.Rearm();
		RequeueLast(book);
		SignalEnqueued();
	}

	private async Task ProcessJobAsync(LiberationJob book)
	{
		Serilog.Log.Logger.Information("Begin processing queued item: '{item_LibraryBook}'", book.LibraryBook);
		JobStarted?.Invoke(this, book);

		var result = await RunJob(book);

		switch (result)
		{
			case ProcessBookResult.AwaitingDecision:
				AddBadBookPause(book);
				break;
			case ProcessBookResult.DiskFull:
				await HandleDiskFullAsync(book);
				break;
			default:
				await FinishJobAsync(book, result);
				break;
		}
	}

	private async Task QueueLoop()
	{
		try
		{
			Serilog.Log.Logger.Information("Begin processing queue");

			BadBookSession.Reset();
			_announcedSkip = false;
			BeginRun();
			RunStarted?.Invoke(this, EventArgs.Empty);

			var activeTasks = new HashSet<Task>();
			// Counted since the last book that actually started, so a book is never shuffled back forever.
			int consecutiveDeferrals = 0;

			while (true)
			{
				// A faulted book task is dropped here, before the closing WhenAll could rethrow it, so
				// its exception has to be observed on the way out or it is lost.
				activeTasks.RemoveWhere(t =>
				{
					if (!t.IsCompleted)
						return false;
					if (t.IsFaulted)
						Serilog.Log.Logger.Error(t.Exception, "A book failed to process and did not report a result");
					return true;
				});

				// Captured before the queue is inspected, so a book enqueued between the dequeue below and
				// the wait at the bottom completes this task and is not missed.
				var enqueued = WaitForEnqueueAsync();

				await DrainSettledWorkAsync();

				if (Volatile.Read(ref _aborted))
				{
					await Task.WhenAll(activeTasks);
					await DrainSettledWorkAsync();
					break;
				}

				// A pause or wait stops dispatch only; books already running carry on.
				if (HasPendingDecision)
				{
					await enqueued;
					continue;
				}

				if (await HoldForSpaceAsync(enqueued))
					continue;

				// If at capacity, wait for a slot to open before trying to dequeue more
				if (activeTasks.Count >= EffectiveConcurrentDownloads)
				{
					await Task.WhenAny(activeTasks);
					continue;
				}

				if (Jobs.TryDequeueNext(out var nextBook))
				{
					// In this single dispatch loop, not the book task: a per-book wait would have every blocked
					// book polling at once. Books already in flight keep running while the loop is held here.
					if (StopBeforeDispatch?.Invoke(nextBook) is { } stopMessage)
					{
						Serilog.Log.Logger.Information("{StopMessage}", stopMessage);
						Jobs.ClearQueue();
						nextBook.Result = ProcessBookResult.Cancelled;
						nextBook.Status = ProcessBookStatus.Cancelled;
						Jobs.MarkCompleted(nextBook);
						JobEnded?.Invoke(this, nextBook);
						continue;
					}

					var gate = await WaitForDailyLimitAsync(nextBook, consecutiveDeferrals);

					if (gate is DailyLimitGate.Skip)
					{
						nextBook.Result = ProcessBookResult.SkippedDailyLimit;
						nextBook.Status = ProcessBookStatus.Cancelled;
						Jobs.MarkCompleted(nextBook);
						JobEnded?.Invoke(this, nextBook);
						continue;
					}

					if (gate is DailyLimitGate.Defer)
					{
						consecutiveDeferrals++;
						RequeueLast(nextBook);
						continue;
					}

					if (gate is DailyLimitGate.Cancelled)
					{
						Serilog.Log.Logger.Information("Queue was cancelled while waiting on the daily download limit.");
						if (TakeDiskFullVictim(nextBook))
						{
							Requeue(nextBook);
							continue;
						}
						nextBook.Result = ProcessBookResult.Cancelled;
						nextBook.Status = ProcessBookStatus.Cancelled;
						// Nothing else would take it off the active list.
						Jobs.MarkCompleted(nextBook);
						continue;
					}

					// A pause raised while the book was parked at the limit holds it too.
					if (HasPendingDecision)
					{
						Jobs.RemoveActive(nextBook);
						Jobs.Enqueue([nextBook]);
						Jobs.MoveQueuePosition(nextBook, QueuePosition.First);
						continue;
					}

					consecutiveDeferrals = 0;
					activeTasks.Add(ProcessJobAsync(nextBook));
					continue;
				}

				// Queue is empty; if no tasks are running we are done - unless a book was queued
				// while we were looking, in which case go round again and pick it up.
				if (activeTasks.Count == 0)
				{
					if (enqueued.IsCompleted || !CanFinishRun())
						continue;
					break;
				}

				// Wake on a book finishing or a new one being queued, so a batch queued just after the
				// loop started fills the available slots instead of trickling in.
				await Task.WhenAny(activeTasks.Append(enqueued));
			}

			await Task.WhenAll(activeTasks);

			Serilog.Log.Logger.Information("Completed processing queue");
			RunCompleted?.Invoke(this, EventArgs.Empty);
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Error(ex, "An error was encountered while processing queued items");
		}
		finally
		{
			ClearHolds();
			RunEnded?.Invoke(this, EventArgs.Empty);
		}
	}
}
