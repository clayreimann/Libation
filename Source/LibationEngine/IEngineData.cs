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

public enum LocateOutcome { Ok, NoSuchBook, Invalid, Busy }

public sealed record LocateResult(LocateOutcome Outcome, BookDetailDto? Book = null, string? Error = null);

public sealed record LocateScanStart(LocateOutcome Outcome, LocateScanDto? Scan = null, string? Error = null);

public interface IEngineData
{
	IReadOnlyList<AccountDto> GetAccounts();

	/// <summary>Newest first unless <paramref name="sort"/> is set. <paramref name="query"/> is Lucene filter syntax; <paramref name="trash"/> lists the trash instead.</summary>
	/// <exception cref="SearchQueryException">The query cannot be parsed.</exception>
	LibraryPageDto GetLibrary(int offset, int limit, string? query = null, bool trash = false, LibrarySortKey? sort = null, bool descending = false);

	/// <summary>Every matching ASIN, newest first, capped at <see cref="LibraryPaging.MaxAsins"/>.</summary>
	/// <exception cref="SearchQueryException">The query cannot be parsed.</exception>
	LibraryAsinsDto GetLibraryAsins(string? query = null, bool trash = false);

	BookDto? GetBook(string asin);
	BookDetailDto? GetBookDetail(string asin);
	BackupCountsDto GetBackupCounts();

	/// <summary>Null when no such book exists.</summary>
	CoverInfo? FindCover(string asin);

	/// <summary>Cached jpeg bytes, fetched on first use; null when unavailable.</summary>
	Task<byte[]?> LoadCover(string pictureId, CoverSize size, CancellationToken ct);
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

	/// <summary>Records an existing audio file under Books for the book and marks it liberated, as desktop's Locate file.</summary>
	Task<LocateResult> LocateFile(string asin, string path);

	/// <summary>Starts the background folder scan; <paramref name="publish"/> receives its progress.</summary>
	LocateScanStart StartLocateScan(string folder, Action<EngineEvent> publish);
	LocateScanDto GetLocateScan();
}
