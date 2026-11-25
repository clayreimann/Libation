using Serilog;

namespace LibationEngine;

public sealed class CycleScheduler
{
	public const int ReconcileFailedExitCode = 3;

	private readonly DaemonOptions options;
	private readonly DaemonJobs jobs;
	private readonly CycleState state;
	private readonly TimeProvider time;
	private readonly AccountsImporter importer;
	private readonly MetricsWriter? metrics;
	private long successTs;
	private int liberateRc;

	public CycleScheduler(DaemonOptions options, DaemonJobs jobs, CycleState state, TimeProvider? time = null)
	{
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

	/// <summary>Runs until cancelled; returns the process exit code.</summary>
	public async Task<int> RunAsync(CancellationToken ct)
	{
		try
		{
			if (options.ReconcileOnStart)
			{
				state.Set(DaemonState.Reconciling);
				var rc = await RunJob("reconcile", jobs.Reconcile, ct);
				state.Set(DaemonState.Idle);
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
				state.SetNextRun(time.GetUtcNow() + options.Interval);
				await Task.Delay(options.Interval, time, ct);
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
		}
		return 0;
	}

	/// <summary>Returns the scan exit code if the scan failed, else the liberate exit code.</summary>
	public async Task<int> RunCycleAsync(CancellationToken ct)
	{
		state.SetNextRun(null);
		try
		{
			importer.Import();
		}
		catch (AccountsImportException ex)
		{
			Log.Logger.Error("{Message}; keeping the current accounts", ex.Message);
		}

		state.Set(DaemonState.Scanning);
		var scanRc = await RunJob("scan", jobs.Scan, ct);
		var now = time.GetUtcNow();
		state.RecordScan(now, scanRc);

		if (scanRc == 0)
		{
			successTs = now.ToUnixTimeSeconds();
			state.Set(DaemonState.Liberating);
			liberateRc = await RunJob("liberate", jobs.Liberate, ct);
			state.RecordLiberate(liberateRc);
		}
		else
			Log.Logger.Warning("Scan failed with exit code {ExitCode}; skipping liberate", scanRc);

		state.Set(DaemonState.Idle);
		WriteMetrics(time.GetUtcNow().ToUnixTimeSeconds(), scanRc);
		return scanRc != 0 ? scanRc : liberateRc;
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
