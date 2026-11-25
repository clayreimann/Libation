using System.Text.Json;
using DataLayer;
using LibationEngine.Contracts;
using LibationFileManager;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
[DoNotParallelize]
public class QueueEventPublisherTests
{
	private string _dir = "";

	[TestInitialize]
	public void Init()
	{
		_dir = Path.Combine(Path.GetTempPath(), $"libation-engine-events-{Guid.NewGuid():N}");
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
		public readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
		public readonly EventBroadcaster Events = new();
		public readonly EventBroadcaster.Subscription Subscription;
		public readonly LiberationQueue Queue;
		public Func<LiberationJob, Task<ProcessBookResult>> Outcome = _ => Task.FromResult(ProcessBookResult.Success);

		public Harness(DiskFullPolicy onDiskFull = DiskFullPolicy.Ask)
		{
			Queue = new LiberationQueue(new LiberationPolicy { MaxConcurrentDownloads = () => 1, OnDiskFull = onDiskFull })
			{
				Clock = Clock,
				MachineCeilingOverride = 1,
				DownloadHistory = _ => []
			};
			Queue.RunJob = async job =>
			{
				var result = await Outcome(job);
				job.Result = result;
				job.Status = result == ProcessBookResult.Success ? ProcessBookStatus.Completed : ProcessBookStatus.Failed;
				return result;
			};
			_ = new QueueEventPublisher(Queue, Events, Clock);
			Subscription = Events.Subscribe();
		}

		public LiberationJob Job(string asin)
		{
			var contributor = Contributor.GetEmpty();
			var book = new Book(new AudibleProductId(asin), $"Title {asin}", null, null, 1, ContentType.Product, [contributor], [contributor], "us");
			return new LiberationJob(new LibraryBook(book, new DateTime(2026, 8, 10), "account"), Configuration.Instance, Queue.BadBookSession);
		}

		public async Task RunAsync(params LiberationJob[] jobs)
		{
			Queue.Jobs.Enqueue(jobs);
			Queue.StartOrWake();
			await Queue.Runner!.WaitAsync(TimeSpan.FromSeconds(10));
		}

		public List<EngineEvent> Drain()
		{
			var list = new List<EngineEvent>();
			while (Subscription.Reader.TryRead(out var evt))
				list.Add(evt);
			return list;
		}
	}

	private static async Task Eventually(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
		while (!condition())
		{
			Assert.IsTrue(DateTime.UtcNow < deadline, "Timed out.");
			await Task.Delay(10);
		}
	}

	[TestMethod]
	public async Task a_book_publishes_started_then_finished_with_its_result()
	{
		var h = new Harness();
		var job = h.Job("B000000001");

		await h.RunAsync(job);

		var events = h.Drain();
		var started = events.OfType<BookStarted>().Single();
		Assert.AreEqual(("B000000001", "Title B000000001", job.Id.Value), (started.Asin, started.Title, started.JobId));
		var finished = events.OfType<BookFinished>().Single();
		Assert.AreEqual(("success", "completed"), (finished.Result, finished.Status));
		Assert.IsTrue(events.IndexOf(started) < events.IndexOf(finished));
	}

	[TestMethod]
	public async Task progress_is_published_at_most_once_a_second_per_book()
	{
		var h = new Harness();
		var job = h.Job("B000000001");
		var release = new TaskCompletionSource();
		h.Outcome = async j =>
		{
			j.ReportProgress(1);
			j.ReportProgress(2);
			h.Clock.Advance(TimeSpan.FromMilliseconds(500));
			j.ReportProgress(3);
			h.Clock.Advance(TimeSpan.FromMilliseconds(600));
			j.ReportProgress(4);
			j.ReportProgress(5);
			await release.Task;
			return ProcessBookResult.Success;
		};

		h.Queue.Jobs.Enqueue([job]);
		h.Queue.StartOrWake();
		await Eventually(() => h.Subscription.Reader.Count >= 3);
		release.SetResult();
		await h.Queue.Runner!;

		CollectionAssert.AreEqual(new[] { 1, 4 }, h.Drain().OfType<BookProgress>().Select(p => p.Percent).ToArray());
	}

