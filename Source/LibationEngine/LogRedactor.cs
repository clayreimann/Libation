using System.Text.RegularExpressions;

namespace LibationEngine;

/// <summary>Strips URL queries, credentials and token-looking values from text that leaves the process.</summary>
public static partial class LogRedactor
{
	public const int MaxLength = 2000;

	[GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
	private static partial Regex Url();

	[GeneratedRegex(@"\b(bearer|basic)\s+[A-Za-z0-9._~+/=\-]+", RegexOptions.IgnoreCase)]
	private static partial Regex Scheme();

	[GeneratedRegex(@"\b(access[_-]?token|refresh[_-]?token|adp[_-]?token|api[_-]?key|apikey|token|password|passwd|secret|authorization|cookie|private[_-]?key)\b(\s*[=:]\s*)(?:(?:bearer|basic)\s+)?[^\s,;&""']+", RegexOptions.IgnoreCase)]
	private static partial Regex Named();

	public static string Redact(string text)
	{
		text = Url().Replace(text, m => StripUrl(m.Value));
		text = Named().Replace(text, "$1$2[redacted]");
		text = Scheme().Replace(text, "$1 [redacted]");
		return text.Length > MaxLength ? text[..MaxLength] + "…" : text;
	}

	private static string StripUrl(string url)
	{
		var cut = url.IndexOfAny(['?', '#']);
		if (cut >= 0)
			url = url[..cut];

		var scheme = url.IndexOf("://", StringComparison.Ordinal) + 3;
		var slash = url.IndexOf('/', scheme);
		var authority = slash < 0 ? url[scheme..] : url[scheme..slash];
		var at = authority.LastIndexOf('@');
		return at < 0 ? url : url[..scheme] + url[(scheme + at + 1)..];
	}
}
