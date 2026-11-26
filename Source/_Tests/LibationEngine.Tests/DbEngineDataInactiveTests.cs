using ApplicationServices;
using AudibleApi;
using AudibleApi.Authorization;
using AudibleUtilities;
using DataLayer;
using LibationEngine.Contracts;
using LibationFileManager;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
[DoNotParallelize]
public class DbEngineDataInactiveTests
{
	private sealed class FakeInventory : IAudibleInventory
	{
		public readonly HashSet<string> Gone = [];
		public IReadOnlyList<string> Candidates = [];
		public IReadOnlyList<string> Accounts = [];
		public int Calls;
		public bool Fail;
		public TaskCompletionSource? Gate;

		public async Task<IReadOnlyList<LibraryBook>> FindInactiveAsync(IReadOnlyList<LibraryBook> candidates, IReadOnlyList<Account> accounts, CancellationToken ct)
		{
			Calls++;
			Candidates = candidates.Select(c => c.Book.AudibleProductId).Order().ToList();
			Accounts = accounts.Select(a => a.AccountId).Order().ToList();
			if (Gate is { } gate)
				await gate.Task.WaitAsync(ct);
			if (Fail)
				throw new InvalidOperationException("scan incomplete");
			return candidates.Where(c => Gone.Contains(c.Book.AudibleProductId)).ToList();
		}
	}

	private string directory = "";
	private string? previousDirectory;
	private readonly FakeInventory inventory = new();
	private readonly List<EngineEvent> events = [];
	private DbEngineData data = null!;

	[TestInitialize]
	public async Task Init()
	{
		directory = Path.Combine(Path.GetTempPath(), $"libation-engine-inactive-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		previousDirectory = Environment.GetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR);
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, directory);
		Configuration.CreateMockInstance();
		AudibleApiStorage.EnsureAccountsSettingsFileExists();
		using (var persister = AudibleApiStorage.GetAccountsSettingsPersister())
		{
			persister.AccountsSettings.Add(new Account("a@example.com") { IdentityTokens = new Identity(Localization.Get("us")), LibraryScan = true });
			persister.AccountsSettings.Add(new Account("b@example.com") { IdentityTokens = new Identity(Localization.Get("us")), LibraryScan = false });
		}
		data = new DbEngineData(inventory: inventory);

		await Add("B0PENDING01", "Zebra", "a@example.com", LiberatedStatus.NotLiberated);
		await Add("B0PENDING02", "Apple", "a@example.com", LiberatedStatus.NotLiberated);
		await Add("B0LIBERATE1", "Done", "a@example.com", LiberatedStatus.Liberated);
		await Add("B0OTHERACC1", "Other", "b@example.com", LiberatedStatus.NotLiberated);
	}

