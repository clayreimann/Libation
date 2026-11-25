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
	public const int MaxBulk = 5000;

	public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	public static void AddServices(IServiceCollection services)
		=> services.AddOpenApi(options =>
		{
			options.AddDocumentTransformer(async (document, context, ct) =>
			{
				document.Info = new() { Title = "Libation daemon API", Version = LibationScaffolding.BuildVersion?.ToString() ?? "0" };

				var schemas = document.Components ??= new();
				schemas.Schemas ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSchema>();
				var refs = new List<Microsoft.OpenApi.IOpenApiSchema>();
				foreach (var type in EventTypes)
				{
					var schema = await context.GetOrCreateSchemaAsync(type, null, ct);
					schemas.Schemas[type.Name] = schema;
					foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
						if (property.PropertyType.Assembly == typeof(StatusDto).Assembly && property.PropertyType.IsClass && !property.PropertyType.IsSubclassOf(typeof(EngineEvent))
							&& schema is Microsoft.OpenApi.OpenApiSchema { Properties: { } members }
							&& members.ContainsKey(JsonNamingPolicy.CamelCase.ConvertName(property.Name)))
						{
							schemas.Schemas.TryAdd(property.PropertyType.Name, await context.GetOrCreateSchemaAsync(property.PropertyType, null, ct));
							members[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = new Microsoft.OpenApi.OpenApiSchemaReference(property.PropertyType.Name, document);
						}
					refs.Add(new Microsoft.OpenApi.OpenApiSchemaReference(type.Name, document));
				}
				schemas.Schemas[nameof(EngineEvent)] = new Microsoft.OpenApi.OpenApiSchema
				{
					Description = "One server-sent event on /api/events; the SSE event name is the name of the matching schema.",
					OneOf = refs
				};

				var stream = document.Paths?["/api/events"].Operations?[System.Net.Http.HttpMethod.Get];
				if (stream?.Responses?["200"].Content?.TryGetValue("text/event-stream", out var media) == true)
					media.Schema = new Microsoft.OpenApi.OpenApiSchemaReference(nameof(EngineEvent), document);
			});
			options.AddOperationTransformer((operation, _, _) =>
			{
				foreach (var parameter in operation.Parameters ?? [])
					if (parameter.Name is { } name && QueryEnums.TryGetValue(name, out var values) && parameter.Schema is Microsoft.OpenApi.OpenApiSchema schema)
						schema.Enum = values.Select(v => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(v)!).ToList();
				foreach (var (status, response) in operation.Responses?.ToList() ?? [])
					foreach (var (contentType, media) in response.Content ?? new Dictionary<string, Microsoft.OpenApi.OpenApiMediaType>())
						if (contentType == "image/jpeg" || operation.OperationId == ExportOperation && status == "200")
							media.Schema = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.String, Format = "binary" };
				return Task.CompletedTask;
			});
			options.AddSchemaTransformer((schema, context, _) =>
			{
				if (context.JsonTypeInfo.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object
					&& context.JsonTypeInfo.Type.Assembly == typeof(StatusDto).Assembly
					&& schema.Properties is { Count: > 0 })
				{
					var nullability = new System.Reflection.NullabilityInfoContext();
					foreach (var property in context.JsonTypeInfo.Properties)
					{
						if (StringEnums.TryGetValue($"{context.JsonTypeInfo.Type.Name}.{property.Name}", out var enumValues)
							&& schema.Properties.TryGetValue(property.Name, out var member)
							&& member is Microsoft.OpenApi.OpenApiSchema text)
						{
							var nullable = text.Type is { } type && type.HasFlag(Microsoft.OpenApi.JsonSchemaType.Null);
							text.Enum = enumValues.Select(v => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(v))
								.Concat(nullable ? [null] : []).ToList()!;
						}
						if (property.AttributeProvider is System.Reflection.PropertyInfo info && schema.Properties.ContainsKey(property.Name))
						{
							var optional = nullability.Create(info).ReadState == System.Reflection.NullabilityState.Nullable
								|| property.AssociatedParameter?.HasDefaultValue == true;
							if (optional)
								schema.Required?.Remove(property.Name);
							else
								(schema.Required ??= new HashSet<string>()).Add(property.Name);
						}
					}
				}
				return Task.CompletedTask;
			});
		});

	public static IReadOnlyList<Type> EventTypes { get; } = typeof(EngineEvent).Assembly.GetTypes()
		.Where(t => t.IsSubclassOf(typeof(EngineEvent)) && !t.IsAbstract)
		.OrderBy(t => t.Name, StringComparer.Ordinal)
		.ToList();

	private static string[] Names<TEnum>() where TEnum : struct, Enum
		=> Enum.GetValues<TEnum>().Select(QueueMapper.Name).ToArray();

	private static readonly string[] BookStatuses = [nameof(LiberatedStatus.NotLiberated), nameof(LiberatedStatus.Liberated), nameof(LiberatedStatus.Error)];

	private static readonly Dictionary<string, string[]> StringEnums = new(StringComparer.OrdinalIgnoreCase)
	{
		["QueueJobDto.State"] = ["working", "queued"],
		["FinishedJobDto.Result"] = Names<ProcessBookResult>(),
		["FinishedJobDto.Status"] = Names<ProcessBookStatus>(),
		["BookFinished.Result"] = Names<ProcessBookResult>(),
		["BookFinished.Status"] = Names<ProcessBookStatus>(),
		["BookDto.BookStatus"] = BookStatuses,
		["BookDto.PdfStatus"] = BookStatuses,
		["BookDetailDto.BookStatus"] = BookStatuses,
		["BookDetailDto.PdfStatus"] = BookStatuses,
		["JobLogLineDto.Level"] = Names<JobLogLevel>(),
		["BookLog.Level"] = Names<JobLogLevel>(),
		["PendingDecisionDto.Reason"] = Names<PauseReason>(),
		["QueueWaitDto.Reason"] = Names<WaitReason>(),
		["BulkItemDto.Result"] = [BulkResults.Ok, BulkResults.NotFound, BulkResults.Conflict],
		["LocateScanDto.State"] = ["idle", "running", "completed", "failed"],
		["CancelJobDto.Outcome"] = ["removed", "cancelling"],
		["AccountDto.AuthState"] = [AuthStates.SignedIn, AuthStates.NeedsLogin],
		["AccountDto.Credentials"] = [CredentialKinds.Modern, CredentialKinds.Legacy, CredentialKinds.None]
	};

	private const string ExportOperation = "exportLibrary";

	private static readonly Dictionary<string, (string ContentType, string Extension)> ExportFormats = new(StringComparer.OrdinalIgnoreCase)
	{
		["csv"] = ("text/csv", "csv"),
		["xlsx"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx"),
		["json"] = ("application/json", "json")
	};

	private static readonly Dictionary<string, string[]> QueryEnums = new()
	{
		["sort"] = Enum.GetNames<LibrarySortKey>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]).ToArray(),
		["dir"] = ["asc", "desc"],
		["size"] = ["small", "large"]
	};

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

	public static void Map(WebApplication app, DaemonOptions options, CycleState state, CycleScheduler scheduler, IEngineData data, LiberationQueue queue, Func<string, LiberationJob?>? createBookJob = null, AccountAuthMonitor? authMonitor = null, LoginSessions? logins = null)
	{
		logins ??= new LoginSessions(new AudibleLoginBackend());
		IReadOnlyList<AccountDto> Accounts()
			=> data.GetAccounts()
				.Select(a => authMonitor?.IsFlagged(a.AccountId) == true ? a with { AuthState = AuthStates.NeedsLogin } : a)
				.ToList();

		IReadOnlyList<string> NeedingLogin()
		{
			try
			{
				return Accounts().Where(a => a.AuthState == AuthStates.NeedsLogin).Select(a => a.AccountId).ToList();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
			{
				return [];
			}
		}

		StatusDto Status()
		{
			var s = state.Snapshot();
			return new(s.State, s.LastRun, s.LastSuccess, s.ScanExitCode, s.LiberateExitCode, s.NextRun,
				(int)options.Interval.TotalSeconds, LibationScaffolding.BuildVersion?.ToString(), NeedingLogin());
		}

		IResult Run(RunKind kind, IReadOnlyList<string>? accounts = null)
			=> scheduler.TryRequestRun(kind, accounts)
				? Results.Json(Status(), statusCode: StatusCodes.Status202Accepted)
				: Results.Json(new ErrorDto("A job is already running or queued."), statusCode: StatusCodes.Status409Conflict);

		var api = app.MapGroup("/api");

		app.MapOpenApi("/api/openapi.json");

		api.MapGet("/status", Status)
			.WithSummary("Daemon state, last results, next run, interval and version.")
			.WithName("getStatus").WithTags("daemon")
			.Produces<StatusDto>(StatusCodes.Status200OK);

		api.MapGet("/accounts", () => Accounts())
			.WithSummary("Accounts: id, name, locale, library-scan flag, authState (signedIn or needsLogin), credentials (modern, legacy or none) and extra marketplaces. Never tokens.")
			.WithName("getAccounts").WithTags("accounts")
			.Produces<IReadOnlyList<AccountDto>>(StatusCodes.Status200OK);

		IResult? AccountsLocked()
			=> string.IsNullOrWhiteSpace(options.AccountsImportPath)
				? null
				: Error("Accounts are managed by the imported accounts file; edit it and restart, because a changed file replaces the accounts here.", StatusCodes.Status409Conflict);

		IResult EditAccount(Func<AccountResult> edit)
		{
			AccountResult result;
			try
			{
				result = edit();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return Error($"Accounts cannot be saved: {ex.Message}", StatusCodes.Status409Conflict);
			}
			return result.Outcome switch
			{
				AccountOutcome.NotFound => Error("No such account.", StatusCodes.Status404NotFound),
				AccountOutcome.Ambiguous => Error("Several accounts share that id; add ?locale= to pick one.", StatusCodes.Status409Conflict),
				_ => Results.Json(result.Account)
			};
		}

		api.MapPatch("/accounts/{id}", (string id, string? locale, AccountPatchRequest? body) =>
		{
			if (body is null || (body.AccountName is null && body.LibraryScan is null))
				return Error("Send accountName and/or libraryScan.", StatusCodes.Status400BadRequest);
			if (body.AccountName is not null && string.IsNullOrWhiteSpace(body.AccountName))
				return Error("accountName must not be blank.", StatusCodes.Status400BadRequest);
			return AccountsLocked() ?? EditAccount(() => data.UpdateAccount(id, locale, body.AccountName, body.LibraryScan));
		}).WithSummary("Renames an account and/or sets whether it is scanned. The marketplace cannot change; add the account again for another. 200 with the account, 400 bad body, 404 unknown, 409 ambiguous id (add ?locale=), accounts managed by an import file, or not writable.")
			.WithName("patchAccount").WithTags("accounts")
			.Produces<AccountDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound)
			.Produces<ErrorDto>(StatusCodes.Status409Conflict);

		api.MapDelete("/accounts/{id}", (string id, string? locale) =>
			AccountsLocked() ?? EditAccount(() => data.DeleteAccount(id, locale)))
			.WithSummary("Removes an account. Its books stay in the library, as in the app. 200 with the removed account, 404 unknown, 409 ambiguous id (add ?locale=), accounts managed by an import file, or not writable.")
			.WithName("deleteAccount").WithTags("accounts")
			.Produces<AccountDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound)
			.Produces<ErrorDto>(StatusCodes.Status409Conflict);

		const string LoginPath = "/api/accounts/login";
		app.Use(async (context, next) =>
		{
			if (context.Request.Path.StartsWithSegments(LoginPath))
			{
				if (options.ApiTokenFile is null)
				{
					context.Response.StatusCode = StatusCodes.Status403Forbidden;
					await context.Response.WriteAsJsonAsync(new ErrorDto("Signing in over the API requires an API token; start the daemon with --api-token-file."));
					return;
				}
				context.Response.Headers.CacheControl = "no-store";
			}
			await next();
		});

		async Task<IResult> Login(Func<Task<IResult>> run)
		{
			try
			{
				return await run();
			}
			catch (LoginException ex)
			{
				return Results.Json(new ErrorDto(ex.Message, ex.Code, ex.Retryable), statusCode: ex.Status);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return Error($"Accounts cannot be saved: {ex.Message}", StatusCodes.Status409Conflict);
			}
		}

		api.MapPost("/accounts/login", (LoginStartRequest? body, CancellationToken ct) => Login(async () =>
		{
			if (body is null)
				return Error("Send locale and, for a new or ambiguous account, accountId.", StatusCodes.Status400BadRequest);
			var started = await logins.StartAsync(body.AccountId, body.Locale, body.DeviceRegistration, string.IsNullOrWhiteSpace(options.AccountsImportPath), ct);
			return Results.Json(new LoginStartDto(started.LoginId, started.Url, started.ExpiresAt));
		})).WithSummary("Starts an external-browser sign-in. Open url, sign in, then post the final address-bar URL to /accounts/login/{loginId}. Needs --api-token-file (403 otherwise); 400 bad request, 409 new account while accounts come from an import file, 429 too many pending, 502 Audible unreachable. A login lasts 10 minutes and is held in memory.")
			.WithName("startAccountLogin").WithTags("accounts")
			.Produces<LoginStartDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest)
			.Produces<ErrorDto>(StatusCodes.Status403Forbidden)
			.Produces<ErrorDto>(StatusCodes.Status409Conflict)
			.Produces<ErrorDto>(StatusCodes.Status429TooManyRequests)
			.Produces<ErrorDto>(StatusCodes.Status502BadGateway);

		api.MapPost("/accounts/login/{loginId}", (string loginId, LoginCompleteRequest? body, CancellationToken ct) => Login(async () =>
		{
			var target = await logins.CompleteAsync(loginId, body?.ResponseUrl, ct);
			if (target is null)
				return Error("No such pending login: it was already used, cancelled or has expired.", StatusCodes.Status404NotFound);
			authMonitor?.Clear(target.AccountId);
			var account = data.GetAccounts().FirstOrDefault(a => a.AccountId.Equals(target.AccountId, StringComparison.OrdinalIgnoreCase) && a.Locale == target.LocaleName);
			return account is null
				? Error("Signed in, but the account could not be read back.", StatusCodes.Status500InternalServerError)
				: Results.Json(account);
		})).WithSummary("Completes a sign-in with the final browser URL (treat it like a password; it is never logged or echoed). Single use. 200 with the account, 400 with code notFinalUrl (retryable true: the login stays usable) or rejected (retryable false: the login is consumed, start again), 403 without an API token, 404 unknown, used or expired.")
			.WithName("completeAccountLogin").WithTags("accounts")
			.Produces<AccountDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest)
			.Produces<ErrorDto>(StatusCodes.Status403Forbidden)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound);

		api.MapDelete("/accounts/login/{loginId}", (string loginId) => logins.Cancel(loginId)
			? Results.Json(new LoginCancelDto(loginId))
			: Error("No such pending login.", StatusCodes.Status404NotFound))
			.WithSummary("Cancels a pending sign-in. 200, 403 without an API token, 404 unknown, used or expired.")
			.WithName("cancelAccountLogin").WithTags("accounts")
			.Produces<LoginCancelDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status403Forbidden)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound);

		api.MapGet("/library", (int? offset, int? limit, string? q, bool? trash, string? sort, string? dir) =>
		{
			var (o, l) = LibraryPaging.Clamp(offset, limit);
			if (!LibrarySorting.TryParse(sort, dir, out var key, out var descending, out var problem))
				return Error(problem, StatusCodes.Status400BadRequest);
			try
			{
				return Results.Json(data.GetLibrary(o, l, q, trash ?? false, key, descending));
			}
			catch (SearchQueryException ex)
			{
				return Error(ex.Message, StatusCodes.Status400BadRequest);
			}
		}).WithSummary("Books, newest first unless sorted. offset (default 0), limit (default 100, max 1000), q (Lucene filter syntax, also with trash), trash=true for the trash, sort (title, author, narrator, series, length, purchaseDate, added, status, rating, lastDownload) with dir=asc|desc. purchaseDate and added are the same date. total counts all matches.")
			.WithName("getLibrary").WithTags("library")
			.Produces<LibraryPageDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest);

		api.MapGet("/library/asins", (string? q, bool? trash) =>
		{
			try
			{
				return Results.Json(data.GetLibraryAsins(q, trash ?? false));
			}
			catch (SearchQueryException ex)
			{
				return Error(ex.Message, StatusCodes.Status400BadRequest);
			}
		}).WithSummary("Every ASIN matching q and trash, newest first, for select-all. total counts all matches; asins stops at 50000.")
			.WithName("getLibraryAsins").WithTags("library")
			.Produces<LibraryAsinsDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest);

		api.MapGet("/library/export", (string? format, string? q, bool? trash) =>
		{
			var wanted = format?.Trim() ?? "";
			if (!ExportFormats.TryGetValue(wanted, out var type))
				return Error("format must be csv, xlsx or json.", StatusCodes.Status400BadRequest);

			string path;
			try
			{
				path = data.ExportLibrary(Enum.Parse<ExportFormat>(wanted, ignoreCase: true), q, trash ?? false);
			}
			catch (SearchQueryException ex)
			{
				return Error(ex.Message, StatusCodes.Status400BadRequest);
			}

			var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
			return Results.File(stream, type.ContentType, $"libation-{(trash == true ? "trash" : "library")}-{DateTime.Now:yyyyMMdd}.{type.Extension}");
		}).WithSummary("Downloads the library as csv, xlsx or json, the same export as the desktop and the CLI, newest first. q (Lucene filter syntax) and trash filter as in /library. 400 bad format or query.")
			.WithName(ExportOperation).WithTags("library")
			.Produces<byte[]>(StatusCodes.Status200OK, "text/csv", ExportFormats["xlsx"].ContentType, "application/json")
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest);

		api.MapGet("/quick-filters", () => Results.Json(data.GetQuickFilters()))
			.WithSummary("The saved quick filters in display order, and whether the first one applies by default.")
			.WithName("getQuickFilters").WithTags("library")
			.Produces<QuickFiltersDto>(StatusCodes.Status200OK);

		api.MapPut("/quick-filters", (QuickFiltersDto? body) =>
		{
			if (body?.Filters is null)
				return Error("filters is required.", StatusCodes.Status400BadRequest);
			for (var i = 0; i < body.Filters.Count; i++)
			{
				if (body.Filters[i]?.Filter is not { } filter || string.IsNullOrWhiteSpace(filter))
					return Error($"filters[{i}]: filter is required.", StatusCodes.Status400BadRequest);
				if (data.ValidateFilter(filter) is { } problem)
					return Error($"filters[{i}]: {problem}", StatusCodes.Status400BadRequest);
			}
			try
			{
				data.SetQuickFilters(body);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return Error($"Quick filters cannot be saved: {ex.Message}", StatusCodes.Status409Conflict);
			}
			return Results.Json(data.GetQuickFilters());
		}).WithSummary("Replaces the whole quick filter list; every filter must parse as a library query. 400 names the offending filters[index]; 409 when the filters file is not writable.")
			.WithName("putQuickFilters").WithTags("library")
			.Produces<QuickFiltersDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest)
			.Produces<ErrorDto>(StatusCodes.Status409Conflict);

		api.MapGet("/library/podcasts", (string? q, bool? trash) =>
		{
			try
			{
				return Results.Json(data.GetPodcastParents(q, trash ?? false));
			}
			catch (SearchQueryException ex)
			{
				return Error(ex.Message, StatusCodes.Status400BadRequest);
			}
		}).WithSummary("Podcast parent rows (isPodcastParent) with at least one episode matching q and trash, by title. /library never returns parents; group its episodes under these by parentAsin. An episode whose parent is missing here is a standalone row, as in the desktop grid.")
			.WithName("getLibraryPodcasts").WithTags("library")
			.Produces<IReadOnlyList<BookDto>>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest);

		api.MapGet("/library/stats", () => Results.Json(data.GetBackupCounts()))
			.WithSummary("Backup counts as in the desktop status bar: books and PDFs liberated, not liberated, errored or unavailable.")
			.WithName("getLibraryStats").WithTags("library")
			.Produces<BackupCountsDto>(StatusCodes.Status200OK);

		api.MapGet("/library/{asin}", (string asin) => data.GetBookDetail(asin) is { } book ? Results.Json(book) : NoSuchBook())
			.WithSummary("One book by ASIN with description, category ladders and file state, trashed ones included, or 404.")
			.WithName("getBook").WithTags("library")
			.Produces<BookDetailDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound);

		api.MapGet("/library/{asin}/cover", async (HttpContext context, string asin, string? size, CancellationToken ct) =>
		{
			var coverSize = size?.Trim().ToLowerInvariant() switch
			{
				null or "" or "small" => CoverSize.Small,
				"large" => CoverSize.Large,
				_ => (CoverSize?)null
			};
			if (coverSize is not { } cover)
				return Error("size must be small or large.", StatusCodes.Status400BadRequest);
			if (data.FindCover(asin) is not { } info)
				return NoSuchBook();
			if (info.PictureId is not { } pictureId)
				return Error("No cover for that book.", StatusCodes.Status404NotFound);

			var etag = $"\"{pictureId}-{cover}\"".ToLowerInvariant();
			void Cacheable()
			{
				context.Response.Headers.ETag = etag;
				context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
			}

			if (context.Request.Headers.IfNoneMatch.Any(v => v == "*" || v?.Split(',').Any(t => t.Trim() == etag) == true))
			{
				Cacheable();
				return Results.StatusCode(StatusCodes.Status304NotModified);
			}

			if (await data.LoadCover(pictureId, cover, ct) is not { } bytes)
				return Error("Cover unavailable.", StatusCodes.Status404NotFound);
			Cacheable();
			return Results.Bytes(bytes, "image/jpeg");
		}).WithSummary("Cover jpeg, size=small (80px, default) or large (500px), from the picture cache and fetched from Audible on first use. Cached immutable with an ETag; 304 on a match, 404 unknown ASIN or no cover.")
			.WithName("getBookCover").WithTags("library")
			.Produces<byte[]>(StatusCodes.Status200OK, "image/jpeg")
			.Produces(StatusCodes.Status304NotModified)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest)
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

		api.MapPost("/library/{asin}/locate", async (string asin, LocateRequest? body) =>
		{
			var result = await data.LocateFile(asin, body?.Path ?? "");
			return result.Outcome switch
			{
				LocateOutcome.NoSuchBook => NoSuchBook(),
				LocateOutcome.Invalid => Error(result.Error!, StatusCodes.Status400BadRequest),
				_ => Results.Json(result.Book)
			};
		}).WithSummary("Records an existing audio file for a book, as desktop's Locate file, and marks it liberated. The path must be an absolute audio file under the Books folder. 200 with the book, 400 bad path, 404 unknown ASIN.")
			.WithName("locateBookFile").WithTags("library")
			.Produces<BookDetailDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound);

		api.MapPost("/library/locate-scan", (LocateScanRequest? body) =>
		{
			var start = data.StartLocateScan(body?.Folder ?? "", scheduler.Events.Publish);
			return start.Outcome switch
			{
				LocateOutcome.Invalid => Error(start.Error!, StatusCodes.Status400BadRequest),
				LocateOutcome.Busy => Error("A locate scan is already running.", StatusCodes.Status409Conflict),
				_ => Results.Json(start.Scan, statusCode: StatusCodes.Status202Accepted)
			};
		}).WithSummary("Scans a folder under the Books folder in the background for audio files carrying an ASIN, as desktop's Locate audiobooks; matches are recorded and marked liberated. Progress arrives as LocateScanProgress events. 202, 400 bad folder, 409 already running.")
			.WithName("startLocateScan").WithTags("library")
			.Produces<LocateScanDto>(StatusCodes.Status202Accepted)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest)
			.Produces<ErrorDto>(StatusCodes.Status409Conflict);

		api.MapGet("/library/locate-scan", () => Results.Json(data.GetLocateScan()))
			.WithSummary("State of the last or running locate scan.")
			.WithName("getLocateScan").WithTags("library")
			.Produces<LocateScanDto>(StatusCodes.Status200OK);

		api.MapPut("/library/tags", async (BulkTagsRequest? body) =>
		{
			if (Validate(body?.Asins) is { } invalid)
				return invalid;
			var edit = new TagEdit(body!.Tags, body.Add, body.Remove);
			if (edit.Set is null == (edit.Add is null && edit.Remove is null))
				return Error("Send tags, or add and/or remove, but not both.", StatusCodes.Status400BadRequest);
			return Bulk(await data.BulkSetTags(Distinct(body.Asins!), edit));
		}).WithSummary("Sets tags on many books: tags replaces, add and remove edit. 200 with a result per ASIN, 400 bad body or over 5000 ASINs.")
			.WithName("bulkSetTags").WithTags("library")
			.Produces<BulkResultDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest);

		api.MapPut("/library/status", async (BulkStatusRequest? body) =>
			Validate(body?.Asins) is { } invalid ? invalid
			: !TryParseStatus(body!.Status, out var status) ? Error("status must be liberated or notLiberated.", StatusCodes.Status400BadRequest)
			: Bulk(await data.BulkSetBookStatus(Distinct(body.Asins!), status)))
			.WithSummary("Sets the liberated status of many books. 200 with a result per ASIN, 400 bad body or over 5000 ASINs.")
			.WithName("bulkSetBookStatus").WithTags("library")
			.Produces<BulkResultDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest);

		api.MapPut("/library/pdf-status", async (BulkStatusRequest? body) =>
			Validate(body?.Asins) is { } invalid ? invalid
			: !TryParseStatus(body!.Status, out var status) ? Error("status must be liberated or notLiberated.", StatusCodes.Status400BadRequest)
			: Bulk(await data.BulkSetPdfStatus(Distinct(body.Asins!), status)))
			.WithSummary("Sets the PDF status of many books; books without a PDF are conflict. 200 with a result per ASIN, 400 bad body or over 5000 ASINs.")
			.WithName("bulkSetPdfStatus").WithTags("library")
			.Produces<BulkResultDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest);

		api.MapPost("/library/trash", async (BulkRequest? body) =>
			Validate(body?.Asins) is { } invalid ? invalid : Bulk(await data.BulkSetTrash(Distinct(body!.Asins!), true)))
			.WithSummary("Moves many books to the trash; ones already there are ok. 200 with a result per ASIN, 400 bad body or over 5000 ASINs.")
			.WithName("bulkTrashBooks").WithTags("library")
			.Produces<BulkResultDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest);

		api.MapPost("/library/restore", async (BulkRequest? body) =>
			Validate(body?.Asins) is { } invalid ? invalid : Bulk(await data.BulkSetTrash(Distinct(body!.Asins!), false)))
			.WithSummary("Restores many books from the trash; ones not in it are ok. 200 with a result per ASIN, 400 bad body or over 5000 ASINs.")
			.WithName("bulkRestoreBooks").WithTags("library")
			.Produces<BulkResultDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest);

		api.MapPost("/library/purge", async (BulkRequest? body) =>
			Validate(body?.Asins) is { } invalid ? invalid : Bulk(await data.BulkPurge(Distinct(body!.Asins!))))
			.WithSummary("Permanently deletes books that are in the trash, irreversibly; others are conflict. 200 with a result per ASIN, 400 bad body or over 5000 ASINs.")
			.WithName("bulkPurgeBooks").WithTags("library")
			.Produces<BulkResultDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest);

		api.MapPost("/library/liberate", (BulkRequest? body) =>
		{
			if (Validate(body?.Asins) is { } invalid)
				return invalid;
			var results = new List<BulkItemDto>();
			lock (enqueueLock)
			{
				var active = queue.Jobs.GetAllItems().Except(queue.Jobs.Completed)
					.Select(j => j.LibraryBook.Book.AudibleProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
				var jobs = new List<LiberationJob>();
				foreach (var asin in Distinct(body!.Asins!))
				{
					if (active.Contains(asin))
						results.Add(new(asin, BulkResults.Conflict));
					else if (createBookJob?.Invoke(asin) is not { } job)
						results.Add(new(asin, BulkResults.NotFound));
					else
					{
						jobs.Add(job);
						results.Add(new(asin, BulkResults.Ok));
					}
				}
				if (jobs.Count > 0)
				{
					queue.Jobs.Enqueue(jobs);
					queue.StartOrWake();
				}
			}
			return Results.Json(BulkResults.Summarize(results), statusCode: StatusCodes.Status202Accepted);
		}).WithSummary("Queues many books for liberation; ones already queued are conflict. 202 with a result per ASIN, 400 bad body or over 5000 ASINs.")
			.WithName("bulkLiberateBooks").WithTags("library")
			.Produces<BulkResultDto>(StatusCodes.Status202Accepted)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest);

		api.MapGet("/settings", () => data.GetSettings())
			.WithSummary("Curated settings: Books and working folders (with whether each exists and is writable), naming templates, download, MP3, queue, library and logging options, and character replacements. Secrets are never returned.")
			.WithName("getSettings").WithTags("settings")
			.Produces<SettingsDto>(StatusCodes.Status200OK);

		api.MapPatch("/settings", async (HttpRequest request) =>
		{
			if (options.ReadonlyConfig)
				return Error("Settings are read-only because the daemon runs with a read-only config directory; edit its Settings.json and restart.", StatusCodes.Status409Conflict);

			JsonDocument body;
			try
			{
				body = await JsonDocument.ParseAsync(request.Body);
			}
			catch (JsonException)
			{
				return FieldErrors("The body is not valid JSON.", new Dictionary<string, string>());
			}

			using (body)
			{
				SettingsUpdateResult result;
				try
				{
					result = data.UpdateSettings(body.RootElement.Clone());
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					return Error($"Settings cannot be saved: {ex.Message}", StatusCodes.Status409Conflict);
				}
				return result.Ok
					? Results.Json(result.Settings)
					: FieldErrors("Some settings are invalid; nothing was changed.", result.Errors!);
			}
		}).WithSummary("Changes the settings named in the body; absent keys are untouched and every key is validated before any is applied. Body mirrors GET /api/settings, so naming.folderTemplate is {\"naming\":{\"folderTemplate\":\"...\"}}. A null inProgress resets it; other keys reject null. 200 with the updated settings (restartRequired lists keys that need a restart), 400 with fields keyed by dotted setting name, 409 when the config is read-only or Settings.json cannot be written.")
			.WithName("patchSettings").WithTags("settings")
			.Accepts<SettingsPatchRequest>("application/json")
			.Produces<SettingsDto>(StatusCodes.Status200OK)
			.Produces<FieldErrorsDto>(StatusCodes.Status400BadRequest)
			.Produces<ErrorDto>(StatusCodes.Status409Conflict);

		api.MapPost("/settings/templates/preview", (TemplatePreviewRequest? body) =>
		{
			if (body?.Template is not { } template)
				return Error("template is required.", StatusCodes.Status400BadRequest);
			var kind = TemplatePreview.Kinds.FirstOrDefault(k => string.Equals(k, body.Kind, StringComparison.OrdinalIgnoreCase));
			if (kind is null)
				return Error($"kind must be one of: {string.Join(", ", TemplatePreview.Kinds)}.", StatusCodes.Status400BadRequest);
			var result = data.PreviewTemplate(kind, template, string.IsNullOrWhiteSpace(body.Asin) ? null : body.Asin.Trim());
			return result.BookFound ? Results.Json(result.Preview) : Error("No such book.", StatusCodes.Status404NotFound);
		}).WithSummary("Checks a naming template and renders it as the app's template editor does, for the library book with the given asin or a sample book. kind is folder, file, chapterFile or chapterTitle. 200 with valid, errors, warnings and preview (null when invalid), 400 bad body, 404 unknown asin.")
			.WithName("previewTemplate").WithTags("settings")
			.Produces<TemplatePreviewDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound);

		api.MapPost("/scan", (ScanRequest? body) =>
		{
			if (body?.Accounts is not { } names)
				return Run(RunKind.Scan);
			if (names.Count == 0 || names.Any(string.IsNullOrWhiteSpace))
				return Error("accounts must name at least one account; omit it to scan all.", StatusCodes.Status400BadRequest);

			var known = data.GetAccounts();
			var unknown = names.Where(n => !known.Any(a => string.Equals(a.AccountId, n.Trim(), StringComparison.OrdinalIgnoreCase)
				|| string.Equals(a.AccountName, n.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
			return unknown.Count > 0
				? Error($"No such account: {string.Join(", ", unknown)}.", StatusCodes.Status400BadRequest)
				: Run(RunKind.Scan, names.Select(n => n.Trim()).ToList());
		}).WithSummary("Runs a library scan now, without liberating. An optional body {\"accounts\":[\"id or name\"]} scans only those accounts, as the CLI's scan does; no body scans all. 202, 400 bad body or unknown account, or 409 if busy.")
			.WithName("scan").WithTags("daemon")
			.Produces<StatusDto>(StatusCodes.Status202Accepted)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest)
			.Produces<ErrorDto>(StatusCodes.Status409Conflict);

		api.MapPost("/liberate", () => Run(RunKind.Cycle))
			.WithSummary("Runs a full cycle now: scan, then liberate if the scan succeeded. 202, or 409 if busy.")
			.WithName("liberate").WithTags("daemon")
			.Produces<StatusDto>(StatusCodes.Status202Accepted)
			.Produces<ErrorDto>(StatusCodes.Status409Conflict);

		api.MapGet("/queue", () => QueueMapper.Snapshot(queue))
			.WithSummary("Working and queued jobs, pending decisions, the current wait if any, whether the user has paused dispatch, and the desktop's counters: queued, completed, errors and when the run began.")
			.WithName("getQueue").WithTags("queue")
			.Produces<QueueDto>(StatusCodes.Status200OK);

		api.MapPost("/queue/decisions/{pauseId:long}", (long pauseId, DecisionRequest? body) => Decide(queue, new PauseId(pauseId), body))
			.WithSummary("Answers a pending decision or wait. 200 with the queue, 400 bad body, 404 unknown id, 409 decision not allowed.")
			.WithName("decide").WithTags("queue")
			.Produces<QueueDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound)
			.Produces<ErrorDto>(StatusCodes.Status409Conflict);

		api.MapDelete("/queue/jobs/{id}", async (string id) =>
			await queue.CancelJobAsync(new JobId(id)) switch
			{
				CancelJobOutcome.Removed => Results.Json(new CancelJobDto(id, "removed")),
				CancelJobOutcome.Cancelling => Results.Json(new CancelJobDto(id, "cancelling")),
				_ => NoSuchJob()
			})
			.WithSummary("Removes a queued job, or cancels a running one, which then finishes as cancelled. 200 with the outcome, removed or cancelling; 404 unknown or finished job.")
			.WithName("cancelJob").WithTags("queue")
			.Produces<CancelJobDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound);

		api.MapPost("/queue/cancel", async () =>
		{
			await queue.CancelAllAsync();
			return Results.Json(QueueMapper.Snapshot(queue));
		}).WithSummary("Clears the queue and cancels every running job, as desktop's Cancel All. 200 with the queue.")
			.WithName("cancelAll").WithTags("queue")
			.Produces<QueueDto>(StatusCodes.Status200OK);

		api.MapPost("/queue/jobs/{id}/move", (string id, MoveRequest? body) =>
		{
			if (!QueueMapper.TryParse(body, out var position))
				return Error("to must be one of top, up, down, bottom.", StatusCodes.Status400BadRequest);
			return queue.MoveJob(new JobId(id), position) switch
			{
				MoveJobOutcome.Moved => Results.Json(QueueMapper.Snapshot(queue)),
				MoveJobOutcome.NotQueued => Error("Only a queued job can be moved.", StatusCodes.Status409Conflict),
				_ => NoSuchJob()
			};
		}).WithSummary("Moves a queued job to the top, up one, down one or the bottom. 200 with the queue, 400 bad body, 404 unknown job, 409 job not queued.")
			.WithName("moveJob").WithTags("queue")
			.Produces<QueueDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status400BadRequest)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound)
			.Produces<ErrorDto>(StatusCodes.Status409Conflict);

		api.MapPost("/queue/pause", () =>
		{
			queue.SetDispatchPaused(true);
			return Results.Json(QueueMapper.Snapshot(queue));
		}).WithSummary("Stops starting new jobs; running ones finish. Held until resumed, across runs. 200 with the queue, also when already paused.")
			.WithName("pauseQueue").WithTags("queue")
			.Produces<QueueDto>(StatusCodes.Status200OK);

		api.MapPost("/queue/resume", () =>
		{
			queue.SetDispatchPaused(false);
			return Results.Json(QueueMapper.Snapshot(queue));
		}).WithSummary("Resumes starting jobs after a pause. 200 with the queue, also when not paused.")
			.WithName("resumeQueue").WithTags("queue")
			.Produces<QueueDto>(StatusCodes.Status200OK);

		api.MapGet("/queue/history", () => queue.History.Snapshot().Select(QueueMapper.ToDto).ToList())
			.WithSummary("The last 200 finished jobs, newest first, with result, status and finish time.")
			.WithName("getQueueHistory").WithTags("queue")
			.Produces<IReadOnlyList<FinishedJobDto>>(StatusCodes.Status200OK);

		api.MapDelete("/queue/history", () =>
		{
			queue.ClearFinished();
			return Results.Json(QueueMapper.Snapshot(queue));
		}).WithSummary("Forgets finished jobs, their logs and the completed and error counts, as desktop's Clear Finished. 200 with the queue.")
			.WithName("clearQueueHistory").WithTags("queue")
			.Produces<QueueDto>(StatusCodes.Status200OK);

		api.MapGet("/queue/jobs/{id}/log", (string id) =>
		{
			var jobId = new JobId(id);
			var log = queue.FindJob(jobId)?.Log ?? queue.History.Find(jobId)?.Log;
			return log is null ? NoSuchJob() : Results.Json(QueueMapper.ToDto(jobId, log));
		}).WithSummary("A job's last 500 log lines, oldest first, with secrets and URL queries removed; works for queued, running and finished jobs. 404 unknown job.")
			.WithName("getJobLog").WithTags("queue")
			.Produces<JobLogDto>(StatusCodes.Status200OK)
			.Produces<ErrorDto>(StatusCodes.Status404NotFound);

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

	private static IResult FieldErrors(string message, IReadOnlyDictionary<string, string> fields)
		=> Results.Json(new FieldErrorsDto(message, fields), statusCode: StatusCodes.Status400BadRequest);

	private static IResult Bulk(IReadOnlyList<BulkItemDto> results) => Results.Json(BulkResults.Summarize(results));

	private static IReadOnlyList<string> Distinct(IReadOnlyList<string> asins) => asins.Select(a => a?.Trim() ?? "").Distinct(StringComparer.Ordinal).ToList();

	private static IResult? Validate(IReadOnlyList<string>? asins)
		=> asins is null || asins.Count == 0 ? Error("asins is required and must not be empty.", StatusCodes.Status400BadRequest)
		: asins.Count > MaxBulk ? Error($"At most {MaxBulk} asins per request.", StatusCodes.Status400BadRequest)
		: null;

	private static IResult NoSuchBook() => Error("No such book.", StatusCodes.Status404NotFound);

	private static IResult NoSuchJob() => Error("No such job.", StatusCodes.Status404NotFound);

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
