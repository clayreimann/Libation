using CommandLine;
using LibationEngine;
using LibationEngine.Host;
using LibationFileManager;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Threading.Tasks;

namespace LibationCli;

[Verb("serve", HelpText = "Run as a daemon: scan and liberate on an interval, with a /health endpoint and optional Prometheus metrics file.")]
internal class ServeOptions : OptionsBase
{
	[Option("interval", HelpText = "Time between cycles: 1h, 30m, 90s, 3600 (seconds) or hh:mm:ss. Default: SLEEP_TIME environment variable, else 1h.")]
	public string? Interval { get; set; }

	[Option("reconcile-on-start", HelpText = "Run 'scan' then 'set-status --downloaded' before the first cycle; books whose file is found are marked downloaded, none are demoted. A failure exits with code 3.")]
	public bool ReconcileOnStart { get; set; }

	[Option("once", HelpText = "Run reconcile (if asked) and one cycle, then exit with its result. SLEEP_TIME=-1 does the same.")]
	public bool Once { get; set; }

	[Option("listen", HelpText = "URL to listen on for /health and the API. 0.0.0.0 or * means all interfaces. Default: http://127.0.0.1:8080")]
	public string? Listen { get; set; }

	[Option("metrics-file", HelpText = "Prometheus textfile to write after each cycle. Default: LIBATION_METRICS_FILE environment variable.")]
	public string? MetricsFile { get; set; }

	[Option("accounts-import", HelpText = "AccountsSettings.json to copy into LibationFiles when its sha256 changes. Default: LIBATION_ACCOUNTS_IMPORT environment variable.")]
	public string? AccountsImport { get; set; }

	[Option("readonly-config", HelpText = "Read-only directory whose Settings.json is copied into LibationFiles at start and whose AccountsSettings.json is imported. Settings are then never written back. Default: LIBATION_READONLY_CONFIG_DIR environment variable.")]
	public string? ReadonlyConfigDir { get; set; }

	[Option("api", HelpText = "Enable the HTTP API under /api. Default: on when LIBATION_API is 1 or true.")]
	public bool Api { get; set; }

	[Option("api-token-file", HelpText = "File holding the bearer token every /api request must present. Default: LIBATION_API_TOKEN_FILE environment variable.")]
	public string? ApiTokenFile { get; set; }

	protected override bool RunsSetup => false;

