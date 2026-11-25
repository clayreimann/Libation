using ApplicationServices;
using DataLayer;
using FileLiberator;
using LibationEngine;
using LibationFileManager;
using LibationUiBase.Forms;
using LibationUiBase;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LibationUiBase.ProcessQueue;

public record LogEntry(DateTime LogDate, string LogMessage)
{
	public string LogDateString => LogDate.ToShortTimeString();
}

public class ProcessQueueViewModel : ReactiveObject
{
	public ObservableCollection<LogEntry> LogEntries { get; } = new();
	private readonly LiberationQueue _engine;
	public ProcessBookQueue Queue { get; }

	/// <summary>Internal so a test can stand in for a user having answered the bad book dialog.</summary>
	internal BadBookSessionContext BadBookSession => _engine.BadBookSession;
	internal LiberationQueue Engine => _engine;
	public Task? QueueRunner => _engine.Runner;
	public bool Running => _engine.Running;

	/// <summary>The single call the dispatch loop makes into a book; replaced in tests by a fake that finishes on command.</summary>
	internal Func<ProcessBookViewModel, Task<ProcessBookResult>> ProcessBookHandler { get; set; }
		= book => book.ProcessOneAsync();

	internal TimeProvider Clock { get => _engine.Clock; set => _engine.Clock = value; }

	internal Func<DateTimeOffset, IReadOnlyList<DownloadHistoryEntry>> DownloadHistory
	{
		get => _engine.DownloadHistory;
		set => _engine.DownloadHistory = value;
	}

	private Timer? _runningTimeTimer;
	private readonly GuiQueuePrompter _prompter;

	public ProcessQueueViewModel()
	{
		_engine = new LiberationQueue(new LiberationPolicy { MaxConcurrentDownloads = () => MaxConcurrentDownloads, OnDiskFull = DiskFullPolicy.Ask });
		_prompter = new GuiQueuePrompter(_engine);
		Queue = new ProcessBookQueue(_engine.Jobs);
		_engine.RunJob = job => ProcessBookHandler(Queue.For(job));

		// The queue is mutated from book threads and read by index from the UI thread, so its
		// notifications have to arrive on the UI thread in the order the queue actually changed.
		// alwaysInvoke: true is the part that matters - it makes BeginInvoke post unconditionally.
		// A plain invoker runs inline when it is already on the UI thread, which would let a
		// UI-thread mutation deliver ahead of notifications a book thread posted earlier.
		// Null context means nobody is bound to this queue anyway; delivery stays inline.
		if (SynchronizationContext.Current is not null)
			Queue.NotificationInvoker = new Dinah.Core.Threading.SynchronizeInvoker(alwaysInvoke: true);

		Queue.QueuedCountChanged += Queue_QueuedCountChanged;
		Queue.CompletedCountChanged += Queue_CompletedCountChanged;
		_engine.LogWritten += (_, message) => AddQueueLogEntry(message);
		_engine.JobStarted += (_, job) => ProcessStart?.Invoke(this, Queue.For(job));
		_engine.JobEnded += (_, job) => ProcessEnd?.Invoke(this, Queue.For(job));
		_engine.RunStarted += Engine_RunStarted;
		_engine.RunCompleted += Engine_RunCompleted;
		_engine.RunEnded += Engine_RunEnded;

		SpeedLimit = Configuration.Instance.DownloadSpeedLimit / 1024m / 1024;
		// Assigned to the field, not through the property: constructing the view model must not write
		// the setting back, or opening the queue on a smaller machine would overwrite what the user chose.
		_maxConcurrentDownloads = Configuration.Instance.MaxConcurrentDownloads;
		AutoScrollQueue = Configuration.Instance.AutoScrollQueue;
	}

	private void Engine_RunStarted(object? sender, EventArgs e)
	{
		RunningTime = string.Empty;
		ProgressBarVisible = true;
		var startingTime = DateTime.Now;
		_runningTimeTimer = new Timer(_ => RunningTime = timeToStr(DateTime.Now - startingTime), null, 0, 500);

		static string timeToStr(TimeSpan time)
			=> time.TotalHours < 1 ? $"{time:mm\\:ss}"
			: $"{time.TotalHours:F0}:{time:mm\\:ss}";
	}

