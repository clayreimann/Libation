using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace LibationEngine.Tests;

[TestClass]
public class CycleSchedulerTests
{
	private sealed class Harness(TempDir dir, bool reconcile = false, TimeSpan? interval = null)
	{
		public int ScanCalls, LiberateCalls, ReconcileCalls;
		public int ScanRc, LiberateRc, ReconcileRc;
		public string Metrics { get; } = dir.File("m.prom");
		public CycleState State { get; } = new();

		public bool Once;

		public CycleScheduler Create() => new(
			new DaemonOptions(interval ?? TimeSpan.FromMilliseconds(10), reconcile, "http://127.0.0.1:0", dir.Path, Metrics, Once: Once),
			new DaemonJobs(
				_ => { Interlocked.Increment(ref ScanCalls); return Task.FromResult(ScanRc); },
				_ => { Interlocked.Increment(ref LiberateCalls); return Task.FromResult(LiberateRc); },
				_ => { Interlocked.Increment(ref ReconcileCalls); return Task.FromResult(ReconcileRc); }),
			State);
	}

	private static string Read(string path, string metric)
		=> File.ReadAllLines(path).Single(l => l.StartsWith(metric + " ")).Split(' ')[1];

	private static int FreePort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	[TestMethod]
	public async Task Scan_ok_runs_liberate_and_records_success()
	{
		using var dir = new TempDir();
		var h = new Harness(dir);
		await h.Create().RunCycleAsync(CancellationToken.None);

		Assert.AreEqual(1, h.LiberateCalls);
		Assert.AreNotEqual("0", Read(h.Metrics, "libation_scan_last_success_timestamp_seconds"));
		Assert.AreEqual("0", Read(h.Metrics, "libation_scan_last_exit_code"));
		var snap = h.State.Snapshot();
		Assert.AreEqual("idle", snap.State);
		Assert.IsNotNull(snap.LastSuccess);
	}

	[TestMethod]
	public async Task Scan_failure_skips_liberate_and_keeps_previous_liberate_code()
	{
		using var dir = new TempDir();
		var h = new Harness(dir) { LiberateRc = 5 };
		var scheduler = h.Create();
		await scheduler.RunCycleAsync(CancellationToken.None);
		var success = Read(h.Metrics, "libation_scan_last_success_timestamp_seconds");

		h.ScanRc = 3;
		h.LiberateRc = 0;
		await scheduler.RunCycleAsync(CancellationToken.None);

		Assert.AreEqual(1, h.LiberateCalls);
		Assert.AreEqual("3", Read(h.Metrics, "libation_scan_last_exit_code"));
		Assert.AreEqual("5", Read(h.Metrics, "libation_liberate_last_exit_code"));
		Assert.AreEqual(success, Read(h.Metrics, "libation_scan_last_success_timestamp_seconds"));
	}

	[TestMethod]
	public async Task Startup_seeds_from_existing_metrics_file()
	{
		using var dir = new TempDir();
		var h = new Harness(dir) { ScanRc = 3 };
		new MetricsWriter(h.Metrics).Write(new(10, 7, 0, 4));

		await h.Create().RunCycleAsync(CancellationToken.None);

		Assert.AreEqual("7", Read(h.Metrics, "libation_scan_last_success_timestamp_seconds"));
		Assert.AreEqual("4", Read(h.Metrics, "libation_liberate_last_exit_code"));
	}

	[TestMethod]
	public async Task A_throwing_job_counts_as_failure()
	{
		using var dir = new TempDir();
		var scheduler = new CycleScheduler(
			new DaemonOptions(TimeSpan.FromSeconds(1), false, "http://127.0.0.1:0", dir.Path, dir.File("m.prom")),
			new DaemonJobs(_ => throw new InvalidOperationException("x"), _ => Task.FromResult(0), _ => Task.FromResult(0)),
			new CycleState());

		await scheduler.RunCycleAsync(CancellationToken.None);

		Assert.AreEqual("1", Read(dir.File("m.prom"), "libation_scan_last_exit_code"));
	}

	[TestMethod]
	public async Task Reconcile_failure_returns_3_without_running_a_cycle()
	{
		using var dir = new TempDir();
		var h = new Harness(dir, reconcile: true) { ReconcileRc = 1 };

		var rc = await h.Create().RunAsync(CancellationToken.None);

		Assert.AreEqual(3, rc);
		Assert.AreEqual(0, h.ScanCalls);
		Assert.IsFalse(File.Exists(h.Metrics));
	}

	[TestMethod]
	public async Task Reconcile_success_is_followed_by_repeating_cycles()
	{
		using var dir = new TempDir();
		var h = new Harness(dir, reconcile: true);
		using var cts = new CancellationTokenSource();
		var run = h.Create().RunAsync(cts.Token);

		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (Volatile.Read(ref h.ScanCalls) < 2 && DateTime.UtcNow < deadline)
			await Task.Delay(10);
		cts.Cancel();

		Assert.AreEqual(0, await run);
		Assert.AreEqual(1, h.ReconcileCalls);
		Assert.IsGreaterThanOrEqualTo(2, h.ScanCalls);
	}

