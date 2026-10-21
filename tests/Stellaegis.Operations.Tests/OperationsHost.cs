using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stellaegis.Operations.AspNetCore;
using Stellaegis.Primitives;
using Stellaegis.Primitives.AspNetCore;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Operations.Tests;

/// <summary>
/// An application on a test server, with the module in memory: the routes of a compartment under
/// <c>/api/t/{compartment}</c>, of the shared space under <c>/api/shared</c>, of the administration under
/// <c>/api/admin</c>. The actor is the <c>X-Actor</c> header; a job is visible to its initiator, or to every
/// user of its space when its visibility says so; every schedule is visible.
/// </summary>
public sealed class OperationsHost : IAsyncDisposable
{
    public const string ActorHeader = "X-Actor";

    private OperationsHost(WebApplication app, TestClock clock)
    {
        App = app;
        Clock = clock;
    }

    public WebApplication App { get; }
    public TestClock Clock { get; }

    public static async Task<OperationsHost> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stellaegis:Operations:PollingInterval"] = "01:00:00",
            ["Stellaegis:Operations:SchedulePollingInterval"] = "01:00:00",
        });
        var clock = new TestClock();
        builder.Services
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<Gate>()
            .AddStellaegisFrame(PostgreSqlFixture.Frame)
            .AddStellaegisRequestContext(context => context
                .CompartmentFromRoute("compartment")
                .ActorFrom(http => Actor.TryParse(http.Request.Headers[ActorHeader].ToString(), out var actor) ? actor : null))
            .AddAuthorization()
            .AddSingleton<IAuthorizationHandler, VisibilityRule>()
            .AddSingleton<IAuthorizationHandler, AnyScheduleRule>()
            .AddStellaegisOperationsInMemory(builder.Configuration, handlers => handlers.AddFromAssemblyOf<OperationsHost>());

        var app = builder.Build();
        app.MapGroup("/api/t/{compartment}").MapOperationsEndpoints().AllowAnonymous();
        app.MapGroup("/api/shared").MapOperationsEndpoints().InSharedSpace().AllowAnonymous();
        app.MapGroup("/api/admin").MapOperationsAdminEndpoints().AllowAnonymous();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new OperationsHost(app, clock);
    }

    public HttpClient Client(string actor)
    {
        var client = App.GetTestClient();
        client.DefaultRequestHeaders.Add(ActorHeader, actor);
        return client;
    }

    public JobWorker Worker => App.Services.GetRequiredService<JobWorker>();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<T>(Json, TestContext.Current.CancellationToken))!;

    public ValueTask DisposeAsync() => App.DisposeAsync();

    /// <summary>A job is visible to its initiator, and to everybody of its space when its visibility says so.</summary>
    private sealed class VisibilityRule(IHttpContextAccessor accessor) : AuthorizationHandler<OperationRequirement, JobRecord>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OperationRequirement requirement, JobRecord resource)
        {
            var actor = accessor.HttpContext!.Request.Headers[ActorHeader].ToString();
            if (resource.Visibility == JobVisibility.Space || resource.Actor.Value == actor)
                context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class AnyScheduleRule : AuthorizationHandler<OperationRequirement, Schedule>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OperationRequirement requirement, Schedule resource)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }
}
