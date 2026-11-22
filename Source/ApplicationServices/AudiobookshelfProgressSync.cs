using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ApplicationServices;

/// <summary>Pushes Audible listening progress to the Audiobookshelf user that owns the API token.</summary>
/// <remarks>
/// Endpoints used (all documented Audiobookshelf API):
/// <list type="bullet">
/// <item><c>GET api/libraries/{id}/items?limit=0&amp;minified=1</c> - every item, with <c>media.metadata.asin</c> and <c>media.duration</c> (seconds).</item>
/// <item><c>GET api/me</c> - the token owner's <c>mediaProgress[]</c>, with <c>lastUpdate</c> in Unix milliseconds.</item>
/// <item><c>PATCH api/me/progress/{libraryItemId}</c> - create or update progress; times are seconds.</item>
/// </list>
/// </remarks>
public static class AudiobookshelfProgressSync
{
	public sealed record AbsItem(string Id, string? Title, string? Subtitle, string? AuthorName, string? Asin, double? DurationSeconds);

	public sealed record AbsProgress(string LibraryItemId, double CurrentTimeSeconds, bool IsFinished, DateTimeOffset? LastUpdate);

	public enum MatchKind { Asin, Title }

	public sealed record Match(AbsItem Item, MatchKind Kind);

	public enum Outcome
	{
		/// <summary>Progress was written to Audiobookshelf.</summary>
		Updated,
		/// <summary>Dry run: progress would have been written.</summary>
		WouldUpdate,
		/// <summary>No Audiobookshelf item matched by ASIN, nor uniquely by title and author.</summary>
		NotOnServer,
		/// <summary>Audiobookshelf already has progress updated at or after Audible's.</summary>
		ServerIsNewer,
		/// <summary>Audiobookshelf already has the same position (within a few seconds).</summary>
		AlreadyInSync,
		/// <summary>Audiobookshelf has the book marked finished; never un-finish it without --force.</summary>
		FinishedOnServer,
		Failed,
	}

	public sealed record SyncResult(ListeningProgress Progress, Outcome Outcome, string? AbsItemId, string Message);

	/// <summary>Positions this close are treated as equal: players round differently and re-sync on resume.</summary>
	public const double SameSpotToleranceSeconds = 5;

	#region pure logic (unit tested)

	public static List<AbsItem> ParseLibraryItems(JObject response)
	{
		var results = response["results"] as JArray ?? [];
		return results
			.OfType<JObject>()
			.Where(r => !string.Equals(r.Value<string>("mediaType"), "podcast", StringComparison.OrdinalIgnoreCase))
			.Select(r =>
			{
				var media = r["media"] as JObject;
				var metadata = media?["metadata"] as JObject;
				var authorName = metadata?.Value<string>("authorName")
					?? (metadata?["authors"] is JArray authors
						? string.Join(", ", authors.Select(a => a.Value<string>("name")).Where(n => !string.IsNullOrWhiteSpace(n)))
						: null);
				return new AbsItem(
					r.Value<string>("id") ?? "",
					metadata?.Value<string>("title"),
					metadata?.Value<string>("subtitle"),
					authorName,
					metadata?.Value<string>("asin"),
					media?["duration"]?.Type is JTokenType.Float or JTokenType.Integer ? media.Value<double>("duration") : null);
			})
			.Where(i => !string.IsNullOrEmpty(i.Id))
			.ToList();
	}

	public static Dictionary<string, AbsProgress> ParseMyProgress(JObject me)
	{
		var results = new Dictionary<string, AbsProgress>(StringComparer.Ordinal);
		foreach (var p in (me["mediaProgress"] as JArray ?? []).OfType<JObject>())
		{
			var itemId = p.Value<string>("libraryItemId");
			// Podcast episodes carry an episodeId and are not something Audible progress maps onto.
			if (string.IsNullOrEmpty(itemId) || !string.IsNullOrEmpty(p.Value<string>("episodeId")))
				continue;

			DateTimeOffset? lastUpdate = p["lastUpdate"]?.Type is JTokenType.Integer or JTokenType.Float
				? DateTimeOffset.FromUnixTimeMilliseconds(p.Value<long>("lastUpdate"))
				: null;

			results[itemId] = new AbsProgress(
				itemId,
				p["currentTime"]?.Type is JTokenType.Integer or JTokenType.Float ? p.Value<double>("currentTime") : 0,
				p.Value<bool?>("isFinished") ?? false,
				lastUpdate);
		}
		return results;
	}

