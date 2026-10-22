using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

/// <summary>
/// Copy and paste: copyItems turns buckets, cards, images and drawings into a clip (<see cref="BoardClip"/>) holding
/// everything on them, and pasteItems adds a clip to a board. The clip is self-contained, so it pastes on another PrikBoard
/// server too.
/// </summary>
public static class ClipEndpoints
{
    public static RouteGroupBuilder MapClipEndpoints(this RouteGroupBuilder api)
    {
        var boards = api.MapGroup("/boards").WithTags("Cards");

        boards.MapPost("/{boardId:guid}/copy", CopyItems)
            .RequireBoardRole(BoardRole.Viewer)
            .Quiet()
            .WithName("copyItems")
            .WithSummary("Copy cards, buckets, images and drawings")
            .WithDescription("Returns the items as a clip: one self-contained piece of JSON with the cards' title, fill, tags, assignees " +
                             "and content, the images (bytes as base64) and drawings on them, buckets with everything in them, and images " +
                             "and drawings on the board. Changes nothing. Pass the clip to pasteItems on any board, on this server or " +
                             $"another PrikBoard server. At most {BoardClip.MaxCards} cards, {BoardClip.MaxBuckets} buckets and " +
                             $"{BoardClip.MaxImageBytes / 1024 / 1024} MB of images per clip.")
            .ProducesValidationProblem();

        boards.MapPost("/{boardId:guid}/paste", PasteItems)
            .RequireBoardRole(BoardRole.Editor)
            .WithMetadata(new RequestSizeLimitAttribute(BoardTransferEndpoints.MaxImportBytes), new BufferedBody())
            .WithName("pasteItems")
            .WithSummary("Paste copied items")
            .WithDescription("Adds what is in a clip from copyItems to the board as new items: cards created by you with new numbers, " +
                             "their images and drawings, buckets (on top of the others) with what was in them, and images and drawings. " +
                             "With X and Y everything keeps its place relative to the rest, the top-left at X and Y; an item joins its " +
                             "pasted bucket, or else the bucket it lands in, and a pasted bucket takes the loose items it covers. With a " +
                             "bucketId (a clip of cards alone) the cards go into free spots in that bucket; with neither, cards go into " +
                             "the board's first bucket and anything else to a free spot. Tags the board doesn't have yet are added with " +
                             "their colors; assignees stay only when they can be assigned here. Links between the pasted cards point at " +
                             "the new cards; links to other cards stay, or, for a clip from another server, become full links to it. A clip " +
                             "from a newer PrikBoard (a newer version, or fields this server doesn't know) is refused with a 400 keyed on " +
                             "clip.version.")
            .ProducesValidationProblem();

        return api;
    }

    private static Task<Results<Ok<BoardClip>, NotFound, ValidationProblem>> CopyItems(
        Guid boardId, CopyItemsRequest request, HttpRequest http, PrikBoardDbContext db, FileStore files, CancellationToken ct) =>
        BuildClipAsync(db, files, boardId, request, ServerUrl(http), ct);

    private static async Task<Results<Ok<PastedItems>, NotFound, ValidationProblem>> PasteItems(
        Guid boardId, PasteItemsRequest request, HttpRequest http, IOptions<HttpJsonOptions> json, ClaimsPrincipal principal,
        PrikBoardDbContext db, FileStore files, CancellationToken ct)
    {
        // A clip from a newer PrikBoard is refused rather than pasted without what this server can't read.
        if (request.Clip.Version <= BoardClip.CurrentVersion
            && await BoardTransferEndpoints.UnknownFieldsAsync(http, json.Value.SerializerOptions, typeof(PasteItemsRequest), ct) is { Count: > 0 } unknown)
        {
            var fields = string.Join(", ", unknown.Take(5)) + (unknown.Count > 5 ? $" and {unknown.Count - 5} more" : "");
            return Invalid("clip.version", $"These items have fields this server doesn't know ({fields}). {MadeWith(request.Clip)} " +
                                           "Update PrikBoard to paste them without losing them.");
        }
        return await PasteAsync(boardId, request, ServerUrl(http), principal, db, files, ct);
    }

