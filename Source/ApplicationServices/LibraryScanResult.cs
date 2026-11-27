using AudibleUtilities;
using Dinah.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ApplicationServices;

/// <summary>What a library scan and import did, account by account.</summary>
/// <param name="TotalCount">Books read from the accounts that scanned.</param>
/// <param name="NewCount">Books that were new to the library.</param>
/// <param name="Accounts">One entry per account asked for, in that order.</param>
public sealed record LibraryScanResult(int TotalCount, int NewCount, IReadOnlyList<AccountScanOutcome> Accounts)
{
	public IEnumerable<AccountScanOutcome> Failed => Accounts.Where(a => !a.Succeeded);
	public bool AllSucceeded => Accounts.All(a => a.Succeeded);
	public bool AnySucceeded => Accounts.Any(a => a.Succeeded);
}

/// <summary>How one account's scan went.</summary>
/// <param name="AccountId">The account's login. Not for logs or metrics; use <see cref="Label"/> there.</param>
/// <param name="Label">
/// A name for the account that is safe to log or export: its nickname, or its masked login when the nickname
/// is unset or contains the login.
/// </param>
/// <param name="NeedsLogin">The scan failed because Audible needs the account to sign in again.</param>
/// <param name="Error">Why the scan failed; null when it succeeded.</param>
public sealed record AccountScanOutcome(
	string AccountId,
	string Label,
	string? Locale,
	bool Succeeded,
	bool NeedsLogin,
	int ItemCount,
	string? Error)
{
	public static AccountScanOutcome Scanned(Account account, int itemCount)
		=> new(account.AccountId, LabelFor(account), account.Locale?.Name, true, false, itemCount, null);

	public static AccountScanOutcome Failed(Account account, Exception ex)
		=> new(account.AccountId, LabelFor(account), account.Locale?.Name, false,
			AuthenticationExceptionHelper.IsAuthenticationFailure(ex), 0, ex.Message);

	public static string LabelFor(Account account)
	{
		var name = account.AccountName;
		return string.IsNullOrWhiteSpace(name) || name.Contains(account.AccountId, StringComparison.OrdinalIgnoreCase)
			? account.AccountId.ToMask()
			: name;
	}
}
