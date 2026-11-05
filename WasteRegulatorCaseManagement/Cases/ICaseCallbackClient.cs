using MongoDB.Bson;

namespace WasteRegulatorCaseManagement.Cases;

public interface ICaseCallbackClient
{
    /// <summary>
    /// Sends <paramref name="payload"/> to the case's source system and
    /// returns the failure reason, or <c>null</c> when the source system
    /// has applied it.
    /// </summary>
    Task<string?> SendAsync(
        string caseType,
        string callbackUrl,
        BsonDocument payload,
        CancellationToken cancellationToken
    );
}
