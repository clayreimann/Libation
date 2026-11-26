using ApplicationServices;
using AudibleApi;
using AudibleUtilities;
using DataLayer;
using LibationEngine.Contracts;
using LibationFileManager;

namespace LibationEngine;

public sealed class DbEngineData(IAudibleReviews? reviews = null, IAudibleQuality? quality = null, IAudibleRecords? records = null, IProgressSync? progress = null, IAudibleRemoval? removal = null) : IEngineData
{
	private readonly IAudibleRemoval audibleRemoval = removal ?? new AudibleRemoval();
	private readonly SemaphoreSlim removalGate = new(1, 1);
	private readonly IAudibleReviews audibleReviews = reviews ?? new AudibleReviews();
	private readonly QualityScanner qualityScanner = new(quality ?? new AudibleQuality());
	private readonly IAudibleRecords audibleRecords = records ?? new AudibleRecords();
	private readonly IProgressSync progressSync = progress ?? new ProgressSync();

	public IReadOnlyList<AccountDto> GetAccounts()
	{
		using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
		return persister.AccountsSettings.Accounts.Select(ToDto).ToList();
	}

	public AccountResult UpdateAccount(string accountId, string? locale, string? accountName, bool? libraryScan)
		=> Edit(accountId, locale, (persister, account) =>
		{
			if (accountName is not null)
				account.AccountName = accountName;
			if (libraryScan is { } scan)
				account.LibraryScan = scan;
			return ToDto(account);
		});

	public AccountResult DeleteAccount(string accountId, string? locale)
		=> Edit(accountId, locale, (persister, account) =>
		{
			var dto = ToDto(account);
			persister.AccountsSettings.Delete(account);
			return dto;
		});

	private static AccountResult Edit(string accountId, string? locale, Func<AccountsSettingsPersister, Account, AccountDto> change)
	{
		using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
		var wanted = string.IsNullOrWhiteSpace(locale) ? null : Localization.Get(locale.Trim()).Name;
		var matches = persister.AccountsSettings.Accounts
			.Where(a => string.Equals(a.AccountId, accountId.Trim(), StringComparison.OrdinalIgnoreCase)
				&& (string.IsNullOrWhiteSpace(locale) || (wanted is { Length: > 0 } && a.Locale?.Name == wanted)))
			.ToList();
		if (matches.Count == 0)
			return new(AccountOutcome.NotFound);
		if (matches.Count > 1)
			return new(AccountOutcome.Ambiguous);

		persister.BeginTransation();
		var dto = change(persister, matches[0]);
		persister.CommitTransation();
		return new(AccountOutcome.Ok, dto);
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
		var path = Path.Combine(Path.GetTempPath(), $"libation-export-{Guid.NewGuid():N}.{format.ToString().ToLowerInvariant()}");
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

	public IReadOnlyList<SeriesGroupDto> GetSeries(string? name = null)
		=> SeriesView.Group(DbContexts.GetLibrary_Flat_NoTracking())
			.Where(g => string.IsNullOrWhiteSpace(name) || g.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase))
			.ToList();

	public SeriesGroupDto? GetSeriesById(string seriesId)
		=> SeriesView.Group(DbContexts.GetLibrary_Flat_NoTracking())
			.FirstOrDefault(g => string.Equals(g.SeriesId, seriesId, StringComparison.OrdinalIgnoreCase));

