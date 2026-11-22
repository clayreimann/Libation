using ApplicationServices;
using CommandLine;
using DataLayer;
using LibationFileManager;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LibationCli;

/// <summary>Shared title/account selection for the <c>progress</c> commands.</summary>
public abstract class ProgressOptionsBase : OptionsBase
{
	[Option('a', "account", HelpText = "Only titles from these accounts (account id or nickname). Repeatable or comma-separated.", Separator = ',')]
	public IEnumerable<string>? Accounts { get; set; }

	[Value(0, MetaName = "[asins]", HelpText = "Optional product IDs. Default: every title in the library.")]
	public IEnumerable<string>? Asins { get; set; }

	/// <summary>Library titles selected by <see cref="Asins"/> and <see cref="Accounts"/>; unknown ASINs are reported on stderr.</summary>
	protected List<LibraryBook> SelectLibraryBooks()
	{
		var books = DbContexts.GetLibrary_Flat_NoTracking().Where(lb => !lb.IsDeleted).ToList();

		if (Accounts?.Any() is true)
		{
			var wanted = Accounts.Select(a => a.Trim()).Where(a => a.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
			using var persister = AudibleUtilities.AudibleApiStorage.GetAccountsSettingsPersister();
			var accountIds = persister.AccountsSettings.GetAll()
				.Where(a => wanted.Contains(a.AccountId) || (a.AccountName is not null && wanted.Contains(a.AccountName)))
				.Select(a => a.AccountId)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			books = books.Where(lb => accountIds.Contains(lb.Account)).ToList();
		}

		if (Asins?.Any() is true)
		{
			var wanted = Asins.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var found = books.Where(lb => wanted.Contains(lb.Book.AudibleProductId)).ToList();
			foreach (var missing in wanted.Except(found.Select(lb => lb.Book.AudibleProductId), StringComparer.OrdinalIgnoreCase))
				Console.Error.WriteLine($"Not in library: {missing}");
			books = found;
		}

		return books;
	}

	/// <summary>Read progress from Audible. CLI never logs in interactively; one failing account does not stop the others.</summary>
	protected async Task<List<ListeningProgress>?> GetProgressAsync()
	{
		var books = SelectLibraryBooks();
		if (books.Count == 0)
		{
			Console.Error.WriteLine("No matching titles in the library. Run 'libationcli scan' first.");
			Environment.ExitCode = (int)ExitCode.RunTimeError;
			return null;
		}

		Console.Error.WriteLine($"Reading listening progress from Audible for {books.Count} title(s)...");
		var progress = await AudibleListeningProgressService.GetProgressAsync(
			books,
			allowInteractiveLogin: false,
			onAccountError: (account, ex) =>
			{
				Console.Error.WriteLine($"Could not read progress for {account}: {ex.Message}");
				Environment.ExitCode = (int)ExitCode.RunTimeError;
			});
		Console.Error.WriteLine($"Audible has a position for {progress.Count} title(s).");
		return progress;
	}
}

[Verb("progress-export", HelpText = "Save Audible listening progress (last-heard position per title) as JSON.")]
public class ProgressExportOptions : ProgressOptionsBase
{
	[Option('p', "path", HelpText = "File to write. Default: print to stdout.")]
	public string? FilePath { get; set; }

	protected override async Task ProcessAsync()
	{
		if (await GetProgressAsync() is not { } progress)
			return;

		var json = AudibleListeningProgressService.ToJson(progress, DateTimeOffset.Now).ToString(Newtonsoft.Json.Formatting.Indented);
		if (string.IsNullOrWhiteSpace(FilePath))
		{
			Console.WriteLine(json);
			return;
		}

		var fullPath = Path.GetFullPath(FilePath);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
		await File.WriteAllTextAsync(fullPath, json);
		Console.Error.WriteLine($"Saved {progress.Count} position(s) to {fullPath}");
	}
}

[Verb("progress-push-abs", HelpText = "Copy Audible listening progress to Audiobookshelf, for the user that owns the configured API token.\n"
	+ "Titles are matched by ASIN, then by title and author. Progress that is newer on Audiobookshelf, or a title it\n"
	+ "already marks finished, is left alone unless --force is given.")]
public class ProgressPushAbsOptions : ProgressOptionsBase
{
	[Option("dry-run", HelpText = "Show what would change without writing to Audiobookshelf.")]
	public bool DryRun { get; set; }

