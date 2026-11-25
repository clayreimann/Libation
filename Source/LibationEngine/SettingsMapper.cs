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
		new(c.AudiobookshelfEnabled, c.AudiobookshelfIncludePdfs, !string.IsNullOrWhiteSpace(c.AudiobookshelfServerUrl)));
}
