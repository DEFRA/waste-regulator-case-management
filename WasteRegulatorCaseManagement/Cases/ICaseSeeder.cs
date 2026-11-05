namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Optional, module-owned hook for populating the case collection with
/// example data on a fresh deployment. The framework runs every registered
/// seeder once at startup, but only when the persisted case collection
/// is empty — so seed data never clobbers a real environment.
///
/// Modules opt in by registering an implementation of this interface from
/// their <see cref="ICaseModule.RegisterServices(IServiceCollection, IConfiguration)"/>.
/// </summary>
public interface ICaseSeeder
{
    /// <summary>
    /// The <see cref="ICaseType.TypeId"/> the seeder belongs to. Used by
    /// the framework to look up the live type so the snapshot/version stamp
    /// on each seeded item matches what an in-flight item would carry.
    /// </summary>
    string TypeId { get; }

    /// <summary>
    /// Build the seed cases. Called from the seeding hosted service with
    /// the live <see cref="ICaseType"/> for <see cref="TypeId"/> and the
    /// application's <see cref="TimeProvider"/> so timestamps are consistent
    /// across seeders.
    /// </summary>
    IEnumerable<Case> Build(ICaseType type, TimeProvider time);
}