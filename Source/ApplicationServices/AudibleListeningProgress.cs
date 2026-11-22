using AudibleApi;
using AudibleUtilities;
using DataLayer;
using Dinah.Core;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ApplicationServices;

/// <summary>One title's last-heard position as reported by Audible, joined with what Libation knows about the book.</summary>
public sealed record ListeningProgress(
	string Asin,
	string? Title,
	string? Authors,
	string Account,
	string Locale,
	long PositionMs,
	DateTimeOffset? LastUpdated,
	long? LengthMs,
	bool IsFinished);

/// <summary>
/// Reads listening progress from Audible's undocumented <c>/1.0/annotations/lastpositions</c> endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Request: <c>GET /1.0/annotations/lastpositions?asins=A,B,C</c> on the marketplace API host, signed like any other
/// library call. Response, verified against the live API (Audible publishes no documentation):
/// </para>
/// <code>
/// {"asin_last_position_heard_annots":[{"asin":"B000000001","last_position_heard":{"last_updated":"2026-11-22 16:17:16.529","position_ms":3832820,"status":"Exists"}}],"response_groups":["always-returned"]}
/// </code>
/// <para><c>last_updated</c> has no <c>T</c> or zone; it is UTC.</para>
/// <para>
/// The response names no device: Audible keeps one synced position per title, whichever device wrote it last.
/// <c>libationcli progress probe</c> dumps this and the neighbouring endpoints raw, to look for device attribution.
/// </para>
/// </remarks>
public static class AudibleListeningProgressService
{
	public const string LastPositionsPath = "/1.0/annotations/lastpositions";

	/// <summary>Batch size used by existing clients of this endpoint. Larger batches are untested.</summary>
	public const int BatchSize = 25;

	/// <summary>Raw position for one ASIN, before it is joined with Libation's library data.</summary>
	public sealed record LastPosition(string Asin, long PositionMs, DateTimeOffset? LastUpdated, string? Status);

	/// <summary>
	/// Parse a lastpositions response. Entries whose status is not <c>Exists</c> (Audible has no position on file)
	/// are dropped. Both the observed <c>_annots</c> key and the <c>_feats</c> spelling are accepted.
	/// </summary>
	public static List<LastPosition> ParseLastPositions(JObject response)
	{
		ArgumentNullException.ThrowIfNull(response);

		var entries = response["asin_last_position_heard_annots"] as JArray
			?? response["asin_last_position_heard_feats"] as JArray
			?? [];

		var results = new List<LastPosition>();
		foreach (var entry in entries.OfType<JObject>())
		{
			var asin = entry.Value<string>("asin");
			if (string.IsNullOrWhiteSpace(asin) || entry["last_position_heard"] is not JObject heard)
				continue;

			var status = heard.Value<string>("status");
			if (!string.Equals(status, "Exists", StringComparison.OrdinalIgnoreCase))
				continue;

			var positionMs = heard["position_ms"]?.Type is JTokenType.Integer or JTokenType.Float
				? heard.Value<long>("position_ms")
				: 0;

			results.Add(new LastPosition(asin, positionMs, ParseTimestamp(heard["last_updated"]), status));
		}
		return results;
	}

	private static DateTimeOffset? ParseTimestamp(JToken? token)
	{
		if (token is null || token.Type == JTokenType.Null)
			return null;
		// JObject.Parse turns ISO strings into DateTime (or DateTimeOffset, depending on settings).
		if (token is JValue { Value: DateTimeOffset dto })
			return dto;
		if (token is JValue { Value: DateTime dt })
			return new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt);

		return DateTimeOffset.TryParse(token.Value<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
			? parsed
			: null;
	}