    /// <summary>Where this server is, as clips name it: scheme and host of the request (the Host header, as for sign-in callbacks).</summary>
    internal static string ServerUrl(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";

    /// <summary>The clip of what <paramref name="what"/> names, all on <paramref name="boardId"/>; 400 keyed on the list otherwise.</summary>
    internal static async Task<Results<Ok<BoardClip>, NotFound, ValidationProblem>> BuildClipAsync(
        PrikBoardDbContext db, FileStore files, Guid boardId, CopyItemsRequest what, string serverUrl, CancellationToken ct)
    {
        var board = await db.Boards.AsNoTracking().Where(b => b.Id == boardId).Select(b => new { b.ShortId, b.Key }).FirstOrDefaultAsync(ct);
        if (board is null) return TypedResults.NotFound();

        var cardIds = (what.CardIds ?? []).Distinct().ToList();
        var bucketIds = (what.BucketIds ?? []).Distinct().ToList();
        var imageIds = (what.ImageIds ?? []).Distinct().ToList();
        var drawingIds = (what.DrawingIds ?? []).Distinct().ToList();
        if (cardIds.Count + bucketIds.Count + imageIds.Count + drawingIds.Count == 0)
        {
            return Invalid("cardIds", "Name at least one card, bucket, image or drawing to copy.");
        }

        var buckets = await db.Buckets.AsNoTracking().Where(b => b.BoardId == boardId && bucketIds.Contains(b.Id)).ToListAsync(ct);
        if (buckets.Count != bucketIds.Count) return Invalid("bucketIds", "Every bucket must be on this board.");
        // A bucket comes with what is in it.
        bool InCopiedBucket(Guid? bucketId) => bucketId is { } id && bucketIds.Contains(id);
        var cards = await db.Cards.AsNoTracking()
            .Where(n => n.BoardId == boardId && (cardIds.Contains(n.Id) || (n.BucketId != null && bucketIds.Contains(n.BucketId.Value))))
            .ToListAsync(ct);
        if (cardIds.Except(cards.Select(n => n.Id)).Any()) return Invalid("cardIds", "Every card must be on this board.");
        var ids = cards.Select(n => n.Id).ToList();

        var images = await db.Images.AsNoTracking().Include(i => i.File)
            .Where(i => i.BoardId == boardId && (i.CardId != null
                ? ids.Contains(i.CardId.Value)
                : imageIds.Contains(i.Id) || (i.BucketId != null && bucketIds.Contains(i.BucketId.Value))))
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .ToListAsync(ct);
        if (imageIds.Except(images.Where(i => i.CardId is null).Select(i => i.Id)).Any())
        {
            return Invalid("imageIds", "Every image must be on this board, and not on a card (those come with their card).");
        }
        if (images.Count > BoardClip.MaxImages || images.Sum(i => i.File!.Size) > BoardClip.MaxImageBytes)
        {
            return Invalid("cardIds", $"These hold more than {BoardClip.MaxImages} images or {BoardClip.MaxImageBytes / 1024 / 1024} MB " +
                                      "of them, more than one copy takes. Copy fewer at a time.");
        }
        var drawings = await db.Drawings.AsNoTracking().Include(d => d.Content)
            .Where(d => d.BoardId == boardId && (d.CardId != null
                ? ids.Contains(d.CardId.Value)
                : drawingIds.Contains(d.Id) || (d.BucketId != null && bucketIds.Contains(d.BucketId.Value))))
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .ToListAsync(ct);
        if (drawingIds.Except(drawings.Where(d => d.CardId is null).Select(d => d.Id)).Any())
        {
            return Invalid("drawingIds", "Every drawing must be on this board, and not on a card (those come with their card).");
        }
        if (cards.Count > BoardClip.MaxCards || drawings.Count > BoardClip.MaxDrawings)
        {
            return Invalid("cardIds", $"These hold more than {BoardClip.MaxCards} cards or {BoardClip.MaxDrawings} drawings. Copy fewer at a time.");
        }

        var tagNames = cards.SelectMany(n => n.Tags).Distinct().ToList();
        var tagColors = await db.BoardTags.AsNoTracking()
            .Where(t => t.BoardId == boardId && tagNames.Contains(t.Name) && t.Color != TagColor.Default)
            .ToDictionaryAsync(t => t.Name, t => t.Color, ct);
        var clippedImages = new List<ClippedImage>();
        foreach (var i in images)
        {
            clippedImages.Add(new ClippedImage(i.CardId, i.File!.FileName, i.Caption, i.X, i.Y, i.Width, await files.ReadAllBytesAsync(i.File, ct),
                InCopiedBucket(i.BucketId) ? i.BucketId : null));
        }

        return TypedResults.Ok(new BoardClip(
            BoardClip.FormatName, BoardClip.CurrentVersion, DateTimeOffset.UtcNow,
            new ClipSource(serverUrl, board.ShortId, board.Key),
            // Oldest edit first: pasted, they get rising timestamps, so the most recently edited card stays on top.
            [.. cards.OrderBy(n => n.UpdatedAt).ThenBy(n => n.Id).Select(n => new ClippedCard(
                n.Id, n.Number, n.Title, n.Color, n.Tags, n.X, n.Y,
                [.. n.Components.Select(b => BoardTransferEndpoints.Exported(b, url => url))], n.AssigneeIds,
                InCopiedBucket(n.BucketId) ? n.BucketId : null))],
            clippedImages,
            [.. drawings.Select(d => new ClippedDrawing(d.CardId, d.X, d.Y,
                [.. d.Content!.Strokes.Zip(d.Colors, (s, color) => new ExportedStroke(color, s.StrokeWidth, [.. s.Points]))],
                BucketId: InCopiedBucket(d.BucketId) ? d.BucketId : null))],
            tagColors,
            AppVersion.Short(AppVersion.Current),
            [.. Layout.BackToFront(buckets).Select(b => new ClippedBucket(b.Id, b.Name, b.Color, b.X, b.Y, b.Width, b.Height, b.Collapsed))]));
    }

    /// <summary>Adds the clip's items to <paramref name="boardId"/>; see pasteItems. <paramref name="serverUrl"/> is this server.</summary>
    internal static async Task<Results<Ok<PastedItems>, NotFound, ValidationProblem>> PasteAsync(
        Guid boardId, PasteItemsRequest request, string serverUrl, ClaimsPrincipal principal, PrikBoardDbContext db, FileStore files,
        CancellationToken ct)
    {
        var clip = request.Clip;
        if (clip.Version > BoardClip.CurrentVersion)
        {
            return Invalid("clip.version", $"These items were copied in a newer format (version {clip.Version}; this server reads up to " +
                                           $"{BoardClip.CurrentVersion}). {MadeWith(clip)} Update PrikBoard to paste them.");
        }
        if (clip.Format != BoardClip.FormatName) return Invalid("clip.format", $"This is not a PrikBoard clip: format must be \"{BoardClip.FormatName}\".");
        if (request.X is null != request.Y is null) return Invalid(request.X is null ? "x" : "y", "Give X and Y together.");
        if (request.BucketId is not null && request.X is not null) return Invalid("bucketId", "Give a bucketId, or X and Y, not both.");
        if (clip.Cards.Select(n => n.Id).Distinct().Count() != clip.Cards.Count) return Invalid("clip.cards", "Card ids must be unique within the clip.");
        var clipBuckets = clip.Buckets ?? [];
        if (clipBuckets.Select(b => b.Id).Distinct().Count() != clipBuckets.Count) return Invalid("clip.buckets", "Bucket ids must be unique within the clip.");
        var looseImages = clip.Images.Where(m => m.CardId is null).ToList();
        var looseDrawings = clip.Drawings.Where(d => d.CardId is null).ToList();
        var cardsOnly = clipBuckets.Count == 0 && looseImages.Count == 0 && looseDrawings.Count == 0;
        if (cardsOnly && clip.Cards.Count == 0) return Invalid("clip", "The clip holds nothing to paste.");
        if (!cardsOnly && request.BucketId is not null)
        {
            return Invalid("bucketId", "Only cards go into a bucket by bucketId; paste buckets, images and drawings at X and Y.");
        }
        var inBucket = clipBuckets.Select(b => b.Id).ToHashSet();
        if (clip.Cards.Any(n => n.BucketId is { } b && !inBucket.Contains(b))
            || looseImages.Any(m => m.BucketId is { } b && !inBucket.Contains(b))
            || looseDrawings.Any(d => d.BucketId is { } b && !inBucket.Contains(b)))
        {
            return Invalid("clip.buckets", "An item names a bucket that is not in the clip.");
        }

        var board = await db.Boards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == boardId, ct);
        if (board is null) return TypedResults.NotFound();
        var me = principal.PrincipalId();
        var sameServer = SameServer(clip.Source.Url, serverUrl);

        // People from the clip stay on the copies only when they can be assigned here.
        var people = clip.Cards.SelectMany(n => n.AssigneeIds ?? []).Distinct().ToList();
        var assignable = people.Count == 0
            ? []
            : (await BoardAccess.AssignableIds(db, boardId).Where(id => people.Contains(id)).ToListAsync(ct)).ToHashSet();

        var now = DateTimeOffset.UtcNow;
        var cards = new List<Card>();
        foreach (var (source, i) in clip.Cards.Select((n, i) => (n, i)))
        {
            var tags = CardTags.Normalize(source.Tags);
            if (tags.Any(t => t.Length > CardTags.MaxLength)) return Invalid($"clip.cards[{i}].tags", $"A tag is at most {CardTags.MaxLength} characters.");
            cards.Add(new Card
            {
                BoardId = boardId,
                Title = source.Title.Trim(),
                Color = source.Color,
                Tags = tags,
                AssigneeIds = [.. (source.AssigneeIds ?? []).Where(assignable.Contains).Distinct()],
                CreatedById = me,
                CreatedAt = now.AddMilliseconds(i),
                UpdatedAt = now.AddMilliseconds(i),
            });
        }
        var copies = clip.Cards.Select((n, i) => (n.Id, Card: cards[i])).ToDictionary(p => p.Id, p => p.Card);

        if (clip.Images.Sum(m => (long)m.Data.Length) > BoardClip.MaxImageBytes)
        {
            return Invalid("clip.images", $"A clip holds at most {BoardClip.MaxImageBytes / 1024 / 1024} MB of images.");
        }
        var images = new List<(BoardImage Image, ClippedImage Source, byte[] Data)>();
        foreach (var (source, i) in clip.Images.Select((m, i) => (m, i)))
        {
            Card? card = null;
            if (source.CardId is { } on && !copies.TryGetValue(on, out card))
            {
                return Invalid($"clip.images[{i}].cardId", "The card is not in the clip.");
            }
            if (source.Data.Length > ImageFormats.MaxBytes)
            {
                return Invalid($"clip.images[{i}].data", $"Images can be at most {ImageFormats.MaxBytes / 1024 / 1024} MB.");
            }
            if (ImageFormats.Detect(source.Data) is not { } info)
            {
                return Invalid($"clip.images[{i}].data", "The file is not a PNG, JPEG, GIF or WebP image (or it is larger than 20000 pixels on a side).");
            }
            var image = new BoardImage
            {
                BoardId = boardId,
                CardId = card?.Id,
                PixelWidth = info.Width,
                PixelHeight = info.Height,
                Caption = PatchText.Apply(null, source.Caption),
                AddedById = me,
                File = StoredFile.For(boardId, ImageEndpoints.CleanFileName(source.FileName, info.ContentType), info.ContentType, source.Data),
                CreatedAt = now.AddMilliseconds(i),
                UpdatedAt = now.AddMilliseconds(i),
            };
            image.Resize(source.Width);
            // On the board it gets its place with the rest, below.
            if (card is not null) (image.X, image.Y) = Layout.OnCardCanvas(source.X, source.Y, image.Width, image.Height);
            images.Add((image, source, source.Data));
        }

        if (clip.Drawings.Count > 0
            && await db.Drawings.CountAsync(d => d.BoardId == boardId, ct) + clip.Drawings.Count > BoardDrawing.MaxPerBoard)
        {
            return Invalid("clip.drawings", $"A board holds at most {BoardDrawing.MaxPerBoard} drawings, and these would pass that. Delete some first.");
        }
        var drawings = new List<(BoardDrawing Drawing, ClippedDrawing Source)>();
        foreach (var (source, i) in clip.Drawings.Select((d, i) => (d, i)))
        {
            Card? card = null;
            if (source.CardId is { } on && !copies.TryGetValue(on, out card))
            {
                return Invalid($"clip.drawings[{i}].cardId", "The card is not in the clip.");
            }
            var drawing = new BoardDrawing
            {
                BoardId = boardId,
                CardId = card?.Id,
                AddedById = me,
                CreatedAt = now.AddMilliseconds(i),
                UpdatedAt = now.AddMilliseconds(i),
            };
            // A drawing on the board is built where it was and moved with the rest, below.
            if (DrawingEndpoints.Build(drawing, source.AllStrokes(), source.X, source.Y, $"clip.drawings[{i}].") is { } invalid) return invalid;
            drawings.Add((drawing, source));
        }

        // Tags new to the board come with their colors.
        if (await TagEndpoints.EnsureBoardTagsAsync(db, boardId, [.. cards.SelectMany(n => n.Tags).Distinct()], ct) is { } tooManyTags)
        {
            return tooManyTags;
        }
        var colors = (clip.TagColors ?? []).GroupBy(p => CardTags.Normalize(p.Key)).ToDictionary(g => g.Key, g => g.First().Value);
        foreach (var tag in db.ChangeTracker.Entries<BoardTag>().Where(e => e.State == EntityState.Added).Select(e => e.Entity))
        {
            if (colors.TryGetValue(tag.Name, out var color)) tag.Color = color;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var bucket = request.BucketId is { } bucketId ? await LockBucketAsync(db, boardId, bucketId, ct) : null;
        if (request.BucketId is not null && bucket is null) return Invalid("bucketId", "The bucket does not exist on this board.");
        var buckets = await db.Buckets.Where(b => b.BoardId == boardId).ToListAsync(ct);
        if (bucket is null && cardsOnly && request.X is null && Layout.InListOrder(buckets).FirstOrDefault() is { } first)
        {
            bucket = await LockBucketAsync(db, boardId, first.Id, ct);
        }
        var others = await db.Cards.Where(n => n.BoardId == boardId).ToListAsync(ct);
        var newBuckets = new List<Bucket>();
        if (bucket is not null) await PackIntoAsync(db, bucket, buckets, others, cards, ct);
        else
        {
            // Everything moves by the same step, from the top-left of what is on the board to X and Y (or a free spot).
            var (x, y) = request is { X: { } px, Y: { } py } ? (Layout.Snap(px), Layout.Snap(py)) : Layout.FreeSpotOutside(buckets, others);
            var lefts = clip.Cards.Select(n => n.X).Concat(clipBuckets.Select(b => b.X)).Concat(looseImages.Select(m => m.X)).Concat(looseDrawings.Select(d => d.X));
            var tops = clip.Cards.Select(n => n.Y).Concat(clipBuckets.Select(b => b.Y)).Concat(looseImages.Select(m => m.Y)).Concat(looseDrawings.Select(d => d.Y));
            var (dx, dy) = (x - Layout.Snap(lefts.Min()), y - Layout.Snap(tops.Min()));
            static int Shift(int value, int by) => Math.Clamp(Layout.Snap(value) + by, Layout.MinCoordinate, Layout.MaxCoordinate);

            // Pasted buckets go on top of the board's, in their own order; what was in them joins them again.
            var pasted = new Dictionary<Guid, Bucket>();
            foreach (var (source, i) in clipBuckets.Select((b, i) => (b, i)))
            {
                var made = new Bucket
                {
                    BoardId = boardId,
                    Name = source.Name.Trim(),
                    Color = source.Color,
                    X = Shift(source.X, dx),
                    Y = Shift(source.Y, dy),
                    Width = Math.Min(Layout.MaxBucketSize, Layout.SnapSize(source.Width, Layout.MinBucketWidth)),
                    Height = Math.Min(Layout.MaxBucketSize, Layout.SnapSize(source.Height, Layout.MinBucketHeight)),
                    CreatedAt = now.AddMilliseconds(i),
                    StackedAt = now.AddMilliseconds(i),
                };
                pasted[source.Id] = made;
                newBuckets.Add(made);
            }
            var stacked = Layout.BackToFront([.. buckets, .. newBuckets]);

            foreach (var (source, card) in clip.Cards.Zip(cards))
            {
                (card.X, card.Y) = (Shift(source.X, dx), Shift(source.Y, dy));
                if (source.BucketId is { } b) card.BucketId = pasted[b].Id;
                else Layout.Place(card, stacked);
            }
            foreach (var (image, source, _) in images.Where(m => m.Image.CardId is null))
            {
                (image.X, image.Y) = (Shift(source.X, dx), Shift(source.Y, dy));
                if (source.BucketId is { } b) image.BucketId = pasted[b].Id;
                else Layout.Place(image, stacked);
            }
            foreach (var (drawing, source) in drawings.Where(d => d.Drawing.CardId is null))
            {
                (drawing.X, drawing.Y) = (Shift(drawing.X, dx), Shift(drawing.Y, dy));
                if (source.BucketId is { } b) drawing.BucketId = pasted[b].Id;
                else Layout.Place(drawing, stacked);
            }

            // What was in them has joined them by id, so they can fold up now. An open one then takes the board's loose items
            // it covers, like a bucket drawn there (not those in other buckets); a folded one takes none, as they would vanish.
            foreach (var (source, made) in clipBuckets.Select(b => (b, pasted[b.Id]))) made.Collapsed = source.Collapsed;
            if (newBuckets.Count > 0)
            {
                List<ICanvasItem> loose =
                [
                    .. await db.Images.Where(i => i.BoardId == boardId && i.CardId == null && i.BucketId == null).ToListAsync(ct),
                    .. await db.Drawings.Where(d => d.BoardId == boardId && d.CardId == null && d.BucketId == null).ToListAsync(ct),
                ];
                foreach (var made in newBuckets) Layout.Adopt(made, stacked, others, loose, looseOnly: true);
            }
        }

        foreach (var card in cards) card.Number = await CardEndpoints.TakeNumberAsync(db, boardId, ct);
        var byNumber = clip.Cards.Zip(cards).ToDictionary(p => p.First.Number, p => p.Second);
        foreach (var (source, i) in clip.Cards.Select((n, i) => (n, i)))
        {
            var components = (source.Components ?? [])
                .Select(b => b with { Links = b.Links is null ? null : FollowLinks(b.Links, clip.Source, sameServer, copies, byNumber, board.ShortId) })
                .ToList();
            if (CardComponentEndpoints.BuildAll(cards[i], components, $"clip.cards[{i}].") is { } invalid) return invalid;
            db.CardChanges.Add(new CardChange
            {
                CardId = cards[i].Id,
                BoardId = boardId,
                ById = me,
                At = now,
                Entries = [new(CardChangeKind.Copied, CardLinks.ReadableId(clip.Source.BoardKey, source.Number),
                    LinkTo(CardLinks.CardPath(clip.Source.BoardShortId, source.Number), clip.Source, sameServer))],
            });
        }

        // Files go on disk before their rows are saved (see FileStore).
        foreach (var (image, _, data) in images) await files.WriteAsync(image.File!, data, ct);
        db.Buckets.AddRange(newBuckets);
        db.Cards.AddRange(cards);
        db.Images.AddRange(images.Select(m => m.Image));
        db.Drawings.AddRange(drawings.Select(d => d.Drawing));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var principals = await CardEndpoints.PrincipalsOnAsync(db, cards, ct);
        var adder = await db.Principals.FindAsync([me], ct);
        foreach (var (image, _, _) in images) image.AddedBy = adder;
        foreach (var (drawing, _) in drawings) drawing.AddedBy = adder;
        var boardRef = BoardRef.Of(board);
        return TypedResults.Ok(new PastedItems(
            [.. cards.Select(n => CardDetail.From(n, boardRef, principals))],
            [.. images.Select(m => ImageDetail.From(m.Image))],
            [.. drawings.Select(d => DrawingDetail.From(d.Drawing))],
            bucket is null ? null : BucketDetail.From(bucket),
            [.. newBuckets.Select(BucketDetail.From)]));
    }

    /// <summary>The bucket, locked until the transaction ends so two pastes into it don't take the same free spots; null when it isn't on the board.</summary>
    private static async Task<Bucket?> LockBucketAsync(PrikBoardDbContext db, Guid boardId, Guid bucketId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Buckets" WHERE "Id" = {bucketId} FOR UPDATE""", ct);
        return await db.Buckets.FirstOrDefaultAsync(b => b.Id == bucketId && b.BoardId == boardId, ct);
    }

