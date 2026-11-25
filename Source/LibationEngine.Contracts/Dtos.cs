namespace LibationEngine.Contracts;

public sealed record StatusDto(
	string State,
	DateTimeOffset? LastRun,
	DateTimeOffset? LastSuccess,
	int? ScanExitCode,
	int? LiberateExitCode,
	DateTimeOffset? NextRun,
	int IntervalSeconds,
	string? Version,
	IReadOnlyList<string> AccountsNeedingLogin);

public static class AuthStates
{
	public const string SignedIn = "signedIn", NeedsLogin = "needsLogin";
}

public static class CredentialKinds
{
	public const string Modern = "modern", Legacy = "legacy", None = "none";
}

public sealed record AccountDto(
	string AccountId,
	string? AccountName,
	string? Locale,
	bool LibraryScan,
	string AuthState,
	string Credentials,
	IReadOnlyList<string> AdditionalLocales);

public sealed record AccountPatchRequest(string? AccountName, bool? LibraryScan);

public sealed record ScanRequest(IReadOnlyList<string>? Accounts);

public sealed record SeriesDto(string Name, string? Order, float Index, string Asin);

public sealed record RatingDto(float Overall, float Performance, float Story);

public record BookDto(
	string Asin,
	string Title,
	string? Subtitle,
	IReadOnlyList<string> Authors,
	IReadOnlyList<string> Narrators,
	IReadOnlyList<SeriesDto> Series,
	int LengthInMinutes,
	string BookStatus,
	string? PdfStatus,
	DateTime DateAdded,
	string Account,
	string Locale,
	string ContentType,
	bool IsPodcastEpisode,
	string Tags,
	bool InTrash,
	IReadOnlyList<string> Categories,
	RatingDto ProductRating,
	RatingDto MyRating,
	bool HasPdf,
	bool IsAudiblePlus,
	DateTime? IncludedUntil,
	DateTime? LastDownloaded,
	string? LastDownloadedVersion,
	string? LastDownloadedFormat,
	bool IsSpatial,
	string? PictureId,
	string? ParentAsin,
	bool AbsentFromLastScan,
	bool PartialDownload,
	bool IsPodcastParent);

/// <summary>A <see cref="BookDto"/> plus the fields too heavy for list payloads.</summary>
public sealed record BookDetailDto : BookDto
{
	public BookDetailDto(BookDto book) : base(book) { }

	public string? Description { get; init; }
	public string? Publisher { get; init; }
	public string? Language { get; init; }
	public string? Copyright { get; init; }
	public bool IsAbridged { get; init; }
	public DateTime? DatePublished { get; init; }
	public IReadOnlyList<IReadOnlyList<string>> CategoryLadders { get; init; } = [];
	public bool AudioFileExists { get; init; }
}

/// <summary>Desktop status-bar counts; Partial has a half-downloaded file, Unavailable is absent from the last scan.</summary>
public sealed record BackupCountsDto(
	int Total,
	int Liberated,
	int Partial,
	int NotLiberated,
	int Error,
	int Unavailable,
	int PdfLiberated,
	int PdfNotLiberated,
	int PdfUnavailable);

public sealed record LibraryPageDto(int Total, int Offset, int Limit, IReadOnlyList<BookDto> Items);

/// <summary>Total counts every match; Asins is capped at 50000.</summary>
public sealed record LibraryAsinsDto(int Total, IReadOnlyList<string> Asins);

public sealed record ErrorDto(string Error);

public sealed record QueueJobDto(string Id, string Asin, string Title, string State);

public sealed record PendingDecisionDto(long Id, string Reason, string? JobId, IReadOnlyList<string> Allowed);

public sealed record VolumeDto(string Root, long? FreeBytes, long RequiredBytes);

public sealed record QueueWaitDto(
	long Id,
	string Reason,
	DateTimeOffset Since,
	DateTimeOffset? NextCheck,
	string? JobId,
	IReadOnlyList<string> Allowed,
	IReadOnlyList<VolumeDto>? Volumes);

/// <summary>Queued waiting, completed and errors count as the desktop queue's do; RunningSince is null when no run is in progress.</summary>
public sealed record QueueStatsDto(int Queued, int Completed, int Errors, DateTimeOffset? RunningSince);

/// <summary>Paused is the user's hold on starting new books, separate from <see cref="Pending"/> and <see cref="Waiting"/>.</summary>
public sealed record QueueDto(
	bool Running,
	IReadOnlyList<QueueJobDto> Jobs,
	IReadOnlyList<PendingDecisionDto> Pending,
	QueueWaitDto? Waiting,
	bool Paused,
	QueueStatsDto Stats);

/// <summary>Outcome is removed for a queued job and cancelling for a running one, which finishes as cancelled.</summary>
public sealed record CancelJobDto(string Id, string Outcome);

