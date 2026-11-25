using System.Runtime.InteropServices;

namespace LibationEngine;

public static class LibationDaemon
{
	/// <summary>Runs until cancelled or SIGTERM/SIGINT; returns the process exit code.</summary>
	public static async Task<int> RunAsync(DaemonOptions options, DaemonJobs jobs, CancellationToken ct = default, TimeProvider? time = null)
	{
		try
		{
			new AccountsImporter(options.AccountsImportPath, options.LibationFilesDir).Import();
		}
		catch (AccountsImportException ex)
		{
			Console.Error.WriteLine(ex.Message);
			return CycleScheduler.ReconcileFailedExitCode;
		}

		using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cts.Cancel(); });
		using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; cts.Cancel(); });

		var state = new CycleState();
		var scheduler = new CycleScheduler(options, jobs, state, time);
		await using var host = options.Once ? null : DaemonHost.Build(options.ListenUrl, state);
		if (host is not null)
			await host.StartAsync(cts.Token);

		var rc = await scheduler.RunAsync(cts.Token);
		if (host is not null)
			await host.StopAsync(CancellationToken.None);
		return rc;
	}
}
