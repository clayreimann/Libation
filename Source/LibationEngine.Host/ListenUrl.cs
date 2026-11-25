using LibationEngine;
namespace LibationEngine.Host;

public static class ListenUrl
{
	/// <summary>Validates a listen URL for Kestrel; host <c>0.0.0.0</c>, <c>*</c> or <c>+</c> becomes <c>*</c> (all interfaces).</summary>
	public static string Normalize(string listenUrl)
	{
		var text = listenUrl.Replace("://*", "://wildcard").Replace("://+", "://wildcard");
		if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
			throw new FormatException($"Invalid listen URL '{listenUrl}'. Use e.g. http://127.0.0.1:8080");

		var host = uri.Host is "wildcard" or "0.0.0.0" ? "*" : uri.Host;
		return $"{uri.Scheme}://{host}:{uri.Port}";
	}

	public static bool IsLoopback(string listenUrl)
	{
		var normalized = Normalize(listenUrl);
		return !normalized.Contains("://*") && new Uri(normalized).IsLoopback;
	}
}
