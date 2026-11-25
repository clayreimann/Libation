using DataLayer;
using LibationFileManager;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Concurrent;

namespace LibationEngine.Tests;

[TestClass]
[DoNotParallelize]
public class LiberationQueueTests
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

	private string _dir = "";

	[TestInitialize]
	public void Init()
	{
		_dir = Path.Combine(Path.GetTempPath(), $"libation-engine-queue-{Guid.NewGuid():N}");
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

	private sealed class Harness
	{
		private readonly ConcurrentDictionary<string, int> _calls = new();

		public LiberationQueue Queue { get; }
		public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
		public ConcurrentQueue<string> Started { get; } = new();
		public ConcurrentQueue<string> Events { get; } = new();
		public Func<string, int, LiberationJob, Task<ProcessBookResult>> Outcome { get; set; }
			= (_, _, _) => Task.FromResult(ProcessBookResult.Success);

		public Harness(int atOnce = 1, DiskFullPolicy onDiskFull = DiskFullPolicy.Ask, DailyLimitPolicy onDailyLimit = DailyLimitPolicy.Wait)
		{
			Queue = new LiberationQueue(new LiberationPolicy { MaxConcurrentDownloads = () => atOnce, OnDiskFull = onDiskFull, OnDailyLimit = onDailyLimit })
			{
				Clock = Clock,
				MachineCeilingOverride = atOnce,
				DownloadHistory = _ => []
			};
			Queue.RunJob = async job =>
			{
				var asin = Asin(job);
				Started.Enqueue(asin);
				var result = await Outcome(asin, _calls.AddOrUpdate(asin, 1, (_, n) => n + 1), job);
				job.Result = result;
				job.Status = result == ProcessBookResult.Success ? ProcessBookStatus.Completed : ProcessBookStatus.Failed;
				return result;
			};
			Queue.QueuePaused += (_, p) => Events.Enqueue($"paused:{p.Reason}");
			Queue.QueueResumed += (_, r) => Events.Enqueue($"resumed:{r.Decision.Kind}");
			Queue.QueueWaiting += (_, w) => Events.Enqueue($"waiting:{w.Reason}");
			Queue.QueueWaitEnded += (_, w) => Events.Enqueue($"waitEnded:{w.Reason}");
		}

		public static string Asin(LiberationJob job) => job.LibraryBook.Book.AudibleProductId.ToString()!;

		public LiberationJob Job(string asin, bool download = false, bool plus = false)
		{
			var contributor = Contributor.GetEmpty();
			var book = new Book(new AudibleProductId(asin), asin, null, null, 1, ContentType.Product, [contributor], [contributor], "us");
			var libraryBook = new LibraryBook(book, new DateTime(2026, 8, 10), "account");
			libraryBook.SetIsAudiblePlus(plus);
			var job = new LiberationJob(libraryBook, Configuration.Instance, Queue.BadBookSession);
			if (download)
				job.AddDownloadDecryptBook();
			return job;
		}

		public void Add(params LiberationJob[] jobs)
		{
			Queue.Jobs.Enqueue(jobs);
			Queue.StartOrWake();
		}

		public async Task Finished()
		{
			var runner = Queue.Runner!;
			Assert.AreSame(runner, await Task.WhenAny(runner, Task.Delay(Patience)), "The queue loop did not finish.");
			await runner;
		}

		public async Task AdvanceUntil(Func<bool> condition, TimeSpan step, string description)
		{
			var deadline = DateTime.UtcNow + Patience;
			while (!condition())
			{
				if (DateTime.UtcNow > deadline)
					Assert.Fail($"Timed out advancing the clock until {description}.");
				Clock.Advance(step);
				await Task.Delay(15);
			}
		}
	}

	private static async Task Eventually(Func<bool> condition, string description)
	{
		var deadline = DateTime.UtcNow + Patience;
		while (!condition())
		{
			if (DateTime.UtcNow > deadline)
				Assert.Fail($"Timed out waiting for {description}.");
			await Task.Delay(10);
		}
	}

	private static Task<ProcessBookResult> Parked(TaskCompletionSource<ProcessBookResult> gate) => gate.Task;

	private static TaskCompletionSource<ProcessBookResult> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

	private static async Task<ProcessBookResult> UntilCancelled(LiberationJob job)
	{
		while (!job.CancellationRequested)
			await Task.Delay(5);
		return ProcessBookResult.Cancelled;
	}

	[TestMethod]
	public async Task a_failed_book_pauses_dispatch_until_the_decision_arrives()
	{
		var h = new Harness();
		h.Outcome = (asin, _, _) => Task.FromResult(asin == "A" ? ProcessBookResult.AwaitingDecision : ProcessBookResult.Success);
		LiberationJob a = h.Job("A"), b = h.Job("B");

		h.Add(a, b);
		await Eventually(() => h.Queue.Pending.Count == 1, "the pause");
		await Task.Delay(100);

		var pause = h.Queue.Pending.Single();
		Assert.AreEqual(PauseReason.BadBook, pause.Reason);
		Assert.AreEqual(a.Id, pause.Job);
		CollectionAssert.AreEqual(new[] { "A" }, h.Started.ToArray(), "A book started while the queue was paused.");

		Assert.IsTrue(h.Queue.Resume(pause.Id, Decision.BadBook(Configuration.BadBookAction.Retry)));
		await h.Finished();

		CollectionAssert.AreEqual(new[] { "A", "B" }, h.Started.ToArray());
		Assert.AreEqual(ProcessBookResult.FailedRetry, a.Result);
		CollectionAssert.AreEqual(new[] { "paused:BadBook", "resumed:BadBook" }, h.Events.ToArray());
		Assert.AreEqual(0, h.Queue.Pending.Count);
	}

	[TestMethod]
	public async Task a_stale_or_disallowed_answer_is_refused_and_leaves_the_pause_open()
	{
		var h = new Harness();
		h.Outcome = (_, _, _) => Task.FromResult(ProcessBookResult.AwaitingDecision);
		h.Add(h.Job("A"));
		await Eventually(() => h.Queue.Pending.Count == 1, "the pause");
		var id = h.Queue.Pending.Single().Id;

		Assert.IsFalse(h.Queue.Resume(new PauseId(id.Value + 99), Decision.Continue));
		Assert.IsFalse(h.Queue.Resume(id, Decision.Continue), "A bad book accepted a guidance answer.");
		Assert.IsFalse(h.Queue.Resume(id, Decision.Stop));
		Assert.AreEqual(1, h.Queue.Pending.Count);

		Assert.IsTrue(h.Queue.Resume(id, Decision.BadBook(Configuration.BadBookAction.Retry)));
		Assert.IsFalse(h.Queue.Resume(id, Decision.BadBook(Configuration.BadBookAction.Retry)), "The same pause was answered twice.");
		await h.Finished();
	}

	[TestMethod]
	public async Task apply_to_all_settles_every_waiting_bad_book_in_arrival_order()
	{
		var h = new Harness(atOnce: 3);
		var gates = new Dictionary<string, TaskCompletionSource<ProcessBookResult>> { ["A"] = Gate(), ["B"] = Gate(), ["C"] = Gate() };
		h.Outcome = (asin, _, _) => Parked(gates[asin]);
		LiberationJob a = h.Job("A"), b = h.Job("B"), c = h.Job("C");
		h.Add(a, b, c);
		await Eventually(() => h.Started.Count == 3, "three books in flight");

		foreach (var asin in new[] { "B", "C", "A" })
		{
			gates[asin].SetResult(ProcessBookResult.AwaitingDecision);
			var expected = asin == "B" ? 1 : asin == "C" ? 2 : 3;
			await Eventually(() => h.Queue.Pending.Count == expected, $"pause {expected}");
		}

		var order = h.Queue.Pending.Select(p => h.Queue.FindJob(p.Job!.Value)!).ToArray();
		CollectionAssert.AreEqual(new[] { b, c, a }, order, "Pauses are not in arrival order.");

		Assert.IsTrue(h.Queue.Resume(h.Queue.Pending[0].Id, Decision.BadBook(Configuration.BadBookAction.Retry, applyToAll: true)));
		await h.Finished();

		Assert.AreEqual(0, h.Queue.Pending.Count);
		Assert.IsTrue(new[] { a, b, c }.All(j => j.Result == ProcessBookResult.FailedRetry));
		Assert.AreEqual(3, h.Events.Count(e => e == "resumed:BadBook"));
		Assert.AreEqual(Configuration.BadBookAction.Retry, h.Queue.BadBookSession.Override);
	}

	[TestMethod]
	public async Task once_an_override_exists_later_failures_never_pause()
	{
		var h = new Harness();
		h.Outcome = (_, _, _) => Task.FromResult(ProcessBookResult.AwaitingDecision);
		LiberationJob a = h.Job("A"), b = h.Job("B");
		h.Add(a, b);
		await Eventually(() => h.Queue.Pending.Count == 1, "the pause");

		h.Queue.Resume(h.Queue.Pending.Single().Id, Decision.BadBook(Configuration.BadBookAction.Retry, applyToAll: true));
		await h.Finished();

		Assert.AreEqual(1, h.Events.Count(e => e == "paused:BadBook"));
		Assert.AreEqual(ProcessBookResult.FailedRetry, b.Result);
	}

	[TestMethod]
	public async Task an_abort_answer_tears_the_queue_down_once_and_the_other_waiting_books_are_cancelled()
	{
		var h = new Harness(atOnce: 2);
		var gates = new Dictionary<string, TaskCompletionSource<ProcessBookResult>> { ["A"] = Gate(), ["B"] = Gate() };
		h.Outcome = (asin, _, _) => Parked(gates[asin]);
		LiberationJob a = h.Job("A"), b = h.Job("B"), c = h.Job("C");
		h.Add(a, b, c);
		await Eventually(() => h.Started.Count == 2, "two books in flight");
		gates["A"].SetResult(ProcessBookResult.AwaitingDecision);
		await Eventually(() => h.Queue.Pending.Count == 1, "the first pause");
		gates["B"].SetResult(ProcessBookResult.AwaitingDecision);
		await Eventually(() => h.Queue.Pending.Count == 2, "the second pause");

		h.Queue.Resume(h.Queue.Pending[1].Id, Decision.BadBook(Configuration.BadBookAction.Abort));
		await h.Finished();

		Assert.AreEqual(ProcessBookResult.FailedAbort, b.Result);
		Assert.AreEqual(ProcessBookResult.Cancelled, a.Result);
		Assert.AreEqual(2, h.Started.Count, "A queued book started after the abort.");
		Assert.AreEqual(2, h.Queue.Jobs.Completed.Count);
		Assert.AreEqual(0, h.Queue.Jobs.Count(j => j.Status == ProcessBookStatus.Queued));
		Assert.AreEqual(0, h.Queue.Pending.Count);
	}

	[TestMethod]
	public async Task a_guidance_pause_is_raised_once_per_run_however_many_books_hit_it()
	{
		var h = new Harness(atOnce: 2);
		var gate = Gate();
		h.Outcome = (_, _, _) => Parked(gate);
		h.Add(h.Job("A"), h.Job("B"));
		await Eventually(() => h.Started.Count == 2, "both books in flight");
		gate.SetResult(ProcessBookResult.LicenseDeniedThrottled);
		await Eventually(() => h.Queue.Pending.Count == 1, "the pause");
		await Eventually(() => h.Queue.Jobs.Completed.Count == 2, "both books to finish");

		Assert.AreEqual(1, h.Queue.Pending.Count);
		Assert.AreEqual(PauseReason.LicenseGuidance, h.Queue.Pending[0].Reason);
		Assert.IsTrue(h.Queue.Resume(h.Queue.Pending[0].Id, Decision.Continue));
		await h.Finished();

		h.Add(h.Job("C"));
		await Eventually(() => h.Queue.Pending.Count == 1, "the pause on the next run");
		h.Queue.Resume(h.Queue.Pending[0].Id, Decision.Continue);
		await h.Finished();
	}

	[TestMethod]
	public async Task a_pause_stops_dispatch_only_so_running_books_finish_and_continue_resumes_the_rest()
	{
		var h = new Harness(atOnce: 2);
		var slow = Gate();
		h.Outcome = (asin, _, _) => asin switch
		{
			"A" => Parked(slow),
			"B" => Task.FromResult(ProcessBookResult.WidevineRecommended),
			_ => Task.FromResult(ProcessBookResult.Success)
		};
		LiberationJob a = h.Job("A"), b = h.Job("B"), c = h.Job("C");
		h.Add(a, b, c);
		await Eventually(() => h.Queue.Pending.Count == 1, "the pause");
		await Task.Delay(100);
		Assert.AreEqual(2, h.Started.Count, "A book started while the queue was paused.");

		slow.SetResult(ProcessBookResult.Success);
		await Eventually(() => a.Result == ProcessBookResult.Success, "the running book to finish");
		Assert.AreEqual(2, h.Started.Count);

		Assert.IsFalse(h.Queue.Resume(h.Queue.Pending[0].Id, Decision.Resume), "Resume is not an answer to guidance.");
		Assert.IsTrue(h.Queue.Resume(h.Queue.Pending[0].Id, Decision.Continue));
		await h.Finished();
		Assert.AreEqual(3, h.Started.Count);
	}

	[TestMethod]
	public async Task stopping_at_a_guidance_pause_clears_the_queue()
	{
		var h = new Harness();
		h.Outcome = (_, _, _) => Task.FromResult(ProcessBookResult.WidevineRecommended);
		h.Add(h.Job("A"), h.Job("B"), h.Job("C"));
		await Eventually(() => h.Queue.Pending.Count == 1, "the pause");

		Assert.IsTrue(h.Queue.Resume(h.Queue.Pending[0].Id, Decision.Stop));
		await h.Finished();

		Assert.AreEqual(1, h.Started.Count);
		Assert.AreEqual(0, h.Queue.Jobs.Count(j => j.Status == ProcessBookStatus.Queued));
	}

	[TestMethod]
	public async Task a_config_abort_tears_the_queue_down_once()
	{
		var h = new Harness(atOnce: 3);
		var gates = new[] { Gate(), Gate(), Gate() };
		h.Outcome = (asin, _, _) => Parked(gates[asin[0] - 'A']);
		var jobs = new[] { h.Job("A"), h.Job("B"), h.Job("C") };
		h.Add(jobs);
		await Eventually(() => h.Started.Count == 3, "three books in flight");

		foreach (var gate in gates)
			gate.SetResult(ProcessBookResult.FailedAbort);
		await h.Finished();

		Assert.AreEqual(1, jobs.Count(j => j.Result == ProcessBookResult.FailedAbort));
		Assert.AreEqual(2, jobs.Count(j => j.Result == ProcessBookResult.Cancelled));
	}

	[TestMethod]
	public async Task disk_full_under_ask_cancels_what_is_running_keeps_the_queue_and_resumes_from_the_start()
	{
		var h = new Harness(atOnce: 2);
		h.Outcome = (asin, call, job) => asin switch
		{
			"A" when call == 1 => Task.Run(async () => { await Task.Delay(50); return ProcessBookResult.DiskFull; }),
			"B" when call == 1 => UntilCancelled(job),
			_ => Task.FromResult(ProcessBookResult.Success)
		};
		LiberationJob a = h.Job("A"), b = h.Job("B"), c = h.Job("C");
		h.Add(a, b, c);

		await Eventually(() => h.Queue.Pending.Count == 1, "the disk full pause");
		var pause = h.Queue.Pending.Single();
		Assert.AreEqual(PauseReason.DiskFull, pause.Reason);
		CollectionAssert.AreEquivalent(new[] { DecisionKind.Resume, DecisionKind.Stop }, pause.Allowed.ToArray());
		await Eventually(() => a.Status == ProcessBookStatus.Queued && b.Status == ProcessBookStatus.Queued, "both books back in the queue");
		await Task.Delay(100);
		Assert.AreEqual(2, h.Started.Count, "A book started while the disk was full.");
		Assert.AreEqual(0, h.Queue.Jobs.Completed.Count);

		Assert.IsTrue(h.Queue.Resume(pause.Id, Decision.Resume));
		await h.Finished();

		Assert.AreEqual(5, h.Started.Count);
		Assert.IsTrue(new[] { a, b, c }.All(j => j.Result == ProcessBookResult.Success));
		Assert.AreEqual(3, h.Queue.Jobs.Completed.Count);
	}

	[TestMethod]
	public async Task disk_full_answered_stop_clears_the_queue()
	{
		var h = new Harness(atOnce: 1);
		h.Outcome = (_, _, _) => Task.FromResult(ProcessBookResult.DiskFull);
		h.Add(h.Job("A"), h.Job("B"));
		await Eventually(() => h.Queue.Pending.Count == 1, "the pause");

		Assert.IsTrue(h.Queue.Resume(h.Queue.Pending[0].Id, Decision.Stop));
		await h.Finished();

		Assert.AreEqual(1, h.Started.Count);
		Assert.AreEqual(0, h.Queue.Jobs.Count(j => j.Status == ProcessBookStatus.Queued));
	}

	[TestMethod]
	public async Task disk_full_under_stop_clears_the_queue_without_a_pause()
	{
		var h = new Harness(onDiskFull: DiskFullPolicy.Stop);
		h.Outcome = (_, _, _) => Task.FromResult(ProcessBookResult.DiskFull);
		LiberationJob a = h.Job("A");
		h.Add(a, h.Job("B"), h.Job("C"));

		await h.Finished();

		Assert.AreEqual(1, h.Started.Count);
		Assert.AreEqual(0, h.Events.Count);
		Assert.AreEqual(ProcessBookResult.DiskFull, a.Result);
		Assert.AreEqual(1, h.Queue.Jobs.Count());
	}

	[TestMethod]
	public async Task disk_full_under_wait_for_space_polls_every_minute_and_carries_on_when_space_returns()
	{
		var h = new Harness(onDiskFull: DiskFullPolicy.WaitForSpace);
		h.Outcome = (asin, call, _) => Task.FromResult(asin == "A" && call == 1 ? ProcessBookResult.DiskFull : ProcessBookResult.Success);
		var free = 10_000_000L;
		var probes = 0;
		h.Queue.SpaceProbe = () =>
		{
			Interlocked.Increment(ref probes);
			return [new VolumeSpace("/", Volatile.Read(ref free), 400_000_000)];
		};
		LiberationJob a = h.Job("A"), b = h.Job("B");

		h.Add(a, b);
		await Eventually(() => h.Queue.Waiting is { Volumes: not null }, "the first probe");

		var wait = h.Queue.Waiting!;
		Assert.AreEqual(WaitReason.DiskFull, wait.Reason);
		Assert.AreEqual(TimeSpan.FromMinutes(1), wait.NextCheck - h.Clock.GetUtcNow());
		Assert.AreEqual(0, h.Queue.Pending.Count);

		await h.AdvanceUntil(() => Volatile.Read(ref probes) >= 3, TimeSpan.FromMinutes(1), "two more polls");
		Assert.AreEqual(1, h.Started.Count, "A book started while space was short.");

		Volatile.Write(ref free, 900_000_000);
		await h.AdvanceUntil(() => h.Queue.Waiting is null, TimeSpan.FromMinutes(1), "the wait to end");
		await h.Finished();

		Assert.AreEqual(3, h.Started.Count);
		Assert.IsTrue(a.Result == ProcessBookResult.Success && b.Result == ProcessBookResult.Success);
		CollectionAssert.AreEqual(new[] { "waiting:DiskFull", "waitEnded:DiskFull" }, h.Events.ToArray());
	}

	[TestMethod]
	public async Task an_unreadable_probe_backs_off_five_fifteen_thirty_then_sixty_minutes()
	{
		var h = new Harness(onDiskFull: DiskFullPolicy.WaitForSpace);
		h.Outcome = (asin, call, _) => Task.FromResult(asin == "A" && call == 1 ? ProcessBookResult.DiskFull : ProcessBookResult.Success);
		var probes = 0;
		h.Queue.SpaceProbe = () => { Interlocked.Increment(ref probes); return null; };

		h.Add(h.Job("A"));

		foreach (var (minutes, expectedProbes) in new[] { (5, 1), (15, 2), (30, 3), (60, 4), (60, 5) })
		{
			await Eventually(() => Volatile.Read(ref probes) == expectedProbes, $"probe {expectedProbes}");
			await Eventually(() => h.Queue.Waiting?.NextCheck - h.Clock.GetUtcNow() == TimeSpan.FromMinutes(minutes), $"a {minutes} minute back-off");
			await Task.Delay(50);
			h.Clock.Advance(TimeSpan.FromMinutes(minutes));
		}

		h.Queue.Resume(h.Queue.Waiting!.Id, Decision.Stop);
		await h.Finished();
	}

	[TestMethod]
	public async Task a_wait_for_space_can_be_ended_early_with_resume()
	{
		var h = new Harness(onDiskFull: DiskFullPolicy.WaitForSpace);
		h.Outcome = (asin, call, _) => Task.FromResult(asin == "A" && call == 1 ? ProcessBookResult.DiskFull : ProcessBookResult.Success);
		h.Queue.SpaceProbe = () => [new VolumeSpace("/", 0, 400_000_000)];
		h.Add(h.Job("A"));
		await Eventually(() => h.Queue.Waiting is { Volumes: not null }, "the wait");

		Assert.IsFalse(h.Queue.Resume(new PauseId(h.Queue.Waiting!.Id.Value + 50), Decision.Resume));
		Assert.IsFalse(h.Queue.Resume(h.Queue.Waiting!.Id, Decision.Continue));
		Assert.IsTrue(h.Queue.Resume(h.Queue.Waiting!.Id, Decision.Resume));
		await h.Finished();

		Assert.AreEqual(2, h.Started.Count);
		Assert.IsNull(h.Queue.Waiting);
	}

	[TestMethod]
	public async Task the_daily_limit_stays_a_wait_the_queue_releases_itself()
	{
		Configuration.Instance.DailyDownloadLimit = Configuration.DailyLimitScope.AllBooks;
		Configuration.Instance.DailyDownloadLimitUnit = Configuration.DailyLimitUnit.Books;
		Configuration.Instance.DailyDownloadLimitQuantity = 1;
		var h = new Harness();
		IReadOnlyList<DownloadHistoryEntry> history = [new DownloadHistoryEntry(h.Clock.GetUtcNow() - TimeSpan.FromHours(1), "USED", false, 1)];
		h.Queue.DownloadHistory = _ => history;
		var a = h.Job("A", download: true);

		h.Add(a);
		await Eventually(() => h.Queue.Waiting is not null, "the wait");

		var wait = h.Queue.Waiting!;
		Assert.AreEqual(WaitReason.DailyLimit, wait.Reason);
		Assert.IsNotNull(wait.Allowance);
		Assert.AreEqual(a.Id, wait.Job);
		Assert.AreEqual(0, wait.Allowed.Count);
		Assert.IsFalse(h.Queue.Resume(wait.Id, Decision.Resume), "The daily limit accepted an early answer.");
		Assert.AreEqual(0, h.Queue.Pending.Count);

		history = [];
		await h.AdvanceUntil(() => h.Started.Count == 1, TimeSpan.FromSeconds(16), "the book to start");
		await h.Finished();

		CollectionAssert.AreEqual(new[] { "waiting:DailyLimit", "waitEnded:DailyLimit" }, h.Events.ToArray());
	}

	[TestMethod]
	public async Task the_skip_policy_leaves_blocked_books_for_the_next_run()
	{
		Configuration.Instance.DailyDownloadLimit = Configuration.DailyLimitScope.AllBooks;
		Configuration.Instance.DailyDownloadLimitUnit = Configuration.DailyLimitUnit.Books;
		Configuration.Instance.DailyDownloadLimitQuantity = 1;
		var h = new Harness(onDailyLimit: DailyLimitPolicy.Skip);
		h.Queue.DownloadHistory = _ => [new DownloadHistoryEntry(h.Clock.GetUtcNow() - TimeSpan.FromHours(1), "USED", false, 1)];
		var logs = new ConcurrentQueue<string>();
		h.Queue.LogWritten += (_, line) => logs.Enqueue(line);
		LiberationJob a = h.Job("A", download: true), b = h.Job("B", download: true), c = h.Job("C");

		h.Add(a, b, c);
		await h.Finished();

		CollectionAssert.AreEqual(new[] { "C" }, h.Started.ToArray());
		Assert.AreEqual(ProcessBookResult.SkippedDailyLimit, a.Result);
		Assert.AreEqual(ProcessBookResult.SkippedDailyLimit, b.Result);
		Assert.IsNull(h.Queue.Waiting);
		Assert.IsTrue(logs.Count > 0, "The limit was not announced.");
		CollectionAssert.AreEqual(new[] { "A", "B", "C" }, h.Queue.Jobs.Completed.Select(Harness.Asin).ToArray());
	}

	[TestMethod]
	public async Task a_stop_before_dispatch_drops_the_book_and_everything_behind_it()
	{
		var h = new Harness();
		h.Queue.StopBeforeDispatch = job => Harness.Asin(job) == "B" ? "enough" : null;
		LiberationJob a = h.Job("A"), b = h.Job("B"), c = h.Job("C");

		h.Add(a, b, c);
		await h.Finished();

		CollectionAssert.AreEqual(new[] { "A" }, h.Started.ToArray());
		Assert.AreEqual(ProcessBookResult.Cancelled, b.Result);
		Assert.AreEqual(0, h.Queue.Jobs.Count(j => j.Status == ProcessBookStatus.Queued));
	}

	[TestMethod]
	public async Task the_auto_responder_continues_guidance_retries_bad_books_and_stops_on_a_full_disk()
	{
		var h = new Harness(atOnce: 1);
		var warnings = new StringWriter();
		h.Outcome = (asin, _, _) => Task.FromResult(asin switch
		{
			"A" => ProcessBookResult.WidevineRecommended,
			"B" => ProcessBookResult.AwaitingDecision,
			"C" => ProcessBookResult.AwaitingDecision,
			"D" => ProcessBookResult.DiskFull,
			_ => ProcessBookResult.Success
		});
		_ = new AutoResponder(h.Queue, new LiberationPolicy { MaxConcurrentDownloads = () => 1 }, warnings);
		LiberationJob a = h.Job("A"), b = h.Job("B"), c = h.Job("C"), d = h.Job("D"), e = h.Job("E");

		h.Add(a, b, c, d, e);
		await h.Finished();

		CollectionAssert.AreEqual(new[] { "A", "B", "C", "D" }, h.Started.ToArray(), "The full disk should have stopped the queue.");
		Assert.AreEqual(ProcessBookResult.FailedRetry, b.Result);
		Assert.AreEqual(ProcessBookResult.FailedRetry, c.Result);
		Assert.AreEqual(1, h.Events.Count(x => x == "paused:BadBook"), "Apply to all should have answered the second bad book.");
		var text = warnings.ToString();
		StringAssert.Contains(text, "WidevineGuidance");
		StringAssert.Contains(text, "Book failed");
		StringAssert.Contains(text, "Disk full");
	}

	[TestMethod]
	public async Task cancel_all_while_a_book_awaits_a_decision_cancels_it_and_closes_the_pause()
	{
		var h = new Harness();
		h.Outcome = (_, _, _) => Task.FromResult(ProcessBookResult.AwaitingDecision);
		LiberationJob a = h.Job("A");
		h.Add(a, h.Job("B"));
		await Eventually(() => h.Queue.Pending.Count == 1, "the pause");

		await h.Queue.CancelAllAsync();
		await h.Finished();

		Assert.AreEqual(ProcessBookResult.Cancelled, a.Result);
		Assert.AreEqual(0, h.Queue.Pending.Count);
		Assert.AreEqual(1, h.Started.Count);
	}

	[TestMethod]
	public async Task cancelling_a_queued_job_removes_it_and_it_never_starts()
	{
		var h = new Harness();
		var gate = Gate();
		h.Outcome = (asin, _, _) => asin == "A" ? Parked(gate) : Task.FromResult(ProcessBookResult.Success);
		LiberationJob a = h.Job("A"), b = h.Job("B");
		h.Add(a, b);
		await Eventually(() => h.Queue.Jobs.GetActive().Count == 1, "A running");

		Assert.AreEqual(CancelJobOutcome.Removed, await h.Queue.CancelJobAsync(b.Id));
		gate.SetResult(ProcessBookResult.Success);
		await h.Finished();

		CollectionAssert.AreEqual(new[] { "A" }, h.Started.ToArray());
		Assert.IsFalse(h.Queue.Jobs.GetAllItems().Contains(b));
	}

	[TestMethod]
	public async Task cancelling_a_running_job_ends_it_cancelled_and_the_next_one_runs()
	{
		var h = new Harness();
		h.Outcome = (asin, _, job) => asin == "A" ? UntilCancelled(job) : Task.FromResult(ProcessBookResult.Success);
		LiberationJob a = h.Job("A"), b = h.Job("B");
		var ended = new List<string>();
		h.Queue.JobEnded += (_, j) => ended.Add($"{Harness.Asin(j)}:{j.Result}");
		h.Add(a, b);
		await Eventually(() => h.Queue.Jobs.GetActive().Count == 1, "A running");

		Assert.AreEqual(CancelJobOutcome.Cancelling, await h.Queue.CancelJobAsync(a.Id));
		await h.Finished();

		CollectionAssert.AreEqual(new[] { "A:Cancelled", "B:Success" }, ended);
		Assert.AreEqual(ProcessBookResult.Cancelled, a.Result);
	}

	[TestMethod]
	public async Task cancelling_an_unknown_or_finished_job_finds_nothing()
	{
		var h = new Harness();
		LiberationJob a = h.Job("A");
		h.Add(a);
		await h.Finished();

		Assert.AreEqual(CancelJobOutcome.NotFound, await h.Queue.CancelJobAsync(a.Id));
		Assert.AreEqual(CancelJobOutcome.NotFound, await h.Queue.CancelJobAsync(new JobId("nope")));
	}

	[TestMethod]
	public async Task cancelling_a_job_parked_at_the_daily_limit_ends_it_cancelled_and_announces_it()
	{
		Configuration.Instance.DailyDownloadLimit = Configuration.DailyLimitScope.AllBooks;
		Configuration.Instance.DailyDownloadLimitUnit = Configuration.DailyLimitUnit.Books;
		Configuration.Instance.DailyDownloadLimitQuantity = 1;
		var h = new Harness();
		h.Queue.DownloadHistory = _ => [new DownloadHistoryEntry(h.Clock.GetUtcNow() - TimeSpan.FromHours(1), "USED", false, 1)];
		var ended = new List<ProcessBookResult>();
		h.Queue.JobEnded += (_, j) => ended.Add(j.Result);
		LiberationJob a = h.Job("A", download: true);
		h.Add(a);
		await Eventually(() => h.Queue.Waiting is not null, "the limit wait");

		Assert.AreEqual(CancelJobOutcome.Cancelling, await h.Queue.CancelJobAsync(a.Id));
		await h.AdvanceUntil(() => h.Queue.Runner!.IsCompleted, TimeSpan.FromSeconds(20), "the run ends");

		CollectionAssert.AreEqual(new[] { ProcessBookResult.Cancelled }, ended);
		Assert.AreEqual(0, h.Started.Count);
	}

	[TestMethod]
	public async Task moving_a_queued_job_reorders_dispatch()
	{
		var h = new Harness();
		var gate = Gate();
		h.Outcome = (asin, _, _) => asin == "A" ? Parked(gate) : Task.FromResult(ProcessBookResult.Success);
		LiberationJob a = h.Job("A"), b = h.Job("B"), c = h.Job("C"), d = h.Job("D");
		h.Add(a, b, c, d);
		await Eventually(() => h.Queue.Jobs.GetActive().Count == 1, "A running");

		Assert.AreEqual(MoveJobOutcome.Moved, h.Queue.MoveJob(d.Id, QueuePosition.First));
		Assert.AreEqual(MoveJobOutcome.Moved, h.Queue.MoveJob(b.Id, QueuePosition.Last));
		Assert.AreEqual(MoveJobOutcome.Moved, h.Queue.MoveJob(c.Id, QueuePosition.OneDown));
		Assert.AreEqual(MoveJobOutcome.Moved, h.Queue.MoveJob(c.Id, QueuePosition.OneUp));
		gate.SetResult(ProcessBookResult.Success);
		await h.Finished();

		CollectionAssert.AreEqual(new[] { "A", "D", "C", "B" }, h.Started.ToArray());
	}

	[TestMethod]
	public async Task only_a_queued_job_can_be_moved()
	{
		var h = new Harness();
		var gate = Gate();
		h.Outcome = (_, _, _) => Parked(gate);
		LiberationJob a = h.Job("A");
		h.Add(a);
		await Eventually(() => h.Queue.Jobs.GetActive().Count == 1, "A running");

		Assert.AreEqual(MoveJobOutcome.NotQueued, h.Queue.MoveJob(a.Id, QueuePosition.First));
		Assert.AreEqual(MoveJobOutcome.NotFound, h.Queue.MoveJob(new JobId("nope"), QueuePosition.First));
		gate.SetResult(ProcessBookResult.Success);
		await h.Finished();
		Assert.AreEqual(MoveJobOutcome.NotQueued, h.Queue.MoveJob(a.Id, QueuePosition.First));
	}

	[TestMethod]
	public async Task a_user_pause_holds_new_books_while_running_ones_finish_and_resume_releases_them()
	{
		var h = new Harness();
		var gate = Gate();
		h.Outcome = (asin, _, _) => asin == "A" ? Parked(gate) : Task.FromResult(ProcessBookResult.Success);
		var changes = 0;
		h.Queue.DispatchPauseChanged += (_, _) => Interlocked.Increment(ref changes);
		h.Add(h.Job("A"), h.Job("B"));
		await Eventually(() => h.Queue.Jobs.GetActive().Count == 1, "A running");

		Assert.IsTrue(h.Queue.SetDispatchPaused(true));
		Assert.IsFalse(h.Queue.SetDispatchPaused(true));
		gate.SetResult(ProcessBookResult.Success);
		await Eventually(() => h.Queue.Jobs.GetCompleted().Count == 1, "A finished");
		await Task.Delay(100);

		CollectionAssert.AreEqual(new[] { "A" }, h.Started.ToArray());
		Assert.IsTrue(h.Queue.Running);
		Assert.IsTrue(h.Queue.DispatchPaused);

		Assert.IsTrue(h.Queue.SetDispatchPaused(false));
		await h.Finished();

		CollectionAssert.AreEqual(new[] { "A", "B" }, h.Started.ToArray());
		Assert.AreEqual(2, changes);
	}

	[TestMethod]
	public async Task a_pause_set_while_idle_holds_the_next_run()
	{
		var h = new Harness();
		h.Queue.SetDispatchPaused(true);
		h.Add(h.Job("A"));
		await Task.Delay(100);
		Assert.AreEqual(0, h.Started.Count);

		h.Queue.SetDispatchPaused(false);
		await h.Finished();
		Assert.AreEqual(1, h.Started.Count);
	}

	[TestMethod]
	public async Task finished_jobs_are_recorded_newest_first_and_clearing_resets_the_counts()
	{
		var h = new Harness();
		h.Outcome = (asin, _, _) => Task.FromResult(asin == "B" ? ProcessBookResult.FailedRetry : ProcessBookResult.Success);
		h.Add(h.Job("A"), h.Job("B"), h.Job("C"));
		await h.Finished();

		var history = h.Queue.History.Snapshot();
		CollectionAssert.AreEqual(new[] { "C", "B", "A" }, history.Select(j => j.Asin).ToArray());
		Assert.AreEqual(ProcessBookResult.FailedRetry, history[1].Result);
		Assert.AreEqual(h.Clock.GetUtcNow(), history[0].FinishedAt);
		var stats = h.Queue.Stats();
		Assert.AreEqual((0, 2, 1), (stats.Queued, stats.Completed, stats.Errors));
		Assert.IsNull(stats.RunningSince);

		h.Queue.ClearFinished();

		Assert.AreEqual(0, h.Queue.History.Snapshot().Count);
		Assert.AreEqual((0, 0, 0), (h.Queue.Stats().Queued, h.Queue.Stats().Completed, h.Queue.Stats().Errors));
	}

	[TestMethod]
	public async Task stats_count_queued_jobs_and_start_the_clock_with_the_run()
	{
		var h = new Harness();
		var gate = Gate();
		h.Outcome = (_, _, _) => Parked(gate);
		h.Add(h.Job("A"), h.Job("B"), h.Job("C"));
		await Eventually(() => h.Queue.Jobs.GetActive().Count == 1, "A running");

		var stats = h.Queue.Stats();
		Assert.AreEqual((2, 0, 0), (stats.Queued, stats.Completed, stats.Errors));
		Assert.AreEqual(h.Clock.GetUtcNow(), stats.RunningSince);

		await h.Queue.CancelAllAsync();
		gate.SetResult(ProcessBookResult.Cancelled);
		await h.Finished();
	}

	private static async Task AssertRunEnded(Harness h, string because)
	{
		await Eventually(() => !h.Queue.Running, because);
		var stats = h.Queue.Stats();
		Assert.IsNull(stats.RunningSince);
		Assert.AreEqual(0, stats.Queued);
	}

	[TestMethod]
	public async Task cancel_all_ends_the_run_while_dispatch_is_paused()
	{
		var h = new Harness();
		h.Queue.SetDispatchPaused(true);
		h.Add(h.Job("A"), h.Job("B"));
		await Eventually(() => h.Queue.Running, "run started");

		await h.Queue.CancelAllAsync();

		await AssertRunEnded(h, "the run to end after Cancel all");
	}

	[TestMethod]
	public async Task cancel_all_ends_the_run_when_the_running_job_unwinds()
	{
		var h = new Harness();
		var gate = Gate();
		h.Outcome = (_, _, _) => Parked(gate);
		h.Add(h.Job("A"), h.Job("B"));
		await Eventually(() => h.Queue.Jobs.GetActive().Count == 1, "A running");

		await h.Queue.CancelAllAsync();
		gate.SetResult(ProcessBookResult.Cancelled);

		await AssertRunEnded(h, "the run to end after Cancel all");
	}

	[TestMethod]
	public async Task a_failed_abort_ends_the_run()
	{
		var h = new Harness();
		h.Outcome = (_, _, _) => Task.FromResult(ProcessBookResult.FailedAbort);
		h.Add(h.Job("A"), h.Job("B"));

		await AssertRunEnded(h, "the run to end after a failed abort");
	}

	[TestMethod]
	public async Task a_job_that_throws_ends_the_run_and_is_not_left_running()
	{
		var h = new Harness();
		h.Outcome = (_, _, _) => throw new InvalidOperationException("boom");
		h.Add(h.Job("A"), h.Job("B"));

		await AssertRunEnded(h, "the run to end after a job threw");
		Assert.AreEqual(0, h.Queue.Jobs.GetActive().Count);
	}

	[TestMethod]
	public void history_keeps_only_the_newest_entries_up_to_its_capacity()
	{
		var h = new Harness();
		var history = new QueueHistory(3);
		var jobs = Enumerable.Range(1, 5).Select(i => h.Job($"J{i}")).ToArray();
		foreach (var job in jobs)
			history.Add(job, DateTimeOffset.UnixEpoch);

		CollectionAssert.AreEqual(new[] { "J5", "J4", "J3" }, history.Snapshot().Select(j => j.Asin).ToArray());
		Assert.IsNull(history.Find(jobs[0].Id));
		Assert.IsNotNull(history.Find(jobs[4].Id));
		Assert.AreEqual(200, new QueueHistory().Capacity);
	}
}
