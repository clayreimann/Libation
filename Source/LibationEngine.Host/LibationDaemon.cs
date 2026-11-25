using LibationEngine;
using System.Runtime.InteropServices;
using Serilog;

namespace LibationEngine.Host;

public static class LibationDaemon
{
	/// <summary>Runs until cancelled or SIGTERM/SIGINT; returns the process exit code.</summary>
	public static async Task<int> RunAsync(DaemonOptions options, DaemonJobs jobs, CancellationToken ct = default, TimeProvider? time = null, IEngineData? data = null, LiberationQueue? queue = null)
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

		string? apiToken = null;
		if (options.ApiEnabled)
		{
			try
			{
				if (options.ApiTokenFile is not null)
					apiToken = ApiToken.Load(options.ApiTokenFile);
			}
			catch (DaemonConfigException ex)
			{
				Console.Error.WriteLine(ex.Message);
				return 1;
			}

			if (!options.Once)
			{
				if (apiToken is null)
					Log.Logger.Warning("The API is enabled without a token: anyone who can reach {ListenUrl} can control the daemon", options.ListenUrl);
				if (apiToken is null && !ListenUrl.IsLoopback(options.ListenUrl))
					Log.Logger.Warning("The unauthenticated API is bound to a non-loopback address ({ListenUrl}); put it behind a proxy or set --api-token-file", options.ListenUrl);
			}
		}

		ApplicationServices.SearchEngineCommands.DeferFullReIndex = options.ApiEnabled && !options.Once;

		using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cts.Cancel(); });
		using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; cts.Cancel(); });

		queue ??= new LiberationQueue(new LiberationPolicy { MaxConcurrentDownloads = () => 1, OnDiskFull = DiskFullPolicy.WaitForSpace });
		using var stopQueue = cts.Token.Register(() => _ = queue.CancelAllAsync());

		var state = new CycleState();
		var scheduler = new CycleScheduler(options, jobs, state, time);
		_ = new QueueEventPublisher(queue, scheduler.Events, time);
		await using var host = options.Once ? null : DaemonHost.Build(options, state, scheduler, queue, data, apiToken, jobs.CreateBookJob);
		if (host is not null)
			await host.StartAsync(cts.Token);

		var rc = await scheduler.RunAsync(cts.Token);
		if (host is not null)
			await host.StopAsync(CancellationToken.None);
		return rc;
	}
}
