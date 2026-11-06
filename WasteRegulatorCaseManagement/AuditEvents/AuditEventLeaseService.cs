using MongoDB.Driver;
using WasteRegulatorCaseManagement.Utils.Mongo;

namespace WasteRegulatorCaseManagement.AuditEvents;

/// <summary>
/// A time-limited lease, one per publishing process, so that only one
/// instance of the service publishes audit events at a time.
/// </summary>
public sealed class AuditEventLeaseService(IMongoDbClientFactory connectionFactory, TimeProvider timeProvider)
{
	private const string OwnerField = "owner";
	private const int DuplicateKeyErrorCode = 11000;

	private readonly IMongoCollection<AuditEventDispatchLease> _leases =
		connectionFactory.GetCollection<AuditEventDispatchLease>(
			AuditEventService.AuditEventDispatchLeaseCollectionName
		);

	private readonly string _instanceId = $"{Environment.MachineName}-{Guid.NewGuid():N}";

	public async Task<bool> TryAcquire(string processName, TimeSpan leaseDuration, CancellationToken cancellationToken)
	{
		var utcNow = AuditEventTime.Now(timeProvider);

		var filter = Builders<AuditEventDispatchLease>.Filter.And(
			Builders<AuditEventDispatchLease>.Filter.Eq(x => x.Id, processName),
			Builders<AuditEventDispatchLease>.Filter.Or(
				Builders<AuditEventDispatchLease>.Filter.Lte(x => x.ExpiresAt, utcNow),
				Builders<AuditEventDispatchLease>.Filter.Eq(x => x.Owner, _instanceId)
			)
		);

		var update = Builders<AuditEventDispatchLease>
			.Update.Set(x => x.Owner, _instanceId)
			.Set(x => x.ExpiresAt, utcNow.Add(leaseDuration))
			.Set(x => x.UpdatedAt, utcNow)
			.SetOnInsert(x => x.Id, processName)
			.SetOnInsert(x => x.CreatedAt, utcNow);

		try
		{
			await _leases.FindOneAndUpdateAsync(
				filter,
				update,
				new FindOneAndUpdateOptions<AuditEventDispatchLease>
				{
					IsUpsert = true,
					ReturnDocument = ReturnDocument.After,
				},
				cancellationToken
			);

			return true;
		}
		catch (MongoCommandException exception) when (exception.Code == DuplicateKeyErrorCode)
		{
			return false;
		}
	}

	public async Task<bool> TryRenew(string processName, TimeSpan leaseDuration, CancellationToken cancellationToken)
	{
		var utcNow = AuditEventTime.Now(timeProvider);

		var result = await _leases.UpdateOneAsync(
			OwnedBy(processName),
			Builders<AuditEventDispatchLease>
				.Update.Set(x => x.ExpiresAt, utcNow.Add(leaseDuration))
				.Set(x => x.UpdatedAt, utcNow),
			cancellationToken: cancellationToken
		);

		return result.MatchedCount == 1;
	}

	public async Task Release(string processName, CancellationToken cancellationToken)
	{
		var utcNow = AuditEventTime.Now(timeProvider);

		await _leases.UpdateOneAsync(
			OwnedBy(processName),
			Builders<AuditEventDispatchLease>
				.Update.Set(x => x.ExpiresAt, utcNow)
				.Set(x => x.UpdatedAt, utcNow)
				.Set(x => x.LastSentAt, utcNow)
				.Unset(OwnerField),
			cancellationToken: cancellationToken
		);
	}

	private FilterDefinition<AuditEventDispatchLease> OwnedBy(string processName) =>
		Builders<AuditEventDispatchLease>.Filter.And(
			Builders<AuditEventDispatchLease>.Filter.Eq(x => x.Id, processName),
			Builders<AuditEventDispatchLease>.Filter.Eq(x => x.Owner, _instanceId)
		);
}
