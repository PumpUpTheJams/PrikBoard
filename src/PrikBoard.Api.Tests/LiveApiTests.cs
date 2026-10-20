using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>The live hub at /api/live: board events, presence and drags, over SignalR long polling against the test server.</summary>
public class LiveApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>, IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly List<HubConnection> connections = [];

    [Fact]
    public async Task Members_hear_about_changes_others_save()
    {
        var owner = await SessionAsync("live-owner@example.com");
        var editor = await SessionAsync("live-editor@example.com");
        var board = await owner.Http.CreateBoardAsync();
        await owner.Http.ShareAsync(board.Id, editor.Http, BoardRole.Editor);

        var live = await ConnectAsync(owner);
        var changes = live.Inbox<BoardChange>("BoardChanged");
        var joined = await live.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        Assert.Equal(BoardRole.Owner, joined.Role);

        await editor.Http.CreateNoteAsync(board.Id);

        var change = await changes.NextAsync();
        Assert.Equal(board.Id, change.BoardId);
        Assert.Equal((await editor.Http.MeAsync()).Id, change.By);
    }

    [Fact]
    public async Task Members_hear_about_restores()
    {
        var owner = await SessionAsync("live-restore-owner@example.com");
        var editor = await SessionAsync("live-restore-editor@example.com");
        var board = await owner.Http.CreateBoardAsync();
        await owner.Http.ShareAsync(board.Id, editor.Http, BoardRole.Editor);
        var note = await editor.Http.CreateNoteAsync(board.Id);
        (await editor.Http.DeleteAsync($"/api/notes/{note.Id}")).EnsureSuccessStatusCode();

        var live = await ConnectAsync(owner);
        var changes = live.Inbox<BoardChange>("BoardChanged");
        await live.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        (await editor.Http.PostAsync($"/api/notes/{note.Id}/restore", null)).EnsureSuccessStatusCode();

        Assert.Equal(board.Id, (await changes.NextAsync()).BoardId);
    }

    [Fact]
    public async Task The_tab_that_saved_is_not_told_again_but_the_other_tabs_are()
    {
        var owner = await SessionAsync("live-tabs@example.com");
        var board = await owner.Http.CreateBoardAsync();
        var tab1 = await ConnectAsync(owner);
        var tab2 = await ConnectAsync(owner);
        var inbox1 = tab1.Inbox<BoardChange>("BoardChanged");
        var inbox2 = tab2.Inbox<BoardChange>("BoardChanged");
        await tab1.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        await tab2.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/boards/{board.Id}/notes") { Content = JsonContent.Create(new { title = "From tab 1" }) };
        request.Headers.Add("X-PrikBoard-Live", tab1.Connection.ConnectionId);
        Assert.Equal(HttpStatusCode.Created, (await owner.Http.SendAsync(request)).StatusCode);

        await inbox2.NextAsync();
        Assert.False(await inbox1.AnyWithinAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task Someone_elses_connection_id_does_not_silence_it()
    {
        var owner = await SessionAsync("live-silence-owner@example.com");
        var editor = await SessionAsync("live-silence-editor@example.com");
        var board = await owner.Http.CreateBoardAsync();
        await owner.Http.ShareAsync(board.Id, editor.Http, BoardRole.Editor);
        var live = await ConnectAsync(owner);
        var changes = live.Inbox<BoardChange>("BoardChanged");
        await live.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/boards/{board.Id}/notes") { Content = JsonContent.Create(new { title = "Sneaky" }) };
        request.Headers.Add("X-PrikBoard-Live", live.Connection.ConnectionId);
        await editor.Http.SendAsync(request);

        await changes.NextAsync();
    }

    [Fact]
    public async Task Reads_and_lookups_send_nothing()
    {
        var owner = await SessionAsync("live-quiet@example.com");
        var board = await owner.Http.CreateBoardAsync();
        var watcher = await SessionAsync("live-quiet-watcher@example.com");
        await owner.Http.ShareAsync(board.Id, watcher.Http, BoardRole.Viewer);
        var live = await ConnectAsync(watcher);
        var changes = live.Inbox<BoardChange>("BoardChanged");
        await live.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);

        await owner.Http.GetAsync($"/api/boards/{board.Id}");
        await owner.Http.PostAsJsonAsync($"/api/boards/{board.Id}/members/lookup", new { email = "nobody@example.com" });

        Assert.False(await changes.AnyWithinAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task Only_members_can_join_a_board()
    {
        var owner = await SessionAsync("live-private@example.com");
        var stranger = await SessionAsync("live-stranger@example.com");
        var board = await owner.Http.CreateBoardAsync();

        var live = await ConnectAsync(stranger);
        await Assert.ThrowsAsync<HubException>(() => live.Connection.InvokeAsync<JoinedBoard>("Join", board.Id));
        await Assert.ThrowsAsync<HubException>(() => live.Connection.InvokeAsync<JoinedBoard>("Join", Guid.NewGuid()));
    }

    [Fact]
    public async Task Viewers_see_who_is_on_the_board()
    {
        var owner = await SessionAsync("live-presence-owner@example.com");
        var viewer = await SessionAsync("live-presence-viewer@example.com");
        var board = await owner.Http.CreateBoardAsync();
        await owner.Http.ShareAsync(board.Id, viewer.Http, BoardRole.Viewer);
        await viewer.Http.PatchAsJsonAsync("/api/me", new { displayName = "Vera" });

        var ownerLive = await ConnectAsync(owner);
        var presence = ownerLive.Inbox<BoardPresence>("Presence");
        await ownerLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);

        var viewerLive = await ConnectAsync(viewer);
        var joined = await viewerLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        Assert.Equal(2, joined.Viewers.Count);

        var update = await presence.NextAsync();
        Assert.Contains(update.Viewers, v => v.DisplayName == "Vera" && v.Kind == PrincipalKind.User);

        // A second tab of the same person is listed once.
        var secondTab = await ConnectAsync(viewer);
        Assert.Equal(2, (await secondTab.Connection.InvokeAsync<JoinedBoard>("Join", board.Id)).Viewers.Count);

        await viewerLive.Connection.StopAsync();
        await secondTab.Connection.StopAsync();
        var left = await presence.NextMatchingAsync(p => p.Viewers.Count == 1);
        Assert.DoesNotContain(left.Viewers, v => v.DisplayName == "Vera");
    }

    [Fact]
    public async Task Editors_drags_reach_the_others_and_viewers_cannot_drag()
    {
        var owner = await SessionAsync("live-drag-owner@example.com");
        var viewer = await SessionAsync("live-drag-viewer@example.com");
        var board = await owner.Http.CreateBoardAsync();
        var note = await owner.Http.CreateNoteAsync(board.Id);
        await owner.Http.ShareAsync(board.Id, viewer.Http, BoardRole.Viewer);

        var ownerLive = await ConnectAsync(owner);
        var viewerLive = await ConnectAsync(viewer);
        var ownerDrags = ownerLive.Inbox<RemoteDrag>("Dragging");
        var viewerDrags = viewerLive.Inbox<RemoteDrag>("Dragging");
        var ended = viewerLive.Inbox<Guid>("DragEnded");
        var joined = await ownerLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        await viewerLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);

        await ownerLive.Connection.InvokeAsync("Drag", new DragUpdate(DragKind.Note, note.Id, 42, -7));
        var drag = await viewerDrags.NextAsync();
        Assert.Equal((DragKind.Note, note.Id, 42d, -7d, joined.Key), (drag.Kind, drag.Id, drag.Dx, drag.Dy, drag.Key));

        // A resize names its corner or edge; past the hub's throttle, so it isn't dropped.
        await Task.Delay(60);
        var bucketId = board.Buckets[0].Id;
        await ownerLive.Connection.InvokeAsync("Drag", new DragUpdate(DragKind.Resize, bucketId, -20, 40, ResizeHandle.TopLeft));
        var resize = await viewerDrags.NextAsync();
        Assert.Equal((DragKind.Resize, bucketId, ResizeHandle.TopLeft), (resize.Kind, resize.Id, resize.Handle));

        // Several selected items moving together list them all; a group of none is no drag.
        await Task.Delay(60);
        await ownerLive.Connection.InvokeAsync("Drag", new DragUpdate(DragKind.Group, note.Id, 20, 20, Items: [note.Id, bucketId]));
        var group = await viewerDrags.NextAsync();
        Assert.Equal((DragKind.Group, note.Id), (group.Kind, group.Id));
        Assert.Equal([note.Id, bucketId], group.Items);
        var empty = await Assert.ThrowsAsync<HubException>(() =>
            ownerLive.Connection.InvokeAsync("Drag", new DragUpdate(DragKind.Group, note.Id, 20, 20, Items: [])));
        Assert.Contains("Group", empty.Message);

        await ownerLive.Connection.InvokeAsync("DragEnd");
        Assert.Equal(joined.Key, await ended.NextAsync());
        Assert.False(await ownerDrags.AnyWithinAsync(TimeSpan.FromMilliseconds(200)));

        var refused = await Assert.ThrowsAsync<HubException>(() =>
            viewerLive.Connection.InvokeAsync("Drag", new DragUpdate(DragKind.Note, note.Id, 1, 1)));
        Assert.Contains("Editor", refused.Message);
    }

    [Fact]
    public async Task Removed_members_stop_hearing_about_the_board()
    {
        var owner = await SessionAsync("live-remove-owner@example.com");
        var editor = await SessionAsync("live-remove-editor@example.com");
        var board = await owner.Http.CreateBoardAsync();
        await owner.Http.ShareAsync(board.Id, editor.Http, BoardRole.Editor);
        var editorId = (await editor.Http.MeAsync()).Id;

        var live = await ConnectAsync(editor);
        var changes = live.Inbox<BoardChange>("BoardChanged");
        var access = live.Inbox<Guid>("AccessChanged");
        var lists = live.Inbox<object?>("BoardsChanged");
        await live.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);

        await owner.Http.DeleteAsync($"/api/boards/{board.Id}/members/{editorId}");
        Assert.Equal(board.Id, await access.NextAsync());
        await lists.NextAsync();
        await changes.DrainAsync();

        await owner.Http.CreateNoteAsync(board.Id);
        Assert.False(await changes.AnyWithinAsync(TimeSpan.FromMilliseconds(500)));
        await Assert.ThrowsAsync<HubException>(() => live.Connection.InvokeAsync<JoinedBoard>("Join", board.Id));
    }

    [Fact]
    public async Task Role_changes_apply_to_open_connections()
    {
        var owner = await SessionAsync("live-role-owner@example.com");
        var editor = await SessionAsync("live-role-editor@example.com");
        var board = await owner.Http.CreateBoardAsync();
        await owner.Http.ShareAsync(board.Id, editor.Http, BoardRole.Editor);
        var editorId = (await editor.Http.MeAsync()).Id;

        var live = await ConnectAsync(editor);
        var access = live.Inbox<Guid>("AccessChanged");
        await live.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        await live.Connection.InvokeAsync("Drag", new DragUpdate(DragKind.Note, Guid.NewGuid(), 1, 1));

        await owner.Http.PatchAsJsonAsync($"/api/boards/{board.Id}/members/{editorId}", new { role = BoardRole.Viewer });
        await access.NextAsync();

        await Assert.ThrowsAsync<HubException>(() => live.Connection.InvokeAsync("Drag", new DragUpdate(DragKind.Note, Guid.NewGuid(), 2, 2)));
    }

    [Fact]
    public async Task Renaming_or_deleting_a_board_updates_every_members_list()
    {
        var owner = await SessionAsync("live-lists-owner@example.com");
        var viewer = await SessionAsync("live-lists-viewer@example.com");
        var board = await owner.Http.CreateBoardAsync();
        await owner.Http.ShareAsync(board.Id, viewer.Http, BoardRole.Viewer);

        // Not joined to the board: list events go to the person wherever they are.
        var live = await ConnectAsync(viewer);
        var lists = live.Inbox<object?>("BoardsChanged");

        await owner.Http.PatchAsJsonAsync($"/api/boards/{board.Id}", new { name = "Renamed" });
        await lists.NextAsync();
        await owner.Http.DeleteAsync($"/api/boards/{board.Id}");
        await lists.NextAsync();
    }

    [Fact]
    public async Task Changing_the_agent_rules_tells_open_boards()
    {
        var owner = await SessionAsync("live-rules-owner@example.com");
        var viewer = await SessionAsync("live-rules-viewer@example.com");
        var board = await owner.Http.CreateBoardAsync();
        await owner.Http.ShareAsync(board.Id, viewer.Http, BoardRole.Viewer);

        var live = await ConnectAsync(viewer);
        var changes = live.Inbox<BoardChange>("BoardChanged");
        await live.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);

        (await owner.Http.PatchAsJsonAsync($"/api/boards/{board.Id}", new { rules = "Keep notes short." })).EnsureSuccessStatusCode();
        Assert.Equal(board.Id, (await changes.NextAsync()).BoardId);
    }

    [Fact]
    public async Task Cross_site_pages_cannot_connect_with_the_cookie()
    {
        var person = await SessionAsync("live-csrf@example.com");
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(person, origin: "https://evil.example"));
    }

    [Fact]
    public async Task Anonymous_callers_cannot_connect()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(new Session(factory.CreateAnonymousClient(), "")));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in connections) await connection.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private sealed record Session(HttpClient Http, string Cookie);

    /// <summary>A signed-in HTTP client plus its cookie, which the hub connection sends too.</summary>
    private async Task<Session> SessionAsync(string email)
    {
        var http = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        var response = await PrikBoardApiFactory.TestSignInAsync(http, email);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        http.DefaultRequestHeaders.Add("Cookie", cookie);
        return new Session(http, cookie);
    }

    private async Task<Live> ConnectAsync(Session session, string origin = "http://localhost")
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/api/live"), options =>
            {
                // The test server has no real sockets; long polling goes through its in-memory handler.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                if (session.Cookie.Length > 0) options.Headers["Cookie"] = session.Cookie;
                options.Headers["Origin"] = origin;
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        connections.Add(connection);
        var live = new Live(connection);
        await connection.StartAsync();
        return live;
    }

    private sealed record Live(HubConnection Connection)
    {
        /// <summary>Collects one kind of server message. Subscribe before the action that triggers it.</summary>
        public Inbox<T> Inbox<T>(string method)
        {
            var channel = Channel.CreateUnbounded<T>();
            if (typeof(T) == typeof(object)) Connection.On(method, () => channel.Writer.TryWrite(default!));
            else Connection.On<T>(method, message => channel.Writer.TryWrite(message));
            return new Inbox<T>(channel.Reader);
        }
    }

    private sealed record Inbox<T>(ChannelReader<T> Reader)
    {
        public async Task<T> NextAsync()
        {
            using var timeout = new CancellationTokenSource(Wait);
            return await Reader.ReadAsync(timeout.Token);
        }

        public async Task<T> NextMatchingAsync(Func<T, bool> match)
        {
            while (true)
            {
                var message = await NextAsync();
                if (match(message)) return message;
            }
        }

        public async Task<bool> AnyWithinAsync(TimeSpan time)
        {
            using var timeout = new CancellationTokenSource(time);
            try
            {
                await Reader.WaitToReadAsync(timeout.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>Lets messages already on their way arrive, then forgets them.</summary>
        public async Task DrainAsync()
        {
            await Task.Delay(200);
            while (Reader.TryRead(out _)) { }
        }
    }
}
