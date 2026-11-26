using AudibleApi;
using AudibleUtilities;
using DataLayer;
using System.Collections.Concurrent;
using System.Security.Authentication;

namespace LibationEngine;

public interface IAudibleRemoval
{
	/// <summary>False when Audible declines; throws when it cannot be reached or the account cannot sign in.</summary>
	Task<bool> RemoveAsync(LibraryBook book, CancellationToken ct);
}

public sealed class AudibleRemoval : IAudibleRemoval
{
	private static readonly TimeSpan ApiLifetime = TimeSpan.FromMinutes(2);
	private readonly ConcurrentDictionary<(string, string), (Api Api, DateTime Created)> apis = new();

	public async Task<bool> RemoveAsync(LibraryBook book, CancellationToken ct)
	{
		var key = (book.Account, book.Book.Locale);
		if (!apis.TryGetValue(key, out var cached) || DateTime.UtcNow - cached.Created > ApiLifetime)
		{
			Account account;
			using (var persister = AudibleApiStorage.GetAccountsSettingsPersister())
				account = persister.AccountsSettings.GetAccount(book.Account, book.Book.Locale)
					?? throw new InvalidCredentialException($"No account found for '{book.Account}' and locale '{book.Book.Locale}'");
			var extended = await ApiExtended.CreateAsync(account, allowInteractiveLogin: false, storeLocale: Localization.Get(book.Book.Locale));
			apis[key] = cached = (extended.Api, DateTime.UtcNow);
		}
		return await cached.Api.RemoveItemFromLibraryAsync(book.Book.AudibleProductId).WaitAsync(ct);
	}
}

/// <summary>The phrase a client must echo back before books are removed from the Audible account.</summary>
public static class RemoveFromAudibleConfirmation
{
	public const int MaxAsins = 100;

	public static string Phrase(int count) => $"remove {count} from audible";
}
