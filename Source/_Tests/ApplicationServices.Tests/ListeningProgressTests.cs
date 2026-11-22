using ApplicationServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using static ApplicationServices.AudiobookshelfProgressSync;

namespace ApplicationServices.Tests;

[TestClass]
public class ListeningProgressTests
{
	private static ListeningProgress Progress(
		string asin = "B0TEST0001",
		string? title = "The Book: A Subtitle",
		string? authors = "Jane Doe",
		long positionMs = 3_600_000,
		DateTimeOffset? lastUpdated = null,
		long? lengthMs = 7_200_000,
		bool isFinished = false)
		=> new(asin, title, authors, "acct", "us", positionMs, lastUpdated ?? new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), lengthMs, isFinished);

	private static AbsItem Item(string id = "li_1", string? title = "The Book", string? subtitle = "A Subtitle", string? author = "Jane Doe", string? asin = null, double? duration = 7300)
		=> new(id, title, subtitle, author, asin, duration);

	#region Audible lastpositions

	[TestMethod]
	public void ParseLastPositions_parses_real_response_as_utc()
	{
		var json = JObject.Parse("""
			{"asin_last_position_heard_annots":[{"asin":"B000000001","last_position_heard":{"last_updated":"2026-11-22 16:17:16.529","position_ms":3832820,"status":"Exists"}}],"response_groups":["always-returned"]}
			""");

		var position = AudibleListeningProgressService.ParseLastPositions(json).Single();

		Assert.AreEqual("B000000001", position.Asin);
		Assert.AreEqual(3832820, position.PositionMs);
		Assert.AreEqual(new DateTimeOffset(2026, 11, 22, 16, 17, 16, 529, TimeSpan.Zero), position.LastUpdated);
		Assert.AreEqual(TimeSpan.Zero, position.LastUpdated!.Value.Offset);
	}

	[TestMethod]
	public void ParseLastPositions_reads_existing_positions_and_drops_others()
	{
		var json = JObject.Parse("""
			{ "asin_last_position_heard_annots": [
				{ "asin": "A1", "last_position_heard": { "last_updated": "2026-11-22 16:17:16.529", "position_ms": 123456, "status": "Exists" } },
				{ "asin": "A2", "last_position_heard": { "status": "DoesNotExist" } },
				{ "asin": "A3" },
				{ "last_position_heard": { "status": "Exists", "position_ms": 1 } }
			] }
			""");

		var positions = AudibleListeningProgressService.ParseLastPositions(json);

		Assert.AreEqual(1, positions.Count);
		Assert.AreEqual("A1", positions[0].Asin);
		Assert.AreEqual(123456, positions[0].PositionMs);
		Assert.AreEqual(new DateTimeOffset(2026, 11, 22, 16, 17, 16, 529, TimeSpan.Zero), positions[0].LastUpdated);
	}

	[TestMethod]
	public void ParseLastPositions_accepts_feats_key_and_empty_response()
	{
		var feats = JObject.Parse("""{ "asin_last_position_heard_feats": [ { "asin": "A1", "last_position_heard": { "position_ms": 5, "status": "Exists" } } ] }""");
		Assert.AreEqual(5, AudibleListeningProgressService.ParseLastPositions(feats).Single().PositionMs);
		Assert.IsNull(AudibleListeningProgressService.ParseLastPositions(feats).Single().LastUpdated);

		Assert.AreEqual(0, AudibleListeningProgressService.ParseLastPositions(new JObject()).Count);
	}

	[TestMethod]
	public void ToJson_writes_positions_in_milliseconds_with_iso_dates()
	{
		var json = AudibleListeningProgressService.ToJson([Progress()], new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
		var item = (JObject)json["items"]![0]!;

		Assert.AreEqual("audible/1.0/annotations/lastpositions", json.Value<string>("source"));
		Assert.AreEqual("B0TEST0001", item.Value<string>("asin"));
		Assert.AreEqual(3_600_000, item.Value<long>("positionMs"));
		Assert.AreEqual("2026-01-02T03:04:05.0000000+00:00", item["lastUpdated"]!.ToString(Newtonsoft.Json.Formatting.None).Trim('"'));
	}

	#endregion

	#region Audiobookshelf parsing

	[TestMethod]
	public void ParseLibraryItems_reads_minified_items_and_skips_podcasts()
	{
		var json = JObject.Parse("""
			{ "results": [
				{ "id": "li_1", "mediaType": "book", "media": { "duration": 7300.5, "metadata": { "title": "T", "subtitle": "S", "authorName": "A, B", "asin": "B0X" } } },
				{ "id": "li_2", "mediaType": "podcast", "media": { "metadata": { "title": "P" } } },
				{ "id": "li_3", "mediaType": "book", "media": { "metadata": { "title": "U", "authors": [ { "name": "C" } ] } } }
			] }
			""");

		var items = ParseLibraryItems(json);

		Assert.AreEqual(2, items.Count);
		Assert.AreEqual(new AbsItem("li_1", "T", "S", "A, B", "B0X", 7300.5), items[0]);
		Assert.AreEqual("C", items[1].AuthorName);
		Assert.IsNull(items[1].DurationSeconds);
	}

	[TestMethod]
	public void ParseMyProgress_keys_by_item_and_ignores_podcast_episodes()
	{
		var json = JObject.Parse("""
			{ "mediaProgress": [
				{ "libraryItemId": "li_1", "currentTime": 12.5, "isFinished": false, "lastUpdate": 1700000000000 },
				{ "libraryItemId": "li_2", "episodeId": "ep_1", "currentTime": 1 }
			] }
			""");

		var progress = ParseMyProgress(json);

		Assert.AreEqual(1, progress.Count);
		Assert.AreEqual(12.5, progress["li_1"].CurrentTimeSeconds);
		Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), progress["li_1"].LastUpdate);
	}

	#endregion

	#region matching

	[TestMethod]
	public void FindItem_prefers_asin_over_title()
	{
		var titleTwin = Item(id: "li_title");
		var asinMatch = Item(id: "li_asin", title: "Something else", asin: "b0test0001");

		var match = FindItem(Progress(), [titleTwin, asinMatch]);

		Assert.AreEqual("li_asin", match!.Item.Id);
		Assert.AreEqual(MatchKind.Asin, match.Kind);
	}

	[TestMethod]
	public void FindItem_matches_title_with_subtitle_or_bare_title()
	{
		Assert.AreEqual(MatchKind.Title, FindItem(Progress(), [Item()])!.Kind);
		Assert.IsNotNull(FindItem(Progress(), [Item(subtitle: null)]), "bare title on server");
		Assert.IsNotNull(FindItem(Progress(title: "The Book (Unabridged)"), [Item(subtitle: null)]));
	}

	[TestMethod]
	public void FindItem_rejects_wrong_author_and_ambiguous_titles()
	{
		Assert.IsNull(FindItem(Progress(), [Item(author: "Someone Else")]));
		Assert.IsNull(FindItem(Progress(), [Item(id: "li_1"), Item(id: "li_2")]), "two equally good matches");
		Assert.IsNull(FindItem(Progress(), [Item(subtitle: "A Different Subtitle")]));
	}

	#endregion

	#region overwrite rules

	[TestMethod]
	public void Decide_writes_when_server_has_no_progress()
		=> Assert.IsNull(Decide(Progress(), null, force: false));

	[TestMethod]
	public void Decide_skips_newer_finished_or_equal_server_progress()
	{
		var audible = Progress(lastUpdated: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));

		var newer = new AbsProgress("li_1", 10, false, new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero));
		Assert.AreEqual(Outcome.ServerIsNewer, Decide(audible, newer, force: false));

		var finished = new AbsProgress("li_1", 10, true, null);
		Assert.AreEqual(Outcome.FinishedOnServer, Decide(audible, finished, force: false));

		var same = new AbsProgress("li_1", 3600 + SameSpotToleranceSeconds, false, null);
		Assert.AreEqual(Outcome.AlreadyInSync, Decide(audible, same, force: false));

		var older = new AbsProgress("li_1", 10, false, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
		Assert.IsNull(Decide(audible, older, force: false));
	}

	[TestMethod]
	public void Decide_force_overrides_every_skip()
		=> Assert.IsNull(Decide(Progress(), new AbsProgress("li_1", 3600, true, DateTimeOffset.MaxValue), force: true));

	[TestMethod]
	public void BuildProgressPayload_uses_seconds_and_server_duration()
	{
		var payload = BuildProgressPayload(Progress(), Item(duration: 7200));

		Assert.AreEqual(3600d, payload.Value<double>("currentTime"));
		Assert.AreEqual(7200d, payload.Value<double>("duration"));
		Assert.AreEqual(0.5, payload.Value<double>("progress"));
		Assert.IsNull(payload["isFinished"], "never clears a finished flag");
		Assert.AreEqual(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).ToUnixTimeMilliseconds(), payload.Value<long>("lastUpdate"));
	}

	[TestMethod]
	public void BuildProgressPayload_falls_back_to_library_length_and_sets_finished()
	{
		var payload = BuildProgressPayload(Progress(isFinished: true), Item(duration: null));

		Assert.AreEqual(7200d, payload.Value<double>("duration"));
		Assert.IsTrue(payload.Value<bool>("isFinished"));
	}

	#endregion

	[TestMethod]
	public void Probe_FindInterestingPaths_flags_device_like_fields()
	{
		var token = JToken.Parse("""{ "payload": { "records": [ { "type": "audible.last_heard", "deviceType": "A2CZJZGLK2JJVM", "startPosition": 1 } ] } }""");

		var paths = AudibleProgressProbe.FindInterestingPaths(token);

		CollectionAssert.AreEqual(new[] { "payload.records[0].deviceType = A2CZJZGLK2JJVM" }, paths);
	}
}
