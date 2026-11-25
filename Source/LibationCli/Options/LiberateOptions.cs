using ApplicationServices;
using CommandLine;
using DataLayer;
using FileLiberator;
using LibationCli.Options;
using LibationEngine;
using LibationFileManager;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LibationCli;

[Verb("liberate", HelpText = "Liberate: book and pdf backups. Default: download and decrypt all un-liberated titles and download pdfs.\n"
	+ "Optional: specify product id(s) via --id or positional ASIN(s) to liberate those book(s), including re-download if already liberated.\n"
	+ "Optional: reads a license file from standard input.\n"
	+ "Optional: stop this run early with --limit-books, --limit-mb or --limit-gb.")]
public class LiberateOptions : ProcessableOptionsBase
{
	[Option(shortName: 'p', longName: "pdf", Required = false, Default = false, HelpText = "Flag to only download pdfs")]
	public bool PdfOnly { get; set; }

	[Option(shortName: 'f', longName: "force", Required = false, Default = false, HelpText = "Force the book to re-download")]
	public bool Force { get; set; }


	[Option(shortName: 'l', longName: "license", Required = false, Default = null, HelpText = "A license file from the get-license command. Either a file path or dash ('-') to read from standard input.")]
	public string? LicenseInput { get; set; }

	#region per-run limit
	// Distinct SetName values make these three mutually exclusive. See ExportOptions for the same pattern.

	[Option(longName: "limit-books", Required = false, Default = null, SetName = "limit-books",
		HelpText = "Stop this run after downloading this many books. A daily download limit, if set, still applies.")]
	public int? LimitBooks { get; set; }

	[Option(longName: "limit-mb", Required = false, Default = null, SetName = "limit-mb",
		HelpText = "Stop this run after downloading about this many MB. Approximate: a title's size is unknown until it is downloaded.")]
	public int? LimitMB { get; set; }

	[Option(longName: "limit-gb", Required = false, Default = null, SetName = "limit-gb",
		HelpText = "Stop this run after downloading about this many GB. Approximate: a title's size is unknown until it is downloaded.")]
	public int? LimitGB { get; set; }

	internal RunDownloadLimit? RunLimit { get; set; }

	internal LiberationQueue? SharedQueue { get; init; }

	#endregion

	/// <summary>
	/// --force means "attempt everything", which includes the titles Audible recently refused. A --pdf run is
	/// held back like any other: a PDF is fetched through the same license request as the audiobook, so a title
	/// Libation is waiting on would be refused for its PDF exactly as it was for its audio.
	/// </summary>
	internal bool HonorsDeferredRetries => !Force;

	/// <summary>
	/// Audible will not license a title the last scan did not find, so a bulk run has nothing to gain by asking.
	/// --force means "attempt everything"; a run that names its titles never reaches the bulk selection at all.
	/// </summary>
	internal bool SkipsTitlesAbsentFromLastScan => !Force;

	protected override async Task ProcessAsync()
	{
		if (!RunDownloadLimit.TryCreate(LimitBooks, LimitMB, LimitGB, PdfOnly, out var runLimit, out var limitError))
		{
			PrintVerbUsage("ERROR", "=====", limitError);
			Environment.ExitCode = (int)ExitCode.RunTimeError;
			return;
		}
		RunLimit = runLimit;

		if (AudibleFileStorage.BooksDirectory is null)
		{
			Console.Error.WriteLine("Error: Books directory is not set. Please configure the 'Books' setting in Settings.json.");
			return;
		}

		var queue = SharedQueue ?? CreateCliQueue();

		if (LicenseInput is string licenseInput)
			await LiberateFromLicense(queue, licenseInput);
		else
			await RunBulkOrTargetedAsync(queue);
	}

	internal static LiberationQueue CreateCliQueue()
	{
		var policy = new LiberationPolicy
		{
			MaxConcurrentDownloads = () => 1,
			OnDiskFull = DiskFullPolicy.Stop,
			OnDailyLimit = DailyLimitPolicy.Skip
		};
		var queue = new LiberationQueue(policy);
		_ = new AutoResponder(queue, policy, Console.Error);
		return queue;
	}

