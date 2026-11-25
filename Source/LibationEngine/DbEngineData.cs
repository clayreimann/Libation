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

	public Task<IReadOnlyList<BulkItemDto>> BulkSetTags(IReadOnlyList<string> asins, TagEdit edit)
		=> Bulk(asins, _ => true, books => books.UpdateUserDefinedItemAsync(udi => udi.Tags = ApplyTags(udi.Tags, edit)));

	public Task<IReadOnlyList<BulkItemDto>> BulkSetBookStatus(IReadOnlyList<string> asins, LiberatedStatus status)
		=> Bulk(asins, _ => true, books => books.UpdateBookStatusAsync(status));

	public Task<IReadOnlyList<BulkItemDto>> BulkSetPdfStatus(IReadOnlyList<string> asins, LiberatedStatus status)
		=> Bulk(asins, lb => lb.Book.UserDefinedItem.PdfStatus.HasValue, books => books.UpdatePdfStatusAsync(status));

	public Task<IReadOnlyList<BulkItemDto>> BulkSetTrash(IReadOnlyList<string> asins, bool inTrash)
		=> Bulk(asins, _ => true, async books =>
		{
			var changing = books.Where(lb => lb.IsDeleted != inTrash).ToList();
			if (changing.Count > 0)
				await (inTrash ? changing.RemoveBooksAsync() : changing.RestoreBooksAsync());
		});

	public Task<IReadOnlyList<BulkItemDto>> BulkPurge(IReadOnlyList<string> asins)
		=> Bulk(asins, lb => lb.IsDeleted, books => books.PermanentlyDeleteBooksAsync());

	private static string ApplyTags(string current, TagEdit edit)
	{
		if (edit.Set is not null)
			return edit.Set;
		var remove = Tokens(edit.Remove).ToHashSet();
		return string.Join(' ', Tokens(current).Concat(Tokens(edit.Add)).Where(t => !remove.Contains(t)).Distinct());
	}

	private static IEnumerable<string> Tokens(string? text)
		=> (text ?? "").Split(null as char[], StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToLowerInvariant().Replace('-', '_'));

	private static async Task<IReadOnlyList<BulkItemDto>> Bulk(IReadOnlyList<string> asins, Func<LibraryBook, bool> eligible, Func<List<LibraryBook>, Task> apply)
	{
		var known = DbContexts.GetLibrary_Flat_NoTracking()
			.Concat(DbContexts.GetDeletedLibraryBooks().Where(lb => lb.IsDeleted))
			.GroupBy(lb => lb.Book.AudibleProductId, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

		var results = new List<BulkItemDto>(asins.Count);
		var accepted = new List<LibraryBook>();
		foreach (var asin in asins)
		{
			if (!known.TryGetValue(asin, out var lb))
				results.Add(new(asin, BulkResults.NotFound));
			else if (!eligible(lb))
				results.Add(new(asin, BulkResults.Conflict));
			else
			{
				accepted.Add(lb);
				results.Add(new(asin, BulkResults.Ok));
			}
		}

		if (accepted.Count > 0)
			await apply(accepted);
		return results;
	}

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
