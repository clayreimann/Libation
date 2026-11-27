using DataLayer;
using Dinah.Core.ErrorHandling;
using FileLiberator;
using LibationFileManager;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
[DoNotParallelize]
public class LiberationJobTests
{
	private string _dir = "";

	[TestInitialize]
	public void Init()
	{
		_dir = Path.Combine(Path.GetTempPath(), $"libation-engine-job-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_dir);
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, _dir);
		Configuration.CreateMockInstance();
	}

	[TestCleanup]
	public void Cleanup()
	{
		Configuration.RestoreSingletonInstance();
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, null);
		try { Directory.Delete(_dir, recursive: true); } catch { }
	}

	private sealed class Step(string name, Action? onRun = null) : Processable
	{
		public override string Name => name;
		public int Runs { get; private set; }
		public override bool Validate(LibraryBook libraryBook) => true;

		public override Task<StatusHandler> ProcessAsync(LibraryBook libraryBook)
		{
			Runs++;
			onRun?.Invoke();
			return Task.FromResult(new StatusHandler());
		}
	}

	private static LiberationJob Job()
	{
		var contributor = Contributor.GetEmpty();
		var book = new Book(new AudibleProductId("B000000001"), "Title", null, null, 1, ContentType.Product, [contributor], [contributor], "us");
		return new LiberationJob(new LibraryBook(book, new DateTime(2026, 8, 10), "account"), Configuration.Instance);
	}

	[TestMethod]
	public async Task a_job_cancelled_before_a_step_starts_ends_cancelled_without_running_it()
	{
		var job = Job();
		Step? step = null;
		job.AddProcessable(c => step = new Step("one") { Configuration = c });

		await job.CancelAsync();
		var result = await job.ProcessOneAsync();

		Assert.AreEqual(ProcessBookResult.Cancelled, result);
		Assert.AreEqual(ProcessBookStatus.Cancelled, job.Status);
		Assert.IsNull(step);
	}

	[TestMethod]
	public async Task a_job_cancelled_between_steps_does_not_start_the_next_one()
	{
		var job = Job();
		Step? second = null;
		Step? first = null;
		job.AddProcessable(c => first = new Step("one", () => job.CancelAsync().GetAwaiter().GetResult()) { Configuration = c });
		job.AddProcessable(c => second = new Step("two") { Configuration = c });

		var result = await job.ProcessOneAsync();

		Assert.AreEqual(ProcessBookResult.Cancelled, result);
		Assert.AreEqual(1, first!.Runs);
		Assert.IsNull(second);
	}

	[TestMethod]
	public async Task log_lines_are_captured_with_their_level_and_in_order()
	{
		var job = Job();
		var seen = new List<JobLogLine>();
		job.LineLogged += (_, line) => seen.Add(line);
		var attention = new List<string>();
		job.AttentionWritten += (_, text) => attention.Add(text);
		job.AddProcessable(c => new Step("one") { Configuration = c });

		await job.ProcessOneAsync();
		job.Emit(JobLogLevel.Info, "hello");
		job.Emit(JobLogLevel.Warning, " careful ");
		job.Emit(JobLogLevel.Error, "broke");

		var lines = job.Log.Snapshot();
		CollectionAssert.AreEqual(seen, lines.ToList());
		CollectionAssert.AreEqual(new[] { JobLogLevel.Info, JobLogLevel.Warning, JobLogLevel.Error }, lines.TakeLast(3).Select(l => l.Level).ToArray());
		Assert.AreEqual("hello", lines[^3].Message);
		Assert.AreEqual("careful", lines[^2].Message);
		CollectionAssert.AreEqual(new[] { "careful", "broke" }, attention);
	}

	[TestMethod]
	public void the_log_keeps_only_the_newest_lines_and_counts_what_it_dropped()
	{
		var log = new JobLog();
		for (var i = 0; i < JobLog.MaxLines + 25; i++)
			log.Add(JobLogLevel.Info, $"line {i}", DateTimeOffset.UnixEpoch);

		var lines = log.Snapshot();
		Assert.AreEqual(JobLog.MaxLines, lines.Count);
		Assert.AreEqual("line 25", lines[0].Message);
		Assert.AreEqual($"line {JobLog.MaxLines + 24}", lines[^1].Message);
		Assert.AreEqual(25, log.Dropped);
	}

	[TestMethod]
	public void logged_text_has_secrets_removed_but_the_event_for_the_desktop_log_does_not()
	{
		var job = Job();
		string? desktop = null;
		job.LogWritten += (_, text) => desktop = text;

		job.Emit(JobLogLevel.Error, "GET https://cdn.example.com/a.aax?token=abc&sig=xyz failed");

		Assert.AreEqual("GET https://cdn.example.com/a.aax?token=abc&sig=xyz failed", desktop);
		Assert.AreEqual("GET https://cdn.example.com/a.aax failed", job.Log.Snapshot().Single().Message);
	}

	[TestMethod]
	[DataRow("see https://user:pw@host.example/p?a=1#frag now", "see https://host.example/p now")]
	[DataRow("Authorization: Bearer abc.def-123", "Authorization: [redacted]")]
	[DataRow("sent Bearer abc.def-123 ok", "sent Bearer [redacted] ok")]
	[DataRow("api_key=SECRET, next", "api_key=[redacted], next")]
	[DataRow("password: hunter2", "password: [redacted]")]
	[DataRow("plain text only", "plain text only")]
	public void redaction(string input, string expected)
		=> Assert.AreEqual(expected, LogRedactor.Redact(input));

	[TestMethod]
	public void very_long_lines_are_cut()
	{
		var redacted = LogRedactor.Redact(new string('x', LogRedactor.MaxLength + 50));
		Assert.AreEqual(LogRedactor.MaxLength + 1, redacted.Length);
	}

	private sealed class ThrottledStep : Processable
	{
		public override string Name => "Download";
		public override bool Validate(LibraryBook libraryBook) => true;

		public override Task<StatusHandler> ProcessAsync(LibraryBook libraryBook)
			=> throw new AudibleApi.ContentLicenseDeniedException(new Uri("https://audible.com/licenserequest"), new AudibleApi.Common.ContentLicense
			{
				Asin = libraryBook.Book.AudibleProductId,
				StatusCode = "Denied",
				LicenseDenialReasons =
				[
					new AudibleApi.Common.LicenseDenialReason
					{
						ValidationType = "Ownership",
						RejectionReason = AudibleApi.Common.RejectionReason.CustomerThrottled,
						Message = "Customer is throttled"
					}
				]
			});
	}

	private static AudibleUtilities.Account SeedAccount()
	{
		AudibleUtilities.AudibleApiStorage.EnsureAccountsSettingsFileExists();
		var account = new AudibleUtilities.Account("account") { IdentityTokens = new AudibleApi.Authorization.Identity(AudibleApi.Localization.Get("us")) };
		using var persister = AudibleUtilities.AudibleApiStorage.GetAccountsSettingsPersister();
		persister.AccountsSettings.Add(account);
		return account;
	}

	private static bool AccountRequiresReRegistration()
	{
		using var persister = AudibleUtilities.AudibleApiStorage.GetAccountsSettingsPersister();
		return persister.AccountsSettings.GetAccount("account", "us")!.RequiresReRegistration;
	}

	[TestMethod]
	public async Task with_a_gate_one_throttled_title_leaves_settings_and_the_account_alone()
	{
		SeedAccount();
		Configuration.Instance.UseWidevine = false;
		var job = Job();
		job.ThrottleRecovery = new ThrottleRecoveryGate();
		job.AddProcessable(c => new ThrottledStep { Configuration = c });

		var result = await job.ProcessOneAsync();

		Assert.AreEqual(ProcessBookResult.LicenseDeniedThrottled, result);
		Assert.IsFalse(Configuration.Instance.UseWidevine);
		Assert.IsFalse(AccountRequiresReRegistration());
		Assert.AreEqual(1, job.ThrottleRecovery.ThrottledTitleCount("account"));
	}

	[TestMethod]
	public async Task with_a_gate_the_account_is_recovered_once_enough_titles_are_throttled()
	{
		SeedAccount();
		Configuration.Instance.UseWidevine = false;
		var gate = new ThrottleRecoveryGate(distinctTitles: 2);
		gate.RecordThrottle("account", "B000000002");
		var job = Job();
		job.ThrottleRecovery = gate;
		job.AddProcessable(c => new ThrottledStep { Configuration = c });

		var result = await job.ProcessOneAsync();

		Assert.AreEqual(ProcessBookResult.LicenseDeniedThrottledRecovered, result);
		Assert.IsTrue(Configuration.Instance.UseWidevine);
		Assert.IsTrue(AccountRequiresReRegistration());
	}

	[TestMethod]
	public async Task without_a_gate_the_desktop_behaviour_of_recovering_at_once_is_kept()
	{
		SeedAccount();
		Configuration.Instance.UseWidevine = false;
		var job = Job();
		job.AddProcessable(c => new ThrottledStep { Configuration = c });

		var result = await job.ProcessOneAsync();

		Assert.AreEqual(ProcessBookResult.LicenseDeniedThrottledRecovered, result);
		Assert.IsTrue(AccountRequiresReRegistration());
	}
}
