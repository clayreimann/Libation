using System.Globalization;
using System.Text.RegularExpressions;

namespace LibationEngine;

/// <summary>Parses <c>1h</c>, <c>30m</c>, <c>90s</c>, <c>1d</c>, bare seconds (<c>3600</c>) or <c>hh:mm:ss</c>.</summary>
public static partial class IntervalParser
{
	[GeneratedRegex(@"^(\d+)([smhd]?)$", RegexOptions.IgnoreCase)]
	private static partial Regex Simple();

	public static bool TryParse(string? text, out TimeSpan interval)
	{
		interval = default;
		text = text?.Trim();
		if (string.IsNullOrEmpty(text))
			return false;

		if (text.Contains(':'))
		{
			if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out interval))
				return false;
		}
		else if (Simple().Match(text) is { Success: true } m && double.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var n))
		{
			interval = m.Groups[2].Value.ToLowerInvariant() switch
			{
				"m" => TimeSpan.FromMinutes(n),
				"h" => TimeSpan.FromHours(n),
				"d" => TimeSpan.FromDays(n),
				_ => TimeSpan.FromSeconds(n),
			};
		}
		else
			return false;

		return interval > TimeSpan.Zero;
	}

	public static TimeSpan Parse(string? text)
		=> TryParse(text, out var interval)
			? interval
			: throw new FormatException($"Invalid interval '{text}'. Use 1h, 30m, 90s, 3600 (seconds) or hh:mm:ss.");
}
