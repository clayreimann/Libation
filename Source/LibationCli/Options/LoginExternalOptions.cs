using ApplicationServices;
using AudibleApi;
using AudibleApi.Common;
using AudibleUtilities;
using CommandLine;
using DataLayer;
using LibationEngine;
using LibationFileManager;
using LoginRequiredReason = LibationEngine.ExternalLoginFlow.LoginRequiredReason;
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace LibationCli;

[Verb("login-external", HelpText = "Sign in with Audible using an external browser: open the printed URL, then paste the final URL from the address bar.")]
internal class LoginExternalOptions : OptionsBase
{
	[Option('a', "account", Required = true, HelpText = "Audible login id (email) for this account.")]
	public string? AccountId { get; set; }

	[Option('l', "locale", Required = true, HelpText = "Audible marketplace / locale name or country code (e.g. us, uk, de, germany).")]
	public string? Locale { get; set; }

	[Option("response-url", Required = false, HelpText = "Final browser URL after login. Use when stdin is not a TTY (e.g. scripts, Docker).")]
	public string? ResponseUrl { get; set; }

	[Option("device-registration", Required = false, HelpText = "CurrentAndroid, RetailAndroid, or Mkb79IPhone. Defaults to Settings. Only used for a new sign-in; remove the account first.")]
	public string? DeviceRegistration { get; set; }

	[Option('f', "force", Required = false, HelpText = "Force re-authentication even if the account is already authenticated.")]
	public bool Force { get; set; }

	internal static LoginRequiredReason EvaluateLoginRequirement(Account? account, ApiAuthenticationMethod requiredAuth, bool force)
		=> ExternalLoginFlow.EvaluateLoginRequirement(account, requiredAuth, force);

	protected override async Task ProcessAsync()
	{
		var accountId = AccountId?.Trim();
		var localeInput = Locale?.Trim();
		if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(localeInput))
		{
			PrintVerbUsage("ERROR", "=====", "Both --account and --locale are required.");
			Environment.ExitCode = (int)ExitCode.RunTimeError;
			return;
		}

		var locale = ResolveLocale(localeInput);
		if (IsEmptyLocale(locale))
		{
			var known = string.Join(", ",
				Localization.Locales
					.Where(l => !l.WithUsername)
					.Select(l => $"{l.CountryCode} ({l.Name})"));
			PrintVerbUsage(
				"ERROR",
				"=====",
				$"Unknown locale '{localeInput}'. Use a country code or locale name, for example: {known}");
			Environment.ExitCode = (int)ExitCode.RunTimeError;
			return;
		}

		if (!TryResolveRegistrationProfile(out var registrationProfile, out var registrationError))
		{
			PrintVerbUsage("ERROR", "=====", registrationError);
			Environment.ExitCode = (int)ExitCode.RunTimeError;
			return;
		}

		var plan = ExternalLoginFlow.Begin(accountId, locale, registrationProfile, !string.IsNullOrWhiteSpace(DeviceRegistration), Force);
		switch (plan.Reason)
		{
			case LoginRequiredReason.AlreadyAuthenticated:
				Console.WriteLine(
					$"Account '{accountId}' ({locale.Name}) is already authenticated. No browser login needed.");
				if (!string.IsNullOrWhiteSpace(DeviceRegistration))
					Console.WriteLine(
						"Device registration only applies to a new sign-in. Remove the account first, then run login-external again.");
				return;

			case LoginRequiredReason.RequiresReRegistration:
				Console.WriteLine($"Account '{accountId}' ({locale.Name}) is marked as requiring re-registration. Proceeding with sign-in...");
				break;

			case LoginRequiredReason.ObsoleteDeviceRegistration:
				Console.WriteLine($"Account '{accountId}' ({locale.Name}) has an obsolete or legacy device registration. Proceeding with sign-in...");
				break;

			case LoginRequiredReason.MissingRequiredTokens:
				Console.WriteLine(
					$"Account '{accountId}' ({locale.Name}) is missing credentials for modern authentication ({plan.RequiredAuth}). Starting sign-in to retrieve missing tokens...");
				break;

			case LoginRequiredReason.Forced:
				Console.WriteLine($"Forcing re-authentication for account '{accountId}' ({locale.Name})...");
				break;
		}

		var presetResponse = ResponseUrl?.Trim();
		if (string.IsNullOrEmpty(presetResponse) && Console.IsInputRedirected)
		{
			Console.Error.WriteLine(
				"Standard input is redirected. Provide the post-login URL with --response-url \"...\".");
			Environment.ExitCode = (int)ExitCode.RunTimeError;
			return;
		}

		var loginExternal = new CliLoginExternal(presetResponse);
		try
		{
			await ExternalLoginFlow.AuthenticateAsync(accountId, locale, plan, loginExternal);
		}
		catch (Exception ex)
		{
			PrintVerbUsage("ERROR", "=====", ex.Message, "", ex.ToString());
			Environment.ExitCode = (int)ExitCode.RunTimeError;
			return;
		}

		Console.WriteLine($"Successfully authenticated account '{accountId}' ({locale.Name}).");
	}

	internal static AudibleApi.Locale ResolveLocale(string localeInput) => ExternalLoginFlow.ResolveLocale(localeInput);

	internal static bool IsEmptyLocale(Locale locale) => ExternalLoginFlow.IsEmptyLocale(locale);

	internal bool TryResolveRegistrationProfile(out DeviceRegistrationProfile profile, out string error)
	{
		if (string.IsNullOrWhiteSpace(DeviceRegistration))
		{
			profile = Configuration.Instance.GetDeviceRegistrationProfile();
			error = "";
			return true;
		}

		if (Enum.TryParse<DeviceRegistrationKind>(DeviceRegistration, ignoreCase: true, out var kind)
			&& Enum.IsDefined(kind))
		{
			profile = DeviceRegistrationProfile.FromKind(kind);
			error = "";
			return true;
		}

		profile = DeviceRegistrationProfile.Default;
		error = $"Unknown device registration '{DeviceRegistration}'. Use CurrentAndroid, RetailAndroid, or Mkb79IPhone.";
		return false;
	}

	private sealed class CliLoginExternal : ILoginExternal
	{
		private readonly string? _presetResponseUrl;

		public CliLoginExternal(string? presetResponseUrl) => _presetResponseUrl = presetResponseUrl;

		public string GetResponseUrl(string loginUrl, CookieCollection signInCookies)
		{
			if (!string.IsNullOrEmpty(_presetResponseUrl))
				return ValidateResponseUrl(_presetResponseUrl);

			Console.WriteLine();
			Console.WriteLine("Open this URL in your web browser and sign in:");
			Console.WriteLine(loginUrl);
			Console.WriteLine();
			Console.WriteLine(
				"After you finish signing in, copy the full URL from your browser's address bar and paste it below.");
			Console.WriteLine("(It is normal if the page says it does not exist.)");
			Console.WriteLine();
			Console.Write("Paste URL: ");

			var line = Console.ReadLine()?.Trim();
			if (string.IsNullOrEmpty(line))
				throw new OperationCanceledException("No response URL was entered.");

			return ValidateResponseUrl(line);
		}

		private static string ValidateResponseUrl(string url)
		{
			if (!Uri.TryCreate(url, UriKind.Absolute, out _))
				throw new ArgumentException("The response URL must be a valid absolute URL.");

			return url;
		}
	}
}
