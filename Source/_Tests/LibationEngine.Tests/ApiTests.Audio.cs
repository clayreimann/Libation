using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

public partial class ApiTests
{
	private const string AudioAsin = "B000000001";

	private sealed class AudioRig : IDisposable
	{
		public readonly TempDir Dir = new();

		public string Add(string fixture, string name)
		{
			var path = Dir.File(name);
			File.Copy(AudioFilesTests.Fixture(fixture), path);
			return path;
		}

		public void Dispose() => Dir.Dispose();
	}

	private static async Task<(JsonElement Json, HttpResponseMessage Response)> GetJson(Harness h, string path)
	{
		var response = await h.Http.GetAsync(h.Base + path);
		return (JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone(), response);
	}

	private static async Task<HttpResponseMessage> Range(Harness h, string path, long? from, long? to)
	{
		var request = new HttpRequestMessage(HttpMethod.Get, h.Base + path);
		request.Headers.Range = new RangeHeaderValue(from, to);
		return await h.Http.SendAsync(request);
	}

	[TestMethod]
	public async Task Audio_files_lists_a_single_file_with_its_type_size_and_duration()
	{
		using var rig = new AudioRig();
		var path = rig.Add("chapters.m4b", "Book.m4b");
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Data.Audio[AudioAsin] = new(AudioOutcome.Ok, [path]);

		var (json, response) = await GetJson(h, $"/api/library/{AudioAsin}/audio/files");

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		Assert.AreEqual(1, json.GetArrayLength());
		var file = json[0];
		CollectionAssert.AreEquivalent(new[] { "index", "name", "contentType", "sizeBytes", "durationSec" }, file.EnumerateObject().Select(p => p.Name).ToArray());
		Assert.AreEqual(0, file.GetProperty("index").GetInt32());
		Assert.AreEqual("Book.m4b", file.GetProperty("name").GetString());
		Assert.AreEqual("audio/mp4", file.GetProperty("contentType").GetString());
		Assert.AreEqual(new FileInfo(path).Length, file.GetProperty("sizeBytes").GetInt64());
		Assert.AreEqual(4.04, file.GetProperty("durationSec").GetDouble(), 0.1);
	}

	[TestMethod]
	public async Task Audio_files_lists_every_part_of_a_split_book_in_play_order()
	{
		using var rig = new AudioRig();
		string[] paths = [rig.Add("plain.mp3", "Book - 1.mp3"), rig.Add("plain.mp3", "Book - 2.mp3"), rig.Add("chapters.mp3", "Book - 3.mp3")];
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Data.Audio[AudioAsin] = new(AudioOutcome.Ok, paths);

		var (json, _) = await GetJson(h, $"/api/library/{AudioAsin}/audio/files");

		CollectionAssert.AreEqual(new[] { "Book - 1.mp3", "Book - 2.mp3", "Book - 3.mp3" }, json.EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToArray());
		CollectionAssert.AreEqual(new[] { 0, 1, 2 }, json.EnumerateArray().Select(f => f.GetProperty("index").GetInt32()).ToArray());
		Assert.IsTrue(json.EnumerateArray().All(f => f.GetProperty("contentType").GetString() == "audio/mpeg"));
	}

	[TestMethod]
	public async Task Audio_files_reports_a_null_duration_when_the_file_cannot_be_read()
	{
		using var rig = new AudioRig();
		var path = rig.Dir.File("Broken.m4b");
		File.WriteAllText(path, "not audio");
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Data.Audio[AudioAsin] = new(AudioOutcome.Ok, [path]);

		var (json, response) = await GetJson(h, $"/api/library/{AudioAsin}/audio/files");

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		Assert.AreEqual(JsonValueKind.Null, json[0].GetProperty("durationSec").ValueKind);
	}

