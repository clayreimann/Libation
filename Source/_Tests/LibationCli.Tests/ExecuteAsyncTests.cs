using CommandLine;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Threading.Tasks;

namespace LibationCli.Tests;

[TestClass]
[DoNotParallelize]
public class ExecuteAsyncTests
{
	[Verb("version")]
	private sealed class StubOptions(Func<Task> body) : OptionsBase
	{
		protected override Task ProcessAsync() => body();
	}

	private int originalExitCode;
	private TextWriter originalError = Console.Error;
	private StringWriter capturedError = new();

	[TestInitialize]
	public void Initialize()
	{
		originalExitCode = Environment.ExitCode;
		originalError = Console.Error;
		capturedError = new StringWriter();
		Console.SetError(capturedError);
	}

	[TestCleanup]
	public void Cleanup()
	{
		Console.SetError(originalError);
		Environment.ExitCode = originalExitCode;
	}

	[TestMethod]
	public async Task Returns_zero_and_clears_a_stale_exit_code()
	{
		Environment.ExitCode = 7;
		Assert.AreEqual(0, await new StubOptions(() => Task.CompletedTask).ExecuteAsync());
	}

	[TestMethod]
	public async Task Returns_the_exit_code_the_body_sets()
	{
		var rc = await new StubOptions(() =>
		{
			Environment.ExitCode = (int)ExitCode.NonRunNonError;
			return Task.CompletedTask;
		}).ExecuteAsync();
		Assert.AreEqual((int)ExitCode.NonRunNonError, rc);
	}

	[TestMethod]
	public async Task A_throwing_body_is_reported_as_a_runtime_error()
	{
		var rc = await new StubOptions(() => throw new InvalidOperationException("boom")).ExecuteAsync();
		Assert.AreEqual((int)ExitCode.RunTimeError, rc);
		Assert.Contains("boom", capturedError.ToString());
	}
}
