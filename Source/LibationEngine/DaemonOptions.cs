namespace LibationEngine;

public sealed record DaemonOptions(
	TimeSpan Interval,
	bool ReconcileOnStart,
	string ListenUrl,
	string LibationFilesDir,
	string? MetricsFile = null,
	string? AccountsImportPath = null);

/// <summary>Each delegate returns a process-style exit code; 0 is success.</summary>
public sealed record DaemonJobs(
	Func<CancellationToken, Task<int>> Scan,
	Func<CancellationToken, Task<int>> Liberate,
	Func<CancellationToken, Task<int>> Reconcile);
