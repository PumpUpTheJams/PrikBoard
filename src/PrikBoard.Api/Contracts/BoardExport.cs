using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>
/// A board as a single self-contained file, from exportBoard and for importBoard. It holds the content of the board (name,
/// agent rules, buckets, tags, notes and images) and nothing about people: no members, roles or user ids. Ids in the file are
/// local to it; importing makes new ones.
/// </summary>
/// <param name="Format">Always "prikboard-board". Marks the file as a PrikBoard export.</param>
/// <param name="Version">Version of this file format, currently 2. Version 1 files (notes with a body, checklist and links instead of blocks) still import; a file with a newer version than the server knows is refused.</param>
/// <param name="ExportedAt">When the file was made (UTC).</param>
/// <param name="Name">Board name.</param>
/// <param name="Description">Board description, or null.</param>
/// <param name="Rules">The board's agent rules in Markdown, or null. Importing makes the importer the owner, who may change them.</param>
/// <param name="Buckets">Buckets in stacking order, back to front.</param>
/// <param name="Tags">Names of the board's tags, including ones no note uses.</param>
/// <param name="Notes">Notes, the most recently edited last (they are drawn on top). A note's bucket follows from where it sits.</param>
/// <param name="Images">Images on the canvas and attached to notes, with their bytes.</param>
/// <param name="TagColors">Colours of the tags that have one (not Default), by tag name. Optional; files without it import with default colours.</param>
public record BoardExport(
    [property: Required] string Format,
    [property: Range(1, int.MaxValue)] int Version,
    DateTimeOffset ExportedAt,
    [property: Required, StringLength(100, MinimumLength = 1)] string Name,
    [property: StringLength(1000)] string? Description,
    [property: StringLength(Board.RulesMaxLength)] string? Rules,
    [property: Required, MaxLength(BoardExport.MaxBuckets)] List<ExportedBucket> Buckets,
    [property: Required, MaxLength(BoardTag.MaxPerBoard)] List<string> Tags,
    [property: Required, MaxLength(BoardExport.MaxNotes)] List<ExportedNote> Notes,
    [property: Required, MaxLength(BoardExport.MaxImages)] List<ExportedImage> Images,
    Dictionary<string, TagColor>? TagColors = null)
{
    public const string FormatName = "prikboard-board";
    public const int CurrentVersion = 2;
    public const int MaxBuckets = 100;
    public const int MaxNotes = 2000;
    public const int MaxImages = 200;
}

/// <summary>A bucket in an export.</summary>
/// <param name="Name">Bucket name.</param>
/// <param name="Color">Outline colour.</param>
/// <param name="X">Left edge in canvas pixels.</param>
/// <param name="Y">Top edge in canvas pixels.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public record ExportedBucket(
    [property: Required, StringLength(100, MinimumLength = 1)] string Name,
    BucketColor Color,
    [property: Range(0, Layout.MaxCoordinate)] int X,
    [property: Range(0, Layout.MaxCoordinate)] int Y,
    [property: Range(Layout.MinBucketWidth, Layout.MaxBucketSize)] int Width,
    [property: Range(Layout.MinBucketHeight, Layout.MaxBucketSize)] int Height);

/// <summary>
/// A note in an export. Who created it and who is assigned are not part of the file. Version 2 files hold the note's
/// content as Blocks; version 1 files have Body, Checklist and Links instead, which become a Text, a Checklist and a
/// Links block on import. The fields of the other version are ignored.
/// </summary>
/// <param name="Id">An id that is only meaningful inside this file: images refer to their note by it, and links to other notes in the file are kept pointing at the right note.</param>
/// <param name="Title">Short title.</param>
/// <param name="Color">Post-it fill.</param>
/// <param name="Tags">Tag names.</param>
/// <param name="X">Left edge in canvas pixels.</param>
/// <param name="Y">Top edge in canvas pixels.</param>
/// <param name="Blocks">Version 2: the note's content on its own canvas, in order. Web links are kept; a link to a note outside the file is dropped on import.</param>
/// <param name="Body">Version 1 only: longer details, or null.</param>
/// <param name="Checklist">Version 1 only: checklist items.</param>
/// <param name="Links">Version 1 only: links. Web addresses are kept; a link to a note outside the file is dropped on import.</param>
public record ExportedNote(
    Guid Id,
    [property: Required, StringLength(200, MinimumLength = 1)] string Title,
    NoteColor Color,
    [property: Required, MaxLength(10)] List<string> Tags,
    [property: Range(0, Layout.MaxCoordinate)] int X,
    [property: Range(0, Layout.MaxCoordinate)] int Y,
    [property: MaxLength(NoteBlock.MaxBlocks)] List<NewNoteBlock>? Blocks = null,
    [property: StringLength(NoteBlock.MaxTextLength)] string? Body = null,
    [property: MaxLength(NoteBlock.MaxItems)] List<ChecklistItem>? Checklist = null,
    [property: MaxLength(NoteBlock.MaxLinks)] List<NoteLink>? Links = null);

/// <summary>An image in an export.</summary>
/// <param name="NoteId">Id of the note (in this file) the image is attached to, or null for an image on the canvas.</param>
/// <param name="FileName">Original file name.</param>
/// <param name="Caption">Caption, or null.</param>
/// <param name="X">Left edge on the board's canvas, or on the note's canvas for an attached image. Version 1 files ignore it for attached images, which are laid out under the note's blocks.</param>
/// <param name="Y">Top edge on the board's canvas, or on the note's canvas for an attached image.</param>
/// <param name="Width">Display width in pixels; the height follows from the aspect ratio.</param>
/// <param name="Data">The image file itself, as base64 in JSON. The real format is detected on import (PNG, JPEG, GIF or WebP, at most 10 MB).</param>
public record ExportedImage(
    Guid? NoteId,
    [property: Required, StringLength(255)] string FileName,
    [property: StringLength(500)] string? Caption,
    [property: Range(0, Layout.MaxCoordinate)] int X,
    [property: Range(0, Layout.MaxCoordinate)] int Y,
    [property: Range(Layout.MinImageWidth, Layout.MaxImageWidth)] int Width,
    [property: Required] byte[] Data);
