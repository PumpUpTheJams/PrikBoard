using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>
/// A board as a single self-contained file, from exportBoard and for importBoard. It holds the content of the board (name,
/// agent rules, buckets, tags, cards, images and drawings) and nothing about people: no members, roles or user ids. Ids in the file are
/// local to it; importing makes new ones.
/// </summary>
/// <param name="Format">Always "prikboard-board". Marks the file as a PrikBoard export.</param>
/// <param name="Version">Version of this file format, currently 6 (3 to 5 still import). Files from before notes were renamed cards (versions 1 and 2) are refused, and so is a file with a newer version than the server knows, or with fields it doesn't know.</param>
/// <param name="ExportedAt">When the file was made (UTC).</param>
/// <param name="Name">Board name.</param>
/// <param name="Description">Board description, or null.</param>
/// <param name="Rules">The board's agent rules in Markdown, or null. Importing makes the importer the owner, who may change them.</param>
/// <param name="Buckets">Buckets in list order (To do, Doing, Done), each with its place in the stack. Files before version 5 list them back to front, and that is their list order too.</param>
/// <param name="Tags">Names of the board's tags, including ones no card uses.</param>
/// <param name="Cards">Cards, the most recently edited last (they are drawn on top). A card's bucket follows from where it sits.</param>
/// <param name="Images">Images on the canvas and attached to cards, with their bytes.</param>
/// <param name="TagColors">Colors of the tags that have one (not Default), by tag name. Optional; files without it import with default colors.</param>
/// <param name="Drawings">Freehand drawings on the canvas and on cards, with their strokes, oldest first. Optional; files without it import without drawings.</param>
/// <param name="Key">The board's key, shown before card numbers (MPCT-415). Optional; without a valid one the new board takes one from its name.</param>
/// <param name="AppVersion">The PrikBoard version that made the file (e.g. 0.0.27), named when another server can't read it. Optional.</param>
public record BoardExport(
    [property: Required] string Format,
    [property: Range(BoardExport.OldestVersion, int.MaxValue, ErrorMessage = BoardExport.TooOldMessage)] int Version,
    DateTimeOffset ExportedAt,
    [property: Required, StringLength(100, MinimumLength = 1)] string Name,
    [property: StringLength(1000)] string? Description,
    [property: StringLength(Board.RulesMaxLength)] string? Rules,
    [property: Required, MaxLength(BoardExport.MaxBuckets)] List<ExportedBucket> Buckets,
    [property: Required, MaxLength(BoardTag.MaxPerBoard)] List<string> Tags,
    [property: Required, MaxLength(BoardExport.MaxCards)] List<ExportedCard> Cards,
    [property: Required, MaxLength(BoardExport.MaxImages)] List<ExportedImage> Images,
    Dictionary<string, TagColor>? TagColors = null,
    [property: MaxLength(BoardDrawing.MaxPerBoard)] List<ExportedDrawing>? Drawings = null,
    [property: StringLength(Board.KeyMaxLength)] string? Key = null,
    [property: StringLength(100)] string? AppVersion = null)
{
    public const string FormatName = "prikboard-board";
    /// <summary>Raise it with every change to the file's fields, so an older server refuses a file it can't fully read.</summary>
    public const int CurrentVersion = 6;
    public const int OldestVersion = 3;
    public const string TooOldMessage =
        "This file is from before notes were renamed cards (version 1 or 2), and this server reads version 3 and later. " +
        "Export the board again from this server to get a file it can import.";
    public const int MaxBuckets = 100;
    public const int MaxCards = 2000;
    public const int MaxImages = 200;
}

/// <summary>A bucket in an export.</summary>
/// <param name="Name">Bucket name.</param>
/// <param name="Color">Outline color.</param>
/// <param name="X">Left edge in canvas pixels.</param>
/// <param name="Y">Top edge in canvas pixels.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Collapsed">Folded up on the board, hiding its cards. Optional.</param>
/// <param name="Stack">Its place in the stack where buckets overlap, 0 at the back. Optional; without it buckets stack in file order.</param>
public record ExportedBucket(
    [property: Required, StringLength(100, MinimumLength = 1)] string Name,
    BucketColor Color,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int X,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int Y,
    [property: Range(Layout.MinBucketWidth, Layout.MaxBucketSize)] int Width,
    [property: Range(Layout.MinBucketHeight, Layout.MaxBucketSize)] int Height,
    bool Collapsed = false,
    [property: Range(0, BoardExport.MaxBuckets - 1)] int? Stack = null);

