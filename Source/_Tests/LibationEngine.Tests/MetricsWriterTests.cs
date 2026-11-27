using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
public class MetricsWriterTests
{
	[TestMethod]
	public void Content_matches_the_loop_script_exactly()
	{
		const string expected =
			"# HELP libation_scan_last_run_timestamp_seconds Unix time the last scan cycle ran.\n" +
			"# TYPE libation_scan_last_run_timestamp_seconds gauge\n" +
			"libation_scan_last_run_timestamp_seconds 1700000100\n" +
			"# HELP libation_scan_last_success_timestamp_seconds Unix time of the last successful scan, 0 if none yet.\n" +
			"# TYPE libation_scan_last_success_timestamp_seconds gauge\n" +
			"libation_scan_last_success_timestamp_seconds 1700000050\n" +
			"# HELP libation_scan_last_exit_code Exit code of the last scan.\n" +
			"# TYPE libation_scan_last_exit_code gauge\n" +
			"libation_scan_last_exit_code 3\n" +
			"# HELP libation_liberate_last_exit_code Exit code of the last liberate run; unchanged when the scan failed.\n" +
			"# TYPE libation_liberate_last_exit_code gauge\n" +
			"libation_liberate_last_exit_code 1\n";

		Assert.AreEqual(expected, MetricsWriter.Format(new(1700000100, 1700000050, 3, 1)));
	}

	[TestMethod]
	public void Missing_file_seeds_zeros()
	{
		using var dir = new TempDir();
		Assert.AreEqual((0L, 0), new MetricsWriter(dir.File("m.prom")).ReadPrevious());
	}

	[TestMethod]
	public void Previous_file_seeds_success_and_liberate_code()
	{
		using var dir = new TempDir();
		var writer = new MetricsWriter(dir.File("m.prom"));
		writer.Write(new(200, 150, 3, 2));
		Assert.AreEqual((150L, 2), writer.ReadPrevious());
	}

	[TestMethod]
	public void Write_replaces_the_file_and_leaves_no_temp_files()
	{
		using var dir = new TempDir();
		var path = dir.File("sub/m.prom");
		var writer = new MetricsWriter(path);
		writer.Write(new(1, 1, 0, 0));
		writer.Write(new(2, 1, 3, 0));

		StringAssert.Contains(File.ReadAllText(path), "libation_scan_last_run_timestamp_seconds 2\n");
		CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)!, "*", SearchOption.AllDirectories));
		if (!OperatingSystem.IsWindows())
			Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(path));
	}

	[TestMethod]
	public void Accounts_are_written_as_labelled_gauges_after_the_totals()
	{
		var text = MetricsWriter.Format(new(10, 5, 4, 0)
		{
			Accounts = [new("Second", "uk", true, false, 10, 10), new("Orig", "us", false, true, 10, 5)]
		});

		StringAssert.StartsWith(text, MetricsWriter.Format(new(10, 5, 4, 0)));
		StringAssert.Contains(text,
			"# HELP libation_account_scan_success 1 if the account's last scan succeeded, 0 if it failed.\n" +
			"# TYPE libation_account_scan_success gauge\n" +
			"libation_account_scan_success{account=\"Orig\",locale=\"us\"} 0\n" +
			"libation_account_scan_success{account=\"Second\",locale=\"uk\"} 1\n");
		StringAssert.Contains(text, "libation_account_needs_login{account=\"Orig\",locale=\"us\"} 1\n");
		StringAssert.Contains(text, "libation_account_scan_last_success_timestamp_seconds{account=\"Orig\",locale=\"us\"} 5\n");
	}

	[TestMethod]
	public void Account_metrics_survive_a_round_trip_including_awkward_names()
	{
		using var dir = new TempDir();
		var writer = new MetricsWriter(dir.File("m.prom"));
		AccountScanMetric awkward = new("My \"main\" \\ acct", "us", false, true, 20, 7);
		writer.Write(new(20, 7, 3, 0) { Accounts = [awkward] });

		Assert.AreEqual(awkward, writer.ReadPreviousAccounts().Single());
		Assert.AreEqual((7L, 0), writer.ReadPrevious());
	}
}
