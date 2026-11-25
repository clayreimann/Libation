using ApplicationServices;
using AudibleApi;
using AudibleApi.Common;
using AudibleUtilities;
using DataLayer;
using LibationFileManager;

namespace LibationEngine;

/// <summary>The sign-in steps shared by <c>libationcli login-external</c> and the daemon's browser login.</summary>
public static class ExternalLoginFlow
{
	public enum LoginRequiredReason
	{
		NotAuthenticated,
		RequiresReRegistration,
		ObsoleteDeviceRegistration,
		MissingRequiredTokens,
		Forced,
		AlreadyAuthenticated
	}

	public sealed record Plan(LoginRequiredReason Reason, string JsonPath, DeviceRegistrationProfile Profile, ApiAuthenticationMethod RequiredAuth, bool ForceLogin, bool Created);

	public static LoginRequiredReason EvaluateLoginRequirement(Account? account, ApiAuthenticationMethod requiredAuth, bool force)
	{
		if (account?.RequiresReRegistration == true)
			return LoginRequiredReason.RequiresReRegistration;

		if (account?.IdentityTokens?.IsValid != true)
			return LoginRequiredReason.NotAuthenticated;

		if (account.HasObsoleteDeviceRegistration())
			return LoginRequiredReason.ObsoleteDeviceRegistration;

		if (!account.IdentityTokens.HasRequiredTokens(requiredAuth))
			return LoginRequiredReason.MissingRequiredTokens;

		return force ? LoginRequiredReason.Forced : LoginRequiredReason.AlreadyAuthenticated;
	}

	/// <summary>Resolves by locale name or country code, preferring modern (non-pre-amazon) locales for a country code.</summary>
	public static Locale ResolveLocale(string localeInput)
	{
		var fromGet = Localization.Get(localeInput);
		if (!IsEmptyLocale(fromGet))
			return fromGet;

		return Localization.Locales
			.Where(l => l.CountryCode.Equals(localeInput, StringComparison.OrdinalIgnoreCase)
				|| l.Name.Equals(localeInput, StringComparison.OrdinalIgnoreCase))
			.OrderBy(l => l.WithUsername)
			.FirstOrDefault()
			?? Locale.Empty;
	}

	public static bool IsEmptyLocale(Locale locale) => string.IsNullOrEmpty(locale.CountryCode);

	/// <summary>Upserts the account under its canonical locale name and decides what sign-in it needs.</summary>
	public static Plan Begin(string accountId, Locale locale, DeviceRegistrationProfile profile, bool explicitProfile, bool force)
	{
		using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
		var created = persister.AccountsSettings.GetAccount(accountId, locale.Name) is null;
		var account = persister.AccountsSettings.Upsert(accountId, locale.Name);
		var requiredAuth = AccountCredentialStatus.RequiredAuthenticationMethods;
		var reason = EvaluateLoginRequirement(account, requiredAuth, force);

		if (reason is LoginRequiredReason.RequiresReRegistration or LoginRequiredReason.ObsoleteDeviceRegistration)
		{
			account.IdentityTokens?.Invalidate();
			if (!explicitProfile)
			{
				Configuration.Instance.UseWidevine = true;
				Configuration.Instance.DeviceRegistrationKind = DeviceRegistrationKind.CurrentAndroid;
				profile = DeviceRegistrationProfile.CurrentAndroid;
			}
		}

		var forceLogin = reason is LoginRequiredReason.Forced or LoginRequiredReason.MissingRequiredTokens or LoginRequiredReason.RequiresReRegistration or LoginRequiredReason.ObsoleteDeviceRegistration;
		return new(reason, account.GetIdentityTokensJsonPath(), profile, requiredAuth, forceLogin, created);
	}

	public static async Task AuthenticateAsync(string accountId, Locale locale, Plan plan, ILoginExternal login)
	{
		_ = await EzApiCreator.GetApiAsync(
			login,
			locale,
			AudibleApiStorage.AccountsSettingsFile,
			plan.JsonPath,
			plan.Profile,
			forceLogin: plan.ForceLogin,
			requiredAuthMethods: plan.RequiredAuth);

		using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
		var account = persister.AccountsSettings.GetAccount(accountId, locale.Name);
		if (account is not null)
			account.RequiresReRegistration = false;

		if (plan.ForceLogin)
			DownloadAttemptFailureStore.ClearForAccount(accountId, DownloadFailureKind.LicenseDenied);
	}

	/// <summary>Removes an account that a sign-in created but never finished.</summary>
	public static void DiscardUnfinished(string accountId, string localeName)
	{
		using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
		var account = persister.AccountsSettings.GetAccount(accountId, localeName);
		if (account?.IdentityTokens?.IsValid != true && account is not null)
			persister.AccountsSettings.Delete(account);
	}
}
