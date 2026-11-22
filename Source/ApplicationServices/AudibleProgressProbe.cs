using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ApplicationServices;

/// <summary>
/// Reverse-engineering aid for <c>libationcli progress probe</c>: calls every Audible endpoint known to carry
/// listening position or history for one title and saves each response body verbatim, so the fields Libation
/// does not (yet) parse - device attribution in particular - can be looked for by hand.
/// </summary>
/// <remarks>Responses contain account data such as customer ids. They are written to local files only.</remarks>
public static class AudibleProgressProbe
{
	/// <summary>Kindle/Whispersync annotation host. Serves the "sidecar" record store that bookmarks also live in.</summary>
	public const string SidecarHost = "https://cde-ta-g7g.amazon.com";

	public sealed record Endpoint(string Name, string? Host, string RequestUri);

	public sealed record ProbeResult(Endpoint Endpoint, int? StatusCode, string FilePath, IReadOnlyList<string> InterestingPaths, string? Error);

	/// <summary>Key fragments that might identify the device, app or session that produced a position.</summary>
	private static readonly string[] InterestingKeyFragments =
		["device", "serial", "client", "player", "source", "session", "platform", "dsn"];

	public static IReadOnlyList<Endpoint> GetEndpoints(string asin, DateTimeOffset now)
	{
		var a = Uri.EscapeDataString(asin);
		var monthStart = now.AddMonths(-11).ToString("yyyy-MM", CultureInfo.InvariantCulture);
		return
		[
			new("annotations-lastpositions", null, $"{AudibleListeningProgressService.LastPositionsPath}?asins={a}"),
			new("lastpositions", null, $"/1.0/lastpositions?asins={a}"),
			new("library-item", null, $"/1.0/library/{a}?response_groups=listening_status,percent_complete,is_finished,is_playable"),
			new("sidecar", SidecarHost, $"/FionaCDEServiceEngine/sidecar?type=AUDI&key={a}"),
			new("stats-aggregates", null, $"/1.0/stats/aggregates?monthly_listening_interval_duration=12&monthly_listening_interval_start_date={monthStart}&response_groups=total_listening_stats&store=Audible"),
			new("stats-finished", null, $"/1.0/stats/status/finished?asin={a}"),
		];
	}

	public static async Task<List<ProbeResult>> RunAsync(AudibleApi.Api api, string asin, string outputDirectory)
	{
		ArgumentNullException.ThrowIfNull(api);
		Directory.CreateDirectory(outputDirectory);

		var results = new List<ProbeResult>();
		foreach (var endpoint in GetEndpoints(asin, DateTimeOffset.Now))
		{
			var filePath = Path.Combine(outputDirectory, $"{asin}.{endpoint.Name}.json");
			try
			{
				using var response = endpoint.Host is null
					? await api.AdHocAuthenticatedGetAsync(endpoint.RequestUri)
					: await api.AdHocAuthenticatedGetAsync(endpoint.RequestUri, api.Sharer.GetSharedHttpClient(endpoint.Host));
				var body = await response.Content.ReadAsStringAsync();

				string toWrite = body;
				IReadOnlyList<string> interesting = [];
				try
				{
					var token = JToken.Parse(body);
					toWrite = token.ToString(Newtonsoft.Json.Formatting.Indented);
					interesting = FindInterestingPaths(token);
				}
				catch (Newtonsoft.Json.JsonException) { /* keep non-JSON bodies verbatim */ }

				await File.WriteAllTextAsync(filePath, toWrite);
				results.Add(new(endpoint, (int)response.StatusCode, filePath, interesting, null));
			}
			catch (Exception ex)
			{
				await File.WriteAllTextAsync(filePath, ex.ToString());
				results.Add(new(endpoint, null, filePath, [], ex.Message));
			}
		}
		return results;
	}

	/// <summary>JSON paths (with values) of every property whose name hints at a device, app or session.</summary>
	public static List<string> FindInterestingPaths(JToken root)
		=> root
		.SelectTokens("$..*")
		.OfType<JValue>()
		.Where(v => v.Parent is JProperty prop
			&& InterestingKeyFragments.Any(f => prop.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
		.Select(v => $"{v.Path} = {v}")
		.Distinct()
		.ToList();
}
