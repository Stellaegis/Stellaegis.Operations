using Stellaegis.Operations.PostgreSQL;
using Stellaegis.Primitives.Data;

namespace Stellaegis.Operations.Tests;

/// <summary>The installation of the database, and its verification under the application's role.</summary>
public sealed class DatabaseLifecycleTests(PostgreSqlFixture postgreSql) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task The_installed_database_is_verified_under_the_application_role_and_installs_again_without_change()
    {
        Assert.SkipWhen(postgreSql.Unavailable is not null, postgreSql.Unavailable ?? string.Empty);

        await DatabaseLifecycle.VerifyAsync([OperationsDatabase.Value], PostgreSqlFixture.Frame, postgreSql.OpenAppAsync);

        var again = await DatabaseLifecycle.InstallAsync([OperationsDatabase.Value], PostgreSqlFixture.Frame, postgreSql.OpenAdminAsync, "stellaegis_operations_app");
        Assert.Empty(again.Applied);
        Assert.Equal(["operations: 0001_initial.sql"], again.Skipped);
    }
}
