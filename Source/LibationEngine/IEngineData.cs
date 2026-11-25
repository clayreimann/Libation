using DataLayer;
using LibationEngine.Contracts;

namespace LibationEngine;

public sealed class SearchQueryException(string message) : Exception(message);

/// <summary>Library reads and per-book changes; ASIN lookups return null when no such book exists.</summary>
public sealed record TagEdit(string? Set, string? Add, string? Remove);

public static class BulkResults
{
	public const string Ok = "ok", NotFound = "notFound", Conflict = "conflict";

	public static BulkResultDto Summarize(IReadOnlyList<BulkItemDto> results)
		=> new(results.Count(r => r.Result == Ok), results.Where(r => r.Result == NotFound).Select(r => r.Asin).ToList(), results);
}

public interface IEngineData
{
	IReadOnlyList<AccountDto> GetAccounts();

	/// <summary>Newest first. <paramref name="query"/> is Lucene filter syntax, over the library; <paramref name="trash"/> lists the trash instead.</summary>
	/// <exception cref="SearchQueryException">The query cannot be parsed.</exception>
	LibraryPageDto GetLibrary(int offset, int limit, string? query = null, bool trash = false);

	BookDto? GetBook(string asin);
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
}