	[TestMethod]
	public async Task a_bad_book_pause_and_its_answer_are_published_with_their_shape()
	{
		var h = new Harness();
		h.Outcome = _ => Task.FromResult(ProcessBookResult.AwaitingDecision);
		var job = h.Job("B000000001");

		h.Queue.Jobs.Enqueue([job]);
		h.Queue.StartOrWake();
		await Eventually(() => h.Queue.Pending.Count == 1);
		var pending = h.Queue.Pending[0];
		Assert.IsTrue(h.Queue.Resume(pending.Id, Decision.BadBook(Configuration.BadBookAction.Retry)));
		await h.Queue.Runner!.WaitAsync(TimeSpan.FromSeconds(10));

		var events = h.Drain();
		var paused = events.OfType<QueuePaused>().Single().Pause;
		Assert.AreEqual(("badBook", job.Id.Value), (paused.Reason, paused.JobId));
		CollectionAssert.AreEqual(new[] { "badBook" }, paused.Allowed.ToArray());
		var resumed = events.OfType<QueueResumed>().Single();
		Assert.AreEqual((pending.Id.Value, "badBook"), (resumed.PauseId, resumed.Decision));
		Assert.AreEqual("failedRetry", events.OfType<BookFinished>().Single().Result);
	}

	[TestMethod]
	public async Task a_disk_full_wait_is_published_and_ended_by_resume()
	{
		var h = new Harness(DiskFullPolicy.WaitForSpace);
		var calls = 0;
		h.Outcome = _ => Task.FromResult(Interlocked.Increment(ref calls) == 1 ? ProcessBookResult.DiskFull : ProcessBookResult.Success);
		h.Queue.SpaceProbe = () => null;

		h.Queue.Jobs.Enqueue([h.Job("B000000001")]);
		h.Queue.StartOrWake();
		await Eventually(() => h.Queue.Waiting is not null);
		Assert.IsTrue(h.Queue.Resume(h.Queue.Waiting!.Id, Decision.Resume));
		await h.Queue.Runner!.WaitAsync(TimeSpan.FromSeconds(10));

		var events = h.Drain();
		var wait = events.OfType<QueueWaiting>().Single().Wait;
		Assert.AreEqual("diskFull", wait.Reason);
		CollectionAssert.AreEqual(new[] { "resume", "stop" }, wait.Allowed.ToArray());
		Assert.AreEqual("diskFull", events.OfType<QueueWaitEnded>().Single().Reason);
	}

	[TestMethod]
	public void events_serialise_as_camel_case_without_secrets()
	{
		var json = JsonSerializer.Serialize(new QueuePaused(new(7, "badBook", "abc", ["badBook"])), new JsonSerializerOptions(JsonSerializerDefaults.Web));
		StringAssert.Contains(json, "\"pause\":{\"id\":7,\"reason\":\"badBook\",\"jobId\":\"abc\",\"allowed\":[\"badBook\"]}");
	}

	[TestMethod]
	public async Task the_snapshot_lists_working_and_queued_jobs_then_pauses()
	{
		var h = new Harness();
		var release = new TaskCompletionSource();
		h.Outcome = async _ => { await release.Task; return ProcessBookResult.Success; };
		LiberationJob a = h.Job("A"), b = h.Job("B");

		h.Queue.Jobs.Enqueue([a, b]);
		h.Queue.StartOrWake();
		await Eventually(() => h.Queue.Jobs.GetActive().Count == 1);

		var snapshot = QueueMapper.Snapshot(h.Queue);
		Assert.IsTrue(snapshot.Running);
		CollectionAssert.AreEqual(new[] { ("A", "working"), ("B", "queued") }, snapshot.Jobs.Select(j => (j.Asin, j.State)).ToArray());
		Assert.AreEqual(0, snapshot.Pending.Count);
		Assert.IsNull(snapshot.Waiting);

		release.SetResult();
		await h.Queue.Runner!;
		Assert.AreEqual(0, QueueMapper.Snapshot(h.Queue).Jobs.Count);
	}

	[TestMethod]
	[DataRow("continue", null, DecisionKind.Continue)]
	[DataRow("Stop", null, DecisionKind.Stop)]
	[DataRow("resume", null, DecisionKind.Resume)]
	[DataRow("badBook", "retry", DecisionKind.BadBook)]
	public void decision_bodies_parse(string decision, string? action, DecisionKind expected)
	{
		Assert.IsTrue(QueueMapper.TryParse(new DecisionRequest(decision, action), out var parsed, out _));
		Assert.AreEqual(expected, parsed.Kind);
	}

