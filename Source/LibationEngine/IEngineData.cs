using DataLayer;
using LibationEngine.Contracts;

namespace LibationEngine;

public sealed class SearchQueryException(string message) : Exception(message);

/// <summary>PictureId is null when the book has no cover.</summary>
public sealed record CoverInfo(string? PictureId);

public enum CoverSize { Small, Large }

/// <summary>Library reads and per-book changes; ASIN lookups return null when no such book exists.</summary>
public sealed record TagEdit(string? Set, string? Add, string? Remove);

public static class BulkResults
{
	public const string Ok = "ok", NotFound = "notFound", Conflict = "conflict";

	public static BulkResultDto Summarize(IReadOnlyList<BulkItemDto> results)
		=> new(results.Count(r => r.Result == Ok), results.Where(r => r.Result == NotFound).Select(r => r.Asin).ToList(), results);
}

/// <summary>Libraries is null when the call failed; Code is an <c>AudiobookshelfApiService.TestCodes</c> value.</summary>
public sealed record AudiobookshelfLibrariesResult(IReadOnlyList<AudiobookshelfLibraryDto>? Libraries, string? Error = null, string? Code = null);

public enum AccountOutcome { Ok, NotFound, Ambiguous }

public sealed record AccountResult(AccountOutcome Outcome, AccountDto? Account = null);

public enum ExportFormat { Csv, Xlsx, Json }

public sealed record TemplatePreviewResult(bool BookFound, TemplatePreviewDto? Preview);

public enum LocateOutcome { Ok, NoSuchBook, Invalid, Busy }

public sealed record LocateResult(LocateOutcome Outcome, BookDetailDto? Book = null, string? Error = null);

public sealed record LocateScanStart(LocateOutcome Outcome, LocateScanDto? Scan = null, string? Error = null);

public enum RatingOutcome { Ok, NoSuchBook, AudibleFailed }

public sealed record RatingResult(RatingOutcome Outcome, BookDetailDto? Book = null, string? Error = null);

public enum QualityOutcome { Ok, Invalid, Busy, NotRunning }

public sealed record QualityScanStart(QualityOutcome Outcome, QualityScanDto? Scan = null, string? Error = null);

public sealed record QualityApplyResult(QualityOutcome Outcome, QualityApplyDto? Applied = null);

public enum BookmarksOutcome { Ok, NoSuchBook, AudibleFailed }

/// <summary>FilePath is the exported temp file, null when the book has no records to export.</summary>
public sealed record BookmarksResult(BookmarksOutcome Outcome, IReadOnlyList<BookmarkDto>? Records = null, string? FilePath = null, string? Error = null);

public interface IEngineData
{
	IReadOnlyList<AccountDto> GetAccounts();

	/// <summary>Sets the display name and/or library-scan flag. <paramref name="locale"/> picks among accounts sharing one login id.</summary>
	/// <exception cref="IOException">The accounts file cannot be written.</exception>
	AccountResult UpdateAccount(string accountId, string? locale, string? accountName, bool? libraryScan);

	/// <summary>Removes the account only; its books stay in the library, as in the desktop Accounts dialog.</summary>
	/// <exception cref="IOException">The accounts file cannot be written.</exception>
	AccountResult DeleteAccount(string accountId, string? locale);

	/// <summary>Newest first unless <paramref name="sort"/> is set. <paramref name="query"/> is Lucene filter syntax; <paramref name="trash"/> lists the trash instead.</summary>
	/// <exception cref="SearchQueryException">The query cannot be parsed.</exception>
	LibraryPageDto GetLibrary(int offset, int limit, string? query = null, bool trash = false, LibrarySortKey? sort = null, bool descending = false);

	/// <summary>Every matching ASIN, newest first, capped at <see cref="LibraryPaging.MaxAsins"/>.</summary>
	/// <exception cref="SearchQueryException">The query cannot be parsed.</exception>
	LibraryAsinsDto GetLibraryAsins(string? query = null, bool trash = false);

	/// <summary>Podcast parent rows having at least one episode that matches <paramref name="query"/> and <paramref name="trash"/>, by title.</summary>
	/// <exception cref="SearchQueryException">The query cannot be parsed.</exception>
	IReadOnlyList<BookDto> GetPodcastParents(string? query = null, bool trash = false);

	/// <summary>Every series in the library whose name contains <paramref name="name"/>, by name.</summary>
	IReadOnlyList<SeriesGroupDto> GetSeries(string? name = null);

	SeriesGroupDto? GetSeriesById(string seriesId);

	/// <summary>A podcast's episodes in the desktop grid's child order; null when <paramref name="parentAsin"/> is not a podcast parent.</summary>
	IReadOnlyList<BookDto>? GetPodcastEpisodes(string parentAsin, bool trash = false);