	[TestMethod]
	public async Task Once_runs_a_single_cycle_and_returns_the_liberate_code()
	{
		using var dir = new TempDir();
		var h = new Harness(dir) { Once = true, LiberateRc = 4 };

		Assert.AreEqual(4, await h.Create().RunAsync(CancellationToken.None));
		Assert.AreEqual(1, h.ScanCalls);
	}

	[TestMethod]
	public async Task Once_returns_the_scan_code_when_the_scan_failed()
	{
		using var dir = new TempDir();
		var h = new Harness(dir) { Once = true, ScanRc = 3, LiberateRc = 4 };

		Assert.AreEqual(3, await h.Create().RunAsync(CancellationToken.None));
		Assert.AreEqual(0, h.LiberateCalls);
	}

	[TestMethod]
	public async Task Once_still_reconciles_first_and_stops_on_failure()
	{
		using var dir = new TempDir();
		var h = new Harness(dir, reconcile: true) { Once = true, ReconcileRc = 1 };

		Assert.AreEqual(3, await h.Create().RunAsync(CancellationToken.None));
		Assert.AreEqual(0, h.ScanCalls);
	}

	[TestMethod]
	public async Task Cancel_reaches_the_running_job_and_exits_cleanly()
	{
		using var dir = new TempDir();
		var started = new TaskCompletionSource();
		var cancelled = new TaskCompletionSource();
		var scheduler = new CycleScheduler(
			new DaemonOptions(TimeSpan.FromSeconds(1), false, "http://127.0.0.1:0", dir.Path),
			new DaemonJobs(async ct =>
			{
				started.SetResult();
				try { await Task.Delay(Timeout.Infinite, ct); }
				catch (OperationCanceledException) { cancelled.SetResult(); throw; }
				return 0;
			}, _ => Task.FromResult(0), _ => Task.FromResult(0)),
			new CycleState());
		using var cts = new CancellationTokenSource();

		var run = scheduler.RunAsync(cts.Token);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
		cts.Cancel();

		Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
		await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
	}

	[TestMethod]
	public async Task Daemon_serves_health_json_and_404s_other_paths()
	{
		using var dir = new TempDir();
		var port = FreePort();
		var jobs = new DaemonJobs(_ => Task.FromResult(3), _ => Task.FromResult(0), _ => Task.FromResult(0));
		using var cts = new CancellationTokenSource();
		var run = LibationDaemon.RunAsync(
			new DaemonOptions(TimeSpan.FromSeconds(30), false, $"http://127.0.0.1:{port}", dir.Path), jobs, cts.Token);

		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
		HttpResponseMessage? response = null;
		JsonElement root = default;
		for (var i = 0; i < 100; i++)
		{
			try
			{
				response = await http.GetAsync($"http://127.0.0.1:{port}/health");
				root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
				if (root.GetProperty("scanExitCode").ValueKind == JsonValueKind.Number)
					break;
			}
			catch (HttpRequestException) { }
			await Task.Delay(50);
		}

		Assert.AreEqual(HttpStatusCode.OK, response!.StatusCode);
		Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
		Assert.AreEqual(3, root.GetProperty("scanExitCode").GetInt32());
		Assert.AreEqual("idle", root.GetProperty("state").GetString());
		Assert.AreEqual(HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{port}/other")).StatusCode);

		cts.Cancel();
		Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
	}

	[TestMethod]
	[DataRow("http://127.0.0.1:8080", "http://127.0.0.1:8080")]
	[DataRow("http://127.0.0.1:8080/", "http://127.0.0.1:8080")]
	[DataRow("http://0.0.0.0:8080", "http://*:8080")]
	[DataRow("http://*:9000", "http://*:9000")]
	[DataRow("http://localhost:1", "http://localhost:1")]
	[DataRow("http://+:9000", "http://*:9000")]
	public void Listen_url_is_normalized_for_kestrel(string url, string expected)
		=> Assert.AreEqual(expected, ListenUrl.Normalize(url));

	[TestMethod]
	public void Health_snapshot_serializes_as_camel_case_json()
	{
		var state = new CycleState();
		state.Set(DaemonState.Scanning);
		state.RecordScan(DateTimeOffset.UnixEpoch, 0);
		state.RecordLiberate(2);

		var json = JsonSerializer.Serialize(state.Snapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		Assert.AreEqual("scanning", root.GetProperty("state").GetString());
		Assert.AreEqual(0, root.GetProperty("scanExitCode").GetInt32());
		Assert.AreEqual(2, root.GetProperty("liberateExitCode").GetInt32());
		Assert.IsNotNull(root.GetProperty("lastSuccess").GetString());
		Assert.AreEqual(JsonValueKind.Null, root.GetProperty("nextRun").ValueKind);
	}
}