	private void Engine_RunCompleted(object? sender, EventArgs e)
	{
		Queue_CompletedCountChanged(this, 0);
		ProgressBarVisible = false;
	}

	private void Engine_RunEnded(object? sender, EventArgs e)
	{
		Interlocked.Exchange(ref _runningTimeTimer, null)?.Dispose();
		DiskSpaceBackupPreflight.ResetBulkPreflightForQueueRun();
	}

	public int CompletedCount { get => field; private set { RaiseAndSetIfChanged(ref field, value); RaisePropertyChanged(nameof(AnyCompleted)); } }
	public int QueuedCount { get => field; private set { this.RaiseAndSetIfChanged(ref field, value); RaisePropertyChanged(nameof(AnyQueued)); } }
	public int ErrorCount { get => field; private set { RaiseAndSetIfChanged(ref field, value); RaisePropertyChanged(nameof(AnyErrors)); } }
	/// <summary>
	/// How many books download and decrypt at once. <see cref="Configuration.MinConcurrentDownloads"/>
	/// means one at a time, which is how Libation behaved before parallel downloads existed - so this
	/// single value is both the limit and the off switch, and the two can never disagree.
	/// </summary>
	public int MaxConcurrentDownloads
	{
		get => _maxConcurrentDownloads;
		set
		{
			var clamped = Math.Clamp(value, Configuration.MinConcurrentDownloads, Configuration.ConcurrentDownloadsHardLimit);
			RaiseAndSetIfChanged(ref _maxConcurrentDownloads, clamped);
			Configuration.Instance.MaxConcurrentDownloads = clamped;
			// The bound is constant now; what changes with the setting is whether this machine can
			// keep up with it.
			RaisePropertyChanged(nameof(ConcurrencyHint));
		}
	}
	private int _maxConcurrentDownloads;


	/// <summary>
	/// What this machine can usefully manage. Overridable so that tests of the dispatch loop pin it
	/// instead of inheriting the host's core count.
	/// </summary>
	internal int? MachineCeilingOverride { get => _engine.MachineCeilingOverride; set => _engine.MachineCeilingOverride = value; }

	/// <summary>
	/// How many books actually run at once: what the user asked for, held down to what this machine
	/// can usefully manage. Applied at the point of use, so the stored setting is left alone.
	/// </summary>
	private int EffectiveConcurrentDownloads => _engine.EffectiveConcurrentDownloads;
	public bool AutoScrollQueue { get => field; set { RaiseAndSetIfChanged(ref field, value); Configuration.Instance.AutoScrollQueue = value; } }

	/// <summary>Exposed so UI controls can bind their spinner bounds rather than hardcoding them.</summary>
	public int MinConcurrentDownloads => Configuration.MinConcurrentDownloads;

	/// <summary>
	/// The spinner's upper bound: the flat hard limit, so both UIs agree and the bound is the same
	/// number on every machine. Bounding it by machine capability instead makes the control lie in two
	/// directions at once - a spinner whose maximum sits under the stored value coerces its display
	/// down and, being two-way, writes that back, so opening the panel on a smaller machine would
	/// overwrite an 8 chosen on a larger one; while raising the bound to meet the stored value leaves
	/// a stored 8 showing 8 on a two-core box that will only ever run 2. Capability is applied where
	/// it actually bites, in <see cref="EffectiveConcurrentDownloads"/>, and <see cref="ConcurrencyHint"/>
	/// says so on screen rather than leaving the two to disagree in silence.
	/// </summary>
	public int MaxAllowedConcurrentDownloads => Configuration.ConcurrentDownloadsHardLimit;

	/// <summary>
	/// Sits beside the spinner when this machine cannot deliver the number the user chose, and is
	/// null when it can. Closes the gap left by bounding the control at the hard limit: the setting
	/// keeps saying what was asked for, and this says what will happen.
	/// </summary>
	/// <remarks>
	/// Kept short on purpose. It lives in whatever width is left beside the spinners on a 400px
	/// pane, and the longer wording it replaced ("on this machine") fit on some machines and
	/// ellipsed to "(4 on this machi..." on others - which left it existing only in the tooltip on
	/// exactly the machines it was written for. The reason it is smaller than the setting is what
	/// the tooltip is for; the number is what has to be legible.
	/// </remarks>
	public string? ConcurrencyHint
		=> EffectiveConcurrentDownloads < MaxConcurrentDownloads
			? $"({EffectiveConcurrentDownloads} at a time)"
			: null;
	public string? RunningTime { get => field; set => RaiseAndSetIfChanged(ref field, value); }
	public bool ProgressBarVisible { get => field; set => RaiseAndSetIfChanged(ref field, value); }
	public bool AnyCompleted => CompletedCount > 0;
	public bool AnyQueued => QueuedCount > 0;
	public bool AnyErrors => ErrorCount > 0;
	public double Progress => 100d * Queue.Completed.Count / Queue.Count;
	public decimal SpeedLimitIncrement { get; private set; }

