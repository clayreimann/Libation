using ApplicationServices;
using DataLayer;
using LibationFileManager;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
[DoNotParallelize]
public class DbEngineDataRatingTests
{
	private sealed class FakeReviews : IAudibleReviews
	{
		public readonly List<string> Sent = [];
		public bool Accept = true;
		public Exception? Throw;

		public Task<bool> SubmitAsync(LibraryBook book, int overall, int performance, int story, CancellationToken ct)
		{
			Sent.Add($"{book.Book.AudibleProductId}:{overall}:{performance}:{story}");
			return Throw is { } ex ? Task.FromException<bool>(ex) : Task.FromResult(Accept);
		}
	}

	private string directory = "";
	private string? previousDirectory;
	private readonly FakeReviews reviews = new();
	private DbEngineData data = null!;

	[TestInitialize]
	public void Init()
	{
		directory = Path.Combine(Path.GetTempPath(), $"libation-engine-rating-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		previousDirectory = Environment.GetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR);
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, directory);
		Configuration.CreateMockInstance();
		data = new DbEngineData(reviews);

		using var context = DbContexts.GetContext();
		var book = new Book(new AudibleProductId("B0RATED0001"), "Rated", "", "Description", 600,
			ContentType.Product, [new Contributor("author")], [new Contributor("narrator")], "us");
		context.LibraryBooks.Add(new LibraryBook(book, new DateTime(2020, 1, 1), "acct"));
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

	[TestMethod]
	public async Task SetMyRating_sends_the_stars_to_audible_and_stores_them()
	{
		var result = await data.SetMyRating("B0RATED0001", 5, 4, 3, default);

		Assert.AreEqual(RatingOutcome.Ok, result.Outcome);
		CollectionAssert.AreEqual(new[] { "B0RATED0001:5:4:3" }, reviews.Sent);
		Assert.AreEqual(new(5, 4, 3), result.Book!.MyRating);
		Assert.AreEqual(new(5, 4, 3), data.GetBook("B0RATED0001")!.MyRating);
	}

	[TestMethod]
	public async Task SetMyRating_stores_nothing_when_audible_rejects_or_is_unreachable()
	{
		reviews.Accept = false;
		var rejected = await data.SetMyRating("B0RATED0001", 5, 4, 3, default);
		reviews.Throw = new HttpRequestException("offline");
		var unreachable = await data.SetMyRating("B0RATED0001", 5, 4, 3, default);

		Assert.AreEqual(RatingOutcome.AudibleFailed, rejected.Outcome);
		Assert.AreEqual(RatingOutcome.AudibleFailed, unreachable.Outcome);
		StringAssert.Contains(unreachable.Error, "offline");
		Assert.AreEqual(new(0, 0, 0), data.GetBook("B0RATED0001")!.MyRating);
	}

	[TestMethod]
	public async Task SetMyRating_with_zero_overall_changes_and_sends_nothing()
	{
		await data.SetMyRating("B0RATED0001", 4, 4, 4, default);
		reviews.Sent.Clear();

		var result = await data.SetMyRating("B0RATED0001", 0, 5, 5, default);

		Assert.AreEqual(RatingOutcome.Ok, result.Outcome);
		Assert.AreEqual(0, reviews.Sent.Count);
		Assert.AreEqual(new(4, 4, 4), result.Book!.MyRating);
	}

	[TestMethod]
	public async Task SetMyRating_is_NoSuchBook_for_an_unknown_asin()
	{
		Assert.AreEqual(RatingOutcome.NoSuchBook, (await data.SetMyRating("B0NOSUCH001", 5, 5, 5, default)).Outcome);
		Assert.AreEqual(0, reviews.Sent.Count);
	}
}
