using ApplicationServices;
using AudibleApi;
using AudibleApi.Common;
using AudibleUtilities;
using DataLayer;
using Dinah.Core;
using Dinah.Core.ErrorHandling;
using FileLiberator;
using LibationFileManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LibationEngine;

/// <summary>One book's chain of steps, run in order.</summary>
public sealed class LiberationJob
{
	public LibraryBook LibraryBook { get; }
	public Configuration Configuration { get; }
	public JobId Id { get; } = JobId.New();
	private readonly BadBookSessionContext? _badBookSession;

	public ProcessBookResult Result { get => field; set { field = value; ResultChanged?.Invoke(this, value); } }
	public ProcessBookStatus Status { get => field; set { field = value; StatusChanged?.Invoke(this, value); } }

	/// <summary>
	/// Transient status shown instead of the usual text, e.g. while the queue waits on the daily download limit.
	/// Null when the book's own state should speak for itself.
	/// </summary>
	public string? StatusOverride { get => field; set { field = value; StatusOverrideChanged?.Invoke(this, value); } }

	/// <summary>
	/// True when this queue item downloads an audiobook, so the daily download limit applies to it.
	/// PDF-only and mp3-conversion items are never limited.
	/// </summary>
	public bool IncludesBookDownload { get; private set; }

	public event EventHandler<ProcessBookResult>? ResultChanged;
	public event EventHandler<ProcessBookStatus>? StatusChanged;
	public event EventHandler<string?>? StatusOverrideChanged;
	public event EventHandler<string>? TitleChanged;
	public event EventHandler<string>? AuthorChanged;
	public event EventHandler<string>? NarratorChanged;
	public event EventHandler<int>? ProgressChanged;
	internal void ReportProgress(int percent) => ProgressChanged?.Invoke(this, percent);
	public event EventHandler<TimeSpan>? TimeRemainingChanged;
	public event EventHandler<byte[]>? CoverDiscovered;

	#region Process Queue Logging

	public event EventHandler<string>? LogWritten;
	/// <summary>The log lines a person running unattended needs to see: errors, license and disk guidance.</summary>
	public event EventHandler<string>? AttentionWritten;
	public event EventHandler<JobLogLine>? LineLogged;
	public JobLog Log { get; } = new();

	internal void Emit(JobLogLevel level, string text)
	{
		text = text.Trim();
		var line = Log.Add(level, text, DateTimeOffset.UtcNow);
		LogWritten?.Invoke(this, text);
		if (level != JobLogLevel.Info)
			AttentionWritten?.Invoke(this, text);
		LineLogged?.Invoke(this, line);
	}

	private void LogError(string? message, Exception? ex = null)
	{
		Emit(JobLogLevel.Error, message ?? "Automated backup: error");
		if (ex is not null)
			Emit(JobLogLevel.Error, "ERROR: " + ex.Message);
	}
	private void LogInfo(string text) => Emit(JobLogLevel.Info, text);
	private void LogAttention(string text) => Emit(JobLogLevel.Warning, text);

	#endregion

	private Processable CurrentProcessable => _currentProcessable ??= Processes.Dequeue().Invoke();
	private void NextProcessable() => _currentProcessable = null;
	private Processable? _currentProcessable;

	/// <summary> A series of Processable actions to perform on this book </summary>
	private Queue<Func<Processable>> Processes { get; } = new();

	/// <summary>
	/// When set, Audible throttling on a legacy or non-Widevine registration only switches settings and marks the
	/// account for a new login once the gate agrees. Null keeps the attended behaviour of recovering at once,
	/// where someone is there to sign in.
	/// </summary>
	public ThrottleRecoveryGate? ThrottleRecovery { get; set; }

	public LiberationJob(LibraryBook libraryBook, Configuration configuration, BadBookSessionContext? badBookSession = null)
	{
		LibraryBook = libraryBook;
		Configuration = configuration;
		_badBookSession = badBookSession;
	}