	internal virtual Processable CreateBookStep(DownloadOptions.LicenseInfo? license)
	{
		var step = CreateProcessable<DownloadDecryptBook>();
		step.LicenseInfo = license;
		return step;
	}

	internal virtual Processable CreatePdfStep() => CreateProcessable<DownloadPdf>();

	internal virtual Processable CreateUploadStep() => CreateProcessable<UploadToAudiobookshelf>();

	internal LiberationJob? CreateTargetedJob(LiberationQueue queue, string asin)
	{
		if (DbContexts.GetLibraryBook_Flat_NoTracking(asin, caseSensative: false) is not LibraryBook lb)
			return null;

		PrepareBookForLiberate(lb, isTargetedRun: true);
		return NewJob(queue, lb, pdfOnly: false);
	}

	private LiberationJob NewJob(LiberationQueue queue, LibraryBook libraryBook, bool pdfOnly, DownloadOptions.LicenseInfo? license = null)
	{
		var job = new LiberationJob(libraryBook, Configuration.Instance, queue.BadBookSession);
		if (pdfOnly)
			job.AddProcessable(_ => CreatePdfStep());
		else
			job.AddDownloadDecryptBook(_ => CreateBookStep(license))
				.AddProcessable(_ => CreatePdfStep())
				.AddProcessable(_ => CreateUploadStep());

		job.AttentionWritten += (_, text) => Console.Error.WriteLine(text);
		return job;
	}

	private sealed class RunOutcome
	{
		public int SkippedForDailyLimit;
		public bool LimitReached;
	}

	/// <summary>Runs the jobs through the queue and returns once the queue has drained.</summary>
	private async Task<RunOutcome> RunJobsAsync(LiberationQueue queue, IReadOnlyList<LiberationJob> jobs)
	{
		var outcome = new RunOutcome();
		if (jobs.Count == 0)
			return outcome;

		var tracker = RunLimit is RunDownloadLimit limit ? new RunLimitTracker(limit, DateTimeOffset.Now) : null;
		var mine = new HashSet<LiberationJob>(jobs);
		var skipped = 0;
		var diskFull = false;

		void OnLog(object? _, string line) => Console.Error.WriteLine(line);
		void OnEnded(object? _, LiberationJob job)
		{
			if (!mine.Contains(job))
				return;

			if (job.Result == ProcessBookResult.DiskFull)
				diskFull = true;
			else if (job.Result == ProcessBookResult.SkippedDailyLimit)
				Interlocked.Increment(ref skipped);
			else if (job.Result is not (ProcessBookResult.Success or ProcessBookResult.Cancelled))
				ReportNextAttempt(job.LibraryBook);
		}

		queue.LogWritten += OnLog;
		queue.JobEnded += OnEnded;
		queue.StopBeforeDispatch = job =>
		{
			if (tracker is null)
				return null;

			if (tracker.TryStop(out var message))
			{
				outcome.LimitReached = true;
				Console.WriteLine(message);
				return message;
			}

			tracker.Attempting(job.LibraryBook.Book.AudibleProductId);
			return null;
		};

		try
		{
			queue.Jobs.Enqueue(jobs.ToList());
			queue.StartOrWake();
			if (queue.Runner is { } runner)
				await runner;
		}
		finally
		{
			queue.StopBeforeDispatch = null;
			queue.JobEnded -= OnEnded;
			queue.LogWritten -= OnLog;
		}

		outcome.SkippedForDailyLimit = skipped;
		if (diskFull)
			Environment.ExitCode = (int)ExitCode.RunTimeError;
		return outcome;
	}

