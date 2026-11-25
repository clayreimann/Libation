using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace LibationEngine;

/// <summary>Kestrel host for <c>/health</c> (and, when enabled, the API).</summary>
public static class DaemonHost
{
	public static WebApplication Build(string listenUrl, CycleState state)
	{
		var builder = WebApplication.CreateSlimBuilder();
		builder.WebHost.UseUrls(ListenUrl.Normalize(listenUrl));
		builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);
		builder.Logging.ClearProviders();
		builder.Logging.AddSerilog(Log.Logger, dispose: false);
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);

		var app = builder.Build();
		app.MapMethods("/health", ["GET", "HEAD"], () => Results.Json(state.Snapshot()));
		return app;
	}
}