	private decimal _speedLimit;
	/// <summary>
	/// The sequential loop also re-read this from each book as that book started
	/// (<c>SpeedLimit = nextBook.Configuration.DownloadSpeedLimit / 1024m / 1024</c>), which kept the
	/// displayed number in step when only one book could be downloading. With several running there is
	/// no single book to read it back from, and doing so would have whichever book happened to start
	/// last overwrite what the user had just typed. The setter is now the only writer: it stores the
	/// value once and pushes it out to every active book.
	/// </summary>
	public decimal SpeedLimit
	{
		get => _speedLimit;
		set
		{
			var newValue = Math.Min(999 * 1024 * 1024, (long)Math.Ceiling(value * 1024 * 1024));
			var config = Configuration.Instance;
			config.DownloadSpeedLimit = newValue;

			_speedLimit
				= config.DownloadSpeedLimit <= newValue ? value
				: value == 0.01m ? config.DownloadSpeedLimit / 1024m / 1024
				: 0;

			config.DownloadSpeedLimit = (long)(_speedLimit * 1024 * 1024);
			// Over a copy inside the engine, because the speed limit is changed from the UI thread while
			// book tasks start and finish.
			_engine.SetSpeedLimit(config.DownloadSpeedLimit);

			SpeedLimitIncrement = _speedLimit > 100 ? 10
				: _speedLimit > 10 ? 1
				: _speedLimit > 1 ? 0.1m
				: 0.01m;

			RaisePropertyChanged(nameof(SpeedLimitIncrement));
			RaisePropertyChanged(nameof(SpeedLimit));
		}
	}

	private void Queue_CompletedCountChanged(object? sender, int e)
	{
		var errCount = Queue.Completed.Count(p => p.Result
			is ProcessBookResult.FailedAbort
			or ProcessBookResult.FailedSkip
			or ProcessBookResult.FailedRetry
			or ProcessBookResult.ValidationFail
			or ProcessBookResult.WidevineRecommended
			or ProcessBookResult.DiskFull);
		var completeCount = Queue.Completed.Count(p => p.Result is ProcessBookResult.Success);

		ErrorCount = errCount;
		CompletedCount = completeCount;
		RaisePropertyChanged(nameof(Progress));
	}

	private void Queue_QueuedCountChanged(object? sender, int cueCount)
	{
		QueuedCount = cueCount;
		RaisePropertyChanged(nameof(Progress));
	}

	private void ProcessBook_LogWritten(object? sender, string logMessage) => AddQueueLogEntry(logMessage);

	private void AddQueueLogEntry(string logMessage)
		=> Invoke(() => LogEntries.Add(new(DateTime.Now, logMessage.Trim())));

	#region Add Books to Queue

	public async Task<bool> QueueDownloadPdfAsync(IList<LibraryBook> libraryBooks, Configuration? config = null)
	{
		config ??= Configuration.Instance;
		if (!await IsBooksDirectoryValidAsync(config))
			return false;

		var needsPdf = libraryBooks.Where(lb => lb.NeedsPdfDownload).ToArray();
		if (needsPdf.Length > 0)
		{
			Serilog.Log.Logger.Information("Begin download {count} pdfs", needsPdf.Length);
			AddDownloadPdf(needsPdf, config);
			return true;
		}
		return false;
	}

