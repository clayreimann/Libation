namespace LibationEngine;

/// <summary>
/// Decides when an unattended queue may act on Audible saying a download was throttled because of a legacy or
/// non-Widevine registration.
/// </summary>
/// <remarks>
/// Acting on it switches every account to Android + Widevine and marks accounts for a new device registration,
/// which a headless install cannot complete on its own: every scan for those accounts then fails until someone
/// signs in again. Audible gives that answer for single titles too, so one refused title is not enough. The gate
/// only lets recovery through once several different titles on the same account have been refused within a
/// window, and any licensed download that succeeds on the account starts the count again.
/// </remarks>
public sealed class ThrottleRecoveryGate
{
	public const int DefaultDistinctTitles = 3;
	public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);

	private readonly object sync = new();
	private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> throttled = new(StringComparer.OrdinalIgnoreCase);
	private readonly TimeProvider time;

	public ThrottleRecoveryGate(int distinctTitles = DefaultDistinctTitles, TimeSpan? window = null, TimeProvider? time = null)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(distinctTitles, 1);
		DistinctTitles = distinctTitles;
		Window = window ?? DefaultWindow;
		this.time = time ?? TimeProvider.System;
	}

	/// <summary>How many different titles on one account must be throttled before recovery is allowed.</summary>
	public int DistinctTitles { get; }

	/// <summary>How far back a throttled title still counts.</summary>
	public TimeSpan Window { get; }

	/// <summary>
	/// Records that <paramref name="asin"/> was throttled for <paramref name="accountId"/>. True when that makes
	/// enough different titles within <see cref="Window"/> to recover the account; the count then starts again.
	/// </summary>
	public bool RecordThrottle(string accountId, string asin)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
		ArgumentException.ThrowIfNullOrWhiteSpace(asin);

		var now = time.GetUtcNow();
		lock (sync)
		{
			if (!throttled.TryGetValue(accountId, out var titles))
				throttled[accountId] = titles = new(StringComparer.OrdinalIgnoreCase);

			foreach (var (old, at) in titles.ToArray())
				if (now - at > Window)
					titles.Remove(old);

			titles[asin] = now;
			if (titles.Count < DistinctTitles)
				return false;

			throttled.Remove(accountId);
			return true;
		}
	}

	/// <summary>How many different titles on the account count toward recovery right now.</summary>
	public int ThrottledTitleCount(string accountId)
	{
		var now = time.GetUtcNow();
		lock (sync)
			return throttled.TryGetValue(accountId, out var titles)
				? titles.Values.Count(at => now - at <= Window)
				: 0;
	}

	/// <summary>A licensed download worked for the account, so it is not throttled as a whole.</summary>
	public void RecordLicensed(string accountId)
	{
		if (string.IsNullOrWhiteSpace(accountId))
			return;
		lock (sync)
			throttled.Remove(accountId);
	}
}
