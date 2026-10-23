using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

public class PrikBoardDbContext(DbContextOptions<PrikBoardDbContext> options) : DbContext(options)
{
    /// <summary>
    /// The query filter that hides deleted cards, buckets, images, drawings and tags until they are restored or purged
    /// (<see cref="Tombstones"/>). Bypass it with <see cref="Tombstones.WithDeleted"/>; raw SQL never applies it.
    /// </summary>
    public const string SoftDelete = "SoftDelete";

    public DbSet<Board> Boards => Set<Board>();
    public DbSet<Bucket> Buckets => Set<Bucket>();
    public DbSet<Card> Cards => Set<Card>();
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
    public DbSet<ImageUpload> ImageUploads => Set<ImageUpload>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<BoardDrawing> Drawings => Set<BoardDrawing>();
    public DbSet<DrawingContent> DrawingContents => Set<DrawingContent>();
    public DbSet<CardChange> CardChanges => Set<CardChange>();
    public DbSet<DiscordGuild> DiscordGuilds => Set<DiscordGuild>();
    public DbSet<DiscordBoardChannels> DiscordBoardChannels => Set<DiscordBoardChannels>();
    public DbSet<DiscordBotSettings> DiscordBotSettings => Set<DiscordBotSettings>();
    public DbSet<DiscordAccount> DiscordAccounts => Set<DiscordAccount>();
    public DbSet<DiscordPreviewMessage> DiscordPreviewMessages => Set<DiscordPreviewMessage>();
    public DbSet<SignInProvider> SignInProviders => Set<SignInProvider>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Board>(board =>
        {
            board.Property(b => b.Name).HasMaxLength(100);
            board.Property(b => b.ShortId).HasMaxLength(Board.ShortIdLength);
            board.HasIndex(b => b.ShortId).IsUnique();
            board.Property(b => b.Key).HasMaxLength(Board.KeyMaxLength);
            board.Property(b => b.Description).HasMaxLength(1000);
            board.Property(b => b.Rules).HasMaxLength(Board.RulesMaxLength);
            board.HasMany(b => b.Buckets)
                .WithOne(c => c.Board)
                .HasForeignKey(c => c.BoardId)
                .OnDelete(DeleteBehavior.Cascade);
            board.HasMany(b => b.Cards)
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
            tag.Property(t => t.Name).HasMaxLength(CardTags.MaxLength);
            tag.Property(t => t.Color).HasConversion<string>().HasMaxLength(20);
            // A deleted tag keeps its name, so a new tag may take it; restoring the old one then fails.
            tag.HasIndex(t => new { t.BoardId, t.Name }).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
            tag.PrimitiveCollection(t => t.RemovedFromCardIds);
            tag.HasQueryFilter(SoftDelete, t => t.DeletedAt == null);
        });

        modelBuilder.Entity<Bucket>(bucket =>
        {
            bucket.Property(b => b.Name).HasMaxLength(100);
            bucket.HasIndex(b => b.BoardId);
            bucket.HasQueryFilter(SoftDelete, b => b.DeletedAt == null);
        });