	public async Task<bool> QueueConvertToMp3Async(IList<LibraryBook> libraryBooks, Configuration? config = null)
	{
		config ??= Configuration.Instance;
		if (!await IsBooksDirectoryValidAsync(config))
			return false;

		//Only Queue Liberated books for conversion.  This isn't a perfect filter, but it's better than nothing.
		var preLiberated = libraryBooks.Where(lb => !lb.AbsentFromLastScan && lb.Book.UserDefinedItem.BookStatus is LiberatedStatus.Liberated && lb.Book.ContentType is DataLayer.ContentType.Product).ToArray();
		if (preLiberated.Length > 0)
		{
			if (preLiberated.Length == 1)
				RemoveCompleted(preLiberated[0]);
			Serilog.Log.Logger.Information("Begin convert {count} books to mp3", preLiberated.Length);
			AddConvertMp3(preLiberated, config);
			return true;
		}
		return false;
	}

	/// <summary>
	/// Queues visible books with an instant simulated failure for testing the bad-book error dialog.
	/// Does not download or modify files.
	/// </summary>
	public void QueueSimulatedBadBookFailures(IList<LibraryBook> libraryBooks, Configuration? config = null, int maxBooks = 5)
	{
		config ??= Configuration.Instance;
		if (libraryBooks.Count == 0)
			return;

		RunOnQueueUiThread(() => addSimulatedBadBookFailuresCore(libraryBooks, config, maxBooks));
	}

	private void addSimulatedBadBookFailuresCore(IList<LibraryBook> libraryBooks, Configuration config, int maxBooks)
	{
		var procs = libraryBooks
			.Where(e => !IsBookInQueue(e))
			.Take(maxBooks)
			.Select(entry => new ProcessBookViewModel(entry, config, _engine.BadBookSession).AddSimulateBadBookFailure())
			.ToArray();

		if (procs.Length == 0)
			return;

		Serilog.Log.Logger.Information("Queueing {count} books for simulated bad-book failure testing", procs.Length);
		AddToQueue(procs);
	}

	/// <param name="notifyIfNothingQueued">
	/// Whether to tell the user when a multi-book request queued nothing. Always logged either way. Automated
	/// callers (auto-download after a scan) pass false so a routine no-op cannot put a dialog on screen.
	/// </param>
	public async Task<bool> QueueDownloadDecryptAsync(IList<LibraryBook> libraryBooks, Configuration? config = null, bool notifyIfNothingQueued = true)
	{
		config ??= Configuration.Instance;
		if (!await IsBooksDirectoryValidAsync(config))
			return false;

		if (libraryBooks.Count == 1)
		{
			var item = libraryBooks[0];

			if (item.AbsentFromLastScan)
			{
				Serilog.Log.Logger.Warning("Download not queued: {libraryBook} is absent from the last library scan.", item.LogFriendly());
				await MessageBoxBase.Show(
					"This title is marked absent from your last library scan.\n\nRun Scan (or `libationcli scan`) so Libation can refresh your library, then try again.",
					"Library scan required",
					MessageBoxButtons.OK,
					MessageBoxIcon.Information);
				return false;
			}
			if (item.NeedsBookDownload)
			{
				RemoveCompleted(item);
				Serilog.Log.Logger.Information("Begin single library book backup of {libraryBook}", item);
				AddDownloadDecrypt([item], config);
				return true;
			}
			if (item.NeedsPdfDownload)
			{
				RemoveCompleted(item);
				Serilog.Log.Logger.Information("Begin single pdf backup of {libraryBook}", item);
				AddDownloadPdf([item], config);
				return true;
			}

			Serilog.Log.Logger.Warning(
				"Download not queued: single-item backup not applicable for {libraryBook} (book status or type does not request download).",
				item.LogFriendly());
			if (!item.Book.AudioExists)
			{
				await MessageBoxBase.Show(
					"Libation could not queue a download for this title.\n\n"
					+ "If it should be downloadable: confirm it is not already liberated, try marking it \"Download Pending\", or check whether a library scan is required.",
					"Download not queued",
					MessageBoxButtons.OK,
					MessageBoxIcon.Information);
			}
			return false;
		}
		else
		{
			// Titles Audible recently refused are left out of a multi-book request but never out of a
			// single-title one: picking one title is the user overriding the wait.
			var request = BackupRequest.Create(libraryBooks, DownloadDeferrals.Load(DateTimeOffset.Now));

			if (request.Queueable.Length == 0)
			{
				// This branch used to return with no log entry and no message, so a request Libation had
				// understood and declined was indistinguishable from a dead button.
				Serilog.Log.Logger.Information(
					"Download not queued: none of the {requested} requested titles need downloading. Skipped: {skipped}",
					request.RequestedCount,
					request.BuildSkippedLogSummary());

				if (notifyIfNothingQueued)
					await MessageBoxBase.Show(
						request.BuildNothingQueuedBody(),
						BackupRequest.NothingQueuedCaption,
						MessageBoxButtons.OK,
						MessageBoxIcon.Information);

				return false;
			}

			if (request.SkippedCount > 0)
				Serilog.Log.Logger.Information(
					"Skipping {skippedCount} of {requested} requested titles. Skipped: {skipped}",
					request.SkippedCount,
					request.RequestedCount,
					request.BuildSkippedLogSummary());

			if (request.Deferred.Count > 0)
				AddQueueLogEntry(request.BuildDeferredDetail(DateTimeOffset.Now));

			// May no-op when free space is unknown (common on UNC); see DiskSpaceBackupPreflight.
			if (!await DiskSpaceBackupPreflight.ConfirmBulkBackupAsync(request.Queueable.Length, config, backupQueueAlreadyRunning: Running))
				return false;

			Serilog.Log.Logger.Information("Begin backup of {count} library books", request.Queueable.Length);
			AddDownloadDecrypt(request.Queueable, config);
			return true;
		}
	}

