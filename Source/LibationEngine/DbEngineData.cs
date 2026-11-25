using ApplicationServices;
using AudibleUtilities;
using DataLayer;
using LibationEngine.Contracts;
using LibationFileManager;

namespace LibationEngine;

public sealed class DbEngineData : IEngineData
{
	public IReadOnlyList<AccountDto> GetAccounts()
	{
		using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
		return persister.AccountsSettings.Accounts.Select(ToDto).ToList();
	}

	public LibraryPageDto GetLibrary(int offset, int limit, string? query = null, bool trash = false, LibrarySortKey? sort = null, bool descending = false)
	{
		var books = Matching(query, trash);
		var ordered = sort is { } key ? LibrarySorting.Order(books, key, descending) : books
			.OrderByDescending(lb => lb.DateAdded)
			.ThenBy(lb => lb.Book.AudibleProductId, StringComparer.Ordinal);
		var partial = AudibleFileStorage.AaxcExistsSnapshot();
		var items = ordered.Skip(offset).Take(limit).Select(lb => ToDto(lb, partial)).ToList();
		return new(books.Count, offset, limit, items);
	}

	public LibraryAsinsDto GetLibraryAsins(string? query = null, bool trash = false)
	{
		var books = Matching(query, trash);
		var asins = books
			.OrderByDescending(lb => lb.DateAdded)
			.ThenBy(lb => lb.Book.AudibleProductId, StringComparer.Ordinal)
			.Take(LibraryPaging.MaxAsins)
			.Select(lb => lb.Book.AudibleProductId)
			.ToList();
		return new(books.Count, asins);
	}

	public string ExportLibrary(ExportFormat format, string? query = null, bool trash = false)
	{
		var books = Matching(query, trash)
			.OrderByDescending(lb => lb.DateAdded)
			.ThenBy(lb => lb.Book.AudibleProductId, StringComparer.Ordinal)
			.ToList();
		var path = Path.Combine(Path.GetTempPath(), $"libation-export-{Guid.NewGuid():N}");
		try
		{
			switch (format)
			{
				case ExportFormat.Csv:
					LibraryExporter.ToCsv(path, books);
					break;
				case ExportFormat.Xlsx:
					LibraryExporter.ToXlsx(path, books);
					break;
				default:
					LibraryExporter.ToJson(path, books);
					break;
			}
			if (!File.Exists(path))
				File.WriteAllBytes(path, []);
			return path;
		}
		catch
		{
			File.Delete(path);
			throw;
		}
	}

	public IReadOnlyList<BookDto> GetPodcastParents(string? query = null, bool trash = false)
	{
		var seriesIds = Matching(query, trash)
			.Where(lb => lb.Book.IsEpisodeChild())
			.SelectMany(lb => lb.Book.SeriesLink.Select(s => s.Series.AudibleSeriesId))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (seriesIds.Count == 0)
			return [];

		var partial = AudibleFileStorage.AaxcExistsSnapshot();
		var all = trash ? DbContexts.GetDeletedLibraryBooks() : DbContexts.GetLibrary_Flat_NoTracking(includeParents: true);
		return all
			.Where(lb => lb.Book.IsEpisodeParent() && seriesIds.Contains(lb.Book.AudibleProductId))
			.OrderBy(lb => lb.Book.TitleWithSubtitle, StringComparer.OrdinalIgnoreCase)
			.ThenBy(lb => lb.Book.AudibleProductId, StringComparer.Ordinal)
			.Select(lb => ToDto(lb, partial))
			.ToList();
	}

	private static List<LibraryBook> Matching(string? query, bool trash)
	{
		var hasQuery = !string.IsNullOrWhiteSpace(query);
		var books = trash
			? DbContexts.GetDeletedLibraryBooks().Where(lb => lb.IsDeleted).ToList()
			: DbContexts.GetLibrary_Flat_NoTracking();
		if (!hasQuery)
			return books;

		var hits = trash ? SearchTrash(query!, books) : Search(query!);
		return books.Where(lb => hits.Contains(lb.Book.AudibleProductId)).ToList();
	}

	private static readonly object TrashSearchLock = new();