/// <summary>A card in an export. Who created it and who is assigned are not part of the file.</summary>
/// <param name="Id">An id that is only meaningful inside this file: images refer to their card by it, and links to other cards in the file are kept pointing at the right card.</param>
/// <param name="Title">Short title; may be empty.</param>
/// <param name="Color">Card fill.</param>
/// <param name="Tags">Tag names.</param>
/// <param name="X">Left edge in canvas pixels.</param>
/// <param name="Y">Top edge in canvas pixels.</param>
/// <param name="Components">The card's content on its own canvas, in order. Web links are kept; a link to a card outside the file is dropped on import.</param>
/// <param name="Number">The card's number on its board. Optional: when every card in the file has a different one, the imported cards keep them; otherwise they are numbered in file order.</param>
public record ExportedCard(
    Guid Id,
    [property: Required(AllowEmptyStrings = true), StringLength(200)] string Title,
    CardColor Color,
    [property: Required, MaxLength(10)] List<string> Tags,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int X,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int Y,
    [property: MaxLength(CardComponent.MaxComponents)] List<NewCardComponent>? Components = null,
    [property: Range(1, int.MaxValue)] int? Number = null);

/// <summary>An image in an export.</summary>
/// <param name="CardId">Id of the card (in this file) the image is attached to, or null for an image on the canvas.</param>
/// <param name="FileName">Original file name.</param>
/// <param name="Caption">Caption, or null.</param>
/// <param name="X">Left edge on the board's canvas, or on the card's canvas for an attached image.</param>
/// <param name="Y">Top edge on the board's canvas, or on the card's canvas for an attached image.</param>
/// <param name="Width">Display width in pixels; the height follows from the aspect ratio.</param>
/// <param name="Data">The image file itself, as base64 in JSON. The real format is detected on import (PNG, JPEG, GIF or WebP, at most 10 MB).</param>
public record ExportedImage(
    Guid? CardId,
    [property: Required, StringLength(255)] string FileName,
    [property: StringLength(500)] string? Caption,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int X,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int Y,
    [property: Range(Layout.MinImageWidth, Layout.MaxImageWidth)] int Width,
    [property: Required] byte[] Data);

/// <summary>A drawing in an export.</summary>
/// <param name="CardId">Id of the card (in this file) the drawing is on, or null for a drawing on the canvas.</param>
/// <param name="X">Left edge on the board's canvas, or on the card's canvas; the points are relative to it.</param>
/// <param name="Y">Top edge on the board's canvas, or on the card's canvas; the points are relative to it.</param>
/// <param name="Strokes">The strokes in drawing order (from version 6).</param>
/// <param name="Color">Files before version 6, where a drawing was one stroke: its ink. Null from version 6.</param>
/// <param name="StrokeWidth">Files before version 6: the stroke's base line width. Null from version 6.</param>
/// <param name="Points">Files before version 6: the stroke's points. Null from version 6.</param>
public record ExportedDrawing(
    Guid? CardId,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int X,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int Y,
    [property: MaxLength(BoardDrawing.MaxStrokes)] List<ExportedStroke>? Strokes = null,
    InkColor? Color = null,
    int? StrokeWidth = null,
    [property: MaxLength(BoardDrawing.MaxPoints)] List<double[]>? Points = null)
{
    /// <summary>The strokes, from either form.</summary>
    public IReadOnlyList<(InkColor Color, int Width, IReadOnlyList<double[]> Points)> AllStrokes() =>
        Strokes is not null
            ? [.. Strokes.Select(s => (s.Color, s.StrokeWidth, (IReadOnlyList<double[]>)s.Points))]
            : Points is not null ? [(Color ?? InkColor.Chalk, StrokeWidth ?? 4, Points)] : [];
}

/// <summary>One stroke of an exported drawing.</summary>
/// <param name="Color">Ink color.</param>
/// <param name="StrokeWidth">Base line width: 2, 4 or 8.</param>
/// <param name="Points">The stroke, each point [x, y, pressure] relative to the drawing's x and y, as in createDrawing.</param>
public record ExportedStroke(
    InkColor Color,
    int StrokeWidth,
    [property: Required, MaxLength(BoardDrawing.MaxPoints)] List<double[]> Points);