	private async Task<bool> IsBooksDirectoryValidAsync(Configuration config)
	{
		if (string.IsNullOrWhiteSpace(config.Books?.Path))
		{
			Serilog.Log.Logger.Error("Books location is not set in configuration.");
			await MessageBoxBase.Show(
				"Please choose a \"Books location\" folder in the Settings menu.",
				"Books Directory Not Set",
				MessageBoxButtons.OK,
				MessageBoxIcon.Error);
			return false;
		}
		else if (AudibleFileStorage.BooksDirectory is not FileManager.LongPath booksDirectory)
		{
			Serilog.Log.Logger.Error("Failed to create books directory: {booksDir}", config.Books?.Path);
			await MessageBoxBase.Show(
				$"Libation was unable to create the \"Books location\" folder at:\n{config.Books}\n\nPlease change the Books location in the settings menu.",
				"Failed to Create Books Directory",
				MessageBoxButtons.OK,
				MessageBoxIcon.Error);
			return false;
		}
		// A removable or failing drive can still answer that it is a directory while every read of it fails, so
		// existing is not the same as usable. Without this the download would begin against an unreadable folder
		// and fail book by book, and Libation would report the library as having nothing downloaded.
		else if (!FileManager.FileUtility.CanEnumerate(booksDirectory))
		{
			Serilog.Log.Logger.Error("Books directory exists but cannot be read: {booksDir}", (string)booksDirectory);
			await MessageBoxBase.Show(
				$"Libation was unable to read the \"Books location\" folder at:\n{booksDirectory}\n\nIf it is on a removable or network drive, check that the drive is connected and working. Otherwise, change the Books location in the settings menu.",
				"Unable to Read Books Directory",
				MessageBoxButtons.OK,
				MessageBoxIcon.Error);
			return false;
		}
		else if (AudibleFileStorage.DownloadsInProgressDirectory is null)
		{
			Serilog.Log.Logger.Error("Failed to create DownloadsInProgressDirectory in {InProgress}", config.InProgress);
			await MessageBoxBase.Show(
				$"Libation was unable to create the \"Downloads In Progress\" folder in:\n{config.InProgress}\n\nPlease change the In Progress location in the settings menu.",
				"Failed to Create Downloads In Progress Directory",
				MessageBoxButtons.OK,
				MessageBoxIcon.Error);
			return false;
		}
		else if (AudibleFileStorage.DecryptInProgressDirectory is null)
		{
			Serilog.Log.Logger.Error("Failed to create DecryptInProgressDirectory in {InProgress}", config.InProgress);
			await MessageBoxBase.Show(
				$"Libation was unable to create the \"Decrypt In Progress\" folder in:\n{config.InProgress}\n\nPlease change the In Progress location in the settings menu.",
				"Failed to Create Decrypt In Progress Directory",
				MessageBoxButtons.OK,
				MessageBoxIcon.Error);
			return false;
		}

		return true;
	}

