using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LibationEngine;

public sealed record MetricsValues(long RunTimestamp, long SuccessTimestamp, int ScanExitCode, int LiberateExitCode)
{
	/// <summary>One entry per account a scan has reported on; empty writes no per-account metrics.</summary>
	public IReadOnlyList<AccountScanMetric> Accounts { get; init; } = [];
}

/// <summary>An account's latest scan, as exported. <paramref name="Account"/> is a log-safe label, never the login.</summary>
public sealed record AccountScanMetric(string Account, string Locale, bool Succeeded, bool NeedsLogin, long LastRunTimestamp, long LastSuccessTimestamp);

public sealed class MetricsWriter(string path)
{
	private const string AccountSuccess = "libation_account_scan_success";
	private const string AccountNeedsLogin = "libation_account_needs_login";
	private const string AccountLastRun = "libation_account_scan_last_run_timestamp_seconds";
	private const string AccountLastSuccess = "libation_account_scan_last_success_timestamp_seconds";

	public static string Format(MetricsValues v) => FormatTotals(v) + FormatAccounts(v.Accounts);

	private static string FormatTotals(MetricsValues v) => $"""
		# HELP libation_scan_last_run_timestamp_seconds Unix time the last scan cycle ran.
		# TYPE libation_scan_last_run_timestamp_seconds gauge
		libation_scan_last_run_timestamp_seconds {v.RunTimestamp}
		# HELP libation_scan_last_success_timestamp_seconds Unix time of the last successful scan, 0 if none yet.
		# TYPE libation_scan_last_success_timestamp_seconds gauge
		libation_scan_last_success_timestamp_seconds {v.SuccessTimestamp}
		# HELP libation_scan_last_exit_code Exit code of the last scan.
		# TYPE libation_scan_last_exit_code gauge
		libation_scan_last_exit_code {v.ScanExitCode}
		# HELP libation_liberate_last_exit_code Exit code of the last liberate run; unchanged when the scan failed.
		# TYPE libation_liberate_last_exit_code gauge
		libation_liberate_last_exit_code {v.LiberateExitCode}

		""".ReplaceLineEndings("\n");

	private static string FormatAccounts(IReadOnlyList<AccountScanMetric> accounts)
	{
		if (accounts.Count == 0)
			return "";

		var ordered = accounts.OrderBy(a => a.Account, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Locale, StringComparer.OrdinalIgnoreCase).ToList();
		var sb = new StringBuilder();
		section(AccountSuccess, "1 if the account's last scan succeeded, 0 if it failed.", a => a.Succeeded ? 1 : 0);
		section(AccountNeedsLogin, "1 if the account's last scan failed because Audible needs it to sign in again.", a => a.NeedsLogin ? 1 : 0);
		section(AccountLastRun, "Unix time the account was last scanned.", a => a.LastRunTimestamp);
		section(AccountLastSuccess, "Unix time of the account's last successful scan, 0 if none yet.", a => a.LastSuccessTimestamp);
		return sb.ToString();

		void section(string name, string help, Func<AccountScanMetric, long> value)
		{
			sb.Append($"# HELP {name} {help}\n# TYPE {name} gauge\n");
			foreach (var a in ordered)
				sb.Append(CultureInfo.InvariantCulture, $"{name}{{account=\"{Escape(a.Account)}\",locale=\"{Escape(a.Locale)}\"}} {value(a)}\n");
		}
	}

	/// <summary>Prometheus label-value escaping: backslash, double quote and newline.</summary>
	private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

	private static string Unescape(string value)
	{
		var sb = new StringBuilder(value.Length);
		for (int i = 0; i < value.Length; i++)
		{
			if (value[i] == '\\' && i + 1 < value.Length)
			{
				var next = value[++i];
				sb.Append(next == 'n' ? '\n' : next);
			}
			else
				sb.Append(value[i]);
		}
		return sb.ToString();
	}

	private static readonly Regex AccountLine = new(
		"""^(?<name>libation_account_[a-z_]+)\{account="(?<account>(?:[^"\\]|\\.)*)",locale="(?<locale>(?:[^"\\]|\\.)*)"\} (?<value>-?\d+)$""",
		RegexOptions.CultureInvariant);

	/// <summary>Reads the per-account metrics from an existing file, so a restart keeps each account's last success.</summary>
	public IReadOnlyList<AccountScanMetric> ReadPreviousAccounts()
	{
		var found = new Dictionary<(string, string), AccountScanMetric>();
		try
		{
			foreach (var line in File.ReadLines(path))
			{
				var m = AccountLine.Match(line);
				if (!m.Success || !long.TryParse(m.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
					continue;
				var key = (Unescape(m.Groups["account"].Value), Unescape(m.Groups["locale"].Value));
				var metric = found.GetValueOrDefault(key) ?? new(key.Item1, key.Item2, false, false, 0, 0);
				found[key] = m.Groups["name"].Value switch
				{
					AccountSuccess => metric with { Succeeded = value == 1 },
					AccountNeedsLogin => metric with { NeedsLogin = value == 1 },
					AccountLastRun => metric with { LastRunTimestamp = value },
					AccountLastSuccess => metric with { LastSuccessTimestamp = value },
					_ => metric
				};
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
		return found.Values.ToList();
	}

	/// <summary>Reads success timestamp and liberate exit code from an existing file; 0 for anything missing.</summary>
	public (long SuccessTimestamp, int LiberateExitCode) ReadPrevious()
	{
		long success = 0;
		int liberate = 0;
		try
		{
			foreach (var line in File.ReadLines(path))
			{
				var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length != 2)
					continue;
				if (parts[0] == "libation_scan_last_success_timestamp_seconds")
					long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out success);
				else if (parts[0] == "libation_liberate_last_exit_code")
					int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out liberate);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
		return (success, liberate);
	}

	public void Write(MetricsValues values)
	{
		var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
		Directory.CreateDirectory(dir);
		var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
		try
		{
			File.WriteAllText(tmp, Format(values));
			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
			File.Move(tmp, path, overwrite: true);
		}
		finally
		{
			if (File.Exists(tmp))
				File.Delete(tmp);
		}
	}
}
