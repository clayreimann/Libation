using CommandLine;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Linq;
using System.Reflection;

namespace LibationCli.Tests;

[TestClass]
public class ProgressOptionsParseTests
{
	private static T Parse<T>(params string[] args) where T : class
	{
		using var error = new StringWriter();
		var outcome = Program.ParseInvocation(args, error);

		Assert.IsNull(outcome.ExitCode, error.ToString());
		return (outcome.Result!.Value as T)!;
	}

	[TestMethod]
	public void Probe_parses_asin_and_output()
	{
		var options = Parse<ProgressProbeOptions>("progress", "probe", "B000000001", "--output", "dir");

		Assert.AreEqual("B000000001", options.Asin);
		Assert.AreEqual("dir", options.OutputDirectory);
	}

	[TestMethod]
	public void Export_parses_path_and_account()
	{
		var options = Parse<ProgressExportOptions>("progress", "export", "-p", "f.json", "-a", "Main");

		Assert.AreEqual("f.json", options.FilePath);
		CollectionAssert.AreEqual(new[] { "Main" }, options.Accounts!.ToArray());
	}

	[TestMethod]
	public void PushAbs_parses_flags_and_library_id()
	{
		var options = Parse<ProgressPushAbsOptions>("progress", "push-abs", "--dry-run", "--force", "--library-id", "x");

		Assert.IsTrue(options.DryRun);
		Assert.IsTrue(options.Force);
		Assert.AreEqual("x", options.LibraryId);
	}

	[TestMethod]
	public void No_verb_reuses_an_option_name()
	{
		var verbs = typeof(Program).Assembly.GetTypes().Where(t => !t.IsAbstract && t.GetCustomAttribute<VerbAttribute>() is not null);
		foreach (var verb in verbs)
		{
			var options = verb.GetProperties(BindingFlags.Public | BindingFlags.Instance)
				.Select(p => p.GetCustomAttribute<OptionAttribute>())
				.OfType<OptionAttribute>()
				.ToList();
			var names = options.Where(o => !string.IsNullOrEmpty(o.ShortName)).Select(o => "-" + o.ShortName)
				.Concat(options.Where(o => !string.IsNullOrEmpty(o.LongName)).Select(o => "--" + o.LongName));
			var duplicates = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
			Assert.IsEmpty(duplicates, $"{verb.Name} declares {string.Join(", ", duplicates)} more than once.");
		}
	}
}