	internal async Task RunBulkOrTargetedAsync(LiberationQueue queue)
	{
		var jobs = new List<LiberationJob>();
		var productIds = GetProductIds().ToArray();
		var skippedAsAbsent = 0;
		var deferredThisRun = new List<DeferredDownload>();

		if (productIds.Length > 0)
		{
			foreach (var asin in productIds)
			{
				if (DbContexts.GetLibraryBook_Flat_NoTracking(asin, caseSensative: false) is LibraryBook lb)
				{
					PrepareBookForLiberate(lb, isTargetedRun: true);
					jobs.Add(NewJob(queue, lb, PdfOnly));
				}
				else
				{
					var msg = $"Book with ASIN '{asin}' not found in library. Skipping.";
					Console.Error.WriteLine(msg);
					Serilog.Log.Logger.Error(msg);
				}
			}
		}
		else
		{
			// Read once, before the first book: a run that spends hours downloading must not start skipping
			// titles because of failures it recorded itself a moment ago.
			var deferrals = HonorsDeferredRetries ? DownloadDeferrals.Load(DateTimeOffset.Now) : DownloadDeferrals.None;
			var libraryBooks = DbContexts.GetLibrary_Flat_NoTracking();

			var firstPass = PdfOnly ? CreatePdfStep() : CreateBookStep(null);
			var followUp = BackFillsPdfs ? CreatePdfStep() : null;

			if (SkipsTitlesAbsentFromLastScan)
			{
				var absent = libraryBooks.Where(lb => !lb.Downloadable).ToList();
				libraryBooks = libraryBooks.Where(lb => lb.Downloadable).ToList();

				// Only titles a pass would have attempted: most absent titles need nothing at all.
				skippedAsAbsent = firstPass.GetValidLibraryBooks(absent)
					.Concat(followUp?.GetValidLibraryBooks(absent) ?? [])
					.DistinctBy(lb => lb.Book.AudibleProductId, StringComparer.OrdinalIgnoreCase)
					.Count();
			}

			// Recorded by product id: a step that just failed still validates, and a title being waited on
			// looks like any other title that needs downloading.
			var settledByFirstPass = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var lb in firstPass.GetValidLibraryBooks(libraryBooks))
			{
				settledByFirstPass.Add(lb.Book.AudibleProductId);
				if (IsDeferred(lb))
					continue;

				PrepareBookForLiberate(lb, isTargetedRun: false);
				jobs.Add(NewJob(queue, lb, PdfOnly));
			}

			if (followUp is not null)
			{
				foreach (var lb in followUp.GetValidLibraryBooks(libraryBooks))
				{
					if (settledByFirstPass.Contains(lb.Book.AudibleProductId) || IsDeferred(lb))
						continue;

					PrepareBookForLiberate(lb, isTargetedRun: false);
					jobs.Add(NewJob(queue, lb, pdfOnly: true));
				}
			}

			bool IsDeferred(LibraryBook libraryBook)
			{
				if (deferrals.Find(libraryBook) is not DeferredDownload deferred)
					return false;

				deferredThisRun.Add(deferred);
				Serilog.Log.Logger.Information(
					"Not attempting {libraryBook} yet. {@DebugInfo}",
					libraryBook.LogFriendly(),
					new { deferred.Kind, deferred.ConsecutiveFailures, deferred.Reason, RetryAfter = deferred.RetryAfter.ToLocalTime() });
				return true;
			}
		}

		var outcome = await RunJobsAsync(queue, jobs);

		if (skippedAsAbsent > 0)
		{
			var summary = AbsentFromLastScanUserMessage.BuildCliSkippedSummary(skippedAsAbsent);
			Console.WriteLine(summary);
			Serilog.Log.Logger.Information(summary);
		}

		if (deferredThisRun.Count > 0)
		{
			var now = DateTimeOffset.Now;
			foreach (var line in DeferredDownloadUserMessage.BuildCliSkippedLines(deferredThisRun, now))
				Console.WriteLine(line);

			Serilog.Log.Logger.Information(
				"Skipped {deferredCount} titles that recently failed to download. Skipped: {skipped}",
				deferredThisRun.Count,
				DeferredDownloadUserMessage.BuildLogBreakdown(deferredThisRun));
		}

		if (outcome.SkippedForDailyLimit > 0)
		{
			var summary = DailyDownloadLimitUserMessage.BuildCliSkippedSummary(outcome.SkippedForDailyLimit);
			Console.WriteLine(summary);
			Serilog.Log.Logger.Information(summary);
		}

