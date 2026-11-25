using ApplicationServices;
using AudibleUtilities;
using DataLayer;
using LibationEngine.Contracts;

namespace LibationEngine;

public sealed class DbEngineData : IEngineData
{
	public IReadOnlyList<AccountDto> GetAccounts()
	{
		using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
		return persister.AccountsSettings.Accounts.Select(ToDto).ToList();
	}

	public LibraryPageDto GetLibrary(int offset, int limit, string? query = null, bool trash = false)
	{
		var hasQuery = !string.IsNullOrWhiteSpace(query);
		if (trash && hasQuery)
			throw new SearchQueryException("A search query cannot be combined with the trash listing.");

		var books = trash
			? DbContexts.GetDeletedLibraryBooks().Where(lb => lb.IsDeleted).ToList()
			: DbContexts.GetLibrary_Flat_NoTracking();

		if (hasQuery)
		{
			var hits = Search(query!);
			books = books.Where(lb => hits.Contains(lb.Book.AudibleProductId)).ToList();
		}

		var items = books
			.OrderByDescending(lb => lb.DateAdded)
			.ThenBy(lb => lb.Book.AudibleProductId, StringComparer.Ordinal)
			.Skip(offset)
			.Take(limit)
			.Select(ToDto)
			.ToList();
		return new(books.Count, offset, limit, items);
	}

	private static HashSet<string> Search(string query)
	{
		try
		{
			return SearchEngineCommands.Search(query).Docs.Select(d => d.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
		}
		catch (Lucene.Net.QueryParsers.ParseException ex)
		{
			throw new SearchQueryException($"Cannot parse the search query: {ex.Message.Split('\n')[0]}");
		}
	}

	public BookDto? GetBook(string asin) => Find(asin) is { } lb ? ToDto(lb) : null;

	public Task<BookDto?> SetTags(string asin, string tags)
		=> Change(asin, lb => lb.UpdateTagsAsync(tags));

	public Task<BookDto?> SetBookStatus(string asin, LiberatedStatus status)
		=> Change(asin, lb => lb.UpdateBookStatusAsync(status));

	public Task<BookDto?> SetTrash(string asin, bool inTrash)
		=> Change(asin, async lb =>
		{
			if (lb.IsDeleted == inTrash)
				return 0;
			return inTrash ? await new[] { lb }.RemoveBooksAsync() : await new[] { lb }.RestoreBooksAsync();
		});

	public SettingsDto GetSettings() => SettingsMapper.From(LibationFileManager.Configuration.Instance);

	private static LibraryBook? Find(string asin)
		=> DbContexts.GetLibraryBook_Flat_NoTracking(asin)
			?? DbContexts.GetDeletedLibraryBooks().FirstOrDefault(lb => lb.IsDeleted && lb.Book.AudibleProductId == asin);

	private async Task<BookDto?> Change(string asin, Func<LibraryBook, Task<int>> change)
	{
		if (Find(asin) is not { } lb)
			return null;
		await change(lb);
		return GetBook(asin);
	}

	public static AccountDto ToDto(Account account)
		=> new(account.AccountId, account.AccountName, account.Locale?.Name, account.LibraryScan);

	public static BookDto ToDto(LibraryBook lb)
	{
		var book = lb.Book;
		return new(
			book.AudibleProductId,
			book.Title,
			string.IsNullOrEmpty(book.Subtitle) ? null : book.Subtitle,
			book.Authors.Select(a => a.Name).ToList(),
			book.Narrators.Select(n => n.Name).ToList(),
			book.SeriesLink.Select(s => new SeriesDto(s.Series.Name ?? "", s.Order, s.Index)).ToList(),
			book.LengthInMinutes,
			book.UserDefinedItem.BookStatus.ToString(),
			book.UserDefinedItem.PdfStatus?.ToString(),
			lb.DateAdded,
			lb.Account,
			book.Locale,
			book.ContentType.ToString(),
			book.ContentType == ContentType.Episode,
			book.UserDefinedItem.Tags ?? "",
			lb.IsDeleted);
	}
}
