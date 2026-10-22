using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>
/// Cards copied from a board, from copyItems and for pasteItems: everything on them in one self-contained piece of JSON, so
/// it can be pasted on any board, on this PrikBoard server or another one. The web app puts it on the clipboard. Like an
/// export (exportBoard) it holds content, not people; ids in it are only meaningful inside it.
/// </summary>
/// <param name="Format">Always "prikboard-clip".</param>
/// <param name="Version">Version of this format, currently 1. A clip with a newer version, or with fields the server doesn't know, is refused.</param>
/// <param name="CopiedAt">When it was copied (UTC).</param>
/// <param name="Source">Where the cards were copied from.</param>
/// <param name="Cards">The cards, the most recently edited last (they are drawn on top).</param>
/// <param name="Images">The images attached to those cards, with their bytes.</param>
/// <param name="Drawings">The drawings on those cards, with their strokes, oldest first.</param>
/// <param name="TagColors">Colors of the cards' tags that have one (not Default), by tag name. Tags new to the board get them.</param>
/// <param name="AppVersion">The PrikBoard version that made the clip, named when another server can't read it.</param>
public record BoardClip(
    [property: Required] string Format,
    [property: Range(1, int.MaxValue)] int Version,
    DateTimeOffset CopiedAt,
    [property: Required] ClipSource Source,
    [property: Required, MinLength(1), MaxLength(BoardClip.MaxCards)] List<ClippedCard> Cards,
    [property: Required, MaxLength(BoardClip.MaxImages)] List<ExportedImage> Images,
    [property: Required, MaxLength(BoardClip.MaxDrawings)] List<ExportedDrawing> Drawings,
    Dictionary<string, TagColor>? TagColors = null,
    [property: StringLength(100)] string? AppVersion = null)
{
    public const string FormatName = "prikboard-clip";
    /// <summary>Raise it with every change to the clip's fields, so an older server refuses a clip it can't fully read.</summary>
    public const int CurrentVersion = 1;
    public const int MaxCards = 200;
    public const int MaxImages = 100;
    public const int MaxDrawings = 1000;
    /// <summary>The most image bytes one clip holds; they travel as base64 through the clipboard.</summary>
    public const long MaxImageBytes = 40 * 1024 * 1024;
}

/// <summary>Where the cards in a clip come from.</summary>
/// <param name="Url">The PrikBoard server they were copied on, e.g. https://prikboard.example.com. Card links in the clip are paths on it.</param>
/// <param name="BoardShortId">The short id of their board.</param>
/// <param name="BoardKey">The key of their board, as in readable ids (MPCT-415).</param>
public record ClipSource(
    [property: Required, StringLength(2000)] string Url,
    [property: Required, StringLength(Board.ShortIdLength, MinimumLength = Board.ShortIdLength)] string BoardShortId,
    [property: Required, StringLength(Board.KeyMaxLength)] string BoardKey);

/// <summary>A card in a clip.</summary>
/// <param name="Id">The card's id on its board: images and drawings refer to their card by it, and links between the copied cards follow it.</param>
/// <param name="Number">The card's number on its board (readable id and path).</param>
/// <param name="Title">Short title; may be empty.</param>
/// <param name="Color">Card fill.</param>
/// <param name="Tags">Tag names.</param>
/// <param name="X">Left edge on its board. Pasted cards keep their places relative to each other.</param>
/// <param name="Y">Top edge on its board.</param>
/// <param name="Components">The card's content on its own canvas, in order. Links to other cards are paths on the source server.</param>
/// <param name="AssigneeIds">Principal ids of its assignees; a paste keeps only those who can be assigned on the board it lands on.</param>
public record ClippedCard(
    Guid Id,
    [property: Range(1, int.MaxValue)] int Number,
    [property: Required(AllowEmptyStrings = true), StringLength(200)] string Title,
    CardColor Color,
    [property: Required, MaxLength(10)] List<string> Tags,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int X,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int Y,
    [property: MaxLength(CardComponent.MaxComponents)] List<NewCardComponent>? Components = null,
    [property: MaxLength(Endpoints.CardEndpoints.MaxAssignees)] List<Guid>? AssigneeIds = null);

/// <summary>Which cards to copy.</summary>
/// <param name="CardIds">The cards, all on the board in the path.</param>
public record CopyItemsRequest(
    [property: Required, MinLength(1), MaxLength(BoardClip.MaxCards)] List<Guid> CardIds);

/// <summary>
/// A clip to paste, and where. With X and Y the cards keep their places relative to each other, the top-left one at X and Y,
/// each in the bucket it lands in. With a bucketId they go into free spots in that bucket. With neither, into the board's
/// first bucket.
/// </summary>
/// <param name="Clip">The clip from copyItems, as it was.</param>
/// <param name="X">Where the left edge of the leftmost card goes; give it with Y.</param>
/// <param name="Y">Where the top edge of the topmost card goes; give it with X.</param>
/// <param name="BucketId">A bucket on this board to put the cards in, instead of X and Y.</param>
public record PasteItemsRequest(
    [property: Required] BoardClip Clip,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null,
    Guid? BucketId = null);

/// <summary>What a paste added to the board.</summary>
/// <param name="Cards">The new cards, in the clip's order.</param>
/// <param name="Images">The images attached to them.</param>
/// <param name="Drawings">The drawings on them.</param>
/// <param name="Bucket">The bucket they went into when one was asked for (it may have grown); null otherwise.</param>
public record PastedItems(List<CardDetail> Cards, List<ImageDetail> Images, List<DrawingDetail> Drawings, BucketDetail? Bucket);