	[TestMethod]
	[DataRow(null, null)]
	[DataRow("explode", null)]
	[DataRow("badBook", null)]
	[DataRow("badBook", "ask")]
	public void malformed_decision_bodies_are_refused(string? decision, string? action)
	{
		Assert.IsFalse(QueueMapper.TryParse(new DecisionRequest(decision, action), out _, out var error));
		Assert.IsFalse(string.IsNullOrEmpty(error));
	}

	[TestMethod]
	public void pausing_and_resuming_dispatch_publish_once_per_change()
	{
		var h = new Harness();

		h.Queue.SetDispatchPaused(true);
		h.Queue.SetDispatchPaused(true);
		h.Queue.SetDispatchPaused(false);

		CollectionAssert.AreEqual(new[] { typeof(QueueDispatchPaused), typeof(QueueDispatchResumed) }, h.Drain().Select(e => e.GetType()).ToArray());
	}

	[TestMethod]
	public async Task log_lines_are_published_with_their_time_level_and_redacted_text()
	{
		var h = new Harness();
		h.Outcome = j =>
		{
			j.Emit(JobLogLevel.Info, "fetching https://cdn.example.com/a?token=abc");
			j.Emit(JobLogLevel.Error, "boom");
			return Task.FromResult(ProcessBookResult.Success);
		};
		var job = h.Job("B000000001");

		await h.RunAsync(job);

		var logs = h.Drain().OfType<BookLog>().ToList();
		CollectionAssert.AreEqual(new[] { "info", "error" }, logs.Select(l => l.Level).ToArray());
		Assert.AreEqual("fetching https://cdn.example.com/a", logs[0].Message);
		Assert.AreEqual(job.Log.Snapshot()[0].At, logs[0].At);
		Assert.AreEqual(job.Id.Value, logs[0].JobId);
	}

	[TestMethod]
	public async Task a_burst_of_info_lines_is_throttled_but_errors_always_get_through()
	{
		var h = new Harness();
		h.Outcome = j =>
		{
			for (var i = 0; i < 100; i++)
				j.Emit(JobLogLevel.Info, $"info {i}");
			j.Emit(JobLogLevel.Error, "late error");
			h.Clock.Advance(TimeSpan.FromSeconds(1.5));
			j.Emit(JobLogLevel.Info, "next window");
			return Task.FromResult(ProcessBookResult.Success);
		};
		var job = h.Job("B000000001");

		await h.RunAsync(job);

		var logs = h.Drain().OfType<BookLog>().ToList();
		Assert.AreEqual(QueueEventPublisher.InfoLinesPerSecond, logs.Count(l => l.Level == "info" && l.Message.StartsWith("info ")));
		Assert.IsTrue(logs.Any(l => l.Message == "late error"));
		Assert.IsTrue(logs.Any(l => l.Message == "next window"));
		Assert.AreEqual(100, job.Log.Snapshot().Count(l => l.Message.StartsWith("info ")));
	}

	[TestMethod]
	public async Task lines_after_a_job_has_ended_are_not_published()
	{
		var h = new Harness();
		var job = h.Job("B000000001");
		await h.RunAsync(job);
		h.Drain();

		job.Emit(JobLogLevel.Error, "after");

		Assert.AreEqual(0, h.Drain().OfType<BookLog>().Count());
	}

	[TestMethod]
	public void the_snapshot_carries_the_pause_flag_and_counters()
	{
		var h = new Harness();
		h.Queue.SetDispatchPaused(true);
		h.Queue.Jobs.Enqueue([h.Job("A"), h.Job("B")]);

		var snapshot = QueueMapper.Snapshot(h.Queue);

		Assert.IsTrue(snapshot.Paused);
		Assert.AreEqual(new QueueStatsDto(2, 0, 0, null), snapshot.Stats);
	}

	[TestMethod]
	[DataRow("top", QueuePosition.First)]
	[DataRow("Up", QueuePosition.OneUp)]
	[DataRow("down", QueuePosition.OneDown)]
	[DataRow(" bottom ", QueuePosition.Last)]
	public void move_bodies_parse(string to, QueuePosition expected)
	{
		Assert.IsTrue(QueueMapper.TryParse(new MoveRequest(to), out var position));
		Assert.AreEqual(expected, position);
	}

	[TestMethod]
	[DataRow(null)]
	[DataRow("sideways")]
	public void malformed_move_bodies_are_refused(string? to)
		=> Assert.IsFalse(QueueMapper.TryParse(to is null ? null : new MoveRequest(to), out _));
}
