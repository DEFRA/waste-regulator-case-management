using MongoDB.Driver;
using WasteRegulatorCaseManagement.Utils.Mongo;

namespace WasteRegulatorCaseManagement.Cases;

public sealed class CaseModuleConfigPersistence(
    IMongoDbClientFactory connectionFactory,
    ILoggerFactory loggerFactory
) : MongoService<CaseModuleConfig>(connectionFactory, "caseModuleConfig", loggerFactory), ICaseModuleConfigStore
{
    public Task UpsertAsync(CaseModuleConfig config, CancellationToken cancellationToken) =>
        Collection.ReplaceOneAsync(
            c => c.CaseType == config.CaseType,
            config,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken
        );

    public async Task<IReadOnlyList<CaseModuleConfig>> ListAsync(CancellationToken cancellationToken) =>
        await Collection.Find(FilterDefinition<CaseModuleConfig>.Empty).ToListAsync(cancellationToken);

    protected override List<CreateIndexModel<CaseModuleConfig>> DefineIndexes(
        IndexKeysDefinitionBuilder<CaseModuleConfig> builder
    ) => [];
}
