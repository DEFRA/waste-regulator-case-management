using MongoDB.Bson.Serialization.Attributes;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// A possible state of a case. <see cref="IsTerminal"/> marks states from which
/// no further progress is expected (for example "ACCEPTED" or "CANCELLED").
///
/// Embedded verbatim in a frozen <see cref="CaseTemplateSnapshot"/>, so it
/// ignores extra BSON elements: a snapshot persisted under an older template
/// that carried since-removed fields must still deserialise rather than
/// throwing a <see cref="System.FormatException"/> for the whole worklist.
/// </summary>
[BsonIgnoreExtraElements]
public sealed record CaseState(string Id, string DisplayName, bool IsTerminal = false);
