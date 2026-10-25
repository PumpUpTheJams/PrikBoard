using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace FakeSlack;

/// <summary>A file on a message, as someone uploaded it; served at <c>/files/{id}/{name}</c> to the bot token only, like Slack's <c>url_private</c>.</summary>
public sealed record FakeFile(string Id, string Name, string Mimetype, byte[] Bytes);

/// <summary>A modal the app opened for someone (<c>views.open</c>), until they submit or close it.</summary>
/// <param name="Id">Its id, V… like Slack's.</param>
/// <param name="User">Who it is open for.</param>
/// <param name="View">The view the app sent.</param>
public sealed record FakeView(string Id, string User, JsonNode View)
{
    public bool Closed { get; set; }
}

/// <summary>
/// Save as card's side of the fake workspace: message shortcuts, the modals the app opens for them and their
/// submission, files that need the bot token, reactions and permalinks.
/// </summary>
public sealed partial class FakeWorkspace
{
    private readonly ConcurrentDictionary<string, FakeFile> _files = new();
    private readonly ConcurrentDictionary<string, string> _triggers = new();
    private readonly List<FakeView> _views = [];

    public FakeFile? File(string id) => _files.GetValueOrDefault(id);

    /// <summary>Keeps an uploaded file, for a message to carry.</summary>
    public FakeFile Upload(string name, string mimetype, byte[] bytes)
    {
        var file = new FakeFile("F" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), name, mimetype, bytes);
        _files[file.Id] = file;
        return file;
    }

    public IReadOnlyList<FakeView> Views
    {
        get
        {
            lock (_lock) return [.. _views];
        }
    }

    /// <summary>
    /// Uses a message shortcut on a message, as Slack does when someone picks it under More actions: an
    /// <c>interactive</c> envelope of type <c>message_action</c> with a <c>trigger_id</c> for one modal, and a
    /// <c>response_url</c> back here. False without a connection or an unknown message.
    /// </summary>
    public async Task<bool> ShortcutAsync(string channel, string ts, string user, string callbackId = "save_to_prikboard")
    {
        var message = Messages.FirstOrDefault(m => m.Channel == channel && m.Ts == ts);
        var connection = _connections.Values.FirstOrDefault();
        if (message is null || connection is null || _baseUrl is null) return false;
        var trigger = Guid.NewGuid().ToString("N");
        _triggers[trigger] = user;
        var responseId = Guid.NewGuid().ToString("N");
        _pendingCommands[responseId] = (channel, user, "Save as card");
        var person = Options.Users.FirstOrDefault(u => u.Id == user);
        await connection.SendAsync(new
        {
            envelope_id = Guid.NewGuid().ToString(),
            type = "interactive",
            accepts_response_payload = false,
            payload = new
            {
                type = "message_action",
                callback_id = callbackId,
                trigger_id = trigger,
                team = new { id = Options.TeamId, domain = Options.TeamName.Replace(' ', '-').ToLowerInvariant() },
                channel = new { id = channel, name = Options.Channels.FirstOrDefault(c => c.Id == channel)?.Name ?? channel },
                user = new { id = user, username = person?.Name ?? user, name = person?.Name ?? user, team_id = Options.TeamId },
                message_ts = ts,
                message = new
                {
                    type = "message",
                    user = message.User,
                    ts = message.Ts,
                    text = message.Text,
                    files = message.Files.Select(f => new
                    {
                        id = f.Id, name = f.Name, mimetype = f.Mimetype, size = f.Bytes.Length,
                        url_private = $"{_baseUrl}/files/{f.Id}/{Uri.EscapeDataString(f.Name)}",
                    }).ToList(),
                },
                response_url = $"{_baseUrl}/_fake/responses/{responseId}",
            },
        });
        return true;
    }

    /// <summary>Opens a modal for a trigger, as <c>views.open</c> does; the error Slack would give, or null.</summary>
    public string? OpenView(string triggerId, JsonNode? view)
    {
        if (view is null) return "invalid_arguments";
        // A trigger works once, like Slack's (which also expire after 3 seconds).
        if (!_triggers.TryRemove(triggerId, out var user)) return "expired_trigger_id";
        lock (_lock) _views.Add(new FakeView("V" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), user, view.DeepClone()));
        return null;
    }

    /// <summary>
    /// Submits an open modal with <paramref name="values"/> (block id to what was picked or typed: an option's value for a
    /// select, the text for an input), as Slack sends it: a <c>view_submission</c> with the view's <c>state.values</c>.
    /// </summary>
    public async Task<bool> SubmitAsync(string viewId, IReadOnlyDictionary<string, string> values)
    {
        FakeView? open;
        lock (_lock) open = _views.FirstOrDefault(v => v.Id == viewId && !v.Closed);
        var connection = _connections.Values.FirstOrDefault();
        if (open is null || connection is null) return false;

        var state = new JsonObject();
        foreach (var block in open.View["blocks"]?.AsArray() ?? [])
        {
            if (block?["type"]?.GetValue<string>() != "input" || block["element"] is not JsonObject element) continue;
            var blockId = block["block_id"]?.GetValue<string>() ?? "";
            var actionId = element["action_id"]?.GetValue<string>() ?? "";
            var type = element["type"]?.GetValue<string>() ?? "";
            // What the person left as it was: the initial value or option.
            var picked = values.TryGetValue(blockId, out var given) ? given : null;
            var action = new JsonObject { ["type"] = type };
            if (type == "static_select")
            {
                var options = (element["option_groups"]?.AsArray().SelectMany(g => g?["options"]?.AsArray() ?? []) ?? element["options"]?.AsArray() ?? [])
                    .OfType<JsonNode>();
                var option = picked is null ? element["initial_option"] : options.FirstOrDefault(o => o["value"]?.GetValue<string>() == picked);
                action["selected_option"] = option?.DeepClone();
            }
            else
            {
                action["value"] = picked ?? element["initial_value"]?.GetValue<string>();
            }
            state[blockId] = new JsonObject { [actionId] = action };
        }
        open.Closed = true;
        await connection.SendAsync(new
        {
            envelope_id = Guid.NewGuid().ToString(),
            type = "interactive",
            accepts_response_payload = true,
            payload = new
            {
                type = "view_submission",
                team = new { id = Options.TeamId },
                user = new { id = open.User },
                view = new
                {
                    id = open.Id,
                    type = "modal",
                    callback_id = open.View["callback_id"]?.GetValue<string>(),
                    private_metadata = open.View["private_metadata"]?.GetValue<string>() ?? "",
                    state = new { values = state },
                },
            },
        });
        return true;
    }

    /// <summary>Closes a modal without submitting it (Cancel or Close).</summary>
    public bool CloseView(string viewId)
    {
        lock (_lock)
        {
            if (_views.FirstOrDefault(v => v.Id == viewId) is not { } view) return false;
            view.Closed = true;
            return true;
        }
    }

    /// <summary>Reacts to a message as the bot (<c>reactions.add</c>); the error Slack would give, or null.</summary>
    public string? React(string channel, string ts, string name)
    {
        lock (_lock)
        {
            if (_messages.FirstOrDefault(m => m.Channel == channel && m.Ts == ts) is not { } message) return "message_not_found";
            if (message.Reactions.Contains(name)) return "already_reacted";
            message.Reactions.Add(name);
            return null;
        }
    }

    /// <summary>A message's link, in Slack's form.</summary>
    public string Permalink(string channel, string ts) =>
        $"https://{Options.TeamName.Replace(' ', '-').ToLowerInvariant()}.slack.com/archives/{channel}/p{ts.Replace(".", "")}";
}
