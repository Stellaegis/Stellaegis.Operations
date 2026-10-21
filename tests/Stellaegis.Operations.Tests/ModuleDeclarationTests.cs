using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stellaegis.Operations.PostgreSQL;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Operations.Tests;

/// <summary>What the module and its stores declare to the application, read against its frame.</summary>
public sealed class ModuleDeclarationTests
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Stellaegis:Operations:PostgreSQL:ConnectionString"] = "Host=nowhere" })
        .Build();

    private static ServiceProvider Build(ApplicationFrame frame, Action<IServiceCollection> addStore)
    {
        var services = new ServiceCollection().AddLogging().AddStellaegisFrame(frame);
        addStore(services);
        return services.BuildServiceProvider();
    }

    private static void InMemory(IServiceCollection services) => services.AddStellaegisOperationsInMemory(Configuration);

    private static void PostgreSql(IServiceCollection services) => services.AddStellaegisOperationsPostgreSQL(Configuration);

    [Fact]
    public void The_module_is_compartmented_accepts_the_shared_space_and_declares_its_operations_whatever_the_store()
    {
        foreach (var addStore in new Action<IServiceCollection>[] { InMemory, PostgreSql })
        {
            using var provider = Build(ApplicationFrame.ManyCompartments().WithSharedSpace(), addStore);

            var module = Assert.Single(provider.GetStellaegisModules());
            Assert.Equal(OperationsModule.Name, module.Name);
            Assert.True(module.IsCompartmented);
            Assert.True(module.AcceptsSharedSpace);
            Assert.Equal(JobOperations.All, module.Operations);
            provider.ValidateStellaegisModules();
        }
    }

    [Fact]
    public void An_application_without_compartments_cannot_use_the_module()
    {
        using var provider = Build(ApplicationFrame.WithoutCompartments, InMemory);

        Assert.Throws<InvalidOperationException>(provider.ValidateStellaegisModules);
    }

    [Fact]
    public void An_isolation_by_owner_is_compatible_whatever_the_store()
    {
        foreach (var addStore in new Action<IServiceCollection>[] { InMemory, PostgreSql })
        {
            using var provider = Build(ApplicationFrame.ManyCompartments().WithOwnerIsolation(), addStore);

            provider.ValidateStellaegisModules();
        }
    }

    [Fact]
    public void The_postgresql_store_declares_its_database_for_the_verification_at_startup()
    {
        using var provider = Build(ApplicationFrame.ManyCompartments(), PostgreSql);

        var database = Assert.Single(provider.GetStellaegisModuleDatabases());
        Assert.Equal(OperationsDatabase.Schema, database.Schema);
        Assert.Equal(typeof(OperationsModule), database.Module);
        Assert.Equal("0001_initial.sql", Assert.Single(database.Scripts).FileName);
    }

    [Fact]
    public void The_options_are_validated()
    {
        Assert.Null(new OperationsOptions().FindError());
        Assert.NotNull(new OperationsOptions { MaxConcurrentJobs = 0 }.FindError());
        Assert.NotNull(new OperationsOptions { ClaimLease = TimeSpan.Zero }.FindError());
        Assert.NotNull(new OperationsDatabaseOptions().FindError());
    }
}
