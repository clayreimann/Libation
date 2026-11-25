using System.Collections.Concurrent;
using AudibleUtilities;
using LibationEngine.Contracts;

namespace LibationEngine;

/// <summary>Remembers accounts whose scan or download failed on auth and publishes <see cref="AccountNeedsLogin"/> once per failure streak, and <see cref="AccountSignedIn"/> when it clears.</summary>
public sealed class AccountAuthMonitor : IDisposable
{
	private readonly EventBroadcaster events;
	private readonly ConcurrentDictionary<string, string?> flagged = new(StringComparer.OrdinalIgnoreCase);

	public AccountAuthMonitor(EventBroadcaster events)
	{
		this.events = events;
		ApiExtended.AuthenticationRequired += OnRequired;
	}

	public IReadOnlyCollection<string> Flagged => flagged.Keys.ToList();

	public bool IsFlagged(string accountId) => flagged.ContainsKey(accountId);

	public void Clear(string accountId) => flagged.TryRemove(accountId, out _);

	/// <summary>Unflags the account and publishes <see cref="AccountSignedIn"/>, flagged or not.</summary>
	public void SignedIn(string accountId, string? locale)
	{
		flagged.TryRemove(accountId, out _);
		events.Publish(new AccountSignedIn(accountId, locale));
	}

	/// <summary>Unflags every account, publishing <see cref="AccountSignedIn"/> for each.</summary>
	public void ClearAll()
	{
		foreach (var (id, locale) in flagged.ToArray())
			if (flagged.TryRemove(id, out _))
				events.Publish(new AccountSignedIn(id, locale));
	}

	public void Dispose() => ApiExtended.AuthenticationRequired -= OnRequired;

	private void OnRequired(AccountSummary summary)
	{
		var id = summary.RevealAccountId();
		if (flagged.TryAdd(id, summary.LocaleName))
			events.Publish(new AccountNeedsLogin(id));
	}
}
