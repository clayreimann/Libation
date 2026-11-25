using ApplicationServices;
using AudibleApi.Common;
using DataLayer;
using ContentType = DataLayer.ContentType;
using LibationFileManager;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
[DoNotParallelize]
public class DbEngineDataBookmarksTests
{
	private sealed class FakeRecords : IAudibleRecords
	{
		public List<IRecord> Records = [];
		public Exception? Throw;
		public readonly List<string> Asked = [];

		public Task<IReadOnlyList<IRecord>> GetAsync(LibraryBook book, CancellationToken ct)
		{
			Asked.Add(book.Book.AudibleProductId);
			return Throw is { } ex ? Task.FromException<IReadOnlyList<IRecord>>(ex) : Task.FromResult<IReadOnlyList<IRecord>>(Records);
		}
	}

	private static readonly DateTimeOffset Created = new(2026, 8, 26, 13, 5, 7, TimeSpan.Zero);

	private string directory = "";
	private string? previousDirectory;
	private readonly FakeRecords records = new();
	private DbEngineData data = null!;

	[TestInitialize]
	public void Init()
	{
		directory = Path.Combine(Path.GetTempPath(), $"libation-engine-bookmarks-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		previousDirectory = Environment.GetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR);
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, directory);
		Configuration.CreateMockInstance();
		data = new DbEngineData(records: records);

		using var context = DbContexts.GetContext();
		var book = new Book(new AudibleProductId("B0MARKED001"), "Marked", "", "Description", 600,
			ContentType.Product, [new Contributor("author")], [new Contributor("narrator")], "us");
		context.LibraryBooks.Add(new LibraryBook(book, new DateTime(2020, 1, 1), "acct"));
		context.SaveChanges();

		records.Records =
		[
			new Clip(Created, TimeSpan.FromMilliseconds(1500), "anno-1", Created, TimeSpan.FromSeconds(20), "clip text", "clip title"),
			new Note(Created, TimeSpan.FromSeconds(30), "anno-2", Created, TimeSpan.FromSeconds(40), "note text"),
			new Bookmark(Created, TimeSpan.FromSeconds(50), "anno-3", Created),
			new LastHeard(Created, TimeSpan.FromSeconds(60)),
		];
	}

	[TestCleanup]
	public void Cleanup()
	{
		Configuration.RestoreSingletonInstance();
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, previousDirectory);
		Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
		try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
	}

	[TestMethod]
	public async Task GetBookmarks_maps_every_record_type_with_seconds()
	{
		var result = await data.GetBookmarks("B0MARKED001", default);

		Assert.AreEqual(BookmarksOutcome.Ok, result.Outcome);
		var list = result.Records!;
		Assert.AreEqual(4, list.Count);
		Assert.AreEqual(new("Clip", 1.5, 20, "clip text", "clip title", Created), list[0]);
		Assert.AreEqual(new("Note", 30, 40, "note text", null, Created), list[1]);
		Assert.AreEqual(new("Bookmark", 50, null, null, null, Created), list[2]);
		Assert.AreEqual(new("LastHeard", 60, null, null, null, Created), list[3]);
	}

	[TestMethod]
	public async Task GetBookmarks_is_NoSuchBook_without_asking_audible_and_AudibleFailed_when_it_throws()
	{
		Assert.AreEqual(BookmarksOutcome.NoSuchBook, (await data.GetBookmarks("B0NOSUCH001", default)).Outcome);
		Assert.AreEqual(0, records.Asked.Count);

		records.Throw = new HttpRequestException("offline");
		var failed = await data.GetBookmarks("B0MARKED001", default);

		Assert.AreEqual(BookmarksOutcome.AudibleFailed, failed.Outcome);
		StringAssert.Contains(failed.Error, "offline");
		Assert.AreEqual(BookmarksOutcome.AudibleFailed, (await data.ExportBookmarks("B0MARKED001", ExportFormat.Csv, default)).Outcome);
	}

	[TestMethod]
	public async Task ExportBookmarks_writes_each_format_with_the_desktop_exporter()
	{
		var csv = await data.ExportBookmarks("B0MARKED001", ExportFormat.Csv, default);
		var json = await data.ExportBookmarks("B0MARKED001", ExportFormat.Json, default);
		var xlsx = await data.ExportBookmarks("B0MARKED001", ExportFormat.Xlsx, default);
		try
		{
			var csvText = await File.ReadAllTextAsync(csv.FilePath!);
			StringAssert.Contains(csvText, "clip title");
			StringAssert.Contains(csvText, "Bookmark");

			using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(json.FilePath!));
			Assert.AreEqual("B0MARKED001", doc.RootElement.GetProperty("asin").GetString());
			Assert.AreEqual(4, doc.RootElement.GetProperty("records").GetArrayLength());

			Assert.AreEqual("PK", System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(xlsx.FilePath!), 0, 2));
		}
		finally
		{
			foreach (var r in new[] { csv, json, xlsx })
				if (r.FilePath is not null)
					File.Delete(r.FilePath);
		}
	}

	[TestMethod]
	public async Task ExportBookmarks_has_no_file_when_the_book_has_no_records()
	{
		records.Records = [];

		var result = await data.ExportBookmarks("B0MARKED001", ExportFormat.Csv, default);

		Assert.AreEqual(BookmarksOutcome.Ok, result.Outcome);
		Assert.IsNull(result.FilePath);
	}
}