	/// <summary>
	/// Find the Audiobookshelf item for an Audible title. ASIN wins. Otherwise the title must match (with or
	/// without subtitle) and authors must be compatible, and exactly one item may qualify: a guess that lands
	/// progress on the wrong book is worse than no match.
	/// </summary>
	public static Match? FindItem(ListeningProgress progress, IReadOnlyList<AbsItem> items)
	{
		var byAsin = items.FirstOrDefault(i => string.Equals(i.Asin?.Trim(), progress.Asin, StringComparison.OrdinalIgnoreCase));
		if (byAsin is not null)
			return new Match(byAsin, MatchKind.Asin);

		var fullTitle = AudiobookshelfApiService.NormalizeTitle(progress.Title);
		if (string.IsNullOrWhiteSpace(fullTitle))
			return null;
		var baseTitle = AudiobookshelfApiService.GetBaseTitle(fullTitle);

		var candidates = items.Where(i =>
		{
			var title = AudiobookshelfApiService.NormalizeTitle(i.Title);
			if (string.IsNullOrWhiteSpace(title))
				return false;
			var withSubtitle = string.IsNullOrWhiteSpace(i.Subtitle) ? title : AudiobookshelfApiService.NormalizeTitle($"{i.Title}: {i.Subtitle}");

			var titleMatch = string.Equals(withSubtitle, fullTitle, StringComparison.OrdinalIgnoreCase)
				|| (string.IsNullOrWhiteSpace(i.Subtitle) && string.Equals(title, baseTitle, StringComparison.OrdinalIgnoreCase));
			if (!titleMatch)
				return false;

			var absAuthors = (i.AuthorName ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
			return AudiobookshelfApiService.AuthorsCompatible(progress.Authors, absAuthors);
		}).Take(2).ToList();

		return candidates.Count == 1 ? new Match(candidates[0], MatchKind.Title) : null;
	}

	/// <summary>Decide whether Audible's position should overwrite what Audiobookshelf has.</summary>
	public static Outcome? Decide(ListeningProgress progress, AbsProgress? existing, bool force)
	{
		if (existing is null || force)
			return null;

		if (existing.IsFinished)
			return Outcome.FinishedOnServer;

		if (Math.Abs(existing.CurrentTimeSeconds - progress.PositionMs / 1000d) <= SameSpotToleranceSeconds)
			return Outcome.AlreadyInSync;

		if (existing.LastUpdate is { } absUpdated && progress.LastUpdated is { } audibleUpdated && absUpdated >= audibleUpdated)
			return Outcome.ServerIsNewer;

		return null;
	}

	/// <summary>Body for <c>PATCH api/me/progress/{id}</c>.</summary>
	public static JObject BuildProgressPayload(ListeningProgress progress, AbsItem item)
	{
		var currentTime = progress.PositionMs / 1000d;
		var duration = item.DurationSeconds is > 0 ? item.DurationSeconds.Value : (progress.LengthMs ?? 0) / 1000d;

		var payload = new JObject { { "currentTime", currentTime } };
		if (duration > 0)
		{
			payload["duration"] = duration;
			payload["progress"] = Math.Clamp(currentTime / duration, 0, 1);
		}
		// Only ever set finished, never clear it: Audible's finished flag comes from Libation's last library scan.
		if (progress.IsFinished)
			payload["isFinished"] = true;
		if (progress.LastUpdated is { } lastUpdated)
			payload["lastUpdate"] = lastUpdated.ToUnixTimeMilliseconds();

		return payload;
	}

	#endregion

	#region network

	public static async Task<List<AbsItem>> GetLibraryItemsAsync(string serverUrl, string apiToken, string libraryId, CancellationToken cancellationToken = default)
	{
		using var client = CreateAuthorizedClient(serverUrl, apiToken);
		var json = await GetJsonAsync(client, $"api/libraries/{Uri.EscapeDataString(libraryId)}/items?limit=0&minified=1", cancellationToken);
		return ParseLibraryItems(json);
	}

	public static async Task<Dictionary<string, AbsProgress>> GetMyProgressAsync(string serverUrl, string apiToken, CancellationToken cancellationToken = default)
	{
		using var client = CreateAuthorizedClient(serverUrl, apiToken);
		return ParseMyProgress(await GetJsonAsync(client, "api/me", cancellationToken));
	}

	/// <summary>Push each progress entry to Audiobookshelf. Never throws for a single title; see <see cref="SyncResult"/>.</summary>
	public static async Task<List<SyncResult>> PushAsync(
		string serverUrl,
		string apiToken,
		string libraryId,
		IEnumerable<ListeningProgress> progress,
		bool force,
		bool dryRun,
		Action<SyncResult>? onResult = null,
		CancellationToken cancellationToken = default)
	{
		var items = await GetLibraryItemsAsync(serverUrl, apiToken, libraryId, cancellationToken);
		var existing = await GetMyProgressAsync(serverUrl, apiToken, cancellationToken);

		using var client = CreateAuthorizedClient(serverUrl, apiToken);
		var results = new List<SyncResult>();

		foreach (var p in progress)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var result = await PushOneAsync(client, p, items, existing, force, dryRun, cancellationToken);
			results.Add(result);
			onResult?.Invoke(result);
		}
		return results;
	}

