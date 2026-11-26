using ApplicationServices;
using DataLayer;
using LibationEngine.Contracts;
using LibationFileManager;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
[DoNotParallelize]
public class DbEngineDataRemovalTests
{
	private sealed class FakeRemoval : IAudibleRemoval
	{
		public readonly List<string> Sent = [];
		public readonly HashSet<string> Declined = [], Throws = [];

		public Task<bool> RemoveAsync(LibraryBook book, CancellationToken ct)
		{
			var asin = book.Book.AudibleProductId;
			Sent.Add(asin);
			if (Throws.Contains(asin))
				throw new HttpRequestException("offline");
			return Task.FromResult(!Declined.Contains(asin));
		}
	}

	private string directory = "";
	private string? previousDirectory;
	private readonly FakeRemoval removal = new();
	private readonly List<EngineEvent> events = [];
	private DbEngineData data = null!;

	[TestInitialize]
	public void Init()
	{
		directory = Path.Combine(Path.GetTempPath(), $"libation-engine-removal-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		previousDirectory = Environment.GetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR);
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, directory);
		Configuration.CreateMockInstance();
		data = new DbEngineData(removal: removal);

		Add("B0PLUS00001", plus: true);
		Add("B0PLUS00002", plus: true);
		Add("B0PLUS00003", plus: true);
		Add("B0OWNED0001", plus: false);
	}

	[TestCleanup]
	public void Cleanup()
	{
		Configuration.RestoreSingletonInstance();
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, previousDirectory);
		Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
		try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
	}

	private static void Add(string asin, bool plus)
	{
		using var context = DbContexts.GetContext();
		var book = new Book(new AudibleProductId(asin), asin, "", "Description", 600, ContentType.Product, [new Contributor("author")], [new Contributor("narrator")], "us");
		var lb = new LibraryBook(book, new DateTime(2020, 1, 1), "acct");
		lb.SetIsAudiblePlus(plus);
		context.LibraryBooks.Add(lb);
		context.SaveChanges();
	}

	private static bool InLibrary(string asin)
		=> DbContexts.GetLibraryBook_Flat_NoTracking(asin) is not null || DbContexts.GetDeletedLibraryBooks().Any(lb => lb.Book.AudibleProductId == asin);

	[TestMethod]
	public async Task Plus_books_are_removed_from_audible_then_deleted_locally()
	{
		var results = await data.RemoveFromAudible(["B0PLUS00001"], events.Add);

		Assert.AreEqual(new RemoveFromAudibleItemDto("B0PLUS00001", "removed"), results.Single());
		CollectionAssert.AreEqual(new[] { "B0PLUS00001" }, removal.Sent);
		Assert.IsFalse(InLibrary("B0PLUS00001"), "desktop hard-deletes the row rather than trashing it");
		Assert.IsTrue(InLibrary("B0PLUS00002"));
		CollectionAssert.AreEqual(new[] { "B0PLUS00001" }, events.OfType<BooksRemovedFromAudible>().Single().Asins.ToArray());
	}

	[TestMethod]
	public async Task Non_plus_and_unknown_books_are_never_sent_to_audible()
	{
		var results = await data.RemoveFromAudible(["B0OWNED0001", "B0NOSUCH001"], events.Add);

		CollectionAssert.AreEqual(new[] { "notPlus", "notFound" }, results.Select(r => r.Result).ToArray());
		Assert.AreEqual(0, removal.Sent.Count);
		Assert.IsTrue(InLibrary("B0OWNED0001"));
		Assert.AreEqual(0, events.Count);
	}

	[TestMethod]
	public async Task A_trashed_plus_book_is_removed_too()
	{
		await new[] { DbContexts.GetLibraryBook_Flat_NoTracking("B0PLUS00001")! }.RemoveBooksAsync();

		var results = await data.RemoveFromAudible(["B0PLUS00001"], events.Add);

		Assert.AreEqual("removed", results.Single().Result);
		Assert.IsFalse(InLibrary("B0PLUS00001"));
	}

	[TestMethod]
	public async Task Failures_do_not_stop_the_rest_and_failed_books_stay_in_the_library()
	{
		removal.Declined.Add("B0PLUS00001");
		removal.Throws.Add("B0PLUS00002");

		var results = await data.RemoveFromAudible(["B0PLUS00001", "B0PLUS00002", "B0OWNED0001", "B0PLUS00003"], events.Add);

		CollectionAssert.AreEqual(new[] { "failed", "failed", "notPlus", "removed" }, results.Select(r => r.Result).ToArray());
		StringAssert.Contains(results[0].Error, "declined");
		StringAssert.Contains(results[1].Error, "offline");
		Assert.IsNull(results[3].Error);
		CollectionAssert.AreEqual(new[] { "B0PLUS00001", "B0PLUS00002", "B0PLUS00003" }, removal.Sent);
		Assert.IsTrue(InLibrary("B0PLUS00001") && InLibrary("B0PLUS00002"));
		Assert.IsFalse(InLibrary("B0PLUS00003"));
		CollectionAssert.AreEqual(new[] { "B0PLUS00003" }, events.OfType<BooksRemovedFromAudible>().Single().Asins.ToArray());
	}

	[TestMethod]
	public async Task A_book_removed_by_an_earlier_call_is_not_found_and_not_sent_again()
	{
		await data.RemoveFromAudible(["B0PLUS00001"], events.Add);
		var results = await data.RemoveFromAudible(["B0PLUS00001"], events.Add);

		Assert.AreEqual("notFound", results.Single().Result);
		Assert.AreEqual(1, removal.Sent.Count);
	}
}
