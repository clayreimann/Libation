using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

public partial class ApiTests
{
	private static Task<HttpResponseMessage> PutRating(Harness h, string asin, string? json) => h.Http.SendAsync(Send(HttpMethod.Put, $"{h.Base}/api/library/{asin}/rating", json));

	[TestMethod]
	public async Task Rating_returns_the_updated_book()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var response = await PutRating(h, "B000000001", """{"overall":5,"performance":4,"story":0}""");

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		using var doc = await Body(response);
		var mine = doc.RootElement.GetProperty("myRating");
		Assert.AreEqual(5, mine.GetProperty("overall").GetInt32());
		Assert.AreEqual(4, mine.GetProperty("performance").GetInt32());
		Assert.AreEqual(0, mine.GetProperty("story").GetInt32());
		Assert.IsTrue(doc.RootElement.TryGetProperty("description", out _));
		CollectionAssert.Contains(h.Data.Calls, "rating:B000000001:5:4:0");
	}

	[TestMethod]
	[DataRow(null)]
	[DataRow("{}")]
	[DataRow("""{"overall":5,"performance":4}""")]
	[DataRow("""{"overall":6,"performance":4,"story":4}""")]
	[DataRow("""{"overall":5,"performance":-1,"story":4}""")]
	[DataRow("""{"overall":5,"performance":4,"story":99}""")]
	[DataRow("""{"overall":4.5,"performance":4,"story":4}""")]
	public async Task Rating_rejects_missing_or_out_of_range_stars_before_calling_anything(string? body)
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.BadRequest, (await PutRating(h, "B000000001", body)).StatusCode);
		Assert.IsFalse(h.Data.Calls.Any(c => c.StartsWith("rating:")));
	}

	[TestMethod]
	public async Task Rating_is_404_for_an_unknown_book_and_502_when_audible_fails()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.NotFound, (await PutRating(h, "nope", """{"overall":5,"performance":5,"story":5}""")).StatusCode);

		var failed = await PutRating(h, "B000000001", """{"overall":3,"performance":3,"story":3}""");
		Assert.AreEqual(HttpStatusCode.BadGateway, failed.StatusCode);
		using var doc = await Body(failed);
		Assert.AreEqual("Audible rejected the rating.", doc.RootElement.GetProperty("error").GetString());
	}
}
