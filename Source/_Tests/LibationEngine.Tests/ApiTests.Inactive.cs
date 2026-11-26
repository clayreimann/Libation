using System.Net;
using System.Text.Json;
using LibationEngine.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

public partial class ApiTests
{
	private static Task<HttpResponseMessage> PostInactive(Harness h, string? json = null)
		=> h.Http.SendAsync(Send(HttpMethod.Post, $"{h.Base}/api/library/inactive-scan", json));

	[TestMethod]
	public async Task Inactive_scan_starts_in_the_background_and_streams_progress()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var request = new HttpRequestMessage(HttpMethod.Get, h.Base + "/api/events");
		using var response = await h.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
		using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

		var started = await PostInactive(h, """{"accounts":["a@example.com"," a@example.com"]}""");
		Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode);
		using (var doc = await Body(started))
		{
			Assert.AreEqual("running", doc.RootElement.GetProperty("state").GetString());
			Assert.AreEqual(3, doc.RootElement.GetProperty("total").GetInt32());
		}
		CollectionAssert.Contains(h.Data.Calls, "inactivescan:a@example.com");

		string? current = null, progress = null;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (progress is null)
		{
			var line = await reader.ReadLineAsync(timeout.Token);
			Assert.IsNotNull(line);
			if (line.StartsWith("event: "))
				current = line[7..];
			else if (line.StartsWith("data: ") && current == nameof(InactiveScanProgress))
				progress = line[6..];
		}
		Assert.AreEqual("running", JsonDocument.Parse(progress).RootElement.GetProperty("scan").GetProperty("state").GetString());
	}

	[TestMethod]
	public async Task Inactive_scan_without_a_body_scans_the_default_accounts()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.Accepted, (await PostInactive(h)).StatusCode);
		CollectionAssert.Contains(h.Data.Calls, "inactivescan:*");
	}

	[TestMethod]
	[DataRow("""{"accounts":[]}""")]
	[DataRow("""{"accounts":[" "]}""")]
	[DataRow("""{"accounts":["nobody"]}""")]
	public async Task Inactive_scan_is_400_for_bad_accounts(string body)
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.BadRequest, (await PostInactive(h, body)).StatusCode);
	}

	[TestMethod]
	public async Task Inactive_scan_is_409_when_running()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.Conflict, (await PostInactive(h, """{"accounts":["busy"]}""")).StatusCode);
	}

	[TestMethod]
	public async Task Inactive_scan_lists_the_books_that_left_the_account()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var doc = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/library/inactive-scan"));
		Assert.AreEqual("completed", doc.RootElement.GetProperty("state").GetString());
		var hit = doc.RootElement.GetProperty("results")[0];
		Assert.AreEqual("B000000001", hit.GetProperty("asin").GetString());
		Assert.AreEqual("Author", hit.GetProperty("authors")[0].GetString());
		Assert.AreEqual("a@example.com", hit.GetProperty("account").GetString());
	}

	[TestMethod]
	public async Task Inactive_scan_cancel_is_409_unless_one_is_running()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.Conflict, (await h.Http.DeleteAsync(h.Base + "/api/library/inactive-scan")).StatusCode);

		await PostInactive(h);
		var cancelled = await h.Http.DeleteAsync(h.Base + "/api/library/inactive-scan");
		Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
		using var doc = await Body(cancelled);
		Assert.AreEqual("cancelling", doc.RootElement.GetProperty("state").GetString());
	}

	[TestMethod]
	public async Task OpenApi_inactive_scan_dtos_require_only_non_nullable_fields_and_type_the_state_enum()
	{
		var schemas = await SchemaDocument();

		CollectionAssert.AreEquivalent(new[] { "state", "total" }, Required(schemas, "InactiveScanDto"));
		CollectionAssert.AreEquivalent(new[] { "asin", "title", "authors", "account" }, Required(schemas, "InactiveBookDto"));
		Assert.AreEqual(6, schemas.GetProperty("InactiveScanDto").GetProperty("properties").GetProperty("state").GetProperty("enum").GetArrayLength());
		Assert.AreEqual("#/components/schemas/InactiveScanDto", schemas.GetProperty("InactiveScanProgress").GetProperty("properties").GetProperty("scan").GetProperty("$ref").GetString());
	}
}
