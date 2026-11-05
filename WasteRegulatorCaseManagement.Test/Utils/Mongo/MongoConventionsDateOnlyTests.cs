using WasteRegulatorCaseManagement.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace WasteRegulatorCaseManagement.Test.Utils.Mongo;

/// <summary>
/// RA-176 regression: <see cref="System.DateOnly"/> values must serialise as a
/// plain ISO date string rather than the driver's default BSON DateTime.
/// </summary>
public class MongoConventionsDateOnlyTests
{
    static MongoConventionsDateOnlyTests() => MongoConventions.Register();

    [Fact]
    public void DateOnly_serialises_to_a_bson_string_not_a_datetime()
    {
        var payload = new DatedPayload { StartDate = new DateOnly(2027, 1, 1) };

        var element = payload.ToBsonDocument()["startDate"];

        Assert.Equal(BsonType.String, element.BsonType);
        Assert.Equal("2027-01-01", element.AsString);
    }

    [Fact]
    public void DateOnly_round_trips_from_iso_string()
    {
        var payload = new DatedPayload { StartDate = new DateOnly(2027, 1, 1) };

        var roundTripped = BsonSerializer.Deserialize<DatedPayload>(
            payload.ToBsonDocument());

        Assert.Equal(new DateOnly(2027, 1, 1), roundTripped.StartDate);
    }

    [Fact]
    public void DateOnly_still_reads_legacy_bson_datetime_documents()
    {
        // Documents written before RA-176 stored dates as a BSON DateTime.
        // The string-representation serializer must remain backward-compatible
        // and read those values back as DateOnly.
        var legacy = new BsonDocument
        {
            ["startDate"] =
                new DateTime(2025, 2, 3, 0, 0, 0, DateTimeKind.Utc)
        };

        var roundTripped =
            BsonSerializer.Deserialize<DatedPayload>(legacy);

        Assert.Equal(new DateOnly(2025, 2, 3), roundTripped.StartDate);
    }

    private sealed class DatedPayload
    {
        public DateOnly StartDate { get; set; }
    }
}
