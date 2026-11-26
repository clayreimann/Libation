using ApplicationServices;
using DataLayer;
using LibationFileManager;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ApplicationServices.AudiobookshelfProgressSync;

namespace LibationEngine.Tests;

[TestClass]
[DoNotParallelize]
public class DbEngineDataProgressTests
{
	private sealed class FakeSync : IProgressSync
	{
		public ProgressSnapshot Snapshot = new(null, null, null);
		public Exception? Throw;
		public BookPushResult Result = new("B0PROGRESS1", "Moving", Outcome.Updated, "Updated", "item", 60);
		public AbsConnection? SeenConnection;
		public (bool Force, bool DryRun)? SeenPush;

		public Task<ProgressSnapshot> ReadAsync(LibraryBook book, AbsConnection? connection, CancellationToken ct)
		{
			SeenConnection = connection;
			return Throw is { } ex ? Task.FromException<ProgressSnapshot>(ex) : Task.FromResult(Snapshot);
		}

		public Task<IReadOnlyList<BookPushResult>> PushAsync(IReadOnlyCollection<LibraryBook> books, AbsConnection connection, bool force, bool dryRun, CancellationToken ct)
		{
			SeenPush = (force, dryRun);
			return Throw is { } ex ? Task.FromException<IReadOnlyList<BookPushResult>>(ex) : Task.FromResult<IReadOnlyList<BookPushResult>>([Result]);
		}
	}

	private string directory = "";
	private string? previousDirectory;
	private readonly FakeSync sync = new();
	private DbEngineData data = null!;

	[TestInitialize]
	public void Init()
	{
		directory = Path.Combine(Path.GetTempPath(), $"libation-engine-progress-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		previousDirectory = Environment.GetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR);
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, directory);
		Configuration.CreateMockInstance();
		data = new DbEngineData(progress: sync);

		using var context = DbContexts.GetContext();
		var book = new Book(new AudibleProductId("B0PROGRESS1"), "Moving", "", "Description", 600,
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

	private static void ConfigureAbs()
	{
		Configuration.Instance.AudiobookshelfServerUrl = "https://abs.example.com";
		Configuration.Instance.AudiobookshelfApiToken = "token";
		Configuration.Instance.AudiobookshelfLibraryId = "lib";
	}

	[TestMethod]
	public async Task GetProgress_maps_both_sides_and_the_matched_item()
	{
		ConfigureAbs();
		var updated = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
		sync.Snapshot = new(
			new("B0PROGRESS1", "Moving", null, "acct", "us", 90_000, updated, null, false),
			new("item", 45.5, true, updated),
			"item");

		var result = await data.GetProgress("B0PROGRESS1", default);

		Assert.AreEqual(ProgressOutcome.Ok, result.Outcome);
		Assert.AreEqual(new(90_000, updated), result.Progress!.Audible);
		Assert.AreEqual(new(45.5, true, updated), result.Progress.Audiobookshelf);
		Assert.AreEqual("item", result.Progress.AbsItemId);
		Assert.AreEqual("https://abs.example.com", sync.SeenConnection!.ServerUrl);
	}

	[TestMethod]
	public async Task GetProgress_passes_no_connection_when_audiobookshelf_is_not_configured()
	{
		var result = await data.GetProgress("B0PROGRESS1", default);

		Assert.AreEqual(ProgressOutcome.Ok, result.Outcome);
		Assert.IsNull(result.Progress!.Audible);
		Assert.IsNull(result.Progress.Audiobookshelf);
		Assert.IsNull(sync.SeenConnection);
	}

	[TestMethod]
	public async Task GetProgress_reports_an_unknown_book_and_a_service_failure()
	{
		Assert.AreEqual(ProgressOutcome.NoSuchBook, (await data.GetProgress("nope", default)).Outcome);

		sync.Throw = new HttpRequestException("offline");
		var failed = await data.GetProgress("B0PROGRESS1", default);
		Assert.AreEqual(ProgressOutcome.ServiceFailed, failed.Outcome);
		StringAssert.Contains(failed.Error, "offline");
	}

	[TestMethod]
	public async Task PushProgress_maps_outcomes_to_pushed_skipped_and_failed()
	{
		ConfigureAbs();

		var pushed = await data.PushProgress("B0PROGRESS1", force: true, dryRun: false, default);
		Assert.AreEqual(ProgressOutcome.Ok, pushed.Outcome);
		Assert.AreEqual(new("B0PROGRESS1", "pushed", null, 60), pushed.Push);
		Assert.AreEqual((true, false), sync.SeenPush);

		sync.Result = sync.Result with { Outcome = Outcome.ServerIsNewer, Message = "Audiobookshelf is newer" };
		Assert.AreEqual(new("B0PROGRESS1", "skipped", "Audiobookshelf is newer", 60), (await data.PushProgress("B0PROGRESS1", false, true, default)).Push);
		Assert.AreEqual((false, true), sync.SeenPush);

		sync.Result = sync.Result with { Outcome = Outcome.Failed, Message = "boom" };
		Assert.AreEqual("failed", (await data.PushProgress("B0PROGRESS1", false, false, default)).Push!.Outcome);

		sync.Result = sync.Result with { Outcome = Outcome.WouldUpdate, Message = "would update" };
		Assert.AreEqual("pushed", (await data.PushProgress("B0PROGRESS1", false, true, default)).Push!.Outcome);
	}

	[TestMethod]
	public async Task PushProgress_is_not_configured_without_a_server_and_turns_a_throw_into_a_failed_push()
	{
		Assert.AreEqual(ProgressOutcome.NotConfigured, (await data.PushProgress("B0PROGRESS1", false, false, default)).Outcome);
		Assert.AreEqual(ProgressOutcome.NoSuchBook, (await data.PushProgress("nope", false, false, default)).Outcome);

		ConfigureAbs();
		sync.Throw = new InvalidOperationException("kaput");
		var result = await data.PushProgress("B0PROGRESS1", false, false, default);
		Assert.AreEqual(ProgressOutcome.Ok, result.Outcome);
		Assert.AreEqual("failed", result.Push!.Outcome);
		Assert.AreEqual("kaput", result.Push.Reason);
	}
}