	public async Task<ProcessBookResult> ProcessOneAsync()
	{
		ProcessBookResult result = ProcessBookResult.None;
		try
		{
			// Run every queued step (decrypt, PDF, MP3, ...) in order so disk-full and other failures
			// from later steps are returned to the queue loop. Do not chain via Processable.Completed.
			while (Processes.Count > 0)
			{
				if (cancellationRequested)
				{
					LogInfo($"Process was cancelled - {LibraryBook.Book}");
					result = ProcessBookResult.Cancelled;
					break;
				}

				var processable = CurrentProcessable;
				result = await ExecuteProcessableAsync(processable);

				if (processable is ILicensedDownload { ObtainedLicense: { } license })
				{
					obtainedLicense = license;
					if (result is ProcessBookResult.Success)
						ThrottleRecovery?.RecordLicensed(LibraryBook.Account);
				}

				UnlinkProcessable(processable);
				NextProcessable();

				if (result is not ProcessBookResult.Success)
					break;
			}
		}
		finally
		{
			if (result == ProcessBookResult.None)
				result = GetFailureResult();

			var status = result switch
			{
				ProcessBookResult.Success => ProcessBookStatus.Completed,
				ProcessBookResult.Cancelled => ProcessBookStatus.Cancelled,
				_ => ProcessBookStatus.Failed,
			};

			Status = status;
		}

		Result = result;
		return result;
	}

	private async Task<ProcessBookResult> ExecuteProcessableAsync(Processable processable)
	{
		string procName = processable.Name;
		ProcessBookResult result = ProcessBookResult.None;
		try
		{
			// Optional steps (e.g. PDF when the book has no supplement) use Validate() to mean "does not apply".
			// Skip them instead of treating ProcessSingleAsync's "Validation failed" as a queue failure.
			if (!processable.Validate(LibraryBook))
				return ProcessBookResult.Success;

			LinkProcessable(processable);

			var statusHandler = await processable.ProcessSingleAsync(LibraryBook, validate: true);

			if (statusHandler.IsSuccess)
				result = ProcessBookResult.Success;
			else if (statusHandler.Errors.Contains("Cancelled"))
			{
				LogInfo($"{procName}:  Process was cancelled - {LibraryBook.Book}");
				result = ProcessBookResult.Cancelled;
			}
			else if (statusHandler.Errors.Contains("Validation failed"))
			{
				LogInfo($"{procName}:  Validation failed - {LibraryBook.Book}");
				result = ProcessBookResult.ValidationFail;
			}
			else
			{
				foreach (var errorMessage in statusHandler.Errors)
					LogError($"{procName}:  {errorMessage}");

				// Prefer disk-full detection over generic retry; avoids treating truncated .aaxc as a normal failure.
				if (statusHandler.Errors.Any(DiskSpaceHelper.ErrorMessageIndicatesDiskFull))
				{
					LogAttention($"{procName}:  Disk is full. Free space or change Books / In progress in Settings. - {LibraryBook.Book}");
					result = ProcessBookResult.DiskFull;
				}
			}
		}
		catch (ApiErrorException ex) when (WidevineRecommendation.ShouldRecommendWidevine(ex, Configuration))
		{
			Serilog.Log.Logger.Error(ex, "ADRM license unavailable (Sable acr:null) for {Book}", LibraryBook.LogFriendly());
			if (!ThrottleRecoveryAllowed())
				LogAttention($"{procName}:  This title needs Widevine. Libation did not change settings or ask for a new login because of one title; it will once {ThrottleRecovery!.DistinctTitles} different titles on this account need it within {ThrottleRecovery.Window.TotalHours:0} hours. - {LibraryBook.Book}");
			else if (System.IO.File.Exists(AudibleApiStorage.AccountsSettingsFile))
			{
				using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
				var account = persister.AccountsSettings.GetAccount(LibraryBook.Account, LibraryBook.Book.Locale);
				ThrottledDeviceRecovery.ApplyRecovery(persister.AccountsSettings, Configuration, account);
			}
			LogAttention($"{procName}:  {WidevineRecommendation.BuildLogSummary(LibraryBook.Book.TitleWithSubtitle, LibraryBook.Account.ToMask())}");
			result = ProcessBookResult.WidevineRecommended;
		}
		catch (ContentLicenseDeniedException ldex)
		{
			Serilog.Log.Logger.Error(ldex, "Content license was denied for {Book}", LibraryBook.LogFriendly());
			if (ldex.IsCustomerThrottled)
			{
				bool handled = false;
				if (System.IO.File.Exists(AudibleApiStorage.AccountsSettingsFile))
				{
					using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
					var account = persister.AccountsSettings.GetAccount(LibraryBook.Account, LibraryBook.Book.Locale);
					if (ThrottledDeviceRecovery.ShouldRecover(ldex, account, Configuration.Instance))
					{
						handled = true;
						if (ThrottleRecoveryAllowed())
						{
							ThrottledDeviceRecovery.ApplyRecovery(persister.AccountsSettings, Configuration.Instance, account);
							LogAttention($"{procName}:  Audible reports throttling on a legacy or non-Widevine registration. Libation updated settings to Android + Widevine and marked account '{LibraryBook.Account.ToMask()}' for re-registration. Sign in on next scan or liberate to complete setup. - {LibraryBook.Book}");
							result = ProcessBookResult.LicenseDeniedThrottledRecovered;
						}
						else
						{
							LogAttention($"{procName}:  Audible reports throttling on a legacy or non-Widevine registration for this title. Libation did not change settings or mark account '{LibraryBook.Account.ToMask()}' for re-registration because of one title; it will once {ThrottleRecovery!.DistinctTitles} different titles on this account are throttled within {ThrottleRecovery.Window.TotalHours:0} hours. - {LibraryBook.Book}");
							result = ProcessBookResult.LicenseDeniedThrottled;
						}
					}
				}

				if (!handled)
				{
					LogAttention($"{procName}:  Audible reports throttling. Try deregistering Libation devices and recreating the account first. Recovery steps: {AppScaffolding.LicenseRecoveryGuidance.DocumentationUrl}. If it persists, wait 24 to 48 hours, sometimes a few days. - {LibraryBook.Book}");
					result = ProcessBookResult.LicenseDeniedThrottled;
				}
			}
			else if (ldex.AYCL?.RejectionReason is null or RejectionReason.GenericError)
			{
				LogAttention($"{procName}:  Content license denied. A temporary interruption of service is possible. If Audible's app plays the title, try registration recovery: {AppScaffolding.LicenseRecoveryGuidance.DocumentationUrl}. Otherwise, waiting 24 to 48 hours, sometimes a few days, may help. - {LibraryBook.Book}");
				result = ProcessBookResult.LicenseDeniedPossibleOutage;
			}
			else if (LibraryBook.IsAudiblePlus)
			{
				LogAttention($"{procName}:  Content license denied for this Audible Plus catalog title. Check access in the Audible app. If it plays, try registration recovery: {AppScaffolding.LicenseRecoveryGuidance.DocumentationUrl}. Heavy Plus use can also require waiting 24 to 48 hours, sometimes a few days. - {LibraryBook.Book}");
				result = ProcessBookResult.LicenseDenied;
			}
			else
			{
				LogAttention($"{procName}:  Content license denied. Check your Audible account to see if you have access to this title. - {LibraryBook.Book}");
				result = ProcessBookResult.LicenseDenied;
			}
		}
		// HRESULT 0x80070070 / known messages from the OS when a volume is full (including many SMB shares).
		catch (Exception ex) when (DiskSpaceHelper.IsDiskFullException(ex))
		{
			Serilog.Log.Logger.Error(ex, "Disk full during {ProcName} for {{@Book}}", procName, LibraryBook.LogFriendly());
			LogAttention($"{procName}:  Disk is full. Free space or change Books / In progress in Settings. - {LibraryBook.Book}");
			result = ProcessBookResult.DiskFull;
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Error(ex, $"Unhandled exception in {procName} for {{@Book}}", LibraryBook.LogFriendly());
			LogError(procName, ex);
		}

		return result;
	}

