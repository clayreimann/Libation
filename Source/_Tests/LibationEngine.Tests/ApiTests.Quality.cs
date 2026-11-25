using System.Net;
using System.Text.Json;
using LibationEngine.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

public partial class ApiTests
{
	private static Task<HttpResponseMessage> PostQuality(Harness h, string path, string? json = null)
		=> h.Http.SendAsync(Send(HttpMethod.Post, $"{h.Base}/api/library/quality-scan{path}", json));

	[TestMethod]
	public async Task Quality_scan_starts_in_the_background_and_streams_progress()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var request = new HttpRequestMessage(HttpMethod.Get, h.Base + "/api/events");
		using var response = await h.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
		using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

		var started = await PostQuality(h, "", """{"asins":["B000000001","B000000001"],"widevine":true}""");
		Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode);
		using (var doc = await Body(started))
		{
			Assert.AreEqual("running", doc.RootElement.GetProperty("state").GetString());
			Assert.IsTrue(doc.RootElement.GetProperty("widevine").GetBoolean());
		}
		CollectionAssert.Contains(h.Data.Calls, "qualityscan:B000000001:True");

		string? current = null, progress = null;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (progress is null)
		{
			var line = await reader.ReadLineAsync(timeout.Token);
			Assert.IsNotNull(line);
			if (line.StartsWith("event: "))
				current = line[7..];
			else if (line.StartsWith("data: ") && current == nameof(QualityScanProgress))
				progress = line[6..];
		}
		Assert.AreEqual(2, JsonDocument.Parse(progress).RootElement.GetProperty("scan").GetProperty("total").GetInt32());
	}

	[TestMethod]
	public async Task Quality_scan_without_a_body_scans_everything()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.Accepted, (await PostQuality(h, "")).StatusCode);
		CollectionAssert.Contains(h.Data.Calls, "qualityscan:*:");
	}

	[TestMethod]
	public async Task Quality_scan_is_400_for_bad_asins_or_nothing_to_scan_and_409_when_running()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.BadRequest, (await PostQuality(h, "", """{"asins":[]}""")).StatusCode);
		Assert.AreEqual(HttpStatusCode.BadRequest, (await PostQuality(h, "", """{"asins":["none"]}""")).StatusCode);
		Assert.AreEqual(HttpStatusCode.Conflict, (await PostQuality(h, "", """{"asins":["busy"]}""")).StatusCode);
	}

	[TestMethod]
	public async Task Quality_scan_lists_results_with_current_and_available_quality()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var doc = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/library/quality-scan"));
		Assert.AreEqual("completed", doc.RootElement.GetProperty("state").GetString());
		var hit = doc.RootElement.GetProperty("results")[0];
		Assert.AreEqual("B000000001", hit.GetProperty("asin").GetString());
		Assert.AreEqual("Title", hit.GetProperty("title").GetString());
		Assert.AreEqual("AAC-LC", hit.GetProperty("currentQuality").GetProperty("codec").GetString());
		Assert.AreEqual(128, hit.GetProperty("availableQuality").GetProperty("bitrate").GetInt32());
	}

	[TestMethod]
	public async Task Quality_scan_cancel_is_409_unless_one_is_running()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.Conflict, (await h.Http.DeleteAsync(h.Base + "/api/library/quality-scan")).StatusCode);

		await PostQuality(h, "");
		var cancelled = await h.Http.DeleteAsync(h.Base + "/api/library/quality-scan");
		Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
		using var doc = await Body(cancelled);
		Assert.AreEqual("cancelling", doc.RootElement.GetProperty("state").GetString());
	}

	[TestMethod]
	public async Task Quality_apply_reports_marked_missing_and_settings_changed()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var response = await PostQuality(h, "/apply", """{"asins":["B000000001","B000000009","B000000001"]}""");

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		using var doc = await Body(response);
		Assert.AreEqual(1, doc.RootElement.GetProperty("marked").GetInt32());
		Assert.AreEqual("B000000009", doc.RootElement.GetProperty("missing")[0].GetString());
		Assert.IsTrue(doc.RootElement.GetProperty("widevineEnabled").GetBoolean());
		Assert.IsFalse(doc.RootElement.GetProperty("accountsMarkedForReRegistration").GetBoolean());
		CollectionAssert.Contains(h.Data.Calls, "qualityapply:B000000001,B000000009");
	}

	[TestMethod]
	[DataRow(null)]
	[DataRow("{}")]
	[DataRow("""{"asins":[]}""")]
	public async Task Quality_apply_is_400_without_asins(string? body)
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.BadRequest, (await PostQuality(h, "/apply", body)).StatusCode);
		Assert.IsFalse(h.Data.Calls.Any(c => c.StartsWith("qualityapply")));
	}

	[TestMethod]
	public async Task Quality_apply_is_409_while_a_scan_runs()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		await PostQuality(h, "");

		Assert.AreEqual(HttpStatusCode.Conflict, (await PostQuality(h, "/apply", """{"asins":["B000000001"]}""")).StatusCode);
	}

	[TestMethod]
	public async Task OpenApi_quality_and_bookmark_dtos_require_only_non_nullable_fields_and_type_their_enums()
	{
		var schemas = await SchemaDocument();

		CollectionAssert.AreEquivalent(new[] { "state", "widevine", "total", "scanned", "failed" }, Required(schemas, "QualityScanDto"));
		CollectionAssert.AreEquivalent(new[] { "asin", "title", "currentQuality", "availableQuality" }, Required(schemas, "QualityResultDto"));
		CollectionAssert.AreEquivalent(new[] { "type", "start", "created" }, Required(schemas, "BookmarkDto"));
		CollectionAssert.AreEquivalent(new[] { "marked", "missing", "widevineEnabled", "xHeAacRequested", "accountsMarkedForReRegistration" }, Required(schemas, "QualityApplyDto"));
		CollectionAssert.AreEquivalent(new[] { "Clip", "Note", "Bookmark", "LastHeard" },
			schemas.GetProperty("BookmarkDto").GetProperty("properties").GetProperty("type").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray());
		Assert.AreEqual(6, schemas.GetProperty("QualityScanDto").GetProperty("properties").GetProperty("state").GetProperty("enum").GetArrayLength());
	}
}
