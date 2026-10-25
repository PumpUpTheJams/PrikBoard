using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>
/// Items copied from a board, from copyItems and for pasteItems: buckets, cards, images and drawings with everything on
/// them in one self-contained piece of JSON, so it can be pasted on any board, on this PrikBoard server or another one. The
/// web app puts it on the clipboard, a whole board's worth if need be. It holds content and the ids of who created and
/// is assigned to cards, which only mean something on this server; other ids in it are only meaningful inside it.
/// </summary>
/// <param name="Format">Always "prikboard-clip".</param>
/// <param name="Version">Version of this format, currently 6 (1 to 5 still paste; bucket colors of 1 to 3 are ignored). A clip with a newer version, or with fields the server doesn't know, is refused.</param>
/// <param name="CopiedAt">When it was copied (UTC).</param>
/// <param name="Source">Where the items were copied from.</param>
/// <param name="Cards">The cards (also those in the copied buckets), the most recently edited last (they are drawn on top).</param>
/// <param name="Images">The images: attached to those cards, or on the board (copied, or in a copied bucket), with their bytes.</param>
/// <param name="Drawings">The drawings: on those cards, or on the board (copied, or in a copied bucket), with their strokes, oldest first.</param>
/// <param name="TagColors">Colors of the cards' tags that have one (not Default), by tag name. Tags new to the board get them.</param>
/// <param name="AppVersion">The PrikBoard version that made the clip, named when another server can't read it.</param>
/// <param name="Buckets">The buckets, back to front (from version 2). Their cards, images and drawings are in the lists above, naming them by bucketId.</param>
/// <param name="Cut">Cut rather than copied (from version 3): pasted on another board on the same server, the originals go to their board's trash.</param>
public record BoardClip(
    [property: Required] string Format,
    [property: Range(1, int.MaxValue)] int Version,
    DateTimeOffset CopiedAt,
    [property: Required] ClipSource Source,
    [property: Required, MaxLength(BoardClip.MaxCards)] List<ClippedCard> Cards,
    [property: Required, MaxLength(BoardClip.MaxImages)] List<ClippedImage> Images,
    [property: Required, MaxLength(BoardClip.MaxDrawings)] List<ClippedDrawing> Drawings,
    Dictionary<string, TagColor>? TagColors = null,
    [property: StringLength(100)] string? AppVersion = null,
    [property: MaxLength(BoardClip.MaxBuckets)] List<ClippedBucket>? Buckets = null,
    bool Cut = false)
{
    public const string FormatName = "prikboard-clip";
    /// <summary>Raise it with every change to the clip's fields, so an older server refuses a clip it can't fully read.</summary>
    public const int CurrentVersion = 6;
    /// <summary>A whole board fits in one clip.</summary>
    public const int MaxCards = 2000;
    public const int MaxImages = 200;
    public const int MaxDrawings = BoardDrawing.MaxPerBoard;
    public const int MaxBuckets = 100;
    /// <summary>The most image bytes one clip holds; they travel as base64 through the clipboard.</summary>
    public const long MaxImageBytes = 150 * 1024 * 1024;
    /// <summary>
    /// The most image bytes a clip holds with the images' data in it, as copyItems returns them and pasteItems takes them
    /// in one request. Larger clips leave the data out (copyItems' imageData) and upload the images first (uploadPasteImage).
    /// </summary>
    public const long MaxInlineImageBytes = 40 * 1024 * 1024;
}

/// <summary>Where the items in a clip come from.</summary>
/// <param name="Url">The PrikBoard server they were copied on, e.g. https://prikboard.example.com. Card links in the clip are paths on it.</param>
/// <param name="BoardShortId">The short id of their board.</param>
/// <param name="BoardKey">The key of their board, as in readable ids (MPCT-415).</param>
public record ClipSource(
    [property: Required, StringLength(2000)] string Url,
    [property: Required, StringLength(Board.ShortIdLength, MinimumLength = Board.ShortIdLength)] string BoardShortId,
    [property: Required, StringLength(Board.KeyMaxLength)] string BoardKey);

/// <summary>A bucket in a clip.</summary>
/// <param name="Id">The bucket's id on its board: the items in it name it by bucketId.</param>
/// <param name="Name">Bucket name.</param>
/// <param name="X">Left edge on its board. Pasted items keep their places relative to each other.</param>
/// <param name="Y">Top edge on its board.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Collapsed">Folded up, hiding its items.</param>
public record ClippedBucket(
    Guid Id,
    [property: Required, StringLength(100, MinimumLength = 1)] string Name,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int X,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int Y,
    [property: Range(Layout.MinBucketWidth, Layout.MaxBucketSize)] int Width,
    [property: Range(Layout.MinBucketHeight, Layout.MaxBucketSize)] int Height,
    bool Collapsed = false);

