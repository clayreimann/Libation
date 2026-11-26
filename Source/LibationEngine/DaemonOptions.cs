using LibationEngine.Contracts;
namespace LibationEngine;

public sealed record DaemonOptions(
	TimeSpan Interval,
	bool ReconcileOnStart,
	string ListenUrl,
	string LibationFilesDir,
	string? MetricsFile = null,
	string? AccountsImportPath = null,
	bool Once = false,
	bool ApiEnabled = false,
	string? ApiTokenFile = null,
	bool ReadonlyConfig = false);

/// <summary>Each delegate returns a process-style exit code; 0 is success.</summary>
public sealed record DaemonJobs(
	Func<CancellationToken, Task<int>> Scan,
	Func<CancellationToken, Task<int>> Liberate,
	Func<CancellationToken, Task<int>> Reconcile)
{
	/// <summary>Builds the job for one book by ASIN, or null when the book is not in the library.</summary>
	public Func<string, LiberationJob?>? CreateBookJob { get; init; }

	/// <summary>Builds an upload-only job for one book by ASIN, or null when the book is not in the library.</summary>
	public Func<string, LiberationJob?>? CreateUploadJob { get; init; }

	/// <summary>Builds a convert-only job for one book by ASIN.</summary>
	public Func<string, ConvertJobResult>? CreateConvertJob { get; init; }

	/// <summary>Builds a push-Audible-progress job for one book by ASIN (second argument is force), or null when the book is not in the library.</summary>
	public Func<string, bool, LiberationJob?>? CreateProgressJob { get; init; }

	/// <summary>Scans only the selected accounts; when null a subset request scans everything.</summary>
	public Func<ScanSelection, CancellationToken, Task<int>>? ScanAccounts { get; init; }
}

/// <summary>Accounts by login id or name, narrowed to one marketplace by Locale; either may be null but not both.</summary>
public sealed record ScanSelection(IReadOnlyList<string>? Accounts, string? Locale)
{
	public bool Matches(AccountDto account)
		=> (Locale is null || string.Equals(account.Locale, Locale, StringComparison.OrdinalIgnoreCase))
			&& (Accounts is null || Accounts.Any(n => string.Equals(account.AccountId, n, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(account.AccountName, n, StringComparison.OrdinalIgnoreCase)));
}