	private bool IsBookInQueue(LibraryBook libraryBook)
		=> Queue.FirstOrDefault(b => b?.LibraryBook?.Book?.AudibleProductId == libraryBook.Book.AudibleProductId) is not ProcessBookViewModel entry ? false
		: entry.Status is ProcessBookStatus.Cancelled or ProcessBookStatus.Failed ? !Queue.RemoveCompleted(entry)
		: true;

	private bool RemoveCompleted(LibraryBook libraryBook)
		=> Queue.FirstOrDefault(b => b?.LibraryBook?.Book?.AudibleProductId == libraryBook.Book.AudibleProductId) is ProcessBookViewModel entry
		&& entry.Status is ProcessBookStatus.Completed
		&& Queue.RemoveCompleted(entry);

	/// <summary>
	/// ProcessBookViewModel requires a captured UI SynchronizationContext. Callers may resume on a
	/// thread-pool thread after await (e.g. auto-download after BackgroundWorker).
	/// </summary>
	private void RunOnQueueUiThread(Action action) => Invoke(action);

	private void AddDownloadPdf(IList<LibraryBook> entries, Configuration config)
		=> RunOnQueueUiThread(() => addDownloadPdfCore(entries, config));

	private void addDownloadPdfCore(IList<LibraryBook> entries, Configuration config)
	{
		var procs = entries.Where(e => !IsBookInQueue(e)).Select(Create).ToArray();
		Serilog.Log.Logger.Information("Queueing {count} books for PDF-only download", procs.Length);
		AddToQueue(procs);

		ProcessBookViewModel Create(LibraryBook entry)
			=> new ProcessBookViewModel(entry, config, _engine.BadBookSession).AddDownloadPdf();
	}

	private void AddDownloadDecrypt(IList<LibraryBook> entries, Configuration config)
		=> RunOnQueueUiThread(() => addDownloadDecryptCore(entries, config));

	private void addDownloadDecryptCore(IList<LibraryBook> entries, Configuration config)
	{
		var procs = entries.Where(e => !IsBookInQueue(e)).Select(Create).ToArray();
		Serilog.Log.Logger.Information("Queueing {count} books for download/decrypt", procs.Length);
		if (procs.Length < entries.Count)
			Serilog.Log.Logger.Information("{count} of the requested books are already in the queue and were not added again", entries.Count - procs.Length);
		AddToQueue(procs);

		ProcessBookViewModel Create(LibraryBook entry)
			=> new ProcessBookViewModel(entry, config, _engine.BadBookSession).AddDownloadDecryptBook().AddDownloadPdf().AddUploadToAudiobookshelf();
	}

	private void AddConvertMp3(IList<LibraryBook> entries, Configuration config)
		=> RunOnQueueUiThread(() => addConvertMp3Core(entries, config));

	private void addConvertMp3Core(IList<LibraryBook> entries, Configuration config)
	{
		var procs = entries.Where(e => !IsBookInQueue(e)).Select(Create).ToArray();
		Serilog.Log.Logger.Information("Queueing {count} books for mp3 conversion", procs.Length);
		AddToQueue(procs);

		ProcessBookViewModel Create(LibraryBook entry)
			=> new ProcessBookViewModel(entry, config, _engine.BadBookSession).AddConvertToMp3();
	}

	/// <summary>
	/// Internal rather than private so the dispatch loop can be started from a test with books it
	/// controls, without going through the queueing dialogs on the way in.
	/// </summary>
	/// <summary>
	/// Internal rather than private so the dispatch loop can be started from a test with books it
	/// controls, without going through the queueing dialogs on the way in.
	/// </summary>
	internal void AddToQueue(IList<ProcessBookViewModel> pbook)
	{
		foreach (var book in pbook)
			book.LogWritten += ProcessBook_LogWritten;

		Queue.Enqueue(pbook);
		_engine.StartOrWake();
	}

	#endregion

	public event EventHandler<ProcessBookViewModel>? ProcessStart;
	public event EventHandler<ProcessBookViewModel>? ProcessEnd;

	/// <summary>
	/// Clears the queue and cancels every book currently downloading, including one held at the daily
	/// download limit - which is why both UIs call this instead of manipulating the queue directly.
	/// </summary>
	/// <param name="except">A book calling this from its own completion path; it must not cancel itself.</param>
	public Task CancelAllAsync(ProcessBookViewModel? except = null)
		=> _engine.CancelAllAsync(except?.Job);
}
