using System.Reflection;
using System.Text.Json;
using AAXClean;
using FileManager;
using LibationEngine.Contracts;
using ApplicationServices;
using LibationFileManager;
using LibationFileManager.Templates;
using Serilog.Events;

namespace LibationEngine;

public sealed record SettingsUpdateResult(SettingsDto? Settings, IReadOnlyDictionary<string, string>? Errors)
{
	public bool Ok => Errors is null;
}

/// <summary>Validates a partial settings body against <see cref="Configuration"/> and applies it all-or-nothing.</summary>
public static class SettingsWriter
{
	public const string BooksEnvironmentVariable = "LIBATION_BOOKS_DIR";

	public const int MinBitrate = 16, MaxBitrate = 320, MinVbrQuality = 0, MaxVbrQuality = 9, MaxMinimumFileDuration = 120;

	/// <summary>Keys whose change needs a daemon restart; none of the covered settings does.</summary>
	public static readonly IReadOnlySet<string> RestartRequiredKeys = new HashSet<string>();

	private static readonly HashSet<string> NullClears = ["inProgress", "audiobookshelf.serverUrl", "audiobookshelf.apiToken", "audiobookshelf.libraryId", "audiobookshelf.folderId"];

	private static readonly NAudio.Lame.EncoderQuality[] EncoderQualities =
		[NAudio.Lame.EncoderQuality.High, NAudio.Lame.EncoderQuality.Standard, NAudio.Lame.EncoderQuality.Fast];

	private static readonly SampleRate[] SampleRates = Enum.GetValues<SampleRate>().Where(r => r >= SampleRate.Hz_8000 && r <= SampleRate.Hz_48000).ToArray();

	private sealed record Change(string Key, Action Apply, Action Revert);

	public static SettingsUpdateResult Apply(Configuration config, JsonElement body)
	{
		var errors = new Dictionary<string, string>();
		if (body.ValueKind != JsonValueKind.Object)
			return new(null, new Dictionary<string, string> { [""] = "The body must be a JSON object." });

		InspectKeys(body, typeof(SettingsPatchRequest), "", errors);
		if (errors.Count > 0)
			return new(null, errors);

		SettingsPatchRequest request;
		try
		{
			request = body.Deserialize<SettingsPatchRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
		}
		catch (JsonException ex)
		{
			var key = ex.Path is { Length: > 2 } path ? path[2..] : "";
			return new(null, new Dictionary<string, string> { [key] = "Wrong type for this setting." });
		}

		var changes = new List<Change>();
		var pending = new PendingFolders();
		Plan(config, body, request, changes, errors, pending);
		if (errors.Count == 0)
			pending.Create(errors);
		if (errors.Count > 0)
			return new(null, errors);

		var restart = changes.Where(c => RestartRequiredKeys.Contains(c.Key)).Select(c => c.Key).ToList();
		var applied = new List<Change>();
		try
		{
			foreach (var change in changes)
			{
				change.Apply();
				applied.Add(change);
			}
		}
		catch
		{
			foreach (var change in Enumerable.Reverse(applied))
				try { change.Revert(); } catch { }
			throw;
		}

		return new(SettingsMapper.From(config) with { RestartRequired = restart }, null);
	}

