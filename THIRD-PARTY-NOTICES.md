# Third-party notices

Stellaegis.Operations is licensed under the MIT License (see [LICENSE](LICENSE)).

Its packages contain no third-party code. They reference the packages below, which NuGet installs
alongside them, each under its own license.

| Component | Used by | License (SPDX) | Project |
| --- | --- | --- | --- |
| Stellaegis.Primitives | all packages | MIT | <https://github.com/Stellaegis/Stellaegis.Primitives> |
| Stellaegis.Primitives.Hosting | all packages | MIT | <https://github.com/Stellaegis/Stellaegis.Primitives> |
| Stellaegis.Primitives.Data | `Stellaegis.Operations.PostgreSQL` | MIT | <https://github.com/Stellaegis/Stellaegis.Primitives> |
| Stellaegis.Primitives.AspNetCore | `Stellaegis.Operations.AspNetCore` | MIT | <https://github.com/Stellaegis/Stellaegis.Primitives> |
| Cronos | all packages | MIT | <https://github.com/HangfireIO/Cronos> |
| Dapper | `Stellaegis.Operations.PostgreSQL` | Apache-2.0 | <https://github.com/DapperLib/Dapper> |
| Npgsql | `Stellaegis.Operations.PostgreSQL` | PostgreSQL | <https://github.com/npgsql/npgsql> |
| Microsoft.Extensions.\* | all packages | MIT | <https://github.com/dotnet> |

`Stellaegis.Operations.AspNetCore` also references the ASP.NET Core shared framework (MIT,
<https://github.com/dotnet/aspnetcore>), which the .NET runtime provides rather than NuGet. The other packages
do not use ASP.NET Core.

Test-only dependencies (xUnit, ASP.NET Core TestHost) are not part of any package and are not listed here.