/// <summary>Body of <c>POST /api/queue/jobs/{id}/move</c>; To is top, up, down or bottom.</summary>
public sealed record MoveRequest(string? To);

public sealed record FinishedJobDto(string Id, string Asin, string Title, string Result, string Status, DateTimeOffset FinishedAt);

public sealed record JobLogLineDto(DateTimeOffset At, string Level, string Message);

/// <summary>Dropped counts older lines pushed out of the bounded log.</summary>
public sealed record JobLogDto(string JobId, int Dropped, IReadOnlyList<JobLogLineDto> Lines);

/// <summary>Body of a decision POST; <see cref="Action"/>, <see cref="ApplyToAll"/> and <see cref="Remember"/> apply to badBook only.</summary>
public sealed record DecisionRequest(string? Decision, string? Action = null, bool ApplyToAll = false, bool Remember = false);

/// <summary>Body of <c>PUT /api/library/{asin}/tags</c>; blank clears the tags.</summary>
public sealed record TagsRequest(string? Tags);

/// <summary>Body of <c>PUT /api/library/{asin}/status</c>; Status is liberated or notLiberated.</summary>
public sealed record StatusRequest(string? Status);

/// <summary>Body of bulk library actions; at most 5000 ASINs.</summary>
public sealed record BulkRequest(IReadOnlyList<string>? Asins);

/// <summary>Body of <c>PUT /api/library/tags</c>; Tags replaces, Add and Remove edit, and Tags excludes the other two.</summary>
public sealed record BulkTagsRequest(IReadOnlyList<string>? Asins, string? Tags = null, string? Add = null, string? Remove = null);

/// <summary>Body of <c>PUT /api/library/status</c> and <c>/pdf-status</c>; Status is liberated or notLiberated.</summary>
public sealed record BulkStatusRequest(IReadOnlyList<string>? Asins, string? Status);

/// <summary>Body of <c>POST /api/library/{asin}/locate</c>; Path is an absolute audio file under the Books folder.</summary>
public sealed record LocateRequest(string? Path);

/// <summary>Body of <c>POST /api/library/locate-scan</c>; Folder is an absolute folder under the Books folder.</summary>
public sealed record LocateScanRequest(string? Folder);