	[TestCleanup]
	public void Cleanup()
	{
		Configuration.RestoreSingletonInstance();
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, previousDirectory);
		Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
		try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
	}

	private static async Task Add(string asin, string title, string account, LiberatedStatus status)
	{
		using (var context = DbContexts.GetContext())
		{
			var book = new Book(new AudibleProductId(asin), title, "", "Description", 600, ContentType.Product, [new Contributor("author")], [new Contributor("narrator")], "us");
			context.LibraryBooks.Add(new LibraryBook(book, new DateTime(2020, 1, 1), account));
			context.SaveChanges();
		}
		if (status != LiberatedStatus.NotLiberated)
			await DbContexts.GetLibraryBook_Flat_NoTracking(asin)!.UpdateBookStatusAsync(status);
	}

	private async Task<InactiveScanDto> WaitFor(string state)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (data.GetInactiveScan().State != state)
		{
			Assert.IsLessThan(deadline, DateTime.UtcNow, $"scan never reached {state}: {data.GetInactiveScan().State}");
			await Task.Delay(10);
		}
		return data.GetInactiveScan();
	}

	[TestMethod]
	public async Task Scan_compares_only_books_not_downloaded_from_the_scanned_accounts_and_lists_the_missing_ones()
	{
		inventory.Gone.UnionWith(["B0PENDING01", "B0PENDING02"]);

		var start = data.StartInactiveScan(null, events.Add);
		var scan = await WaitFor("completed");

		Assert.AreEqual(InactiveOutcome.Ok, start.Outcome);
		Assert.AreEqual("running", start.Scan!.State);
		CollectionAssert.AreEqual(new[] { "B0PENDING01", "B0PENDING02" }, inventory.Candidates.ToArray());
		CollectionAssert.AreEqual(new[] { "a@example.com" }, inventory.Accounts.ToArray());
		Assert.AreEqual(2, scan.Total);
		Assert.AreEqual(2, scan.Scanned);
		CollectionAssert.AreEqual(new[] { "B0PENDING02", "B0PENDING01" }, scan.Results!.Select(r => r.Asin).ToArray(), "by title");
		var first = scan.Results![0];
		Assert.AreEqual(("Apple", "a@example.com"), (first.Title, first.Account));
		CollectionAssert.AreEqual(new[] { "author" }, first.Authors.ToArray());
		var last = events.OfType<InactiveScanProgress>().Last().Scan;
		Assert.AreEqual("completed", last.State);
		Assert.IsNull(last.Results);
		Assert.IsNotNull(scan.FinishedAt);
	}

	[TestMethod]
	public async Task Scan_never_removes_anything()
	{
		inventory.Gone.Add("B0PENDING01");

		data.StartInactiveScan(null, events.Add);
		await WaitFor("completed");

		Assert.IsNotNull(DbContexts.GetLibraryBook_Flat_NoTracking("B0PENDING01"));
		Assert.AreEqual(0, DbContexts.GetDeletedLibraryBooks().Count);
	}

	[TestMethod]
	public async Task Scan_limited_to_accounts_matches_ids_case_insensitively()
	{
		data.StartInactiveScan(["B@EXAMPLE.COM"], events.Add);
		await WaitFor("completed");

		CollectionAssert.AreEqual(new[] { "B0OTHERACC1" }, inventory.Candidates.ToArray());
		CollectionAssert.AreEqual(new[] { "b@example.com" }, inventory.Accounts.ToArray());
	}

	[TestMethod]
	public void Scan_is_invalid_for_an_unknown_account_or_when_nothing_is_left_to_compare()
	{
		Assert.AreEqual(InactiveOutcome.Invalid, data.StartInactiveScan(["nobody@example.com"], events.Add).Outcome);

		using (var persister = AudibleApiStorage.GetAccountsSettingsPersister())
			persister.AccountsSettings.Accounts.First(a => a.AccountId == "a@example.com").LibraryScan = false;
		Assert.AreEqual(InactiveOutcome.Invalid, data.StartInactiveScan(null, events.Add).Outcome);
		Assert.AreEqual(0, inventory.Calls);
		Assert.AreEqual("idle", data.GetInactiveScan().State);
	}

	[TestMethod]
	public async Task A_failed_scan_reports_the_error_and_flags_nothing()
	{
		inventory.Fail = true;

		data.StartInactiveScan(null, events.Add);
		var scan = await WaitFor("failed");

		Assert.AreEqual("scan incomplete", scan.Error);
		Assert.IsNull(scan.Results);
	}

	[TestMethod]
	public async Task A_second_scan_is_busy_and_cancel_stops_the_first()
	{
		inventory.Gate = new();
		data.StartInactiveScan(null, events.Add);

		Assert.AreEqual(InactiveOutcome.Busy, data.StartInactiveScan(null, events.Add).Outcome);
		Assert.AreEqual(1, inventory.Calls);
		Assert.AreEqual("cancelling", data.CancelInactiveScan(events.Add).Scan!.State);
		var scan = await WaitFor("cancelled");

		Assert.IsNull(scan.Results);
		Assert.AreEqual(InactiveOutcome.NotRunning, data.CancelInactiveScan(events.Add).Outcome);
		Assert.AreEqual(InactiveOutcome.Ok, data.StartInactiveScan(null, events.Add).Outcome, "a new scan can start after a cancel");
	}

	[TestMethod]
	public void Cancel_is_not_running_before_any_scan()
		=> Assert.AreEqual(InactiveOutcome.NotRunning, data.CancelInactiveScan(events.Add).Outcome);
}
