using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using AudibleApi;
using LibationEngine.Contracts;
using Serilog;

namespace LibationEngine;

public sealed class LoginException(int status, string message, string? code = null, bool? retryable = null) : Exception(message)
{
	public int Status { get; } = status;
	public string? Code { get; } = code;
	public bool? Retryable { get; } = retryable;
}

public sealed record LoginTarget(string AccountId, string LocaleName, bool Exists, string? DeviceRegistration);

/// <summary>The Audible-facing half of a browser login; faked in tests so nothing reaches Amazon.</summary>
public interface IExternalLoginBackend
{
	LoginTarget Resolve(string? accountId, string locale, string? deviceRegistration);

	/// <summary>Runs the sign-in, calling <c>login</c> for the response URL; returns when tokens are saved.</summary>
	Task AuthenticateAsync(LoginTarget target, ILoginExternal login, CancellationToken ct);

	void DiscardUnfinished(LoginTarget target);
}

public sealed record PendingLogin(string LoginId, string AccountId, string LocaleName, string Url, DateTimeOffset ExpiresAt);

/// <summary>Pending browser logins, in memory only; each is single-use and expires.</summary>
public sealed class LoginSessions : IDisposable
{
	public const int MaxPending = 5;
	public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
	private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

	private readonly IExternalLoginBackend backend;
	private readonly TimeProvider time;
	private readonly ConcurrentDictionary<string, Pending> pending = new();

	public LoginSessions(IExternalLoginBackend backend, TimeProvider? time = null)
	{
		this.backend = backend;
		this.time = time ?? TimeProvider.System;
	}

	public int PendingCount
	{
		get
		{
			Sweep();
			return pending.Count;
		}
	}

	/// <summary>Started, unexpired logins, soonest to expire first; the sign-in URL only, never a response URL.</summary>
	public IReadOnlyList<PendingLogin> List()
	{
		Sweep();
		return pending
			.Select(p => (p.Key, Entry: p.Value, Url: p.Value.Url))
			.Where(p => p.Url is not null)
			.OrderBy(p => p.Entry.ExpiresAt)
			.Select(p => new PendingLogin(p.Key, p.Entry.Target.AccountId, p.Entry.Target.LocaleName, p.Url!, p.Entry.ExpiresAt))
			.ToList();
	}

	public async Task<(string LoginId, string Url, DateTimeOffset ExpiresAt)> StartAsync(string? accountId, string? locale, string? deviceRegistration, bool allowNewAccount, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(locale))
			throw new LoginException(400, "locale is required.");

		Sweep();
		var target = backend.Resolve(accountId, locale, deviceRegistration);
		if (!target.Exists && !allowNewAccount)
			throw new LoginException(409, "Accounts are managed by the imported accounts file; this account is not in it, so add it there and restart. Existing accounts can still sign in again.");

		foreach (var (key, other) in pending)
			if (other.Target.AccountId.Equals(target.AccountId, StringComparison.OrdinalIgnoreCase) && other.Target.LocaleName == target.LocaleName && pending.TryRemove(key, out _))
				other.Dispose();

		if (pending.Count >= MaxPending)
			throw new LoginException(429, $"Too many sign-ins are waiting (limit {MaxPending}); finish or cancel one first.");

		var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
		var entry = new Pending(target, time.GetUtcNow() + Lifetime);
		var bridge = new Bridge(entry);
		entry.Run = Task.Run(async () =>
		{
			try
			{
				await backend.AuthenticateAsync(target, bridge, entry.Cts.Token);
			}
			catch (Exception ex)
			{
				entry.UrlReady.TrySetException(ex);
				entry.Failed = true;
				if (!entry.Cts.IsCancellationRequested)
					Log.Logger.Warning("A browser sign-in failed ({ExceptionType})", ex.GetType().Name);
				throw;
			}
			finally
			{
				if (entry.Failed)
				{
					pending.TryRemove(new KeyValuePair<string, Pending>(id, entry));
					TryDiscard(entry);
				}
			}
		});
		_ = entry.Run.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
		pending[id] = entry;

