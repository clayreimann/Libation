using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
public class ThrottleRecoveryGateTests
{
	private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 26, 23, 42, 0, TimeSpan.Zero));

	[TestMethod]
	public void One_title_throttled_again_and_again_never_recovers_the_account()
	{
		var gate = new ThrottleRecoveryGate(time: clock);
		for (var i = 0; i < 10; i++)
		{
			Assert.IsFalse(gate.RecordThrottle("orig", "B002UZZ5PS"));
			clock.Advance(TimeSpan.FromHours(1));
		}
	}

	[TestMethod]
	public void Enough_different_titles_recover_and_the_count_starts_again()
	{
		var gate = new ThrottleRecoveryGate(distinctTitles: 3, time: clock);
		Assert.IsFalse(gate.RecordThrottle("orig", "B1"));
		Assert.IsFalse(gate.RecordThrottle("orig", "B2"));
		Assert.IsTrue(gate.RecordThrottle("orig", "B3"));
		Assert.AreEqual(0, gate.ThrottledTitleCount("orig"));
		Assert.IsFalse(gate.RecordThrottle("orig", "B4"));
	}

	[TestMethod]
	public void Accounts_are_counted_separately()
	{
		var gate = new ThrottleRecoveryGate(distinctTitles: 2, time: clock);
		Assert.IsFalse(gate.RecordThrottle("orig", "B1"));
		Assert.IsFalse(gate.RecordThrottle("second", "B2"));
		Assert.IsTrue(gate.RecordThrottle("orig", "B3"));
	}

	[TestMethod]
	public void Titles_older_than_the_window_stop_counting()
	{
		var gate = new ThrottleRecoveryGate(distinctTitles: 2, window: TimeSpan.FromHours(24), time: clock);
		Assert.IsFalse(gate.RecordThrottle("orig", "B1"));
		clock.Advance(TimeSpan.FromHours(25));
		Assert.IsFalse(gate.RecordThrottle("orig", "B2"));
		Assert.AreEqual(1, gate.ThrottledTitleCount("orig"));
	}

	[TestMethod]
	public void A_licensed_download_on_the_account_starts_the_count_again()
	{
		var gate = new ThrottleRecoveryGate(distinctTitles: 2, time: clock);
		Assert.IsFalse(gate.RecordThrottle("orig", "B1"));
		gate.RecordLicensed("orig");
		Assert.IsFalse(gate.RecordThrottle("orig", "B2"));
	}
}
