using AaxDecrypter;
using AudibleUtilities;
using DataLayer;
using Dinah.Core;
using Dinah.Core.Net.Http;
using LibationFileManager;
using Newtonsoft.Json.Linq;

namespace LibationEngine;

public sealed record AudioQuality(string Codec, int Bitrate);

public sealed record QualityUpgradeSettings(bool WidevineEnabled, bool XHeAacRequested, bool AccountsMarkedForReRegistration);

/// <summary>The better-quality rules shared by the desktop dialog and the daemon scan.</summary>
public static class QualityCheck
{
	public const double SignificantRatio = 1.15;

	public static bool ShouldScan(LibraryBook lb)
		=> lb.Book.ContentType is ContentType.Product
		&& !lb.Book.IsSpatial // /metadata reports only ac-4 for spatial books
		&& lb.Book.UserDefinedItem.BookStatus is LiberatedStatus.Liberated;

	public static bool IsSignificant(int bitrate, int availableBitrate)
		=> (double)availableBitrate / bitrate >= SignificantRatio;

	public static (string? File, AudioFormat Format) FindHighestExistingFormat(LibraryBook libraryBook)
	{
		var largestFile
			= AudibleFileStorage.Audio
			.GetPaths(libraryBook.Book.AudibleProductId)
			.Select(p => new FileInfo(p))
			.Where(f => f.Exists && f.Extension.EqualsInsensitive(".m4b"))
			.OrderByDescending(f => f.Length)
			.FirstOrDefault()
			?.FullName;

		return largestFile is null ? (null, AudioFormat.Default)
			: (largestFile, AudioFormatDecoder.FromMpeg4(largestFile));
	}

	public static string MetadataUrl(LibraryBook libraryBook, bool widevine)
	{
		var locale = AudibleApi.Localization.Get(libraryBook.Book.Locale);
		return string.Format(BaseUrl, locale.TopDomain, libraryBook.Book.AudibleProductId, widevine ? "Widevine" : "Adrm");
	}

	public static async Task<AudioQuality> ReadAvailableAsync(HttpResponseMessage response)
	{
		var data = await response.Content.ReadAsJObjectAsync();
		var totalLengthMs = data["content_metadata"]?["chapter_info"]?.Value<long>("runtime_length_ms") ?? throw new InvalidDataException("Missing runtime length");
		var contentReference = data["content_metadata"]?["content_reference"];
		var totalSize = contentReference?.Value<long>("content_size_in_bytes") ?? throw new InvalidDataException("Missing content size");
		var codec = contentReference?.Value<string>("codec") ?? throw new InvalidDataException("Missing codec name");

		var codecString = codec switch
		{
			AudibleApi.Codecs.AAC_LC => "AAC-LC",
			AudibleApi.Codecs.xHE_AAC => "xHE-AAC",
			AudibleApi.Codecs.EC_3 => "EC-3",
			AudibleApi.Codecs.AC_4 => "AC-4",
			_ => codec,
		};

		return new(codecString, (int)(totalSize / 1024d * 1000 / totalLengthMs * 8));
	}

	/// <summary>Switches on Widevine and xHE-AAC and flags accounts for sign-in, as desktop does before re-downloading upgrades.</summary>
	public static QualityUpgradeSettings ApplyUpgradeSettings(bool scanWidevine, IReadOnlyCollection<string?> availableCodecs)
	{
		var configuration = Configuration.Instance;
		bool widevineNewlyEnabled = false;
		bool xHeAacNewlyRequested = false;
		bool accountsMarked = false;

		bool requiresWidevine = scanWidevine || availableCodecs.Any(c => c is "xHE-AAC" or "EC-3" or "AC-4");
		if (File.Exists(AudibleApiStorage.AccountsSettingsFile))
		{
			using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
			if (requiresWidevine)
			{
				var recovery = ThrottledDeviceRecovery.ApplyRecovery(persister.AccountsSettings, configuration);
				widevineNewlyEnabled = recovery.WidevineEnabled;
				accountsMarked = recovery.MarkedAccounts.Count > 0;
			}
			else
			{
				accountsMarked = ThrottledDeviceRecovery.MarkObsoleteAccounts(persister.AccountsSettings) > 0;
			}
		}
		else if (requiresWidevine)
		{
			widevineNewlyEnabled = !configuration.UseWidevine;
			configuration.UseWidevine = true;
			configuration.DeviceRegistrationKind = AudibleApi.DeviceRegistrationKind.CurrentAndroid;
		}

		if (requiresWidevine && availableCodecs.Any(c => c == "xHE-AAC") && !configuration.Request_xHE_AAC)
		{
			configuration.Request_xHE_AAC = true;
			xHeAacNewlyRequested = true;
		}

		return new(widevineNewlyEnabled, xHeAacNewlyRequested, accountsMarked);
	}

	private const string BaseUrl = "ht" + "tps://api.audible.{0}/1.0/content/{1}/metadata?response_groups=chapter_info,content_reference&quality=High&drm_type={2}";
}

public interface IAudibleQuality
{
	/// <summary>Throws when Audible cannot be reached or answers with an error.</summary>
	Task<AudioQuality> GetAvailableAsync(LibraryBook book, bool widevine, CancellationToken ct);
}

public sealed class AudibleQuality : IAudibleQuality
{
	private static readonly HttpClient Client = new();

	public async Task<AudioQuality> GetAvailableAsync(LibraryBook book, bool widevine, CancellationToken ct)
	{
		using var response = await Client.GetAsync(QualityCheck.MetadataUrl(book, widevine), ct);
		return await QualityCheck.ReadAvailableAsync(response.EnsureSuccessStatusCode());
	}
}
