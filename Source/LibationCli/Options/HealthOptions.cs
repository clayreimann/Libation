using CommandLine;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace LibationCli;

[Verb("health", HelpText = "Query a running 'serve' daemon's /health endpoint. Exits 0 on success, 1 otherwise.")]
internal class HealthOptions : OptionsBase
{
	[Option("url", Default = "http://127.0.0.1:8080/health", HelpText = "Health endpoint URL.")]
	public string Url { get; set; } = "http://127.0.0.1:8080/health";

	protected override bool RunsSetup => false;

	protected override async Task ProcessAsync()
	{
		try
		{
			using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
			using var response = await client.GetAsync(Url);
			if (response.IsSuccessStatusCode)
				return;
			Console.Error.WriteLine($"{Url} returned {(int)response.StatusCode}");
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
		{
			Console.Error.WriteLine($"{Url}: {ex.Message}");
		}
		Environment.ExitCode = (int)ExitCode.NonRunNonError;
	}
}
