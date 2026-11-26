using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

public partial class ApiTests
{
	private static Task<HttpResponseMessage> PostRemoval(Harness h, string json)
		=> h.Http.SendAsync(Send(HttpMethod.Post, $"{h.Base}/api/library/remove-from-audible", json));

	[TestMethod]
	public async Task Remove_from_audible_runs_only_with_the_exact_phrase_for_the_distinct_count()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var response = await PostRemoval(h, """{"asins":["B000000001","B000000002","B000000001"],"confirm":"remove 2 from audible"}""");

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		using var doc = await Body(response);
		Assert.AreEqual(2, doc.RootElement.GetProperty("results").GetArrayLength());
		Assert.AreEqual("removed", doc.RootElement.GetProperty("results")[0].GetProperty("result").GetString());
		CollectionAssert.Contains(h.Data.Calls, "removefromaudible:B000000001,B000000002");
	}

	[TestMethod]
	[DataRow("""{"asins":["B000000001","B000000002"]}""")]
	[DataRow("""{"asins":["B000000001","B000000002"],"confirm":""}""")]
	[DataRow("""{"asins":["B000000001","B000000002"],"confirm":"remove 1 from audible"}""")]
	[DataRow("""{"asins":["B000000001","B000000002"],"confirm":"remove 3 from audible"}""")]
	[DataRow("""{"asins":["B000000001","B000000002"],"confirm":"Remove 2 from audible"}""")]
	[DataRow("""{"asins":["B000000001","B000000002"],"confirm":"remove 2 from audible "}""")]
	[DataRow("""{"asins":["B000000001","B000000001"],"confirm":"remove 2 from audible"}""")]
	public async Task Remove_from_audible_is_400_confirmation_required_for_a_wrong_or_missing_phrase(string body)
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var response = await PostRemoval(h, body);

		Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
		using var doc = await Body(response);
		Assert.AreEqual("confirmationRequired", doc.RootElement.GetProperty("code").GetString());
		var expected = body.Contains("B000000001\",\"B000000001") ? "remove 1 from audible" : "remove 2 from audible";
		StringAssert.Contains(doc.RootElement.GetProperty("error").GetString(), $"\"{expected}\"");
		Assert.IsFalse(h.Data.Calls.Any(c => c.StartsWith("removefromaudible")));
	}

	[TestMethod]
	[DataRow(null)]
	[DataRow("{}")]
	[DataRow("""{"asins":[],"confirm":"remove 0 from audible"}""")]
	[DataRow("""{"asins":[" "],"confirm":"remove 1 from audible"}""")]
	public async Task Remove_from_audible_is_400_without_asins(string? body)
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var response = await h.Http.SendAsync(Send(HttpMethod.Post, $"{h.Base}/api/library/remove-from-audible", body));

		Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
		using var doc = await Body(response);
		Assert.AreNotEqual("confirmationRequired", doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
		Assert.IsFalse(h.Data.Calls.Any(c => c.StartsWith("removefromaudible")));
	}

	[TestMethod]
	public async Task Remove_from_audible_caps_the_request_at_100_asins()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		string Json(int n) => JsonSerializer.Serialize(new { asins = Enumerable.Range(0, n).Select(i => $"B{i:D9}"), confirm = $"remove {n} from audible" });

		Assert.AreEqual(HttpStatusCode.BadRequest, (await PostRemoval(h, Json(101))).StatusCode);
		Assert.IsFalse(h.Data.Calls.Any(c => c.StartsWith("removefromaudible")));
		Assert.AreEqual(HttpStatusCode.OK, (await PostRemoval(h, Json(100))).StatusCode);
	}

	[TestMethod]
	public async Task Remove_from_audible_publishes_the_removed_asins()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var request = new HttpRequestMessage(HttpMethod.Get, h.Base + "/api/events");
		using var response = await h.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
		using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
		await PostRemoval(h, """{"asins":["B000000001"],"confirm":"remove 1 from audible"}""");

		string? current = null, data = null;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (data is null)
		{
			var line = await reader.ReadLineAsync(timeout.Token);
			Assert.IsNotNull(line);
			if (line.StartsWith("event: "))
				current = line[7..];
			else if (line.StartsWith("data: ") && current == "BooksRemovedFromAudible")
				data = line[6..];
		}
		Assert.AreEqual("B000000001", JsonDocument.Parse(data).RootElement.GetProperty("asins")[0].GetString());
	}

	[TestMethod]
	public async Task OpenApi_remove_from_audible_dtos_require_only_non_nullable_fields_and_type_the_result_enum()
	{
		var schemas = await SchemaDocument();

		CollectionAssert.AreEquivalent(new[] { "asin", "result" }, Required(schemas, "RemoveFromAudibleItemDto"));
		CollectionAssert.AreEquivalent(new[] { "results" }, Required(schemas, "RemoveFromAudibleDto"));
		CollectionAssert.AreEquivalent(new[] { "removed", "notPlus", "notFound", "failed" },
			schemas.GetProperty("RemoveFromAudibleItemDto").GetProperty("properties").GetProperty("result").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray());
		CollectionAssert.AreEquivalent(Array.Empty<string>(), RequiredOrNone(schemas, "RemoveFromAudibleRequest"));
		Assert.IsTrue(schemas.TryGetProperty("BooksRemovedFromAudible", out _));
		Assert.IsTrue(schemas.GetProperty("BookDto").GetProperty("properties").TryGetProperty("isAudiblePlus", out _));
		Assert.IsTrue(schemas.GetProperty("BookDetailDto").GetProperty("properties").TryGetProperty("isAudiblePlus", out _));
	}
}
