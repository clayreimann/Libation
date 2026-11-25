using System.Globalization;

namespace LibationEngine;

public sealed record MetricsValues(long RunTimestamp, long SuccessTimestamp, int ScanExitCode, int LiberateExitCode);

public sealed class MetricsWriter(string path)
{
	public static string Format(MetricsValues v) => $"""
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
