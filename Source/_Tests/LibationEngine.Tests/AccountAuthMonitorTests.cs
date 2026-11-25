using AudibleApi;
using AudibleApi.Authorization;
using AudibleUtilities;
using LibationEngine.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
public class AccountAuthMonitorTests
{
	private static Account NeedsReRegistration(string id)
		=> new(id) { IdentityTokens = new Identity(Localization.Get("us")), RequiresReRegistration = true };

	private static Task Fail(Account account)
		=> Assert.ThrowsAsync<AuthenticationRequiredException>(() => ApiExtended.CreateAsync(account, allowInteractiveLogin: false));

	private static List<string> Drain(EventBroadcaster.Subscription sub)
	{
		var ids = new List<string>();
		while (sub.Reader.TryRead(out var evt))
			if (evt is AccountNeedsLogin login)
				ids.Add(login.AccountId);
		return ids;
	}

	[TestMethod]
	public async Task A_failed_login_publishes_once_per_streak_and_flags_the_account()
	{
		var events = new EventBroadcaster();
		using var sub = events.Subscribe();
		using var monitor = new AccountAuthMonitor(events);
		var id = $"{Guid.NewGuid():N}@example.com";

		await Fail(NeedsReRegistration(id));
		await Fail(NeedsReRegistration(id));

		CollectionAssert.AreEqual(new[] { id }, Drain(sub).Where(i => i == id).ToArray());
		Assert.IsTrue(monitor.IsFlagged(id.ToUpperInvariant()));

		monitor.ClearAll();
		Assert.IsFalse(monitor.IsFlagged(id));
		await Fail(NeedsReRegistration(id));
		CollectionAssert.AreEqual(new[] { id }, Drain(sub).Where(i => i == id).ToArray());
	}

	[TestMethod]
	public async Task A_successful_scan_publishes_signed_in_for_each_flagged_account_once()
	{
		var events = new EventBroadcaster();
		using var sub = events.Subscribe();
		using var monitor = new AccountAuthMonitor(events);
		var id = $"{Guid.NewGuid():N}@example.com";
		await Fail(NeedsReRegistration(id));

		monitor.ClearAll();
		monitor.ClearAll();

		var signedIn = new List<AccountSignedIn>();
		while (sub.Reader.TryRead(out var evt))
			if (evt is AccountSignedIn s && s.AccountId == id)
				signedIn.Add(s);
		Assert.AreEqual(1, signedIn.Count);
		Assert.AreEqual("us", signedIn[0].Locale);
	}

	[TestMethod]
	public void A_subset_scan_only_signs_in_the_flagged_accounts_it_covered()
	{
		var events = new EventBroadcaster();
		using var sub = events.Subscribe();
		using var monitor = new AccountAuthMonitor(events);
		AccountDto Account(string id, string locale) => new(id, id + " name", locale, true, AuthStates.NeedsLogin, CredentialKinds.None, []);
		var accounts = new[] { Account("a@example.com", "us"), Account("a@example.com", "uk"), Account("b@example.com", "us") };
		monitor.Flag("a@example.com", "uk");
		monitor.Flag("b@example.com", "us");

		monitor.ScanSucceeded(new ScanSelection(["a@example.com"], "us"), () => accounts);
		Assert.IsTrue(monitor.IsFlagged("a@example.com"));

		monitor.ScanSucceeded(new ScanSelection(["a@example.com"], "uk"), () => accounts);

		Assert.IsFalse(monitor.IsFlagged("a@example.com"));
		Assert.IsTrue(monitor.IsFlagged("b@example.com"));
		Assert.IsTrue(sub.Reader.TryRead(out var evt));
		Assert.AreEqual(("a@example.com", "uk"), (((AccountSignedIn)evt!).AccountId, ((AccountSignedIn)evt).Locale));
		Assert.IsFalse(sub.Reader.TryRead(out _));
	}

	[TestMethod]
	public void SignedIn_publishes_even_when_the_account_was_not_flagged()
	{
		var events = new EventBroadcaster();
		using var sub = events.Subscribe();
		using var monitor = new AccountAuthMonitor(events);

		monitor.SignedIn("a@example.com", "uk");

		Assert.IsTrue(sub.Reader.TryRead(out var evt));
		Assert.AreEqual(new AccountSignedIn("a@example.com", "uk") { At = evt!.At }, evt);
	}

	[TestMethod]
	public async Task Clear_unflags_one_account_and_dispose_stops_listening()
	{
		var events = new EventBroadcaster();
		using var sub = events.Subscribe();
		var monitor = new AccountAuthMonitor(events);
		var id = $"{Guid.NewGuid():N}@example.com";
		await Fail(NeedsReRegistration(id));
		monitor.Clear(id);
		Assert.IsFalse(monitor.IsFlagged(id));

		monitor.Dispose();
		Drain(sub);
		await Fail(NeedsReRegistration(id));

		Assert.IsFalse(monitor.IsFlagged(id));
		Assert.IsEmpty(Drain(sub));
	}

	[TestMethod]
	public void The_event_carries_only_the_account_id()
	{
		var json = System.Text.Json.JsonSerializer.Serialize(new AccountNeedsLogin("a@example.com"), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
		var names = System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
		CollectionAssert.AreEqual(new[] { "accountId", "at" }, names);
	}

	[TestMethod]
	public async Task Scheduler_runs_the_account_subset_job_and_reports_success()
	{
		using var dir = new TempDir();
		var subsets = new List<string>();
		var successes = 0;
		var scanCalls = 0;
		var jobs = new DaemonJobs(_ => { scanCalls++; return Task.FromResult(0); }, _ => Task.FromResult(0), _ => Task.FromResult(0))
		{
			ScanAccounts = (selection, _) => { subsets.Add(string.Join(',', selection.Accounts!)); return Task.FromResult(0); }
		};
		var scheduler = new CycleScheduler(new DaemonOptions(TimeSpan.FromMinutes(30), false, "http://127.0.0.1:0", dir.Path), jobs, new CycleState())
		{
			OnScanSucceeded = _ => successes++
		};
		using var cts = new CancellationTokenSource();
		var run = scheduler.RunAsync(cts.Token);
		await Eventually(() => scheduler.TryRequestRun(RunKind.Scan, new ScanSelection(["a", "b"], null)));
		await Eventually(() => subsets.Count == 1);
		cts.Cancel();
		await run;

		Assert.AreEqual("a,b", subsets[0]);
		Assert.AreEqual(1, scanCalls);
		Assert.AreEqual(2, successes);
	}

	private static async Task Eventually(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (DateTime.UtcNow < deadline)
		{
			if (condition())
				return;
			await Task.Delay(20);
		}
		Assert.Fail("Condition not met within 10s.");
	}
}