	/// <summary>True when there is no gate, or the gate says enough titles on this account have now been refused.</summary>
	private bool ThrottleRecoveryAllowed()
		=> ThrottleRecovery?.RecordThrottle(LibraryBook.Account, LibraryBook.Book.AudibleProductId) ?? true;

	private volatile bool cancellationRequested;

	/// <summary>
	/// Whether cancellation has been asked for on this book, whether or not there was anything
	/// running to cancel at the time.
	/// </summary>
	/// <remarks>
	/// A book held at the daily download limit has been taken off the queue but has not started a
	/// step, so <see cref="CancelAsync"/> finds nothing to call and the book carries on waiting.
	/// This is how the gate learns to stop rather than resume it. Per book rather than per queue on
	/// purpose: a queue-wide flag has to be cleared again at some point, and whatever clears it can
	/// un-cancel a book that is still parked - which is the same bug in a different place.
	/// </remarks>
	public bool CancellationRequested => cancellationRequested;

	/// <summary>
	/// Cancels this book's running step, if it has one. Safe to call on a book that has already
	/// finished processing.
	/// </summary>
	/// <remarks>
	/// Reads the <see cref="_currentProcessable"/> field rather than <see cref="CurrentProcessable"/>.
	/// The property is lazy - <c>_currentProcessable ??= Processes.Dequeue().Invoke()</c> - so on a
	/// book that has run its last step the field is null and <see cref="Processes"/> is empty, and
	/// reading it throws "Queue empty.". That is not a narrow race: every book waiting in the bad
	/// book dialog is in exactly that state, because <c>ProcessOneAsync</c> reaches the dialog from
	/// its <c>finally</c> after the processable loop has drained. Reading the field also avoids
	/// dequeuing from a non-thread-safe <see cref="Queue{T}"/> on the cancelling thread while the
	/// book's own loop is reading it.
	/// </remarks>
	public async Task CancelAsync()
	{
		// Recorded before the early return, because the case where there is nothing to cancel is the
		// one that matters. See CancellationRequested.
		cancellationRequested = true;

		// Deliberately the field, not the property. See remarks.
		var processable = _currentProcessable;
		if (processable is not AudioDecodable audioDecodable)
			return;

		try
		{
			await audioDecodable.CancelAsync();
		}
		catch (Exception ex)
		{
			// Not CurrentProcessable.Name - that would throw a second time, out of the catch.
			LogError($"{audioDecodable.Name}:  Error while cancelling", ex);
		}
	}

