using ApplicationServices;
using DataLayer;
using LibationEngine.Contracts;
using LibationFileManager;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NuGet.Versioning;

namespace LibationEngine.Tests;

[TestClass]
[DoNotParallelize]
public class DbEngineDataQualityTests
{
	private sealed class FakeQuality : IAudibleQuality
	{
		public readonly Dictionary<string, AudioQuality> Available = [];
		public readonly List<string> Asked = [];
		public TaskCompletionSource? Gate;

		public async Task<AudioQuality> GetAvailableAsync(LibraryBook book, bool widevine, CancellationToken ct)
		{
			var asin = book.Book.AudibleProductId;
			Asked.Add($"{asin}:{widevine}");
			if (Gate is { } gate)
				await gate.Task.WaitAsync(ct);
			return Available.TryGetValue(asin, out var quality) ? quality : throw new HttpRequestException("offline");
		}
	}

	private string directory = "";
	private string? previousDirectory;
	private readonly FakeQuality quality = new();
	private readonly List<EngineEvent> events = [];
	private DbEngineData data = null!;

	[TestInitialize]
	public async Task Init()
	{
		directory = Path.Combine(Path.GetTempPath(), $"libation-engine-quality-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		previousDirectory = Environment.GetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR);
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, directory);
		Configuration.CreateMockInstance();
		data = new DbEngineData(quality: quality);

		await Add("B0UPGRADE01", LiberatedStatus.Liberated, new AudioFormat(Codec.AAC_LC, 64, 44100, 2));
		await Add("B0SAME00001", LiberatedStatus.Liberated, new AudioFormat(Codec.AAC_LC, 128, 44100, 2));
		await Add("B0NOFORMAT1", LiberatedStatus.Liberated, null);
		await Add("B0PENDING01", LiberatedStatus.NotLiberated, null);
		await Add("B0FAILS0001", LiberatedStatus.Liberated, new AudioFormat(Codec.AAC_LC, 64, 44100, 2));
		quality.Available["B0UPGRADE01"] = new("xHE-AAC", 128);
		quality.Available["B0SAME00001"] = new("AAC-LC", 128);
	}

