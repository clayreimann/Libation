using DataLayer;
using FileLiberator;
using LibationEngine;
using LibationFileManager;
using System;
using System.Threading.Tasks;

namespace LibationUiBase.ProcessQueue;

/// <summary>
/// This is the viewmodel for queued processables
/// </summary>
public class ProcessBookViewModel : ReactiveObject
{
	private readonly LiberationJob _job;
	internal LiberationJob Job => _job;

	public LibraryBook LibraryBook => _job.LibraryBook;
	public Configuration Configuration => _job.Configuration;

	/// <summary>Lets a test or the designer stand in for the queue's job lookup.</summary>
	public static implicit operator LiberationJob(ProcessBookViewModel vm) => vm._job;

	#region Properties exposed to the view
	public ProcessBookResult Result { get => _result; set => _job.Result = value; }
	public ProcessBookStatus Status { get => _status; set => _job.Status = value; }
	public string? Narrator { get => field; set => RaiseAndSetIfChanged(ref field, value); }
	public string? Author { get => field; set => RaiseAndSetIfChanged(ref field, value); }
	public string? Title { get => field; set => RaiseAndSetIfChanged(ref field, value); }
	public int Progress { get => field; protected set => RaiseAndSetIfChanged(ref field, value); }
	public TimeSpan TimeRemaining { get => field; set { RaiseAndSetIfChanged(ref field, value); ETA = $"ETA: {value:mm\\:ss}"; } }
	public string? ETA { get => field; private set => RaiseAndSetIfChanged(ref field, value); }
	public object? Cover { get => field; protected set => RaiseAndSetIfChanged(ref field, value); }
	public bool IsFinished => Status is not ProcessBookStatus.Queued and not ProcessBookStatus.Working;
	public bool IsDownloading => Status is ProcessBookStatus.Working;
	public bool Queued => Status is ProcessBookStatus.Queued;

	private ProcessBookResult _result;
	private ProcessBookStatus _status;
	private string? _statusOverride;

	/// <summary>
	/// Transient status shown instead of the usual text, e.g. while the queue waits on the daily download limit.
	/// Null when the book's own state should speak for itself.
	/// </summary>
	public string? StatusOverride { get => _statusOverride; set => _job.StatusOverride = value; }

	/// <summary>
	/// True when this queue item downloads an audiobook, so the daily download limit applies to it.
	/// PDF-only and mp3-conversion items are never limited.
	/// </summary>
	public bool IncludesBookDownload => _job.IncludesBookDownload;

	public string StatusText => StatusOverride ?? (Result, LibraryBook.IsAudiblePlus) switch
	{
		(ProcessBookResult.Success, _) => "Finished",
		(ProcessBookResult.Cancelled, _) => "Cancelled",
		(ProcessBookResult.ValidationFail, _) => "Validation fail",
		(ProcessBookResult.FailedRetry, _) => "Error, will retry later",
		(ProcessBookResult.FailedSkip, _) => "Error, Skipping",
		(ProcessBookResult.FailedAbort, _) => "Error, Abort",
		(ProcessBookResult.LicenseDenied, true) => "License denied (Plus; often temporary)",
		(ProcessBookResult.LicenseDenied, false) => "License Denied",
		(ProcessBookResult.LicenseDeniedPossibleOutage, _) => "Possible Service Interruption",
		(ProcessBookResult.LicenseDeniedThrottled, _) => "License denied (throttled)",
		(ProcessBookResult.LicenseDeniedThrottledRecovered, _) => "License denied (re-login needed)",
		(ProcessBookResult.WidevineRecommended, _) => WidevineRecommendationUserMessage.QueueStatusText,
		(ProcessBookResult.DiskFull, _) => "Disk full, queue stopped",
		(ProcessBookResult.AwaitingDecision, _) => "Error, waiting for your choice",
		_ => Status.ToString(),
	};

	#endregion

