namespace WasteRegulatorCaseManagement.AuditEvents;

internal static class AuditEventTime
{
	/// <summary>The current UTC time truncated to the millisecond precision BSON dates store.</summary>
	public static DateTime Now(TimeProvider timeProvider)
	{
		var now = timeProvider.GetUtcNow();
		return new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero).UtcDateTime;
	}
}
