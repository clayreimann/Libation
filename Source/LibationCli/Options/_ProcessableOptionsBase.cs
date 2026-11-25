using ApplicationServices;
using AudibleApi;
using AudibleUtilities;
using CommandLine;
using DataLayer;
using FileLiberator;
using LibationFileManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LibationCli;

public abstract class ProcessableOptionsBase : OptionsBase
{

	[Value(0, MetaName = "[asins]", HelpText = "Optional product IDs (ASINs) of books to process.")]
	public IEnumerable<string>? Asins { get; set; }

	[Option('i', "id", Required = false, HelpText = "Product ID (ASIN) of a book to process. Repeatable. Same as positional [asins].")]
	public IEnumerable<string>? Ids { get; set; }

	protected IEnumerable<string> GetProductIds()
		=> (Asins ?? []).Concat(Ids ?? []).Select(NormalizeProductId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase);

	private static string NormalizeProductId(string id) => id.Trim().TrimStart('[').TrimEnd(']');

	protected static TProcessable CreateProcessable<TProcessable>(EventHandler<LibraryBook>? completedAction = null)
		where TProcessable : Processable, IProcessable<TProcessable>
	{
		var strProc = TProcessable.Create(Configuration.Instance);
		LibraryBook? currentLibraryBook = null;

		if (Environment.UserInteractive && !Console.IsOutputRedirected && !Console.IsErrorRedirected)
		{
			var progressBar = new ConsoleProgressBar(Console.Out);
			strProc.Completed += (_, e) => progressBar.Clear();
			strProc.StreamingTimeRemaining += (_, e) => progressBar.RemainingTime = e;
			strProc.StreamingProgressChanged += (_, e) => progressBar.Progress = e.ProgressPercentage;
		}

		strProc.Begin += (o, e) =>
		{
			currentLibraryBook = e;
			Console.WriteLine($"{typeof(TProcessable).Name} Begin: {e}");
		};

		strProc.Completed += (o, e) =>
		{
			Console.WriteLine($"{typeof(TProcessable).Name} Completed: {e}");
		};

		strProc.Completed += (s, e) =>
		{
			try
			{
				completedAction?.Invoke(s, e);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine("CLI error. See log for more details.");
				Serilog.Log.Logger.Error(ex, "CLI error");
			}
		};

		if (strProc is AudioDecodable audDec)
		{
			audDec.RequestCoverArt += (_, _) =>
			{
				if (currentLibraryBook is null)
					return null;

				var pictureId = Configuration.Instance.FileDownloadQuality == Configuration.DownloadQuality.High
				? currentLibraryBook.Book.PictureLarge ?? currentLibraryBook.Book.PictureId
				: currentLibraryBook.Book.PictureId;

				return pictureId is null ? null : PictureStorage.GetPictureSynchronously(new PictureDefinition(pictureId, PictureSize.Native));
			};
		}

		return strProc;
	}

	protected async Task RunAsync(Processable Processable, Action<LibraryBook>? config = null, Action<string>? notFound = null)
	{
		var productIds = GetProductIds().ToArray();
		if (productIds.Length > 0)
		{
			foreach (var asin in productIds)
			{
				if (DbContexts.GetLibraryBook_Flat_NoTracking(asin, caseSensative: false) is LibraryBook lb)
				{
					config?.Invoke(lb);
					await ProcessOneAsync(Processable, lb, true);
				}
				else
				{
					var msg = $"Book with ASIN '{asin}' not found in library. Skipping.";
					Console.Error.WriteLine(msg);
					Serilog.Log.Logger.Error(msg);
					notFound?.Invoke(asin);
				}
			}
		}
		else
		{
			foreach (var lb in Processable.GetValidLibraryBooks(DbContexts.GetLibrary_Flat_NoTracking()))
			{
				config?.Invoke(lb);
				await ProcessOneAsync(Processable, lb, false);
			}
		}

		var done = "Done. All books have been processed";
		Console.WriteLine(done);
		Serilog.Log.Logger.Information(done);
	}

