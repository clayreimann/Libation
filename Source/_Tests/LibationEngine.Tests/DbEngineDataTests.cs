using ApplicationServices;
using DataLayer;
using LibationEngine.Contracts;
using LibationFileManager;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
[DoNotParallelize]
public class DbEngineDataTests
{
	private string directory = "";
	private string booksDirectory = "";
	private string? previousDirectory;
	private readonly DbEngineData data = new();

	[TestInitialize]
	public void Init()
	{
		directory = Path.Combine(Path.GetTempPath(), $"libation-engine-db-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		previousDirectory = Environment.GetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR);
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, directory);
		Configuration.CreateMockInstance();
		booksDirectory = Path.Combine(directory, "Books");
		Directory.CreateDirectory(booksDirectory);
		Configuration.Instance.Books = booksDirectory;

		using var context = DbContexts.GetContext();
		AddBook(context, "B0PLAIN0001", pdf: false);
		AddBook(context, "B0PDF000001", pdf: true);
		AddBook(context, "B0TRASHED01", pdf: false, deleted: true);
		context.SaveChanges();
	}

	[TestCleanup]
	public void Cleanup()
	{
		Configuration.RestoreSingletonInstance();
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, previousDirectory);
		Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
		try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
	}

	private static void AddBook(LibationContext context, string asin, bool pdf, bool deleted = false)
	{
		var book = new Book(new AudibleProductId(asin), asin, "", "Description", 600,
			ContentType.Product, [new Contributor(asin + " author")], [new Contributor(asin + " narrator")], "us");
		if (pdf)
		{
			book.SetSupplementDownloadUrl("https://example.com/a.pdf");
		}
		context.LibraryBooks.Add(new LibraryBook(book, new DateTime(2020, 1, 1), "acct") { IsDeleted = deleted });
	}

	[DataTestMethod]
	[DataRow(ExportFormat.Csv, ".csv")]
	[DataRow(ExportFormat.Json, ".json")]
	[DataRow(ExportFormat.Xlsx, ".xlsx")]
	public void ExportLibrary_writes_a_real_file_for_every_format(ExportFormat format, string extension)
	{
		var path = data.ExportLibrary(format);
		try
		{
			Assert.AreEqual(extension, Path.GetExtension(path));
			Assert.IsTrue(new FileInfo(path).Length > 0);
			if (format == ExportFormat.Xlsx)
				using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
					Assert.IsNotNull(zip.GetEntry("xl/workbook.xml"));
		}
		finally
		{
			File.Delete(path);
		}
	}

	private static string[] Results(IReadOnlyList<BulkItemDto> items) => items.Select(i => $"{i.Asin}:{i.Result}").ToArray();

	[TestMethod]
	public async Task BulkSetTags_sets_adds_and_removes()
	{
		string[] asins = ["B0PLAIN0001", "B0PDF000001", "B0MISSING"];
		CollectionAssert.AreEqual(
			new[] { "B0PLAIN0001:ok", "B0PDF000001:ok", "B0MISSING:notFound" },
			Results(await data.BulkSetTags(asins, new("alpha beta", null, null))));
		Assert.AreEqual("alpha beta", data.GetBook("B0PDF000001")!.Tags);

		await data.BulkSetTags(asins, new(null, "Gamma beta new-tag", null));
		Assert.AreEqual("alpha beta gamma new_tag", data.GetBook("B0PLAIN0001")!.Tags);

		await data.BulkSetTags(asins, new(null, null, "alpha gamma"));
		Assert.AreEqual("beta new_tag", data.GetBook("B0PLAIN0001")!.Tags);

		await data.BulkSetTags(asins, new("", null, null));
		Assert.AreEqual("", data.GetBook("B0PLAIN0001")!.Tags);
	}

	[TestMethod]
	public async Task BulkSetBookStatus_updates_every_book()
	{
		await data.BulkSetBookStatus(["B0PLAIN0001", "B0PDF000001"], LiberatedStatus.Liberated);
		Assert.AreEqual("Liberated", data.GetBook("B0PLAIN0001")!.BookStatus);
		Assert.AreEqual("Liberated", data.GetBook("B0PDF000001")!.BookStatus);

		await data.BulkSetBookStatus(["B0PLAIN0001"], LiberatedStatus.NotLiberated);
		Assert.AreEqual("NotLiberated", data.GetBook("B0PLAIN0001")!.BookStatus);
		Assert.AreEqual("Liberated", data.GetBook("B0PDF000001")!.BookStatus);
	}

	[TestMethod]
	public async Task BulkSetPdfStatus_conflicts_on_books_without_a_pdf()
	{
		CollectionAssert.AreEqual(
			new[] { "B0PLAIN0001:conflict", "B0PDF000001:ok" },
			Results(await data.BulkSetPdfStatus(["B0PLAIN0001", "B0PDF000001"], LiberatedStatus.Liberated)));
		Assert.IsNull(data.GetBook("B0PLAIN0001")!.PdfStatus);
		Assert.AreEqual("Liberated", data.GetBook("B0PDF000001")!.PdfStatus);
	}

	[TestMethod]
	public async Task BulkSetTrash_trashes_and_restores_and_is_idempotent()
	{
		string[] asins = ["B0PLAIN0001", "B0TRASHED01"];
		CollectionAssert.AreEqual(new[] { "B0PLAIN0001:ok", "B0TRASHED01:ok" }, Results(await data.BulkSetTrash(asins, true)));
		Assert.IsTrue(data.GetBook("B0PLAIN0001")!.InTrash);
		Assert.AreEqual(1, data.GetLibrary(0, 10).Total);
		Assert.AreEqual(2, data.GetLibrary(0, 10, trash: true).Total);

		await data.BulkSetTrash(asins, false);
		Assert.IsFalse(data.GetBook("B0PLAIN0001")!.InTrash);
		Assert.IsFalse(data.GetBook("B0TRASHED01")!.InTrash);
		Assert.AreEqual(3, data.GetLibrary(0, 10).Total);
	}

	[TestMethod]
	public async Task BulkPurge_deletes_only_trashed_books()
	{
		CollectionAssert.AreEqual(
			new[] { "B0PLAIN0001:conflict", "B0TRASHED01:ok", "B0MISSING:notFound" },
			Results(await data.BulkPurge(["B0PLAIN0001", "B0TRASHED01", "B0MISSING"])));

		Assert.IsNull(data.GetBook("B0TRASHED01"));
		Assert.IsNotNull(data.GetBook("B0PLAIN0001"));
		Assert.AreEqual(0, data.GetLibrary(0, 10, trash: true).Total);
		using var context = DbContexts.GetContext();
		Assert.AreEqual(2, context.Books.Count());
	}

	[TestMethod]
	public void GetLibrary_sorts_and_reverses()
	{
		string[] Asins(LibrarySortKey? key, bool desc) => data.GetLibrary(0, 10, sort: key, descending: desc).Items.Select(b => b.Asin).ToArray();

		CollectionAssert.AreEqual(new[] { "B0PDF000001", "B0PLAIN0001" }, Asins(LibrarySortKey.Author, false));
		CollectionAssert.AreEqual(new[] { "B0PLAIN0001", "B0PDF000001" }, Asins(LibrarySortKey.Author, true));
		Assert.HasCount(2, Asins(null, false));
	}

	[TestMethod]
	public void GetLibrary_lists_the_trash_and_filters_it_with_a_query()
	{
		CollectionAssert.AreEqual(new[] { "B0TRASHED01" }, data.GetLibrary(0, 10, trash: true).Items.Select(b => b.Asin).ToArray());
		Assert.AreEqual(1, data.GetLibrary(0, 10, "B0TRASHED01", trash: true).Total);
		Assert.AreEqual(0, data.GetLibrary(0, 10, "author:nobody", trash: true).Total);
		Assert.Throws<SearchQueryException>(() => data.GetLibrary(0, 10, "bad[", trash: true));
	}

	[TestMethod]
	public void GetLibraryAsins_returns_every_match()
	{
		var live = data.GetLibraryAsins();
		Assert.AreEqual(2, live.Total);
		CollectionAssert.AreEquivalent(new[] { "B0PLAIN0001", "B0PDF000001" }, live.Asins.ToArray());
		CollectionAssert.AreEqual(new[] { "B0TRASHED01" }, data.GetLibraryAsins(trash: true).Asins.ToArray());
	}

	[TestMethod]
	public void Books_carry_the_richer_fields_and_detail_adds_file_state()
	{
		var pdf = data.GetBook("B0PDF000001")!;
		Assert.IsTrue(pdf.HasPdf);
		Assert.IsFalse(data.GetBook("B0PLAIN0001")!.HasPdf);
		Assert.IsFalse(pdf.AbsentFromLastScan);

		var detail = data.GetBookDetail("B0PLAIN0001")!;
		Assert.AreEqual("Description", detail.Description);
		Assert.IsFalse(detail.AudioFileExists);
		Assert.IsFalse(detail.PartialDownload);
		Assert.IsNull(data.GetBookDetail("nope"));
		Assert.IsNotNull(data.GetBookDetail("B0TRASHED01"));
	}

	[TestMethod]
	public void GetBackupCounts_counts_from_the_database()
	{
		var counts = data.GetBackupCounts();
		Assert.AreEqual(2, counts.Total);
		Assert.AreEqual(1, counts.PdfNotLiberated);
	}

	[TestMethod]
	public async Task Reconcile_finds_nothing_outside_Books_and_never_downgrades()
	{
		var outside = Path.Combine(directory, "elsewhere");
		Directory.CreateDirectory(outside);
		File.WriteAllText(Path.Combine(outside, "B0PLAIN0001.m4b"), "x");
		await data.SetBookStatus("B0PDF000001", LiberatedStatus.Liberated);

		var books = DbContexts.GetLibrary_Flat_NoTracking();
		var reconcile = new BulkSetDownloadStatus(books, setDownloaded: true, setNotDownloaded: false);

		Assert.AreEqual(0, reconcile.Discover());
		await reconcile.ExecuteAsync();
		Assert.AreEqual("NotLiberated", data.GetBook("B0PLAIN0001")!.BookStatus);
		Assert.AreEqual("Liberated", data.GetBook("B0PDF000001")!.BookStatus);
	}

	private string WriteFile(string folder, string name, byte[]? content = null)
	{
		Directory.CreateDirectory(folder);
		var path = Path.Combine(folder, name);
		File.WriteAllBytes(path, content ?? [1]);
		return path;
	}

	private static byte[] Mp3WithAsin(string asin)
	{
		var frame = new List<byte> { 0 };
		frame.AddRange(System.Text.Encoding.ASCII.GetBytes("AUDIBLE_ASIN"));
		frame.Add(0);
		frame.AddRange(System.Text.Encoding.ASCII.GetBytes(asin));

		var body = new List<byte>(System.Text.Encoding.ASCII.GetBytes("TXXX"));
		body.AddRange([0, 0, 0, (byte)frame.Count, 0, 0]);
		body.AddRange(frame);

		var tag = new List<byte>(System.Text.Encoding.ASCII.GetBytes("ID3"));
		tag.AddRange([3, 0, 0, 0, 0, (byte)(body.Count >> 7), (byte)(body.Count & 0x7F)]);
		tag.AddRange(body);
		return [.. tag];
	}

	[TestMethod]
	public async Task LocateFile_records_a_file_under_Books_and_marks_the_book_liberated()
	{
		var path = WriteFile(Path.Combine(booksDirectory, "Some Author", "Some Book"), "no asin in this name.m4b");

		var result = await data.LocateFile("B0PLAIN0001", path);

		Assert.AreEqual(LocateOutcome.Ok, result.Outcome);
		Assert.AreEqual("Liberated", result.Book!.BookStatus);
		Assert.IsTrue(result.Book.AudioFileExists);
		Assert.AreEqual(path, AudibleFileStorage.Audio.GetPath("B0PLAIN0001")?.Path);
		Assert.AreEqual("Liberated", data.GetBook("B0PLAIN0001")!.BookStatus);
	}

	[TestMethod]
	public async Task LocateFile_rejects_paths_outside_Books_traversal_and_non_audio()
	{
		var outside = WriteFile(Path.Combine(directory, "elsewhere"), "B0PLAIN0001.m4b");
		var sibling = WriteFile(booksDirectory + "2", "B0PLAIN0001.m4b");
		var text = WriteFile(booksDirectory, "notes.txt");
		var inside = WriteFile(booksDirectory, "real.m4b");

		string[] bad =
		[
			outside,
			sibling,
			text,
			Path.Combine(booksDirectory, "missing.m4b"),
			Path.Combine(booksDirectory, "..", "elsewhere", "B0PLAIN0001.m4b"),
			Path.Combine(booksDirectory, "sub", "..", "real.m4b"),
			"real.m4b",
			"",
			booksDirectory
		];
		foreach (var path in bad)
		{
			var result = await data.LocateFile("B0PLAIN0001", path);
			Assert.AreEqual(LocateOutcome.Invalid, result.Outcome, path);
			Assert.IsFalse(string.IsNullOrEmpty(result.Error), path);
		}

		Assert.AreEqual("NotLiberated", data.GetBook("B0PLAIN0001")!.BookStatus);
		Assert.AreEqual(LocateOutcome.NoSuchBook, (await data.LocateFile("nope", inside)).Outcome);
	}

	[TestMethod]
	public async Task FindAudio_resolves_a_liberated_books_files_in_play_order_with_its_cue()
	{
		var folder = Path.Combine(booksDirectory, "Author", "Book");
		var ten = WriteFile(folder, "Book - 10 [B0PLAIN0001].m4b");
		var two = WriteFile(folder, "Book - 2 [B0PLAIN0001].m4b");
		WriteFile(folder, "Book - 2 [B0PLAIN0001].mp3");
		var cue = WriteFile(folder, "Book.cue");
		FilePathCache.Insert("B0PLAIN0001", ten);
		FilePathCache.Insert("B0PLAIN0001", two);
		FilePathCache.Insert("B0PLAIN0001", cue);
		await data.SetBookStatus("B0PLAIN0001", LiberatedStatus.Liberated);

		var audio = data.FindAudio("B0PLAIN0001");

		Assert.AreEqual(AudioOutcome.Ok, audio.Outcome);
		CollectionAssert.AreEqual(new[] { two, ten }, audio.Paths!.ToArray());
		Assert.AreEqual(cue, audio.CuePath);
	}

	[TestMethod]
	public async Task FindAudio_looks_for_a_cue_beside_a_single_file_when_none_is_indexed()
	{
		var book = WriteFile(booksDirectory, "Single [B0PDF000001].m4b");
		FilePathCache.Insert("B0PDF000001", book);
		await data.SetBookStatus("B0PDF000001", LiberatedStatus.Liberated);

		Assert.AreEqual(Path.Combine(booksDirectory, "Single [B0PDF000001].cue"), data.FindAudio("B0PDF000001").CuePath);
	}

	[TestMethod]
	public async Task FindAudio_says_why_a_book_has_no_audio()
	{
		Assert.AreEqual(AudioOutcome.NoSuchBook, data.FindAudio("B0NOSUCH001").Outcome);

		WriteFile(booksDirectory, "B0PLAIN0001.m4b");
		FilePathCache.Insert("B0PLAIN0001", Path.Combine(booksDirectory, "B0PLAIN0001.m4b"));
		Assert.AreEqual(AudioOutcome.NotLiberated, data.FindAudio("B0PLAIN0001").Outcome);

		await data.SetBookStatus("B0PDF000001", LiberatedStatus.Liberated);
		Assert.AreEqual(AudioOutcome.FilesMissing, data.FindAudio("B0PDF000001").Outcome);

		await data.SetBookStatus("B0PLAIN0001", LiberatedStatus.Liberated);
		File.Delete(Path.Combine(booksDirectory, "B0PLAIN0001.m4b"));
		Assert.AreEqual(AudioOutcome.FilesMissing, data.FindAudio("B0PLAIN0001").Outcome);
	}

	[TestMethod]
	public async Task FindAudio_finds_a_trashed_liberated_book()
	{
		var path = WriteFile(booksDirectory, "B0TRASHED01.m4b");
		FilePathCache.Insert("B0TRASHED01", path);
		await data.SetBookStatus("B0TRASHED01", LiberatedStatus.Liberated);

		Assert.AreEqual(AudioOutcome.Ok, data.FindAudio("B0TRASHED01").Outcome);
	}

	[TestMethod]
	public async Task LocateFile_without_a_Books_folder_is_invalid()
	{
		var inside = WriteFile(booksDirectory, "real.m4b");
		Configuration.Instance.Books = null;
		Assert.AreEqual(LocateOutcome.Invalid, (await data.LocateFile("B0PLAIN0001", inside)).Outcome);
	}

	[TestMethod]
	public async Task LocateScan_matches_embedded_asins_and_marks_books_liberated()
	{
		WriteFile(Path.Combine(booksDirectory, "found"), "untagged-name.mp3", Mp3WithAsin("B0PLAIN0001"));
		WriteFile(Path.Combine(booksDirectory, "found"), "other.mp3", Mp3WithAsin("B0UNKNOWN01"));
		WriteFile(Path.Combine(booksDirectory, "found"), "no tag.mp3");
		var events = new List<LocateScanDto>();

		var start = data.StartLocateScan(Path.Combine(booksDirectory, "found"), e => events.Add(((LocateScanProgress)e).Scan));
		Assert.AreEqual(LocateOutcome.Ok, start.Outcome);

		for (var i = 0; i < 100 && data.GetLocateScan().State == "running"; i++)
			await Task.Delay(100);

		var scan = data.GetLocateScan();
		Assert.AreEqual("completed", scan.State);
		Assert.AreEqual(2, scan.Found);
		Assert.AreEqual(1, scan.Matched);
		Assert.AreEqual(1, scan.Updated);
		Assert.AreEqual("running", events[0].State);
		Assert.AreEqual("completed", events[^1].State);
		Assert.AreEqual("Liberated", data.GetBook("B0PLAIN0001")!.BookStatus);
		Assert.AreEqual("NotLiberated", data.GetBook("B0PDF000001")!.BookStatus);
	}

	[TestMethod]
	public async Task LocateScan_reads_the_asin_from_an_mp3_tagged_by_ffmpeg()
	{
		var folder = Path.Combine(booksDirectory, "real");
		Directory.CreateDirectory(folder);
		File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tagged-by-ffmpeg.mp3"), Path.Combine(folder, "book.mp3"));

		Assert.AreEqual(LocateOutcome.Ok, data.StartLocateScan(folder, _ => { }).Outcome);
		for (var i = 0; i < 100 && data.GetLocateScan().State == "running"; i++)
			await Task.Delay(100);

		var scan = data.GetLocateScan();
		Assert.AreEqual(1, scan.Found);
		Assert.AreEqual(1, scan.Matched);
		Assert.AreEqual("Liberated", data.GetBook("B0PLAIN0001")!.BookStatus);
	}

	[TestMethod]
	public async Task List_pages_flag_a_partial_download_without_marking_liberated_books()
	{
		Configuration.Instance.InProgress = Path.Combine(directory, "InProgress");
		using (var context = DbContexts.GetContext())
		{
			AddBook(context, "B0PARTIAL01", pdf: false);
			AddBook(context, "B0LIBPART01", pdf: false);
			context.SaveChanges();
		}
		var inProgress = AudibleFileStorage.DownloadsInProgressDirectory!;
		File.WriteAllText(Path.Combine(inProgress, "B0PARTIAL01.aaxc"), "x");
		File.WriteAllText(Path.Combine(inProgress, "B0LIBPART01.aaxc"), "x");
		await data.SetBookStatus("B0LIBPART01", LiberatedStatus.Liberated);

		var items = data.GetLibrary(0, 10).Items.ToDictionary(b => b.Asin);
		Assert.IsTrue(items["B0PARTIAL01"].PartialDownload);
		Assert.IsFalse(items["B0LIBPART01"].PartialDownload);
		Assert.IsFalse(items["B0PLAIN0001"].PartialDownload);
		Assert.IsTrue(data.GetBook("B0PARTIAL01")!.PartialDownload);
		Assert.IsTrue(data.GetBookDetail("B0PARTIAL01")!.PartialDownload);
	}

	[TestMethod]
	public void Podcast_parents_come_back_beside_their_episodes_and_never_in_the_library_list()
	{
		using (var context = DbContexts.GetContext())
		{
			var parent = new Book(new AudibleProductId("B0SHOW00001"), "The Show", "", "", 0, ContentType.Parent, [new Contributor("host")], [new Contributor("host")], "us");
			parent.UpsertSeries(new Series(new AudibleSeriesId("B0SHOW00001"), "The Show"), "");
			var episode = new Book(new AudibleProductId("B0EPISODE01"), "Episode One", "", "", 5, ContentType.Episode, [new Contributor("host")], [new Contributor("host")], "us");
			episode.UpsertSeries(new Series(new AudibleSeriesId("B0SHOW00001"), "The Show"), "1");
			var other = new Book(new AudibleProductId("B0OTHERSHOW"), "Other Show", "", "", 0, ContentType.Parent, [new Contributor("host")], [new Contributor("host")], "us");
			other.UpsertSeries(new Series(new AudibleSeriesId("B0OTHERSHOW"), "Other Show"), "");
			foreach (var b in new[] { parent, episode, other })
				context.LibraryBooks.Add(new LibraryBook(b, new DateTime(2020, 1, 1), "acct"));
			context.SaveChanges();
		}

		var parents = data.GetPodcastParents();
		CollectionAssert.AreEqual(new[] { "B0SHOW00001" }, parents.Select(p => p.Asin).ToArray());
		Assert.IsTrue(parents[0].IsPodcastParent);
		Assert.IsNull(parents[0].ParentAsin);

		var episodes = data.GetLibrary(0, 20).Items;
		Assert.IsFalse(episodes.Any(b => b.IsPodcastParent));
		Assert.AreEqual("B0SHOW00001", episodes.Single(b => b.Asin == "B0EPISODE01").ParentAsin);
	}

	[TestMethod]
	public void Podcast_parent_series_is_the_parents_own_links_as_in_the_desktop_grid()
	{
		using (var context = DbContexts.GetContext())
		{
			var linked = new Book(new AudibleProductId("B0LINKED001"), "Linked Show", "", "", 0, ContentType.Parent, [new Contributor("host")], [new Contributor("host")], "us");
			linked.UpsertSeries(new Series(new AudibleSeriesId("B0LINKED001"), "Linked Show"), "");
			var bare = new Book(new AudibleProductId("B0BARE000001"), "Bare Show", "", "", 0, ContentType.Parent, [new Contributor("host")], [new Contributor("host")], "us");
			var one = new Book(new AudibleProductId("B0EPISODE02"), "Linked Episode", "", "", 5, ContentType.Episode, [new Contributor("host")], [new Contributor("host")], "us");
			one.UpsertSeries(new Series(new AudibleSeriesId("B0LINKED001"), "Linked Show"), "1");
			var two = new Book(new AudibleProductId("B0EPISODE03"), "Bare Episode", "", "", 5, ContentType.Episode, [new Contributor("host")], [new Contributor("host")], "us");
			two.UpsertSeries(new Series(new AudibleSeriesId("B0BARE000001"), "Bare Show"), "1");
			foreach (var b in new[] { linked, bare, one, two })
				context.LibraryBooks.Add(new LibraryBook(b, new DateTime(2020, 1, 1), "acct"));
			context.SaveChanges();
		}

		var parents = data.GetPodcastParents().ToDictionary(p => p.Asin);
		Assert.AreEqual("Linked Show", parents["B0LINKED001"].Series.Single().Name);
		Assert.AreEqual(0, parents["B0BARE000001"].Series.Count);
	}

	[TestMethod]
	public void LocateScan_rejects_folders_outside_Books()
	{
		var elsewhere = Path.Combine(directory, "elsewhere");
		Directory.CreateDirectory(elsewhere);

		Assert.AreEqual(LocateOutcome.Invalid, data.StartLocateScan(elsewhere, _ => { }).Outcome);
		Assert.AreEqual(LocateOutcome.Invalid, data.StartLocateScan(Path.Combine(booksDirectory, "missing"), _ => { }).Outcome);
		Assert.AreEqual(LocateOutcome.Invalid, data.StartLocateScan("", _ => { }).Outcome);
		Assert.AreEqual("idle", data.GetLocateScan().State);
	}

	[TestMethod]
	public async Task A_located_book_is_not_selected_for_download_and_survives_reconcile()
	{
		var path = WriteFile(booksDirectory, "plain.m4b");
		await data.LocateFile("B0PLAIN0001", path);
		File.Delete(path);

		var books = DbContexts.GetLibrary_Flat_NoTracking();
		Assert.IsTrue(books.Single(b => b.Book.AudibleProductId == "B0PLAIN0001").Book.AudioExists);
		var reconcile = new BulkSetDownloadStatus(books, setDownloaded: true, setNotDownloaded: false);
		reconcile.Discover();
		await reconcile.ExecuteAsync();
		Assert.AreEqual("Liberated", data.GetBook("B0PLAIN0001")!.BookStatus);
	}
}
