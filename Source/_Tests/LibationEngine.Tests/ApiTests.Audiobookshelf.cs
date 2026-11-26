using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LibationEngine.Contracts;
using LibationFileManager;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

public partial class ApiTests
{
	private const string GoodToken = "good-SECRETTOKEN";
	private const string BadToken = "bad-SECRETTOKEN";

	private sealed class FakeAbs : IAsyncDisposable
	{
		private readonly WebApplication app;
		public readonly string Url;
		public volatile int LibrariesStatus = 200;

		public FakeAbs()
		{
			var probe = new TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			var port = ((IPEndPoint)probe.LocalEndpoint).Port;
			probe.Stop();
			Url = $"http://127.0.0.1:{port}";

			var builder = WebApplication.CreateSlimBuilder();
			builder.WebHost.UseUrls(Url);
			app = builder.Build();
			static bool Authorized(HttpRequest r) => r.Headers.Authorization == $"Bearer {GoodToken}";
			app.MapPost("/api/authorize", (HttpRequest r) => Authorized(r)
				? Results.Json(new { user = new { username = "clay" }, serverSettings = new { version = "2.17.0" } })
				: Results.StatusCode(401));
			app.MapGet("/api/libraries", (HttpRequest r) => !Authorized(r) ? Results.StatusCode(401)
				: LibrariesStatus != 200 ? Results.StatusCode(LibrariesStatus)
				: Results.Json(new
				{
					libraries = new object[]
					{
						new { id = "lib1", name = "Audiobooks", mediaType = "book", folders = new[] { new { id = "fol1", fullPath = "/audiobooks" }, new { id = "fol2", fullPath = "/more" } } },
						new { id = "lib2", name = "Shows", mediaType = "podcast", folders = new[] { new { id = "fol3", fullPath = "/podcasts" } } }
					}
				}));
			app.StartAsync().GetAwaiter().GetResult();
		}

		public async ValueTask DisposeAsync() => await app.DisposeAsync();
	}

	private static string ClosedPortUrl()
	{
		var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		return $"http://127.0.0.1:{port}";
	}

	private static async Task<JsonDocument> Body(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

	private static Task<HttpResponseMessage> Patch(Harness h, string json) => h.Http.SendAsync(Send(HttpMethod.Patch, h.Base + "/api/settings", json));

	private static Task<HttpResponseMessage> AbsPost(Harness h, string path, string? json = null) => h.Http.SendAsync(Send(HttpMethod.Post, h.Base + path, json));

	private static async Task ConfigureAbs(Harness h, FakeAbs abs, string token = GoodToken)
		=> Assert.AreEqual(HttpStatusCode.OK, (await Patch(h, $$$"""{"audiobookshelf":{"enabled":true,"serverUrl":"{{{abs.Url}}}","apiToken":"{{{token}}}","libraryId":"lib1","folderId":"fol1"}}""")).StatusCode);

	[TestMethod]
	public async Task Audiobookshelf_settings_patch_saves_and_reports_the_token_only_as_set()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using (var before = await Body(await h.Http.GetAsync(h.Base + "/api/settings")))
		{
			var abs = before.RootElement.GetProperty("audiobookshelf");
			Assert.IsFalse(abs.GetProperty("tokenSet").GetBoolean());
			Assert.AreEqual(JsonValueKind.Null, abs.GetProperty("serverUrl").ValueKind);
			Assert.IsFalse(abs.GetProperty("pushProgressAfterUpload").GetBoolean());
		}

		var response = await Patch(h, $$$"""{"audiobookshelf":{"enabled":true,"includePdfs":true,"pushProgressAfterUpload":true,"serverUrl":"https://abs.example.com/library/abc","apiToken":"{{{GoodToken}}}","libraryId":"lib1","folderId":"fol1"}}""");
		var text = await response.Content.ReadAsStringAsync();
		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);
		Assert.DoesNotContain("SECRETTOKEN", text);
		using (var doc = JsonDocument.Parse(text))
		{
			var abs = doc.RootElement.GetProperty("audiobookshelf");
			Assert.IsTrue(abs.GetProperty("enabled").GetBoolean());
			Assert.IsTrue(abs.GetProperty("includePdfs").GetBoolean());
			Assert.IsTrue(abs.GetProperty("pushProgressAfterUpload").GetBoolean());
			Assert.IsTrue(abs.GetProperty("tokenSet").GetBoolean());
			Assert.IsTrue(abs.GetProperty("serverConfigured").GetBoolean());
			Assert.AreEqual("https://abs.example.com", abs.GetProperty("serverUrl").GetString());
			Assert.AreEqual("lib1", abs.GetProperty("libraryId").GetString());
			Assert.AreEqual("fol1", abs.GetProperty("folderId").GetString());
		}
		Assert.AreEqual(GoodToken, ApplicationServices.AudiobookshelfTokenStorage.DecryptToken(Configuration.Instance.AudiobookshelfApiToken));
		Assert.DoesNotContain("SECRETTOKEN", await h.Http.GetStringAsync(h.Base + "/api/settings"));

