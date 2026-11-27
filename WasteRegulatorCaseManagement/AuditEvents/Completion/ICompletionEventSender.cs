namespace WasteRegulatorCaseManagement.AuditEvents.Completion;

public interface ICompletionEventSender
{
	Task Send(CaseCompletionEnvelope envelope, CancellationToken cancellationToken);
}