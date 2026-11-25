using ApplicationServices;
using DataLayer;
using LibationFileManager;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
[DoNotParallelize]
public class DbEngineDataSeriesTests
{
	private string directory = "";
	private string? previousDirectory;
	private readonly DbEngineData data = new();

	[TestInitialize]
	public void Init()
	{
		directory = Path.Combine(Path.GetTempPath(), $"libation-engine-series-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		previousDirectory = Environment.GetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR);
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, directory);
		Configuration.CreateMockInstance();

		using var context = DbContexts.GetContext();
		var alpha = new Series(new AudibleSeriesId("B0SERIES001"), "alpha Saga");
		var zed = new Series(new AudibleSeriesId("B0SERIES002"), "Zed");
		var show = new Series(new AudibleSeriesId("B0PODCAST01"), "The Show");

		Add(context, "B0BOOK00003", ContentType.Product, (alpha, "3"));
		Add(context, "B0BOOK00001", ContentType.Product, (alpha, "1"), (zed, "2")).UserDefinedItem.BookStatus = LiberatedStatus.Liberated;
		Add(context, "B0BOOK00002", ContentType.Product, (alpha, "2"));
		Add(context, "B0BOOKNOIDX", ContentType.Product, (alpha, null));
		Add(context, "B0LONELY001", ContentType.Product);
		Add(context, "B0PODCAST01", ContentType.Parent);
		Add(context, "B0EPISODE01", ContentType.Episode, (show, "1"));
		Add(context, "B0EPISODE02", ContentType.Episode, (show, "2"));
		Add(context, "B0EPISODE10", ContentType.Episode, (show, "10"));
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

	private static Book Add(LibationContext context, string asin, ContentType type, params (Series Series, string? Order)[] series)
	{
		var book = new Book(new AudibleProductId(asin), asin, "", "Description", 600, type, [new Contributor(asin + " author")], [new Contributor(asin + " narrator")], "us");
		foreach (var (s, order) in series)
			book.UpsertSeries(s, order);
		context.LibraryBooks.Add(new LibraryBook(book, new DateTime(2020, 1, 1), "acct"));
		return book;
	}

	[TestMethod]
	public void GetSeries_lists_series_by_name_with_books_in_series_order()
	{
		var all = data.GetSeries();

		CollectionAssert.AreEqual(new[] { "alpha Saga", "The Show", "Zed" }, all.Select(g => g.Name).ToArray());
		var alpha = all[0];
		Assert.AreEqual("B0SERIES001", alpha.SeriesId);
		Assert.AreEqual(4, alpha.BookCount);
		Assert.AreEqual(1, alpha.LiberatedCount);
		CollectionAssert.AreEqual(new[] { "B0BOOK00001", "B0BOOK00002", "B0BOOK00003", "B0BOOKNOIDX" }, alpha.Books.Select(b => b.Asin).ToArray());
		CollectionAssert.AreEqual(new string?[] { "1", "2", "3", null }, alpha.Books.Select(b => b.Order).ToArray());
		Assert.AreEqual(1, all[2].BookCount);
		Assert.AreEqual("2", all[2].Books[0].Order);
	}

	[TestMethod]
	public void GetSeries_filters_by_name_ignoring_case()
	{
		CollectionAssert.AreEqual(new[] { "alpha Saga" }, data.GetSeries("SAGA").Select(g => g.Name).ToArray());
		Assert.AreEqual(0, data.GetSeries("nothing").Count);
	}

	[TestMethod]
	public void GetSeries_by_id_returns_one_series_or_null()
	{
		Assert.AreEqual("Zed", data.GetSeriesById("B0SERIES002")!.Name);
		Assert.IsNull(data.GetSeriesById("B0NOSUCH"));
	}

	[TestMethod]
	public void GetPodcastEpisodes_orders_children_as_the_grid_does()
	{
		var episodes = data.GetPodcastEpisodes("B0PODCAST01")!;

		CollectionAssert.AreEqual(new[] { "B0EPISODE10", "B0EPISODE02", "B0EPISODE01" }, episodes.Select(e => e.Asin).ToArray());
		Assert.IsTrue(episodes.All(e => e.ParentAsin == "B0PODCAST01"));
	}

	[TestMethod]
	public void GetPodcastEpisodes_is_null_for_anything_but_a_podcast_parent()
	{
		Assert.IsNull(data.GetPodcastEpisodes("B0BOOK00001"));
		Assert.IsNull(data.GetPodcastEpisodes("B0NOSUCH"));
	}
}