    /// <summary>Puts the new cards into free spots in the bucket, as placeCardsInBucket does, growing it downwards when it is full.</summary>
    private static async Task PackIntoAsync(
        PrikBoardDbContext db, Bucket bucket, List<Bucket> buckets, List<Card> others, List<Card> cards, CancellationToken ct)
    {
        var images = await db.Images.Where(i => i.BoardId == bucket.BoardId && i.CardId == null).ToListAsync(ct);
        bool InTheWay(Guid? owner) => owner is null || owner == bucket.Id;
        List<(int X, int Y, int Width, int Height)> taken =
        [
            .. others.Where(n => InTheWay(n.BucketId)).Select(n => (n.X, n.Y, Layout.CardWidth, Layout.CardHeight)),
            .. images.Where(i => InTheWay(i.BucketId)).Select(i => (i.X, i.Y, i.Width, i.Height)),
        ];
        bucket.ToFront();
        foreach (var (card, (x, y)) in cards.Zip(Layout.PackIn(bucket, cards.Count, taken, GrowSide.Down)))
        {
            (card.X, card.Y) = (x, y);
            card.BucketId = bucket.Id;
        }
        // A bucket that grew can now cover loose items too.
        var drawings = await db.Drawings.Where(d => d.BoardId == bucket.BoardId && d.CardId == null).ToListAsync(ct);
        Layout.Adopt(bucket, Layout.BackToFront(buckets), others, [.. images, .. drawings], looseOnly: true);
    }

