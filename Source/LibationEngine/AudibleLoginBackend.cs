using AudibleApi;
using LibationFileManager;

namespace LibationEngine;

public sealed class AudibleLoginBackend : IExternalLoginBackend
{
	public LoginTarget Resolve(string? accountId, string locale, string? deviceRegistration)
	{
		var resolved = ExternalLoginFlow.ResolveLocale(locale.Trim());
		if (ExternalLoginFlow.IsEmptyLocale(resolved))
			throw new LoginException(400, $"Unknown locale '{locale.Trim()}'. Use a country code or locale name such as us, uk or germany.");

		if (!string.IsNullOrWhiteSpace(deviceRegistration)
			&& !(Enum.TryParse<DeviceRegistrationKind>(deviceRegistration, ignoreCase: true, out var kind) && Enum.IsDefined(kind)))
			throw new LoginException(400, "Unknown deviceRegistration; use CurrentAndroid, RetailAndroid or Mkb79IPhone.");

		using var persister = AudibleUtilities.AudibleApiStorage.GetAccountsSettingsPersister();
		var id = accountId?.Trim();
		if (string.IsNullOrEmpty(id))
		{
			var same = persister.AccountsSettings.Accounts.Where(a => a.Locale?.Name == resolved.Name).ToList();
			if (same.Count != 1)
				throw new LoginException(400, "accountId is required unless exactly one account uses that locale.");
			id = same[0].AccountId;
		}

		var existing = persister.AccountsSettings.GetAccount(id, resolved.Name);
		return new(existing?.AccountId ?? id, resolved.Name, existing is not null, string.IsNullOrWhiteSpace(deviceRegistration) ? null : deviceRegistration.Trim());
	}

	public async Task AuthenticateAsync(LoginTarget target, ILoginExternal login, CancellationToken ct)
	{
		var locale = ExternalLoginFlow.ResolveLocale(target.LocaleName);
		var explicitProfile = target.DeviceRegistration is not null;
		var profile = explicitProfile
			? DeviceRegistrationProfile.FromKind(Enum.Parse<DeviceRegistrationKind>(target.DeviceRegistration!, ignoreCase: true))
			: Configuration.Instance.GetDeviceRegistrationProfile();

		var plan = ExternalLoginFlow.Begin(target.AccountId, locale, profile, explicitProfile, force: true);
		await ExternalLoginFlow.AuthenticateAsync(target.AccountId, locale, plan, login).WaitAsync(ct);
	}

	public void DiscardUnfinished(LoginTarget target) => ExternalLoginFlow.DiscardUnfinished(target.AccountId, target.LocaleName);
}