	public IReadOnlyList<BookDto>? GetPodcastEpisodes(string parentAsin, bool trash = false)
	{
		var all = trash ? DbContexts.GetDeletedLibraryBooks() : DbContexts.GetLibrary_Flat_NoTracking(includeParents: true);
		if (all.FirstOrDefault(lb => lb.Book.IsEpisodeParent() && string.Equals(lb.Book.AudibleProductId, parentAsin, StringComparison.OrdinalIgnoreCase)) is not { } parent)
			return null;

		var partial = AudibleFileStorage.AaxcExistsSnapshot();
		return SeriesView.EpisodeOrder(all.FindChildren(parent).ToList(), parent.Book.AudibleProductId).Select(lb => ToDto(lb, partial)).ToList();
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

	public AudioLookup FindAudio(string asin)
	{
		if (Find(asin) is not { } lb)
			return new(AudioOutcome.NoSuchBook);
		if (lb.Book.UserDefinedItem.BookStatus is not LiberatedStatus.Liberated)
			return new(AudioOutcome.NotLiberated);
		if (AudibleFileStorage.Audio.GetPath(asin)?.PathWithoutPrefix is not { } primary)
			return new(AudioOutcome.FilesMissing);

		var paths = AudioFiles.PlayOrder(primary, AudibleFileStorage.Audio.GetPaths(asin).Select(p => p.PathWithoutPrefix));
		var cue = FilePathCache.GetFiles(asin).Where(f => f.fileType == FileType.Cue).Select(f => f.path.PathWithoutPrefix).FirstOrDefault()
			?? Path.ChangeExtension(primary, ".cue");
		return new(AudioOutcome.Ok, paths, cue);
	}

	public async Task<byte[]?> LoadCover(string pictureId, CoverSize size, CancellationToken ct)
	{
		var definition = new PictureDefinition(pictureId, size == CoverSize.Large ? PictureSize._500x500 : PictureSize._80x80);
		var bytes = await Task.Run(() => PictureStorage.GetPictureSynchronously(definition, ct), ct);
		return bytes.Length == 0 ? null : bytes;
	}

	public async Task<RatingResult> SetMyRating(string asin, int overall, int performance, int story, CancellationToken ct)
	{
		if (Find(asin) is not { } lb)
			return new(RatingOutcome.NoSuchBook);
		if (overall == 0)
			return new(RatingOutcome.Ok, GetBookDetail(asin));

		try
		{
			if (!await audibleReviews.SubmitAsync(lb, overall, performance, story, ct))
				return new(RatingOutcome.AudibleFailed, Error: "Audible rejected the rating.");
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return new(RatingOutcome.AudibleFailed, Error: $"Audible could not be reached: {ex.Message}");
		}

		await lb.UpdateUserDefinedItemAsync(rating: new Rating(overall, performance, story));
		return new(RatingOutcome.Ok, GetBookDetail(asin));
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

	public async Task<AudiobookshelfTestDto> TestAudiobookshelf(string? url, string? token, CancellationToken ct)
	{
		var config = LibationFileManager.Configuration.Instance;
		url ??= config.AudiobookshelfServerUrl;
		token ??= config.AudiobookshelfApiToken;
		if (string.IsNullOrWhiteSpace(url))
			return new(false, Error: "No server URL is saved.", Code: AudiobookshelfApiService.TestCodes.NotConfigured);
		var result = await AudiobookshelfApiService.TestConnectionAsync(url, token, ct);
		return new(result.Ok, result.ServerVersion, result.User, result.Error, result.Code);
	}

	public async Task<AudiobookshelfLibrariesResult> GetAudiobookshelfLibraries(CancellationToken ct)
	{
		var config = LibationFileManager.Configuration.Instance;
		if (string.IsNullOrWhiteSpace(config.AudiobookshelfServerUrl) || string.IsNullOrWhiteSpace(config.AudiobookshelfApiToken))
			return new(null, "Audiobookshelf has no saved server URL and API token.", AudiobookshelfApiService.TestCodes.NotConfigured);
		try
		{
			var libraries = await AudiobookshelfApiService.GetLibrariesAsync(config.AudiobookshelfServerUrl, config.AudiobookshelfApiToken).WaitAsync(ct);
			return new(libraries.Select(l => new AudiobookshelfLibraryDto(l.Id, l.Name, l.MediaType, l.Folders.Select(f => new AudiobookshelfFolderDto(f.Id, f.FullPath)).ToList())).ToList());
		}
		catch (Exception ex) when (ex is ArgumentException or HttpRequestException or Newtonsoft.Json.JsonException)
		{
			return new(null, ex.Message, AudiobookshelfApiService.ClassifyFailure(ex));
		}
	}

	private static readonly object SettingsGate = new();

	public SettingsUpdateResult UpdateSettings(System.Text.Json.JsonElement patch)
	{
		lock (SettingsGate)
			return SettingsWriter.Apply(LibationFileManager.Configuration.Instance, patch);
	}

	public TemplatePreviewResult PreviewTemplate(string kind, string template, string? asin)
	{
		LibraryBook? book = null;
		if (asin is not null && (book = Find(asin)) is null)
			return new(false, null);
		return new(true, TemplatePreview.Render(LibationFileManager.Configuration.Instance, kind, template, book));
	}

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

	public QualityScanStart StartQualityScan(IReadOnlyList<string>? asins, bool? widevine, Action<EngineEvent> publish)
	{
		var wanted = asins is null ? null : new HashSet<string>(asins, StringComparer.OrdinalIgnoreCase);
		var books = DbContexts.GetLibrary_Flat_NoTracking()
			.Where(QualityCheck.ShouldScan)
			.Where(lb => wanted is null || wanted.Contains(lb.Book.AudibleProductId))
			.OrderBy(lb => lb.Book.AudibleProductId, StringComparer.Ordinal)
			.ToList();
		if (books.Count == 0)
			return new(QualityOutcome.Invalid, Error: "No liberated, non-spatial books to scan.");
		return qualityScanner.TryStart(books, widevine ?? LibationFileManager.Configuration.Instance.UseWidevine, publish)
			? new(QualityOutcome.Ok, qualityScanner.Current)
			: new(QualityOutcome.Busy);
	}

	public QualityScanDto GetQualityScan() => qualityScanner.Current;

	public QualityScanStart CancelQualityScan(Action<EngineEvent> publish)
		=> qualityScanner.Cancel(publish) is { } scan ? new(QualityOutcome.Ok, scan) : new(QualityOutcome.NotRunning);

	public async Task<QualityApplyResult> ApplyQualityUpgrades(IReadOnlyList<string> asins)
	{
		if (qualityScanner.Running)
			return new(QualityOutcome.Busy);

		var scan = qualityScanner.Current;
		var hits = (scan.Results ?? []).ToDictionary(r => r.Asin, StringComparer.OrdinalIgnoreCase);
		var books = new List<LibraryBook>();
		var applied = new List<QualityResultDto>();
		var missing = new List<string>();
		foreach (var asin in asins)
		{
			if (hits.TryGetValue(asin, out var hit) && DbContexts.GetLibraryBook_Flat_NoTracking(hit.Asin) is { } lb)
			{
				books.Add(lb);
				applied.Add(hit);
			}
			else
				missing.Add(asin);
		}

		var settings = new QualityUpgradeSettings(false, false, false);
		if (books.Count > 0)
		{
			await books.UpdateBookStatusAsync(LiberatedStatus.NotLiberated);
			qualityScanner.Remove(applied.Select(r => r.Asin).ToHashSet(StringComparer.OrdinalIgnoreCase));
			settings = QualityCheck.ApplyUpgradeSettings(scan.Widevine, applied.Select(r => (string?)r.AvailableQuality.Codec).ToArray());
		}
		return new(QualityOutcome.Ok, new(books.Count, missing, settings.WidevineEnabled, settings.XHeAacRequested, settings.AccountsMarkedForReRegistration));
	}

	public async Task<BookmarksResult> GetBookmarks(string asin, CancellationToken ct)
	{
		var fetched = await FetchRecords(asin, ct);
		return fetched.Outcome is BookmarksOutcome.Ok
			? new(BookmarksOutcome.Ok, fetched.Records!.Select(ToBookmarkDto).ToList())
			: new(fetched.Outcome, Error: fetched.Error);
	}

	public async Task<BookmarksResult> ExportBookmarks(string asin, ExportFormat format, CancellationToken ct)
	{
		var fetched = await FetchRecords(asin, ct);
		if (fetched.Outcome is not BookmarksOutcome.Ok)
			return new(fetched.Outcome, Error: fetched.Error);
		if (fetched.Records!.Count == 0)
			return new(BookmarksOutcome.Ok);

		var path = Path.Combine(Path.GetTempPath(), $"libation-records-{Guid.NewGuid():N}.{format.ToString().ToLowerInvariant()}");
		try
		{
			switch (format)
			{
				case ExportFormat.Csv:
					RecordExporter.ToCsv(path, fetched.Records);
					break;
				case ExportFormat.Xlsx:
					RecordExporter.ToXlsx(path, fetched.Records);
					break;
				default:
					RecordExporter.ToJson(path, fetched.Book!, fetched.Records);
					break;
			}
		}
		catch
		{
			try { File.Delete(path); } catch (IOException) { }
			throw;
		}
		return new(BookmarksOutcome.Ok, FilePath: path);
	}

	public async Task<BookProgressResult> GetProgress(string asin, CancellationToken ct)
	{
		if (Find(asin) is not { } lb)
			return new(ProgressOutcome.NoSuchBook);
		try
		{
			var snapshot = await progressSync.ReadAsync(lb, AbsConnection.From(LibationFileManager.Configuration.Instance), ct);
			return new(ProgressOutcome.Ok, new(
				snapshot.Audible is { } a ? new(a.PositionMs, a.LastUpdated) : null,
				snapshot.Audiobookshelf is { } s ? new(s.CurrentTimeSeconds, s.IsFinished, s.LastUpdate) : null,
				snapshot.AbsItemId));
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return new(ProgressOutcome.ServiceFailed, Error: $"Progress could not be read: {ex.Message}");
		}
	}

	public async Task<ProgressPushResult> PushProgress(string asin, bool force, bool dryRun, CancellationToken ct)
	{
		if (Find(asin) is not { } lb)
			return new(ProgressOutcome.NoSuchBook);
		if (AbsConnection.From(LibationFileManager.Configuration.Instance) is not { } connection)
			return new(ProgressOutcome.NotConfigured);
		try
		{
			var result = (await progressSync.PushAsync([lb], connection, force, dryRun, ct))[0];
			return new(ProgressOutcome.Ok, ToPushDto(result));
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return new(ProgressOutcome.Ok, new(asin, "failed", ex.Message));
		}
	}

	internal static ProgressPushDto ToPushDto(BookPushResult r)
		=> new(r.Asin, r.Pushed ? "pushed" : r.Failed ? "failed" : "skipped", r.Outcome is AudiobookshelfProgressSync.Outcome.Updated ? null : r.Message, r.PositionSeconds);

	private async Task<(BookmarksOutcome Outcome, LibraryBook? Book, IReadOnlyList<AudibleApi.Common.IRecord>? Records, string? Error)> FetchRecords(string asin, CancellationToken ct)
	{
		if (Find(asin) is not { } lb)
			return (BookmarksOutcome.NoSuchBook, null, null, null);
		try
		{
			return (BookmarksOutcome.Ok, lb, await audibleRecords.GetAsync(lb, ct), null);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return (BookmarksOutcome.AudibleFailed, lb, null, $"Audible could not be reached: {ex.Message}");
		}
	}

	private static BookmarkDto ToBookmarkDto(AudibleApi.Common.IRecord record)
	{
		var range = record as AudibleApi.Common.IRangeAnnotation;
		return new(record.GetType().Name, record.Start.TotalSeconds, range?.End.TotalSeconds, range?.Text, (record as AudibleApi.Common.Clip)?.Title, record.Created);
	}

	public async Task<IReadOnlyList<RemoveFromAudibleItemDto>> RemoveFromAudible(IReadOnlyList<string> asins, Action<EngineEvent> publish)
	{
		await removalGate.WaitAsync();
		try
		{
			var results = new List<RemoveFromAudibleItemDto>(asins.Count);
			var removed = new List<string>();
			foreach (var asin in asins)
			{
				var item = await RemoveOneFromAudible(asin);
				results.Add(item);
				if (item.Result == RemoveFromAudibleResults.Removed)
					removed.Add(asin);
			}
			if (removed.Count > 0)
				publish(new BooksRemovedFromAudible(removed));
			return results;
		}
		finally
		{
			removalGate.Release();
		}
	}

	private async Task<RemoveFromAudibleItemDto> RemoveOneFromAudible(string asin)
	{
		if (Find(asin) is not { } lb)
			return new(asin, RemoveFromAudibleResults.NotFound);
		if (!lb.IsAudiblePlus)
			return new(asin, RemoveFromAudibleResults.NotPlus);

		try
		{
			if (!await audibleRemoval.RemoveAsync(lb, CancellationToken.None))
			{
				Serilog.Log.Logger.Warning("Audible declined to remove {Asin} from the account", asin);
				return new(asin, RemoveFromAudibleResults.Failed, "Audible declined to remove the book.");
			}
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Error(ex, "Failed to remove {Asin} from the Audible account", asin);
			return new(asin, RemoveFromAudibleResults.Failed, $"Audible could not be reached: {ex.Message}");
		}

		Serilog.Log.Logger.Information("Removed {Asin} from the Audible account", asin);
		try
		{
			await new[] { lb }.PermanentlyDeleteBooksAsync();
			return new(asin, RemoveFromAudibleResults.Removed);
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Error(ex, "Removed {Asin} from Audible but could not delete it from the library", asin);
			return new(asin, RemoveFromAudibleResults.Removed, $"Removed from Audible but could not be deleted from the library: {ex.Message}");
		}
	}

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
		=> new(account.AccountId, account.AccountName, account.Locale?.Name, account.LibraryScan,
			AccountCredentialStatus.GetSignInStatus(account) == AccountSignInStatus.NotSignedIn || AccountCredentialStatus.LooksLikeMissingCredentials(account)
				? AuthStates.NeedsLogin : AuthStates.SignedIn,
			AccountCredentialStatus.GetSignInStatus(account) switch
			{
				AccountSignInStatus.Modern => CredentialKinds.Modern,
				AccountSignInStatus.Legacy => CredentialKinds.Legacy,
				_ => CredentialKinds.None
			},
			account.AdditionalLocales.Select(l => l.Name).ToList());

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