	/// <summary>Writes the matching books to a new temp file as desktop's export does; the caller deletes it.</summary>
	/// <exception cref="SearchQueryException">The query cannot be parsed.</exception>
	string ExportLibrary(ExportFormat format, string? query = null, bool trash = false);

	QuickFiltersDto GetQuickFilters();

	/// <exception cref="IOException">The filters file cannot be written.</exception>
	void SetQuickFilters(QuickFiltersDto filters);

	/// <summary>Null when the query parses, else the parse error.</summary>
	string? ValidateFilter(string filter);

	BookDto? GetBook(string asin);
	BookDetailDto? GetBookDetail(string asin);
	BackupCountsDto GetBackupCounts();

	/// <summary>Null when no such book exists.</summary>
	CoverInfo? FindCover(string asin);

	/// <summary>The book's audio files in play order, resolved from the library and file index only.</summary>
	AudioLookup FindAudio(string asin);

	/// <summary>Cached jpeg bytes, fetched on first use; null when unavailable.</summary>
	Task<byte[]?> LoadCover(string pictureId, CoverSize size, CancellationToken ct);
	/// <summary>Pushes the stars to Audible, then stores them; nothing is stored when Audible fails. A zero <paramref name="overall"/> is a no-op, as in the grid.</summary>
	Task<RatingResult> SetMyRating(string asin, int overall, int performance, int story, CancellationToken ct);

	Task<BookDto?> SetTags(string asin, string tags);
	Task<BookDto?> SetBookStatus(string asin, LiberatedStatus status);
	Task<BookDto?> SetTrash(string asin, bool inTrash);

	/// <summary>Bulk calls take distinct ASINs and answer each in order: ok, notFound, or conflict.</summary>
	Task<IReadOnlyList<BulkItemDto>> BulkSetTags(IReadOnlyList<string> asins, TagEdit edit);
	Task<IReadOnlyList<BulkItemDto>> BulkSetBookStatus(IReadOnlyList<string> asins, LiberatedStatus status);
	Task<IReadOnlyList<BulkItemDto>> BulkSetPdfStatus(IReadOnlyList<string> asins, LiberatedStatus status);
	Task<IReadOnlyList<BulkItemDto>> BulkSetTrash(IReadOnlyList<string> asins, bool inTrash);
	Task<IReadOnlyList<BulkItemDto>> BulkPurge(IReadOnlyList<string> asins);
	SettingsDto GetSettings();

	/// <summary>Validates every key before applying any; a rejected patch changes nothing.</summary>
	/// <exception cref="IOException">Settings.json cannot be written; earlier changes in the patch are rolled back.</exception>
	SettingsUpdateResult UpdateSettings(System.Text.Json.JsonElement patch);

	/// <summary>Omitted <paramref name="url"/> and <paramref name="token"/> fall back to the saved ones; failures are reported in the result, not thrown.</summary>
	Task<AudiobookshelfTestDto> TestAudiobookshelf(string? url, string? token, CancellationToken ct);

	/// <summary>Book libraries on the saved Audiobookshelf server.</summary>
	Task<AudiobookshelfLibrariesResult> GetAudiobookshelfLibraries(CancellationToken ct);

	/// <summary><paramref name="kind"/> is one of <see cref="TemplatePreview.Kinds"/>; BookFound is false when <paramref name="asin"/> is given but unknown.</summary>
	TemplatePreviewResult PreviewTemplate(string kind, string template, string? asin);

	/// <summary>Records an existing audio file under Books for the book and marks it liberated, as desktop's Locate file.</summary>
	Task<LocateResult> LocateFile(string asin, string path);

	/// <summary>Starts the background folder scan; <paramref name="publish"/> receives its progress.</summary>
	LocateScanStart StartLocateScan(string folder, Action<EngineEvent> publish);
	LocateScanDto GetLocateScan();

	/// <summary>Starts a background quality scan of <paramref name="asins"/> (default: every liberated book); only liberated, non-spatial books are scanned.</summary>
	QualityScanStart StartQualityScan(IReadOnlyList<string>? asins, bool? widevine, Action<EngineEvent> publish);
	QualityScanDto GetQualityScan();

	/// <summary>NotRunning when no scan is running.</summary>
	QualityScanStart CancelQualityScan(Action<EngineEvent> publish);

	/// <summary>Marks scanned upgrades NotLiberated and prepares settings for the better audio, as desktop's Mark as Download Pending; Busy while a scan runs.</summary>
	Task<QualityApplyResult> ApplyQualityUpgrades(IReadOnlyList<string> asins);

	/// <summary>The book's clips, notes and bookmarks, fetched live from Audible.</summary>
	Task<BookmarksResult> GetBookmarks(string asin, CancellationToken ct);

	/// <summary>Writes the records to a new temp file with desktop's exporter; the caller deletes it.</summary>
	Task<BookmarksResult> ExportBookmarks(string asin, ExportFormat format, CancellationToken ct);
}
