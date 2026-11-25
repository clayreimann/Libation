using FileManager;
using LibationEngine.Contracts;
using LibationFileManager;

namespace LibationEngine;

/// <summary>Projects the non-secret settings; add a field only after checking it is not a credential or key path.</summary>
public static class SettingsMapper
{
	public static SettingsDto From(Configuration c) => new(
		c.Books?.Path,
		c.InProgress,
		new(c.FolderTemplate, c.FileTemplate, c.ChapterFileTemplate, c.ChapterTitleTemplate),
		new(
			c.FileDownloadQuality.ToString(),
			c.OverwriteExisting,
			c.UseWidevine,
			c.Request_xHE_AAC,
			c.SpatialAudioCodec.ToString(),
			c.DownloadCoverArt,
			c.DownloadClipsBookmarks,
			c.ClipsBookmarksFileFormat.ToString(),
			c.CreateCueSheet,
			c.SaveMetadataToFile,
			c.RetainAaxFile,
			c.SplitFilesByChapter,
			c.MergeOpeningAndEndCredits,
			c.StripUnabridged,
			c.StripAudibleBrandAudio,
			c.DecryptToLossy,
			c.MoveMoovToBeginning,
			c.CombineNestedChapterTitles,
			c.AllowLibationFixup,
			c.UseCoverAsFolderIcon,
			c.MinimumFileDuration,
			c.CreationTime.ToString(),
			c.LastWriteTime.ToString()),
		new(
			c.MaxConcurrentDownloads,
			c.DownloadSpeedLimit,
			c.DailyDownloadLimit.ToString(),
			c.DailyDownloadLimitQuantity,
			c.DailyDownloadLimitUnit.ToString(),
			c.BadBook.ToString()),
		new(c.ImportEpisodes, c.ImportPlusTitles, c.DownloadEpisodes, c.AutoDownloadEpisodes, c.SavePodcastsToParentFolder),
		new(
			c.AudiobookshelfEnabled,
			c.AudiobookshelfIncludePdfs,
			!string.IsNullOrWhiteSpace(c.AudiobookshelfServerUrl),
			string.IsNullOrWhiteSpace(c.AudiobookshelfServerUrl) ? null : c.AudiobookshelfServerUrl,
			!string.IsNullOrWhiteSpace(c.AudiobookshelfApiToken),
			string.IsNullOrWhiteSpace(c.AudiobookshelfLibraryId) ? null : c.AudiobookshelfLibraryId,
			string.IsNullOrWhiteSpace(c.AudiobookshelfFolderId) ? null : c.AudiobookshelfFolderId),
		new(
			c.LameTargetBitrate,
			c.LameBitrate,
			c.LameConstantBitrate,
			c.LameMatchSourceBR,
			c.LameVBRQuality,
			c.LameDownsampleMono,
			c.LameEncoderQuality.ToString(),
			(int)c.MaxSampleRate),
		c.LogLevel.ToString(),
		c.ReplacementCharacters.Replacements
			.Select(r => new ReplacementDto(r.CharacterToReplace == default ? "" : r.CharacterToReplace.ToString(), r.ReplacementString, r.Description, r.Mandatory))
			.ToList(),
		new(Check(c.Books?.Path), Check(c.InProgress)));

	private static FolderCheckDto Check(string? path)
		=> string.IsNullOrWhiteSpace(path)
			? new(path, false, false)
			: new(path, Directory.Exists(path), FileSystemTest.CanWriteDirectory(path));
}
