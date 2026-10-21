using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Operations.Tests;

/// <summary>What the module declares to the application, read against its frame.</summary>
public sealed class ModuleDeclarationTests
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder().Build();

    private static ServiceProvider Build(ApplicationFrame frame)
    {
        var services = new ServiceCollection().AddLogging().AddStellaegisFrame(frame);
        services.AddStellaegisOperationsInMemory(Configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_module_is_compartmented_accepts_the_shared_space_and_declares_its_operations()
    {
        using var provider = Build(ApplicationFrame.ManyCompartments().WithSharedSpace());

        var module = Assert.Single(provider.GetStellaegisModules());
        Assert.Equal(OperationsModule.Name, module.Name);
        Assert.True(module.IsCompartmented);
        Assert.True(module.AcceptsSharedSpace);
        Assert.Equal(JobOperations.All, module.Operations);
        provider.ValidateStellaegisModules();
    }

    [Fact]
    public void An_application_without_compartments_cannot_use_the_module()
    {
        using var provider = Build(ApplicationFrame.WithoutCompartments);

        Assert.Throws<InvalidOperationException>(provider.ValidateStellaegisModules);
    }

    [Fact]
    public void An_isolation_by_owner_is_compatible_whatever_the_store()
    {
        using var provider = Build(ApplicationFrame.ManyCompartments().WithOwnerIsolation());

        provider.ValidateStellaegisModules();
    }

    [Fact]
    public void The_options_are_validated()
    {
        Assert.Null(new OperationsOptions().FindError());
        Assert.NotNull(new OperationsOptions { MaxConcurrentJobs = 0 }.FindError());
        Assert.NotNull(new OperationsOptions { ClaimLease = TimeSpan.Zero }.FindError());
    }
}
