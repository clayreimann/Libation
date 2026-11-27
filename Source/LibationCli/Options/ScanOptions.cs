using ApplicationServices;
using AudibleUtilities;
using CommandLine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LibationCli;

[Verb("scan", HelpText = "Scan library. Default: scan all accounts. Optional: use 'account' flag to specify a single account.")]
public class ScanOptions : OptionsBase
{
	[Value(0, MetaName = "Accounts", HelpText = "Optional: user ID or nicknames of accounts to scan.", Required = false)]
	public IEnumerable<string>? AccountNames { get; set; }

	[Option("locale", HelpText = "Optional: only scan accounts in this marketplace, such as us or uk.")]
	public string? Locale { get; set; }

	protected override async Task ProcessAsync()
	{
		var accounts = getAccounts();
		if (!accounts.Any())
		{
			Console.WriteLine("No accounts. Exiting.");
			Environment.ExitCode = (int)ExitCode.RunTimeError;
			return;
		}

		var _accounts = accounts.ToArray();

		var intro
			= (_accounts.Length == 1)
			? "Scanning Audible library. This may take a few minutes."
			: $"Scanning Audible library: {_accounts.Length} accounts. This may take a few minutes per account.";
		Console.WriteLine(intro);

		// CLI/Docker cannot interactive-login. Each account fails on its own, so one that needs a login does not
		// stop the others from being scanned and imported.
		var result = await LibraryCommands.ImportAccountsIsolatedAsync(_accounts, allowInteractiveLogin: false);
		AccountOutcomes = result.Accounts;

		foreach (var failed in result.Failed)
		{
			// stderr is not teed into Serilog, so this is the one place the owner can be told which account in full.
			var account = _accounts.FirstOrDefault(a => a.AccountId == failed.AccountId);
			var label = AccountCredentialStatus.FormatAccountLabel(account);
			Console.Error.WriteLine(failed.NeedsLogin
				? $"Audible login is required for {label}. It was not scanned."
				: $"Could not scan {label}: {failed.Error}");
		}

		Console.WriteLine(result.AllSucceeded ? "Scan complete." : $"Scan finished with {result.Failed.Count()} of {_accounts.Length} accounts failed.");
		Console.WriteLine($"Total processed: {result.TotalCount}");
		Console.WriteLine($"New: {result.NewCount}");

		if (!result.AllSucceeded)
			Environment.ExitCode = (int)(result.AnySucceeded ? ExitCode.ScanPartlyFailed : ExitCode.RunTimeError);
	}

	/// <summary>How each account's scan went, once <see cref="OptionsBase.ExecuteAsync"/> has run; null if it never got that far.</summary>
	public IReadOnlyList<AccountScanOutcome>? AccountOutcomes { get; private set; }

	private Account[] getAccounts()
	{
		using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
		var allAccounts = persister.AccountsSettings.GetAll()
			.Where(a => string.IsNullOrWhiteSpace(Locale) || string.Equals(a.Locale?.Name, Locale.Trim(), StringComparison.OrdinalIgnoreCase))
			.ToArray();

		if (AccountNames?.Any() is not true)
			return allAccounts;

		var accountNames = AccountNames.Select(n => n.ToLower()).ToArray();

		var found
			= allAccounts
			.Where(acct => accountNames.Contains(acct.AccountName?.ToLower()) || accountNames.Contains(acct.AccountId.ToLower()))
			.ToArray();

		var notFound = allAccounts.Except(found).ToArray();

		// no accounts found. do not continue
		if (!found.Any())
		{
			Console.WriteLine("Accounts not found:");
			foreach (var nf in notFound)
				Console.WriteLine($"- {nf}");
			return found;
		}

		// some accounts not found. continue after message
		if (notFound.Any())
		{
			Console.WriteLine("Accounts found:");
			foreach (var f in found)
				Console.WriteLine($"- {f}");
			Console.WriteLine("Accounts not found:");
			foreach (var nf in notFound)
				Console.WriteLine($"- {nf}");
		}

		// else: all accounts area found. silently continue

		return found;
	}
}
