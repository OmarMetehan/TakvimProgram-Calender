using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Data.Converters;

namespace Takvim.Data;

public class TakvimDbContext(DbContextOptions<TakvimDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Calendar> Calendars => Set<Calendar>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<EventCategory> EventCategories => Set<EventCategory>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<ChangeLogEntry> ChangeLog => Set<ChangeLogEntry>();
    public DbSet<WorkingHours> WorkingHours => Set<WorkingHours>();
    public DbSet<WorkLocationEntry> WorkLocations => Set<WorkLocationEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // NodaTime ve DateTimeOffset tiplerini sıralanabilir metne çeviren dönüştürücüler.
        // Nullable karşılıkları (LocalDateTime?, Instant?) da bu kayıtlara dahildir.
        configurationBuilder.Properties<LocalDateTime>()
            .HaveConversion<NodaTimeConverters.LocalDateTimeToStringConverter>();
        configurationBuilder.Properties<Instant>()
            .HaveConversion<NodaTimeConverters.InstantToStringConverter>();
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<NodaTimeConverters.DateTimeOffsetToStringConverter>();
        configurationBuilder.Properties<LocalDate>()
            .HaveConversion<NodaTimeConverters.LocalDateToStringConverter>();
        configurationBuilder.Properties<LocalTime>()
            .HaveConversion<NodaTimeConverters.LocalTimeToStringConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.Property(x => x.Locale).HasMaxLength(16);
        });

        modelBuilder.Entity<Calendar>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Color).HasMaxLength(32);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OwnerUserId, x.SortOrder });
            e.HasIndex(x => x.DeletedAt);
        });

        modelBuilder.Entity<Event>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.Uid).HasMaxLength(255);
            e.Property(x => x.ETag).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.LocationText).HasMaxLength(1000);
            e.Property(x => x.Color).HasMaxLength(32);
            e.Property(x => x.StartTimeZoneId).HasMaxLength(64);
            e.Property(x => x.EndTimeZoneId).HasMaxLength(64);
            e.Property(x => x.OnlineMeetingProvider).HasMaxLength(32);
            e.Property(x => x.SearchText).HasMaxLength(4000);

            e.HasOne(x => x.Calendar).WithMany(c => c.Events)
                .HasForeignKey(x => x.CalendarId).OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Organizer).WithMany()
                .HasForeignKey(x => x.OrganizerUserId).OnDelete(DeleteBehavior.SetNull);

            // Seri kökü ile istisnaları arasındaki kendine başvuran ilişki.
            e.HasOne(x => x.Series).WithMany(x => x.Exceptions)
                .HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);

            // Görünüm sorgularının tamamı bu indeksin üzerinden çalışır:
            // "şu takvimlerde, şu tarih aralığına değen, silinmemiş etkinlikler".
            e.HasIndex(x => new { x.CalendarId, x.StartUtc, x.EndUtc });

            // Bir seride aynı örneğin iki istisnası olamaz.
            e.HasIndex(x => new { x.SeriesId, x.RecurrenceId }).IsUnique();

            // ICS içe aktarımı ve CalDAV bir etkinliği UID ile bulur.
            e.HasIndex(x => new { x.CalendarId, x.Uid, x.RecurrenceId }).IsUnique();

            // Çöp kutusu ve süresiz seri taraması.
            e.HasIndex(x => x.DeletedAt);
            e.HasIndex(x => x.SeriesEndUtc);

            // Serbest metin arama; LIKE '%...%' indeks kullanamaz ama sütunu
            // dar tutmak tarama maliyetini düşürür.
            e.HasIndex(x => x.SearchText);
        });

        modelBuilder.Entity<Category>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Color).HasMaxLength(32);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OwnerUserId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<EventCategory>(e =>
        {
            e.HasKey(x => new { x.EventId, x.CategoryId });
            e.HasOne(x => x.Event).WithMany(x => x.Categories)
                .HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Category).WithMany(x => x.Events)
                .HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
            // Kategoriye tıklayınca anında süzme bu indeksi kullanır.
            e.HasIndex(x => x.CategoryId);
        });

        modelBuilder.Entity<Reminder>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Event).WithMany(x => x.Reminders)
                .HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.EventId);
        });

        modelBuilder.Entity<ChangeLogEntry>(e =>
        {
            e.HasKey(x => x.SyncToken);
            e.Property(x => x.SyncToken).ValueGeneratedOnAdd();
            e.Property(x => x.EntityType).HasMaxLength(64);
            // Artımlı senkronizasyon: "şu takvimde, şu token'dan sonraki değişiklikler".
            e.HasIndex(x => new { x.CalendarId, x.SyncToken });
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            // Geri alma, bir işlemin tüm satırlarını bu indeksle toplar.
            e.HasIndex(x => x.OperationId);
        });

        modelBuilder.Entity<WorkingHours>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            // Bir kullanıcının bir gün için tek mesai tanımı olur.
            e.HasIndex(x => new { x.UserId, x.DayOfWeek }).IsUnique();
        });

        modelBuilder.Entity<WorkLocationEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Note).HasMaxLength(200);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.Date }).IsUnique();
        });

        base.OnModelCreating(modelBuilder);
    }
}
