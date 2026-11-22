using System.Collections.Generic;

namespace LibationCli;

internal static class CliCommandGroups
{
	public static IReadOnlyList<CliCommandGroup> All { get; } =
	[
		new(
			"abs",
			"Audiobookshelf commands.",
			[
				new("upload", "upload", "Upload liberated audiobooks to Audiobookshelf.", typeof(AbsUploadOptions)),
			]),
		new(
			"progress",
			"Audible listening progress commands.",
			[
				new("export", "progress-export", "Save Audible listening progress as JSON.", typeof(ProgressExportOptions)),
				new("push-abs", "progress-push-abs", "Copy Audible listening progress to Audiobookshelf.", typeof(ProgressPushAbsOptions)),
				new("probe", "progress-probe", "Save raw Audible progress responses for one title (developer aid).", typeof(ProgressProbeOptions)),
			]),
	];
}
