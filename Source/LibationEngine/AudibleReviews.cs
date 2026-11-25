using AudibleApi;
using AudibleUtilities;
using DataLayer;
using System.Security.Authentication;

namespace LibationEngine;

public interface IAudibleReviews
{
	/// <summary>False when Audible rejects the rating; throws when it cannot be reached or the account cannot sign in.</summary>
	Task<bool> SubmitAsync(LibraryBook book, int overall, int performance, int story, CancellationToken ct);
}

public sealed class AudibleReviews : IAudibleReviews
{
	public async Task<bool> SubmitAsync(LibraryBook book, int overall, int performance, int story, CancellationToken ct)
	{
		Account account;
		using (var persister = AudibleApiStorage.GetAccountsSettingsPersister())
			account = persister.AccountsSettings.GetAccount(book.Account, book.Book.Locale)
				?? throw new InvalidCredentialException($"No account found for '{book.Account}' and locale '{book.Book.Locale}'");

		var api = await ApiExtended.CreateAsync(account, allowInteractiveLogin: false, storeLocale: Localization.Get(book.Book.Locale));
		return await api.Api.ReviewAsync(book.Book.AudibleProductId, overall, performance, story).WaitAsync(ct);
	}
}