    /// <summary>
    /// Links to the copied cards point at their copies; web links stay. Links to other cards stay on the same server, and
    /// become full links to the source server on another one (or are dropped when the clip names no usable server).
    /// </summary>
    private static List<CardLink> FollowLinks(
        IEnumerable<CardLink> links, ClipSource source, bool sameServer, Dictionary<Guid, Card> copies, Dictionary<int, Card> byNumber, string shortId)
    {
        var followed = new List<CardLink>();
        foreach (var link in links)
        {
            if (!CardLinks.TryParse(link.Url, out var target))
            {
                followed.Add(link);
                continue;
            }
            var copy = target.ByNumber
                ? target.BoardShortId == source.BoardShortId ? byNumber.GetValueOrDefault(target.Number) : null
                : copies.GetValueOrDefault(target.CardId);
            if (copy is not null) followed.Add(link with { Url = CardLinks.CardPath(shortId, copy.Number) });
            else if (LinkTo(link.Url, source, sameServer) is { } url) followed.Add(link with { Url = url });
        }
        return followed;
    }

    /// <summary>A path on the source server as a link from here: the path itself on the same server, a full URL otherwise.</summary>
    private static string? LinkTo(string path, ClipSource source, bool sameServer)
    {
        if (sameServer) return path;
        var url = source.Url.TrimEnd('/') + path.Trim();
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? url : null;
    }

    private static bool SameServer(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
        && Uri.Compare(x, y, UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static string MadeWith(BoardClip clip) => clip.AppVersion is { } made
        ? $"They were copied with PrikBoard {made}, and this server runs {AppVersion.Short(AppVersion.Current)}."
        : "They were copied with a newer PrikBoard than this server runs.";

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
