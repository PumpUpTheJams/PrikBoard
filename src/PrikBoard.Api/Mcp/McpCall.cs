using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Live;

namespace PrikBoard.Api.Mcp;

/// <summary>
/// What every MCP tool needs from the request: the same board access check, validation and live updates as the REST
/// endpoints, with failures turned into tool errors (<see cref="McpException"/>) the model can read and act on.
/// </summary>
internal static class McpCall
{
    /// <summary>camelCase and enums as strings, like the REST API.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static HttpContext Http(IHttpContextAccessor accessor) =>
        accessor.HttpContext ?? throw new InvalidOperationException("MCP tools run inside an HTTP request.");

    /// <summary>
    /// Checks the caller's role (and, for writes, the token's scope) on the board an id belongs to, and returns the board
    /// id. The board becomes the one the caller last used (<see cref="LastBoard"/>).
    /// </summary>
    public static Task<Guid> AuthorizeAsync(HttpContext http, BoardScope scope, Guid id, BoardRole minimum) =>
        CheckedAsync(http, minimum, () => BoardAccess.AuthorizeAsync(http, scope, id, minimum));

    private static async Task<Guid> CheckedAsync(HttpContext http, BoardRole minimum, Func<Task<BoardAccess.Authorization>> authorize)
    {
        if (minimum >= BoardRole.Editor && !ApiScopes.Has(http.User, ApiScopes.Write))
        {
            throw new McpException("This token is read-only (boards:read), so it cannot change anything.");
        }
        var access = await authorize();
        if (access.Failure is not null) throw Error(access.Failure);
        await LastBoard.RememberAsync(http, access.BoardId);
        return access.BoardId;
    }

    /// <summary>Like the overload with an id, for the board with this short id, found in the same query as the role.</summary>
    public static Task<Guid> AuthorizeAsync(HttpContext http, string shortId, BoardRole minimum) =>
        CheckedAsync(http, minimum, () => BoardAccess.AuthorizeAsync(http, shortId, minimum));

    /// <summary>The value of a successful handler result; anything else becomes a tool error.</summary>
    public static T Value<T>(IResult result) =>
        Unwrap(result) is IValueHttpResult<T> { Value: { } value } ? value : throw Error(result);

    /// <summary>Throws unless the handler result is a success.</summary>
    public static void Succeeded(IResult result)
    {
        if (Unwrap(result) is not IStatusCodeHttpResult { StatusCode: >= 200 and < 300 }) throw Error(result);
    }

    /// <summary>
    /// Applies the DataAnnotations on a request record, and on the items in its lists (such as a component's checklist items),
    /// as the REST API does.
    /// </summary>
    public static void Validate(object request)
    {
        var problems = new List<ValidationResult>();
        Collect(request, problems, depth: 3);
        if (problems.Count > 0) throw new McpException(string.Join(" ", problems.Select(p => p.ErrorMessage)));
    }

    private static void Collect(object value, List<ValidationResult> problems, int depth)
    {
        Validator.TryValidateObject(value, new ValidationContext(value), problems, validateAllProperties: true);
        if (depth == 0) return;
        foreach (var list in value.GetType().GetProperties().Select(p => p.GetValue(value)).OfType<IEnumerable>().Where(v => v is not string))
        {
            foreach (var item in list.Cast<object?>().Where(i => i is not null && i is not string && !i.GetType().IsPrimitive && i is not Guid))
            {
                Collect(item!, problems, depth - 1);
            }
        }
    }

    /// <summary>
    /// Tells people watching the board (live updates) to reload, as the REST endpoints do after a change, and marks the
    /// call as a write on that board, so <see cref="RulesReminder"/> reminds the agent of the board's rules.
    /// </summary>
    public static Task AnnounceAsync(HttpContext http, Guid boardId)
    {
        http.Items[ChangedBoardItem] = boardId;
        return http.RequestServices.GetRequiredService<BoardEvents>().ChangedAsync(boardId, http);
    }

    /// <summary>
    /// A call-tool filter: after a successful write on a board with agent rules, adds a short reminder with the time the
    /// rules last changed, so the agent keeps following them and notices when they change. The rules themselves are not
    /// repeated; get_board and get_board_rules return them.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> RulesReminder(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) => async (context, ct) =>
    {
        var result = await next(context, ct);
        var http = context.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
        if (result.IsError == true || http?.Items[ChangedBoardItem] is not Guid boardId) return result;

        if (await RulesUpdatedAtAsync(http, boardId, ct) is { } updatedAt)
        {
            result.Content.Add(new TextContentBlock
            {
                Text = $"This board has agent rules (last changed {updatedAt:yyyy-MM-dd HH:mm:ss}Z). Follow them in every action. " +
                       "The rules you got from get_board or get_board_rules are current unless their rulesUpdatedAt is older than this.",
            });
        }
        return result;
    };

    private const string ChangedBoardItem = "PrikBoard.Mcp.ChangedBoard";

    private sealed record RulesStamp(DateTimeOffset? UpdatedAt);

    private static string RulesKey(Guid boardId) => "mcp-rules:" + boardId;

    /// <summary>
    /// When the board's rules last changed, or null when it has none. Cached for a minute, as every write asks;
    /// <see cref="ForgetRules"/> drops it when the rules change (on this instance only).
    /// </summary>
    private static async Task<DateTimeOffset?> RulesUpdatedAtAsync(HttpContext http, Guid boardId, CancellationToken ct)
    {
        var cache = http.RequestServices.GetRequiredService<IMemoryCache>();
        if (cache.TryGetValue(RulesKey(boardId), out RulesStamp? stamp) && stamp is not null) return stamp.UpdatedAt;
        var db = http.RequestServices.GetRequiredService<PrikBoardDbContext>();
        var changed = await db.Boards.Where(b => b.Id == boardId && b.Rules != null).Select(b => b.RulesUpdatedAt).ToListAsync(ct);
        var updatedAt = changed is [{ } at] ? at : (DateTimeOffset?)null;
        cache.Set(RulesKey(boardId), new RulesStamp(updatedAt), TimeSpan.FromMinutes(1));
        return updatedAt;
    }

    /// <summary>Drops what the MCP code caches about a board's rules (here and in <see cref="LastBoard"/>), after they change.</summary>
    public static void ForgetRules(IMemoryCache cache, Guid boardId)
    {
        cache.Remove(RulesKey(boardId));
        LastBoard.Forget(cache, boardId);
    }

    private static IResult Unwrap(IResult result) => result is INestedHttpResult nested ? nested.Result : result;

    private static McpException Error(IResult result) => Unwrap(result) switch
    {
        NotFound => new McpException("Not found, or you have no access to its board."),
        ValidationProblem validation => new McpException(string.Join(" ",
            validation.ProblemDetails.Errors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}"))),
        ProblemHttpResult problem => new McpException(problem.ProblemDetails.Detail ?? problem.ProblemDetails.Title ?? "The request failed."),
        _ => new McpException("The request failed."),
    };
}