		try
		{
			var url = await entry.UrlReady.Task.WaitAsync(StartTimeout, ct);
			return (id, url, entry.ExpiresAt);
		}
		catch (Exception)
		{
			pending.TryRemove(id, out _);
			entry.Failed = true;
			entry.Dispose();
			TryDiscard(entry);
			ct.ThrowIfCancellationRequested();
			throw new LoginException(502, "Could not start the sign-in with Audible; try again.");
		}
	}

	/// <summary>Null when the login is unknown, used, cancelled or expired.</summary>
	public async Task<LoginTarget?> CompleteAsync(string loginId, string? responseUrl, CancellationToken ct)
	{
		Sweep();
		if (!pending.TryGetValue(loginId, out var entry))
			return null;

		if (!LooksLikeResponse(responseUrl))
			throw new LoginException(400, "That is not the final sign-in URL; copy the full address from the browser's address bar after signing in.", ErrorCodes.NotFinalUrl, retryable: true);

		if (Interlocked.Exchange(ref entry.Claimed, 1) == 1)
			return null;
		pending.TryRemove(loginId, out _);

		entry.Response.TrySetResult(responseUrl!);
		try
		{
			await entry.Run.WaitAsync(ct);
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			return null;
		}
		catch (Exception) when (!ct.IsCancellationRequested)
		{
			throw new LoginException(400, "Audible did not accept that sign-in; start a new login and try again.", ErrorCodes.Rejected, retryable: false);
		}
		finally
		{
			entry.Dispose();
		}
		return entry.Target;
	}

	public bool Cancel(string loginId)
	{
		Sweep();
		if (!pending.TryRemove(loginId, out var entry) || Interlocked.Exchange(ref entry.Claimed, 1) == 1)
			return false;
		entry.Failed = true;
		entry.Dispose();
		TryDiscard(entry);
		return true;
	}

	public void Dispose()
	{
		foreach (var (key, entry) in pending)
			if (pending.TryRemove(key, out _))
				entry.Dispose();
	}

	private void Sweep()
	{
		var now = time.GetUtcNow();
		foreach (var (key, entry) in pending)
			if (entry.ExpiresAt <= now && pending.TryRemove(key, out _))
			{
				entry.Failed = true;
				entry.Dispose();
				TryDiscard(entry);
			}
	}

	private void TryDiscard(Pending entry)
	{
		if (entry.Target.Exists || Interlocked.Exchange(ref entry.Discarded, 1) == 1)
			return;
		try
		{
			backend.DiscardUnfinished(entry.Target);
		}
		catch (Exception ex)
		{
			Log.Logger.Warning("An unfinished browser sign-in could not be cleaned up ({ExceptionType})", ex.GetType().Name);
		}
	}

	private static bool LooksLikeResponse(string? url)
		=> Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
			&& uri.Scheme == Uri.UriSchemeHttps
			&& (uri.Query.Contains("openid.oa2.authorization_code=", StringComparison.Ordinal));

	private sealed class Pending(LoginTarget target, DateTimeOffset expiresAt) : IDisposable
	{
		public LoginTarget Target { get; } = target;
		public DateTimeOffset ExpiresAt { get; } = expiresAt;
		public readonly CancellationTokenSource Cts = new(Lifetime);
		public readonly TaskCompletionSource<string> UrlReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public readonly TaskCompletionSource<string> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Task Run = Task.CompletedTask;
		public int Claimed;
		public int Discarded;
		public volatile bool Failed;
		public volatile string? Url;

		public void Dispose()
		{
			try { Cts.Cancel(); } catch (ObjectDisposedException) { }
		}
	}

	private sealed class Bridge(Pending entry) : ILoginExternal
	{
		public string GetResponseUrl(string loginUrl, CookieCollection signInCookies)
		{
			entry.Url = loginUrl;
			entry.UrlReady.TrySetResult(loginUrl);
			return entry.Response.Task.WaitAsync(entry.Cts.Token).GetAwaiter().GetResult().Trim();
		}
	}
}
