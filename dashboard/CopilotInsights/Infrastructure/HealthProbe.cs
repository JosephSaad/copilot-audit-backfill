using Dapper;
using Microsoft.Data.SqlClient;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// /healthz, for a load balancer: 200 when this web server answers and reaches its database, 503 when it cannot. No sign-in,
/// and nothing in the answer but that: no data, no names, no server or database names, no error text. Under IIS with
/// Windows authentication, the installer lets this one address answer without sign-in. Admin › Health stays the page for
/// people.
/// </summary>
public static class HealthProbe
{
    public const string Path = "/healthz";

    public static async Task<IResult> AnswerAsync(TenantAccessor tenants, ILoggerFactory logs)
    {
        try
        {
            /* Each tenant's database, at most five seconds each. */
            foreach (var t in tenants.All)
            {
                await using var c = new SqlConnection(t.ConnectionString);
                await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1;", commandTimeout: 5));
            }
            return Results.Text("ok", "text/plain");
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
        {
            Incident.Note(logs.CreateLogger(typeof(HealthProbe)), ex, "The health probe could not reach a database");
            return Results.Text("unavailable", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
