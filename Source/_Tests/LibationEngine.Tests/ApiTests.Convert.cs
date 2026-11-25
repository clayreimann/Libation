using System.Net;
using LibationEngine.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

public partial class ApiTests
{
	private static Task<HttpResponseMessage> ConvertPost(Harness h, string path, string? json = null) => h.Http.SendAsync(Send(HttpMethod.Post, h.Base + path, json));

	[TestMethod]
	public async Task Convert_queues_convertible_books_as_jobs_and_reports_the_rest()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		var release = new TaskCompletionSource();
		h.Outcome = async _ => { await release.Task; return ProcessBookResult.Success; };

		foreach (var body in new[] { null, "{}", "{\"asins\":[]}" })
			Assert.AreEqual(HttpStatusCode.BadRequest, (await ConvertPost(h, "/api/library/convert-mp3", body)).StatusCode);

		var response = await ConvertPost(h, "/api/library/convert-mp3", """{"asins":["B000000001","B000000002","nope"]}""");

		Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
		using (var doc = await Body(response))
		{
			Assert.AreEqual(1, doc.RootElement.GetProperty("updated").GetInt32());
			CollectionAssert.AreEqual(new[] { "nope" }, doc.RootElement.GetProperty("missing").EnumerateArray().Select(e => e.GetString()).ToArray());
			CollectionAssert.AreEqual(new[] { "ok", "conflict", "notFound" }, doc.RootElement.GetProperty("results").EnumerateArray().Select(e => e.GetProperty("result").GetString()).ToArray());
		}
		await Eventually(async () => (await h.QueueJson()).RootElement.GetProperty("jobs").GetArrayLength() == 1);

		using var again = await Body(await ConvertPost(h, "/api/library/convert-mp3", """{"asins":["B000000001"]}"""));
		Assert.AreEqual("conflict", again.RootElement.GetProperty("results")[0].GetProperty("result").GetString());

		release.SetResult();
		await h.Queue.Runner!;
	}

	[TestMethod]
	public async Task Convert_one_book_returns_the_job_or_404_or_409()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		var release = new TaskCompletionSource();
		h.Outcome = async _ => { await release.Task; return ProcessBookResult.Success; };

		Assert.AreEqual(HttpStatusCode.NotFound, (await ConvertPost(h, "/api/library/nope/convert-mp3")).StatusCode);
		Assert.AreEqual(HttpStatusCode.Conflict, (await ConvertPost(h, "/api/library/B000000002/convert-mp3")).StatusCode);

		var response = await ConvertPost(h, "/api/library/B000000001/convert-mp3");
		Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
		using (var doc = await Body(response))
			Assert.AreEqual("B000000001", doc.RootElement.GetProperty("asin").GetString());

		Assert.AreEqual(HttpStatusCode.Conflict, (await ConvertPost(h, "/api/library/B000000001/convert-mp3")).StatusCode);

		release.SetResult();
		await h.Queue.Runner!;
	}
}