	private static async Task<SyncResult> PushOneAsync(
		HttpClient client,
		ListeningProgress p,
		IReadOnlyList<AbsItem> items,
		IReadOnlyDictionary<string, AbsProgress> existing,
		bool force,
		bool dryRun,
		CancellationToken cancellationToken)
	{
		var label = $"[{p.Asin}] {p.Title}";
		if (FindItem(p, items) is not { } match)
			return new(p, Outcome.NotOnServer, null, $"{label}: not found in the Audiobookshelf library");

		var itemId = match.Item.Id;
		if (Decide(p, existing.GetValueOrDefault(itemId), force) is { } skip)
			return new(p, skip, itemId, $"{label}: {Describe(skip)}");

		var position = TimeSpan.FromMilliseconds(p.PositionMs).ToString(@"h\:mm\:ss");
		if (dryRun)
			return new(p, Outcome.WouldUpdate, itemId, $"{label}: would set position to {position} (matched by {match.Kind})");

		try
		{
			using var content = new StringContent(BuildProgressPayload(p, match.Item).ToString(Formatting.None), Encoding.UTF8, "application/json");
			using var response = await client.PatchAsync($"api/me/progress/{Uri.EscapeDataString(itemId)}", content, cancellationToken);
			if (response.IsSuccessStatusCode)
				return new(p, Outcome.Updated, itemId, $"{label}: set position to {position} (matched by {match.Kind})");

			var body = await response.Content.ReadAsStringAsync(cancellationToken);
			return new(p, Outcome.Failed, itemId, $"{label}: Audiobookshelf returned {(int)response.StatusCode} ({response.StatusCode}). {AudibleListeningProgressService.Truncate(body)}");
		}
		catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
		{
			Serilog.Log.Logger.Error(ex, "Audiobookshelf progress update failed for {Asin}", p.Asin);
			return new(p, Outcome.Failed, itemId, $"{label}: {ex.Message}");
		}
	}

	private static string Describe(Outcome outcome) => outcome switch
	{
		Outcome.ServerIsNewer => "Audiobookshelf progress is newer; skipped (use --force to overwrite)",
		Outcome.AlreadyInSync => "already in sync",
		Outcome.FinishedOnServer => "marked finished on Audiobookshelf; skipped (use --force to overwrite)",
		_ => outcome.ToString(),
	};

	private static HttpClient CreateAuthorizedClient(string serverUrl, string apiToken)
	{
		var client = AudiobookshelfApiService.CreateClient(serverUrl);
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AudiobookshelfTokenStorage.DecryptToken(apiToken) ?? "");
		client.Timeout = TimeSpan.FromMinutes(2);
		return client;
	}

	private static async Task<JObject> GetJsonAsync(HttpClient client, string requestUri, CancellationToken cancellationToken)
	{
		using var response = await client.GetAsync(requestUri, cancellationToken);
		var body = await response.Content.ReadAsStringAsync(cancellationToken);
		if (!response.IsSuccessStatusCode)
			throw new HttpRequestException($"Audiobookshelf returned {(int)response.StatusCode} ({response.StatusCode}) for {requestUri}. {AudibleListeningProgressService.Truncate(body)}");
		return JObject.Parse(body);
	}

	#endregion
}
