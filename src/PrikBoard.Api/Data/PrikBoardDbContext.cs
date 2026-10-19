using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

public class PrikBoardDbContext(DbContextOptions<PrikBoardDbContext> options) : DbContext(options)
{
    public DbSet<Board> Boards => Set<Board>();
    public DbSet<Bucket> Buckets => Set<Bucket>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<BoardTag> BoardTags => Set<BoardTag>();
    public DbSet<Principal> Principals => Set<Principal>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<UserIdentity> UserIdentities => Set<UserIdentity>();
    public DbSet<BoardMember> BoardMembers => Set<BoardMember>();
    public DbSet<BoardInvite> BoardInvites => Set<BoardInvite>();
    public DbSet<AccessToken> AccessTokens => Set<AccessToken>();
    public DbSet<AppSignInGrant> AppSignInGrants => Set<AppSignInGrant>();
    public DbSet<BoardImage> Images => Set<BoardImage>();
    public DbSet<ImageContent> ImageContents => Set<ImageContent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Board>(board =>
        {
            board.Property(b => b.Name).HasMaxLength(100);
            board.Property(b => b.Description).HasMaxLength(1000);
            board.Property(b => b.Rules).HasMaxLength(Board.RulesMaxLength);
            board.HasMany(b => b.Buckets)
                .WithOne(c => c.Board)
                .HasForeignKey(c => c.BoardId)
                .OnDelete(DeleteBehavior.Cascade);
            board.HasMany(b => b.Notes)
                .WithOne(n => n.Board)
                .HasForeignKey(n => n.BoardId)
                .OnDelete(DeleteBehavior.Cascade);
            board.HasMany(b => b.Tags)
                .WithOne(t => t.Board)
                .HasForeignKey(t => t.BoardId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BoardTag>(tag =>
        {
            tag.Property(t => t.Name).HasMaxLength(NoteTags.MaxLength);
            tag.HasIndex(t => new { t.BoardId, t.Name }).IsUnique();
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
            note.Property(n => n.Color).HasConversion<string>().HasMaxLength(20);
            // Lists that are always read and replaced whole live on the note: arrays, or JSON columns.
            note.PrimitiveCollection(n => n.Tags).IsRequired();
            note.PrimitiveCollection(n => n.AssigneeIds).IsRequired();
            note.Property(n => n.Blocks).HasColumnType("jsonb").HasConversion(NoteBlocksJson.Converter, NoteBlocksJson.Comparer);
            // Deleting a bucket leaves its notes on the board as loose notes.
            note.HasOne<Bucket>()
                .WithMany()
                .HasForeignKey(n => n.BucketId)
                .OnDelete(DeleteBehavior.SetNull);
            note.HasIndex(n => n.BoardId);
            note.HasOne(n => n.CreatedBy).WithMany().HasForeignKey(n => n.CreatedById).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BoardImage>(image =>
        {
            image.Property(i => i.FileName).HasMaxLength(255);
            image.Property(i => i.ContentType).HasMaxLength(50);
            image.Property(i => i.Caption).HasMaxLength(500);
            image.HasOne(i => i.AddedBy).WithMany().HasForeignKey(i => i.AddedById).OnDelete(DeleteBehavior.SetNull);
            image.HasOne(i => i.Board).WithMany(b => b.Images).HasForeignKey(i => i.BoardId).OnDelete(DeleteBehavior.Cascade);
            // An attached image goes when its note goes.
            image.HasOne<Note>().WithMany().HasForeignKey(i => i.NoteId).OnDelete(DeleteBehavior.Cascade);
            image.HasOne(i => i.Content).WithOne().HasForeignKey<ImageContent>(c => c.ImageId).OnDelete(DeleteBehavior.Cascade);
            image.HasIndex(i => i.BoardId);
            image.HasIndex(i => i.NoteId);
        });

        modelBuilder.Entity<ImageContent>(content => content.HasKey(c => c.ImageId));

        // Users and agents share one table, so memberships, invites and images point at a single kind of id.
        modelBuilder.Entity<Principal>(principal =>
        {
            principal.ToTable("Principals");
            principal.HasDiscriminator<string>("Kind").HasValue<User>("User").HasValue<Agent>("Agent");
            principal.Property<string>("Kind").HasMaxLength(10);
            principal.Property(p => p.DisplayName).HasMaxLength(100);
            principal.Ignore(p => p.FallbackName);
            principal.Ignore(p => p.ShownName);
        });

        modelBuilder.Entity<User>(user =>
            user.HasMany(u => u.Identities).WithOne(i => i.User).HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.Cascade));

        modelBuilder.Entity<Agent>(agent =>
        {
            agent.Property(a => a.Issuer).HasMaxLength(50);
            agent.Property(a => a.ClientId).HasMaxLength(255);
            agent.HasIndex(a => new { a.Issuer, a.ClientId }).IsUnique().HasFilter("\"Kind\" = 'Agent'");
            // Keeps the agent (and the name on what it did) when its owner goes; their tokens go with them.
            agent.HasOne<User>().WithMany().HasForeignKey(a => a.OwnerId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AccessToken>(token =>
        {
            token.Property(t => t.TokenHash).HasMaxLength(32);
            token.HasIndex(t => t.TokenHash).IsUnique();
            token.Property(t => t.Hint).HasMaxLength(8);
            token.Property(t => t.MaxRole).HasConversion<string>().HasMaxLength(10);
            token.Property(t => t.Kind).HasConversion<string>().HasMaxLength(10);
            token.Property(t => t.Name).HasMaxLength(100);
            token.HasOne(t => t.Owner).WithMany().HasForeignKey(t => t.OwnerId).OnDelete(DeleteBehavior.Cascade);
            token.HasOne(t => t.Agent).WithMany().HasForeignKey(t => t.AgentId).OnDelete(DeleteBehavior.Cascade);
            token.HasIndex(t => t.OwnerId);
        });

        modelBuilder.Entity<AppSignInGrant>(grant =>
        {
            grant.Property(g => g.CodeHash).HasMaxLength(32);
            grant.HasIndex(g => g.CodeHash).IsUnique();
            grant.Property(g => g.CodeChallenge).HasMaxLength(64);
            grant.Property(g => g.Name).HasMaxLength(100);
            grant.HasOne(g => g.Owner).WithMany().HasForeignKey(g => g.OwnerId).OnDelete(DeleteBehavior.Cascade);
            grant.HasIndex(g => g.OwnerId);
        });

        modelBuilder.Entity<UserIdentity>(identity =>
        {
            identity.Property(i => i.Provider).HasMaxLength(50);
            identity.Property(i => i.SubjectHash).HasMaxLength(32);
            identity.Property(i => i.EmailHash).HasMaxLength(32);
            identity.HasIndex(i => new { i.Provider, i.SubjectHash }).IsUnique();
            identity.HasIndex(i => i.EmailHash);
        });

        modelBuilder.Entity<BoardMember>(member =>
        {
            member.HasKey(m => new { m.BoardId, m.PrincipalId });
            member.Property(m => m.Role).HasConversion<string>().HasMaxLength(10);
            member.HasOne(m => m.Board).WithMany(b => b.Members).HasForeignKey(m => m.BoardId).OnDelete(DeleteBehavior.Cascade);
            member.HasOne(m => m.Principal).WithMany().HasForeignKey(m => m.PrincipalId).OnDelete(DeleteBehavior.Cascade);
            member.HasIndex(m => m.PrincipalId);
        });

        modelBuilder.Entity<BoardInvite>(invite =>
        {
            invite.Property(i => i.Role).HasConversion<string>().HasMaxLength(10);
            invite.Property(i => i.TokenHash).HasMaxLength(32);
            invite.HasIndex(i => i.TokenHash).IsUnique();
            invite.HasIndex(i => i.BoardId);
            invite.HasOne<Board>().WithMany().HasForeignKey(i => i.BoardId).OnDelete(DeleteBehavior.Cascade);
            invite.HasOne(i => i.CreatedBy).WithMany().HasForeignKey(i => i.CreatedById).OnDelete(DeleteBehavior.SetNull);
            invite.HasOne<Principal>().WithMany().HasForeignKey(i => i.AcceptedById).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
