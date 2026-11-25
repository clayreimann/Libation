using System.Text.Json;
using AppScaffolding;
using LibationEngine.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LibationEngine;

public static class ApiEndpoints
{
	public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	public static void RequireToken(WebApplication app, string token)
		=> app.Use(async (context, next) =>
		{
			if (context.Request.Path.StartsWithSegments("/api"))
			{
				var header = context.Request.Headers.Authorization.ToString();
				var presented = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
				if (!ApiToken.Matches(token, presented))
				{
					context.Response.Headers.WWWAuthenticate = "Bearer";
					context.Response.StatusCode = StatusCodes.Status401Unauthorized;
					return;
				}
			}
			await next();
		});

	public static void Map(WebApplication app, DaemonOptions options, CycleState state, CycleScheduler scheduler, IEngineData data)
	{
		StatusDto Status()
		{
			var s = state.Snapshot();
			return new(s.State, s.LastRun, s.LastSuccess, s.ScanExitCode, s.LiberateExitCode, s.NextRun,
				(int)options.Interval.TotalSeconds, LibationScaffolding.BuildVersion?.ToString());
		}

		IResult Run(RunKind kind)
			=> scheduler.TryRequestRun(kind)
				? Results.Json(Status(), statusCode: StatusCodes.Status202Accepted)
				: Results.Json(new ErrorDto("A job is already running or queued."), statusCode: StatusCodes.Status409Conflict);

		var api = app.MapGroup("/api");

		api.MapGet("/status", Status)
			.WithSummary("Daemon state, last results, next run, interval and version.");

		api.MapGet("/accounts", () => data.GetAccounts())
			.WithSummary("Accounts: id, name, locale and library-scan flag. Never tokens.");

		api.MapGet("/library", (int? offset, int? limit) =>
		{
			var (o, l) = LibraryPaging.Clamp(offset, limit);
			return data.GetLibrary(o, l);
		}).WithSummary("Books, newest first. offset (default 0), limit (default 100, max 1000).");

		api.MapGet("/library/{asin}", (string asin) => data.GetBook(asin) is { } book ? Results.Json(book) : Results.NotFound(new ErrorDto("No such book.")))
			.WithSummary("One book by ASIN, or 404.");

		api.MapPost("/scan", () => Run(RunKind.Scan))
			.WithSummary("Runs a library scan now, without liberating. 202, or 409 if busy.");

		api.MapPost("/liberate", () => Run(RunKind.Cycle))
			.WithSummary("Runs a full cycle now: scan, then liberate if the scan succeeded. 202, or 409 if busy.");

		api.MapGet("/events", (HttpContext context) => StreamEvents(context, scheduler.Events))
			.WithSummary("Server-Sent Events; event name is the record type, data is its JSON.");
	}

	private static async Task StreamEvents(HttpContext context, EventBroadcaster events)
	{
		var ct = context.RequestAborted;
		context.Response.ContentType = "text/event-stream";
		context.Response.Headers.CacheControl = "no-cache";
		context.Response.Headers["X-Accel-Buffering"] = "no";

		using var subscription = events.Subscribe();
		var body = context.Response.Body;
		try
		{
			await context.Response.WriteAsync(": connected\n\n", ct);
			await body.FlushAsync(ct);

			while (!ct.IsCancellationRequested)
			{
				using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
				beat.CancelAfter(Heartbeat);
				try
				{
					if (!await subscription.Reader.WaitToReadAsync(beat.Token))
						return;
					while (subscription.Reader.TryRead(out var evt))
						await context.Response.WriteAsync($"event: {evt.GetType().Name}\ndata: {JsonSerializer.Serialize(evt, evt.GetType(), Json)}\n\n", ct);
				}
				catch (OperationCanceledException) when (!ct.IsCancellationRequested)
				{
					await context.Response.WriteAsync(": keepalive\n\n", ct);
				}
				await body.FlushAsync(ct);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}
}
