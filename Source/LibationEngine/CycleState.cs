namespace LibationEngine;

public enum DaemonState { Idle, Reconciling, Scanning, Liberating }

public sealed record CycleSnapshot(
	string State,
	DateTimeOffset? LastRun,
	DateTimeOffset? LastSuccess,
	int? ScanExitCode,
	int? LiberateExitCode,
	DateTimeOffset? NextRun);

public sealed class CycleState
{
	private readonly object gate = new();
	private DaemonState state = DaemonState.Idle;
	private DateTimeOffset? lastRun, lastSuccess, nextRun;
	private int? scanRc, liberateRc;

	public void Set(DaemonState value) { lock (gate) state = value; }

	public void Seed(DateTimeOffset? lastSuccess, int? liberateRc)
	{
		lock (gate)
		{
			this.lastSuccess = lastSuccess;
			this.liberateRc = liberateRc;
		}
	}

	public void RecordScan(DateTimeOffset now, int rc)
	{
		lock (gate)
		{
			lastRun = now;
			scanRc = rc;
			if (rc == 0)
				lastSuccess = now;
		}
	}

	public void RecordLiberate(int rc) { lock (gate) liberateRc = rc; }

	public void SetNextRun(DateTimeOffset? value) { lock (gate) nextRun = value; }

	public CycleSnapshot Snapshot()
	{
		lock (gate)
			return new(state.ToString().ToLowerInvariant(), lastRun, lastSuccess, scanRc, liberateRc, nextRun);
	}
}