	public LiberationJob AddDownloadPdf() => AddProcessable<DownloadPdf>();
	public LiberationJob AddDownloadDecryptBook() => AddDownloadDecryptBook(DownloadDecryptBook.Create);
	public LiberationJob AddDownloadDecryptBook(Func<Configuration, Processable> create)
	{
		IncludesBookDownload = true;
		return AddProcessable(create);
	}
	public LiberationJob AddConvertToMp3() => AddProcessable<ConvertToMp3>();
	public LiberationJob AddUploadToAudiobookshelf() => AddProcessable(config =>
	{
		var upload = UploadToAudiobookshelf.Create(config);
		upload.OutcomeDetermined += (_, e) =>
		{
			if (e.Outcome is UploadToAudiobookshelf.UploadOutcome.Failed)
				Emit(JobLogLevel.Error, e.Message);
			else if (e.Outcome is UploadToAudiobookshelf.UploadOutcome.NoFilesFound)
				Emit(JobLogLevel.Warning, e.Message);
		};
		return upload;
	});
	public LiberationJob AddPushProgressToAudiobookshelf(bool force) => AddProcessable(config =>
	{
		var push = new PushProgressToAudiobookshelf { Configuration = config, Force = force };
		push.ResultDetermined += (_, r) => Emit(r.Failed ? JobLogLevel.Error : JobLogLevel.Info, r.Pushed ? "Pushed Audible listening progress to Audiobookshelf" : r.Message);
		return push;
	});
	public LiberationJob AddSimulateBadBookFailure() => AddProcessable<SimulateBadBookFailure>();

	private LiberationJob AddProcessable<T>() where T : Processable, IProcessable<T>
		=> AddProcessable(T.Create);

	public LiberationJob AddProcessable(Func<Configuration, Processable> create)
	{
		Func<Processable> step = () =>
		{
			var processable = create(Configuration);

			// Each step is created as it is reached, so by now an earlier step has published whatever license
			// it obtained. Handing it on is what keeps a title to one license request per run.
			if (processable is ILicensedDownload licensed && obtainedLicense is not null)
				licensed.LicenseInfo = obtainedLicense;

			return processable;
		};
		_steps.Add(step);
		Processes.Enqueue(step);
		return this;
	}

	private readonly List<Func<Processable>> _steps = [];

	/// <summary>Resets a finished job to run its steps again from the start.</summary>
	internal void Rearm()
	{
		Processes.Clear();
		foreach (var step in _steps)
			Processes.Enqueue(step);
		_currentProcessable = null;
		cancellationRequested = false;
		Result = ProcessBookResult.None;
		Status = ProcessBookStatus.Queued;
	}

	/// <summary>The license an earlier step for this book obtained, for the steps that follow it to reuse.</summary>
	private DownloadOptions.LicenseInfo? obtainedLicense;

	public override string ToString() => LibraryBook.ToString();

	#region Subscribers and Unsubscribers

	private void LinkProcessable(Processable processable)
	{
		processable.Begin += Processable_Begin;
		processable.Completed += Processable_Completed;
		processable.StatusUpdate += Processable_StatusUpdate;
		processable.StreamingProgressChanged += Streamable_StreamingProgressChanged;
		processable.StreamingTimeRemaining += Streamable_StreamingTimeRemaining;

		if (processable is AudioDecodable audioDecodable)
		{
			audioDecodable.RequestCoverArt += AudioDecodable_RequestCoverArt;
			audioDecodable.TitleDiscovered += AudioDecodable_TitleDiscovered;
			audioDecodable.AuthorsDiscovered += AudioDecodable_AuthorsDiscovered;
			audioDecodable.NarratorsDiscovered += AudioDecodable_NarratorsDiscovered;
			audioDecodable.CoverImageDiscovered += AudioDecodable_CoverImageDiscovered;
		}
	}

