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

		public Harness(int atOnce = 1, DiskFullPolicy onDiskFull = DiskFullPolicy.Ask)
		{
			Queue = new LiberationQueue(new LiberationPolicy { MaxConcurrentDownloads = () => atOnce, OnDiskFull = onDiskFull })
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
}