        modelBuilder.Entity<Card>(card =>
        {
            card.Property(n => n.Title).HasMaxLength(200);
            card.Property(n => n.Color).HasConversion<string>().HasMaxLength(20);
            // Lists that are always read and replaced whole live on the card: arrays, or JSON columns.
            card.PrimitiveCollection(n => n.Tags).IsRequired();
            card.PrimitiveCollection(n => n.AssigneeIds).IsRequired();
            card.Property(n => n.Components).HasColumnType("jsonb").HasConversion(CardComponentsJson.Converter, CardComponentsJson.Comparer);
            // A card still in a bucket when the bucket is purged stays on the board, loose.
            card.HasOne<Bucket>()
                .WithMany()
                .HasForeignKey(n => n.BucketId)
                .OnDelete(DeleteBehavior.SetNull);
            card.HasIndex(n => n.BoardId);
            // Unfiltered, unlike the tag names: a deleted card keeps its number, and gets it back when restored.
            card.HasIndex(n => new { n.BoardId, n.Number }).IsUnique();
            card.HasQueryFilter(SoftDelete, n => n.DeletedAt == null);
            card.HasOne(n => n.CreatedBy).WithMany().HasForeignKey(n => n.CreatedById).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CardChange>(change =>
        {
            change.Property(c => c.Entries).HasColumnType("jsonb").HasConversion(CardChangesJson.Converter, CardChangesJson.Comparer);
            // The history goes when its card is purged; the name on it goes when its author does.
            change.HasOne<Card>().WithMany().HasForeignKey(c => c.CardId).OnDelete(DeleteBehavior.Cascade);
            change.HasOne(c => c.By).WithMany().HasForeignKey(c => c.ById).OnDelete(DeleteBehavior.SetNull);
            change.HasIndex(c => new { c.CardId, c.At });
            // The Discord bot follows the latest changes (Integrations/DiscordActivity).
            change.HasIndex(c => c.At);
        });

        modelBuilder.Entity<BoardImage>(image =>
        {
            image.Property(i => i.Caption).HasMaxLength(BoardImage.MaxCaptionLength);
            image.HasOne(i => i.AddedBy).WithMany().HasForeignKey(i => i.AddedById).OnDelete(DeleteBehavior.SetNull);
            image.HasOne(i => i.Board).WithMany(b => b.Images).HasForeignKey(i => i.BoardId).OnDelete(DeleteBehavior.Cascade);
            // An attached image goes when its card goes.
            image.HasOne<Card>().WithMany().HasForeignKey(i => i.CardId).OnDelete(DeleteBehavior.Cascade);
            // Like a card, an image in a purged bucket stays on the board, loose.
            image.HasOne<Bucket>().WithMany().HasForeignKey(i => i.BucketId).OnDelete(DeleteBehavior.SetNull);
            // Files are deleted by the sweep once nothing refers to them (FileStore.SweepAsync), never by a cascade.
            image.HasOne(i => i.File).WithOne().HasForeignKey<BoardImage>(i => i.FileId).OnDelete(DeleteBehavior.Restrict);
            image.HasIndex(i => i.BoardId);
            image.HasIndex(i => i.CardId);
            image.HasQueryFilter(SoftDelete, i => i.DeletedAt == null);
        });

        modelBuilder.Entity<StoredFile>(file =>
        {
            file.Property(f => f.FileName).HasMaxLength(StoredFile.MaxFileNameLength);
            file.Property(f => f.ContentType).HasMaxLength(50);
            file.Property(f => f.Sha256).HasMaxLength(32);
        });

        modelBuilder.Entity<BoardDrawing>(drawing =>
        {
            drawing.PrimitiveCollection(d => d.Colors).IsRequired().ElementType().HasConversion<string>();
            // A shortcut to Content.Strokes, not a column.
            drawing.Ignore(d => d.Strokes);
            drawing.HasOne(d => d.AddedBy).WithMany().HasForeignKey(d => d.AddedById).OnDelete(DeleteBehavior.SetNull);
            drawing.HasOne(d => d.Board).WithMany(b => b.Drawings).HasForeignKey(d => d.BoardId).OnDelete(DeleteBehavior.Cascade);
            // A drawing on a card goes when its card goes.
            drawing.HasOne<Card>().WithMany().HasForeignKey(d => d.CardId).OnDelete(DeleteBehavior.Cascade);
            // Like a card, a drawing in a purged bucket stays on the board, loose.
            drawing.HasOne<Bucket>().WithMany().HasForeignKey(d => d.BucketId).OnDelete(DeleteBehavior.SetNull);
            drawing.HasOne(d => d.Content).WithOne().HasForeignKey<DrawingContent>(c => c.DrawingId).OnDelete(DeleteBehavior.Cascade);
            drawing.HasIndex(d => d.BoardId);
            drawing.HasIndex(d => d.CardId);
            drawing.HasQueryFilter(SoftDelete, d => d.DeletedAt == null);
        });

        modelBuilder.Entity<DrawingContent>(content =>
        {
            content.HasKey(c => c.DrawingId);
            content.Property(c => c.Strokes).HasColumnType("jsonb").HasConversion(DrawingStrokesJson.Converter, DrawingStrokesJson.Comparer);
        });

        // Users and agents share one table, so memberships, invites and images point at a single kind of id.
        modelBuilder.Entity<Principal>(principal =>
        {
            principal.ToTable("Principals");
            principal.HasDiscriminator<string>("Kind").HasValue<User>("User").HasValue<Agent>("Agent");
            principal.Property<string>("Kind").HasMaxLength(10);
            principal.Property(p => p.DisplayName).HasMaxLength(100);
            principal.Ignore(p => p.FallbackName);
            principal.Ignore(p => p.ShownName);
            principal.HasOne<Board>().WithMany().HasForeignKey(p => p.LastBoardId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<User>(user =>
        {
            user.HasMany(u => u.Identities).WithOne(i => i.User).HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.Cascade);
            user.HasOne(u => u.AvatarFile).WithOne().HasForeignKey<User>(u => u.AvatarFileId).OnDelete(DeleteBehavior.Restrict);
        });

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
            grant.Property(g => g.CodeChallenge).HasMaxLength(AppSignInGrant.CodeChallengeLength);
            grant.Property(g => g.Name).HasMaxLength(100);
            grant.HasOne(g => g.Owner).WithMany().HasForeignKey(g => g.OwnerId).OnDelete(DeleteBehavior.Cascade);
            grant.HasIndex(g => g.OwnerId);
        });

        modelBuilder.Entity<DiscordGuild>(guild =>
        {
            guild.HasKey(g => g.GuildId);
            guild.Property(g => g.GuildId).HasMaxLength(DiscordGuild.IdMaxLength);
            guild.HasOne(g => g.Agent).WithOne().HasForeignKey<DiscordGuild>(g => g.AgentId).OnDelete(DeleteBehavior.Cascade);
            guild.HasOne<User>().WithMany().HasForeignKey(g => g.ConnectedById).OnDelete(DeleteBehavior.SetNull);
        });

        // Gone with the board or the server; a removed membership leaves it behind, and connecting again replaces it.
        modelBuilder.Entity<DiscordBoardChannels>(channels =>
        {
            channels.HasKey(c => new { c.GuildId, c.BoardId });
            channels.Property(c => c.GuildId).HasMaxLength(DiscordGuild.IdMaxLength);
            channels.Property(c => c.Channels).HasColumnType("jsonb").HasConversion(DiscordChannelsJson.Converter, DiscordChannelsJson.Comparer);
            channels.HasOne<DiscordGuild>().WithMany().HasForeignKey(c => c.GuildId).OnDelete(DeleteBehavior.Cascade);
            channels.HasOne<Board>().WithMany().HasForeignKey(c => c.BoardId).OnDelete(DeleteBehavior.Cascade);
            channels.HasIndex(c => c.BoardId);
        });

        // Only the hash of the Discord user id, like a sign-in's subject; gone with the user.
        modelBuilder.Entity<DiscordAccount>(account =>
        {
            account.HasKey(a => a.DiscordUserHash);
            account.Property(a => a.DiscordUserHash).HasMaxLength(32);
            account.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
            account.HasIndex(a => a.UserId);
        });

        // Discord's ids, not personal data; dropped after DiscordPreviewMessage.KeptFor (DiscordActivity).
        modelBuilder.Entity<DiscordPreviewMessage>(message =>
        {
            message.HasKey(m => m.MessageId);
            message.Property(m => m.MessageId).HasMaxLength(DiscordGuild.IdMaxLength);
            message.Property(m => m.GuildId).HasMaxLength(DiscordGuild.IdMaxLength);
            message.Property(m => m.ChannelId).HasMaxLength(DiscordGuild.IdMaxLength);
            message.PrimitiveCollection(m => m.Sources).IsRequired();
            message.PrimitiveCollection(m => m.CardIds).IsRequired();
            message.PrimitiveCollection(m => m.BoardIds).IsRequired();
            message.HasIndex(m => m.PostedAt);
        });

        modelBuilder.Entity<DiscordBotSettings>(bot =>
        {
            bot.Property(b => b.Id).ValueGeneratedNever();
            bot.Property(b => b.ApplicationId).HasMaxLength(DiscordGuild.IdMaxLength);
            bot.Property(b => b.BotName).HasMaxLength(100);
            bot.Property(b => b.PublicUrl).HasMaxLength(Domain.DiscordBotSettings.PublicUrlMaxLength);
            bot.HasOne<User>().WithMany().HasForeignKey(b => b.UpdatedById).OnDelete(DeleteBehavior.SetNull);
        });

        // Gone with its board, card, caller, member or token: an upload URL never outlives the access it was given for.
        modelBuilder.Entity<ImageUpload>(upload =>
        {
            upload.Property(u => u.TokenHash).HasMaxLength(32);
            upload.HasIndex(u => u.TokenHash).IsUnique();
            upload.Property(u => u.Caption).HasMaxLength(BoardImage.MaxCaptionLength);
            upload.Property(u => u.FileName).HasMaxLength(StoredFile.MaxFileNameLength);
            upload.Property(u => u.Cap).HasConversion<string>().HasMaxLength(10);
            upload.HasOne<Board>().WithMany().HasForeignKey(u => u.BoardId).OnDelete(DeleteBehavior.Cascade);
            upload.HasOne<Card>().WithMany().HasForeignKey(u => u.CardId).OnDelete(DeleteBehavior.Cascade);
            upload.HasOne<Principal>().WithMany().HasForeignKey(u => u.PrincipalId).OnDelete(DeleteBehavior.Cascade);
            upload.HasOne<Principal>().WithMany().HasForeignKey(u => u.MemberId).OnDelete(DeleteBehavior.Cascade);
            upload.HasOne<AccessToken>().WithMany().HasForeignKey(u => u.AccessTokenId).OnDelete(DeleteBehavior.Cascade);
            upload.HasIndex(u => u.PrincipalId);
        });

        modelBuilder.Entity<SignInProvider>(provider =>
        {
            provider.Property(p => p.Id).HasMaxLength(SignInProvider.IdMaxLength);
            provider.Property(p => p.Type).HasConversion<string>().HasMaxLength(20);
            provider.Property(p => p.DisplayName).HasMaxLength(SignInProvider.DisplayNameMaxLength);
            provider.Property(p => p.Authority).HasMaxLength(500);
            provider.Property(p => p.ClientId).HasMaxLength(500);
            provider.PrimitiveCollection(p => p.Scopes).IsRequired();
            provider.Property(p => p.GitHubAppUrl).HasMaxLength(500);
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
