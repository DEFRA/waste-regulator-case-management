using MongoDB.Bson.Serialization.Attributes;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Frozen copy of a <see cref="CaseModuleConfig"/>'s template (states,
/// transitions and version) captured when a case is first submitted.
/// Stored alongside the case so that — even if the live case type's
/// configuration evolves later — the case and its audit history continue to
/// render with the same action set and template version they were assessed
/// against.
///
/// Snapshots are taken eagerly at submission rather than lazily on read so
/// that the frozen view survives the case type being removed or its
/// state machine changing.
///
/// By design a frozen snapshot can contain fields the live model has since
/// removed, so it ignores extra BSON elements: a snapshot persisted under an
/// older template must still deserialise when the worklist is queried rather
/// than throwing a <see cref="System.FormatException"/> for the whole batch.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class CaseTemplateSnapshot : ICaseTemplate
{
	public required string TemplateVersion { get; init; }

	public required IReadOnlyCollection<CaseState> States { get; init; }

	public required IReadOnlyCollection<CaseTransition> Transitions { get; init; }

	/// <summary>
	/// Build a snapshot from a live <see cref="CaseModuleConfig"/>, so the
	/// snapshot is self-contained and does not need to call the live type
	/// again later.
	/// </summary>
	public static CaseTemplateSnapshot Capture(CaseModuleConfig type)
	{
		ArgumentNullException.ThrowIfNull(type);

		return new CaseTemplateSnapshot
		{
			TemplateVersion = type.TemplateVersion,
			States = type.States.ToList(),
			Transitions = type.Transitions.ToList()
		};
	}
}
