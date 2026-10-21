using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stellaegis.Operations.InMemory;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Operations;

/// <summary>Registers the handlers the application runs its jobs with.</summary>
public sealed class JobHandlers
{
    private readonly IServiceCollection _services;

    internal JobHandlers(IServiceCollection services) => _services = services;

    /// <summary>Registers a handler, resolved in the scope of each job it runs.</summary>
    public JobHandlers Add<THandler>() where THandler : class, IJobHandler
    {
        _services.TryAddEnumerable(ServiceDescriptor.Scoped<IJobHandler, THandler>());
        return this;
    }

    /// <summary>Registers every concrete handler of the assembly holding <typeparamref name="TMarker"/>.</summary>
    public JobHandlers AddFromAssemblyOf<TMarker>() => AddFromAssembly(typeof(TMarker).Assembly);

    /// <summary>Registers every concrete handler of <paramref name="assembly"/>.</summary>
    public JobHandlers AddFromAssembly(Assembly assembly)
    {
        foreach (var type in Guard.NotNull(assembly).GetTypes())
        {
            if (type is { IsClass: true, IsAbstract: false } && typeof(IJobHandler).IsAssignableFrom(type))
                _services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IJobHandler), type));
        }
        return this;
    }
}

/// <summary>The registration of the module.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the module with its in-memory stores, for development and tests: the service, the two workers,
    /// the options bound from <c>Stellaegis:Operations</c>, and the handlers <paramref name="handlers"/> adds.
    /// Declares <see cref="OperationsModule"/> to the application, with its operations.
    /// </summary>
    public static IServiceCollection AddStellaegisOperationsInMemory(
        this IServiceCollection services, IConfiguration configuration, Action<JobHandlers>? handlers = null)
    {
        services.TryAddSingleton<IJobStore, InMemoryJobStore>();
        services.TryAddSingleton<IScheduleStore, InMemoryScheduleStore>();
        return services.AddStellaegisOperationsServices(configuration, handlers);
    }

    /// <summary>
    /// Registers the service, the workers and the options, and declares the module. Called by the registration
    /// of each store, once; a host calls one of them.
    /// </summary>
    public static IServiceCollection AddStellaegisOperationsServices(
        this IServiceCollection services, IConfiguration configuration, Action<JobHandlers>? handlers = null)
    {
        Guard.NotNull(services);
        Guard.NotNull(configuration);

        services.AddStellaegisModule(ModuleDeclaration.Compartmented<OperationsModule>()
            .AcceptingSharedSpace()
            .WithOperations(JobOperations.All)
            // Compatible with an isolation by owner by nature: a job is a queue entry, owned by nobody; who may see
            // it among the users of its space is its visibility, read by the application's handler.
            .WithCapability(ModuleCapability.OwnerIsolation));
        services.AddStellaegisOptions<OperationsOptions>(configuration, OperationsOptions.SectionName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<JobTypeCatalog>(provider =>
        {
            using var scope = provider.CreateScope();
            return new JobTypeCatalog(scope.ServiceProvider.GetServices<IJobHandler>());
        });
        services.TryAddScoped<IJobService, JobService>();
        services.TryAddSingleton<JobWorker>();
        services.TryAddSingleton<ScheduleWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<JobWorker>());
        services.AddHostedService(provider => provider.GetRequiredService<ScheduleWorker>());
        handlers?.Invoke(new JobHandlers(services));
        return services;
    }
}