	protected async Task ProcessOneAsync(Processable Processable, LibraryBook libraryBook, bool validate)
	{
		try
		{
			var statusHandler = await Processable.ProcessSingleAsync(libraryBook, validate);

			if (statusHandler.IsSuccess)
				return;

			foreach (var errorMessage in statusHandler.Errors)
			{
				Console.Error.WriteLine(errorMessage);
				Serilog.Log.Logger.Error(errorMessage);
			}
		}
		catch (ApiErrorException ex) when (WidevineRecommendation.ShouldRecommendWidevine(ex, Configuration.Instance))
		{
			string? accountId = null;
			string? localeName = null;
			if (System.IO.File.Exists(AudibleApiStorage.AccountsSettingsFile))
			{
				using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
				var account = persister.AccountsSettings.GetAccount(libraryBook.Account, libraryBook.Book.Locale);
				accountId = account?.AccountId ?? libraryBook.Account;
				localeName = account?.Locale?.Name ?? libraryBook.Book.Locale;
				ThrottledDeviceRecovery.ApplyRecovery(persister.AccountsSettings, Configuration.Instance, account);
			}

			Console.Error.WriteLine(WidevineRecommendation.BuildLogSummary(libraryBook.Book.TitleWithSubtitle, accountId));
			if (!string.IsNullOrWhiteSpace(accountId))
			{
				Console.Error.WriteLine(
					$"Run 'libationcli login-external --account {accountId} --locale {localeName}' to sign in and register for Widevine credentials.");
			}

			Serilog.Log.Logger.Error(ex, "ADRM license unavailable (Sable acr:null) {@DebugInfo}", new { Book = libraryBook.LogFriendly() });
			ReportNextAttempt(libraryBook);
		}
		catch (ContentLicenseDeniedException clEx)
		{
			bool recovered = false;
			if (System.IO.File.Exists(AudibleApiStorage.AccountsSettingsFile))
			{
				using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
				var account = persister.AccountsSettings.GetAccount(libraryBook.Account, libraryBook.Book.Locale);

				if (ThrottledDeviceRecovery.ShouldRecover(clEx, account, Configuration.Instance))
				{
					ThrottledDeviceRecovery.ApplyRecovery(persister.AccountsSettings, Configuration.Instance, account);
					Console.Error.WriteLine(
						$"Audible reported CustomerThrottled on a legacy or non-Widevine device registration. Libation has updated settings to Android emulator with Widevine enabled, and marked account '{account?.AccountId ?? libraryBook.Account}' for re-registration.");
					Console.Error.WriteLine(
						$"Run 'libationcli login-external --account {account?.AccountId ?? libraryBook.Account} --locale {account?.Locale?.Name ?? libraryBook.Book.Locale}' to sign in and register a fresh device.");
					recovered = true;
				}
			}

			if (!recovered)
			{
				var (locale, hasAdditionalMarketplaces) = AppScaffolding.LicenseRecoveryGuidance.GetAccountContext(libraryBook);
				foreach (var line in ContentLicenseDeniedCliSummary.Lines(clEx, locale, AppScaffolding.LibationScaffolding.LastVersionCheckOutcome, hasAdditionalMarketplaces))
					Console.Error.WriteLine(line);
			}

			Serilog.Log.Logger.Error(clEx, "Content license denied {@DebugInfo}", new { Book = libraryBook.LogFriendly() });
			ReportNextAttempt(libraryBook);
		}
		catch (Exception ex)
		{
			var msg = "Error processing book. Skipping. For options of skipping or marking as error, retry with main Libation app.";
			Console.Error.WriteLine(msg + ". See log for more details.");
			Serilog.Log.Logger.Error(ex, $"{msg} {{@DebugInfo}}", new { Book = libraryBook.LogFriendly() });

			if (!ReportNextAttempt(libraryBook))
				Console.Error.WriteLine("This book will be tried again on next attempt.");
		}
	}

	/// <summary>
	/// Says when a title Libation has decided to wait on will be attempted again, so a scheduled run explains
	/// its own silence on the next several runs rather than appearing to have forgotten the title.
	/// </summary>
	/// <returns>True when the title is being waited on.</returns>
	protected static bool ReportNextAttempt(LibraryBook libraryBook)
	{
		var now = DateTimeOffset.Now;
		if (DownloadAttemptFailureStore.Find(libraryBook, now) is not DeferredDownload deferred)
			return false;

		Console.Error.WriteLine(
			$"Not attempting this title again {DeferredDownloadUserMessage.DescribeWhen(deferred.RetryAfter, now)}. "
			+ "To try it sooner, name it: libationcli liberate " + libraryBook.Book.AudibleProductId);
		return true;
	}
}
