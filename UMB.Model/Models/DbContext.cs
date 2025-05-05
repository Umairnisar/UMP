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

            // User constraints
            modelBuilder.Entity<User>()
                .HasIndex(u => u.Email)
                .IsUnique();

            // PlatformAccount constraints
            modelBuilder.Entity<PlatformAccount>()
                .HasOne(pa => pa.User)
                .WithMany(u => u.PlatformAccounts)
                .HasForeignKey(pa => pa.UserId);

            modelBuilder.Entity<PlatformAccount>()
                .HasIndex(pa => new { pa.UserId, pa.PlatformType, pa.AccountIdentifier })
                .IsUnique();

            // MessageMetadata constraints
            modelBuilder.Entity<MessageMetadata>()
                .HasOne(mm => mm.User)
                .WithMany()
                .HasForeignKey(mm => mm.UserId);

            // TextProcessingLog constraints
            modelBuilder.Entity<TextProcessingLog>()
                .HasOne(tpl => tpl.User)
                .WithMany()
                .HasForeignKey(tpl => tpl.UserId);

            // MessageAttachment constraints
            modelBuilder.Entity<MessageAttachment>()
                .HasOne(ma => ma.Message)
                .WithMany(mm => mm.Attachments)
                .HasForeignKey(ma => ma.MessageMetadataId)
                .OnDelete(DeleteBehavior.Cascade);

            // WhatsAppConnection constraints
            modelBuilder.Entity<WhatsAppConnection>()
                .HasKey(e => e.Id);

            modelBuilder.Entity<WhatsAppConnection>()
                .Property(e => e.PhoneNumberId).IsRequired().HasMaxLength(100);

            modelBuilder.Entity<WhatsAppConnection>()
                .Property(e => e.AccessToken).IsRequired().HasMaxLength(500);

            modelBuilder.Entity<WhatsAppConnection>()
                .Property(e => e.PhoneNumber).IsRequired().HasMaxLength(20);

            modelBuilder.Entity<WhatsAppConnection>()
                .Property(e => e.BusinessName).HasMaxLength(100);

            modelBuilder.Entity<WhatsAppConnection>()
                .Property(e => e.IsConnected).HasDefaultValue(true);

            // Unique index on UserId and PhoneNumber
            modelBuilder.Entity<WhatsAppConnection>()
                .HasIndex(e => new { e.UserId, e.PhoneNumber })
                .IsUnique();

            // Index on PhoneNumberId for faster webhook lookups
            modelBuilder.Entity<WhatsAppConnection>()
                .HasIndex(e => e.PhoneNumberId);

            // Configure relationship with User
            modelBuilder.Entity<WhatsAppConnection>()
                .HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // PlatformMessageSync constraints
            modelBuilder.Entity<PlatformMessageSync>()
                .HasKey(e => e.Id);

            modelBuilder.Entity<PlatformMessageSync>()
                .Property(e => e.PlatformType).IsRequired().HasMaxLength(50);

            // Create unique index for user + platform combination
            modelBuilder.Entity<PlatformMessageSync>()
                .HasIndex(e => new { e.UserId, e.PlatformType })
                .IsUnique();

            modelBuilder.Entity<PlatformMessageSync>()
                .HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
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