		var done = outcome.LimitReached
			? "Done. Stopped early: this run's download limit was reached."
			: "Done. All books have been processed";
		Console.WriteLine(done);
		Serilog.Log.Logger.Information(done);
	}

	/// <summary>
	/// Whether this run also picks up titles that need nothing but their PDF. The verb is "book and pdf
	/// backups", but the main pass only selects titles that need downloading, so on its own it never reaches
	/// one whose audio it already has. A --pdf run selects those titles to begin with, and a run that names
	/// its titles re-downloads them and gets their PDFs from that.
	/// </summary>
	internal bool BackFillsPdfs => !PdfOnly && !GetProductIds().Any();

	private async Task LiberateFromLicense(LiberationQueue queue, string licPath)
	{
		var licenseInfo = licPath is "-" ? ReadLicenseFromStdIn()
			: ReadLicenseFromFile(licPath);

		if (licenseInfo is null)
			return;

		if (licenseInfo?.ContentMetadata?.ContentReference?.Asin is not string asin)
		{
			Console.Error.WriteLine("Error: License file is missing ASIN information.");
			return;
		}

		if (DbContexts.GetLibraryBook_Flat_NoTracking(asin) is not LibraryBook libraryBook)
		{
			Console.Error.WriteLine($"Book not found with asin={asin}");
			return;
		}

		PrepareBookForLiberate(libraryBook, isTargetedRun: true);

		var outcome = await RunJobsAsync(queue, [NewJob(queue, libraryBook, PdfOnly, licenseInfo)]);
		if (outcome.SkippedForDailyLimit > 0)
			Console.WriteLine(DailyDownloadLimitUserMessage.BuildCliSkippedSummary(outcome.SkippedForDailyLimit));
	}

	private static DownloadOptions.LicenseInfo? ReadLicenseFromFile(string licFile)
	{
		if (!File.Exists(licFile))
		{
			Console.Error.WriteLine("File does not exist: " + licFile);
			return null;
		}

		Console.WriteLine("Reading license from file.");
		try
		{
			var serializer = CreateLicenseInfoSerializer();
			using var reader = new JsonTextReader(new StreamReader(licFile));
			return serializer.Deserialize<DownloadOptions.LicenseInfo>(reader);
		}
		catch (Exception ex)
		{
			Serilog.Log.Error(ex, "Failed to read license file: {LicenseFile}", licFile);
			Console.Error.WriteLine("Error: Failed to read license file. Please ensure the file is a valid license file in JSON format.");
		}
		return null;
	}

	private static DownloadOptions.LicenseInfo? ReadLicenseFromStdIn()
	{
		if (!Console.IsInputRedirected)
		{
			Console.Error.WriteLine("Ther is nothing in standard input to read.");
			return null;
		}

		Console.WriteLine("Reading license from standard input.");
		try
		{
			var serializer = CreateLicenseInfoSerializer();
			using var reader = new JsonTextReader(new StreamReader(Console.OpenStandardInput()));
			return serializer.Deserialize<DownloadOptions.LicenseInfo>(reader);
		}
		catch (Exception ex)
		{
			Serilog.Log.Error(ex, "Failed to read license from standard input");
			Console.Error.WriteLine("Error: Failed to read license file from standard input. Please ensure the input is a valid license file in JSON format.");
		}
		return null;
	}

	private static JsonSerializer CreateLicenseInfoSerializer()
	{
		var jsonSettings = new JsonSerializerSettings
		{
			NullValueHandling = NullValueHandling.Ignore,
			Converters = [new StringEnumConverter(), new ByteArrayHexConverter()]
		};

		return JsonSerializer.Create(jsonSettings);
	}

	private void PrepareBookForLiberate(LibraryBook lb, bool isTargetedRun)
	{
		// Targeted runs (explicit ASIN/id) always re-download. --force is for re-downloading the whole library.
		if (Force || isTargetedRun)
		{
			if (!PdfOnly)
				lb.Book.UserDefinedItem.BookStatus = LiberatedStatus.NotLiberated;
			lb.Book.UserDefinedItem.SetPdfStatus(LiberatedStatus.NotLiberated);

			// The status above is set on an untracked copy, so the central clear in updateUserDefinedItem
			// never sees it. Asking for this title is the user overriding any wait Libation was observing,
			// and the wait must restart from the beginning if the attempt fails again.
			DownloadAttemptFailureStore.Clear(lb);
		}
	}
}