	[TestCleanup]
	public void Cleanup()
	{
		Configuration.RestoreSingletonInstance();
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, previousDirectory);
		Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
		try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
	}

	private static async Task Add(string asin, LiberatedStatus status, AudioFormat? format)
	{
		using (var context = DbContexts.GetContext())
		{
			var book = new Book(new AudibleProductId(asin), asin, "", "Description", 600, ContentType.Product, [new Contributor("author")], [new Contributor("narrator")], "us");
			context.LibraryBooks.Add(new LibraryBook(book, new DateTime(2020, 1, 1), "acct"));
			context.SaveChanges();
		}
		var lb = DbContexts.GetLibraryBook_Flat_NoTracking(asin)!;
		if (format is not null)
			await lb.UpdateBookStatusAsync(status, new NuGetVersion(1, 0, 0), format, "v1");
		else if (status != LiberatedStatus.NotLiberated)
			await lb.UpdateBookStatusAsync(status);
	}

	private async Task<QualityScanDto> WaitFor(string state)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (data.GetQualityScan().State != state)
		{
			Assert.IsLessThan(deadline, DateTime.UtcNow, $"scan never reached {state}: {data.GetQualityScan().State}");
			await Task.Delay(10);
		}
		return data.GetQualityScan();
	}

	[TestMethod]
	public async Task Scan_reports_only_books_with_a_significantly_better_format()
	{
		var start = data.StartQualityScan(null, null, events.Add);
		var scan = await WaitFor("completed");

		Assert.AreEqual(QualityOutcome.Ok, start.Outcome);
		Assert.AreEqual(4, scan.Total);
		Assert.AreEqual(4, scan.Scanned);
		Assert.AreEqual(2, scan.Failed);
		var hit = scan.Results!.Single();
		Assert.AreEqual(new QualityResultDto("B0UPGRADE01", "B0UPGRADE01", new("AAC-LC", 64), new("xHE-AAC", 128)), hit);
		CollectionAssert.DoesNotContain(quality.Asked, "B0PENDING01:False");
		Assert.IsFalse(quality.Asked.Any(a => a.StartsWith("B0NOFORMAT1")), "a book with no known format is not asked about");
		var last = events.OfType<QualityScanProgress>().Last().Scan;
		Assert.AreEqual("completed", last.State);
		Assert.AreEqual(1, last.Results!.Count);
		Assert.IsTrue(events.OfType<QualityScanProgress>().Where(e => e.Scan.State == "running").All(e => e.Scan.Results is null));
	}

	[TestMethod]
	public async Task Scan_limited_to_asins_and_widevine_defaults_to_the_setting()
	{
		Configuration.Instance.UseWidevine = true;

		data.StartQualityScan(["B0UPGRADE01", "B0PENDING01"], null, events.Add);
		var scan = await WaitFor("completed");

		Assert.AreEqual(1, scan.Total);
		Assert.IsTrue(scan.Widevine);
		CollectionAssert.AreEqual(new[] { "B0UPGRADE01:True" }, quality.Asked);
	}

	[TestMethod]
	public void Scan_with_nothing_eligible_is_invalid()
	{
		var start = data.StartQualityScan(["B0PENDING01", "B0NOSUCH001"], null, events.Add);

		Assert.AreEqual(QualityOutcome.Invalid, start.Outcome);
		Assert.AreEqual("idle", data.GetQualityScan().State);
	}

	[TestMethod]
	public async Task A_second_scan_is_busy_and_cancel_stops_the_first_keeping_results()
	{
		quality.Gate = new();
		data.StartQualityScan(["B0UPGRADE01"], null, events.Add);

		Assert.AreEqual(QualityOutcome.Busy, data.StartQualityScan(null, null, events.Add).Outcome);
		Assert.AreEqual("cancelling", data.CancelQualityScan(events.Add).Scan!.State);
		var scan = await WaitFor("cancelled");

		Assert.AreEqual(0, scan.Scanned);
		Assert.AreEqual(QualityOutcome.NotRunning, data.CancelQualityScan(events.Add).Outcome);
		Assert.AreEqual(QualityOutcome.Ok, data.StartQualityScan(null, null, events.Add).Outcome);
	}

	[TestMethod]
	public async Task Apply_marks_upgrades_not_liberated_enables_xhe_aac_and_drops_them_from_the_results()
	{
		Configuration.Instance.UseWidevine = false;
		Configuration.Instance.Request_xHE_AAC = false;
		data.StartQualityScan(null, null, events.Add);
		await WaitFor("completed");

		var result = await data.ApplyQualityUpgrades(["B0UPGRADE01", "B0SAME00001"]);

		Assert.AreEqual(QualityOutcome.Ok, result.Outcome);
		Assert.AreEqual(1, result.Applied!.Marked);
		CollectionAssert.AreEqual(new[] { "B0SAME00001" }, result.Applied.Missing.ToArray());
		Assert.IsTrue(result.Applied.WidevineEnabled);
		Assert.IsTrue(result.Applied.XHeAacRequested);
		Assert.IsTrue(Configuration.Instance.UseWidevine);
		Assert.IsTrue(Configuration.Instance.Request_xHE_AAC);
		Assert.AreEqual("NotLiberated", data.GetBook("B0UPGRADE01")!.BookStatus);
		Assert.AreEqual("Liberated", data.GetBook("B0SAME00001")!.BookStatus);
		Assert.AreEqual(0, data.GetQualityScan().Results!.Count);
	}

	[TestMethod]
	public async Task Apply_without_hits_changes_nothing_and_is_busy_during_a_scan()
	{
		Configuration.Instance.UseWidevine = false;
		var none = await data.ApplyQualityUpgrades(["B0UPGRADE01"]);
		Assert.AreEqual(0, none.Applied!.Marked);
		Assert.IsFalse(Configuration.Instance.UseWidevine);
		Assert.AreEqual("Liberated", data.GetBook("B0UPGRADE01")!.BookStatus);

		quality.Gate = new();
		data.StartQualityScan(null, null, events.Add);
		Assert.AreEqual(QualityOutcome.Busy, (await data.ApplyQualityUpgrades(["B0UPGRADE01"])).Outcome);
		data.CancelQualityScan(events.Add);
		await WaitFor("cancelled");
	}
}