/// <summary>A card in a clip.</summary>
/// <param name="Id">The card's id on its board: images and drawings refer to their card by it, and links between the copied cards follow it.</param>
/// <param name="Number">The card's number on its board (readable id and path).</param>
/// <param name="Title">Short title; may be empty.</param>
/// <param name="Color">Card fill.</param>
/// <param name="Tags">Tag names.</param>
/// <param name="X">Left edge on its board. Pasted items keep their places relative to each other.</param>
/// <param name="Y">Top edge on its board.</param>
/// <param name="Components">The card's content on its own canvas, in order. Links to other cards are paths on the source server.</param>
/// <param name="AssigneeIds">Principal ids of its assignees; a paste keeps only those who can be assigned on the board it lands on.</param>
/// <param name="BucketId">The copied bucket it is in (Buckets), or null (from version 2).</param>
/// <param name="CreatedById">Principal id of who created it (from version 6): a copy pasted on the same board keeps them as its creator.</param>
public record ClippedCard(
    Guid Id,
    [property: Range(1, int.MaxValue)] int Number,
    [property: Required(AllowEmptyStrings = true), StringLength(200)] string Title,
    CardColor Color,
    [property: Required, MaxLength(10)] List<string> Tags,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int X,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int Y,
    [property: MaxLength(CardComponent.MaxComponents)] List<NewCardComponent>? Components = null,
    [property: MaxLength(Endpoints.CardEndpoints.MaxAssignees)] List<Guid>? AssigneeIds = null,
    Guid? BucketId = null,
    Guid? CreatedById = null);

/// <summary>An image in a clip, with its file.</summary>
/// <param name="CardId">Id of the card (in this clip) the image is attached to, or null for an image on the board.</param>
/// <param name="FileName">Original file name.</param>
/// <param name="Caption">Caption, or null.</param>
/// <param name="X">Left edge on the board, or on the card's canvas for an attached image.</param>
/// <param name="Y">Top edge on the board, or on the card's canvas for an attached image.</param>
/// <param name="Width">Display width in pixels; the height follows from the aspect ratio.</param>
/// <param name="Data">The image file itself, as base64 in JSON; the real format is read from it (PNG, JPEG, GIF or WebP, at most 10 MB). Empty when copyItems left it out (imageData false): get it from getImageContent by Id, and paste it in Data or upload it with uploadPasteImage.</param>
/// <param name="BucketId">For an image on the board: the copied bucket it is in (Buckets), or null (from version 2).</param>
/// <param name="Id">The image's id on its board, so a cut can remove it (from version 3).</param>
/// <param name="Height">Display height in pixels (from version 6), so where it goes can be shown before its data is read.</param>
public record ClippedImage(
    Guid? CardId,
    [property: Required, StringLength(255)] string FileName,
    [property: StringLength(500)] string? Caption,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int X,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int Y,
    [property: Range(Layout.MinImageWidth, Layout.MaxImageWidth)] int Width,
    [property: Required] byte[] Data,
    Guid? BucketId = null,
    Guid? Id = null,
    int? Height = null);

/// <summary>A drawing in a clip, with its strokes.</summary>
/// <param name="CardId">Id of the card (in this clip) the drawing is on, or null for a drawing on the board.</param>
/// <param name="X">Left edge on the board, or on the card's canvas; the points are relative to it.</param>
/// <param name="Y">Top edge on the board, or on the card's canvas; the points are relative to it.</param>
/// <param name="Strokes">The strokes in drawing order.</param>
/// <param name="Color">Unused (an older one-stroke form); null.</param>
/// <param name="StrokeWidth">Unused (an older one-stroke form); null.</param>
/// <param name="Points">Unused (an older one-stroke form); null.</param>
/// <param name="BucketId">For a drawing on the board: the copied bucket it is in (Buckets), or null (from version 2).</param>
/// <param name="Id">The drawing's id on its board, so a cut can remove it (from version 3).</param>
/// <param name="BucketsBelow">For a drawing on the board in none of the copied buckets: how many of them (Buckets) it is stacked above (from version 5). Optional; without it, it goes above them all.</param>
public record ClippedDrawing(
    Guid? CardId,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int X,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int Y,
    [property: MaxLength(BoardDrawing.MaxStrokes)] List<ClippedStroke>? Strokes = null,
    InkColor? Color = null,
    int? StrokeWidth = null,
    [property: MaxLength(BoardDrawing.MaxPoints)] List<double[]>? Points = null,
    Guid? BucketId = null,
    Guid? Id = null,
    [property: Range(0, BoardClip.MaxBuckets)] int? BucketsBelow = null)
{
    /// <summary>The strokes, from either form.</summary>
    public IReadOnlyList<(InkColor Color, int Width, IReadOnlyList<double[]> Points)> AllStrokes() =>
        Strokes is not null
            ? [.. Strokes.Select(s => (s.Color, s.StrokeWidth, (IReadOnlyList<double[]>)s.Points))]
            : Points is not null ? [(Color ?? InkColor.Chalk, StrokeWidth ?? 4, Points)] : [];
}

