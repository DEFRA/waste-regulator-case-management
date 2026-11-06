namespace WasteRegulatorCaseManagement.AuditEvents.Analytics;

public interface IAnalyticsEventSender
{
	Task Send(AnalyticsEvent analyticsEvent, CancellationToken cancellationToken);
}
