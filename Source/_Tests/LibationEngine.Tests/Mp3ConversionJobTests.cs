using ApplicationServices;
using DataLayer;
using LibationFileManager;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
[DoNotParallelize]
public class Mp3ConversionJobTests
{
	private string directory = "";
	private string booksDirectory = "";
	private string? previousDirectory;
	private LiberationQueue queue = null!;

	[TestInitialize]
	public void Init()
	{
		directory = Path.Combine(Path.GetTempPath(), $"libation-engine-mp3-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		previousDirectory = Environment.GetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR);
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, directory);
		Configuration.CreateMockInstance();
		booksDirectory = Path.Combine(directory, "Books");
		Directory.CreateDirectory(booksDirectory);
		Configuration.Instance.Books = booksDirectory;
		queue = new LiberationQueue(new LiberationPolicy { MaxConcurrentDownloads = () => 1 });

		using var context = DbContexts.GetContext();
		AddBook(context, "B0LIBERATED", LiberatedStatus.Liberated, ContentType.Product);
		AddBook(context, "B0NOTLIBER1", LiberatedStatus.NotLiberated, ContentType.Product);
		AddBook(context, "B0EPISODE01", LiberatedStatus.Liberated, ContentType.Episode);
		AddBook(context, "B0ABSENT001", LiberatedStatus.Liberated, ContentType.Product, absent: true);
		AddBook(context, "B0ALREADYMP", LiberatedStatus.Liberated, ContentType.Product);
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

	private static void AddBook(LibationContext context, string asin, LiberatedStatus status, ContentType type, bool absent = false)
	{
		var book = new Book(new AudibleProductId(asin), asin, "", "Description", 600, type, [new Contributor(asin + " author")], [new Contributor(asin + " narrator")], "us");
		book.UserDefinedItem.BookStatus = status;
		context.LibraryBooks.Add(new LibraryBook(book, new DateTime(2020, 1, 1), "acct") { AbsentFromLastScan = absent });
	}

	private string WriteFile(string name)
	{
		var path = Path.Combine(booksDirectory, name);
		File.WriteAllBytes(path, [1]);
		return path;
	}

	[TestMethod]
	public void Create_builds_a_convert_job_for_a_liberated_book_with_an_m4b()
	{
		FilePathCache.Insert("B0LIBERATED", WriteFile("a [B0LIBERATED].m4b"));

		var made = Mp3ConversionJob.Create(queue, "B0LIBERATED");

		Assert.AreEqual(ConvertOutcome.Ok, made.Outcome);
		Assert.AreEqual("B0LIBERATED", made.Job!.LibraryBook.Book.AudibleProductId);
		Assert.IsFalse(made.Job.IncludesBookDownload);
	}

	[TestMethod]
	public void Create_is_a_conflict_for_a_book_that_desktop_would_not_convert()
	{
		foreach (var asin in new[] { "B0NOTLIBER1", "B0EPISODE01", "B0ABSENT001" })
			FilePathCache.Insert(asin, WriteFile($"a [{asin}].m4b"));

		Assert.AreEqual(ConvertOutcome.Conflict, Mp3ConversionJob.Create(queue, "B0NOTLIBER1").Outcome);
		Assert.AreEqual(ConvertOutcome.Conflict, Mp3ConversionJob.Create(queue, "B0EPISODE01").Outcome);
		Assert.AreEqual(ConvertOutcome.Conflict, Mp3ConversionJob.Create(queue, "B0ABSENT001").Outcome);
	}

	[TestMethod]
	public void Create_is_a_conflict_when_the_mp3_exists_or_there_is_no_m4b()
	{
		FilePathCache.Insert("B0ALREADYMP", WriteFile("b [B0ALREADYMP].m4b"));
		FilePathCache.Insert("B0ALREADYMP", WriteFile("b [B0ALREADYMP].mp3"));

		Assert.AreEqual(ConvertOutcome.Conflict, Mp3ConversionJob.Create(queue, "B0ALREADYMP").Outcome);
		Assert.AreEqual(ConvertOutcome.Conflict, Mp3ConversionJob.Create(queue, "B0LIBERATED").Outcome);
	}

	[TestMethod]
	public void Create_is_not_found_for_an_unknown_asin()
		=> Assert.AreEqual(ConvertOutcome.NotFound, Mp3ConversionJob.Create(queue, "B0NOSUCH001").Outcome);
}
