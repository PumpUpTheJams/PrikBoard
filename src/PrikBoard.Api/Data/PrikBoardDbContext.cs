using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

public class PrikBoardDbContext(DbContextOptions<PrikBoardDbContext> options) : DbContext(options)
{
    public DbSet<Board> Boards => Set<Board>();
    public DbSet<Bucket> Buckets => Set<Bucket>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<User> Users => Set<User>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot compare or sort DateTimeOffset values stored as text; store them as binary instead.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Board>(board =>
        {
            board.Property(b => b.Name).HasMaxLength(100);
            board.Property(b => b.Description).HasMaxLength(1000);
            board.HasMany(b => b.Buckets)
                .WithOne(c => c.Board)
                .HasForeignKey(c => c.BoardId)
                .OnDelete(DeleteBehavior.Cascade);
            board.HasMany(b => b.Notes)
                .WithOne(n => n.Board)
                .HasForeignKey(n => n.BoardId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Bucket>(bucket =>
        {
            bucket.Property(b => b.Name).HasMaxLength(100);
            bucket.Property(b => b.Color).HasConversion<string>().HasMaxLength(20);
            bucket.HasIndex(b => b.BoardId);
        });

        modelBuilder.Entity<Note>(note =>
        {
            note.Property(n => n.Title).HasMaxLength(200);
            note.Property(n => n.Body).HasMaxLength(4000);
            note.Property(n => n.Color).HasConversion<string>().HasMaxLength(20);
            // Deleting a bucket leaves its notes on the board as loose notes.
            note.HasOne<Bucket>()
                .WithMany()
                .HasForeignKey(n => n.BucketId)
                .OnDelete(DeleteBehavior.SetNull);
            note.HasIndex(n => n.BoardId);
        });

        modelBuilder.Entity<User>(user =>
        {
            user.Property(u => u.Provider).HasMaxLength(50);
            user.Property(u => u.Subject).HasMaxLength(255);
            user.Property(u => u.Name).HasMaxLength(200);
            user.Property(u => u.Email).HasMaxLength(320);
            user.HasIndex(u => new { u.Provider, u.Subject }).IsUnique();
        });
    }
}