	private void UnlinkProcessable(Processable processable)
	{
		processable.Begin -= Processable_Begin;
		processable.Completed -= Processable_Completed;
		processable.StatusUpdate -= Processable_StatusUpdate;
		processable.StreamingProgressChanged -= Streamable_StreamingProgressChanged;
		processable.StreamingTimeRemaining -= Streamable_StreamingTimeRemaining;

		if (processable is AudioDecodable audioDecodable)
		{
			audioDecodable.RequestCoverArt -= AudioDecodable_RequestCoverArt;
			audioDecodable.TitleDiscovered -= AudioDecodable_TitleDiscovered;
			audioDecodable.AuthorsDiscovered -= AudioDecodable_AuthorsDiscovered;
			audioDecodable.NarratorsDiscovered -= AudioDecodable_NarratorsDiscovered;
			audioDecodable.CoverImageDiscovered -= AudioDecodable_CoverImageDiscovered;
		}
	}

	#endregion

	#region AudioDecodable event handlers

	private void AudioDecodable_TitleDiscovered(object? sender, string title) => TitleChanged?.Invoke(this, title);
	private void AudioDecodable_AuthorsDiscovered(object? sender, string authors) => AuthorChanged?.Invoke(this, authors);
	private void AudioDecodable_NarratorsDiscovered(object? sender, string narrators) => NarratorChanged?.Invoke(this, narrators);
	private void AudioDecodable_CoverImageDiscovered(object? sender, byte[] coverArt) => CoverDiscovered?.Invoke(this, coverArt);

	private byte[]? AudioDecodable_RequestCoverArt(object? sender, EventArgs e)
	{
		var pictureId = Configuration.Instance.FileDownloadQuality == Configuration.DownloadQuality.High
		? LibraryBook.Book.PictureLarge ?? LibraryBook.Book.PictureId
		: LibraryBook.Book.PictureId;

		if (pictureId is null)
			return null;

		byte[] coverData = PictureStorage.GetPictureSynchronously(new PictureDefinition(pictureId, PictureSize.Native));
		AudioDecodable_CoverImageDiscovered(this, coverData);
		return coverData;
	}

	#endregion

	#region Streamable event handlers

	private void Streamable_StreamingTimeRemaining(object? sender, TimeSpan timeRemaining) => TimeRemainingChanged?.Invoke(this, timeRemaining);
	private void Streamable_StreamingProgressChanged(object? sender, Dinah.Core.Net.Http.DownloadProgress downloadProgress)
	{
		if (!downloadProgress.ProgressPercentage.HasValue)
			return;

		if (downloadProgress.ProgressPercentage == 0)
			TimeRemainingChanged?.Invoke(this, TimeSpan.Zero);
		else
			ProgressChanged?.Invoke(this, (int)downloadProgress.ProgressPercentage);
	}

	#endregion

	#region Processable event handlers

	private void Processable_Begin(object? sender, LibraryBook libraryBook)
	{
		Status = ProcessBookStatus.Working;

		if (sender is Processable processable)
			LogInfo($"{Environment.NewLine}{processable.Name} Step, Begin: {libraryBook.Book}");

		TitleChanged?.Invoke(this, libraryBook.Book.TitleWithSubtitle);
		AuthorChanged?.Invoke(this, libraryBook.Book.AuthorNames);
		NarratorChanged?.Invoke(this, libraryBook.Book.NarratorNames);
	}

	private void Processable_StatusUpdate(object? sender, string statusUpdate)
		=> LogInfo(statusUpdate);

	private void Processable_Completed(object? sender, LibraryBook libraryBook)
	{
		if (sender is Processable processable)
			LogInfo($"{processable.Name} Step, Completed: {libraryBook.Book}");
	}

	#endregion

	#region Failure Handler

	private ProcessBookResult GetFailureResult()
	{
		LogError($"ERROR. All books have not been processed. Book failed: {LibraryBook.Book}");

		var action = Configuration.BadBook switch
		{
			Configuration.BadBookAction.Abort => Configuration.BadBookAction.Abort,
			Configuration.BadBookAction.Retry => Configuration.BadBookAction.Retry,
			Configuration.BadBookAction.Ignore => Configuration.BadBookAction.Ignore,
			_ => _badBookSession?.Override
		};

		return action switch
		{
			Configuration.BadBookAction.Ignore => ProcessBookResult.FailedSkip,
			Configuration.BadBookAction.Abort => ProcessBookResult.FailedAbort,
			Configuration.BadBookAction.Retry => ProcessBookResult.FailedRetry,
			_ => ProcessBookResult.AwaitingDecision
		};
	}

	#endregion
}
