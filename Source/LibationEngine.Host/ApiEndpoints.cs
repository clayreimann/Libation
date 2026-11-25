using System.Text.Json;
using AppScaffolding;
using DataLayer;
using LibationEngine.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LibationEngine.Host;

public static class ApiEndpoints
{
	public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	public static void AddServices(IServiceCollection services)
		=> services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
		{
			document.Info = new() { Title = "Libation daemon API", Version = LibationScaffolding.BuildVersion?.ToString() ?? "0" };
			return Task.CompletedTask;
		}));

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

	public static void Map(WebApplication app, DaemonOptions options, CycleState state, CycleScheduler scheduler, IEngineData data, LiberationQueue queue, Func<string, LiberationJob?>? createBookJob = null)
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

		app.MapOpenApi("/api/openapi.json");

		api.MapGet("/status", Status)
			.WithSummary("Daemon state, last results, next run, interval and version.")
			.WithName("getStatus").WithTags("daemon")
			.Produces<StatusDto>(StatusCodes.Status200OK);

		api.MapGet("/accounts", () => data.GetAccounts())
			.WithSummary("Accounts: id, name, locale and library-scan flag. Never tokens.")
			.WithName("getAccounts").WithTags("daemon")
			.Produces<IReadOnlyList<AccountDto>>(StatusCodes.Status200OK);

		api.MapGet("/library", (int? offset, int? limit, string? q, bool? trash) =>
		{
			var (o, l) = LibraryPaging.Clamp(offset, limit);
			try
			{
				return Results.Json(data.GetLibrary(o, l, q, trash ?? false));
			}
			catch (SearchQueryException ex)
			{
				return Error(ex.Message, StatusCodes.Status400BadRequest);
			}
		}).WithSummary("Books, newest first. offset (default 0), limit (default 100, max 1000), q (Lucene filter syntax), trash=true for the trash. total counts all matches.")
			.WithName("getLibrary").WithTags("library")
			.Produces<LibraryPageDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest);

		api.MapGet("/library/{asin}", (string asin) => data.GetBook(asin) is { } book ? Results.Json(book) : NoSuchBook())
			.WithSummary("One book by ASIN, trashed ones included, or 404.")
			.WithName("getBook").WithTags("library")
			.Produces<BookDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound);

		api.MapPut("/library/{asin}/tags", async (string asin, TagsRequest? body) =>
			body?.Tags is null
				? Error("tags is required; an empty string clears them.", StatusCodes.Status400BadRequest)
				: await data.SetTags(asin, body.Tags) is { } book ? Results.Json(book) : NoSuchBook())
			.WithSummary("Replaces a book's tags. 200 with the book, 400 bad body, 404 unknown ASIN.")
			.WithName("setBookTags").WithTags("library")
			.Produces<BookDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound);

		api.MapPut("/library/{asin}/status", async (string asin, StatusRequest? body) =>
			!TryParseStatus(body?.Status, out var status)
				? Error("status must be liberated or notLiberated.", StatusCodes.Status400BadRequest)
				: await data.SetBookStatus(asin, status) is { } book ? Results.Json(book) : NoSuchBook())
			.WithSummary("Sets a book's liberated status to liberated or notLiberated. 200 with the book, 400 bad body, 404 unknown ASIN.")
			.WithName("setBookStatus").WithTags("library")
			.Produces<BookDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound);

		api.MapPost("/library/{asin}/trash", async (string asin) => await data.SetTrash(asin, true) is { } book ? Results.Json(book) : NoSuchBook())
			.WithSummary("Moves a book to the trash. 200 with the book, 404 unknown ASIN.")
			.WithName("trashBook").WithTags("library")
			.Produces<BookDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound);

		api.MapPost("/library/{asin}/restore", async (string asin) => await data.SetTrash(asin, false) is { } book ? Results.Json(book) : NoSuchBook())
			.WithSummary("Restores a book from the trash. 200 with the book, 404 unknown ASIN.")
			.WithName("restoreBook").WithTags("library")
			.Produces<BookDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound);

		var enqueueLock = new object();
		api.MapPost("/library/{asin}/liberate", (string asin) =>
		{
			lock (enqueueLock)
			{
				if (queue.Jobs.GetAllItems().Except(queue.Jobs.Completed).Any(j => string.Equals(j.LibraryBook.Book.AudibleProductId, asin, StringComparison.OrdinalIgnoreCase)))
					return Error("That book is already queued.", StatusCodes.Status409Conflict);
				if (createBookJob?.Invoke(asin) is not { } job)
					return NoSuchBook();

				queue.Jobs.Enqueue([job]);
				queue.StartOrWake();
				return Results.Json(QueueMapper.ToDto(job, "queued"), statusCode: StatusCodes.Status202Accepted);
			}
		}).WithSummary("Queues one book for liberation, re-downloading it if already liberated. 202 with the job, 404 unknown ASIN, 409 already queued.")
			.WithName("liberateBook").WithTags("library")
			.Produces<QueueJobDto>(StatusCodes.Status202Accepted)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound)
			.Produces<ErrorDto>(StatusCodes.Status409Conflict);

		api.MapGet("/settings", () => data.GetSettings())
			.WithSummary("Curated read-only settings: Books and working folders, naming templates, download, queue and library options. No secrets.")
			.WithName("getSettings").WithTags("settings")
			.Produces<SettingsDto>(StatusCodes.Status200OK);

		api.MapPost("/scan", () => Run(RunKind.Scan))
			.WithSummary("Runs a library scan now, without liberating. 202, or 409 if busy.")
			.WithName("scan").WithTags("daemon")
			.Produces<StatusDto>(StatusCodes.Status202Accepted)
			.Produces<ErrorDto>(StatusCodes.Status409Conflict);

		api.MapPost("/liberate", () => Run(RunKind.Cycle))
			.WithSummary("Runs a full cycle now: scan, then liberate if the scan succeeded. 202, or 409 if busy.")
			.WithName("liberate").WithTags("daemon")
			.Produces<StatusDto>(StatusCodes.Status202Accepted)
			.Produces<ErrorDto>(StatusCodes.Status409Conflict);

		api.MapGet("/queue", () => QueueMapper.Snapshot(queue))
			.WithSummary("Working and queued jobs, pending decisions and the current wait, if any.")
			.WithName("getQueue").WithTags("queue")
			.Produces<QueueDto>(StatusCodes.Status200OK);

		api.MapPost("/queue/decisions/{pauseId:long}", (long pauseId, DecisionRequest? body) => Decide(queue, new PauseId(pauseId), body))
			.WithSummary("Answers a pending decision or wait. 200 with the queue, 400 bad body, 404 unknown id, 409 decision not allowed.")
			.WithName("decide").WithTags("queue")
			.Produces<QueueDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound)
			.Produces<ErrorDto>(StatusCodes.Status409Conflict);

		api.MapGet("/events", async (HttpContext context) =>
		{
			await StreamEvents(context, scheduler.Events);
			return Results.Empty;
		})
			.WithSummary("Server-Sent Events; event name is the record type, data is its JSON.")
			.WithName("streamEvents").WithTags("daemon")
			.Produces<string>(StatusCodes.Status200OK, "text/event-stream");
	}

	private static IResult Error(string message, int status) => Results.Json(new ErrorDto(message), statusCode: status);

	private static IResult NoSuchBook() => Error("No such book.", StatusCodes.Status404NotFound);

	private static bool TryParseStatus(string? text, out LiberatedStatus status)
	{
		status = text?.Trim().ToLowerInvariant() switch
		{
			"liberated" => LiberatedStatus.Liberated,
			"notliberated" => LiberatedStatus.NotLiberated,
			_ => LiberatedStatus.Error
		};
		return status != LiberatedStatus.Error;
	}

	private static IResult Decide(LiberationQueue queue, PauseId id, DecisionRequest? body)
	{
		if (!QueueMapper.TryParse(body, out var decision, out var error))
			return Results.Json(new ErrorDto(error), statusCode: StatusCodes.Status400BadRequest);

		var allowed = queue.Pending.FirstOrDefault(p => p.Id == id)?.Allowed
			?? (queue.Waiting is { } wait && wait.Id == id ? wait.Allowed : null);
		if (allowed is null)
			return Results.Json(new ErrorDto("No such pending decision."), statusCode: StatusCodes.Status404NotFound);
		if (!allowed.Contains(decision.Kind))
			return Results.Json(new ErrorDto("That decision is not allowed here."), statusCode: StatusCodes.Status409Conflict);

		return queue.Resume(id, decision)
			? Results.Json(QueueMapper.Snapshot(queue))
			: Results.Json(new ErrorDto("No such pending decision."), statusCode: StatusCodes.Status404NotFound);
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
