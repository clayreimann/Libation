using AaxDecrypter;
using ApplicationServices;
using AudibleUtilities;
using DataLayer;
using Dinah.Core;
using Dinah.Core.Net.Http;
using LibationEngine;
using LibationFileManager;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LibationUiBase;

public record MarkBooksResult(
	int MarkedCount,
	bool WidevineEnabled,
	bool XHeAacRequested,
	bool AccountsMarkedForReRegistration)
{
	public string? GetSummaryMessage()
	{
		if (MarkedCount == 0)
			return null;

		var message = MarkedCount == 1
			? "Marked 1 book as Download Pending."
			: $"Marked {MarkedCount} books as Download Pending.";

		if (WidevineEnabled || AccountsMarkedForReRegistration)
		{
			message += "\r\n\r\nLibation has automatically configured your download settings for higher-quality audio and marked your accounts for a fresh sign-in on your next scan or download so that the higher-quality audio can be acquired.";
		}
		else if (XHeAacRequested)
		{
			message += "\r\n\r\nLibation has automatically enabled xHE-AAC audio requests in your settings.";
		}

		return message;
	}
}

public class FindBetterQualityBooksViewModel : ReactiveObject
{
	public const string StartScanBtnText = "Scan Audible for Higher Quality Audio";
	public const string StopScanBtnText = "Stop Scanning";
	public const string UseWidevineSboxText = "Use Widevine?";
	public const string InitialMessage = """
		This tool will scan your liberated audiobooks to see if Audible
		has a higher quality version available.

		For each liberated audiobook in your library, it will try to read the existing audio file to determine its codec and bitrate. If no local file is found, it will use the 'Last Downloaded' format information stored in the database.
		
		It will then query Audible's API to get the highest quality format currently available for that audiobook.
		
		If you check the 'Use Widevine' option, it will query for Widevine-protected formats, which may or may not be xHE-AAC. If unchecked, it will query for Audible DRM-protected formats, which are typically AAC-LC.

		Click 'Scan Audible for Higher Quality Audio' to begin.

		When done, click 'Mark X books as Download Pending' to allow Libation to re-download those books in the higher quality format.

		Marking books will automatically configure Widevine and xHE-AAC settings and prompt for a fresh account sign-in if needed, ensuring the upgraded audio streams are downloaded.

		Note: If you have a large library, downloading many higher-quality audiobooks in quick succession may trigger Audible's daily download limits. You can configure a daily download limit or download delay in Settings > Network if needed.

		Display this help message again in the future?
		""";

	public event EventHandler<BookDataViewModel>? BookScanned;
	public IList<BookDataViewModel>? Books { get => field; set => RaiseAndSetIfChanged(ref field, value); }
	public bool ShowFindBetterQualityBooksHelp { get => Configuration.Instance.GetNonString(defaultValue: true); set => Configuration.Instance.SetNonString(value); }
	public bool ScanWidevine { get; set; }
	public int SignificantCount
	{
		get => field;
		set
		{
			RaiseAndSetIfChanged(ref field, value);
			MarkBooksButtonText = value == 0 ? null
				: value == 1 ? "Mark 1 book as 'Download Pending'"
				: $"Mark {value} books as 'Download Pending'";
		}
	}
	public bool IsScanning { get => field; set { RaiseAndSetIfChanged(ref field, value); ScanButtonText = field ? StopScanBtnText : StartScanBtnText; } }
	public string? MarkBooksButtonText { get => field; set => RaiseAndSetIfChanged(ref field, value); }
	public string? ScanCount { get => field; set => RaiseAndSetIfChanged(ref field, value); }
	public string ScanButtonText { get => field; set => RaiseAndSetIfChanged(ref field, value); } = StartScanBtnText;

	private CancellationTokenSource? cts;

	public FindBetterQualityBooksViewModel()
	{
		ScanWidevine = Configuration.Instance.UseWidevine;
	}