	[Option("force", HelpText = "Overwrite Audiobookshelf progress even when it is newer or finished.")]
	public bool Force { get; set; }

	[Option("library-id", HelpText = "Audiobookshelf library to match against. Default: the library chosen in Settings.")]
	public string? LibraryId { get; set; }

	protected override async Task ProcessAsync()
	{
		var config = Configuration.Instance;
		var libraryId = string.IsNullOrWhiteSpace(LibraryId) ? config.AudiobookshelfLibraryId : LibraryId.Trim();

		var errors = new List<string>();
		if (string.IsNullOrWhiteSpace(config.AudiobookshelfServerUrl))
			errors.Add("Server URL is not set.");
		if (string.IsNullOrWhiteSpace(config.AudiobookshelfApiToken))
			errors.Add("API token is not set.");
		if (string.IsNullOrWhiteSpace(libraryId))
			errors.Add("No library is selected (choose one in Settings or pass --library-id).");
		if (errors.Count > 0)
		{
			Console.Error.WriteLine("Audiobookshelf is not fully configured:");
			foreach (var error in errors)
				Console.Error.WriteLine("  " + error);
			Console.Error.WriteLine("Set these under Settings -> Audiobookshelf in the main Libation app, then retry.");
			Environment.ExitCode = (int)ExitCode.RunTimeError;
			return;
		}

		if (await GetProgressAsync() is not { } progress || progress.Count == 0)
			return;

		var results = await AudiobookshelfProgressSync.PushAsync(
			config.AudiobookshelfServerUrl!,
			config.AudiobookshelfApiToken!,
			libraryId!,
			progress,
			Force,
			DryRun,
			r =>
			{
				if (r.Outcome is AudiobookshelfProgressSync.Outcome.Failed)
					Console.Error.WriteLine(r.Message);
				else
					Console.WriteLine(r.Message);
			});

		Console.WriteLine();
		Console.WriteLine(DryRun ? "Audiobookshelf progress (dry run) summary:" : "Audiobookshelf progress summary:");
		foreach (var group in results.GroupBy(r => r.Outcome).OrderBy(g => g.Key))
			Console.WriteLine($"  {group.Key,-18} {group.Count()}");

		if (results.Any(r => r.Outcome is AudiobookshelfProgressSync.Outcome.Failed))
			Environment.ExitCode = (int)ExitCode.RunTimeError;
	}
}

[Verb("progress-probe", HelpText = "Developer aid: save the raw response of every Audible endpoint known to carry listening\n"
	+ "position or history for one title, and list any fields that look like they identify a device.\n"
	+ "Files may contain account identifiers; review before sharing them.")]
public class ProgressProbeOptions : OptionsBase
{
	[Value(0, MetaName = "[asin]", HelpText = "Product ID of a title you have listened to recently.", Required = true)]
	public string? Asin { get; set; }

	[Option('o', "output", HelpText = "Directory to write responses to. Default: ./progress-probe")]
	public string? OutputDirectory { get; set; }

	protected override async Task ProcessAsync()
	{
		if (string.IsNullOrWhiteSpace(Asin) || DbContexts.GetLibraryBook_Flat_NoTracking(Asin) is not LibraryBook libraryBook)
		{
			Console.Error.WriteLine($"Book not found with asin={Asin}");
			Environment.ExitCode = (int)ExitCode.RunTimeError;
			return;
		}

		var outputDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(OutputDirectory) ? "progress-probe" : OutputDirectory);
		var api = await FileLiberator.UtilityExtensions.GetApiAsync(libraryBook);
		var results = await AudibleProgressProbe.RunAsync(api, libraryBook.Book.AudibleProductId, outputDirectory);

		foreach (var r in results)
		{
			var status = r.Error is not null ? $"error: {r.Error}" : $"HTTP {r.StatusCode}";
			Console.WriteLine($"{r.Endpoint.Name,-26} {status,-10} {r.FilePath}");
			foreach (var path in r.InterestingPaths)
				Console.WriteLine($"    {path}");
		}
		Console.WriteLine();
		Console.WriteLine("Indented lines are fields whose names suggest a device, client or session.");
		Console.WriteLine("To check attribution: listen on one device, wait a minute, probe; repeat on another device and compare.");
	}
}
