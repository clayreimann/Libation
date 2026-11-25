using DataLayer;
using LibationEngine.Contracts;

namespace LibationEngine;

public sealed class SearchQueryException(string message) : Exception(message);

/// <summary>Library reads and per-book changes; ASIN lookups return null when no such book exists.</summary>
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
	SettingsDto GetSettings();
}