	protected override async Task ProcessAsync()
	{
		var intervalText = Interval ?? Environment.GetEnvironmentVariable("SLEEP_TIME");
		var once = Once || (Interval is null && intervalText?.Trim() == "-1");
		if (once || string.IsNullOrWhiteSpace(intervalText))
			intervalText = "1h";
		if (!IntervalParser.TryParse(intervalText, out var interval))
		{
			Console.Error.WriteLine($"Invalid interval '{intervalText}'. Use 1h, 30m, 90s, 3600 (seconds) or hh:mm:ss.");
			Environment.ExitCode = (int)ExitCode.ParseError;
			return;
		}

		var filesDir = LibationFiles?.FullName
			?? Environment.GetEnvironmentVariable(LibationFileManager.LibationFiles.LIBATION_FILES_DIR)
			?? Environment.GetEnvironmentVariable("LIBATION_CONFIG_DIR");
		if (string.IsNullOrWhiteSpace(filesDir))
		{
			Console.Error.WriteLine($"serve needs a LibationFiles directory: use '--libationFiles' or the '{LibationFileManager.LibationFiles.LIBATION_FILES_DIR}' environment variable.");
			Environment.ExitCode = (int)ExitCode.RunTimeError;
			return;
		}

		filesDir = Path.GetFullPath(filesDir);
		Directory.CreateDirectory(filesDir);
		Environment.SetEnvironmentVariable(LibationFileManager.LibationFiles.LIBATION_FILES_DIR, filesDir);

		var readonlyDir = ReadonlyConfigDir ?? Environment.GetEnvironmentVariable("LIBATION_READONLY_CONFIG_DIR");
		if (string.IsNullOrWhiteSpace(readonlyDir))
			readonlyDir = null;

		var copiedSettings = false;
		if (readonlyDir is not null)
		{
			try
			{
				readonlyDir = Path.GetFullPath(readonlyDir);
				copiedSettings = ReadonlyConfig.Apply(readonlyDir, filesDir);
			}
			catch (DaemonConfigException ex)
			{
				Console.Error.WriteLine(ex.Message);
				Environment.ExitCode = (int)ExitCode.RunTimeError;
				return;
			}
		}

		var settingsFile = Path.Combine(filesDir, LibationFileManager.LibationFiles.SETTINGS_JSON);
		if (!copiedSettings && !File.Exists(settingsFile))
		{
			var books = Environment.GetEnvironmentVariable("LIBATION_BOOKS_DIR");
			var settings = new JObject { ["Books"] = string.IsNullOrWhiteSpace(books) ? "/data" : books };
			await File.WriteAllTextAsync(settingsFile, settings.ToString());
		}

		var accountsImport = AccountsImport ?? Environment.GetEnvironmentVariable("LIBATION_ACCOUNTS_IMPORT");
		if (string.IsNullOrWhiteSpace(accountsImport))
			accountsImport = readonlyDir is null ? null : ReadonlyConfig.DefaultAccountsPath(readonlyDir);
		try
		{
			new AccountsImporter(accountsImport, filesDir).Import();
		}
		catch (AccountsImportException ex)
		{
			Console.Error.WriteLine(ex.Message);
			Environment.ExitCode = (int)ExitCode.RunTimeError;
			return;
		}

		if (MasterKeyPreflight.Check(filesDir) is { } keyProblem)
		{
			Console.Error.WriteLine(keyProblem);
			Environment.ExitCode = (int)ExitCode.RunTimeError;
			return;
		}

		if (!TryInitializeSetup(ephemeralSettings: readonlyDir is not null))
			return;

		if (readonlyDir is not null)
			Serilog.Log.Logger.Information("Read-only config from {ReadonlyDir}; writable LibationFiles at {LibationFiles}", readonlyDir, filesDir);

		if (Environment.GetEnvironmentVariable("LIBATION_BOOKS_DIR") is { Length: > 0 } booksDir)
			Configuration.Instance.Books = booksDir;

		if (SettingOverrides is not null)
			ProcessSettingsOverrides();

		var apiEnabled = Api || Environment.GetEnvironmentVariable("LIBATION_API")?.Trim() is "1" or "true";
		var apiTokenFile = ApiTokenFile ?? Environment.GetEnvironmentVariable("LIBATION_API_TOKEN_FILE");
		if (string.IsNullOrWhiteSpace(apiTokenFile))
			apiTokenFile = null;

		var options = new DaemonOptions(
			interval,
			ReconcileOnStart,
			Listen ?? "http://127.0.0.1:8080",
			filesDir,
			MetricsFile ?? Environment.GetEnvironmentVariable("LIBATION_METRICS_FILE"),
			accountsImport,
			once,
			apiEnabled,
			apiTokenFile is null ? null : Path.GetFullPath(apiTokenFile),
			readonlyDir is not null);

		var policy = new LiberationPolicy
		{
			MaxConcurrentDownloads = () => 1,
			OnDiskFull = DiskFullPolicy.WaitForSpace,
			OnDailyLimit = DailyLimitPolicy.Skip
		};
		var queue = new LiberationQueue(policy);
		_ = new AutoResponder(queue, policy, Console.Error);

		var jobs = new DaemonJobs(
			_ => new ScanOptions().ExecuteAsync(),
			_ => new LiberateOptions { SharedQueue = queue }.ExecuteAsync(),
			async _ =>
			{
				var rc = await new ScanOptions().ExecuteAsync();
				return rc != 0 ? rc : await new SetDownloadStatusOptions { SetDownloaded = true }.ExecuteAsync();
			})
		{
			CreateBookJob = asin => new LiberateOptions { SharedQueue = queue }.CreateTargetedJob(queue, asin),
			CreateUploadJob = asin => AudiobookshelfUploadJob.Create(queue, asin),
			CreateConvertJob = asin => Mp3ConversionJob.Create(queue, asin),
			ScanAccounts = (selection, _) => new ScanOptions { AccountNames = selection.Accounts, Locale = selection.Locale }.ExecuteAsync()
		};

		Environment.ExitCode = await LibationDaemon.RunAsync(options, jobs, queue: queue);
	}
}
