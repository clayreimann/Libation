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
}