	[TestMethod]
	[DataRow("audio/files")]
	[DataRow("audio")]
	[DataRow("chapters")]
	public async Task Audio_routes_are_404_with_an_error_when_the_book_is_unknown_not_liberated_or_has_no_files(string route)
	{
		using var rig = new AudioRig();
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Data.Audio["B0NOTLIBER1"] = new(AudioOutcome.NotLiberated);
		h.Data.Audio["B0NOFILES01"] = new(AudioOutcome.FilesMissing);
		h.Data.Audio["B0GONE00001"] = new(AudioOutcome.Ok, [rig.Dir.File("gone.m4b")]);

		foreach (var asin in new[] { "B0UNKNOWN01", "B0NOTLIBER1", "B0NOFILES01", "B0GONE00001" })
		{
			var (json, response) = await GetJson(h, $"/api/library/{asin}/{route}");

			Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, $"{asin} {route}");
			Assert.IsFalse(string.IsNullOrEmpty(json.GetProperty("error").GetString()), $"{asin} {route}");
		}
	}

	[TestMethod]
	public async Task Audio_serves_the_whole_file_with_caching_and_range_headers()
	{
		using var rig = new AudioRig();
		var path = rig.Add("chapters.m4b", "Book.m4b");
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Data.Audio[AudioAsin] = new(AudioOutcome.Ok, [path]);

		var response = await h.Http.GetAsync($"{h.Base}/api/library/{AudioAsin}/audio");

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		CollectionAssert.AreEqual(await File.ReadAllBytesAsync(path), await response.Content.ReadAsByteArrayAsync());
		Assert.AreEqual("audio/mp4", response.Content.Headers.ContentType!.MediaType);
		Assert.AreEqual(new FileInfo(path).Length, response.Content.Headers.ContentLength);
		Assert.AreEqual("bytes", response.Headers.AcceptRanges.Single());
		Assert.IsNotNull(response.Headers.ETag);
		Assert.IsNotNull(response.Content.Headers.LastModified);
		Assert.AreEqual("private", response.Headers.CacheControl!.ToString());
		Assert.IsFalse(response.Content.Headers.ContentEncoding.Any());
	}

	[TestMethod]
	public async Task Audio_answers_a_range_with_206_and_exactly_those_bytes()
	{
		using var rig = new AudioRig();
		var path = rig.Add("chapters.m4b", "Book.m4b");
		var bytes = await File.ReadAllBytesAsync(path);
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Data.Audio[AudioAsin] = new(AudioOutcome.Ok, [path]);
		var url = $"/api/library/{AudioAsin}/audio";

		var middle = await Range(h, url, 100, 199);
		Assert.AreEqual(HttpStatusCode.PartialContent, middle.StatusCode);
		Assert.AreEqual($"bytes 100-199/{bytes.Length}", middle.Content.Headers.ContentRange!.ToString());
		Assert.AreEqual(100, middle.Content.Headers.ContentLength);
		CollectionAssert.AreEqual(bytes[100..200], await middle.Content.ReadAsByteArrayAsync());
		Assert.AreEqual("bytes", middle.Headers.AcceptRanges.Single());
		Assert.IsNotNull(middle.Headers.ETag);
		Assert.IsNotNull(middle.Content.Headers.LastModified);
		Assert.AreEqual("private", middle.Headers.CacheControl!.ToString());
		Assert.AreEqual("audio/mp4", middle.Content.Headers.ContentType!.MediaType);

		var tail = await Range(h, url, bytes.Length - 10, null);
		Assert.AreEqual(HttpStatusCode.PartialContent, tail.StatusCode);
		CollectionAssert.AreEqual(bytes[^10..], await tail.Content.ReadAsByteArrayAsync());

		var suffix = await Range(h, url, null, 5);
		Assert.AreEqual(HttpStatusCode.PartialContent, suffix.StatusCode);
		CollectionAssert.AreEqual(bytes[^5..], await suffix.Content.ReadAsByteArrayAsync());

		var beyond = await Range(h, url, bytes.Length + 50, bytes.Length + 60);
		Assert.AreEqual(HttpStatusCode.RequestedRangeNotSatisfiable, beyond.StatusCode);
	}

	[TestMethod]
	public async Task Audio_honours_a_matching_if_none_match_and_ignores_a_stale_if_range()
	{
		using var rig = new AudioRig();
		var path = rig.Add("plain.mp3", "Book.mp3");
		var bytes = await File.ReadAllBytesAsync(path);
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Data.Audio[AudioAsin] = new(AudioOutcome.Ok, [path]);
		var url = $"{h.Base}/api/library/{AudioAsin}/audio";
		var etag = (await h.Http.GetAsync(url)).Headers.ETag!;

		var cached = new HttpRequestMessage(HttpMethod.Get, url);
		cached.Headers.IfNoneMatch.Add(etag);
		Assert.AreEqual(HttpStatusCode.NotModified, (await h.Http.SendAsync(cached)).StatusCode);

		var stale = new HttpRequestMessage(HttpMethod.Get, url);
		stale.Headers.Range = new RangeHeaderValue(0, 9);
		stale.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"other\""));
		var whole = await h.Http.SendAsync(stale);
		Assert.AreEqual(HttpStatusCode.OK, whole.StatusCode);
		Assert.AreEqual(bytes.Length, (await whole.Content.ReadAsByteArrayAsync()).Length);
	}

	[TestMethod]
	public async Task Audio_serves_an_mp3_as_audio_mpeg()
	{
		using var rig = new AudioRig();
		var path = rig.Add("plain.mp3", "Book.mp3");
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Data.Audio[AudioAsin] = new(AudioOutcome.Ok, [path]);

		var response = await Range(h, $"/api/library/{AudioAsin}/audio", 0, 3);

		Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
		Assert.AreEqual("audio/mpeg", response.Content.Headers.ContentType!.MediaType);
	}

	[TestMethod]
	public async Task Audio_picks_a_part_of_a_split_book_by_index_and_404s_a_bad_one()
	{
		using var rig = new AudioRig();
		var first = rig.Add("plain.mp3", "Book - 1.mp3");
		var second = rig.Add("chapters.mp3", "Book - 2.mp3");
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Data.Audio[AudioAsin] = new(AudioOutcome.Ok, [first, second]);
		var url = $"{h.Base}/api/library/{AudioAsin}/audio";

		CollectionAssert.AreEqual(await File.ReadAllBytesAsync(first), await h.Http.GetByteArrayAsync(url));
		CollectionAssert.AreEqual(await File.ReadAllBytesAsync(first), await h.Http.GetByteArrayAsync(url + "?file=0"));
		CollectionAssert.AreEqual(await File.ReadAllBytesAsync(second), await h.Http.GetByteArrayAsync(url + "?file=1"));

		foreach (var bad in new[] { "2", "-1", "99", "2147483647" })
		{
			var response = await h.Http.GetAsync($"{url}?file={bad}");
			Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, bad);
			Assert.IsFalse(string.IsNullOrEmpty(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString()), bad);
		}
		Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Http.GetAsync(url + "?file=abc")).StatusCode);
	}

	[TestMethod]
	public async Task Audio_never_takes_a_path_from_the_client()
	{
		using var rig = new AudioRig();
		var path = rig.Add("plain.mp3", "Book.mp3");
		var secret = rig.Dir.File("secret.txt");
		File.WriteAllText(secret, "do not serve");
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Data.Audio[AudioAsin] = new(AudioOutcome.Ok, [path]);

		foreach (var query in new[] { $"?path={Uri.EscapeDataString(secret)}", $"?file={Uri.EscapeDataString(secret)}", "?file=../secret.txt" })
		{
			var response = await h.Http.GetAsync($"{h.Base}/api/library/{AudioAsin}/audio{query}");
			CollectionAssert.AreNotEqual(await File.ReadAllBytesAsync(secret), await response.Content.ReadAsByteArrayAsync(), query);
		}
		CollectionAssert.AreEqual(await File.ReadAllBytesAsync(path), await h.Http.GetByteArrayAsync($"{h.Base}/api/library/{AudioAsin}/audio?path={Uri.EscapeDataString(secret)}"));
	}

	[TestMethod]
	public async Task Audio_routes_need_the_bearer_token_including_for_ranges()
	{
		using var rig = new AudioRig();
		var path = rig.Add("plain.mp3", "Book.mp3");
		await using var h = await TokenHarness();
		h.Data.Audio[AudioAsin] = new(AudioOutcome.Ok, [path]);

		foreach (var route in new[] { "audio/files", "audio", "chapters" })
		{
			var url = $"{h.Base}/api/library/{AudioAsin}/{route}";
			Assert.AreEqual(HttpStatusCode.Unauthorized, (await h.Http.SendAsync(Send(HttpMethod.Get, url))).StatusCode, route);
			Assert.AreEqual(HttpStatusCode.OK, (await h.Http.SendAsync(Authed(HttpMethod.Get, url))).StatusCode, route);
		}

		var unauthenticated = new HttpRequestMessage(HttpMethod.Get, $"{h.Base}/api/library/{AudioAsin}/audio");
		unauthenticated.Headers.Range = new RangeHeaderValue(0, 9);
		Assert.AreEqual(HttpStatusCode.Unauthorized, (await h.Http.SendAsync(unauthenticated)).StatusCode);

		var authed = Authed(HttpMethod.Get, $"{h.Base}/api/library/{AudioAsin}/audio");
		authed.Headers.Range = new RangeHeaderValue(0, 9);
		var partial = await h.Http.SendAsync(authed);
		Assert.AreEqual(HttpStatusCode.PartialContent, partial.StatusCode);
		Assert.AreEqual(10, (await partial.Content.ReadAsByteArrayAsync()).Length);
	}

	[TestMethod]
	public async Task Chapters_come_from_the_embedded_chapters_of_a_single_file()
	{
		using var rig = new AudioRig();
		var path = rig.Add("chapters.m4b", "Book.m4b");
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Data.Audio[AudioAsin] = new(AudioOutcome.Ok, [path]);

		var (json, response) = await GetJson(h, $"/api/library/{AudioAsin}/chapters");

		Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
		CollectionAssert.AreEquivalent(new[] { "index", "title", "startSec", "durationSec", "fileIndex", "fileStartSec" }, json[0].EnumerateObject().Select(p => p.Name).ToArray());
		CollectionAssert.AreEqual(new[] { "Opening", "Middle", "Ending" }, json.EnumerateArray().Select(c => c.GetProperty("title").GetString()).ToArray());
		CollectionAssert.AreEqual(new[] { 0d, 1, 2.5 }, json.EnumerateArray().Select(c => Math.Round(c.GetProperty("startSec").GetDouble(), 2)).ToArray());
		Assert.IsTrue(json.EnumerateArray().All(c => c.GetProperty("fileIndex").GetInt32() == 0));
	}

	[TestMethod]
	public async Task Chapters_fall_back_to_the_cue_sheet_beside_the_book()
	{
		using var rig = new AudioRig();
		var path = rig.Add("plain.mp3", "Book.mp3");
		var cue = rig.Dir.File("Book.cue");
		File.WriteAllText(cue, "FILE \"Book.mp3\" MP3\nTRACK 1 AUDIO\n  TITLE \"First\"\n  INDEX 01 0:00:00\nTRACK 2 AUDIO\n  TITLE \"Second\"\n  INDEX 01 0:01:00\n");
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Data.Audio[AudioAsin] = new(AudioOutcome.Ok, [path], cue);

		var (json, _) = await GetJson(h, $"/api/library/{AudioAsin}/chapters");

		CollectionAssert.AreEqual(new[] { "First", "Second" }, json.EnumerateArray().Select(c => c.GetProperty("title").GetString()).ToArray());
		Assert.AreEqual(1, json[1].GetProperty("startSec").GetDouble());
		Assert.AreEqual(1, json[1].GetProperty("fileStartSec").GetDouble());
	}

	[TestMethod]
	public async Task Chapters_fall_back_to_one_per_file_titled_by_file_name()
	{
		using var rig = new AudioRig();
		string[] paths = [rig.Add("plain.m4b", "Book - 01 - Intro.m4b"), rig.Add("plain.m4b", "Book - 02 - Rest.m4b")];
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Data.Audio[AudioAsin] = new(AudioOutcome.Ok, paths, rig.Dir.File("missing.cue"));

		var (json, _) = await GetJson(h, $"/api/library/{AudioAsin}/chapters");

		var length = AudioFiles.ReadDuration(paths[0])!.Value;
		CollectionAssert.AreEqual(new[] { "Book - 01 - Intro", "Book - 02 - Rest" }, json.EnumerateArray().Select(c => c.GetProperty("title").GetString()).ToArray());
		CollectionAssert.AreEqual(new[] { 0, 1 }, json.EnumerateArray().Select(c => c.GetProperty("fileIndex").GetInt32()).ToArray());
		CollectionAssert.AreEqual(new[] { 0d, length }, json.EnumerateArray().Select(c => c.GetProperty("startSec").GetDouble()).ToArray());
		CollectionAssert.AreEqual(new[] { 0d, 0 }, json.EnumerateArray().Select(c => c.GetProperty("fileStartSec").GetDouble()).ToArray());
		Assert.AreEqual(length, json[0].GetProperty("durationSec").GetDouble());
	}

	[TestMethod]
	public async Task Chapters_of_a_split_book_are_cumulative_across_files_and_relative_within_each()
	{
		using var rig = new AudioRig();
		string[] paths = [rig.Add("plain.mp3", "Book - 1.mp3"), rig.Add("chapters.mp3", "Book - 2.mp3")];
		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		h.Data.Audio[AudioAsin] = new(AudioOutcome.Ok, paths);

		var (json, _) = await GetJson(h, $"/api/library/{AudioAsin}/chapters");

		var offset = AudioFiles.ReadDuration(paths[0])!.Value;
		CollectionAssert.AreEqual(new[] { "Book - 1", "Opening", "Middle", "Ending" }, json.EnumerateArray().Select(c => c.GetProperty("title").GetString()).ToArray());
		CollectionAssert.AreEqual(new[] { 0, 1, 1, 1 }, json.EnumerateArray().Select(c => c.GetProperty("fileIndex").GetInt32()).ToArray());
		CollectionAssert.AreEqual(new[] { 0d, offset, offset + 1, offset + 2.5 }, json.EnumerateArray().Select(c => c.GetProperty("startSec").GetDouble()).ToArray());
		CollectionAssert.AreEqual(new[] { 0d, 0, 1, 2.5 }, json.EnumerateArray().Select(c => c.GetProperty("fileStartSec").GetDouble()).ToArray());
	}

	[TestMethod]
	public async Task OpenApi_audio_dtos_require_only_non_nullable_properties()
	{
		var schemas = await SchemaDocument();

		CollectionAssert.AreEquivalent(new[] { "index", "name", "contentType", "sizeBytes" }, Required(schemas, "AudioFileDto"));
		CollectionAssert.AreEquivalent(new[] { "index", "title", "startSec", "fileIndex", "fileStartSec" }, Required(schemas, "ChapterDto"));

		await using var h = new Harness(api: true);
		await h.WaitIdleAfterFirstCycle();
		using var doc = JsonDocument.Parse(await h.Http.GetStringAsync(h.Base + "/api/openapi.json"));
		var paths = doc.RootElement.GetProperty("paths");
		var audio = paths.GetProperty("/api/library/{asin}/audio").GetProperty("get").GetProperty("responses");
		Assert.IsTrue(audio.GetProperty("200").GetProperty("content").TryGetProperty("audio/mp4", out var media));
		Assert.AreEqual("binary", media.GetProperty("schema").GetProperty("format").GetString());
		Assert.IsTrue(audio.TryGetProperty("206", out _));
		Assert.IsTrue(audio.TryGetProperty("404", out _));
		Assert.IsTrue(paths.TryGetProperty("/api/library/{asin}/audio/files", out _));
		Assert.IsTrue(paths.TryGetProperty("/api/library/{asin}/chapters", out _));
	}
}
