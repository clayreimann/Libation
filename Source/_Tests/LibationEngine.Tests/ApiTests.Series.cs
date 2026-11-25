using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

public partial class ApiTests
{
	[TestMethod]
	public async Task Series_lists_groups_and_filters_by_name()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using (var all = await Body(await h.Http.GetAsync(h.Base + "/api/library/series")))
		{
			var series = all.RootElement[0];
			Assert.AreEqual("B0SERIES01", series.GetProperty("seriesId").GetString());
			Assert.AreEqual("Saga", series.GetProperty("name").GetString());
			Assert.AreEqual(2, series.GetProperty("bookCount").GetInt32());
			Assert.AreEqual(1, series.GetProperty("liberatedCount").GetInt32());
			Assert.AreEqual("B000000002", series.GetProperty("books")[1].GetProperty("asin").GetString());
			Assert.AreEqual("2", series.GetProperty("books")[1].GetProperty("order").GetString());
		}

		using var none = await Body(await h.Http.GetAsync(h.Base + "/api/library/series?q=zzz"));
		Assert.AreEqual(0, none.RootElement.GetArrayLength());
		Assert.AreEqual("zzz", h.Data.LastSeriesQuery);
	}

	[TestMethod]
	public async Task Series_by_id_is_404_when_unknown()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using (var one = await Body(await h.Http.GetAsync(h.Base + "/api/library/series/B0SERIES01")))
			Assert.AreEqual("Saga", one.RootElement.GetProperty("name").GetString());
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.GetAsync(h.Base + "/api/library/series/nope")).StatusCode);
	}

	[TestMethod]
	public async Task Podcast_episodes_list_children_or_404()
	{
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();

		using (var episodes = await Body(await h.Http.GetAsync(h.Base + "/api/library/podcasts/B000000009/episodes?trash=true")))
		{
			Assert.AreEqual("B000000009", episodes.RootElement[0].GetProperty("parentAsin").GetString());
			Assert.IsTrue(episodes.RootElement[0].GetProperty("isPodcastEpisode").GetBoolean());
		}
		Assert.AreEqual(true, h.Data.LastFilter?.Trash);
		Assert.AreEqual(HttpStatusCode.NotFound, (await h.Http.GetAsync(h.Base + "/api/library/podcasts/B000000001/episodes")).StatusCode);
	}
}
