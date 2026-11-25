using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using LibationEngine.Contracts;
using LibationEngine.Host;
using DataLayer;
using LibationEngine;
using LibationFileManager;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
public class ApiTests
{
	private string configDir = "";

	[TestInitialize]
	public void Init()
	{
		configDir = Path.Combine(Path.GetTempPath(), $"libation-engine-api-{Guid.NewGuid():N}");
		Directory.CreateDirectory(configDir);
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, configDir);
		Configuration.CreateMockInstance();
	}

	[TestCleanup]
	public void Cleanup()
	{
		Configuration.RestoreSingletonInstance();
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, null);
		try { Directory.Delete(configDir, recursive: true); } catch { }
	}

	private sealed class FakeData : IEngineData
	{
		public (int Offset, int Limit)? LastPage;
		public (string? Query, bool Trash)? LastFilter;
		public (LibrarySortKey? Sort, bool Descending)? LastSort;
		public (CoverSize Size, string PictureId)? LastCover;
		public readonly List<string> Calls = [];

		public List<AccountDto> Accounts = [new("a@example.com", "Main", "us", true, AuthStates.SignedIn, CredentialKinds.Modern, [])];
		public bool AccountsReadOnly;

		public IReadOnlyList<AccountDto> GetAccounts() => Accounts.ToList();

		private AccountResult Find(string id, string? locale, Func<AccountDto, AccountDto?> change)
		{
			Calls.Add($"account:{id}:{locale}");
			if (AccountsReadOnly)
				throw new UnauthorizedAccessException("denied");
			var matches = Accounts.Where(a => a.AccountId == id && (locale is null || a.Locale == locale)).ToList();
			if (matches.Count == 0)
				return new(AccountOutcome.NotFound);
			if (matches.Count > 1)
				return new(AccountOutcome.Ambiguous);
			var index = Accounts.IndexOf(matches[0]);
			var updated = change(matches[0]);
			if (updated is null)
				Accounts.RemoveAt(index);
			else
				Accounts[index] = updated;
			return new(AccountOutcome.Ok, updated ?? matches[0]);
		}

		public AccountResult UpdateAccount(string accountId, string? locale, string? accountName, bool? libraryScan)
			=> Find(accountId, locale, a => a with { AccountName = accountName ?? a.AccountName, LibraryScan = libraryScan ?? a.LibraryScan });

		public AccountResult DeleteAccount(string accountId, string? locale) => Find(accountId, locale, _ => null);

		public LibraryPageDto GetLibrary(int offset, int limit, string? query = null, bool trash = false, LibrarySortKey? sort = null, bool descending = false)
		{
			LastPage = (offset, limit);
			LastFilter = (query, trash);
			LastSort = (sort, descending);
			if (query == "bad[")
				throw new SearchQueryException("Cannot parse the search query: bad[");
			return new(7, offset, limit, [Book("B000000001")]);
		}

		public LibraryAsinsDto GetLibraryAsins(string? query = null, bool trash = false)
		{
			LastFilter = (query, trash);
			if (query == "bad[")
				throw new SearchQueryException("Cannot parse the search query: bad[");
			return new(1, ["B000000001"]);
		}

		public string ExportLibrary(ExportFormat format, string? query = null, bool trash = false)
		{
			LastFilter = (query, trash);
			if (query == "bad[")
				throw new SearchQueryException("Cannot parse the search query: bad[");
			var path = Path.GetTempFileName();
			File.WriteAllText(path, $"exported {format}");
			LastExportPath = path;
			return path;
		}

		public string? LastExportPath;

		public QuickFiltersDto Filters = new(false, []);
		public bool FiltersReadOnly;

		public QuickFiltersDto GetQuickFilters() => Filters;

		public void SetQuickFilters(QuickFiltersDto filters)
		{
			if (FiltersReadOnly)
				throw new UnauthorizedAccessException("denied");
			Filters = filters;
		}

		public string? ValidateFilter(string filter) => filter.Contains('[') ? "Cannot parse the search query: " + filter : null;

		public IReadOnlyList<BookDto> GetPodcastParents(string? query = null, bool trash = false)
		{
			LastFilter = (query, trash);
			if (query == "bad[")
				throw new SearchQueryException("Cannot parse the search query: bad[");
			return [Book("B000000009") with { Title = "Show", IsPodcastParent = true, ContentType = "Parent" }];
		}

		public BookDto? GetBook(string asin) => asin == "B000000001" ? Book(asin) : null;

		public BookDetailDto? GetBookDetail(string asin)
			=> GetBook(asin) is { } b ? new BookDetailDto(b) { Description = "About", CategoryLadders = [["Fiction", "Mystery"]], AudioFileExists = true } : null;

		public BackupCountsDto GetBackupCounts() => new(10, 4, 1, 3, 1, 1, 2, 5, 3);

		public CoverInfo? FindCover(string asin) => asin switch
		{
			"B000000001" => new("PIC1"),
			"B000000002" => new(null),
			"B000000003" => new("GONE"),
			_ => null
		};

		public Task<byte[]?> LoadCover(string pictureId, CoverSize size, CancellationToken ct)
		{
			LastCover = (size, pictureId);
			return Task.FromResult<byte[]?>(pictureId == "GONE" ? null : [0xFF, 0xD8, 0xFF, 0xD9]);
		}

		public Task<BookDto?> SetTags(string asin, string tags)
		{
			Calls.Add($"tags:{asin}:{tags}");
			return Task.FromResult(GetBook(asin) is { } b ? b with { Tags = tags } : null);
		}

		public Task<BookDto?> SetBookStatus(string asin, LiberatedStatus status)
		{
			Calls.Add($"status:{asin}:{status}");
			return Task.FromResult(GetBook(asin) is { } b ? b with { BookStatus = status.ToString() } : null);
		}

		public Task<BookDto?> SetTrash(string asin, bool inTrash)
		{
			Calls.Add($"trash:{asin}:{inTrash}");
			return Task.FromResult(GetBook(asin) is { } b ? b with { InTrash = inTrash } : null);
		}

		private Task<IReadOnlyList<BulkItemDto>> Bulk(string call, IReadOnlyList<string> asins)
		{
			Calls.Add($"{call}:{string.Join(',', asins)}");
			return Task.FromResult<IReadOnlyList<BulkItemDto>>(asins
				.Select(a => new BulkItemDto(a, a == "B000000001" ? BulkResults.Ok : a == "B000000002" ? BulkResults.Conflict : BulkResults.NotFound))
				.ToList());
		}

		public Task<IReadOnlyList<BulkItemDto>> BulkSetTags(IReadOnlyList<string> asins, TagEdit edit)
			=> Bulk($"bulktags:{edit.Set}|{edit.Add}|{edit.Remove}", asins);

		public Task<IReadOnlyList<BulkItemDto>> BulkSetBookStatus(IReadOnlyList<string> asins, LiberatedStatus status)
			=> Bulk($"bulkstatus:{status}", asins);

		public Task<IReadOnlyList<BulkItemDto>> BulkSetPdfStatus(IReadOnlyList<string> asins, LiberatedStatus status)
			=> Bulk($"bulkpdf:{status}", asins);

		public Task<IReadOnlyList<BulkItemDto>> BulkSetTrash(IReadOnlyList<string> asins, bool inTrash)
			=> Bulk($"bulktrash:{inTrash}", asins);

		public Task<IReadOnlyList<BulkItemDto>> BulkPurge(IReadOnlyList<string> asins)
			=> Bulk("bulkpurge", asins);

		public SettingsDto GetSettings()
			=> SettingsMapper.From(Configuration.Instance);

		public SettingsUpdateResult UpdateSettings(JsonElement patch)
			=> SettingsWriter.Apply(Configuration.Instance, patch);

		public TemplatePreviewResult PreviewTemplate(string kind, string template, string? asin)
			=> asin is not (null or "B000000001") ? new(false, null) : new(true, TemplatePreview.Render(Configuration.Instance, kind, template, null));

		public Task<LocateResult> LocateFile(string asin, string path)
		{
			Calls.Add($"locate:{asin}:{path}");
			return Task.FromResult(
				GetBookDetail(asin) is not { } book ? new LocateResult(LocateOutcome.NoSuchBook)
				: path == "/books/a.m4b" ? new LocateResult(LocateOutcome.Ok, book with { BookStatus = "Liberated" })
				: new LocateResult(LocateOutcome.Invalid, Error: "path must be inside the Books folder."));
		}

		public LocateScanStart StartLocateScan(string folder, Action<EngineEvent> publish)
		{
			Calls.Add($"locatescan:{folder}");
			if (folder == "/busy")
				return new(LocateOutcome.Busy);
			if (folder != "/books")
				return new(LocateOutcome.Invalid, Error: "path must be inside the Books folder.");
			var scan = new LocateScanDto("running", folder, 0, 0, 0, null, DateTimeOffset.UnixEpoch, null);
			publish(new LocateScanProgress(scan));
			return new(LocateOutcome.Ok, scan);
		}

		public LocateScanDto GetLocateScan() => new("idle", null, 0, 0, 0, null, null, null);

		private static BookDto Book(string asin)
			=> new(asin, "Title", null, ["Author"], ["Narrator"], [], 60, "NotLiberated", null, DateTime.UnixEpoch, "a@example.com", "us", "Product", false, "", false,
				["Mystery"], new(4.5f, 4f, 5f), new(0, 0, 0), false, false, null, null, null, null, false, "PIC1", null, false, false, false);
	}

	private sealed class Harness : IAsyncDisposable
	{
		private readonly TempDir dir = new();
		private readonly CancellationTokenSource cts = new();
		private readonly Task<int> run;
		public readonly FakeData Data = new();
		public readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
		public readonly string Base;
		public int ScanCalls, LiberateCalls;
		public readonly List<string> ScanSubsets = [];
		public volatile TaskCompletionSource? Gate;
		public readonly SemaphoreSlim ScanEntered = new(0);

		public readonly LiberationQueue Queue;
		public volatile Func<LiberationJob, Task<ProcessBookResult>> Outcome = _ => Task.FromResult(ProcessBookResult.Success);

		public Harness(bool api, string? tokenFile = null, bool startRun = true, DiskFullPolicy onDiskFull = DiskFullPolicy.Ask, string? accountsImport = null, LoginSessions? logins = null, bool readonlyConfig = false)
		{
			Queue = new LiberationQueue(new LiberationPolicy { MaxConcurrentDownloads = () => 1, OnDiskFull = onDiskFull })
			{
				MachineCeilingOverride = 1,
				SpaceProbe = () => null
			};
			Queue.RunJob = async job =>
			{
				var result = await Outcome(job);
				job.Result = result;
				job.Status = result == ProcessBookResult.Success ? ProcessBookStatus.Completed : ProcessBookStatus.Failed;
				return result;
			};

			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			var port = ((IPEndPoint)listener.LocalEndpoint).Port;
			listener.Stop();
			Base = $"http://127.0.0.1:{port}";

			var jobs = new DaemonJobs(
				async ct =>
				{
					Interlocked.Increment(ref ScanCalls);
					ScanEntered.Release();
					if (Gate is { } gate)
						await gate.Task.WaitAsync(ct);
					return 0;
				},
				_ => { Interlocked.Increment(ref LiberateCalls); return Task.FromResult(0); },
				_ => Task.FromResult(0))
			{
				CreateBookJob = asin => asin == "B000000001" ? Job(asin) : null,
				ScanAccounts = (accounts, _) =>
				{
					ScanSubsets.Add(string.Join(',', accounts));
					return Task.FromResult(0);
				}
			};

			run = LibationDaemon.RunAsync(
				new DaemonOptions(TimeSpan.FromMinutes(30), false, Base, dir.Path, AccountsImportPath: accountsImport, ApiEnabled: api, ApiTokenFile: tokenFile, ReadonlyConfig: readonlyConfig),
				jobs, cts.Token, data: Data, queue: Queue, logins: logins);
		}

		public Task<int> Run => run;

		public LiberationJob Job(string asin)
		{
			var contributor = Contributor.GetEmpty();
			var book = new Book(new AudibleProductId(asin), $"Title {asin}", null, null, 1, ContentType.Product, [contributor], [contributor], "us");
			return new LiberationJob(new LibraryBook(book, new DateTime(2026, 8, 10), "account"), Configuration.Instance, Queue.BadBookSession);
		}

		public void Enqueue(params LiberationJob[] jobs)
		{
			Queue.Jobs.Enqueue(jobs);
			Queue.StartOrWake();
		}

		public async Task<JsonDocument> QueueJson()
			=> JsonDocument.Parse(await Http.GetStringAsync(Base + "/api/queue"));

		public Task<HttpResponseMessage> Decide(long id, string json)
			=> Http.PostAsync($"{Base}/api/queue/decisions/{id}", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

		public async Task WaitIdleAfterFirstCycle()
		{
			await Eventually(async () =>
			{
				try
				{
					using var doc = JsonDocument.Parse(await Http.GetStringAsync(Base + "/health"));
					return doc.RootElement.GetProperty("liberateExitCode").ValueKind == JsonValueKind.Number
						&& doc.RootElement.GetProperty("state").GetString() == "idle";
				}
				catch (HttpRequestException) { return false; }
			});
		}

		public async ValueTask DisposeAsync()
		{
			cts.Cancel();
			await run.WaitAsync(TimeSpan.FromSeconds(10));
			Http.Dispose();
			dir.Dispose();
		}
	}

	private static async Task Eventually(Func<Task<bool>> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (DateTime.UtcNow < deadline)
		{
			if (await condition())
				return;
			await Task.Delay(20);
		}
		Assert.Fail("Condition not met within 10s.");
	}

	[TestMethod]
	public async Task Api_is_404_when_disabled_but_health_works()
	{
		await using var h = new Harness(api: false);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.GetAsync(h.Base + "/api/status")).StatusCode);
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.PostAsync(h.Base + "/api/scan", null)).StatusCode);
		Assert.AreEqual(HttpStatusCode.OK, (await h.Http.GetAsync(h.Base + "/health")).StatusCode);
	}

	[TestMethod]
	public async Task Token_is_required_for_api_but_not_health()
	{
		using var dir = new TempDir();
		var tokenFile = dir.File("token");
		File.WriteAllText(tokenFile, "s3cret\n");
		await using var h = new Harness(api: true, tokenFile);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.OK, (await h.Http.GetAsync(h.Base + "/health")).StatusCode);
		Assert.AreEqual(HttpStatusCode.Unauthorized, (await h.Http.GetAsync(h.Base + "/api/status")).StatusCode);
		Assert.AreEqual(HttpStatusCode.Unauthorized, (await h.Http.PostAsync(h.Base + "/api/scan", null)).StatusCode);

		var wrong = new HttpRequestMessage(HttpMethod.Get, h.Base + "/api/status");
		wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
		Assert.AreEqual(HttpStatusCode.Unauthorized, (await h.Http.SendAsync(wrong)).StatusCode);

		var right = new HttpRequestMessage(HttpMethod.Get, h.Base + "/api/status");
		right.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "s3cret");
		Assert.AreEqual(HttpStatusCode.OK, (await h.Http.SendAsync(right)).StatusCode);
	}

	[TestMethod]
	public async Task Missing_or_empty_token_file_fails_startup()
	{
		using var dir = new TempDir();
		var empty = dir.File("empty");
		File.WriteAllText(empty, " \n");

		foreach (var path in new[] { dir.File("missing"), empty })
		{
			await using var h = new Harness(api: true, path);
			Assert.AreEqual(1, await h.Run.WaitAsync(TimeSpan.FromSeconds(10)));
		}
	}

	[TestMethod]
	public async Task Status_reports_state_interval_and_version_key()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var doc = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/status"));
		Assert.AreEqual("idle", doc.RootElement.GetProperty("state").GetString());
		Assert.AreEqual(1800, doc.RootElement.GetProperty("intervalSeconds").GetInt32());
		Assert.IsTrue(doc.RootElement.TryGetProperty("version", out _));
	}

	[TestMethod]
	public async Task Status_says_whether_accounts_are_managed_by_an_import_file()
	{
		async Task<bool> Managed(string? import)
		{
			await using var h = new Harness(api: true, accountsImport: import);
			await h.WaitIdleAfterFirstCycle();
			using var doc = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/status"));
			return doc.RootElement.GetProperty("accountsManagedByImport").GetBoolean();
		}

		Assert.IsFalse(await Managed(null));
		Assert.IsTrue(await Managed(Path.Combine(Path.GetTempPath(), "missing-accounts.json")));
	}

	[TestMethod]
	public async Task Scan_runs_exactly_once_and_a_concurrent_request_gets_409()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		var liberatesBefore = h.LiberateCalls;
		while (h.ScanEntered.CurrentCount > 0)
			await h.ScanEntered.WaitAsync();
		h.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

		var first = await h.Http.PostAsync(h.Base + "/api/scan", null);
		Assert.AreEqual(HttpStatusCode.Accepted, first.StatusCode);
		await h.ScanEntered.WaitAsync(TimeSpan.FromSeconds(10));

		var second = await h.Http.PostAsync(h.Base + "/api/scan", null);
		var third = await h.Http.PostAsync(h.Base + "/api/liberate", null);
		Assert.AreEqual(HttpStatusCode.Conflict, second.StatusCode);
		Assert.AreEqual(HttpStatusCode.Conflict, third.StatusCode);

		h.Gate.SetResult();
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(2, h.ScanCalls);
		Assert.AreEqual(liberatesBefore, h.LiberateCalls);
	}

	[TestMethod]
	public async Task Liberate_runs_scan_then_liberate()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.Accepted, (await h.Http.PostAsync(h.Base + "/api/liberate", null)).StatusCode);
		await Eventually(() => Task.FromResult(h.LiberateCalls == 2));

		Assert.AreEqual(2, h.ScanCalls);
	}

	[TestMethod]
	public async Task Events_stream_delivers_scan_started_and_finished()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var request = new HttpRequestMessage(HttpMethod.Get, h.Base + "/api/events");
		using var response = await h.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
		Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
		using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

		Assert.AreEqual(HttpStatusCode.Accepted, (await h.Http.PostAsync(h.Base + "/api/scan", null)).StatusCode);

		var seen = new List<string>();
		string? finishedData = null;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (finishedData is null)
		{
			var line = await reader.ReadLineAsync(timeout.Token);
			Assert.IsNotNull(line);
			if (line.StartsWith("event: "))
				seen.Add(line[7..]);
			else if (line.StartsWith("data: ") && seen.LastOrDefault() == nameof(ScanFinished))
				finishedData = line[6..];
		}

		CollectionAssert.IsSubsetOf(new[] { nameof(ScanStarted), nameof(ScanFinished) }, seen);
		Assert.IsLessThan(seen.IndexOf(nameof(ScanFinished)), seen.IndexOf(nameof(ScanStarted)));
		Assert.AreEqual(0, JsonDocument.Parse(finishedData).RootElement.GetProperty("exitCode").GetInt32());
	}

	[TestMethod]
	public async Task Event_subscription_is_released_on_disconnect()
	{
		using var dir = new TempDir();
		var events = new EventBroadcaster();
		var scheduler = new CycleScheduler(
			new DaemonOptions(TimeSpan.FromMinutes(1), false, "http://127.0.0.1:0", dir.Path),
			new DaemonJobs(_ => Task.FromResult(0), _ => Task.FromResult(0), _ => Task.FromResult(0)),
			new CycleState(), events: events);

		using (events.Subscribe())
			Assert.AreEqual(1, events.SubscriberCount);
		Assert.AreEqual(0, events.SubscriberCount);
		Assert.IsFalse(scheduler.TryRequestRun(RunKind.Scan));
	}

	[TestMethod]
	public void Broadcaster_drops_oldest_for_a_slow_subscriber()
	{
		var events = new EventBroadcaster(capacity: 2);
		using var sub = events.Subscribe();

		events.Publish(new ScanFinished(1));
		events.Publish(new ScanFinished(2));
		events.Publish(new ScanFinished(3));

		Assert.IsTrue(sub.Reader.TryRead(out var a));
		Assert.IsTrue(sub.Reader.TryRead(out var b));
		Assert.AreEqual(2, ((ScanFinished)a!).ExitCode);
		Assert.AreEqual(3, ((ScanFinished)b!).ExitCode);
		Assert.IsFalse(sub.Reader.TryRead(out _));
	}

	[TestMethod]
	public async Task Accounts_endpoint_exposes_no_token_or_identity_fields()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var doc = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/accounts"));
		var names = doc.RootElement[0].EnumerateObject().Select(p => p.Name).Order().ToArray();
		CollectionAssert.AreEqual(new[] { "accountId", "accountName", "additionalLocales", "authState", "credentials", "libraryScan", "locale" }, names);
	}

	[TestMethod]
	public async Task Status_lists_accounts_needing_login_from_stored_state()
	{
		await using var h = new Harness(api: true);
		h.Data.Accounts.Add(new("b@example.com", "Second", "uk", true, AuthStates.NeedsLogin, CredentialKinds.None, []));
		await h.WaitIdleAfterFirstCycle();

		using var doc = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/status"));
		CollectionAssert.AreEqual(new[] { "b@example.com" }, doc.RootElement.GetProperty("accountsNeedingLogin").EnumerateArray().Select(e => e.GetString()).ToArray());
		using var accounts = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/accounts"));
		Assert.AreEqual("needsLogin", accounts.RootElement[1].GetProperty("authState").GetString());
	}

	[TestMethod]
	public async Task Patch_account_renames_and_toggles_scan_then_validates()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var ok = await h.Http.SendAsync(Send(HttpMethod.Patch, h.Base + "/api/accounts/a@example.com", """{"accountName":"Renamed","libraryScan":false}"""));
		Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
		using var body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
		Assert.AreEqual("Renamed", body.RootElement.GetProperty("accountName").GetString());
		Assert.IsFalse(body.RootElement.GetProperty("libraryScan").GetBoolean());

		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.SendAsync(Send(HttpMethod.Patch, h.Base + "/api/accounts/a@example.com", "{}"))).StatusCode);
		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.SendAsync(Send(HttpMethod.Patch, h.Base + "/api/accounts/a@example.com", """{"accountName":"  "}"""))).StatusCode);
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.SendAsync(Send(HttpMethod.Patch, h.Base + "/api/accounts/nobody", """{"libraryScan":true}"""))).StatusCode);
	}

	[TestMethod]
	public async Task Account_edits_need_a_locale_when_the_id_is_shared()
	{
		await using var h = new Harness(api: true);
		h.Data.Accounts.Add(new("a@example.com", "UK", "uk", true, AuthStates.SignedIn, CredentialKinds.Modern, []));
		await h.WaitIdleAfterFirstCycle();

		var ambiguous = await h.Http.SendAsync(Send(HttpMethod.Delete, h.Base + "/api/accounts/a@example.com"));
		Assert.AreEqual(HttpStatusCode.Conflict, ambiguous.StatusCode);
		Assert.HasCount(2, h.Data.Accounts);

		var ok = await h.Http.SendAsync(Send(HttpMethod.Delete, h.Base + "/api/accounts/a@example.com?locale=uk"));
		Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
		Assert.AreEqual("us", h.Data.Accounts.Single().Locale);
	}

	[TestMethod]
	public async Task Delete_account_removes_it_and_404s_the_second_time()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.OK, (await h.Http.SendAsync(Send(HttpMethod.Delete, h.Base + "/api/accounts/a@example.com"))).StatusCode);
		Assert.IsEmpty(h.Data.Accounts);
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.SendAsync(Send(HttpMethod.Delete, h.Base + "/api/accounts/a@example.com"))).StatusCode);
	}

	[TestMethod]
	public async Task Account_edits_are_409_when_the_accounts_file_is_not_writable()
	{
		await using var h = new Harness(api: true);
		h.Data.AccountsReadOnly = true;
		await h.WaitIdleAfterFirstCycle();

		var patch = await h.Http.SendAsync(Send(HttpMethod.Patch, h.Base + "/api/accounts/a@example.com", """{"libraryScan":false}"""));
		Assert.AreEqual(HttpStatusCode.Conflict, patch.StatusCode);
		Assert.Contains("cannot be saved", await patch.Content.ReadAsStringAsync());
		Assert.AreEqual(HttpStatusCode.Conflict, (await h.Http.SendAsync(Send(HttpMethod.Delete, h.Base + "/api/accounts/a@example.com"))).StatusCode);
	}

	[TestMethod]
	public async Task Account_edits_are_409_without_touching_data_when_accounts_come_from_an_import_file()
	{
		await using var h = new Harness(api: true, accountsImport: Path.Combine(Path.GetTempPath(), "missing-accounts.json"));
		await h.WaitIdleAfterFirstCycle();

		var patch = await h.Http.SendAsync(Send(HttpMethod.Patch, h.Base + "/api/accounts/a@example.com", """{"libraryScan":false}"""));
		Assert.AreEqual(HttpStatusCode.Conflict, patch.StatusCode);
		Assert.Contains("imported accounts file", await patch.Content.ReadAsStringAsync());
		Assert.AreEqual(HttpStatusCode.Conflict, (await h.Http.SendAsync(Send(HttpMethod.Delete, h.Base + "/api/accounts/a@example.com"))).StatusCode);
		Assert.IsFalse(h.Data.Calls.Any(c => c.StartsWith("account:")));
		Assert.HasCount(1, h.Data.Accounts);
	}

	[TestMethod]
	public async Task Scan_with_accounts_scans_only_those_and_validates_the_body()
	{
		await using var h = new Harness(api: true);
		h.Data.Accounts.Add(new("b@example.com", "Second", "uk", true, AuthStates.SignedIn, CredentialKinds.Modern, []));
		await h.WaitIdleAfterFirstCycle();
		var scansBefore = h.ScanCalls;

		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.SendAsync(Send(HttpMethod.Post, h.Base + "/api/scan", """{"accounts":[]}"""))).StatusCode);
		var unknown = await h.Http.SendAsync(Send(HttpMethod.Post, h.Base + "/api/scan", """{"accounts":["nobody@example.com"]}"""));
		Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
		Assert.Contains("nobody@example.com", await unknown.Content.ReadAsStringAsync());
		Assert.IsEmpty(h.ScanSubsets);

		Assert.AreEqual(HttpStatusCode.Accepted, (await h.Http.SendAsync(Send(HttpMethod.Post, h.Base + "/api/scan", """{"accounts":["B@example.com","Main"]}"""))).StatusCode);
		await Eventually(() => Task.FromResult(h.ScanSubsets.Count == 1));
		Assert.AreEqual("B@example.com,Main", h.ScanSubsets[0]);
		Assert.AreEqual(scansBefore, h.ScanCalls);
	}

	[TestMethod]
	public async Task Scan_without_a_body_or_with_null_accounts_scans_everything()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		var before = h.ScanCalls;

		Assert.AreEqual(HttpStatusCode.Accepted, (await h.Http.PostAsync(h.Base + "/api/scan", null)).StatusCode);
		await Eventually(() => Task.FromResult(h.ScanCalls == before + 1));
		await h.WaitIdleAfterFirstCycle();
		Assert.AreEqual(HttpStatusCode.Accepted, (await h.Http.SendAsync(Send(HttpMethod.Post, h.Base + "/api/scan", """{"accounts":null}"""))).StatusCode);
		await Eventually(() => Task.FromResult(h.ScanCalls == before + 2));
		Assert.IsEmpty(h.ScanSubsets);
	}

	[TestMethod]
	public void Account_mapping_never_carries_secrets()
	{
		var account = new AudibleUtilities.Account("a@example.com") { AccountName = "Main", DecryptKey = "deadbeef" };

		var json = JsonSerializer.Serialize(DbEngineData.ToDto(account), new JsonSerializerOptions(JsonSerializerDefaults.Web));

		Assert.DoesNotContain("deadbeef", json);
		Assert.Contains("a@example.com", json);
	}

	[TestMethod]
	public async Task Library_endpoints_clamp_paging_and_404_unknown_asin()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		await h.Http.GetStringAsync(h.Base + "/api/library?offset=-5&limit=99999");
		Assert.AreEqual((0, 1000), h.Data.LastPage);
		await h.Http.GetStringAsync(h.Base + "/api/library");
		Assert.AreEqual((0, 100), h.Data.LastPage);

		Assert.AreEqual(HttpStatusCode.OK, (await h.Http.GetAsync(h.Base + "/api/library/B000000001")).StatusCode);
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.GetAsync(h.Base + "/api/library/nope")).StatusCode);
	}

	private static HttpRequestMessage Authed(HttpMethod method, string url, string? json = null)
	{
		var request = new HttpRequestMessage(method, url);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "s3cret");
		if (json is not null)
			request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
		return request;
	}

	private static async Task<Harness> TokenHarness(LoginSessions? logins = null, string? accountsImport = null)
	{
		var dir = new TempDir();
		var tokenFile = dir.File("token");
		File.WriteAllText(tokenFile, "s3cret\n");
		var h = new Harness(api: true, tokenFile, accountsImport: accountsImport, logins: logins);
		await h.WaitIdleAfterFirstCycle();
		return h;
	}

	private static HttpRequestMessage Send(HttpMethod method, string url, string? json = null)
		=> new(method, url) { Content = json is null ? null : new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

	[TestMethod]
	public async Task Library_passes_query_and_trash_and_reports_the_match_total()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var doc = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/library?q=author%3AAuthor&offset=3&limit=2"));
		Assert.AreEqual(("author:Author", false), h.Data.LastFilter);
		Assert.AreEqual((3, 2), h.Data.LastPage);
		Assert.AreEqual(7, doc.RootElement.GetProperty("total").GetInt32());

		await h.Http.GetStringAsync(h.Base + "/api/library?trash=true");
		Assert.AreEqual((null, true), h.Data.LastFilter);
		await h.Http.GetStringAsync(h.Base + "/api/library");
		Assert.AreEqual((null, false), h.Data.LastFilter);
	}

	[TestMethod]
	public async Task Library_passes_sort_and_direction_and_rejects_bad_values()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		await h.Http.GetStringAsync(h.Base + "/api/library?sort=lastDownload&dir=desc");
		Assert.AreEqual((LibrarySortKey.LastDownload, true), h.Data.LastSort);
		await h.Http.GetStringAsync(h.Base + "/api/library?sort=Title");
		Assert.AreEqual((LibrarySortKey.Title, false), h.Data.LastSort);
		await h.Http.GetStringAsync(h.Base + "/api/library");
		Assert.AreEqual((null, false), h.Data.LastSort);

		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.GetAsync(h.Base + "/api/library?sort=bogus")).StatusCode);
		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.GetAsync(h.Base + "/api/library?sort=title&dir=sideways")).StatusCode);
	}

	[TestMethod]
	public async Task Library_asins_stats_and_detail_are_served()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var asins = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/library/asins?q=x&trash=true"));
		Assert.AreEqual(("x", true), h.Data.LastFilter);
		Assert.AreEqual(1, asins.RootElement.GetProperty("total").GetInt32());
		Assert.AreEqual("B000000001", asins.RootElement.GetProperty("asins")[0].GetString());
		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.GetAsync(h.Base + "/api/library/asins?q=bad%5B")).StatusCode);

		using var stats = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/library/stats"));
		Assert.AreEqual(4, stats.RootElement.GetProperty("liberated").GetInt32());
		Assert.AreEqual(5, stats.RootElement.GetProperty("pdfNotLiberated").GetInt32());

		using var detail = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/library/B000000001"));
		Assert.AreEqual("About", detail.RootElement.GetProperty("description").GetString());
		Assert.AreEqual("Mystery", detail.RootElement.GetProperty("categoryLadders")[0][1].GetString());
		Assert.IsTrue(detail.RootElement.GetProperty("audioFileExists").GetBoolean());
		Assert.AreEqual("PIC1", detail.RootElement.GetProperty("pictureId").GetString());
	}

	[TestMethod]
	public async Task OpenApi_types_result_and_status_as_string_enums_with_the_wire_values()
	{
		using var doc = await OpenApiDocument();
		var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
		string[] Values(string schema, string property)
		{
			var node = schemas.GetProperty(schema).GetProperty("properties").GetProperty(property);
			return node.GetProperty("enum").EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray();
		}

		foreach (var schema in new[] { "FinishedJobDto", "BookFinished" })
		{
			CollectionAssert.Contains(Values(schema, "result"), "failedRetry");
			CollectionAssert.Contains(Values(schema, "result"), "success");
			CollectionAssert.AreEquivalent(new[] { "queued", "cancelled", "working", "completed", "failed" }, Values(schema, "status"));
		}
		CollectionAssert.AreEquivalent(new[] { "working", "queued" }, Values("QueueJobDto", "state"));
		CollectionAssert.AreEquivalent(new[] { "NotLiberated", "Liberated", "Error" }, Values("BookDto", "bookStatus"));
		CollectionAssert.AreEquivalent(new[] { "NotLiberated", "Liberated", "Error" }, Values("BookDto", "pdfStatus"));
	}

	[TestMethod]
	public async Task OpenApi_describes_every_engine_event()
	{
		using var doc = await OpenApiDocument();
		var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
		var events = typeof(EngineEvent).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(EngineEvent)) && !t.IsAbstract).ToList();
		Assert.IsTrue(events.Count > 10);
		foreach (var type in events)
			Assert.IsTrue(schemas.TryGetProperty(type.Name, out _), $"no schema for {type.Name}");

		var oneOf = schemas.GetProperty("EngineEvent").GetProperty("oneOf").EnumerateArray().Select(e => e.GetProperty("$ref").GetString()!).ToList();
		Assert.AreEqual(events.Count, oneOf.Count);
		Assert.IsTrue(oneOf.Contains("#/components/schemas/QueueChanged"));

		var stream = doc.RootElement.GetProperty("paths").GetProperty("/api/events").GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("text/event-stream");
		Assert.AreEqual("#/components/schemas/EngineEvent", stream.GetProperty("schema").GetProperty("$ref").GetString());
		Assert.AreEqual("string", schemas.GetProperty("BookStarted").GetProperty("properties").GetProperty("jobId").GetProperty("type").GetString());
	}

	[TestMethod]
	public async Task Podcast_parents_are_served_with_the_same_filter()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var parents = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/library/podcasts?q=x&trash=true"));
		Assert.AreEqual(("x", true), h.Data.LastFilter);
		Assert.IsTrue(parents.RootElement[0].GetProperty("isPodcastParent").GetBoolean());
		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.GetAsync(h.Base + "/api/library/podcasts?q=bad%5B")).StatusCode);
	}

	[TestMethod]
	[DataRow("csv", "text/csv", ".csv")]
	[DataRow("XLSX", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx")]
	[DataRow("json", "application/json", ".json")]
	public async Task Export_streams_a_file_with_type_name_and_filter(string format, string contentType, string extension)
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var response = await h.Http.GetAsync($"{h.Base}/api/library/export?format={format}&q=x&trash=true");
		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		Assert.AreEqual(contentType, response.Content.Headers.ContentType?.MediaType);
		Assert.AreEqual("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
		StringAssert.StartsWith(response.Content.Headers.ContentDisposition?.FileName, "libation-trash-");
		StringAssert.EndsWith(response.Content.Headers.ContentDisposition?.FileName, extension);
		Assert.AreEqual($"exported {Enum.Parse<ExportFormat>(format, ignoreCase: true)}", await response.Content.ReadAsStringAsync());
		Assert.AreEqual(("x", true), h.Data.LastFilter);
		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (File.Exists(h.Data.LastExportPath) && DateTime.UtcNow < deadline)
			await Task.Delay(20);
		Assert.IsFalse(File.Exists(h.Data.LastExportPath), "the temp file is removed once streamed");
	}

	[TestMethod]
	[DataRow("")]
	[DataRow("?format=pdf")]
	[DataRow("?format=csv&q=bad%5B")]
	public async Task Export_rejects_a_bad_format_or_query(string query)
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.GetAsync($"{h.Base}/api/library/export{query}")).StatusCode);
	}

	[TestMethod]
	public async Task Quick_filters_round_trip_and_reject_a_bad_filter_by_index()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		var url = $"{h.Base}/api/quick-filters";

		var put = await h.Http.PutAsync(url, Body("""{"useDefault":true,"filters":[{"filter":"author:x","name":"X"},{"filter":"tag:y"}]}"""));
		Assert.AreEqual(HttpStatusCode.OK, put.StatusCode);
		var got = JsonDocument.Parse(await h.Http.GetStringAsync(url)).RootElement;
		Assert.IsTrue(got.GetProperty("useDefault").GetBoolean());
		Assert.AreEqual("X", got.GetProperty("filters")[0].GetProperty("name").GetString());
		Assert.AreEqual("tag:y", got.GetProperty("filters")[1].GetProperty("filter").GetString());

		var bad = await h.Http.PutAsync(url, Body("""{"useDefault":false,"filters":[{"filter":"ok"},{"filter":"bad["}]}"""));
		Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode);
		StringAssert.Contains(await bad.Content.ReadAsStringAsync(), "filters[1]");
		Assert.AreEqual(2, h.Data.Filters.Filters.Count, "a rejected list changes nothing");

		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.PutAsync(url, Body("""{"useDefault":false}"""))).StatusCode);
	}

	[TestMethod]
	public async Task Quick_filters_answer_409_when_the_file_is_not_writable()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Data.FiltersReadOnly = true;
		var put = await h.Http.PutAsync($"{h.Base}/api/quick-filters", Body("""{"useDefault":false,"filters":[{"filter":"a"}]}"""));
		Assert.AreEqual(HttpStatusCode.Conflict, put.StatusCode);
	}

	[TestMethod]
	public async Task Cover_serves_cacheable_jpeg_and_answers_304_400_404()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var ok = await h.Http.GetAsync(h.Base + "/api/library/B000000001/cover?size=large");
		Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
		Assert.AreEqual("image/jpeg", ok.Content.Headers.ContentType?.MediaType);
		Assert.AreEqual((CoverSize.Large, "PIC1"), h.Data.LastCover);
		Assert.AreEqual(4, (await ok.Content.ReadAsByteArrayAsync()).Length);
		StringAssert.Contains(ok.Headers.CacheControl!.ToString(), "immutable");
		var etag = ok.Headers.ETag!.Tag;

		var small = await h.Http.GetAsync(h.Base + "/api/library/B000000001/cover");
		Assert.AreEqual(CoverSize.Small, h.Data.LastCover!.Value.Size);
		Assert.AreNotEqual(etag, small.Headers.ETag!.Tag);

		var request = new HttpRequestMessage(HttpMethod.Get, h.Base + "/api/library/B000000001/cover?size=large");
		request.Headers.TryAddWithoutValidation("If-None-Match", etag);
		var notModified = await h.Http.SendAsync(request);
		Assert.AreEqual(HttpStatusCode.NotModified, notModified.StatusCode);

		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.GetAsync(h.Base + "/api/library/B000000001/cover?size=huge")).StatusCode);
		var noCover = await h.Http.GetAsync(h.Base + "/api/library/B000000002/cover");
		Assert.AreEqual(HttpStatusCode.NotFound, noCover.StatusCode);
		Assert.IsNull(noCover.Headers.CacheControl);
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.GetAsync(h.Base + "/api/library/B000000003/cover")).StatusCode);
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.GetAsync(h.Base + "/api/library/nope/cover")).StatusCode);
	}

	[TestMethod]
	public async Task Library_rejects_an_unparseable_query_with_400()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var response = await h.Http.GetAsync(h.Base + "/api/library?q=bad%5B");
		Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
		StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Cannot parse");
	}

	[TestMethod]
	public async Task Setting_tags_replaces_them_and_validates_the_body()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var ok = await h.Http.SendAsync(Send(HttpMethod.Put, h.Base + "/api/library/B000000001/tags", "{\"tags\":\"one two\"}"));
		Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
		using var doc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
		Assert.AreEqual("one two", doc.RootElement.GetProperty("tags").GetString());
		Assert.AreEqual("tags:B000000001:one two", h.Data.Calls.Single());

		Assert.AreEqual(HttpStatusCode.OK, (await h.Http.SendAsync(Send(HttpMethod.Put, h.Base + "/api/library/B000000001/tags", "{\"tags\":\"\"}"))).StatusCode);
		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.SendAsync(Send(HttpMethod.Put, h.Base + "/api/library/B000000001/tags", "{}"))).StatusCode);
		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.SendAsync(Send(HttpMethod.Put, h.Base + "/api/library/B000000001/tags"))).StatusCode);
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.SendAsync(Send(HttpMethod.Put, h.Base + "/api/library/nope/tags", "{\"tags\":\"x\"}"))).StatusCode);
		Assert.AreEqual(3, h.Data.Calls.Count);
	}

	[TestMethod]
	[DataRow("liberated", "Liberated")]
	[DataRow("notLiberated", "NotLiberated")]
	[DataRow(" NOTLIBERATED ", "NotLiberated")]
	public async Task Setting_status_accepts_liberated_and_not_liberated(string wire, string expected)
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var response = await h.Http.SendAsync(Send(HttpMethod.Put, h.Base + "/api/library/B000000001/status", $"{{\"status\":\"{wire}\"}}"));
		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.AreEqual(expected, doc.RootElement.GetProperty("bookStatus").GetString());
		Assert.AreEqual($"status:B000000001:{expected}", h.Data.Calls.Single());
	}

	[TestMethod]
	public async Task Setting_status_validates_the_body_and_404s_unknown_asins()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		foreach (var body in new[] { "{\"status\":\"error\"}", "{\"status\":\"bogus\"}", "{}" })
			Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.SendAsync(Send(HttpMethod.Put, h.Base + "/api/library/B000000001/status", body))).StatusCode, body);
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.SendAsync(Send(HttpMethod.Put, h.Base + "/api/library/nope/status", "{\"status\":\"liberated\"}"))).StatusCode);
		Assert.AreEqual(0, h.Data.Calls.Count(c => c.StartsWith("status:B000000001")));
	}

	[TestMethod]
	public async Task Trash_and_restore_toggle_the_flag_and_404_unknown_asins()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var trashed = await h.Http.PostAsync(h.Base + "/api/library/B000000001/trash", null);
		Assert.AreEqual(HttpStatusCode.OK, trashed.StatusCode);
		using (var doc = JsonDocument.Parse(await trashed.Content.ReadAsStringAsync()))
			Assert.IsTrue(doc.RootElement.GetProperty("inTrash").GetBoolean());

		var restored = await h.Http.PostAsync(h.Base + "/api/library/B000000001/restore", null);
		using (var doc = JsonDocument.Parse(await restored.Content.ReadAsStringAsync()))
			Assert.IsFalse(doc.RootElement.GetProperty("inTrash").GetBoolean());

		CollectionAssert.AreEqual(new[] { "trash:B000000001:True", "trash:B000000001:False" }, h.Data.Calls);
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.PostAsync(h.Base + "/api/library/nope/trash", null)).StatusCode);
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.PostAsync(h.Base + "/api/library/nope/restore", null)).StatusCode);
	}

	[TestMethod]
	public async Task Liberating_one_book_queues_it_and_returns_202_with_the_job_id()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		var release = new TaskCompletionSource();
		h.Outcome = async _ => { await release.Task; return ProcessBookResult.Success; };

		var response = await h.Http.PostAsync(h.Base + "/api/library/B000000001/liberate", null);
		Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
		using var accepted = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.AreEqual("B000000001", accepted.RootElement.GetProperty("asin").GetString());
		Assert.AreEqual("queued", accepted.RootElement.GetProperty("state").GetString());
		var id = accepted.RootElement.GetProperty("id").GetString();

		await Eventually(async () => (await h.QueueJson()).RootElement.GetProperty("jobs").EnumerateArray().Any(j => j.GetProperty("id").GetString() == id));

		Assert.AreEqual(HttpStatusCode.Conflict, (await h.Http.PostAsync(h.Base + "/api/library/B000000001/liberate", null)).StatusCode);

		release.SetResult();
		await h.Queue.Runner!;
		Assert.AreEqual(HttpStatusCode.Accepted, (await h.Http.PostAsync(h.Base + "/api/library/B000000001/liberate", null)).StatusCode);
	}

	[TestMethod]
	public async Task Locating_a_file_returns_the_book_detail_and_maps_failures()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var ok = await h.Http.SendAsync(Send(HttpMethod.Post, h.Base + "/api/library/B000000001/locate", "{\"path\":\"/books/a.m4b\"}"));
		Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
		using (var doc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()))
		{
			Assert.AreEqual("Liberated", doc.RootElement.GetProperty("bookStatus").GetString());
			Assert.IsTrue(doc.RootElement.GetProperty("audioFileExists").GetBoolean());
		}

		foreach (var body in new[] { "{\"path\":\"/etc/passwd\"}", "{}", null })
			Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.SendAsync(Send(HttpMethod.Post, h.Base + "/api/library/B000000001/locate", body))).StatusCode, body);
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.SendAsync(Send(HttpMethod.Post, h.Base + "/api/library/nope/locate", "{\"path\":\"/books/a.m4b\"}"))).StatusCode);
	}

	[TestMethod]
	public async Task Locate_scan_starts_in_the_background_and_streams_progress()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var request = new HttpRequestMessage(HttpMethod.Get, h.Base + "/api/events");
		using var response = await h.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
		using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

		var started = await h.Http.SendAsync(Send(HttpMethod.Post, h.Base + "/api/library/locate-scan", "{\"folder\":\"/books\"}"));
		Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode);
		using (var doc = JsonDocument.Parse(await started.Content.ReadAsStringAsync()))
			Assert.AreEqual("running", doc.RootElement.GetProperty("state").GetString());

		string? current = null, progress = null;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (progress is null)
		{
			var line = await reader.ReadLineAsync(timeout.Token);
			Assert.IsNotNull(line);
			if (line.StartsWith("event: "))
				current = line[7..];
			else if (line.StartsWith("data: ") && current == nameof(LocateScanProgress))
				progress = line[6..];
		}
		Assert.AreEqual("/books", JsonDocument.Parse(progress).RootElement.GetProperty("scan").GetProperty("folder").GetString());

		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.SendAsync(Send(HttpMethod.Post, h.Base + "/api/library/locate-scan", "{\"folder\":\"/etc\"}"))).StatusCode);
		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.SendAsync(Send(HttpMethod.Post, h.Base + "/api/library/locate-scan"))).StatusCode);
		Assert.AreEqual(HttpStatusCode.Conflict, (await h.Http.SendAsync(Send(HttpMethod.Post, h.Base + "/api/library/locate-scan", "{\"folder\":\"/busy\"}"))).StatusCode);

		using var status = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/library/locate-scan"));
		Assert.AreEqual("idle", status.RootElement.GetProperty("state").GetString());
	}

	[TestMethod]
	public async Task Liberating_an_unknown_book_is_404()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.PostAsync(h.Base + "/api/library/nope/liberate", null)).StatusCode);
		Assert.AreEqual(0, h.Queue.Jobs.GetAllItems().Count());
	}

	[TestMethod]
	public async Task Bulk_actions_answer_per_asin_and_dedupe()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var calls = new (HttpMethod Method, string Path, string Extra, string Call)[]
		{
			(HttpMethod.Put, "/api/library/tags", ",\"tags\":\"a b\"", "bulktags:a b||"),
			(HttpMethod.Put, "/api/library/tags", ",\"add\":\"a\",\"remove\":\"b\"", "bulktags:|a|b"),
			(HttpMethod.Put, "/api/library/status", ",\"status\":\"liberated\"", "bulkstatus:Liberated"),
			(HttpMethod.Put, "/api/library/pdf-status", ",\"status\":\"notLiberated\"", "bulkpdf:NotLiberated"),
			(HttpMethod.Post, "/api/library/trash", "", "bulktrash:True"),
			(HttpMethod.Post, "/api/library/restore", "", "bulktrash:False"),
			(HttpMethod.Post, "/api/library/purge", "", "bulkpurge")
		};
		foreach (var (method, path, extra, call) in calls)
		{
			var response = await h.Http.SendAsync(Send(method, h.Base + path, $"{{\"asins\":[\"B000000001\",\"B000000002\",\"nope\",\"B000000001\"]{extra}}}"));
			Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
			using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
			Assert.AreEqual(1, doc.RootElement.GetProperty("updated").GetInt32(), path);
			CollectionAssert.AreEqual(new[] { "nope" }, doc.RootElement.GetProperty("missing").EnumerateArray().Select(e => e.GetString()).ToArray());
			CollectionAssert.AreEqual(new[] { "ok", "conflict", "notFound" }, doc.RootElement.GetProperty("results").EnumerateArray().Select(e => e.GetProperty("result").GetString()).ToArray());
			Assert.AreEqual($"{call}:B000000001,B000000002,nope", h.Data.Calls.Last(), path);
		}
	}

	[TestMethod]
	public async Task Bulk_actions_validate_the_body_and_cap_the_size()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var tooMany = string.Join(',', Enumerable.Range(0, ApiEndpoints.MaxBulk + 1).Select(i => $"\"A{i}\""));
		var atCap = string.Join(',', Enumerable.Range(0, ApiEndpoints.MaxBulk).Select(i => $"\"A{i}\""));
		foreach (var (method, path) in new[] { (HttpMethod.Put, "/api/library/tags"), (HttpMethod.Put, "/api/library/status"), (HttpMethod.Put, "/api/library/pdf-status"), (HttpMethod.Post, "/api/library/trash"), (HttpMethod.Post, "/api/library/restore"), (HttpMethod.Post, "/api/library/purge"), (HttpMethod.Post, "/api/library/liberate") })
		{
			foreach (var body in new[] { null, "{}", "{\"asins\":[]}", $"{{\"asins\":[{tooMany}],\"tags\":\"x\",\"status\":\"liberated\"}}" })
				Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.SendAsync(Send(method, h.Base + path, body))).StatusCode, $"{path} {body?.Length}");
		}

		foreach (var body in new[] { "{\"asins\":[\"B000000001\"]}", "{\"asins\":[\"B000000001\"],\"tags\":\"x\",\"add\":\"y\"}", "{\"asins\":[\"B000000001\"],\"tags\":null}" })
			Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.SendAsync(Send(HttpMethod.Put, h.Base + "/api/library/tags", body))).StatusCode, body);
		foreach (var path in new[] { "/api/library/status", "/api/library/pdf-status" })
			Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.SendAsync(Send(HttpMethod.Put, h.Base + path, "{\"asins\":[\"B000000001\"],\"status\":\"error\"}"))).StatusCode, path);

		Assert.AreEqual(0, h.Data.Calls.Count);
		Assert.AreEqual(HttpStatusCode.OK, (await h.Http.SendAsync(Send(HttpMethod.Post, h.Base + "/api/library/trash", $"{{\"asins\":[{atCap}]}}"))).StatusCode);
	}

	[TestMethod]
	public async Task Bulk_liberate_queues_each_book_and_reports_conflicts_and_unknowns()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		var release = new TaskCompletionSource();
		h.Outcome = async _ => { await release.Task; return ProcessBookResult.Success; };

		var response = await h.Http.SendAsync(Send(HttpMethod.Post, h.Base + "/api/library/liberate", "{\"asins\":[\"B000000001\",\"nope\"]}"));
		Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
		using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
		{
			Assert.AreEqual(1, doc.RootElement.GetProperty("updated").GetInt32());
			CollectionAssert.AreEqual(new[] { "nope" }, doc.RootElement.GetProperty("missing").EnumerateArray().Select(e => e.GetString()).ToArray());
		}
		await Eventually(async () => (await h.QueueJson()).RootElement.GetProperty("jobs").GetArrayLength() == 1);

		using var again = JsonDocument.Parse(await (await h.Http.SendAsync(Send(HttpMethod.Post, h.Base + "/api/library/liberate", "{\"asins\":[\"B000000001\"]}"))).Content.ReadAsStringAsync());
		Assert.AreEqual("conflict", again.RootElement.GetProperty("results")[0].GetProperty("result").GetString());
		Assert.AreEqual(1, h.Queue.Jobs.GetAllItems().Count());

		release.SetResult();
		await h.Queue.Runner!;
	}

	[TestMethod]
	public async Task Book_actions_and_settings_are_404_when_the_api_is_off_and_401_without_the_token()
	{
		await using (var off = new Harness(api: false))
		{
			await off.WaitIdleAfterFirstCycle();
			Assert.AreEqual(HttpStatusCode.NotFound, (await off.Http.GetAsync(off.Base + "/api/settings")).StatusCode);
			Assert.AreEqual(HttpStatusCode.NotFound, (await off.Http.PostAsync(off.Base + "/api/library/B000000001/liberate", null)).StatusCode);
		}

		await using var h = await TokenHarness();
		var calls = new (HttpMethod Method, string Path, string? Body)[]
		{
			(HttpMethod.Get, "/api/library?q=x", null),
			(HttpMethod.Get, "/api/settings", null),
			(HttpMethod.Patch, "/api/settings", "{}"),
			(HttpMethod.Post, "/api/settings/templates/preview", "{\"template\":\"<title>\",\"kind\":\"file\"}"),
			(HttpMethod.Put, "/api/library/B000000001/tags", "{\"tags\":\"x\"}"),
			(HttpMethod.Put, "/api/library/B000000001/status", "{\"status\":\"liberated\"}"),
			(HttpMethod.Post, "/api/library/B000000001/trash", null),
			(HttpMethod.Post, "/api/library/B000000001/restore", null),
			(HttpMethod.Post, "/api/library/B000000001/liberate", null),
			(HttpMethod.Post, "/api/library/purge", "{\"asins\":[\"B000000001\"]}"),
			(HttpMethod.Post, "/api/library/liberate", "{\"asins\":[\"B000000001\"]}")
		};
		foreach (var (method, path, body) in calls)
		{
			Assert.AreEqual(HttpStatusCode.Unauthorized, (await h.Http.SendAsync(Send(method, h.Base + path, body))).StatusCode, path);
			Assert.AreNotEqual(HttpStatusCode.Unauthorized, (await h.Http.SendAsync(Authed(method, h.Base + path, body))).StatusCode, path);
		}
	}

	[TestMethod]
	public async Task Settings_endpoint_returns_the_curated_subset()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var doc = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/settings"));
		var root = doc.RootElement;
		Assert.IsTrue(root.TryGetProperty("books", out _));
		Assert.IsTrue(root.GetProperty("naming").TryGetProperty("folderTemplate", out _));
		Assert.IsTrue(root.GetProperty("download").TryGetProperty("overwriteExisting", out _));
		Assert.IsTrue(root.GetProperty("queue").TryGetProperty("maxConcurrentDownloads", out _));
	}

	private static StringContent Json(string body) => new(body, System.Text.Encoding.UTF8, "application/json");

	[TestMethod]
	public async Task Patch_settings_changes_only_the_named_keys_and_returns_the_updated_settings()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		var books = Path.Combine(configDir, "NewBooks");

		var response = await h.Http.PatchAsync(h.Base + "/api/settings", Json($$"""{"books":{{JsonSerializer.Serialize(books)}},"download":{"createCueSheet":true},"mp3":{"bitrate":96},"logLevel":"Debug"}"""));

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		var root = doc.RootElement;
		Assert.AreEqual(books, root.GetProperty("books").GetString());
		Assert.IsTrue(root.GetProperty("download").GetProperty("createCueSheet").GetBoolean());
		Assert.IsFalse(root.GetProperty("download").GetProperty("retainAaxFile").GetBoolean());
		Assert.AreEqual(96, root.GetProperty("mp3").GetProperty("bitrate").GetInt32());
		Assert.AreEqual(0, root.GetProperty("restartRequired").GetArrayLength());
		Assert.IsTrue(root.GetProperty("folders").GetProperty("books").GetProperty("exists").GetBoolean());
		Assert.IsTrue(Directory.Exists(books));
		Assert.AreEqual(96, Configuration.Instance.LameBitrate);
	}

	[TestMethod]
	public async Task Patch_settings_rejects_the_whole_body_when_any_key_is_invalid()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var response = await h.Http.PatchAsync(h.Base + "/api/settings", Json("""{"download":{"createCueSheet":true},"mp3":{"bitrate":5},"naming":{"fileTemplate":""},"bogus":1,"library":{"importEpisodes":null}}"""));

		Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		StringAssert.Contains(doc.RootElement.GetProperty("error").GetString(), "nothing was changed");
		var fields = doc.RootElement.GetProperty("fields");
		Assert.AreEqual("Unknown setting.", fields.GetProperty("bogus").GetString());
		Assert.AreEqual("Must not be null.", fields.GetProperty("library.importEpisodes").GetString());
		Assert.IsFalse(Configuration.Instance.CreateCueSheet);
	}

	[TestMethod]
	[DataRow("not json")]
	[DataRow("[1]")]
	public async Task Patch_settings_rejects_a_body_that_is_not_an_object(string body)
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var response = await h.Http.PatchAsync(h.Base + "/api/settings", Json(body));

		Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.IsTrue(doc.RootElement.TryGetProperty("fields", out _));
	}

	[TestMethod]
	public async Task Patch_settings_is_409_when_the_config_is_read_only()
	{
		await using var h = new Harness(api: true, readonlyConfig: true);
		await h.WaitIdleAfterFirstCycle();

		var response = await h.Http.PatchAsync(h.Base + "/api/settings", Json("""{"download":{"createCueSheet":true}}"""));

		Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
		StringAssert.Contains(await response.Content.ReadAsStringAsync(), "read-only");
		Assert.IsFalse(Configuration.Instance.CreateCueSheet);
		Assert.AreEqual(HttpStatusCode.OK, (await h.Http.GetAsync(h.Base + "/api/settings")).StatusCode);
	}

	[TestMethod]
	public async Task Template_preview_renders_with_the_sample_book_and_reports_errors()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		async Task<JsonElement> Preview(string body)
		{
			var response = await h.Http.PostAsync(h.Base + "/api/settings/templates/preview", Json(body));
			Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
			return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
		}

		var file = await Preview("""{"template":"<title> - <first author>","kind":"file"}""");
		Assert.IsTrue(file.GetProperty("valid").GetBoolean());
		StringAssert.Contains(file.GetProperty("preview").GetString(), "A Study in Scarlet: A Sherlock Holmes Novel - Arthur Conan Doyle.m4b");

		var title = await Preview("""{"template":"<ch#> - <ch title>","kind":"chapterTitle"}""");
		Assert.IsTrue(title.GetProperty("valid").GetBoolean());

		var bad = await Preview("""{"template":"/abs/<title>","kind":"folder"}""");
		Assert.IsFalse(bad.GetProperty("valid").GetBoolean());
		Assert.IsGreaterThan(0, bad.GetProperty("errors").GetArrayLength());
		Assert.AreEqual(JsonValueKind.Null, bad.GetProperty("preview").ValueKind);

		var warn = await Preview("""{"template":"<title>","kind":"chapterFile"}""");
		Assert.IsTrue(warn.GetProperty("valid").GetBoolean());
		Assert.AreEqual(1, warn.GetProperty("warnings").GetArrayLength());

		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.PostAsync(h.Base + "/api/settings/templates/preview", Json("""{"template":"<title>","kind":"nope"}"""))).StatusCode);
		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.PostAsync(h.Base + "/api/settings/templates/preview", Json("""{"kind":"file"}"""))).StatusCode);
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.PostAsync(h.Base + "/api/settings/templates/preview", Json("""{"template":"<title>","kind":"file","asin":"B0NOSUCH001"}"""))).StatusCode);
	}

	[TestMethod]
	public async Task OpenApi_settings_patch_has_only_optional_fields()
	{
		var schemas = await SchemaDocument();
		foreach (var name in new[] { "SettingsPatchRequest", "NamingPatch", "DownloadPatch", "Mp3Patch", "QueuePatch", "LibraryPatch", "ReplacementPatch" })
			CollectionAssert.AreEqual(Array.Empty<string>(), RequiredOrNone(schemas, name), name);
		CollectionAssert.IsSubsetOf(new[] { "naming", "mp3", "folders", "logLevel", "replacementCharacters" }, Required(schemas, "SettingsDto"));
		CollectionAssert.DoesNotContain(Required(schemas, "SettingsDto"), "restartRequired");
		CollectionAssert.AreEquivalent(new[] { "error", "fields" }, Required(schemas, "FieldErrorsDto"));
		CollectionAssert.AreEquivalent(new[] { "error" }, Required(schemas, "ErrorDto"));
	}

	[TestMethod]
	public void Settings_mapping_never_carries_secrets()
	{
		var c = Configuration.Instance;
		c.AudiobookshelfApiToken = "abs-token-VALUE";
		c.AudiobookshelfServerUrl = "https://abs.example.com/secret-path";
		c.AudiobookshelfLibraryId = "lib-id-VALUE";
		c.AudiobookshelfFolderId = "folder-id-VALUE";

		var json = JsonSerializer.Serialize(SettingsMapper.From(c), new JsonSerializerOptions(JsonSerializerDefaults.Web));

		foreach (var secret in new[] { "abs-token-VALUE", "abs.example.com", "lib-id-VALUE", "folder-id-VALUE" })
			Assert.DoesNotContain(secret, json);
		StringAssert.Contains(json, "\"serverConfigured\":true");
		foreach (var name in typeof(SettingsDto).Assembly.GetTypes().Where(t => t.Name.EndsWith("SettingsDto")).SelectMany(t => t.GetProperties()).Select(p => p.Name))
			Assert.IsFalse(name.Contains("Token") || name.Contains("Key") || name.Contains("Secret") || name.Contains("Password"), name);
	}

	[TestMethod]
	public async Task OpenApi_document_follows_the_api_gate_and_the_token()
	{
		await using (var off = new Harness(api: false))
		{
			await off.WaitIdleAfterFirstCycle();
			Assert.AreEqual(HttpStatusCode.NotFound, (await off.Http.GetAsync(off.Base + "/api/openapi.json")).StatusCode);
		}

		await using var h = await TokenHarness();
		Assert.AreEqual(HttpStatusCode.Unauthorized, (await h.Http.GetAsync(h.Base + "/api/openapi.json")).StatusCode);
		var ok = await h.Http.SendAsync(Authed(HttpMethod.Get, h.Base + "/api/openapi.json"));
		Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
		Assert.AreEqual("application/json", ok.Content.Headers.ContentType?.MediaType);
	}

	private static async Task<JsonElement> SchemaDocument()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		using var doc = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/openapi.json"));
		return doc.RootElement.GetProperty("components").GetProperty("schemas").Clone();
	}

	private static string[] Required(JsonElement schemas, string name)
		=> schemas.GetProperty(name).GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();

	private static string[] RequiredOrNone(JsonElement schemas, string name)
		=> schemas.GetProperty(name).TryGetProperty("required", out var required) ? required.EnumerateArray().Select(e => e.GetString()!).ToArray() : [];

	[TestMethod]
	public async Task OpenApi_requires_exactly_the_non_nullable_properties_without_defaults()
	{
		var schemas = await SchemaDocument();
		foreach (var name in new[] { "BookDto", "BookDetailDto" })
		{
			var required = Required(schemas, name);
			CollectionAssert.Contains(required, "asin");
			CollectionAssert.DoesNotContain(required, "subtitle", name);
			CollectionAssert.DoesNotContain(required, "pdfStatus", name);
			CollectionAssert.DoesNotContain(required, "parentAsin", name);
		}
		CollectionAssert.Contains(Required(schemas, "BookDetailDto"), "isAbridged");
		CollectionAssert.DoesNotContain(Required(schemas, "BookDetailDto"), "description");

		var decision = RequiredOrNone(schemas, "DecisionRequest");
		CollectionAssert.DoesNotContain(decision, "applyToAll");
		CollectionAssert.DoesNotContain(decision, "remember");
		CollectionAssert.DoesNotContain(decision, "decision");
	}

	[TestMethod]
	[DataRow("QueuePaused", "pause", "PendingDecisionDto", "reason")]
	[DataRow("QueueWaiting", "wait", "QueueWaitDto", "reason")]
	[DataRow("LocateScanProgress", "scan", "LocateScanDto", "state")]
	public async Task OpenApi_events_reference_shared_dtos_so_their_enums_survive(string evt, string property, string dto, string enumProperty)
	{
		var schemas = await SchemaDocument();
		var member = schemas.GetProperty(evt).GetProperty("properties").GetProperty(property);
		Assert.AreEqual($"#/components/schemas/{dto}", member.GetProperty("$ref").GetString());
		Assert.IsGreaterThan(0, schemas.GetProperty(dto).GetProperty("properties").GetProperty(enumProperty).GetProperty("enum").GetArrayLength());
	}

	[TestMethod]
	public async Task OpenApi_queue_changed_references_the_queue_dto_whose_jobs_keep_their_state_enum()
	{
		var schemas = await SchemaDocument();
		Assert.AreEqual("#/components/schemas/QueueDto", schemas.GetProperty("QueueChanged").GetProperty("properties").GetProperty("queue").GetProperty("$ref").GetString());
		Assert.IsGreaterThan(0, schemas.GetProperty("QueueJobDto").GetProperty("properties").GetProperty("state").GetProperty("enum").GetArrayLength());
	}


	private const string LoginUrl = "https://www.amazon.com/ap/signin?verifier=SECRETMARKER_VERIFIER";
	private const string GoodResponse = "https://www.amazon.com/ap/maplanding?openid.oa2.authorization_code=SECRETMARKERCODE";

	private sealed class FakeLoginBackend : IExternalLoginBackend
	{
		public FakeData Data = new();
		public readonly List<string> Discarded = [];

		public LoginTarget Resolve(string? accountId, string locale, string? deviceRegistration)
		{
			if (locale == "xx")
				throw new LoginException(400, "Unknown locale.");
			var id = accountId ?? throw new LoginException(400, "accountId is required.");
			return new(id, locale, Data.Accounts.Any(a => a.AccountId == id && a.Locale == locale), deviceRegistration);
		}

		public Task AuthenticateAsync(LoginTarget target, AudibleApi.ILoginExternal login, CancellationToken ct)
		{
			var url = login.GetResponseUrl(LoginUrl, new CookieCollection());
			if (url.Contains("reject"))
				throw new InvalidOperationException("Audible refused " + url);
			if (!target.Exists)
				Data.Accounts.Add(new(target.AccountId, target.AccountId, target.LocaleName, true, AuthStates.SignedIn, CredentialKinds.Modern, []));
			return Task.CompletedTask;
		}

		public void DiscardUnfinished(LoginTarget target) => Discarded.Add(target.AccountId);
	}

	private sealed class LoginRig : IAsyncDisposable
	{
		public readonly FakeLoginBackend Backend = new();
		public readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider Time = new();
		public readonly LoginSessions Sessions;
		public Harness H = null!;
		public string Base => H.Base;
		public HttpClient Http => H.Http;

		public LoginRig() => Sessions = new LoginSessions(Backend, Time);

		public static async Task<LoginRig> Create(string? accountsImport = null)
		{
			var rig = new LoginRig();
			rig.H = await TokenHarness(rig.Sessions, accountsImport);
			rig.Backend.Data = rig.H.Data;
			return rig;
		}

		public async Task<(HttpStatusCode Status, JsonElement Body)> Call(HttpMethod method, string path, string? json = null)
		{
			var response = await Http.SendAsync(Authed(method, Base + path, json));
			var text = await response.Content.ReadAsStringAsync();
			return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
		}

		public async Task<string> Start(string account = "new@example.com")
		{
			var (status, body) = await Call(HttpMethod.Post, "/api/accounts/login", $$"""{"accountId":"{{account}}","locale":"us"}""");
			Assert.AreEqual(HttpStatusCode.OK, status);
			return body.GetProperty("loginId").GetString()!;
		}

		public ValueTask DisposeAsync() => H.DisposeAsync();
	}

	private static string Complete(string url = GoodResponse) => JsonSerializer.Serialize(new { responseUrl = url });

	[TestMethod]
	public async Task Login_routes_are_403_without_an_api_token_and_name_the_flag()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		foreach (var (method, path, body) in new[]
		{
			(HttpMethod.Post, "/api/accounts/login", """{"locale":"us","accountId":"a"}"""),
			(HttpMethod.Post, "/api/accounts/login/abc", Complete()),
			(HttpMethod.Delete, "/api/accounts/login/abc", null)
		})
		{
			var response = await h.Http.SendAsync(Send(method, h.Base + path, body));
			Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, path);
			StringAssert.Contains(await response.Content.ReadAsStringAsync(), "--api-token-file");
		}
	}

	[TestMethod]
	public async Task Login_start_returns_the_url_and_completing_it_adds_the_account_once()
	{
		await using var rig = await LoginRig.Create();
		var (status, started) = await rig.Call(HttpMethod.Post, "/api/accounts/login", """{"accountId":"new@example.com","locale":"us"}""");
		Assert.AreEqual(HttpStatusCode.OK, status);
		CollectionAssert.AreEquivalent(new[] { "loginId", "url", "expiresAt" }, started.EnumerateObject().Select(p => p.Name).ToArray());
		Assert.AreEqual(LoginUrl, started.GetProperty("url").GetString());
		var id = started.GetProperty("loginId").GetString()!;
		Assert.AreEqual(1, rig.Sessions.PendingCount);

		var (done, account) = await rig.Call(HttpMethod.Post, $"/api/accounts/login/{id}", Complete());
		Assert.AreEqual(HttpStatusCode.OK, done);
		Assert.AreEqual("new@example.com", account.GetProperty("accountId").GetString());
		Assert.AreEqual("signedIn", account.GetProperty("authState").GetString());
		StringAssert.DoesNotMatch(account.GetRawText(), new System.Text.RegularExpressions.Regex("SECRETMARKER"));
		Assert.AreEqual(0, rig.Sessions.PendingCount);

		Assert.AreEqual(HttpStatusCode.NotFound, (await rig.Call(HttpMethod.Post, $"/api/accounts/login/{id}", Complete())).Status);
	}

	[TestMethod]
	public async Task Login_with_a_bad_response_url_is_400_and_stays_usable()
	{
		await using var rig = await LoginRig.Create();
		var id = await rig.Start();
		foreach (var bad in new[] { "", "not a url", "http://www.amazon.com/ap/maplanding?openid.oa2.authorization_code=x", "https://www.amazon.com/ap/signin" })
		{
			var (status, body) = await rig.Call(HttpMethod.Post, $"/api/accounts/login/{id}", Complete(bad));
			Assert.AreEqual(HttpStatusCode.BadRequest, status, bad);
			Assert.AreEqual("notFinalUrl", body.GetProperty("code").GetString());
			Assert.IsTrue(body.GetProperty("retryable").GetBoolean());
			if (bad.Length > 0)
				StringAssert.DoesNotMatch(body.GetRawText(), new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(bad)));
		}
		Assert.AreEqual(HttpStatusCode.BadRequest, (await rig.Call(HttpMethod.Post, $"/api/accounts/login/{id}")).Status);
		Assert.AreEqual(HttpStatusCode.OK, (await rig.Call(HttpMethod.Post, $"/api/accounts/login/{id}", Complete())).Status);
	}

	[TestMethod]
	public async Task Login_rejected_by_audible_is_400_consumed_and_cleaned_up()
	{
		await using var rig = await LoginRig.Create();
		var id = await rig.Start();
		var (status, body) = await rig.Call(HttpMethod.Post, $"/api/accounts/login/{id}", Complete("https://www.amazon.com/ap/maplanding?openid.oa2.authorization_code=reject"));
		Assert.AreEqual(HttpStatusCode.BadRequest, status);
		Assert.AreEqual("rejected", body.GetProperty("code").GetString());
		Assert.IsFalse(body.GetProperty("retryable").GetBoolean());
		StringAssert.DoesNotMatch(body.GetRawText(), new System.Text.RegularExpressions.Regex("authorization_code"));
		Assert.AreEqual(HttpStatusCode.NotFound, (await rig.Call(HttpMethod.Post, $"/api/accounts/login/{id}", Complete())).Status);
		CollectionAssert.AreEqual(new[] { "new@example.com" }, rig.Backend.Discarded);
	}

	[TestMethod]
	public async Task Login_expires_after_ten_minutes()
	{
		await using var rig = await LoginRig.Create();
		var id = await rig.Start();
		rig.Time.Advance(TimeSpan.FromMinutes(9));
		Assert.AreEqual(1, rig.Sessions.PendingCount);
		rig.Time.Advance(TimeSpan.FromMinutes(2));
		Assert.AreEqual(HttpStatusCode.NotFound, (await rig.Call(HttpMethod.Post, $"/api/accounts/login/{id}", Complete())).Status);
		Assert.AreEqual(0, rig.Sessions.PendingCount);
		Assert.AreEqual(1, rig.Backend.Discarded.Count);
		Assert.IsFalse(rig.H.Data.Accounts.Any(a => a.AccountId == "new@example.com"));
	}

	[TestMethod]
	public async Task Login_caps_pending_sign_ins_and_cancel_frees_a_slot()
	{
		await using var rig = await LoginRig.Create();
		var ids = new List<string>();
		for (var i = 0; i < LoginSessions.MaxPending; i++)
			ids.Add(await rig.Start($"user{i}@example.com"));

		var (status, _) = await rig.Call(HttpMethod.Post, "/api/accounts/login", """{"accountId":"extra@example.com","locale":"us"}""");
		Assert.AreEqual(HttpStatusCode.TooManyRequests, status);

		var (cancelled, body) = await rig.Call(HttpMethod.Delete, $"/api/accounts/login/{ids[0]}");
		Assert.AreEqual(HttpStatusCode.OK, cancelled);
		Assert.AreEqual(ids[0], body.GetProperty("loginId").GetString());
		Assert.AreEqual(HttpStatusCode.NotFound, (await rig.Call(HttpMethod.Delete, $"/api/accounts/login/{ids[0]}")).Status);
		Assert.AreEqual(HttpStatusCode.NotFound, (await rig.Call(HttpMethod.Post, $"/api/accounts/login/{ids[0]}", Complete())).Status);
		await rig.Start("extra@example.com");
	}

	[TestMethod]
	public async Task Login_start_validates_the_request_and_replaces_a_pending_login_for_the_same_account()
	{
		await using var rig = await LoginRig.Create();
		Assert.AreEqual(HttpStatusCode.BadRequest, (await rig.Call(HttpMethod.Post, "/api/accounts/login", """{"accountId":"a"}""")).Status);
		Assert.AreEqual(HttpStatusCode.BadRequest, (await rig.Call(HttpMethod.Post, "/api/accounts/login", """{"accountId":"a","locale":"xx"}""")).Status);
		Assert.AreEqual(HttpStatusCode.BadRequest, (await rig.Call(HttpMethod.Post, "/api/accounts/login", """{"locale":"us"}""")).Status);

		var first = await rig.Start();
		var second = await rig.Start();
		Assert.AreEqual(1, rig.Sessions.PendingCount);
		Assert.AreEqual(HttpStatusCode.NotFound, (await rig.Call(HttpMethod.Post, $"/api/accounts/login/{first}", Complete())).Status);
		Assert.AreEqual(HttpStatusCode.OK, (await rig.Call(HttpMethod.Post, $"/api/accounts/login/{second}", Complete())).Status);
	}

	[TestMethod]
	public async Task Login_in_import_mode_allows_existing_accounts_and_refuses_new_ones()
	{
		await using var rig = await LoginRig.Create(accountsImport: Path.Combine(Path.GetTempPath(), "missing-accounts.json"));

		var (refused, body) = await rig.Call(HttpMethod.Post, "/api/accounts/login", """{"accountId":"new@example.com","locale":"us"}""");
		Assert.AreEqual(HttpStatusCode.Conflict, refused);
		StringAssert.Contains(body.GetProperty("error").GetString(), "imported accounts file");

		var id = await rig.Start("a@example.com");
		var (status, account) = await rig.Call(HttpMethod.Post, $"/api/accounts/login/{id}", Complete());
		Assert.AreEqual(HttpStatusCode.OK, status);
		Assert.AreEqual("a@example.com", account.GetProperty("accountId").GetString());
	}

	[TestMethod]
	public async Task Login_never_puts_the_url_verifier_or_response_in_a_log_event_or_a_cached_response()
	{
		var sink = new CaptureSink();
		var previous = Serilog.Log.Logger;
		Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
		try
		{
			await using var rig = await LoginRig.Create();
			var bad = "https://www.amazon.com/ap/maplanding?openid.oa2.authorization_code=reject_SECRETMARKER";
			var first = await rig.Start();
			await rig.Call(HttpMethod.Post, $"/api/accounts/login/{first}", Complete("not-a-url-SECRETMARKER"));
			await rig.Call(HttpMethod.Post, $"/api/accounts/login/{first}", Complete(bad));
			var second = await rig.Start("b@example.com");
			await rig.Call(HttpMethod.Post, $"/api/accounts/login/{second}", Complete());
			var third = await rig.Start("c@example.com");
			await rig.Call(HttpMethod.Delete, $"/api/accounts/login/{third}");
			await rig.Call(HttpMethod.Post, "/api/accounts/login", "{not json SECRETMARKER");

			var response = await rig.Http.SendAsync(Authed(HttpMethod.Post, rig.Base + "/api/accounts/login", """{"accountId":"d@example.com","locale":"us"}"""));
			Assert.AreEqual("no-store", response.Headers.CacheControl?.ToString());
		}
		finally
		{
			Serilog.Log.Logger = previous;
		}

		Assert.IsGreaterThan(0, sink.Events.Count, "the capture saw the failed sign-in warning");
		foreach (var rendered in sink.Events)
		{
			StringAssert.DoesNotMatch(rendered, new System.Text.RegularExpressions.Regex("SECRETMARKER|authorization_code|verifier|reject"));
		}
	}

	private sealed class CaptureSink : Serilog.Core.ILogEventSink
	{
		public readonly System.Collections.Concurrent.ConcurrentBag<string> Events = [];

		public void Emit(Serilog.Events.LogEvent logEvent)
			=> Events.Add(logEvent.RenderMessage() + " " + string.Join(' ', logEvent.Properties.Select(p => $"{p.Key}={p.Value}")) + " " + logEvent.Exception);
	}

	[TestMethod]
	public async Task OpenApi_document_lists_every_route_with_a_typed_success_response()
	{
		using var dir = new TempDir();
		var port = ((IPEndPoint)new Func<IPEndPoint>(() =>
		{
			var l = new TcpListener(IPAddress.Loopback, 0);
			l.Start();
			var ep = (IPEndPoint)l.LocalEndpoint;
			l.Stop();
			return ep;
		})()).Port;
		var options = new DaemonOptions(TimeSpan.FromMinutes(30), false, $"http://127.0.0.1:{port}", dir.Path, ApiEnabled: true);
		var state = new CycleState();
		var scheduler = new CycleScheduler(options, new DaemonJobs(_ => Task.FromResult(0), _ => Task.FromResult(0), _ => Task.FromResult(0)), state);
		var queue = new LiberationQueue(new LiberationPolicy { MaxConcurrentDownloads = () => 1 });
		await using var app = DaemonHost.Build(options, state, scheduler, queue, new FakeData());
		await app.StartAsync();
		using var http = new HttpClient();

		using var doc = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/api/openapi.json"));
		var documented = new SortedSet<string>();
		var operationIds = new List<string>();
		foreach (var path in doc.RootElement.GetProperty("paths").EnumerateObject())
		{
			foreach (var operation in path.Value.EnumerateObject())
			{
				documented.Add($"{operation.Name.ToUpperInvariant()} {path.Name}");
				operationIds.Add(operation.Value.GetProperty("operationId").GetString()!);
				var success = operation.Value.GetProperty("responses").EnumerateObject().Single(r => r.Name is "200" or "202");
				Assert.IsTrue(success.Value.GetProperty("content").EnumerateObject().Any(), $"{operation.Name} {path.Name} has no typed success response");
			}
		}

		var routed = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
			.OfType<RouteEndpoint>()
			.Where(e => e.RoutePattern.RawText!.StartsWith("/api/") && e.RoutePattern.RawText != "/api/openapi.json")
			.SelectMany(e => e.Metadata.GetRequiredMetadata<HttpMethodMetadata>().HttpMethods
				.Select(m => $"{m} {System.Text.RegularExpressions.Regex.Replace(e.RoutePattern.RawText!, @":[a-z]+(?=\})", "")}"))
			.ToImmutableSortedSet();

		Assert.IsGreaterThan(10, routed.Count);
		CollectionAssert.AreEqual(routed.ToList(), documented.ToList());

		var paths = doc.RootElement.GetProperty("paths");
		Assert.IsTrue(paths.GetProperty("/api/library/{asin}/cover").GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content").TryGetProperty("image/jpeg", out _));
		var sortParam = paths.GetProperty("/api/library").GetProperty("get").GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "sort");
		Assert.AreEqual(10, sortParam.GetProperty("schema").GetProperty("enum").GetArrayLength());
		Assert.AreEqual(operationIds.Count, operationIds.Distinct().Count());
		await app.StopAsync();
	}

	private static async Task<JsonDocument> OpenApiDocument()
	{
		using var dir = new TempDir();
		var l = new TcpListener(IPAddress.Loopback, 0);
		l.Start();
		var port = ((IPEndPoint)l.LocalEndpoint).Port;
		l.Stop();
		var options = new DaemonOptions(TimeSpan.FromMinutes(30), false, $"http://127.0.0.1:{port}", dir.Path, ApiEnabled: true);
		var state = new CycleState();
		var scheduler = new CycleScheduler(options, new DaemonJobs(_ => Task.FromResult(0), _ => Task.FromResult(0), _ => Task.FromResult(0)), state);
		var queue = new LiberationQueue(new LiberationPolicy { MaxConcurrentDownloads = () => 1 });
		await using var app = DaemonHost.Build(options, state, scheduler, queue, new FakeData());
		await app.StartAsync();
		using var http = new HttpClient();
		var doc = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/api/openapi.json"));
		await app.StopAsync();
		return doc;
	}

	[TestMethod]
	public async Task OpenApi_marks_non_nullable_dto_properties_required()
	{
		using var doc = await OpenApiDocument();
		var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
		string[] Required(string name) => schemas.GetProperty(name).GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();

		foreach (var name in new[] { "BookDto", "BookDetailDto" })
			CollectionAssert.IsSubsetOf(new[] { "asin", "title", "authors", "bookStatus", "dateAdded", "inTrash", "partialDownload" }, Required(name));
		CollectionAssert.IsSubsetOf(new[] { "audioFileExists", "categoryLadders", "isAbridged" }, Required("BookDetailDto"));
	}

	[TestMethod]
	[DataRow(null, null, 0, 100)]
	[DataRow(-1, 0, 0, 1)]
	[DataRow(40, 5000, 40, 1000)]
	[DataRow(10, 25, 10, 25)]
	public void Paging_clamps(int? offset, int? limit, int expectedOffset, int expectedLimit)
		=> Assert.AreEqual((expectedOffset, expectedLimit), LibraryPaging.Clamp(offset, limit));

	[TestMethod]
	[DataRow("http://127.0.0.1:8080", true)]
	[DataRow("http://localhost:8080", true)]
	[DataRow("http://[::1]:8080", true)]
	[DataRow("http://0.0.0.0:8080", false)]
	[DataRow("http://*:8080", false)]
	[DataRow("http://192.168.1.5:8080", false)]
	public void Loopback_detection(string url, bool loopback)
		=> Assert.AreEqual(loopback, ListenUrl.IsLoopback(url));

	[TestMethod]
	public async Task Queue_endpoints_follow_the_api_gate_and_the_token()
	{
		await using (var off = new Harness(api: false))
		{
			await off.WaitIdleAfterFirstCycle();
			Assert.AreEqual(HttpStatusCode.NotFound, (await off.Http.GetAsync(off.Base + "/api/queue")).StatusCode);
			Assert.AreEqual(HttpStatusCode.NotFound, (await off.Decide(1, "{\"decision\":\"continue\"}")).StatusCode);
		}

		using var dir = new TempDir();
		var tokenFile = dir.File("token");
		File.WriteAllText(tokenFile, "s3cret\n");
		await using var h = new Harness(api: true, tokenFile);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.Unauthorized, (await h.Http.GetAsync(h.Base + "/api/queue")).StatusCode);
		Assert.AreEqual(HttpStatusCode.Unauthorized, (await h.Decide(1, "{\"decision\":\"continue\"}")).StatusCode);
	}

	[TestMethod]
	public async Task Queue_is_empty_when_idle()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var doc = await h.QueueJson();
		Assert.AreEqual("{\"running\":false,\"jobs\":[],\"pending\":[],\"waiting\":null,\"paused\":false,\"stats\":{\"queued\":0,\"completed\":0,\"errors\":0,\"runningSince\":null}}", doc.RootElement.GetRawText());
	}

	[TestMethod]
	public async Task Queue_lists_working_and_queued_jobs_with_their_states()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		var release = new TaskCompletionSource();
		h.Outcome = async _ => { await release.Task; return ProcessBookResult.Success; };
		var a = h.Job("B000000001");
		var b = h.Job("B000000002");

		h.Enqueue(a, b);
		await Eventually(async () => (await h.QueueJson()).RootElement.GetProperty("jobs").EnumerateArray().Any(j => j.GetProperty("state").GetString() == "working"));

		using var doc = await h.QueueJson();
		var jobs = doc.RootElement.GetProperty("jobs");
		Assert.IsTrue(doc.RootElement.GetProperty("running").GetBoolean());
		Assert.AreEqual(a.Id.Value, jobs[0].GetProperty("id").GetString());
		Assert.AreEqual("B000000001", jobs[0].GetProperty("asin").GetString());
		Assert.AreEqual("Title B000000001", jobs[0].GetProperty("title").GetString());
		CollectionAssert.AreEqual(new[] { "working", "queued" }, jobs.EnumerateArray().Select(j => j.GetProperty("state").GetString()).ToArray());

		release.SetResult();
		await h.Queue.Runner!;
	}

	[TestMethod]
	public async Task A_bad_book_pause_is_listed_and_answered_over_the_api()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Outcome = _ => Task.FromResult(ProcessBookResult.AwaitingDecision);
		var job = h.Job("B000000001");

		h.Enqueue(job);
		await Eventually(async () => (await h.QueueJson()).RootElement.GetProperty("pending").GetArrayLength() == 1);

		long id;
		using (var doc = await h.QueueJson())
		{
			var pause = doc.RootElement.GetProperty("pending")[0];
			id = pause.GetProperty("id").GetInt64();
			Assert.AreEqual("badBook", pause.GetProperty("reason").GetString());
			Assert.AreEqual(job.Id.Value, pause.GetProperty("jobId").GetString());
			CollectionAssert.AreEqual(new[] { "badBook" }, pause.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()).ToArray());
		}

		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Decide(id, "{\"decision\":\"explode\"}")).StatusCode);
		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Decide(id, "{\"decision\":\"badBook\"}")).StatusCode);
		Assert.AreEqual(HttpStatusCode.Conflict, (await h.Decide(id, "{\"decision\":\"continue\"}")).StatusCode);
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Decide(id + 100, "{\"decision\":\"badBook\",\"action\":\"retry\"}")).StatusCode);
		Assert.AreEqual(1, h.Queue.Pending.Count, "A refused answer must leave the pause in place.");

		var ok = await h.Decide(id, "{\"decision\":\"badBook\",\"action\":\"retry\",\"applyToAll\":true}");
		Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
		await h.Queue.Runner!.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.AreEqual(ProcessBookResult.FailedRetry, job.Result);

		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Decide(id, "{\"decision\":\"badBook\",\"action\":\"retry\"}")).StatusCode, "A stale id must be refused.");
	}

	[TestMethod]
	public async Task A_disk_full_wait_can_be_resumed_over_the_api_but_the_daily_limit_wait_cannot()
	{
		await using var h = new Harness(api: true, onDiskFull: DiskFullPolicy.WaitForSpace);
		await h.WaitIdleAfterFirstCycle();
		var calls = 0;
		h.Outcome = _ => Task.FromResult(Interlocked.Increment(ref calls) == 1 ? ProcessBookResult.DiskFull : ProcessBookResult.Success);

		h.Enqueue(h.Job("B000000001"));
		await Eventually(async () => (await h.QueueJson()).RootElement.GetProperty("waiting").ValueKind == JsonValueKind.Object);

		long id;
		using (var doc = await h.QueueJson())
		{
			var wait = doc.RootElement.GetProperty("waiting");
			id = wait.GetProperty("id").GetInt64();
			Assert.AreEqual("diskFull", wait.GetProperty("reason").GetString());
			CollectionAssert.AreEqual(new[] { "resume", "stop" }, wait.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()).ToArray());
		}

		Assert.AreEqual(HttpStatusCode.Conflict, (await h.Decide(id, "{\"decision\":\"continue\"}")).StatusCode);
		Assert.AreEqual(HttpStatusCode.OK, (await h.Decide(id, "{\"decision\":\"resume\"}")).StatusCode);
		await h.Queue.Runner!.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.AreEqual(2, calls);
	}

	[TestMethod]
	public async Task Events_stream_delivers_book_and_queue_events()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var request = new HttpRequestMessage(HttpMethod.Get, h.Base + "/api/events");
		using var response = await h.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
		using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

		h.Outcome = _ => Task.FromResult(ProcessBookResult.AwaitingDecision);
		var job = h.Job("B000000001");
		h.Enqueue(job);

		var data = new Dictionary<string, string>();
		string? current = null;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (!data.ContainsKey(nameof(QueuePaused)))
		{
			var line = await reader.ReadLineAsync(timeout.Token);
			Assert.IsNotNull(line);
			if (line.StartsWith("event: "))
				current = line[7..];
			else if (line.StartsWith("data: ") && current is not null)
				data[current] = line[6..];
		}

		Assert.AreEqual(job.Id.Value, JsonDocument.Parse(data[nameof(BookStarted)]).RootElement.GetProperty("jobId").GetString());
		Assert.AreEqual("B000000001", JsonDocument.Parse(data[nameof(BookStarted)]).RootElement.GetProperty("asin").GetString());
		var pause = JsonDocument.Parse(data[nameof(QueuePaused)]).RootElement.GetProperty("pause");
		Assert.AreEqual("badBook", pause.GetProperty("reason").GetString());

		Assert.IsTrue(h.Queue.Resume(h.Queue.Pending[0].Id, Decision.BadBook(Configuration.BadBookAction.Retry)));
		while (!data.ContainsKey(nameof(BookFinished)))
		{
			var line = await reader.ReadLineAsync(timeout.Token);
			Assert.IsNotNull(line);
			if (line.StartsWith("event: "))
				current = line[7..];
			else if (line.StartsWith("data: ") && current is not null)
				data[current] = line[6..];
		}

		using var finished = JsonDocument.Parse(data[nameof(BookFinished)]);
		Assert.AreEqual("failedRetry", finished.RootElement.GetProperty("result").GetString());
		Assert.AreEqual("failed", finished.RootElement.GetProperty("status").GetString());
		Assert.IsTrue(data.ContainsKey(nameof(QueueResumed)));
		Assert.IsFalse(data.Values.Any(v => v.Contains("token", StringComparison.OrdinalIgnoreCase)));
	}

	private static StringContent Body(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

	private async Task<(HttpStatusCode Status, JsonDocument Body)> Call(Harness h, HttpMethod method, string path, string? json = null)
	{
		using var request = new HttpRequestMessage(method, h.Base + path) { Content = json is null ? null : Body(json) };
		using var response = await h.Http.SendAsync(request);
		return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()));
	}

	private static async Task<string> FirstWorkingJobId(Harness h)
	{
		string? id = null;
		await Eventually(async () =>
		{
			using var doc = await h.QueueJson();
			id = doc.RootElement.GetProperty("jobs").EnumerateArray().FirstOrDefault(j => j.GetProperty("state").GetString() == "working").ValueKind == JsonValueKind.Object
				? doc.RootElement.GetProperty("jobs").EnumerateArray().First(j => j.GetProperty("state").GetString() == "working").GetProperty("id").GetString()
				: null;
			return id is not null;
		});
		return id!;
	}

	[TestMethod]
	public async Task Cancelling_a_queued_job_removes_it_and_a_running_one_ends_cancelled()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Outcome = async job =>
		{
			while (!job.CancellationRequested)
				await Task.Delay(5);
			return ProcessBookResult.Cancelled;
		};
		LiberationJob a = h.Job("A"), b = h.Job("B");
		h.Enqueue(a, b);
		await FirstWorkingJobId(h);

		var (removed, removedBody) = await Call(h, HttpMethod.Delete, $"/api/queue/jobs/{b.Id.Value}");
		Assert.AreEqual(HttpStatusCode.OK, removed);
		Assert.AreEqual("removed", removedBody.RootElement.GetProperty("outcome").GetString());

		var (cancelling, cancellingBody) = await Call(h, HttpMethod.Delete, $"/api/queue/jobs/{a.Id.Value}");
		Assert.AreEqual(HttpStatusCode.OK, cancelling);
		Assert.AreEqual("cancelling", cancellingBody.RootElement.GetProperty("outcome").GetString());
		await h.Queue.Runner!.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.AreEqual(ProcessBookResult.Cancelled, a.Result);
		var (finished, _) = await Call(h, HttpMethod.Delete, $"/api/queue/jobs/{a.Id.Value}");
		Assert.AreEqual(HttpStatusCode.NotFound, finished);
		var (unknown, _) = await Call(h, HttpMethod.Delete, "/api/queue/jobs/nope");
		Assert.AreEqual(HttpStatusCode.NotFound, unknown);
	}

	[TestMethod]
	public async Task Cancel_all_clears_the_queue_and_cancels_what_is_running()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Outcome = async job =>
		{
			while (!job.CancellationRequested)
				await Task.Delay(5);
			return ProcessBookResult.Cancelled;
		};
		LiberationJob a = h.Job("A");
		h.Enqueue(a, h.Job("B"), h.Job("C"));
		await FirstWorkingJobId(h);

		var (status, body) = await Call(h, HttpMethod.Post, "/api/queue/cancel");
		await h.Queue.Runner!.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.AreEqual(HttpStatusCode.OK, status);
		Assert.IsFalse(body.RootElement.GetProperty("jobs").EnumerateArray().Any(j => j.GetProperty("state").GetString() == "queued"));
		Assert.AreEqual(ProcessBookResult.Cancelled, a.Result);
		using var after = await h.QueueJson();
		Assert.AreEqual(0, after.RootElement.GetProperty("jobs").GetArrayLength());
	}

	[TestMethod]
	public async Task Moving_a_job_reorders_the_queue_and_refuses_what_it_cannot_move()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		var gate = new TaskCompletionSource();
		h.Outcome = async _ => { await gate.Task; return ProcessBookResult.Success; };
		LiberationJob a = h.Job("A"), b = h.Job("B"), c = h.Job("C");
		h.Enqueue(a, b, c);
		await FirstWorkingJobId(h);

		var (ok, body) = await Call(h, HttpMethod.Post, $"/api/queue/jobs/{c.Id.Value}/move", "{\"to\":\"top\"}");
		Assert.AreEqual(HttpStatusCode.OK, ok);
		CollectionAssert.AreEqual(new[] { "A", "C", "B" }, body.RootElement.GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("asin").GetString()).ToArray());

		Assert.AreEqual(HttpStatusCode.Conflict, (await Call(h, HttpMethod.Post, $"/api/queue/jobs/{a.Id.Value}/move", "{\"to\":\"top\"}")).Status);
		Assert.AreEqual(HttpStatusCode.NotFound, (await Call(h, HttpMethod.Post, "/api/queue/jobs/nope/move", "{\"to\":\"top\"}")).Status);
		Assert.AreEqual(HttpStatusCode.BadRequest, (await Call(h, HttpMethod.Post, $"/api/queue/jobs/{b.Id.Value}/move", "{\"to\":\"left\"}")).Status);
		Assert.AreEqual(HttpStatusCode.BadRequest, (await Call(h, HttpMethod.Post, $"/api/queue/jobs/{b.Id.Value}/move")).Status);

		gate.SetResult();
		await h.Queue.Runner!.WaitAsync(TimeSpan.FromSeconds(10));
	}

	[TestMethod]
	public async Task Pause_holds_new_jobs_until_resume_and_shows_in_the_queue()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var (paused, pausedBody) = await Call(h, HttpMethod.Post, "/api/queue/pause");
		Assert.IsTrue(pausedBody.RootElement.GetProperty("paused").GetBoolean());
		Assert.AreEqual(HttpStatusCode.OK, paused);
		var started = new List<string>();
		h.Outcome = job => { lock (started) started.Add(job.LibraryBook.Book.AudibleProductId); return Task.FromResult(ProcessBookResult.Success); };
		h.Enqueue(h.Job("A"));
		await Task.Delay(150);

		using (var doc = await h.QueueJson())
		{
			Assert.IsTrue(doc.RootElement.GetProperty("paused").GetBoolean());
			Assert.AreEqual(1, doc.RootElement.GetProperty("stats").GetProperty("queued").GetInt32());
		}
		Assert.AreEqual(0, started.Count);

		var (resumed, resumedBody) = await Call(h, HttpMethod.Post, "/api/queue/resume");
		Assert.AreEqual(HttpStatusCode.OK, resumed);
		Assert.IsFalse(resumedBody.RootElement.GetProperty("paused").GetBoolean());
		await h.Queue.Runner!.WaitAsync(TimeSpan.FromSeconds(10));
		CollectionAssert.AreEqual(new[] { "A" }, started);
	}

	[TestMethod]
	public async Task History_lists_finished_jobs_and_delete_clears_it_with_the_counters()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Outcome = job => Task.FromResult(job.LibraryBook.Book.AudibleProductId == "B" ? ProcessBookResult.FailedRetry : ProcessBookResult.Success);
		h.Enqueue(h.Job("A"), h.Job("B"));
		await h.Queue.Runner!.WaitAsync(TimeSpan.FromSeconds(10));

		var (status, history) = await Call(h, HttpMethod.Get, "/api/queue/history");
		Assert.AreEqual(HttpStatusCode.OK, status);
		var items = history.RootElement.EnumerateArray().ToList();
		CollectionAssert.AreEqual(new[] { "B", "A" }, items.Select(j => j.GetProperty("asin").GetString()).ToArray());
		Assert.AreEqual("failedRetry", items[0].GetProperty("result").GetString());
		Assert.AreEqual("failed", items[0].GetProperty("status").GetString());
		Assert.AreEqual(JsonValueKind.String, items[0].GetProperty("finishedAt").ValueKind);
		using (var doc = await h.QueueJson())
		{
			var stats = doc.RootElement.GetProperty("stats");
			Assert.AreEqual((1, 1), (stats.GetProperty("completed").GetInt32(), stats.GetProperty("errors").GetInt32()));
		}

		var (cleared, _) = await Call(h, HttpMethod.Delete, "/api/queue/history");
		Assert.AreEqual(HttpStatusCode.OK, cleared);
		var (_, empty) = await Call(h, HttpMethod.Get, "/api/queue/history");
		Assert.AreEqual(0, empty.RootElement.GetArrayLength());
		using var after = await h.QueueJson();
		Assert.AreEqual(0, after.RootElement.GetProperty("stats").GetProperty("completed").GetInt32());
	}

	[TestMethod]
	public async Task A_job_log_is_served_while_running_and_after_it_finishes_without_secrets()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		var gate = new TaskCompletionSource();
		h.Outcome = async job =>
		{
			job.Emit(JobLogLevel.Info, "start https://cdn.example.com/x.aax?token=SECRET123");
			await gate.Task;
			job.Emit(JobLogLevel.Error, "failed: access_token=SECRET456");
			return ProcessBookResult.Success;
		};
		LiberationJob a = h.Job("A");
		h.Enqueue(a);
		await FirstWorkingJobId(h);

		await Eventually(async () => (await Call(h, HttpMethod.Get, $"/api/queue/jobs/{a.Id.Value}/log")).Body.RootElement.GetProperty("lines").GetArrayLength() == 1);
		gate.SetResult();
		await h.Queue.Runner!.WaitAsync(TimeSpan.FromSeconds(10));
		h.Queue.Jobs.ClearCompleted();

		var (finished, body) = await Call(h, HttpMethod.Get, $"/api/queue/jobs/{a.Id.Value}/log");
		Assert.AreEqual(HttpStatusCode.OK, finished);
		var lines = body.RootElement.GetProperty("lines").EnumerateArray().ToList();
		CollectionAssert.AreEqual(new[] { "info", "error" }, lines.Select(l => l.GetProperty("level").GetString()).ToArray());
		Assert.AreEqual(0, body.RootElement.GetProperty("dropped").GetInt32());
		Assert.AreEqual(a.Id.Value, body.RootElement.GetProperty("jobId").GetString());
		Assert.IsFalse(body.RootElement.GetRawText().Contains("SECRET", StringComparison.Ordinal));

		Assert.AreEqual(HttpStatusCode.NotFound, (await Call(h, HttpMethod.Get, "/api/queue/jobs/nope/log")).Status);
	}

	[TestMethod]
	public async Task Events_stream_delivers_book_log_and_pause_events()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var request = new HttpRequestMessage(HttpMethod.Get, h.Base + "/api/events");
		using var response = await h.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
		using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

		h.Outcome = job => { job.Emit(JobLogLevel.Warning, "heads up"); return Task.FromResult(ProcessBookResult.Success); };
		h.Queue.SetDispatchPaused(true);
		h.Queue.SetDispatchPaused(false);
		var job = h.Job("B000000001");
		h.Enqueue(job);

		var data = new Dictionary<string, string>();
		string? current = null;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (!data.ContainsKey(nameof(BookLog)) || !data.ContainsKey(nameof(QueueDispatchResumed)))
		{
			var line = await reader.ReadLineAsync(timeout.Token);
			Assert.IsNotNull(line);
			if (line.StartsWith("event: "))
				current = line[7..];
			else if (line.StartsWith("data: ") && current is not null)
				data[current] = line[6..];
		}

		using var log = JsonDocument.Parse(data[nameof(BookLog)]);
		Assert.AreEqual(job.Id.Value, log.RootElement.GetProperty("jobId").GetString());
		Assert.AreEqual("warning", log.RootElement.GetProperty("level").GetString());
		Assert.AreEqual("heads up", log.RootElement.GetProperty("message").GetString());
		Assert.AreEqual(JsonValueKind.String, log.RootElement.GetProperty("at").ValueKind);
		Assert.IsTrue(data.ContainsKey(nameof(QueueDispatchPaused)));
	}
}