	/// <summary>Fetch raw positions for <paramref name="asins"/>, in batches of <see cref="BatchSize"/>.</summary>
	public static async Task<List<LastPosition>> GetLastPositionsAsync(AudibleApi.Api api, IEnumerable<string> asins, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(api);

		var results = new List<LastPosition>();
		foreach (var batch in asins.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(BatchSize))
		{
			cancellationToken.ThrowIfCancellationRequested();

			var requestUri = $"{LastPositionsPath}?asins={string.Join(',', batch.Select(Uri.EscapeDataString))}";
			using var response = await api.AdHocAuthenticatedGetAsync(requestUri);
			var body = await response.Content.ReadAsStringAsync(cancellationToken);

			if (!response.IsSuccessStatusCode)
				throw new HttpRequestException($"Audible returned {(int)response.StatusCode} ({response.StatusCode}) for {LastPositionsPath}. Body: {Truncate(body)}");

			results.AddRange(ParseLastPositions(JObject.Parse(body)));
		}
		return results;
	}

	/// <summary>
	/// Fetch progress for <paramref name="libraryBooks"/>, one API session per account and marketplace.
	/// Titles Audible has no position for are omitted.
	/// </summary>
	/// <param name="onAccountError">Called when one account/marketplace fails; the others still run.</param>
	public static async Task<List<ListeningProgress>> GetProgressAsync(
		IEnumerable<LibraryBook> libraryBooks,
		bool allowInteractiveLogin,
		Action<string, Exception>? onAccountError = null,
		CancellationToken cancellationToken = default)
	{
		var groups = libraryBooks
			.Where(lb => !string.IsNullOrWhiteSpace(lb.Book.AudibleProductId))
			.GroupBy(lb => (lb.Account, lb.Book.Locale))
			.ToList();

		var results = new List<ListeningProgress>();
		if (groups.Count == 0)
			return results;

		using var persister = AudibleApiStorage.GetAccountsSettingsPersister();

		foreach (var group in groups)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var (accountId, locale) = group.Key;
			var label = $"{accountId.ToMask()} ({locale})";

			try
			{
				var account = persister.AccountsSettings.GetAccount(accountId, locale)
					?? throw new InvalidOperationException($"Account {label} is not configured in Libation; skipped {group.Count()} title(s).");

				var apiExtended = await ApiExtended.CreateAsync(account, allowInteractiveLogin, Localization.Get(locale));
				var books = group.ToDictionary(lb => lb.Book.AudibleProductId, StringComparer.OrdinalIgnoreCase);
				var positions = await GetLastPositionsAsync(apiExtended.Api, books.Keys, cancellationToken);

				foreach (var position in positions)
				{
					if (!books.TryGetValue(position.Asin, out var lb))
						continue;

					results.Add(new ListeningProgress(
						Asin: lb.Book.AudibleProductId,
						Title: lb.Book.TitleWithSubtitle,
						Authors: lb.Book.AuthorNames,
						Account: lb.Account,
						Locale: lb.Book.Locale,
						PositionMs: position.PositionMs,
						LastUpdated: position.LastUpdated,
						LengthMs: lb.Book.LengthInMinutes > 0 ? lb.Book.LengthInMinutes * 60_000L : null,
						IsFinished: lb.Book.UserDefinedItem.IsFinished));
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				Serilog.Log.Logger.Error(ex, "Failed to read Audible listening progress for {Account}", label);
				if (onAccountError is null)
					throw;
				onAccountError(label, ex);
			}
		}
		return results;
	}

	/// <summary>Serialize progress for <c>progress export</c>. Times are ISO 8601; positions are milliseconds.</summary>
	public static JObject ToJson(IEnumerable<ListeningProgress> progress, DateTimeOffset exportTime)
		=> new()
		{
			{ "exportTime", exportTime.ToString("o", CultureInfo.InvariantCulture) },
			{ "source", "audible" + LastPositionsPath },
			{ "items", new JArray(progress.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).Select(p => new JObject
				{
					{ "asin", p.Asin },
					{ "title", p.Title },
					{ "authors", p.Authors },
					{ "account", p.Account },
					{ "locale", p.Locale },
					{ "positionMs", p.PositionMs },
					{ "lastUpdated", p.LastUpdated?.ToString("o", CultureInfo.InvariantCulture) },
					{ "lengthMs", p.LengthMs },
					{ "isFinished", p.IsFinished },
				})) },
		};

	internal static string Truncate(string? text, int maxLength = 280)
		=> string.IsNullOrEmpty(text) ? "(empty)" : text.Length <= maxLength ? text : text[..maxLength] + "...";
}
