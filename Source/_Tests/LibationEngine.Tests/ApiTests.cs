using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using LibationEngine.Contracts;
using LibationEngine.Host;
using DataLayer;
using LibationFileManager;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
public class ApiTests
{
	private string configDir = "";

	[TestInitialize]
	public void Init()
	{
		configDir = Path.Combine(Path.GetTempPath(), $"libation-engine-api-{Guid.NewGuid():N}");
		Directory.CreateDirectory(configDir);
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, configDir);
		Configuration.CreateMockInstance();
	}

	[TestCleanup]
	public void Cleanup()
	{
		Configuration.RestoreSingletonInstance();
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, null);
		try { Directory.Delete(configDir, recursive: true); } catch { }
	}

	private sealed class FakeData : IEngineData
	{
		public (int Offset, int Limit)? LastPage;

		public IReadOnlyList<AccountDto> GetAccounts() => [new("a@example.com", "Main", "us", true)];

		public LibraryPageDto GetLibrary(int offset, int limit)
		{
			LastPage = (offset, limit);
			return new(1, offset, limit, [Book("B000000001")]);
		}

		public BookDto? GetBook(string asin) => asin == "B000000001" ? Book(asin) : null;

		private static BookDto Book(string asin)
			=> new(asin, "Title", null, ["Author"], ["Narrator"], [], 60, "NotLiberated", null, DateTime.UnixEpoch, "a@example.com", "us", "Product", false);
	}

	private sealed class Harness : IAsyncDisposable
	{
		private readonly TempDir dir = new();
		private readonly CancellationTokenSource cts = new();
		private readonly Task<int> run;
		public readonly FakeData Data = new();
		public readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
		public readonly string Base;
		public int ScanCalls, LiberateCalls;
		public volatile TaskCompletionSource? Gate;
		public readonly SemaphoreSlim ScanEntered = new(0);

		public readonly LiberationQueue Queue;
		public volatile Func<LiberationJob, Task<ProcessBookResult>> Outcome = _ => Task.FromResult(ProcessBookResult.Success);

		public Harness(bool api, string? tokenFile = null, bool startRun = true, DiskFullPolicy onDiskFull = DiskFullPolicy.Ask)
		{
			Queue = new LiberationQueue(new LiberationPolicy { MaxConcurrentDownloads = () => 1, OnDiskFull = onDiskFull })
			{
				MachineCeilingOverride = 1,
				SpaceProbe = () => null
			};
			Queue.RunJob = async job =>
			{
				var result = await Outcome(job);
				job.Result = result;
				job.Status = result == ProcessBookResult.Success ? ProcessBookStatus.Completed : ProcessBookStatus.Failed;
				return result;
			};

			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			var port = ((IPEndPoint)listener.LocalEndpoint).Port;
			listener.Stop();
			Base = $"http://127.0.0.1:{port}";

			var jobs = new DaemonJobs(
				async ct =>
				{
					Interlocked.Increment(ref ScanCalls);
					ScanEntered.Release();
					if (Gate is { } gate)
						await gate.Task.WaitAsync(ct);
					return 0;
				},
				_ => { Interlocked.Increment(ref LiberateCalls); return Task.FromResult(0); },
				_ => Task.FromResult(0));

			run = LibationDaemon.RunAsync(
				new DaemonOptions(TimeSpan.FromMinutes(30), false, Base, dir.Path, ApiEnabled: api, ApiTokenFile: tokenFile),
				jobs, cts.Token, data: Data, queue: Queue);
		}

		public Task<int> Run => run;

		public LiberationJob Job(string asin)
		{
			var contributor = Contributor.GetEmpty();
			var book = new Book(new AudibleProductId(asin), $"Title {asin}", null, null, 1, ContentType.Product, [contributor], [contributor], "us");
			return new LiberationJob(new LibraryBook(book, new DateTime(2026, 8, 10), "account"), Configuration.Instance, Queue.BadBookSession);
		}

		public void Enqueue(params LiberationJob[] jobs)
		{
			Queue.Jobs.Enqueue(jobs);
			Queue.StartOrWake();
		}

		public async Task<JsonDocument> QueueJson()
			=> JsonDocument.Parse(await Http.GetStringAsync(Base + "/api/queue"));

		public Task<HttpResponseMessage> Decide(long id, string json)
			=> Http.PostAsync($"{Base}/api/queue/decisions/{id}", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

		public async Task WaitIdleAfterFirstCycle()
		{
			await Eventually(async () =>
			{
				try
				{
					using var doc = JsonDocument.Parse(await Http.GetStringAsync(Base + "/health"));
					return doc.RootElement.GetProperty("liberateExitCode").ValueKind == JsonValueKind.Number
						&& doc.RootElement.GetProperty("state").GetString() == "idle";
				}
				catch (HttpRequestException) { return false; }
			});
		}

		public async ValueTask DisposeAsync()
		{
			cts.Cancel();
			await run.WaitAsync(TimeSpan.FromSeconds(10));
			Http.Dispose();
			dir.Dispose();
		}
	}

	private static async Task Eventually(Func<Task<bool>> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (DateTime.UtcNow < deadline)
		{
			if (await condition())
				return;
			await Task.Delay(20);
		}
		Assert.Fail("Condition not met within 10s.");
	}

	[TestMethod]
	public async Task Api_is_404_when_disabled_but_health_works()
	{
		await using var h = new Harness(api: false);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.GetAsync(h.Base + "/api/status")).StatusCode);
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.PostAsync(h.Base + "/api/scan", null)).StatusCode);
		Assert.AreEqual(HttpStatusCode.OK, (await h.Http.GetAsync(h.Base + "/health")).StatusCode);
	}

	[TestMethod]
	public async Task Token_is_required_for_api_but_not_health()
	{
		using var dir = new TempDir();
		var tokenFile = dir.File("token");
		File.WriteAllText(tokenFile, "s3cret\n");
		await using var h = new Harness(api: true, tokenFile);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.OK, (await h.Http.GetAsync(h.Base + "/health")).StatusCode);
		Assert.AreEqual(HttpStatusCode.Unauthorized, (await h.Http.GetAsync(h.Base + "/api/status")).StatusCode);
		Assert.AreEqual(HttpStatusCode.Unauthorized, (await h.Http.PostAsync(h.Base + "/api/scan", null)).StatusCode);

		var wrong = new HttpRequestMessage(HttpMethod.Get, h.Base + "/api/status");
		wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
		Assert.AreEqual(HttpStatusCode.Unauthorized, (await h.Http.SendAsync(wrong)).StatusCode);

		var right = new HttpRequestMessage(HttpMethod.Get, h.Base + "/api/status");
		right.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "s3cret");
		Assert.AreEqual(HttpStatusCode.OK, (await h.Http.SendAsync(right)).StatusCode);
	}

	[TestMethod]
	public async Task Missing_or_empty_token_file_fails_startup()
	{
		using var dir = new TempDir();
		var empty = dir.File("empty");
		File.WriteAllText(empty, " \n");

		foreach (var path in new[] { dir.File("missing"), empty })
		{
			await using var h = new Harness(api: true, path);
			Assert.AreEqual(1, await h.Run.WaitAsync(TimeSpan.FromSeconds(10)));
		}
	}

	[TestMethod]
	public async Task Status_reports_state_interval_and_version_key()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var doc = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/status"));
		Assert.AreEqual("idle", doc.RootElement.GetProperty("state").GetString());
		Assert.AreEqual(1800, doc.RootElement.GetProperty("intervalSeconds").GetInt32());
		Assert.IsTrue(doc.RootElement.TryGetProperty("version", out _));
	}

	[TestMethod]
	public async Task Scan_runs_exactly_once_and_a_concurrent_request_gets_409()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		var liberatesBefore = h.LiberateCalls;
		while (h.ScanEntered.CurrentCount > 0)
			await h.ScanEntered.WaitAsync();
		h.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

		var first = await h.Http.PostAsync(h.Base + "/api/scan", null);
		Assert.AreEqual(HttpStatusCode.Accepted, first.StatusCode);
		await h.ScanEntered.WaitAsync(TimeSpan.FromSeconds(10));

		var second = await h.Http.PostAsync(h.Base + "/api/scan", null);
		var third = await h.Http.PostAsync(h.Base + "/api/liberate", null);
		Assert.AreEqual(HttpStatusCode.Conflict, second.StatusCode);
		Assert.AreEqual(HttpStatusCode.Conflict, third.StatusCode);

		h.Gate.SetResult();
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(2, h.ScanCalls);
		Assert.AreEqual(liberatesBefore, h.LiberateCalls);
	}

	[TestMethod]
	public async Task Liberate_runs_scan_then_liberate()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.Accepted, (await h.Http.PostAsync(h.Base + "/api/liberate", null)).StatusCode);
		await Eventually(() => Task.FromResult(h.LiberateCalls == 2));

		Assert.AreEqual(2, h.ScanCalls);
	}

	[TestMethod]
	public async Task Events_stream_delivers_scan_started_and_finished()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var request = new HttpRequestMessage(HttpMethod.Get, h.Base + "/api/events");
		using var response = await h.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
		Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
		using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

		Assert.AreEqual(HttpStatusCode.Accepted, (await h.Http.PostAsync(h.Base + "/api/scan", null)).StatusCode);

		var seen = new List<string>();
		string? finishedData = null;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (finishedData is null)
		{
			var line = await reader.ReadLineAsync(timeout.Token);
			Assert.IsNotNull(line);
			if (line.StartsWith("event: "))
				seen.Add(line[7..]);
			else if (line.StartsWith("data: ") && seen.LastOrDefault() == nameof(ScanFinished))
				finishedData = line[6..];
		}

		CollectionAssert.IsSubsetOf(new[] { nameof(ScanStarted), nameof(ScanFinished) }, seen);
		Assert.IsLessThan(seen.IndexOf(nameof(ScanFinished)), seen.IndexOf(nameof(ScanStarted)));
		Assert.AreEqual(0, JsonDocument.Parse(finishedData).RootElement.GetProperty("exitCode").GetInt32());
	}

	[TestMethod]
	public async Task Event_subscription_is_released_on_disconnect()
	{
		using var dir = new TempDir();
		var events = new EventBroadcaster();
		var scheduler = new CycleScheduler(
			new DaemonOptions(TimeSpan.FromMinutes(1), false, "http://127.0.0.1:0", dir.Path),
			new DaemonJobs(_ => Task.FromResult(0), _ => Task.FromResult(0), _ => Task.FromResult(0)),
			new CycleState(), events: events);

		using (events.Subscribe())
			Assert.AreEqual(1, events.SubscriberCount);
		Assert.AreEqual(0, events.SubscriberCount);
		Assert.IsFalse(scheduler.TryRequestRun(RunKind.Scan));
	}

	[TestMethod]
	public void Broadcaster_drops_oldest_for_a_slow_subscriber()
	{
		var events = new EventBroadcaster(capacity: 2);
		using var sub = events.Subscribe();

		events.Publish(new ScanFinished(1));
		events.Publish(new ScanFinished(2));
		events.Publish(new ScanFinished(3));

		Assert.IsTrue(sub.Reader.TryRead(out var a));
		Assert.IsTrue(sub.Reader.TryRead(out var b));
		Assert.AreEqual(2, ((ScanFinished)a!).ExitCode);
		Assert.AreEqual(3, ((ScanFinished)b!).ExitCode);
		Assert.IsFalse(sub.Reader.TryRead(out _));
	}

	[TestMethod]
	public async Task Accounts_endpoint_exposes_no_token_or_identity_fields()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var doc = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/accounts"));
		var names = doc.RootElement[0].EnumerateObject().Select(p => p.Name).Order().ToArray();
		CollectionAssert.AreEqual(new[] { "accountId", "accountName", "libraryScan", "locale" }, names);
	}

	[TestMethod]
	public void Account_mapping_never_carries_secrets()
	{
		var account = new AudibleUtilities.Account("a@example.com") { AccountName = "Main", DecryptKey = "deadbeef" };

		var json = JsonSerializer.Serialize(DbEngineData.ToDto(account), new JsonSerializerOptions(JsonSerializerDefaults.Web));

		Assert.DoesNotContain("deadbeef", json);
		Assert.Contains("a@example.com", json);
	}

	[TestMethod]
	public async Task Library_endpoints_clamp_paging_and_404_unknown_asin()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		await h.Http.GetStringAsync(h.Base + "/api/library?offset=-5&limit=99999");
		Assert.AreEqual((0, 1000), h.Data.LastPage);
		await h.Http.GetStringAsync(h.Base + "/api/library");
		Assert.AreEqual((0, 100), h.Data.LastPage);

		Assert.AreEqual(HttpStatusCode.OK, (await h.Http.GetAsync(h.Base + "/api/library/B000000001")).StatusCode);
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.GetAsync(h.Base + "/api/library/nope")).StatusCode);
	}

	[TestMethod]
	[DataRow(null, null, 0, 100)]
	[DataRow(-1, 0, 0, 1)]
	[DataRow(40, 5000, 40, 1000)]
	[DataRow(10, 25, 10, 25)]
	public void Paging_clamps(int? offset, int? limit, int expectedOffset, int expectedLimit)
		=> Assert.AreEqual((expectedOffset, expectedLimit), LibraryPaging.Clamp(offset, limit));

	[TestMethod]
	[DataRow("http://127.0.0.1:8080", true)]
	[DataRow("http://localhost:8080", true)]
	[DataRow("http://[::1]:8080", true)]
	[DataRow("http://0.0.0.0:8080", false)]
	[DataRow("http://*:8080", false)]
	[DataRow("http://192.168.1.5:8080", false)]
	public void Loopback_detection(string url, bool loopback)
		=> Assert.AreEqual(loopback, ListenUrl.IsLoopback(url));

	[TestMethod]
	public async Task Queue_endpoints_follow_the_api_gate_and_the_token()
	{
		await using (var off = new Harness(api: false))
		{
			await off.WaitIdleAfterFirstCycle();
			Assert.AreEqual(HttpStatusCode.NotFound, (await off.Http.GetAsync(off.Base + "/api/queue")).StatusCode);
			Assert.AreEqual(HttpStatusCode.NotFound, (await off.Decide(1, "{\"decision\":\"continue\"}")).StatusCode);
		}

		using var dir = new TempDir();
		var tokenFile = dir.File("token");
		File.WriteAllText(tokenFile, "s3cret\n");
		await using var h = new Harness(api: true, tokenFile);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.Unauthorized, (await h.Http.GetAsync(h.Base + "/api/queue")).StatusCode);
		Assert.AreEqual(HttpStatusCode.Unauthorized, (await h.Decide(1, "{\"decision\":\"continue\"}")).StatusCode);
	}

	[TestMethod]
	public async Task Queue_is_empty_when_idle()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var doc = await h.QueueJson();
		Assert.AreEqual("{\"running\":false,\"jobs\":[],\"pending\":[],\"waiting\":null}", doc.RootElement.GetRawText());
	}

	[TestMethod]
	public async Task Queue_lists_working_and_queued_jobs_with_their_states()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		var release = new TaskCompletionSource();
		h.Outcome = async _ => { await release.Task; return ProcessBookResult.Success; };
		var a = h.Job("B000000001");
		var b = h.Job("B000000002");

		h.Enqueue(a, b);
		await Eventually(async () => (await h.QueueJson()).RootElement.GetProperty("jobs").EnumerateArray().Any(j => j.GetProperty("state").GetString() == "working"));

		using var doc = await h.QueueJson();
		var jobs = doc.RootElement.GetProperty("jobs");
		Assert.IsTrue(doc.RootElement.GetProperty("running").GetBoolean());
		Assert.AreEqual(a.Id.Value, jobs[0].GetProperty("id").GetString());
		Assert.AreEqual("B000000001", jobs[0].GetProperty("asin").GetString());
		Assert.AreEqual("Title B000000001", jobs[0].GetProperty("title").GetString());
		CollectionAssert.AreEqual(new[] { "working", "queued" }, jobs.EnumerateArray().Select(j => j.GetProperty("state").GetString()).ToArray());

		release.SetResult();
		await h.Queue.Runner!;
	}

	[TestMethod]
	public async Task A_bad_book_pause_is_listed_and_answered_over_the_api()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Outcome = _ => Task.FromResult(ProcessBookResult.AwaitingDecision);
		var job = h.Job("B000000001");

		h.Enqueue(job);
		await Eventually(async () => (await h.QueueJson()).RootElement.GetProperty("pending").GetArrayLength() == 1);

		long id;
		using (var doc = await h.QueueJson())
		{
			var pause = doc.RootElement.GetProperty("pending")[0];
			id = pause.GetProperty("id").GetInt64();
			Assert.AreEqual("badBook", pause.GetProperty("reason").GetString());
			Assert.AreEqual(job.Id.Value, pause.GetProperty("jobId").GetString());
			CollectionAssert.AreEqual(new[] { "badBook" }, pause.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()).ToArray());
		}

		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Decide(id, "{\"decision\":\"explode\"}")).StatusCode);
		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Decide(id, "{\"decision\":\"badBook\"}")).StatusCode);
		Assert.AreEqual(HttpStatusCode.Conflict, (await h.Decide(id, "{\"decision\":\"continue\"}")).StatusCode);
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Decide(id + 100, "{\"decision\":\"badBook\",\"action\":\"retry\"}")).StatusCode);
		Assert.AreEqual(1, h.Queue.Pending.Count, "A refused answer must leave the pause in place.");

		var ok = await h.Decide(id, "{\"decision\":\"badBook\",\"action\":\"retry\",\"applyToAll\":true}");
		Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
		await h.Queue.Runner!.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.AreEqual(ProcessBookResult.FailedRetry, job.Result);

		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Decide(id, "{\"decision\":\"badBook\",\"action\":\"retry\"}")).StatusCode, "A stale id must be refused.");
	}

	[TestMethod]
	public async Task A_disk_full_wait_can_be_resumed_over_the_api_but_the_daily_limit_wait_cannot()
	{
		await using var h = new Harness(api: true, onDiskFull: DiskFullPolicy.WaitForSpace);
		await h.WaitIdleAfterFirstCycle();
		var calls = 0;
		h.Outcome = _ => Task.FromResult(Interlocked.Increment(ref calls) == 1 ? ProcessBookResult.DiskFull : ProcessBookResult.Success);

		h.Enqueue(h.Job("B000000001"));
		await Eventually(async () => (await h.QueueJson()).RootElement.GetProperty("waiting").ValueKind == JsonValueKind.Object);

		long id;
		using (var doc = await h.QueueJson())
		{
			var wait = doc.RootElement.GetProperty("waiting");
			id = wait.GetProperty("id").GetInt64();
			Assert.AreEqual("diskFull", wait.GetProperty("reason").GetString());
			CollectionAssert.AreEqual(new[] { "resume", "stop" }, wait.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()).ToArray());
		}

		Assert.AreEqual(HttpStatusCode.Conflict, (await h.Decide(id, "{\"decision\":\"continue\"}")).StatusCode);
		Assert.AreEqual(HttpStatusCode.OK, (await h.Decide(id, "{\"decision\":\"resume\"}")).StatusCode);
		await h.Queue.Runner!.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.AreEqual(2, calls);
	}

	[TestMethod]
	public async Task Events_stream_delivers_book_and_queue_events()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var request = new HttpRequestMessage(HttpMethod.Get, h.Base + "/api/events");
		using var response = await h.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
		using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

		h.Outcome = _ => Task.FromResult(ProcessBookResult.AwaitingDecision);
		var job = h.Job("B000000001");
		h.Enqueue(job);

		var data = new Dictionary<string, string>();
		string? current = null;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (!data.ContainsKey(nameof(QueuePaused)))
		{
			var line = await reader.ReadLineAsync(timeout.Token);
			Assert.IsNotNull(line);
			if (line.StartsWith("event: "))
				current = line[7..];
			else if (line.StartsWith("data: ") && current is not null)
				data[current] = line[6..];
		}

		Assert.AreEqual(job.Id.Value, JsonDocument.Parse(data[nameof(BookStarted)]).RootElement.GetProperty("jobId").GetString());
		Assert.AreEqual("B000000001", JsonDocument.Parse(data[nameof(BookStarted)]).RootElement.GetProperty("asin").GetString());
		var pause = JsonDocument.Parse(data[nameof(QueuePaused)]).RootElement.GetProperty("pause");
		Assert.AreEqual("badBook", pause.GetProperty("reason").GetString());

		Assert.IsTrue(h.Queue.Resume(h.Queue.Pending[0].Id, Decision.BadBook(Configuration.BadBookAction.Retry)));
		while (!data.ContainsKey(nameof(BookFinished)))
		{
			var line = await reader.ReadLineAsync(timeout.Token);
			Assert.IsNotNull(line);
			if (line.StartsWith("event: "))
				current = line[7..];
			else if (line.StartsWith("data: ") && current is not null)
				data[current] = line[6..];
		}

		using var finished = JsonDocument.Parse(data[nameof(BookFinished)]);
		Assert.AreEqual("failedRetry", finished.RootElement.GetProperty("result").GetString());
		Assert.AreEqual("failed", finished.RootElement.GetProperty("status").GetString());
		Assert.IsTrue(data.ContainsKey(nameof(QueueResumed)));
		Assert.IsFalse(data.Values.Any(v => v.Contains("token", StringComparison.OrdinalIgnoreCase)));
	}
}
