using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

public partial class ApiTests
{
	[TestMethod]
	public async Task Bookmarks_lists_clips_notes_and_bookmarks()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using var doc = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/library/B000000001/bookmarks"));

		Assert.AreEqual(2, doc.RootElement.GetArrayLength());
		var clip = doc.RootElement[0];
		Assert.AreEqual("Clip", clip.GetProperty("type").GetString());
		Assert.AreEqual(10.5, clip.GetProperty("start").GetDouble());
		Assert.AreEqual(20, clip.GetProperty("end").GetDouble());
		Assert.AreEqual("a note", clip.GetProperty("note").GetString());
		Assert.AreEqual("A clip", clip.GetProperty("title").GetString());
		Assert.IsTrue(clip.TryGetProperty("created", out _));
		Assert.AreEqual("Bookmark", doc.RootElement[1].GetProperty("type").GetString());
	}

	[TestMethod]
	[DataRow("csv", "text/csv", ".csv")]
	[DataRow("XLSX", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx")]
	[DataRow("json", "application/json", ".json")]
	public async Task Bookmarks_download_as_a_file_in_the_requested_format(string format, string contentType, string extension)
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		var response = await h.Http.GetAsync($"{h.Base}/api/library/B000000001/bookmarks?format={format}");

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		Assert.AreEqual(contentType, response.Content.Headers.ContentType?.MediaType);
		Assert.AreEqual("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
		StringAssert.EndsWith(response.Content.Headers.ContentDisposition?.FileName, extension);
		Assert.AreEqual($"fake-{Enum.Parse<LibationEngine.ExportFormat>(format, ignoreCase: true)}", await response.Content.ReadAsStringAsync());
	}

	[TestMethod]
	public async Task Bookmarks_download_is_204_when_the_book_has_none()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Data.BookmarksEmpty = true;

		Assert.AreEqual(HttpStatusCode.NoContent, (await h.Http.GetAsync($"{h.Base}/api/library/B000000001/bookmarks?format=csv")).StatusCode);
		using var doc = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/library/B000000001/bookmarks"));
		Assert.AreEqual(0, doc.RootElement.GetArrayLength());
	}

	[TestMethod]
	public async Task Bookmarks_are_404_for_an_unknown_book_400_for_a_bad_format_and_502_when_audible_fails()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.GetAsync(h.Base + "/api/library/nope/bookmarks")).StatusCode);
		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.GetAsync(h.Base + "/api/library/B000000001/bookmarks?format=pdf")).StatusCode);
		Assert.IsFalse(h.Data.Calls.Any(c => c.StartsWith("bookmarks:B000000001")));

		h.Data.BookmarksFail = true;
		foreach (var query in new[] { "", "?format=csv" })
		{
			var failed = await h.Http.GetAsync($"{h.Base}/api/library/B000000001/bookmarks{query}");
			Assert.AreEqual(HttpStatusCode.BadGateway, failed.StatusCode);
			using var doc = await Body(failed);
			StringAssert.Contains(doc.RootElement.GetProperty("error").GetString(), "offline");
		}
	}
}