	public event EventHandler<string>? LogWritten;

	public bool CancellationRequested => _job.CancellationRequested;

	public ProcessBookViewModel(LibraryBook libraryBook, Configuration configuration, BadBookSessionContext? badBookSession = null)
	{
		_job = new LiberationJob(libraryBook, configuration, badBookSession);
		_job.ResultChanged += Job_ResultChanged;
		_job.StatusChanged += Job_StatusChanged;
		_job.StatusOverrideChanged += Job_StatusOverrideChanged;
		_job.TitleChanged += (_, title) => Title = title;
		_job.AuthorChanged += (_, author) => Author = author;
		_job.NarratorChanged += (_, narrator) => Narrator = narrator;
		_job.ProgressChanged += (_, progress) => Progress = progress;
		_job.TimeRemainingChanged += (_, timeRemaining) => TimeRemaining = timeRemaining;
		_job.CoverDiscovered += (_, coverArt) => Cover = BaseUtil.LoadImage(coverArt, PictureSize._80x80);
		_job.LogWritten += (_, text) => LogWritten?.Invoke(this, text);

		Title = LibraryBook.Book.TitleWithSubtitle;
		Author = LibraryBook.Book.AuthorNames;
		Narrator = LibraryBook.Book.NarratorNames;

		var pictureId = LibraryBook.Book.PictureId ?? LibraryBook.Book.PictureLarge;
		if (string.IsNullOrEmpty(pictureId))
			return;

		(bool isDefault, byte[] picture) = PictureStorage.GetPicture(new PictureDefinition(pictureId, PictureSize._80x80));

		if (isDefault)
			PictureStorage.PictureCached += PictureStorage_PictureCached;

		Cover = BaseUtil.LoadImage(picture, PictureSize._80x80);
	}

	private void Job_ResultChanged(object? sender, ProcessBookResult result)
	{
		RaiseAndSetIfChanged(ref _result, result, nameof(Result));
		RaisePropertyChanged(nameof(StatusText));
	}

	private void Job_StatusChanged(object? sender, ProcessBookStatus status)
	{
		RaiseAndSetIfChanged(ref _status, status, nameof(Status));
		RaisePropertyChanged(nameof(IsFinished));
		RaisePropertyChanged(nameof(IsDownloading));
		RaisePropertyChanged(nameof(Queued));
	}

	private void Job_StatusOverrideChanged(object? sender, string? statusOverride)
	{
		RaiseAndSetIfChanged(ref _statusOverride, statusOverride, nameof(StatusOverride));
		RaisePropertyChanged(nameof(StatusText));
	}

	private void PictureStorage_PictureCached(object? sender, PictureCachedEventArgs e)
	{
		if (e.Definition.PictureId == LibraryBook.Book.PictureId)
		{
			Cover = BaseUtil.LoadImage(e.Picture, PictureSize._80x80);
			PictureStorage.PictureCached -= PictureStorage_PictureCached;
		}
	}

	public Task<ProcessBookResult> ProcessOneAsync() => _job.ProcessOneAsync();

	public Task CancelAsync() => _job.CancelAsync();

	public ProcessBookViewModel AddDownloadPdf() { _job.AddDownloadPdf(); return this; }
	public ProcessBookViewModel AddDownloadDecryptBook() { _job.AddDownloadDecryptBook(); return this; }
	public ProcessBookViewModel AddConvertToMp3() { _job.AddConvertToMp3(); return this; }
	public ProcessBookViewModel AddUploadToAudiobookshelf() { _job.AddUploadToAudiobookshelf(); return this; }
	public ProcessBookViewModel AddSimulateBadBookFailure() { _job.AddSimulateBadBookFailure(); return this; }

	internal ProcessBookViewModel AddProcessable(Func<Configuration, Processable> create)
	{
		_job.AddProcessable(create);
		return this;
	}

	public override string ToString() => LibraryBook.ToString();
}
