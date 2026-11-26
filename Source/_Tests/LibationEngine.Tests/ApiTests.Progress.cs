using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

public partial class ApiTests
{
	private static Task<HttpResponseMessage> ProgressPost(Harness h, string path, string? json = null) => h.Http.SendAsync(Send(HttpMethod.Post, h.Base + path, json));

	[TestMethod]
	public async Task Progress_returns_both_sides_and_null_for_a_missing_one()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var doc = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/library/B000000001/progress"));

		Assert.AreEqual(3832820, doc.RootElement.GetProperty("audible").GetProperty("positionMs").GetInt64());
		Assert.IsTrue(doc.RootElement.GetProperty("audible").TryGetProperty("lastUpdated", out _));
		Assert.AreEqual(JsonValueKind.Null, doc.RootElement.GetProperty("audiobookshelf").ValueKind);
		Assert.AreEqual("abs-item", doc.RootElement.GetProperty("absItemId").GetString());
	}

	[TestMethod]
	public async Task Progress_is_404_for_an_unknown_book_and_502_when_a_service_fails()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.GetAsync(h.Base + "/api/library/nope/progress")).StatusCode);

		h.Data.ProgressFail = true;
		var failed = await h.Http.GetAsync(h.Base + "/api/library/B000000001/progress");
		Assert.AreEqual(HttpStatusCode.BadGateway, failed.StatusCode);
		StringAssert.Contains(await failed.Content.ReadAsStringAsync(), "offline");
	}

	[TestMethod]
	public async Task Push_one_returns_the_outcome_and_passes_force_and_dryRun()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var response = await ProgressPost(h, "/api/library/B000000001/progress/push-abs", """{"force":true,"dryRun":true}""");

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		using (var doc = await Body(response))
		{
			Assert.AreEqual("B000000001", doc.RootElement.GetProperty("asin").GetString());
			Assert.AreEqual("pushed", doc.RootElement.GetProperty("outcome").GetString());
			Assert.AreEqual(3832.82, doc.RootElement.GetProperty("positionSec").GetDouble());
		}
		CollectionAssert.Contains(h.Data.Calls, "progresspush:B000000001:True:True");

		using var bare = await Body(await ProgressPost(h, "/api/library/B000000001/progress/push-abs"));
		Assert.AreEqual("pushed", bare.RootElement.GetProperty("outcome").GetString());
		CollectionAssert.Contains(h.Data.Calls, "progresspush:B000000001:False:False");
	}

	[TestMethod]
	public async Task Push_one_reports_a_skip_with_200_and_is_404_or_409_otherwise()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		h.Data.PushOutcome = "skipped";
		using (var skipped = await Body(await ProgressPost(h, "/api/library/B000000001/progress/push-abs")))
		{
			Assert.AreEqual("skipped", skipped.RootElement.GetProperty("outcome").GetString());
			Assert.AreEqual("Audiobookshelf has newer progress", skipped.RootElement.GetProperty("reason").GetString());
		}

		Assert.AreEqual(HttpStatusCode.NotFound, (await ProgressPost(h, "/api/library/nope/progress/push-abs")).StatusCode);
		h.Data.ProgressNotConfigured = true;
		Assert.AreEqual(HttpStatusCode.Conflict, (await ProgressPost(h, "/api/library/B000000001/progress/push-abs")).StatusCode);
	}

	[TestMethod]
	public async Task Push_many_queues_liberated_books_as_jobs_and_reports_the_rest()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		var release = new TaskCompletionSource();
		h.Outcome = async _ => { await release.Task; return ProcessBookResult.Success; };

		foreach (var body in new[] { null, "{}", "{\"asins\":[]}" })
			Assert.AreEqual(HttpStatusCode.BadRequest, (await ProgressPost(h, "/api/library/progress/push-abs", body)).StatusCode);

		const string request = """{"asins":["B000000001","B000000002","nope"],"force":true}""";
		Assert.AreEqual(HttpStatusCode.Conflict, (await ProgressPost(h, "/api/library/progress/push-abs", request)).StatusCode);

		await using var abs = new FakeAbs();
		await ConfigureAbs(h, abs);
		var response = await ProgressPost(h, "/api/library/progress/push-abs", request);

		Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
		using (var doc = await Body(response))
		{
			Assert.AreEqual(1, doc.RootElement.GetProperty("updated").GetInt32());
			CollectionAssert.AreEqual(new[] { "nope" }, doc.RootElement.GetProperty("missing").EnumerateArray().Select(e => e.GetString()).ToArray());
			CollectionAssert.AreEqual(new[] { "ok", "conflict", "notFound" }, doc.RootElement.GetProperty("results").EnumerateArray().Select(e => e.GetProperty("result").GetString()).ToArray());
		}
		await Eventually(async () => (await h.QueueJson()).RootElement.GetProperty("jobs").GetArrayLength() == 1);

		using var again = await Body(await ProgressPost(h, "/api/library/progress/push-abs", """{"asins":["B000000001"]}"""));
		Assert.AreEqual("conflict", again.RootElement.GetProperty("results")[0].GetProperty("result").GetString());

		release.SetResult();
		await h.Queue.Runner!;
	}
}