	public static bool ShouldScan(LibraryBook lb) => QualityCheck.ShouldScan(lb);

	public void StopScan()
	{
		cts?.Cancel();
	}

	public async Task<MarkBooksResult> MarkBooksAsync()
	{
		var significant = Books?.Where(b => b.IsSignificant).ToArray() ?? [];
		if (significant.Length == 0)
			return new MarkBooksResult(0, false, false, false);

		await significant.Select(b => b.LibraryBook).UpdateBookStatusAsync(LiberatedStatus.NotLiberated);
		Array.ForEach(significant, b => Books?.Remove(b));

		SignificantCount = Books?.Count(b => b.IsSignificant) ?? 0;

		var settings = QualityCheck.ApplyUpgradeSettings(ScanWidevine, significant.Select(b => b.AvailableCodec).ToArray());

		return new MarkBooksResult(significant.Length, settings.WidevineEnabled, settings.XHeAacRequested, settings.AccountsMarkedForReRegistration);
	}

	public async Task ScanAsync()
	{
		if (cts?.IsCancellationRequested is true || Books is null)
			return;
		IsScanning = true;

		foreach (var b in Books)
		{
			b.AvailableBitrate = 0;
			b.AvailableCodec = null;
			b.ScanStatus = ProcessBookStatus.Queued;
		}
		ScanCount = $"0 of {Books.Count:N0} scanned";

		try
		{
			using var cli = new HttpClient();
			cts = new CancellationTokenSource();
			for (int i = 0; i < Books.Count; i++)
			{
				var b = Books[i];
				try
				{
					cts.Token.ThrowIfCancellationRequested();
					var url = QualityCheck.MetadataUrl(b.LibraryBook, ScanWidevine);
					//Don't re-scan a file if we have already loaded existing audio codec and bitrate.
					if (b.Bitrate == 0 && b.Codec == null)
					{
						var (file, bestFormat) = QualityCheck.FindHighestExistingFormat(b.LibraryBook);

						if (file is not null)
						{
							b.FoundFile = Configuration.Instance.Books?.Path is string booksDir ? Path.GetRelativePath(booksDir, file) : file;
							b.Bitrate = bestFormat.BitRate;
							b.Codec = bestFormat.CodecString;
						}
						else if (b.LibraryBook.Book.UserDefinedItem.LastDownloadedFormat is not null)
						{
							b.FoundFile = "File not found. Using 'Last Downloaded' format.";
							b.Bitrate = b.LibraryBook.Book.UserDefinedItem.LastDownloadedFormat.BitRate;
							b.Codec = b.LibraryBook.Book.UserDefinedItem.LastDownloadedFormat.CodecString;
						}
						else
						{
							b.FoundFile = "File not found and no 'Last Downloaded' format found.";
							b.ScanStatus = ProcessBookStatus.Failed;
							continue;
						}
					}

					var resp = await cli.GetAsync(url, cts.Token);
					var available = await QualityCheck.ReadAvailableAsync(resp.EnsureSuccessStatusCode());

					b.AvailableCodec = available.Codec;
					b.AvailableBitrate = available.Bitrate;
					b.ScanStatus = ProcessBookStatus.Completed;
				}
				catch (OperationCanceledException)
				{
					b.ScanStatus = ProcessBookStatus.Cancelled;
					break;
				}
				catch (Exception ex)
				{
					Serilog.Log.Logger.Error(ex, "Error checking for better quality for {@Asin}", new { b.Asin });
					b.FoundFile = $"Error: {ex.Message}";
					b.ScanStatus = ProcessBookStatus.Failed;
				}
				finally
				{
					SignificantCount = Books.Count(b => b.IsSignificant);
					ScanCount = $"{i + 1:N0} of {Books.Count:N0} scanned";
					BookScanned?.Invoke(this, b);
				}
			}
		}
		finally
		{
			cts?.Dispose();
			cts = null;
			IsScanning = false;
		}
	}
}