	private static HashSet<string> SearchTrash(string query, List<LibraryBook> trashed)
	{
		lock (TrashSearchLock)
		{
			try
			{
				var engine = new LibationSearchEngine.SearchEngine(TempSearchEngine.SearchEnginePath);
				engine.CreateNewIndex(trashed, overwrite: true);
				return engine.Search(query).Docs.Select(d => d.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
			}
			catch (Lucene.Net.QueryParsers.ParseException ex)
			{
				throw new SearchQueryException($"Cannot parse the search query: {ex.Message.Split('\n')[0]}");
			}
		}
	}

	private static HashSet<string> Search(string query)
	{
		SearchEngineCommands.WaitForPendingIndex();
		try
		{
			return SearchEngineCommands.Search(query).Docs.Select(d => d.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
		}
		catch (Lucene.Net.QueryParsers.ParseException ex)
		{
			throw new SearchQueryException($"Cannot parse the search query: {ex.Message.Split('\n')[0]}");
		}
	}

	public QuickFiltersDto GetQuickFilters()
		=> new(QuickFilters.UseDefault, QuickFilters.Filters.Select(f => new QuickFilterDto(f.Filter, f.Name)).ToList());

	public void SetQuickFilters(QuickFiltersDto filters)
	{
		QuickFilters.ReplaceAll(filters.Filters.Select(f => new QuickFilters.NamedFilter(f.Filter, string.IsNullOrWhiteSpace(f.Name) ? null : f.Name.Trim())));
		QuickFilters.UseDefault = filters.UseDefault;
	}

	public string? ValidateFilter(string filter)
	{
		try
		{
			Search(filter);
			return null;
		}
		catch (SearchQueryException ex)
		{
			return ex.Message;
		}
	}

	public BookDto? GetBook(string asin) => Find(asin) is { } lb ? ToDto(lb) : null;

	public BookDetailDto? GetBookDetail(string asin) => Find(asin) is { } lb ? ToDetailDto(lb) : null;

	public BackupCountsDto GetBackupCounts()
	{
		var counts = LibraryCommands.GetCounts(requestedBy: "api");
		return new(
			counts.booksFullyBackedUp + counts.booksDownloadedOnly + counts.booksNoProgress + counts.booksError + counts.booksUnavailable,
			counts.booksFullyBackedUp, counts.booksDownloadedOnly, counts.booksNoProgress, counts.booksError, counts.booksUnavailable,
			counts.pdfsDownloaded, counts.pdfsNotDownloaded, counts.pdfsUnavailable);
	}

	public CoverInfo? FindCover(string asin)
		=> Find(asin) is { } lb ? new(lb.Book.PictureId ?? lb.Book.PictureLarge) : null;

	public async Task<byte[]?> LoadCover(string pictureId, CoverSize size, CancellationToken ct)
	{
		var definition = new PictureDefinition(pictureId, size == CoverSize.Large ? PictureSize._500x500 : PictureSize._80x80);
		var bytes = await Task.Run(() => PictureStorage.GetPictureSynchronously(definition, ct), ct);
		return bytes.Length == 0 ? null : bytes;
	}

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

	private readonly LocateScanner locateScanner = new();

	public async Task<LocateResult> LocateFile(string asin, string path)
	{
		if (Find(asin) is not { } lb)
			return new(LocateOutcome.NoSuchBook);
		if (!LocatePaths.TryResolveAudioFile(path, LibationFileManager.Configuration.Instance.Books?.PathWithoutPrefix, out var full, out var error))
			return new(LocateOutcome.Invalid, Error: error);

		FilePathCache.Insert(lb.Book.AudibleProductId, full);
		if (lb.Book.UserDefinedItem.BookStatus is not LiberatedStatus.Liberated)
			await lb.UpdateBookStatusAsync(LiberatedStatus.Liberated);
		return new(LocateOutcome.Ok, GetBookDetail(asin));
	}

	public LocateScanStart StartLocateScan(string folder, Action<EngineEvent> publish)
	{
		if (!LocatePaths.TryResolveFolder(folder, LibationFileManager.Configuration.Instance.Books?.PathWithoutPrefix, out var full, out var error))
			return new(LocateOutcome.Invalid, Error: error);
		return locateScanner.TryStart(full, publish) ? new(LocateOutcome.Ok, locateScanner.Current) : new(LocateOutcome.Busy);
	}

	public LocateScanDto GetLocateScan() => locateScanner.Current;

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

	public static BookDto ToDto(LibraryBook lb, Func<string, bool>? aaxcExists = null)
	{
		var book = lb.Book;
		var udi = book.UserDefinedItem;
		return new(
			book.AudibleProductId,
			book.Title,
			string.IsNullOrEmpty(book.Subtitle) ? null : book.Subtitle,
			book.Authors.Select(a => a.Name).ToList(),
			book.Narrators.Select(n => n.Name).ToList(),
			book.SeriesLink.Select(s => new SeriesDto(s.Series.Name ?? "", s.Order, s.Index, s.Series.AudibleSeriesId)).ToList(),
			book.LengthInMinutes,
			udi.BookStatus.ToString(),
			udi.PdfStatus?.ToString(),
			lb.DateAdded,
			lb.Account,
			book.Locale,
			book.ContentType.ToString(),
			book.ContentType == ContentType.Episode,
			udi.Tags ?? "",
			lb.IsDeleted,
			book.LowestCategoryNames(),
			ToDto(book.Rating),
			ToDto(udi.Rating),
			book.HasPdf,
			lb.IsAudiblePlus,
			lb.IncludedUntil,
			udi.LastDownloaded,
			udi.LastDownloadedVersion?.ToString(),
			udi.LastDownloadedFormat?.ToString(),
			book.IsSpatial,
			book.PictureId ?? book.PictureLarge,
			book.IsEpisodeChild() ? book.SeriesLink.FirstOrDefault()?.Series.AudibleSeriesId : null,
			lb.AbsentFromLastScan,
			!book.AudioExists && (aaxcExists ?? AudibleFileStorage.AaxcExists)(book.AudibleProductId),
			book.IsEpisodeParent());
	}

	public static BookDetailDto ToDetailDto(LibraryBook lb)
	{
		var book = lb.Book;
		return new(ToDto(lb))
		{
			Description = book.Description,
			Publisher = book.Publisher,
			Language = book.Language,
			Copyright = book.Copyright,
			IsAbridged = book.IsAbridged,
			DatePublished = book.DatePublished,
			CategoryLadders = book.Categories
				.Select(c => (IReadOnlyList<string>)c.CategoryLadder.Categories.Select(x => x.Name).ToList())
				.ToList(),
			AudioFileExists = AudibleFileStorage.Audio.GetPath(book.AudibleProductId) is not null
		};
	}

	private static RatingDto ToDto(Rating? rating)
		=> rating is null ? new(0, 0, 0) : new(rating.OverallRating, rating.PerformanceRating, rating.StoryRating);
}