/// <summary>One stroke of a drawing in a clip.</summary>
/// <param name="Color">Ink color.</param>
/// <param name="StrokeWidth">Base line width: 2, 4 or 8.</param>
/// <param name="Points">The stroke, each point [x, y, pressure] relative to the drawing's x and y, as in createDrawing.</param>
public record ClippedStroke(
    InkColor Color,
    int StrokeWidth,
    [property: Required, MaxLength(BoardDrawing.MaxPoints)] List<double[]> Points);

/// <summary>What to copy, all on the board in the path. A bucket comes with the cards, images and drawings in it.</summary>
/// <param name="CardIds">Cards.</param>
/// <param name="BucketIds">Buckets, with what is in them.</param>
/// <param name="ImageIds">Images on the board (images on cards come with their card).</param>
/// <param name="DrawingIds">Drawings on the board (drawings on cards come with their card).</param>
/// <param name="Cut">Cut them: the clip says so, and pasted on another board of this server the originals go to the trash. Needs the Editor role.</param>
/// <param name="ImageData">With the images' bytes in the clip (default), up to 40 MB of them; false leaves them out (data is empty), up to 150 MB, so a big copy can fetch them one by one from getImageContent.</param>
public record CopyItemsRequest(
    [property: MaxLength(BoardClip.MaxCards)] List<Guid>? CardIds = null,
    [property: MaxLength(BoardClip.MaxBuckets)] List<Guid>? BucketIds = null,
    [property: MaxLength(BoardClip.MaxImages)] List<Guid>? ImageIds = null,
    [property: MaxLength(BoardClip.MaxDrawings)] List<Guid>? DrawingIds = null,
    bool Cut = false,
    bool ImageData = true);

/// <summary>
/// A clip to paste, and where. With X and Y everything keeps its place relative to the rest, the top-left at X and Y; an
/// item joins its pasted bucket, or else the bucket it lands in. With a bucketId (cards only) they go into free spots in that
/// bucket. With neither, a clip of cards alone goes into the board's first bucket, and any other clip to a free spot.
/// </summary>
/// <param name="Clip">The clip from copyItems, as it was.</param>
/// <param name="X">Where the left edge of the leftmost item goes; give it with Y.</param>
/// <param name="Y">Where the top edge of the topmost item goes; give it with X.</param>
/// <param name="BucketId">A bucket on this board to put the cards in, instead of X and Y; only for a clip of cards alone.</param>
/// <param name="ImageFiles">For a big clip: by index in the clip's images, the token uploadPasteImage gave for that image's file, whose data in the clip is then empty; null for an image whose data is in the clip.</param>
public record PasteItemsRequest(
    [property: Required] BoardClip Clip,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null,
    Guid? BucketId = null,
    [property: MaxLength(BoardClip.MaxImages)] List<string?>? ImageFiles = null);

/// <summary>An image file uploaded for a paste (uploadPasteImage), waiting for pasteItems to put it on the board.</summary>
/// <param name="Token">Give it in pasteItems' imageFiles, at the image's index. Only you can use it, on this board, until ExpiresAt.</param>
/// <param name="ContentType">The format read from the file.</param>
/// <param name="Size">The file's size in bytes.</param>
/// <param name="ExpiresAt">When the token stops working (UTC). The file goes with it unless a paste used it.</param>
public record PasteImageFile(string Token, string ContentType, long Size, DateTimeOffset ExpiresAt);

/// <summary>What a paste added to the board.</summary>
/// <param name="Cards">The new cards, in the clip's order.</param>
/// <param name="Images">The new images: on those cards, or on the board.</param>
/// <param name="Drawings">The new drawings: on those cards, or on the board.</param>
/// <param name="Bucket">The bucket the cards went into when one was asked for (it may have grown); null otherwise.</param>
/// <param name="Buckets">The new buckets, back to front, on top of the board's others.</param>
/// <param name="Moved">For a cut clip from another board on this server: the originals now in that board's trash, as they were. Null otherwise (a copy, or a cut from another server, whose originals stay).</param>
public record PastedItems(
    List<CardDetail> Cards, List<ImageDetail> Images, List<DrawingDetail> Drawings, BucketDetail? Bucket, List<BucketDetail> Buckets,
    MovedOriginals? Moved = null);

/// <summary>The originals of a cut, deleted when it was pasted (restoreCard, restoreBucket, restoreImage and restoreDrawing bring them back).</summary>
/// <param name="BoardId">The board they were cut from.</param>
/// <param name="BoardName">Its name.</param>
/// <param name="Cards">The cards, with the images and drawings on them.</param>
/// <param name="Buckets">The buckets. What else was in them when they were cut stays on the board, loose.</param>
/// <param name="Images">The images that were on the board.</param>
/// <param name="Drawings">The drawings that were on the board.</param>
public record MovedOriginals(
    Guid BoardId, string BoardName, List<CardDetail> Cards, List<BucketDetail> Buckets, List<ImageDetail> Images, List<DrawingDetail> Drawings);
