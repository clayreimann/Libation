namespace LibationEngine;

public enum ProcessBookResult
{
	None,
	Success,
	Cancelled,
	ValidationFail,
	FailedRetry,
	FailedSkip,
	FailedAbort,
	LicenseDenied,
	LicenseDeniedPossibleOutage,
	/// <summary>Audible named CustomerThrottled on the license denial.</summary>
	LicenseDeniedThrottled,
	/// <summary>Audible throttled on legacy/non-Widevine registration; settings thunked to Android+Widevine and account marked for re-registration.</summary>
	LicenseDeniedThrottledRecovered,
	/// <summary>ADRM licenserequest failed with Sable acr:null; Widevine may work (see WidevineRecommendation).</summary>
	WidevineRecommended,
	/// <summary>Volume full on write; queue should stop (see LiberationQueue).</summary>
	DiskFull
}