/// <summary>State is idle, running, completed or failed. Found counts audio files carrying an ASIN, Matched those whose book is in the library, Updated the books newly marked liberated.</summary>
public sealed record LocateScanDto(string State, string? Folder, int Found, int Matched, int Updated, string? Error, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

/// <summary>Result is ok, notFound or conflict.</summary>
public sealed record BulkItemDto(string Asin, string Result);

/// <summary>Updated counts ok items; Missing lists the notFound ASINs; Results has every ASIN in request order.</summary>
public sealed record BulkResultDto(int Updated, IReadOnlyList<string> Missing, IReadOnlyList<BulkItemDto> Results);

public sealed record NamingSettingsDto(string FolderTemplate, string FileTemplate, string ChapterFileTemplate, string ChapterTitleTemplate);

public sealed record DownloadSettingsDto(
	string Quality,
	bool OverwriteExisting,
	bool UseWidevine,
	bool RequestXheAac,
	string SpatialAudioCodec,
	bool DownloadCoverArt,
	bool DownloadClipsBookmarks,
	string ClipsBookmarksFileFormat,
	bool CreateCueSheet,
	bool SaveMetadataToFile,
	bool RetainAaxFile,
	bool SplitFilesByChapter,
	bool MergeOpeningAndEndCredits,
	bool StripUnabridged,
	bool StripAudibleBrandAudio,
	bool DecryptToLossy,
	bool MoveMoovToBeginning,
	bool CombineNestedChapterTitles,
	bool AllowLibationFixup,
	bool UseCoverAsFolderIcon,
	int MinimumFileDuration,
	string CreationTime,
	string LastWriteTime);

public sealed record QueueSettingsDto(
	int MaxConcurrentDownloads,
	long DownloadSpeedLimit,
	string DailyDownloadLimit,
	int DailyDownloadLimitQuantity,
	string DailyDownloadLimitUnit,
	string BadBook);

public sealed record LibrarySettingsDto(
	bool ImportEpisodes,
	bool ImportPlusTitles,
	bool DownloadEpisodes,
	bool AutoDownloadEpisodes,
	bool SavePodcastsToParentFolder);

public sealed record AudiobookshelfSettingsDto(bool Enabled, bool IncludePdfs, bool ServerConfigured);

public sealed record Mp3SettingsDto(
	bool TargetBitrate,
	int Bitrate,
	bool ConstantBitrate,
	bool MatchSourceBitrate,
	int VbrQuality,
	bool DownsampleMono,
	string EncoderQuality,
	int MaxSampleRate);

/// <summary>Character is empty for the "all other invalid characters" row; the first six rows are mandatory.</summary>
public sealed record ReplacementDto(string Character, string Replacement, string Description, bool Mandatory);

public sealed record FolderCheckDto(string? Path, bool Exists, bool Writable);

public sealed record FoldersDto(FolderCheckDto Books, FolderCheckDto InProgress);

/// <summary>Curated, non-secret settings; no tokens, key files or account data. RestartRequired is set only on a PATCH response.</summary>
public sealed record SettingsDto(
	string? Books,
	string InProgress,
	NamingSettingsDto Naming,
	DownloadSettingsDto Download,
	QueueSettingsDto Queue,
	LibrarySettingsDto Library,
	AudiobookshelfSettingsDto Audiobookshelf,
	Mp3SettingsDto Mp3,
	string LogLevel,
	IReadOnlyList<ReplacementDto> ReplacementCharacters,
	FoldersDto Folders,
	IReadOnlyList<string>? RestartRequired = null);

public sealed record NamingPatch(string? FolderTemplate, string? FileTemplate, string? ChapterFileTemplate, string? ChapterTitleTemplate);

public sealed record DownloadPatch(
	string? Quality,
	bool? OverwriteExisting,
	bool? UseWidevine,
	bool? RequestXheAac,
	string? SpatialAudioCodec,
	bool? DownloadCoverArt,
	bool? DownloadClipsBookmarks,
	string? ClipsBookmarksFileFormat,
	bool? CreateCueSheet,
	bool? SaveMetadataToFile,
	bool? RetainAaxFile,
	bool? SplitFilesByChapter,
	bool? MergeOpeningAndEndCredits,
	bool? StripUnabridged,
	bool? StripAudibleBrandAudio,
	bool? DecryptToLossy,
	bool? MoveMoovToBeginning,
	bool? CombineNestedChapterTitles,
	bool? AllowLibationFixup,
	bool? UseCoverAsFolderIcon,
	int? MinimumFileDuration,
	string? CreationTime,
	string? LastWriteTime);

public sealed record Mp3Patch(
	bool? TargetBitrate,
	int? Bitrate,
	bool? ConstantBitrate,
	bool? MatchSourceBitrate,
	int? VbrQuality,
	bool? DownsampleMono,
	string? EncoderQuality,
	int? MaxSampleRate);

public sealed record QueuePatch(string? DailyDownloadLimit, int? DailyDownloadLimitQuantity, string? DailyDownloadLimitUnit, string? BadBook);

public sealed record LibraryPatch(bool? ImportEpisodes, bool? ImportPlusTitles, bool? DownloadEpisodes, bool? AutoDownloadEpisodes, bool? SavePodcastsToParentFolder);

public sealed record ReplacementPatch(string? Character, string? Replacement, string? Description);

/// <summary>Body of <c>PATCH /api/settings</c>; only present keys change. A null InProgress resets it to the default; every other key rejects null.</summary>
public sealed record SettingsPatchRequest(
	string? Books,
	string? InProgress,
	NamingPatch? Naming,
	DownloadPatch? Download,
	Mp3Patch? Mp3,
	QueuePatch? Queue,
	LibraryPatch? Library,
	string? LogLevel,
	IReadOnlyList<ReplacementPatch>? ReplacementCharacters);

/// <summary>A rejected PATCH: Fields maps the dotted key (for example <c>naming.folderTemplate</c>) to its message.</summary>
public sealed record FieldErrorsDto(string Error, IReadOnlyDictionary<string, string> Fields);

/// <summary>Body of <c>POST /api/settings/templates/preview</c>; Kind is folder, file, chapterFile or chapterTitle.</summary>
public sealed record TemplatePreviewRequest(string? Template, string? Kind, string? Asin);

/// <summary>Preview is null when the template is invalid.</summary>
public sealed record TemplatePreviewDto(bool Valid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, string? Preview);

public sealed record QuickFilterDto(string Filter, string? Name = null);

public sealed record QuickFiltersDto(bool UseDefault, IReadOnlyList<QuickFilterDto> Filters);

/// <summary>Body of <c>POST /api/accounts/login</c>; AccountId may be omitted only when exactly one account uses that locale.</summary>
public sealed record LoginStartRequest(string? AccountId, string? Locale, string? DeviceRegistration = null);

/// <summary>Open Url in a browser, sign in, then post the final address-bar URL to <c>/api/accounts/login/{loginId}</c> before ExpiresAt.</summary>
public sealed record LoginStartDto(string LoginId, string Url, DateTimeOffset ExpiresAt);

/// <summary>Body of <c>POST /api/accounts/login/{loginId}</c>; treat the URL like a password.</summary>
public sealed record LoginCompleteRequest(string? ResponseUrl);

public sealed record LoginCancelDto(string LoginId);
