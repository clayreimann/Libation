using LibationEngine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace LibationEngine.Host;

/// <summary>Kestrel host for <c>/health</c> and, when enabled, <c>/api</c>.</summary>
public static class DaemonHost
{
	public static WebApplication Build(DaemonOptions options, CycleState state, CycleScheduler scheduler, LiberationQueue queue, IEngineData? data = null, string? apiToken = null, Func<string, LiberationJob?>? createBookJob = null, AccountAuthMonitor? authMonitor = null)
	{
		var builder = WebApplication.CreateSlimBuilder();
		builder.WebHost.UseUrls(ListenUrl.Normalize(options.ListenUrl));
		builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);
		builder.Logging.ClearProviders();
		builder.Logging.AddSerilog(Log.Logger, dispose: false);
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);

		if (options.ApiEnabled)
			ApiEndpoints.AddServices(builder.Services);

		var app = builder.Build();
		app.MapMethods("/health", ["GET", "HEAD"], () => Results.Json(state.Snapshot())).ExcludeFromDescription();

		if (options.ApiEnabled)
		{
			if (apiToken is not null)
				ApiEndpoints.RequireToken(app, apiToken);
			ApiEndpoints.Map(app, options, state, scheduler, data ?? new DbEngineData(), queue, createBookJob, authMonitor);
		}
		return app;
	}
}
