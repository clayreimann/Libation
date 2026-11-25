using DataLayer;
using Dinah.Core.ErrorHandling;
using FileLiberator;
using LibationEngine;
using LibationFileManager;
using System.Threading.Tasks;

namespace LibationCli.Tests;

/// <summary>A liberate run whose steps are fakes, driven through a real queue.</summary>
internal sealed class StubbedLiberate(Processable book, Processable? pdf = null) : LiberateOptions
{
	internal override Processable CreateBookStep(DownloadOptions.LicenseInfo? license) => book;
	internal override Processable CreatePdfStep() => pdf ?? new NotApplicable { Configuration = Configuration.Instance };
	internal override Processable CreateUploadStep() => new NotApplicable { Configuration = Configuration.Instance };

	public Task Go(LiberationQueue? queue = null)
		=> RunBulkOrTargetedAsync(queue ?? CreateCliQueue());

	private sealed class NotApplicable : Processable
	{
		public override string Name => nameof(NotApplicable);
		public override bool Validate(LibraryBook libraryBook) => false;
		public override Task<StatusHandler> ProcessAsync(LibraryBook libraryBook) => Task.FromResult(new StatusHandler());
	}
}
