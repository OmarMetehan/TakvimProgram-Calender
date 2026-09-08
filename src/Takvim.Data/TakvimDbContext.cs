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
    public DbSet<Attendee> Attendees => Set<Attendee>();
    public DbSet<CalendarShare> CalendarShares => Set<CalendarShare>();
    public DbSet<AppPassword> AppPasswords => Set<AppPassword>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<SavedLocation> SavedLocations => Set<SavedLocation>();
    public DbSet<EventTemplate> EventTemplates => Set<EventTemplate>();
    public DbSet<SavedSearch> SavedSearches => Set<SavedSearch>();
    public DbSet<TaskList> TaskLists => Set<TaskList>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<ResourceBooking> ResourceBookings => Set<ResourceBooking>();
    public DbSet<AppointmentSchedule> AppointmentSchedules => Set<AppointmentSchedule>();
    public DbSet<AppointmentWindow> AppointmentWindows => Set<AppointmentWindow>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<MailAccount> MailAccounts => Set<MailAccount>();
    public DbSet<EventProposal> EventProposals => Set<EventProposal>();

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

            // Sessiz saat penceresi dört sütundan hesaplanır; kendisi sütun değildir.
            e.Ignore(x => x.QuietHours);
        });

        modelBuilder.Entity<Calendar>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Color).HasMaxLength(32);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.Property(x => x.SourceUrl).HasMaxLength(2000);
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
            e.Property(x => x.AgendaText).HasMaxLength(8000);
            e.Property(x => x.PrivateNotes).HasMaxLength(8000);

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

        modelBuilder.Entity<MailAccount>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.EmailAddress).HasMaxLength(320);
            e.Property(x => x.LastError).HasMaxLength(500);

            e.HasOne(x => x.User).WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

            // Aynı kutu bir kullanıcıda iki kez bağlanamaz.
            e.HasIndex(x => new { x.UserId, x.Provider, x.EmailAddress }).IsUnique();
        });

        modelBuilder.Entity<EventProposal>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.MessageId).HasMaxLength(255);
            e.Property(x => x.Subject).HasMaxLength(500);
            e.Property(x => x.From).HasMaxLength(320);
            e.Property(x => x.Snippet).HasMaxLength(1000);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.LocationText).HasMaxLength(1000);
            e.Property(x => x.OnlineMeetingUrl).HasMaxLength(2000);

            e.HasOne(x => x.Account).WithMany()
                .HasForeignKey(x => x.MailAccountId).OnDelete(DeleteBehavior.Cascade);

            // Aynı ileti ikinci kez önerilmez — reddedilmiş olsa bile.
            e.HasIndex(x => new { x.MailAccountId, x.MessageId }).IsUnique();
            e.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<AppointmentSchedule>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Slug).HasMaxLength(60);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.LocationText).HasMaxLength(1000);
            e.Property(x => x.OnlineMeetingProvider).HasMaxLength(32);

            e.Ignore(x => x.BlockLength);
            e.Ignore(x => x.Length);

            e.HasOne(x => x.Owner).WithMany()
                .HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Calendar).WithMany()
                .HasForeignKey(x => x.CalendarId).OnDelete(DeleteBehavior.Cascade);

            // Adres satırındaki kısa ad benzersizdir; yerel ağa açılan sayfa
            // sayfayı bununla bulur.
            e.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<AppointmentWindow>(e =>
        {
            e.HasKey(x => x.Id);

            e.HasOne(x => x.Schedule).WithMany(s => s.Windows)
                .HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.ScheduleId);
        });

        modelBuilder.Entity<Appointment>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.GuestName).HasMaxLength(200);
            e.Property(x => x.GuestEmail).HasMaxLength(320);
            e.Property(x => x.Note).HasMaxLength(2000);
            e.Property(x => x.CancellationReason).HasMaxLength(500);

            e.Ignore(x => x.IsCancelled);

            e.HasOne(x => x.Schedule).WithMany()
                .HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Cascade);

            // Etkinlik kalıcı silinirse randevu kaydı da gider.
            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);

            // Dilim sorgusunun ve günlük sınırın dayandığı indeks.
            e.HasIndex(x => new { x.ScheduleId, x.StartUtc });
        });

        modelBuilder.Entity<Resource>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Location).HasMaxLength(300);
            e.Property(x => x.Notes).HasMaxLength(2000);

            e.Ignore(x => x.Summary);

            // Kaynak silinirse takvimi de gider; ikisi tek bir şeydir.
            e.HasOne(x => x.Calendar).WithMany()
                .HasForeignKey(x => x.CalendarId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.IsActive);
            e.HasIndex(x => x.CalendarId).IsUnique();
        });

        modelBuilder.Entity<ResourceBooking>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.ResponseNote).HasMaxLength(500);

            e.HasOne(x => x.Resource).WithMany(r => r.Bookings)
                .HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Cascade);

            // Toplantı kalıcı silinirse tutma kaydı da gider.
            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);

            // Aynı kaynak bir toplantıya iki kez tutulamaz.
            e.HasIndex(x => new { x.ResourceId, x.EventId }).IsUnique();

            // Çakışma sorgusunun dayandığı indeks.
            e.HasIndex(x => new { x.ResourceId, x.StartUtc, x.EndUtc });
        });

        modelBuilder.Entity<TaskList>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Color).HasMaxLength(32);

            e.HasOne<User>().WithMany()
                .HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.OwnerUserId);
        });

        modelBuilder.Entity<TaskItem>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.Notes).HasMaxLength(8000);
            e.Property(x => x.RecurrenceRule).HasMaxLength(500);
            e.Property(x => x.SearchText).HasMaxLength(4000);

            e.Ignore(x => x.IsDone);
            e.Ignore(x => x.Due);

            e.HasOne(x => x.List).WithMany(l => l.Tasks)
                .HasForeignKey(x => x.TaskListId).OnDelete(DeleteBehavior.Cascade);

            // Alt görevler üst görevle birlikte gider.
            e.HasOne(x => x.Parent).WithMany(t => t.Subtasks)
                .HasForeignKey(x => x.ParentTaskId).OnDelete(DeleteBehavior.Cascade);

            // Takvime ayrılan blok silinirse görev kalır, yalnızca bağ kopar.
            e.HasOne<Event>().WithMany()
                .HasForeignKey(x => x.ScheduledEventId).OnDelete(DeleteBehavior.SetNull);

            // Liste görünümünün ve gün şeridinin dayandığı iki indeks.
            e.HasIndex(x => new { x.TaskListId, x.DeletedAt });
            e.HasIndex(x => x.DueDate);
        });

        modelBuilder.Entity<SavedSearch>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.Name).HasMaxLength(120);

            e.HasOne<User>().WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<EventTemplate>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.Name).HasMaxLength(120);

            e.HasOne<User>().WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

            // Aynı ad ikinci kez verildiğinde kastedilen şey güncellemektir.
            e.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<SavedLocation>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.Text).HasMaxLength(1000);
            e.Property(x => x.NormalizedText).HasMaxLength(1000);

            e.HasOne<User>().WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

            // Aynı konum bir kullanıcıda iki kez tutulmaz; kayıt yolu bu indekse
            // dayanarak "varsa artır, yoksa ekle" yapar.
            e.HasIndex(x => new { x.UserId, x.NormalizedText }).IsUnique();
        });

        modelBuilder.Entity<Attachment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.ContentType).HasMaxLength(160);
            e.Property(x => x.StorageName).HasMaxLength(80);
            e.Ignore(x => x.SizeText);
            e.Ignore(x => x.Icon);

            e.HasOne(x => x.Event).WithMany(x => x.Attachments)
                .HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.EventId);
            // Yetim dosya temizliği bu sütunu tarar.
            e.HasIndex(x => x.StorageName).IsUnique();
        });

        modelBuilder.Entity<AppPassword>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Label).HasMaxLength(100);
            e.Property(x => x.Prefix).HasMaxLength(8);
            e.Ignore(x => x.IsActive);

            e.HasOne(x => x.User).WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

            // Kimlik doğrulama, kullanıcının etkin parolalarını bu indeksle bulur.
            e.HasIndex(x => new { x.UserId, x.RevokedAt });
        });

        modelBuilder.Entity<Attendee>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.ResponseComment).HasMaxLength(1000);
            e.Property(x => x.ProposalNote).HasMaxLength(1000);
            // Hesaplanan alan; sütunu yok.
            e.Ignore(x => x.IsResponseStale);
            e.Ignore(x => x.HasProposal);

            e.HasOne(x => x.Event).WithMany(x => x.Attendees)
                .HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);

            // Aynı kişi bir etkinliğe iki kez davet edilemez.
            e.HasIndex(x => new { x.EventId, x.Email }).IsUnique();
            // "Bana gelen davetler" sorgusunun dayandığı indeks.
            e.HasIndex(x => new { x.UserId, x.Response });
        });

        modelBuilder.Entity<CalendarShare>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Calendar).WithMany(c => c.Shares)
                .HasForeignKey(x => x.CalendarId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Grantee).WithMany()
                .HasForeignKey(x => x.GranteeUserId).OnDelete(DeleteBehavior.Cascade);

            // Bir takvim bir kullanıcıyla tek bir seviyede paylaşılır.
            e.HasIndex(x => new { x.CalendarId, x.GranteeUserId }).IsUnique();
            e.HasIndex(x => x.GranteeUserId);
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
