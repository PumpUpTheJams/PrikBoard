using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

/// <summary>
/// Turns the notes cards still have into labels, once, at startup: Text entries in a card's components (jsonb), read as JSON since
/// no type holds them any more. Each becomes a label with a background in the note's place and width, its title a heading, and is
/// kept as a <see cref="NoteBackup"/>. A note with a scroll bar becomes a taller label, so what is below it on the card moves down.
/// Cards in the trash too, whose labels share their stamp. Nothing is recorded in the cards' history.
/// </summary>
public static class NoteMigration
{
    private const string TextKind = "Text";
    private const int Batch = 200;

    /// <summary>What a run did. PlainText counts notes whose Markdown labels refused, kept as plain text.</summary>
    public readonly record struct Report(int Cards, int Labels, int Empty, int PlainText);

    private sealed class Row
    {
        public Guid Id { get; set; }
        public Guid BoardId { get; set; }
        public Guid? CreatedById { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public string Components { get; set; } = "";
    }

    /// <summary>
    /// <see cref="RunAsync"/> at startup, until a run has finished: nothing writes notes any more, and looking for them reads every
    /// card (no index holds what is in the components).
    /// </summary>
    public static async Task<Report> RunOnceAsync(PrikBoardDbContext db, CancellationToken ct = default)
    {
        if (await db.DataMigrations.AnyAsync(m => m.Name == nameof(NoteMigration), ct)) return new();
        var report = await RunAsync(db, ct);
        db.DataMigrations.Add(new DataMigration { Name = nameof(NoteMigration) });
        await db.SaveChangesAsync(ct);
        return report;
    }

    /// <summary>Migrates every card with notes, each in a transaction of its own, so a run stopped halfway goes on from there next time.</summary>
    public static async Task<Report> RunAsync(PrikBoardDbContext db, CancellationToken ct = default)
    {
        var report = new Report();
        while (true)
        {
            var rows = await db.Database.SqlQuery<Row>($$"""
                SELECT "Id", "BoardId", "CreatedById", "DeletedAt", "Components"::text AS "Components"
                FROM "Cards" WHERE "Components" @> '[{"kind":"Text"}]'::jsonb LIMIT {{Batch}}
                """).ToListAsync(ct);
            if (rows.Count == 0) return report;
            foreach (var row in rows)
            {
                report = await MigrateCardAsync(db, row, report, ct);
                db.ChangeTracker.Clear();
            }
        }
    }

    private static async Task<Report> MigrateCardAsync(PrikBoardDbContext db, Row card, Report report, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var components = JsonNode.Parse(card.Components)!.AsArray();
        var images = await db.Images.WithDeleted().Where(i => i.CardId == card.Id).ToListAsync(ct);
        var drawings = await db.Drawings.WithDeleted().Where(d => d.CardId == card.Id).ToListAsync(ct);
        var labels = await db.Labels.WithDeleted().Where(l => l.CardId == card.Id).ToListAsync(ct);

        var notes = components.OfType<JsonObject>().Where(c => (string?)c["kind"] == TextKind)
            .OrderBy(c => Number(c, "y")).ThenBy(c => Number(c, "x")).ToList();
        var others = components.OfType<JsonObject>().Except(notes).ToList();
        var now = DateTimeOffset.UtcNow;
        var (added, empty, plain) = (0, 0, 0);

        foreach (var note in notes)
        {
            var (x, y, width, height) = (Number(note, "x"), Number(note, "y"), Number(note, "width"), Number(note, "height"));
            var (title, text) = ((string?)note["title"], (string?)note["text"]);
            var backup = new NoteBackup
            {
                Id = Guid.TryParse((string?)note["id"], out var id) ? id : Guid.CreateVersion7(),
                CardId = card.Id, BoardId = card.BoardId, Title = title, Text = text, X = x, Y = y, Width = width, Height = height, MigratedAt = now,
            };
            db.NoteBackups.Add(backup);

            var (blocks, wasPlain) = BlocksOf(title, text);
            if (blocks is null)
            {
                empty++;
                continue;
            }
            if (wasPlain) plain++;
            var label = new BoardLabel
            {
                BoardId = card.BoardId,
                CardId = card.Id,
                X = x,
                Y = y,
                Size = BoardLabel.CardTextSize,
                Background = true,
                AddedById = card.CreatedById,
                CreatedAt = now,
                UpdatedAt = now,
                DeletedAt = card.DeletedAt,
            };
            label.SetContent(blocks);
            label.SetWrap(width);
            label.Estimate();
            backup.LabelId = label.Id;
            db.Labels.Add(label);
            added++;

            // A note scrolled what didn't fit; a label shows it all, so what is below moves out of its way.
            var overflow = label.Height - height;
            if (overflow <= 0) continue;
            var by = (int)Math.Ceiling(overflow / (double)Layout.Grid) * Layout.Grid;
            bool Below(int itemX, int itemY, int itemWidth) => itemY >= y + height && itemX < x + label.Width && x < itemX + itemWidth;
            foreach (var other in others.Concat(notes.Where(n => Number(n, "y") >= y && n != note)))
            {
                if (Below(Number(other, "x"), Number(other, "y"), Number(other, "width"))) other["y"] = Number(other, "y") + by;
            }
            foreach (var image in images.Where(i => Below(i.X, i.Y, i.Width))) image.Y += by;
            foreach (var drawing in drawings.Where(d => Below(d.X, d.Y, d.Width))) drawing.Y += by;
            foreach (var other in labels.Where(l => Below(l.X, l.Y, l.Width))) other.Y += by;
        }

        await db.SaveChangesAsync(ct);
        var left = new JsonArray([.. others.Select(c => c.DeepClone())]).ToJsonString();
        await db.Database.ExecuteSqlAsync($"""UPDATE "Cards" SET "Components" = {left}::jsonb WHERE "Id" = {card.Id}""", ct);
        await transaction.CommitAsync(ct);
        return new(report.Cards + 1, report.Labels + added, report.Empty + empty, report.PlainText + plain);
    }

    /// <summary>A note's title and text as blocks, past the limits on new labels since this text is there already; null with no text at all.</summary>
    public static (List<LabelBlock>? Blocks, bool Plain) BlocksOf(string? title, string? text)
    {
        var (hasTitle, hasText) = (!string.IsNullOrWhiteSpace(title), !string.IsNullOrWhiteSpace(text));
        if (!hasTitle && !hasText) return (null, false);
        var markdown = LabelMarkdown.Titled(title, text);
        var (blocks, problem) = BoardLabel.Check(LabelMarkdown.Parse(markdown), maxTextLength: int.MaxValue);
        if (problem is null) return (blocks, false);
        // What Markdown can't say, say as it is: this can't refuse, as long as there is text.
        List<LabelBlock> plain = [.. markdown.Split("\n\n").Select(part => new LabelBlock(LabelBlockKind.Paragraph, [new LabelRun(part)]))];
        return (plain, true);
    }

    private static int Number(JsonObject component, string name) => (int?)component[name] ?? 0;
}
