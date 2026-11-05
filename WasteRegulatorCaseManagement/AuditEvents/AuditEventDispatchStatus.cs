namespace WasteRegulatorCaseManagement.AuditEvents;

public enum AuditEventDispatchStatus
{
	Dispatched,
	Failed,
	DeadLettered,
}