	private static void InspectKeys(JsonElement element, Type type, string prefix, Dictionary<string, string> errors)
	{
		foreach (var member in element.EnumerateObject())
		{
			var key = prefix + member.Name;
			var info = type.GetProperties().FirstOrDefault(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name) == member.Name);
			if (info is null)
			{
				errors[key] = "Unknown setting.";
				continue;
			}
			if (member.Value.ValueKind == JsonValueKind.Null)
			{
				if (!NullClears.Contains(key))
					errors[key] = "Must not be null.";
				continue;
			}
			var propertyType = Nullable.GetUnderlyingType(info.PropertyType) ?? info.PropertyType;
			if (propertyType.Assembly == typeof(SettingsDto).Assembly && !propertyType.IsGenericType)
			{
				if (member.Value.ValueKind != JsonValueKind.Object)
					errors[key] = "Must be an object.";
				else
					InspectKeys(member.Value, propertyType, key + ".", errors);
			}
		}
	}

	private sealed class PendingFolders
	{
		private readonly List<(string Key, string Path)> folders = [];

		public void Add(string key, string path) => folders.Add((key, path));

		public void Create(Dictionary<string, string> errors)
		{
			foreach (var (key, path) in folders)
			{
				try
				{
					Directory.CreateDirectory(path);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					errors[key] = $"Cannot create the folder: {ex.Message}";
				}
			}
		}
	}

	private static void Plan(Configuration c, JsonElement body, SettingsPatchRequest r, List<Change> changes, Dictionary<string, string> errors, PendingFolders folders)
	{
		void Set<T>(string key, T next, Func<T> get, Action<T> set)
		{
			var old = get();
			changes.Add(new(key, () => set(next), () => set(old)));
		}

		void SetBool(string key, bool? value, Func<bool> get, Action<bool> set)
		{
			if (value is { } v)
				Set(key, v, get, set);
		}

		bool ParseEnum<T>(string key, string? text, out T value, params T[] allowed) where T : struct, Enum
		{
			value = default;
			if (text is null)
				return false;
			var options = allowed.Length > 0 ? allowed : System.Enum.GetValues<T>();
			if (!int.TryParse(text, out _) && System.Enum.TryParse(text, true, out value) && options.Contains(value))
				return true;
			errors[key] = $"Must be one of: {string.Join(", ", options)}.";
			return false;
		}

		void SetEnum<T>(string key, string? text, Func<T> get, Action<T> set, params T[] allowed) where T : struct, Enum
		{
			if (ParseEnum<T>(key, text, out var value, allowed))
				Set(key, value, get, set);
		}

		void InRange(string key, int? value, int min, int max, Func<int> get, Action<int> set)
		{
			if (value is not { } v)
				return;
			if (v < min || v > max)
				errors[key] = $"Must be between {min} and {max}.";
			else
				Set(key, v, get, set);
		}

		if (r.Books is not null)
		{
			var path = CheckFolder("books", r.Books, errors);
			if (path is not null && Environment.GetEnvironmentVariable(BooksEnvironmentVariable) is { Length: > 0 })
				errors["books"] = $"Set by the {BooksEnvironmentVariable} environment variable; change that instead.";
			else if (path is not null)
			{
				folders.Add("books", path);
				Set<string?>("books", path, () => c.Books?.Path, v => c.Books = v);
			}
		}

		if (body.TryGetProperty("inProgress", out var inProgress))
		{
			if (inProgress.ValueKind == JsonValueKind.Null || string.IsNullOrWhiteSpace(r.InProgress))
				Set("inProgress", Configuration.WinTemp, () => c.InProgress, v => c.InProgress = v);
			else if (CheckFolder("inProgress", r.InProgress, errors) is { } path)
				Set("inProgress", path, () => c.InProgress, v => c.InProgress = v);
		}

		if (r.Naming is { } naming)
		{
			Template<Templates.FolderTemplate>("naming.folderTemplate", naming.FolderTemplate, () => c.FolderTemplate, v => c.FolderTemplate = v);
			Template<Templates.FileTemplate>("naming.fileTemplate", naming.FileTemplate, () => c.FileTemplate, v => c.FileTemplate = v);
			Template<Templates.ChapterFileTemplate>("naming.chapterFileTemplate", naming.ChapterFileTemplate, () => c.ChapterFileTemplate, v => c.ChapterFileTemplate = v);
			Template<Templates.ChapterTitleTemplate>("naming.chapterTitleTemplate", naming.ChapterTitleTemplate, () => c.ChapterTitleTemplate, v => c.ChapterTitleTemplate = v);
		}

		void Template<T>(string key, string? text, Func<string> get, Action<string> set) where T : Templates, ITemplate, new()
		{
			if (text is null)
				return;
			if (string.IsNullOrWhiteSpace(text))
			{
				errors[key] = "The template must not be empty.";
				return;
			}
			Templates.TryGetTemplate<T>(text, out var template);
			if (!template.IsValid)
				errors[key] = string.Join(" ", template.Errors);
			else
				Set(key, template.TemplateText, get, set);
		}

		if (r.Download is { } d)
		{
			SetEnum("download.quality", d.Quality, () => c.FileDownloadQuality, v => c.FileDownloadQuality = v);
			SetBool("download.overwriteExisting", d.OverwriteExisting, () => c.OverwriteExisting, v => c.OverwriteExisting = v);
			SetBool("download.useWidevine", d.UseWidevine, () => c.UseWidevine, v => c.UseWidevine = v);
			SetBool("download.requestXheAac", d.RequestXheAac, () => c.Request_xHE_AAC, v => c.Request_xHE_AAC = v);
			SetEnum("download.spatialAudioCodec", d.SpatialAudioCodec, () => c.SpatialAudioCodec, v => c.SpatialAudioCodec = v);
			SetBool("download.downloadCoverArt", d.DownloadCoverArt, () => c.DownloadCoverArt, v => c.DownloadCoverArt = v);
			SetBool("download.downloadClipsBookmarks", d.DownloadClipsBookmarks, () => c.DownloadClipsBookmarks, v => c.DownloadClipsBookmarks = v);
			SetEnum("download.clipsBookmarksFileFormat", d.ClipsBookmarksFileFormat, () => c.ClipsBookmarksFileFormat, v => c.ClipsBookmarksFileFormat = v);
			SetBool("download.createCueSheet", d.CreateCueSheet, () => c.CreateCueSheet, v => c.CreateCueSheet = v);
			SetBool("download.saveMetadataToFile", d.SaveMetadataToFile, () => c.SaveMetadataToFile, v => c.SaveMetadataToFile = v);
			SetBool("download.retainAaxFile", d.RetainAaxFile, () => c.RetainAaxFile, v => c.RetainAaxFile = v);
			SetBool("download.mergeOpeningAndEndCredits", d.MergeOpeningAndEndCredits, () => c.MergeOpeningAndEndCredits, v => c.MergeOpeningAndEndCredits = v);
			SetBool("download.moveMoovToBeginning", d.MoveMoovToBeginning, () => c.MoveMoovToBeginning, v => c.MoveMoovToBeginning = v);
			SetBool("download.combineNestedChapterTitles", d.CombineNestedChapterTitles, () => c.CombineNestedChapterTitles, v => c.CombineNestedChapterTitles = v);
			SetBool("download.useCoverAsFolderIcon", d.UseCoverAsFolderIcon, () => c.UseCoverAsFolderIcon, v => c.UseCoverAsFolderIcon = v);
			InRange("download.minimumFileDuration", d.MinimumFileDuration, 0, MaxMinimumFileDuration, () => c.MinimumFileDuration, v => c.MinimumFileDuration = v);
			SetEnum("download.creationTime", d.CreationTime, () => c.CreationTime, v => c.CreationTime = v);
			SetEnum("download.lastWriteTime", d.LastWriteTime, () => c.LastWriteTime, v => c.LastWriteTime = v);

			var fixup = d.AllowLibationFixup ?? c.AllowLibationFixup;
			SetBool("download.allowLibationFixup", d.AllowLibationFixup, () => c.AllowLibationFixup, v => c.AllowLibationFixup = v);
			void NeedsFixup(string key, bool? value, Func<bool> get, Action<bool> set)
			{
				if (fixup)
					SetBool(key, value, get, set);
				else if (value == true)
					errors[key] = "Requires allowLibationFixup.";
				else if (value == false || get())
					Set(key, false, get, set);
			}
			NeedsFixup("download.splitFilesByChapter", d.SplitFilesByChapter, () => c.SplitFilesByChapter, v => c.SplitFilesByChapter = v);
			NeedsFixup("download.stripUnabridged", d.StripUnabridged, () => c.StripUnabridged, v => c.StripUnabridged = v);
			NeedsFixup("download.stripAudibleBrandAudio", d.StripAudibleBrandAudio, () => c.StripAudibleBrandAudio, v => c.StripAudibleBrandAudio = v);
			NeedsFixup("download.decryptToLossy", d.DecryptToLossy, () => c.DecryptToLossy, v => c.DecryptToLossy = v);
		}

		if (r.Mp3 is { } m)
		{
			SetBool("mp3.targetBitrate", m.TargetBitrate, () => c.LameTargetBitrate, v => c.LameTargetBitrate = v);
			InRange("mp3.bitrate", m.Bitrate, MinBitrate, MaxBitrate, () => c.LameBitrate, v => c.LameBitrate = v);
			SetBool("mp3.constantBitrate", m.ConstantBitrate, () => c.LameConstantBitrate, v => c.LameConstantBitrate = v);
			SetBool("mp3.matchSourceBitrate", m.MatchSourceBitrate, () => c.LameMatchSourceBR, v => c.LameMatchSourceBR = v);
			InRange("mp3.vbrQuality", m.VbrQuality, MinVbrQuality, MaxVbrQuality, () => c.LameVBRQuality, v => c.LameVBRQuality = v);
			SetBool("mp3.downsampleMono", m.DownsampleMono, () => c.LameDownsampleMono, v => c.LameDownsampleMono = v);
			SetEnum("mp3.encoderQuality", m.EncoderQuality, () => c.LameEncoderQuality, v => c.LameEncoderQuality = v, EncoderQualities);
			if (m.MaxSampleRate is { } hz)
			{
				if (SampleRates.Cast<SampleRate?>().FirstOrDefault(s => (int)s! == hz) is { } rate)
					Set("mp3.maxSampleRate", rate, () => c.MaxSampleRate, v => c.MaxSampleRate = v);
				else
					errors["mp3.maxSampleRate"] = $"Must be one of: {string.Join(", ", SampleRates.Select(s => (int)s))}.";
			}
		}

		if (r.Queue is { } q)
		{
			SetEnum("queue.dailyDownloadLimit", q.DailyDownloadLimit, () => c.DailyDownloadLimit, v => c.DailyDownloadLimit = v);
			if (q.DailyDownloadLimitQuantity is { } quantity)
			{
				if (quantity < 1)
					errors["queue.dailyDownloadLimitQuantity"] = "Must be at least 1.";
				else
					Set("queue.dailyDownloadLimitQuantity", quantity, () => c.DailyDownloadLimitQuantity, v => c.DailyDownloadLimitQuantity = v);
			}
			SetEnum("queue.dailyDownloadLimitUnit", q.DailyDownloadLimitUnit, () => c.DailyDownloadLimitUnit, v => c.DailyDownloadLimitUnit = v);
			SetEnum("queue.badBook", q.BadBook, () => c.BadBook, v => c.BadBook = v);
		}

		if (r.Library is { } l)
		{
			SetBool("library.importEpisodes", l.ImportEpisodes, () => c.ImportEpisodes, v => c.ImportEpisodes = v);
			SetBool("library.importPlusTitles", l.ImportPlusTitles, () => c.ImportPlusTitles, v => c.ImportPlusTitles = v);
			SetBool("library.downloadEpisodes", l.DownloadEpisodes, () => c.DownloadEpisodes, v => c.DownloadEpisodes = v);
			SetBool("library.autoDownloadEpisodes", l.AutoDownloadEpisodes, () => c.AutoDownloadEpisodes, v => c.AutoDownloadEpisodes = v);
			SetBool("library.savePodcastsToParentFolder", l.SavePodcastsToParentFolder, () => c.SavePodcastsToParentFolder, v => c.SavePodcastsToParentFolder = v);
		}

		if (r.Audiobookshelf is { } abs)
			PlanAudiobookshelf(c, body.GetProperty("audiobookshelf"), abs, errors, Set<string?>, SetBool);

		SetEnum("logLevel", r.LogLevel, () => c.LogLevel, v => c.LogLevel = v);

		if (r.ReplacementCharacters is { } rows && ReplacementsFrom(rows, errors) is { } replacements)
			Set("replacementCharacters", replacements, () => c.ReplacementCharacters, v => c.ReplacementCharacters = v);
	}

	private static void PlanAudiobookshelf(
		Configuration c,
		JsonElement body,
		AudiobookshelfPatch a,
		Dictionary<string, string> errors,
		Action<string, string?, Func<string?>, Action<string?>> set,
		Action<string, bool?, Func<bool>, Action<bool>> setBool)
	{
		setBool("audiobookshelf.enabled", a.Enabled, () => c.AudiobookshelfEnabled, v => c.AudiobookshelfEnabled = v);
		setBool("audiobookshelf.includePdfs", a.IncludePdfs, () => c.AudiobookshelfIncludePdfs, v => c.AudiobookshelfIncludePdfs = v);
		setBool("audiobookshelf.pushProgressAfterUpload", a.PushProgressAfterUpload, () => c.AudiobookshelfPushProgressAfterUpload, v => c.AudiobookshelfPushProgressAfterUpload = v);

		bool Present(string name, out bool isNull)
		{
			isNull = body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Null;
			return body.TryGetProperty(name, out _);
		}

		string? Text(string key, string? value)
		{
			if (!string.IsNullOrWhiteSpace(value))
				return value.Trim();
			errors[key] = "Must not be blank; send null to clear it.";
			return null;
		}

		if (Present("serverUrl", out var urlNull))
		{
			if (urlNull)
				set("audiobookshelf.serverUrl", null, () => c.AudiobookshelfServerUrl, v => c.AudiobookshelfServerUrl = v);
			else if (Text("audiobookshelf.serverUrl", a.ServerUrl) is { } url)
			{
				if (AudiobookshelfApiService.TryValidateServerUrl(url, out var normalized, out var problem))
					set("audiobookshelf.serverUrl", normalized, () => c.AudiobookshelfServerUrl, v => c.AudiobookshelfServerUrl = v);
				else
					errors["audiobookshelf.serverUrl"] = problem;
			}
		}

		if (Present("apiToken", out var tokenNull))
		{
			if (tokenNull)
				set("audiobookshelf.apiToken", null, () => c.AudiobookshelfApiToken, v => c.AudiobookshelfApiToken = v);
			else if (Text("audiobookshelf.apiToken", a.ApiToken) is { } token)
				set("audiobookshelf.apiToken", AudiobookshelfTokenStorage.EncryptToken(token), () => c.AudiobookshelfApiToken, v => c.AudiobookshelfApiToken = v);
		}

		var libraryId = c.AudiobookshelfLibraryId;
		var libraryChanged = false;
		var libraryOk = true;
		if (Present("libraryId", out var libraryNull))
		{
			if (libraryNull)
			{
				libraryId = null;
				libraryChanged = true;
				set("audiobookshelf.libraryId", null, () => c.AudiobookshelfLibraryId, v => c.AudiobookshelfLibraryId = v);
			}
			else if (Text("audiobookshelf.libraryId", a.LibraryId) is { } id)
			{
				libraryChanged = id != libraryId;
				libraryId = id;
				set("audiobookshelf.libraryId", id, () => c.AudiobookshelfLibraryId, v => c.AudiobookshelfLibraryId = v);
			}
			else
				libraryOk = false;
		}

		if (Present("folderId", out var folderNull))
		{
			if (folderNull)
				set("audiobookshelf.folderId", null, () => c.AudiobookshelfFolderId, v => c.AudiobookshelfFolderId = v);
			else if (Text("audiobookshelf.folderId", a.FolderId) is { } folder)
			{
				if (string.IsNullOrWhiteSpace(libraryId))
					errors["audiobookshelf.folderId"] = "Requires libraryId.";
				else
					set("audiobookshelf.folderId", folder, () => c.AudiobookshelfFolderId, v => c.AudiobookshelfFolderId = v);
			}
		}
		else if (libraryOk && libraryChanged && !string.IsNullOrWhiteSpace(c.AudiobookshelfFolderId))
			set("audiobookshelf.folderId", null, () => c.AudiobookshelfFolderId, v => c.AudiobookshelfFolderId = v);
	}

	private static string? CheckFolder(string key, string path, Dictionary<string, string> errors)
	{
		if (string.IsNullOrWhiteSpace(path))
			errors[key] = "Must not be blank.";
		else if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
			errors[key] = "Contains characters that are not valid in a path.";
		else if (!Path.IsPathFullyQualified(path))
			errors[key] = "Must be an absolute path.";
		else if (File.Exists(path))
			errors[key] = "A file with that name already exists.";
		else
			return path;
		return null;
	}

	private static ReplacementCharacters? ReplacementsFrom(IReadOnlyList<ReplacementPatch> rows, Dictionary<string, string> errors)
	{
		var fixedRows = ReplacementCharacters.Default(Configuration.IsWindows).Replacements.Take(Replacement.FIXED_COUNT).ToList();
		var before = errors.Count;
		if (rows.Count < Replacement.FIXED_COUNT)
		{
			errors["replacementCharacters"] = $"The first {Replacement.FIXED_COUNT} entries ({string.Join(", ", fixedRows.Select(f => f.Description))}) are required.";
			return null;
		}

		var result = new List<Replacement>();
		var seen = new HashSet<char>();
		for (var i = 0; i < rows.Count; i++)
		{
			var key = $"replacementCharacters[{i}]";
			var row = rows[i];
			if (row.Replacement is null)
			{
				errors[key] = "replacement is required.";
				continue;
			}
			if (ReplacementCharacters.ContainsInvalidFilenameChar(row.Replacement))
			{
				errors[key] = "replacement contains characters that are not valid in a file name.";
				continue;
			}

			if (i < Replacement.FIXED_COUNT)
			{
				var fixedRow = fixedRows[i];
				if (row.Character is { } shown && shown != (fixedRow.CharacterToReplace == default ? "" : fixedRow.CharacterToReplace.ToString()))
					errors[key] = $"Entry {i} is {fixedRow.Description} and its character cannot change.";
				else
					result.Add(new Replacement(fixedRow.CharacterToReplace, row.Replacement, fixedRow.Description) { Mandatory = true });
				continue;
			}

			if (row.Character is not { Length: 1 } character)
				errors[key] = "character must be a single character.";
			else if (fixedRows.Any(f => f.CharacterToReplace == character[0]) || !seen.Add(character[0]))
				errors[key] = $"'{character}' is already replaced by another entry.";
			else
				result.Add(new Replacement(character[0], row.Replacement, row.Description ?? ""));
		}

		return errors.Count == before ? new ReplacementCharacters { Replacements = result } : null;
	}
}
