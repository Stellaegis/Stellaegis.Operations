using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Operations.PostgreSQL;

/// <summary>The registration of the module on PostgreSQL.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the module with its PostgreSQL stores: the data source of the application's role, from
    /// <c>Stellaegis:Operations:PostgreSQL</c>; the unit of work in a space; the database for the verification at
    /// startup; then everything <c>AddStellaegisOperationsServices</c> registers, with the handlers
    /// <paramref name="handlers"/> adds.
    /// <code>
    /// builder.Services.AddStellaegisOperationsPostgreSQL(builder.Configuration, handlers => handlers.AddFromAssemblyOf&lt;Program&gt;());
    /// </code>
    /// </summary>
    public static IServiceCollection AddStellaegisOperationsPostgreSQL(
        this IServiceCollection services, IConfiguration configuration, Action<JobHandlers>? handlers = null)
    {
        Guard.NotNull(services);
        Guard.NotNull(configuration);

        services.AddStellaegisOptions<OperationsDatabaseOptions>(configuration, OperationsDatabaseOptions.SectionName);
        services.TryAddSingleton(provider =>
            new OperationsDataSource(NpgsqlDataSource.Create(provider.GetRequiredService<IOptions<OperationsDatabaseOptions>>().Value.ConnectionString)));
        services.TryAddSingleton<ISpaceUnitOfWorkFactory<OperationsDatabase>>(provider =>
            new AdoSpaceUnitOfWorkFactory<OperationsDatabase>(
                provider.GetRequiredService<OperationsDataSource>().Value, OperationsRepositories.Registry(), SessionContext.PostgreSql));
        services.TryAddSingleton<IJobStore, PostgreSqlJobStore>();
        services.TryAddSingleton<IScheduleStore, PostgreSqlScheduleStore>();
        services.AddStellaegisModuleDatabase(OperationsDatabase.Value, OpenAsApplicationAsync);
        return services.AddStellaegisOperationsServices(configuration, handlers);
    }

    private static async Task<System.Data.Common.DbConnection> OpenAsApplicationAsync(IServiceProvider provider, CancellationToken cancellationToken)
        => await provider.GetRequiredService<OperationsDataSource>().Value.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>The data source of the module, under the application's role: one per host, pooled by Npgsql.</summary>
public sealed class OperationsDataSource(NpgsqlDataSource value) : IDisposable, IAsyncDisposable
{
    /// <summary>The data source.</summary>
    public NpgsqlDataSource Value { get; } = Guard.NotNull(value);

    /// <inheritdoc/>
    public void Dispose() => Value.Dispose();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => Value.DisposeAsync();
}
