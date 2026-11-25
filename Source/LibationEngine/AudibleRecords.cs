using AudibleApi;
using AudibleApi.Common;
using AudibleUtilities;
using DataLayer;
using System.Security.Authentication;

namespace LibationEngine;

public interface IAudibleRecords
{
	/// <summary>Throws when Audible cannot be reached or the account cannot sign in.</summary>
	Task<IReadOnlyList<IRecord>> GetAsync(LibraryBook book, CancellationToken ct);
}

public sealed class AudibleRecords : IAudibleRecords
{
	public async Task<IReadOnlyList<IRecord>> GetAsync(LibraryBook book, CancellationToken ct)
	{
		Account account;
		using (var persister = AudibleApiStorage.GetAccountsSettingsPersister())
			account = persister.AccountsSettings.GetAccount(book.Account, book.Book.Locale)
				?? throw new InvalidCredentialException($"No account found for '{book.Account}' and locale '{book.Book.Locale}'");

		var api = await ApiExtended.CreateAsync(account, allowInteractiveLogin: false, storeLocale: Localization.Get(book.Book.Locale));
		return (await api.Api.GetRecordsAsync(book.Book.AudibleProductId).WaitAsync(ct)).ToList();
	}
}
