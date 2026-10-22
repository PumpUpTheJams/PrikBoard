using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>
/// Items copied from a board, from copyItems and for pasteItems: buckets, cards, images and drawings with everything on
/// them in one self-contained piece of JSON, so it can be pasted on any board, on this PrikBoard server or another one. The
/// web app puts it on the clipboard. Like an export (exportBoard) it holds content, not people; ids in it are only
/// meaningful inside it.
/// </summary>
/// <param name="Format">Always "prikboard-clip".</param>
/// <param name="Version">Version of this format, currently 2 (1 still pastes). A clip with a newer version, or with fields the server doesn't know, is refused.</param>
/// <param name="CopiedAt">When it was copied (UTC).</param>
/// <param name="Source">Where the items were copied from.</param>
/// <param name="Cards">The cards (also those in the copied buckets), the most recently edited last (they are drawn on top).</param>
/// <param name="Images">The images: attached to those cards, or on the board (copied, or in a copied bucket), with their bytes.</param>
/// <param name="Drawings">The drawings: on those cards, or on the board (copied, or in a copied bucket), with their strokes, oldest first.</param>
/// <param name="TagColors">Colors of the cards' tags that have one (not Default), by tag name. Tags new to the board get them.</param>
/// <param name="AppVersion">The PrikBoard version that made the clip, named when another server can't read it.</param>
/// <param name="Buckets">The buckets, back to front (from version 2). Their cards, images and drawings are in the lists above, naming them by bucketId.</param>
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
    [property: MaxLength(BoardClip.MaxBuckets)] List<ClippedBucket>? Buckets = null)
{
    public const string FormatName = "prikboard-clip";
    /// <summary>Raise it with every change to the clip's fields, so an older server refuses a clip it can't fully read.</summary>
    public const int CurrentVersion = 2;
    public const int MaxCards = 200;
    public const int MaxImages = 100;
    public const int MaxDrawings = 1000;
    public const int MaxBuckets = 50;
    /// <summary>The most image bytes one clip holds; they travel as base64 through the clipboard.</summary>
    public const long MaxImageBytes = 40 * 1024 * 1024;
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
/// <param name="Color">Outline color.</param>
/// <param name="X">Left edge on its board. Pasted items keep their places relative to each other.</param>
/// <param name="Y">Top edge on its board.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Collapsed">Folded up, hiding its items.</param>
public record ClippedBucket(
    Guid Id,
    [property: Required, StringLength(100, MinimumLength = 1)] string Name,
    BucketColor Color,
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
    Guid? BucketId = null);

/// <summary>An image in a clip: as in an export, and the copied bucket it is in.</summary>
/// <param name="CardId">Id of the card (in this clip) the image is attached to, or null for an image on the board.</param>
/// <param name="FileName">Original file name.</param>
/// <param name="Caption">Caption, or null.</param>
/// <param name="X">Left edge on the board, or on the card's canvas for an attached image.</param>
/// <param name="Y">Top edge on the board, or on the card's canvas for an attached image.</param>
/// <param name="Width">Display width in pixels; the height follows from the aspect ratio.</param>
/// <param name="Data">The image file itself, as base64 in JSON (PNG, JPEG, GIF or WebP, at most 10 MB).</param>
/// <param name="BucketId">For an image on the board: the copied bucket it is in (Buckets), or null (from version 2).</param>
public record ClippedImage(
    Guid? CardId, string FileName, string? Caption, int X, int Y, int Width, byte[] Data, Guid? BucketId = null)
    : ExportedImage(CardId, FileName, Caption, X, Y, Width, Data);

/// <summary>A drawing in a clip: as in an export, and the copied bucket it is in.</summary>
/// <param name="CardId">Id of the card (in this clip) the drawing is on, or null for a drawing on the board.</param>
/// <param name="X">Left edge on the board, or on the card's canvas; the points are relative to it.</param>
/// <param name="Y">Top edge on the board, or on the card's canvas; the points are relative to it.</param>
/// <param name="Strokes">The strokes in drawing order.</param>
/// <param name="Color">Unused (the export file's older one-stroke form); null.</param>
/// <param name="StrokeWidth">Unused (the export file's older one-stroke form); null.</param>
/// <param name="Points">Unused (the export file's older one-stroke form); null.</param>
/// <param name="BucketId">For a drawing on the board: the copied bucket it is in (Buckets), or null (from version 2).</param>
public record ClippedDrawing(
    Guid? CardId, int X, int Y, List<ExportedStroke>? Strokes = null, InkColor? Color = null, int? StrokeWidth = null,
    List<double[]>? Points = null, Guid? BucketId = null)
    : ExportedDrawing(CardId, X, Y, Strokes, Color, StrokeWidth, Points);

/// <summary>What to copy, all on the board in the path. A bucket comes with the cards, images and drawings in it.</summary>
/// <param name="CardIds">Cards.</param>
/// <param name="BucketIds">Buckets, with what is in them.</param>
/// <param name="ImageIds">Images on the board (images on cards come with their card).</param>
/// <param name="DrawingIds">Drawings on the board (drawings on cards come with their card).</param>
public record CopyItemsRequest(
    [property: MaxLength(BoardClip.MaxCards)] List<Guid>? CardIds = null,
    [property: MaxLength(BoardClip.MaxBuckets)] List<Guid>? BucketIds = null,
    [property: MaxLength(BoardClip.MaxImages)] List<Guid>? ImageIds = null,
    [property: MaxLength(BoardClip.MaxDrawings)] List<Guid>? DrawingIds = null);

/// <summary>
/// A clip to paste, and where. With X and Y everything keeps its place relative to the rest, the top-left at X and Y; an
/// item joins its pasted bucket, or else the bucket it lands in. With a bucketId (cards only) they go into free spots in that
/// bucket. With neither, cards go into the board's first bucket, and a clip with buckets, images or drawings to a free spot.
/// </summary>
/// <param name="Clip">The clip from copyItems, as it was.</param>
/// <param name="X">Where the left edge of the leftmost item goes; give it with Y.</param>
/// <param name="Y">Where the top edge of the topmost item goes; give it with X.</param>
/// <param name="BucketId">A bucket on this board to put the cards in, instead of X and Y; only for a clip of cards alone.</param>
public record PasteItemsRequest(
    [property: Required] BoardClip Clip,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null,
    Guid? BucketId = null);

/// <summary>What a paste added to the board.</summary>
/// <param name="Cards">The new cards, in the clip's order.</param>
/// <param name="Images">The new images: on those cards, or on the board.</param>
/// <param name="Drawings">The new drawings: on those cards, or on the board.</param>
/// <param name="Bucket">The bucket the cards went into when one was asked for (it may have grown); null otherwise.</param>
/// <param name="Buckets">The new buckets, back to front, on top of the board's others.</param>
public record PastedItems(
    List<CardDetail> Cards, List<ImageDetail> Images, List<DrawingDetail> Drawings, BucketDetail? Bucket, List<BucketDetail> Buckets);
