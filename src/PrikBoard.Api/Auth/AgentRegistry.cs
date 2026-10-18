using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Auth;

/// <summary>
/// Maps a token's client (issuer + client id) to its <see cref="Agent"/> principal, creating the agent on first sight.
/// An agent sees no board until an owner adds it, so registering one grants nothing by itself.
/// </summary>
public static class AgentRegistry
{
    /// <summary>The client id in a machine token: azp (Auth0, Entra), client_id (Okta, Keycloak), appid (Entra v1), else the subject.</summary>
    public static string ClientId(ClaimsPrincipal principal) =>
        principal.FindFirstValue("azp")
        ?? principal.FindFirstValue("client_id")
        ?? principal.FindFirstValue("appid")
        ?? principal.FindFirstValue("sub")
        ?? "unknown";

    public static async Task<Guid> ResolveAsync(IServiceProvider services, string issuer, string clientId, CancellationToken ct)
    {
        var cache = services.GetRequiredService<IMemoryCache>();
        var key = ("agent", issuer, clientId);
        if (cache.TryGetValue(key, out Guid cached)) return cached;

        var db = services.GetRequiredService<PrikBoardDbContext>();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Issuer == issuer && a.ClientId == clientId, ct);
        if (agent is null)
        {
            agent = new Agent { Issuer = issuer, ClientId = clientId };
            db.Agents.Add(agent);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another request registered the same client at the same moment.
                db.ChangeTracker.Clear();
                agent = await db.Agents.FirstAsync(a => a.Issuer == issuer && a.ClientId == clientId, ct);
            }
        }
        else if (agent.LastSeenAt < DateTimeOffset.UtcNow.AddHours(-1))
        {
            agent.LastSeenAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        cache.Set(key, agent.Id, TimeSpan.FromMinutes(10));
        return agent.Id;
    }
}