		using var cleared = await Body(await Patch(h, """{"audiobookshelf":{"apiToken":null,"serverUrl":null,"libraryId":null}}"""));
		var after = cleared.RootElement.GetProperty("audiobookshelf");
		Assert.IsFalse(after.GetProperty("tokenSet").GetBoolean());
		Assert.IsFalse(after.GetProperty("serverConfigured").GetBoolean());
		Assert.AreEqual(JsonValueKind.Null, after.GetProperty("libraryId").ValueKind);
		Assert.AreEqual(JsonValueKind.Null, after.GetProperty("folderId").ValueKind);
		Assert.IsTrue(after.GetProperty("enabled").GetBoolean());
	}

	[TestMethod]
	[DataRow("""{"audiobookshelf":{"serverUrl":"ftp://abs.example.com"}}""", "audiobookshelf.serverUrl")]
	[DataRow("""{"audiobookshelf":{"serverUrl":"/relative/path"}}""", "audiobookshelf.serverUrl")]
	[DataRow("""{"audiobookshelf":{"serverUrl":"abs.example.com"}}""", "audiobookshelf.serverUrl")]
	[DataRow("""{"audiobookshelf":{"serverUrl":"https://user:pw@abs.example.com"}}""", "audiobookshelf.serverUrl")]
	[DataRow("""{"audiobookshelf":{"serverUrl":"  "}}""", "audiobookshelf.serverUrl")]
	[DataRow("""{"audiobookshelf":{"apiToken":""}}""", "audiobookshelf.apiToken")]
	[DataRow("""{"audiobookshelf":{"folderId":"fol1"}}""", "audiobookshelf.folderId")]
	[DataRow("""{"audiobookshelf":{"libraryId":" "}}""", "audiobookshelf.libraryId")]
	[DataRow("""{"audiobookshelf":{"enabled":null}}""", "audiobookshelf.enabled")]
	[DataRow("""{"audiobookshelf":{"enabled":"yes"}}""", "audiobookshelf.enabled")]
	[DataRow("""{"audiobookshelf":{"bogus":1}}""", "audiobookshelf.bogus")]
	public async Task Audiobookshelf_settings_patch_rejects_bad_values_and_changes_nothing(string body, string field)
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var response = await Patch(h, body);

		Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
		using var doc = await Body(response);
		Assert.IsTrue(doc.RootElement.GetProperty("fields").TryGetProperty(field, out _), await response.Content.ReadAsStringAsync());
		Assert.IsNull(Configuration.Instance.AudiobookshelfServerUrl);
		Assert.IsNull(Configuration.Instance.AudiobookshelfApiToken);
	}

	[TestMethod]
	public async Task One_bad_audiobookshelf_field_stops_the_token_and_other_fields_being_saved()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var response = await Patch(h, $$$"""{"audiobookshelf":{"enabled":true,"apiToken":"{{{GoodToken}}}","serverUrl":"ftp://x"}}""");

		Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.IsFalse(Configuration.Instance.AudiobookshelfEnabled);
		Assert.IsNull(Configuration.Instance.AudiobookshelfApiToken);
	}

	[TestMethod]
	public async Task Choosing_another_library_clears_the_folder_unless_one_is_named()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		await using var abs = new FakeAbs();
		await ConfigureAbs(h, abs);

		await Patch(h, """{"audiobookshelf":{"libraryId":"lib1"}}""");
		Assert.AreEqual("fol1", Configuration.Instance.AudiobookshelfFolderId);

		await Patch(h, """{"audiobookshelf":{"libraryId":"lib9"}}""");
		Assert.IsNull(Configuration.Instance.AudiobookshelfFolderId);

		await Patch(h, """{"audiobookshelf":{"libraryId":"lib1","folderId":"fol2"}}""");
		Assert.AreEqual("fol2", Configuration.Instance.AudiobookshelfFolderId);
	}

	[TestMethod]
	public async Task Audiobookshelf_settings_patch_is_409_in_read_only_mode()
	{
		await using var h = new Harness(api: true, readonlyConfig: true);
		await h.WaitIdleAfterFirstCycle();

		var response = await Patch(h, $$$"""{"audiobookshelf":{"apiToken":"{{{GoodToken}}}"}}""");

		Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
		Assert.DoesNotContain("SECRETTOKEN", await response.Content.ReadAsStringAsync());
		Assert.IsNull(Configuration.Instance.AudiobookshelfApiToken);
	}

	[TestMethod]
	public async Task Test_connection_uses_saved_or_supplied_values_and_maps_failures_to_codes()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		await using var abs = new FakeAbs();

		async Task<(HttpStatusCode, JsonElement)> Test(string? json)
		{
			var response = await AbsPost(h, "/api/audiobookshelf/test", json);
			return (response.StatusCode, (await Body(response)).RootElement.Clone());
		}

		var (status, none) = await Test(null);
		Assert.AreEqual(HttpStatusCode.OK, status);
		Assert.IsFalse(none.GetProperty("ok").GetBoolean());
		Assert.AreEqual("notConfigured", none.GetProperty("code").GetString());

		var (_, unsaved) = await Test($$$"""{"url":"{{{abs.Url}}}","token":"{{{GoodToken}}}"}""");
		Assert.IsTrue(unsaved.GetProperty("ok").GetBoolean());
		Assert.AreEqual("2.17.0", unsaved.GetProperty("serverVersion").GetString());
		Assert.AreEqual("clay", unsaved.GetProperty("user").GetString());
		Assert.IsNull(Configuration.Instance.AudiobookshelfServerUrl);

		await ConfigureAbs(h, abs);
		var (_, saved) = await Test(null);
		Assert.IsTrue(saved.GetProperty("ok").GetBoolean());
		var (_, sameUrl) = await Test($$$"""{"url":"{{{abs.Url}}}/library/x"}""");
		Assert.IsTrue(sameUrl.GetProperty("ok").GetBoolean());

		var (_, rejected) = await Test($$$"""{"token":"{{{BadToken}}}"}""");
		Assert.AreEqual("unauthorized", rejected.GetProperty("code").GetString());
		Assert.DoesNotContain("SECRETTOKEN", rejected.GetRawText());

		var (_, unreachable) = await Test($$$"""{"url":"{{{ClosedPortUrl()}}}","token":"{{{BadToken}}}"}""");
		Assert.AreEqual("unreachable", unreachable.GetProperty("code").GetString());
		Assert.DoesNotContain("SECRETTOKEN", unreachable.GetRawText());

		var (_, badUrl) = await Test($$$"""{"url":"ftp://nope","token":"{{{BadToken}}}"}""");
		Assert.AreEqual("badUrl", badUrl.GetProperty("code").GetString());
		Assert.DoesNotContain("SECRETTOKEN", badUrl.GetRawText());
	}

	[TestMethod]
	public async Task Test_connection_refuses_to_send_the_saved_token_to_a_different_url_and_rejects_blanks()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		await using var abs = new FakeAbs();
		await ConfigureAbs(h, abs);

		foreach (var body in new[] { """{"url":"https://elsewhere.example.com"}""", """{"url":""}""", """{"token":" "}""" })
			Assert.AreEqual(HttpStatusCode.BadRequest, (await AbsPost(h, "/api/audiobookshelf/test", body)).StatusCode, body);
	}

	[TestMethod]
	public async Task Libraries_lists_book_libraries_with_folders()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		await using var abs = new FakeAbs();

		var unconfigured = await h.Http.GetAsync(h.Base + "/api/audiobookshelf/libraries");
		Assert.AreEqual(HttpStatusCode.Conflict, unconfigured.StatusCode);

		await ConfigureAbs(h, abs);
		var response = await h.Http.GetAsync(h.Base + "/api/audiobookshelf/libraries");
		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		using var doc = await Body(response);
		var libraries = doc.RootElement.EnumerateArray().ToList();
		Assert.HasCount(1, libraries);
		Assert.AreEqual("lib1", libraries[0].GetProperty("id").GetString());
		Assert.AreEqual("Audiobooks", libraries[0].GetProperty("name").GetString());
		Assert.AreEqual("book", libraries[0].GetProperty("mediaType").GetString());
		var folders = libraries[0].GetProperty("folders").EnumerateArray().ToList();
		Assert.AreEqual(2, folders.Count);
		Assert.AreEqual("fol1", folders[0].GetProperty("id").GetString());
		Assert.AreEqual("/audiobooks", folders[0].GetProperty("path").GetString());
	}

	[TestMethod]
	public async Task Libraries_is_502_with_a_code_when_the_server_rejects_or_cannot_be_reached()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		await using var abs = new FakeAbs();

		await ConfigureAbs(h, abs, BadToken);
		var rejected = await h.Http.GetAsync(h.Base + "/api/audiobookshelf/libraries");
		Assert.AreEqual(HttpStatusCode.BadGateway, rejected.StatusCode);
		using (var doc = await Body(rejected))
			Assert.AreEqual("unauthorized", doc.RootElement.GetProperty("code").GetString());

		await ConfigureAbs(h, abs);
		abs.LibrariesStatus = 500;
		var broken = await h.Http.GetAsync(h.Base + "/api/audiobookshelf/libraries");
		Assert.AreEqual(HttpStatusCode.BadGateway, broken.StatusCode);
		using (var doc = await Body(broken))
			Assert.AreEqual("serverError", doc.RootElement.GetProperty("code").GetString());

		await Patch(h, $$$"""{"audiobookshelf":{"serverUrl":"{{{ClosedPortUrl()}}}"}}""");
		var down = await h.Http.GetAsync(h.Base + "/api/audiobookshelf/libraries");
		Assert.AreEqual(HttpStatusCode.BadGateway, down.StatusCode);
		using (var doc = await Body(down))
			Assert.AreEqual("unreachable", doc.RootElement.GetProperty("code").GetString());
	}

	[TestMethod]
	public async Task Upload_queues_liberated_books_as_jobs_and_reports_the_rest()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		var release = new TaskCompletionSource();
		h.Outcome = async _ => { await release.Task; return ProcessBookResult.Success; };

		foreach (var body in new[] { null, "{}", "{\"asins\":[]}" })
			Assert.AreEqual(HttpStatusCode.BadRequest, (await AbsPost(h, "/api/audiobookshelf/upload", body)).StatusCode);

		const string request = """{"asins":["B000000001","B000000002","nope"]}""";
		Assert.AreEqual(HttpStatusCode.Conflict, (await AbsPost(h, "/api/audiobookshelf/upload", request)).StatusCode);

		await using var abs = new FakeAbs();
		await ConfigureAbs(h, abs);
		var response = await AbsPost(h, "/api/audiobookshelf/upload", request);

		Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
		using (var doc = await Body(response))
		{
			Assert.AreEqual(1, doc.RootElement.GetProperty("updated").GetInt32());
			CollectionAssert.AreEqual(new[] { "nope" }, doc.RootElement.GetProperty("missing").EnumerateArray().Select(e => e.GetString()).ToArray());
			CollectionAssert.AreEqual(new[] { "ok", "conflict", "notFound" }, doc.RootElement.GetProperty("results").EnumerateArray().Select(e => e.GetProperty("result").GetString()).ToArray());
		}
		await Eventually(async () => (await h.QueueJson()).RootElement.GetProperty("jobs").GetArrayLength() == 1);

		using var again = await Body(await AbsPost(h, "/api/audiobookshelf/upload", """{"asins":["B000000001"]}"""));
		Assert.AreEqual("conflict", again.RootElement.GetProperty("results")[0].GetProperty("result").GetString());

		release.SetResult();
		await h.Queue.Runner!;
	}

	[TestMethod]
	public async Task Upload_is_409_unless_audiobookshelf_is_enabled_and_fully_configured()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		await using var abs = new FakeAbs();
		await ConfigureAbs(h, abs);

		foreach (var clear in new[] { """{"enabled":false}""", """{"enabled":true,"apiToken":null}""", """{"apiToken":"x","folderId":null}""", """{"libraryId":null}""" })
		{
			await Patch(h, $$$"""{"audiobookshelf":{{{clear}}}}""");
			Assert.AreEqual(HttpStatusCode.Conflict, (await AbsPost(h, "/api/audiobookshelf/upload", """{"asins":["B000000001"]}""")).StatusCode, clear);
			await ConfigureAbs(h, abs);
		}
	}

	[TestMethod]
	public async Task The_audiobookshelf_token_never_reaches_a_log_event_or_a_response()
	{
		var sink = new CaptureSink();
		var previous = Serilog.Log.Logger;
		Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
		try
		{
			await using var h = new Harness(api: true);
			await h.WaitIdleAfterFirstCycle();
			await using var abs = new FakeAbs();
			var responses = new List<string>();
			async Task Record(Task<HttpResponseMessage> call) => responses.Add(await (await call).Content.ReadAsStringAsync());

			await Record(Patch(h, $$$"""{"audiobookshelf":{"apiToken":"{{{GoodToken}}}","serverUrl":"{{{abs.Url}}}","libraryId":"lib1","folderId":"fol1","enabled":true}}"""));
			await Record(Patch(h, $$$"""{"audiobookshelf":{"apiToken":"{{{BadToken}}}","serverUrl":"ftp://nope"}}"""));
			await Record(h.Http.GetAsync(h.Base + "/api/settings"));
			await Record(h.Http.GetAsync(h.Base + "/api/audiobookshelf/libraries"));
			await Record(AbsPost(h, "/api/audiobookshelf/test"));
			await Record(AbsPost(h, "/api/audiobookshelf/test", $$$"""{"url":"{{{abs.Url}}}","token":"{{{BadToken}}}"}"""));
			await Record(AbsPost(h, "/api/audiobookshelf/test", $$$"""{"url":"{{{ClosedPortUrl()}}}","token":"{{{BadToken}}}"}"""));
			await Record(Patch(h, $$$"""{"audiobookshelf":{"apiToken":"{{{BadToken}}}"}}"""));
			await Record(h.Http.GetAsync(h.Base + "/api/audiobookshelf/libraries"));
			await Record(AbsPost(h, "/api/audiobookshelf/upload", """{"asins":["B000000001"]}"""));

			Assert.HasCount(10, responses);
			foreach (var body in responses)
				Assert.DoesNotContain("SECRETTOKEN", body);
		}
		finally
		{
			Serilog.Log.Logger = previous;
		}

		Assert.IsGreaterThan(0, sink.Events.Count, "the capture saw the daemon's own log events");
		foreach (var rendered in sink.Events)
			Assert.DoesNotContain("SECRETTOKEN", rendered);
	}

	[TestMethod]
	public async Task OpenApi_audiobookshelf_schemas_require_only_non_nullable_members()
	{
		using var doc = await OpenApiDocument();
		var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
		string[] Required(string name) => schemas.GetProperty(name).GetProperty("required").EnumerateArray().Select(e => e.GetString()!).Order().ToArray();

		CollectionAssert.AreEqual(new[] { "ok" }, Required("AudiobookshelfTestDto"));
		CollectionAssert.AreEqual(new[] { "folders", "id", "mediaType", "name" }, Required("AudiobookshelfLibraryDto"));
		CollectionAssert.AreEqual(new[] { "id", "path" }, Required("AudiobookshelfFolderDto"));
		CollectionAssert.IsSubsetOf(new[] { "enabled", "includePdfs", "pushProgressAfterUpload", "serverConfigured", "tokenSet" }, Required("AudiobookshelfSettingsDto"));
		CollectionAssert.DoesNotContain(Required("AudiobookshelfSettingsDto"), "serverUrl");
		var paths = doc.RootElement.GetProperty("paths");
		foreach (var path in new[] { "/api/audiobookshelf/test", "/api/audiobookshelf/libraries", "/api/audiobookshelf/upload", "/api/library/{asin}/progress", "/api/library/{asin}/progress/push-abs", "/api/library/progress/push-abs" })
			Assert.IsTrue(paths.TryGetProperty(path, out _), path);
	}
}
