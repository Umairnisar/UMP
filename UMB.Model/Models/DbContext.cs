using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace UMB.Model.Models
{
    public class AppDbContext : DbContext
    {
        public AppDbContext() { }
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<PlatformAccount> PlatformAccounts { get; set; }
        public DbSet<MessageMetadata> MessageMetadatas { get; set; }
        public DbSet<TextProcessingLog> TextProcessingLogs { get; set; }
        public DbSet<WhatsAppConnection> WhatsAppConnections { get; set; }
        public DbSet<PlatformMessageSync> PlatformMessageSyncs { get; set; }
        public DbSet<MessageAttachment> MessageAttachments { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Example constraints or relationships:
            modelBuilder.Entity<User>()
                .HasIndex(u => u.Email)
                .IsUnique();

            modelBuilder.Entity<PlatformAccount>()
                .HasOne(pa => pa.User)
                .WithMany(u => u.PlatformAccounts)
                .HasForeignKey(pa => pa.UserId);

            modelBuilder.Entity<MessageMetadata>()
                .HasOne(mm => mm.User)
                .WithMany()
                .HasForeignKey(mm => mm.UserId);

            modelBuilder.Entity<TextProcessingLog>()
                .HasOne(tpl => tpl.User)
                .WithMany()
                .HasForeignKey(tpl => tpl.UserId);

            modelBuilder.Entity<MessageAttachment>()
                .HasOne(ma => ma.Message)
                .WithMany(mm => mm.Attachments)
                .HasForeignKey(ma => ma.MessageMetadataId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<WhatsAppConnection>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.PhoneNumberId).IsRequired().HasMaxLength(100);
                entity.Property(e => e.AccessToken).IsRequired().HasMaxLength(500);
                entity.Property(e => e.PhoneNumber).IsRequired().HasMaxLength(20);
                entity.Property(e => e.BusinessName).HasMaxLength(100);
                entity.Property(e => e.IsConnected).HasDefaultValue(true);

                // Unique index on UserId since a user should only have one WhatsApp connection
                entity.HasIndex(e => e.UserId).IsUnique();

                // Index on PhoneNumberId for faster webhook lookups
                entity.HasIndex(e => e.PhoneNumberId);

                // Configure relationship with User
                entity.HasOne(e => e.User)
                      .WithOne()
                      .HasForeignKey<WhatsAppConnection>(e => e.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<PlatformMessageSync>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.PlatformType).IsRequired().HasMaxLength(50);

                // Create unique index for user + platform combination
                entity.HasIndex(e => new { e.UserId, e.PlatformType }).IsUnique();

                // Configure relationship with User
                entity.HasOne(e => e.User)
                      .WithMany()
                      .HasForeignKey(e => e.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }

    // Design-time factory for migrations
    public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
            // Temporary connection string for design-time (replace with your actual connection string)
            optionsBuilder.UseSqlServer("Server=77.68.54.17;Database=UnifiedMessagingDb;User ID=sa;Password=gaiweuva$21%aszx(&^;MultipleActiveResultSets=true; TrustServerCertificate=True");

            return new AppDbContext(optionsBuilder.Options);
        }
    }
}