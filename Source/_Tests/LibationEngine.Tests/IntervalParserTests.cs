using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
public class IntervalParserTests
{
	[TestMethod]
	[DataRow("1h", 3600)]
	[DataRow("30m", 1800)]
	[DataRow("90s", 90)]
	[DataRow("3600", 3600)]
	[DataRow("01:00:00", 3600)]
	[DataRow("0:01:30", 90)]
	[DataRow("1d", 86400)]
	[DataRow(" 2H ", 7200)]
	public void Accepts_supported_forms(string text, int seconds)
	{
		Assert.IsTrue(IntervalParser.TryParse(text, out var interval));
		Assert.AreEqual(TimeSpan.FromSeconds(seconds), interval);
	}

	[TestMethod]
	[DataRow("")]
	[DataRow(null)]
	[DataRow("-1")]
	[DataRow("0")]
	[DataRow("0s")]
	[DataRow("abc")]
	[DataRow("1x")]
	[DataRow("1.5h")]
	[DataRow("00:00:00")]
	public void Rejects_everything_else(string? text)
		=> Assert.IsFalse(IntervalParser.TryParse(text, out _));

	[TestMethod]
	public void Parse_throws_on_bad_input()
		=> Assert.ThrowsExactly<FormatException>(() => IntervalParser.Parse("nope"));
}
