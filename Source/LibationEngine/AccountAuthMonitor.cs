using System.Collections.Concurrent;
using AudibleUtilities;
using LibationEngine.Contracts;

namespace LibationEngine;

/// <summary>Remembers accounts whose scan or download failed on auth and publishes <see cref="AccountNeedsLogin"/> once per failure streak.</summary>
public sealed class AccountAuthMonitor : IDisposable
{
	private readonly EventBroadcaster events;
	private readonly ConcurrentDictionary<string, byte> flagged = new(StringComparer.OrdinalIgnoreCase);

	public AccountAuthMonitor(EventBroadcaster events)
	{
		this.events = events;
		ApiExtended.AuthenticationRequired += OnRequired;
	}

	public IReadOnlyCollection<string> Flagged => flagged.Keys.ToList();

	public bool IsFlagged(string accountId) => flagged.ContainsKey(accountId);

	public void Clear(string accountId) => flagged.TryRemove(accountId, out _);

	public void ClearAll() => flagged.Clear();

	public void Dispose() => ApiExtended.AuthenticationRequired -= OnRequired;

	private void OnRequired(AccountSummary summary)
	{
		var id = summary.RevealAccountId();
		if (flagged.TryAdd(id, 0))
			events.Publish(new AccountNeedsLogin(id));
	}
}
