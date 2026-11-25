using LibationEngine.Contracts;
using Serilog;

namespace LibationEngine;

public enum RunKind { Scan, Cycle }

public sealed class CycleScheduler
{
	public const int ReconcileFailedExitCode = 3;

	private readonly DaemonOptions options;
	private readonly DaemonJobs jobs;
	private readonly CycleState state;
	private readonly TimeProvider time;
	private readonly AccountsImporter importer;
	private readonly MetricsWriter? metrics;
	private readonly object runGate = new();
	private TaskCompletionSource? wake;
	private RunKind? requested;
	private ScanSelection? requestedAccounts;
	private long successTs;
	private int liberateRc;

	public CycleScheduler(DaemonOptions options, DaemonJobs jobs, CycleState state, TimeProvider? time = null, EventBroadcaster? events = null)
	{
		Events = events ?? new EventBroadcaster();
		this.options = options;
		this.jobs = jobs;
		this.state = state;
		this.time = time ?? TimeProvider.System;
		importer = new AccountsImporter(options.AccountsImportPath, options.LibationFilesDir);
		metrics = options.MetricsFile is null ? null : new MetricsWriter(options.MetricsFile);

		if (metrics is not null)
			(successTs, liberateRc) = metrics.ReadPrevious();
		state.Seed(successTs > 0 ? DateTimeOffset.FromUnixTimeSeconds(successTs) : null, liberateRc);
	}

	public EventBroadcaster Events { get; }

	/// <summary>Raised after a scan that exits 0.</summary>
	public Action<ScanSelection?>? OnScanSucceeded { get; set; }

	/// <summary>Queues an immediate run; false if a job is running or one is already queued. <paramref name="selection"/> narrows a scan.</summary>
	public bool TryRequestRun(RunKind kind, ScanSelection? selection = null)
	{
		lock (runGate)
		{
			if (wake is null || requested is not null)
				return false;
			requested = kind;
			requestedAccounts = kind == RunKind.Scan ? selection : null;
			wake.TrySetResult();
			return true;
		}
	}

	/// <summary>Runs until cancelled; returns the process exit code.</summary>
	public async Task<int> RunAsync(CancellationToken ct)
	{
		try
		{
			if (options.ReconcileOnStart)
			{
				SetState(DaemonState.Reconciling);
				var rc = await RunJob("reconcile", jobs.Reconcile, ct);
				SetState(DaemonState.Idle);
				if (rc != 0)
				{
					Log.Logger.Error("Reconcile failed with exit code {ExitCode}; stopping", rc);
					return ReconcileFailedExitCode;
				}
			}

			if (options.Once)
				return await RunCycleAsync(ct);

			while (!ct.IsCancellationRequested)
			{
				await RunCycleAsync(ct);
				var next = time.GetUtcNow() + options.Interval;
				while (!ct.IsCancellationRequested)
				{
					SetNextRun(next);
					var kind = await WaitAsync(next, ct);
					if (kind is null)
						break;
					state.SetNextRun(null);
					if (kind == RunKind.Scan)
						await RunScanOnlyAsync(ct, TakeRequestedAccounts());
					else
					{
						await RunCycleAsync(ct);
						next = time.GetUtcNow() + options.Interval;
					}
				}
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
		}
		return 0;
	}

	private async Task<RunKind?> WaitAsync(DateTimeOffset until, CancellationToken ct)
	{
		TaskCompletionSource signal;
		lock (runGate)
		{
			signal = wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		try
		{
			var delay = Task.Delay(Max(until - time.GetUtcNow(), TimeSpan.Zero), time, delayCts.Token);
			await Task.WhenAny(delay, signal.Task);
		}
		finally
		{
			delayCts.Cancel();
			lock (runGate)
				wake = null;
		}

		lock (runGate)
		{
			var kind = requested;
			requested = null;
			return kind;
		}
	}

	private ScanSelection? TakeRequestedAccounts()
	{
		lock (runGate)
		{
			var accounts = requestedAccounts;
			requestedAccounts = null;
			return accounts;
		}
	}

	private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

	private void SetNextRun(DateTimeOffset? value)
	{
		state.SetNextRun(value);
		Events.Publish(new NextRunScheduled(value));
	}

	private void SetState(DaemonState value)
	{
		state.Set(value);
		Events.Publish(new StateChanged(value.ToString().ToLowerInvariant()));
	}

	private void ImportAccounts()
	{
		try
		{
			importer.Import();
		}
		catch (AccountsImportException ex)
		{
			Log.Logger.Error("{Message}; keeping the current accounts", ex.Message);
		}
	}

	private async Task<int> ScanAsync(CancellationToken ct, ScanSelection? selection = null)
	{
		SetState(DaemonState.Scanning);
		Events.Publish(new ScanStarted());
		var scanRc = selection is not null && jobs.ScanAccounts is { } subset
			? await RunJob("scan", token => subset(selection, token), ct)
			: await RunJob("scan", jobs.Scan, ct);
		var now = time.GetUtcNow();
		state.RecordScan(now, scanRc);
		if (scanRc == 0)
		{
			successTs = now.ToUnixTimeSeconds();
			OnScanSucceeded?.Invoke(jobs.ScanAccounts is null ? null : selection);
		}
		Events.Publish(new ScanFinished(scanRc));
		return scanRc;
	}

	private async Task RunScanOnlyAsync(CancellationToken ct, ScanSelection? selection)
	{
		ImportAccounts();
		var scanRc = await ScanAsync(ct, selection);
		SetState(DaemonState.Idle);
		WriteMetrics(time.GetUtcNow().ToUnixTimeSeconds(), scanRc);
	}

	/// <summary>Returns the scan exit code if the scan failed, else the liberate exit code.</summary>
	public async Task<int> RunCycleAsync(CancellationToken ct)
	{
		SetNextRun(null);
		ImportAccounts();

		var scanRc = await ScanAsync(ct);
		if (scanRc == 0)
		{
			SetState(DaemonState.Liberating);
			Events.Publish(new LiberateStarted());
			liberateRc = await RunJob("liberate", jobs.Liberate, ct);
			state.RecordLiberate(liberateRc);
			Events.Publish(new LiberateFinished(liberateRc));
		}
		else
			Log.Logger.Warning("Scan failed with exit code {ExitCode}; skipping liberate", scanRc);

		SetState(DaemonState.Idle);
		WriteMetrics(time.GetUtcNow().ToUnixTimeSeconds(), scanRc);
		var rc = scanRc != 0 ? scanRc : liberateRc;
		Events.Publish(new CycleFinished(rc));
		return rc;
	}

	private void WriteMetrics(long runTs, int scanRc)
	{
		if (metrics is null)
			return;
		try
		{
			metrics.Write(new(runTs, successTs, scanRc, liberateRc));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Log.Logger.Warning(ex, "Could not write metrics file");
		}
	}

	private static async Task<int> RunJob(string name, Func<CancellationToken, Task<int>> job, CancellationToken ct)
	{
		try
		{
			return await job(ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Log.Logger.Error(ex, "{Job} threw", name);
			return 1;
		}
	}
}
