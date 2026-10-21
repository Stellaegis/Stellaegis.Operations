using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Operations.PostgreSQL;

/// <summary>
/// The database of the module on PostgreSQL: the schema <c>ops</c>, its scripts embedded in this package, which
/// <see cref="DatabaseLifecycle.InstallAsync"/> applies and <see cref="DatabaseLifecycle.VerifyAsync"/> checks at
/// startup. Also the marker of its unit of work.
/// </summary>
public sealed class OperationsDatabase
{
    /// <summary>The schema.</summary>
    public const string Schema = "ops";

    /// <summary>The declaration of the database, with its scripts.</summary>
    public static ModuleDatabase Value { get; } = ModuleDatabase.PostgreSql<OperationsModule>(Schema)
        .WithScripts(SqlScripts.FromResources(typeof(OperationsDatabase).Assembly, "Stellaegis.Operations.PostgreSQL.Sql.PostgreSql"));

    private OperationsDatabase()
    {
    }
}

/// <summary>
/// The options of the PostgreSQL store, bound from <c>Stellaegis:Operations:PostgreSQL</c>: the connection string
/// the application connects under, with its own role.
/// <code>
/// Stellaegis:Operations:PostgreSQL:ConnectionString = Host=db;Database=primary;Username=app;Password=…
/// </code>
/// </summary>
public sealed class OperationsDatabaseOptions : IValidatableOptions
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "Stellaegis:Operations:PostgreSQL";

    /// <summary>The connection string of the application's role.</summary>
    public string ConnectionString { get; init; } = string.Empty;

    /// <inheritdoc/>
    public string? FindError()
        => string.IsNullOrWhiteSpace(ConnectionString) ? "ConnectionString is required." : null;
}
