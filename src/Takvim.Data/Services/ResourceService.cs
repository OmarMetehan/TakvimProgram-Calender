using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Time;

namespace Takvim.Data.Services;

/// <summary>Kaynak tanımlama girdisi.</summary>
public sealed record ResourceInput
{
    public required string Name { get; init; }

    public ResourceKind Kind { get; init; } = ResourceKind.Room;

    public int? Capacity { get; init; }
    public string? Location { get; init; }
    public ResourceFeatures Features { get; init; }

    public BookingPolicy Policy { get; init; } = BookingPolicy.AutoAccept;
    public Guid? ApproverUserId { get; init; }

    public string? Notes { get; init; }
    public string Color { get; init; } = "graphite";
}

/// <summary>Uygun kaynak aramasının ölçütleri.</summary>
public sealed record ResourceQuery
{
    public ResourceKind? Kind { get; init; }

    /// <summary>En az bu kadar kişi almalı.</summary>
    public int? MinimumCapacity { get; init; }

    /// <summary>Bu özelliklerin <b>hepsini</b> taşımalı.</summary>
    public ResourceFeatures RequiredFeatures { get; init; }

    /// <summary>Serbest metin: ad ve konumda aranır.</summary>
    public string? Term { get; init; }
}

/// <summary>Bir kaynağın verilen aralıktaki durumu.</summary>
/// <param name="Resource">Kaynağın kendisi.</param>
/// <param name="IsFree">Aralık boş mu.</param>
/// <param name="ConflictTitle">Doluysa çakışan tutmanın başlığı; gizliyse null.</param>
public sealed record ResourceAvailability(Resource Resource, bool IsFree, string? ConflictTitle)
{
    /// <summary>Onay isteyen kaynak; boş olsa da hemen kesinleşmez.</summary>
    public bool NeedsApproval => Resource.Policy == BookingPolicy.RequiresApproval;
}

/// <summary>Rezervasyon denemesinin sonucu.</summary>
public sealed record BookingResult(ResourceBooking? Booking, string? Error)
{
    /// <summary>
    /// İşlem beklendiği gibi sonuçlandı mı. Kaydın varlığına değil hatanın
    /// yokluğuna bakar: reddedilen bir talep de kayıt döndürür, ama başarı
    /// değildir.
    /// </summary>
    public bool Success => Error is null;

    /// <summary>Kabul edildi mi, yoksa onay mı bekliyor.</summary>
    public bool IsPending => Success && Booking?.Status == ResourceBookingStatus.Requested;
}

/// <summary>
/// Oda ve ekipman rezervasyonu.
/// <para>
/// Her kaynağın kendi takvimi vardır ve rezervasyon o takvime konan bir
/// etkinliktir. Çakışma denetimi de meşguliyet görünümü de bu sayede ayrıca
/// yazılmaz; kaynak takvimi başka bir takvim gibi paylaşılabilir ve CalDAV'a
/// çıkabilir.
/// </para>
/// </summary>
public sealed class ResourceService(
    TakvimDbContext db,
    EventService events,
    TimeZoneService zones,
    IClock clock)
{
    // ==================================================================
    // Kaynak tanımları
    // ==================================================================

    public Task<List<Resource>> GetAllAsync(bool includeInactive = false, CancellationToken ct = default)
        => db.Resources
            .AsNoTracking()
            .Include(r => r.Calendar)
            .Where(r => includeInactive || r.IsActive)
            .OrderBy(r => r.Kind)
            .ThenBy(r => r.Name)
            .ToListAsync(ct);

    public Task<Resource?> FindAsync(Guid resourceId, CancellationToken ct = default)
        => db.Resources
            .AsNoTracking()
            .Include(r => r.Calendar)
            .FirstOrDefaultAsync(r => r.Id == resourceId, ct);

    /// <summary>
    /// Kaynağı ve ona ait takvimi birlikte açar. Kaynak takvimi salt okunur
    /// <b>değildir</b>: rezervasyonlar oraya yazılır; ama kimsenin kişisel
    /// takvimi olmadığı için kenar çubuğunda gizli başlar.
    /// </summary>
    public async Task<Resource> CreateAsync(
        ResourceInput input, Guid ownerUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Name);

        var now = clock.GetCurrentInstant().ToDateTimeOffset();

        var calendar = new Calendar
        {
            Name = input.Name.Trim(),
            OwnerUserId = ownerUserId,
            Kind = CalendarKind.Resource,
            Color = input.Color,
            IsVisible = false,
            Description = input.Location,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Calendars.Add(calendar);

        var resource = new Resource
        {
            CalendarId = calendar.Id,
            Name = input.Name.Trim(),
            Kind = input.Kind,
            Capacity = input.Kind == ResourceKind.Room ? input.Capacity : null,
            Location = input.Location,
            Features = input.Features,
            Policy = input.Policy,
            ApproverUserId = input.ApproverUserId,
            Notes = input.Notes,
            CreatedAt = now,
        };

        db.Resources.Add(resource);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return resource;
    }

    public async Task<Resource?> UpdateAsync(
        Guid resourceId, ResourceInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Name);

        var resource = await db.Resources
            .Include(r => r.Calendar)
            .FirstOrDefaultAsync(r => r.Id == resourceId, ct).ConfigureAwait(false);

        if (resource is null) return null;

        resource.Name = input.Name.Trim();
        resource.Kind = input.Kind;
        resource.Capacity = input.Kind == ResourceKind.Room ? input.Capacity : null;
        resource.Location = input.Location;
        resource.Features = input.Features;
        resource.Policy = input.Policy;
        resource.ApproverUserId = input.ApproverUserId;
        resource.Notes = input.Notes;

        // Takvim adı kaynağın adını izler; ikisi ayrışırsa meşguliyet
        // görünümünde tanınmayan bir satır belirir.
        if (resource.Calendar is { } calendar)
        {
            calendar.Name = resource.Name;
            calendar.Color = input.Color;
            calendar.Description = input.Location;
            calendar.UpdatedAt = clock.GetCurrentInstant().ToDateTimeOffset();
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return resource;
    }

    /// <summary>
    /// Kaynağı takvimiyle birlikte kalıcı olarak siler.
    /// <para>
    /// Neredeyse her durumda <see cref="SetActiveAsync"/> tercih edilmelidir:
    /// geçmiş rezervasyonlar "hangi odadaydı" sorusunun cevabıdır ve burada
    /// kaybolurlar. Silme, yanlışlıkla açılmış bir kaynağı geri almak içindir.
    /// </para>
    /// <para>
    /// Takvim yabancı anahtarın <b>hedefi</b> olduğu için basamaklı silme ters
    /// yönde işler; bu yüzden ikisi burada elle birlikte silinir.
    /// </para>
    /// </summary>
    public async Task DeleteAsync(Guid resourceId, CancellationToken ct = default)
    {
        var resource = await db.Resources
            .FirstOrDefaultAsync(r => r.Id == resourceId, ct).ConfigureAwait(false);

        if (resource is null) return;

        var calendarId = resource.CalendarId;

        db.Resources.Remove(resource);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await db.Calendars.Where(c => c.Id == calendarId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Kaynağı kullanımdan kaldırır. Silmek yerine kapatmak, geçmişi bozmadan
    /// listelerden çıkarmayı sağlar; tercih edilen yol budur.
    /// </summary>
    public async Task SetActiveAsync(Guid resourceId, bool isActive, CancellationToken ct = default)
    {
        var resource = await db.Resources
            .FirstOrDefaultAsync(r => r.Id == resourceId, ct).ConfigureAwait(false);

        if (resource is null) return;

        resource.IsActive = isActive;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ==================================================================
    // Uygunluk
    // ==================================================================

    /// <summary>
    /// Ölçütlere uyan kaynakları, verilen aralıktaki doluluklarıyla birlikte
    /// döner. Boş olanlar önce gelir.
    /// </summary>
    /// <param name="ignoreEventId">
    /// Var olan bir toplantının odası değiştirilirken kendi tutması çakışma
    /// sayılmamalıdır; onun kimliği buraya geçer.
    /// </param>
    public async Task<List<ResourceAvailability>> FindAvailableAsync(
        Instant fromInclusive,
        Instant toExclusive,
        ResourceQuery? query = null,
        Guid? ignoreEventId = null,
        CancellationToken ct = default)
    {
        query ??= new ResourceQuery();

        var candidates = db.Resources
            .AsNoTracking()
            .Include(r => r.Calendar)
            .Where(r => r.IsActive);

        if (query.Kind is { } kind) candidates = candidates.Where(r => r.Kind == kind);

        if (query.MinimumCapacity is { } minimum)
            candidates = candidates.Where(r => r.Capacity != null && r.Capacity >= minimum);

        if (!string.IsNullOrWhiteSpace(query.Term))
        {
            var normalized = Core.Text.TurkishText.Normalize(query.Term);

            // Kaynak sayısı azdır (bir kurumda onlarca); bellekte süzmek
            // ayrı bir arama sütunu tutmaktan daha ucuza gelir.
            var loaded = await candidates.ToListAsync(ct).ConfigureAwait(false);

            loaded = [.. loaded.Where(r =>
                Core.Text.TurkishText.Normalize(r.Name).Contains(normalized, StringComparison.Ordinal)
                || Core.Text.TurkishText.Normalize(r.Location).Contains(normalized, StringComparison.Ordinal))];

            return await BuildAvailabilityAsync(
                Filter(loaded, query), fromInclusive, toExclusive, ignoreEventId, ct).ConfigureAwait(false);
        }

        var all = await candidates.ToListAsync(ct).ConfigureAwait(false);

        return await BuildAvailabilityAsync(
            Filter(all, query), fromInclusive, toExclusive, ignoreEventId, ct).ConfigureAwait(false);
    }

    /// <summary>Tek bir kaynağın verilen aralıkta boş olup olmadığı.</summary>
    public async Task<bool> IsFreeAsync(
        Guid resourceId,
        Instant fromInclusive,
        Instant toExclusive,
        Guid? ignoreEventId = null,
        CancellationToken ct = default)
        => !await ConflictQuery(resourceId, fromInclusive, toExclusive, ignoreEventId)
            .AnyAsync(ct).ConfigureAwait(false);

    /// <summary>Bir toplantının tuttuğu kaynaklar.</summary>
    public Task<List<ResourceBooking>> GetForEventAsync(Guid eventId, CancellationToken ct = default)
        => db.ResourceBookings
            .AsNoTracking()
            .Include(b => b.Resource)
            .Where(b => b.EventId == eventId)
            .OrderBy(b => b.Resource!.Name)
            .ToListAsync(ct);

    /// <summary>Onay bekleyen talepler; kaynak sorumlusunun kuyruğu.</summary>
    public Task<List<ResourceBooking>> GetPendingAsync(Guid approverUserId, CancellationToken ct = default)
        => db.ResourceBookings
            .AsNoTracking()
            .Include(b => b.Resource)!.ThenInclude(r => r!.Calendar)
            .Include(b => b.Event)
            .Where(b => b.Status == ResourceBookingStatus.Requested
                        && (b.Resource!.ApproverUserId == approverUserId
                            || (b.Resource.ApproverUserId == null
                                && b.Resource.Calendar!.OwnerUserId == approverUserId)))
            .OrderBy(b => b.StartUtc)
            .ToListAsync(ct);

    // ==================================================================
    // Rezervasyon
    // ==================================================================

    /// <summary>
    /// Toplantı için kaynağı tutar.
    /// <para>
    /// Kendiliğinden kabul eden bir kaynakta tutma kaydı hemen açılır. Onay
    /// isteyen kaynakta <b>kayıt açılmaz</b>: onaylanmamış bir talep odayı
    /// işgal etmemelidir, yoksa onay bekleyen üç talep odayı üç kez doldurur.
    /// Bunun bedeli, onay anında odanın kapılmış olabilmesidir; o durum onay
    /// sırasında yeniden denetlenir.
    /// </para>
    /// </summary>
    public async Task<BookingResult> BookAsync(
        Guid resourceId,
        Guid eventId,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        var resource = await db.Resources
            .FirstOrDefaultAsync(r => r.Id == resourceId && r.IsActive, ct).ConfigureAwait(false);

        if (resource is null) return new BookingResult(null, "Kaynak bulunamadı.");

        var meeting = await db.Events
            .FirstOrDefaultAsync(e => e.Id == eventId && e.DeletedAt == null, ct).ConfigureAwait(false);

        if (meeting is null) return new BookingResult(null, "Etkinlik bulunamadı.");

        var existing = await db.ResourceBookings
            .FirstOrDefaultAsync(b => b.ResourceId == resourceId && b.EventId == eventId, ct)
            .ConfigureAwait(false);

        if (existing is { Status: not ResourceBookingStatus.Declined })
        {
            return new BookingResult(existing, null);
        }

        if (!await IsFreeAsync(resourceId, meeting.StartUtc, meeting.EndUtc, eventId, ct).ConfigureAwait(false))
        {
            return new BookingResult(null, $"{resource.Name} bu saatte dolu.");
        }

        var booking = existing ?? new ResourceBooking
        {
            ResourceId = resourceId,
            EventId = eventId,
            RequestedByUserId = actorUserId,
        };

        booking.StartUtc = meeting.StartUtc;
        booking.EndUtc = meeting.EndUtc;
        booking.RequestedAt = clock.GetCurrentInstant().ToDateTimeOffset();
        booking.RespondedAt = null;
        booking.ResponseNote = null;

        if (resource.Policy == BookingPolicy.AutoAccept)
        {
            booking.Status = ResourceBookingStatus.Accepted;
            booking.HoldEventId = await CreateHoldAsync(resource, meeting, actorUserId, ct).ConfigureAwait(false);
        }
        else
        {
            booking.Status = ResourceBookingStatus.Requested;
            booking.HoldEventId = null;
        }

        if (existing is null) db.ResourceBookings.Add(booking);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new BookingResult(booking, null);
    }

    /// <summary>Onay isteyen bir talebi karşılar.</summary>
    public async Task<BookingResult> RespondAsync(
        Guid bookingId,
        bool accept,
        Guid actorUserId,
        string? note = null,
        CancellationToken ct = default)
    {
        var booking = await db.ResourceBookings
            .Include(b => b.Resource)
            .Include(b => b.Event)
            .FirstOrDefaultAsync(b => b.Id == bookingId, ct).ConfigureAwait(false);

        if (booking?.Resource is not { } resource) return new BookingResult(null, "Talep bulunamadı.");
        if (booking.Event is not { } meeting) return new BookingResult(null, "Etkinlik silinmiş.");

        booking.RespondedAt = clock.GetCurrentInstant().ToDateTimeOffset();
        booking.ResponseNote = note;

        if (!accept)
        {
            booking.Status = ResourceBookingStatus.Declined;
            await ReleaseHoldAsync(booking, actorUserId, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return new BookingResult(booking, null);
        }

        // Talep beklerken oda kapılmış olabilir; onay anında yeniden bakılır.
        if (!await IsFreeAsync(resource.Id, meeting.StartUtc, meeting.EndUtc, meeting.Id, ct).ConfigureAwait(false))
        {
            booking.Status = ResourceBookingStatus.Declined;
            booking.ResponseNote = note ?? "Onay beklerken bu saat doldu.";

            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return new BookingResult(booking, $"{resource.Name} bu saatte artık dolu.");
        }

        booking.Status = ResourceBookingStatus.Accepted;
        booking.StartUtc = meeting.StartUtc;
        booking.EndUtc = meeting.EndUtc;
        booking.HoldEventId = await CreateHoldAsync(resource, meeting, actorUserId, ct).ConfigureAwait(false);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new BookingResult(booking, null);
    }

    /// <summary>Kaynağı serbest bırakır; tutma kaydı da silinir.</summary>
    public async Task ReleaseAsync(Guid bookingId, Guid actorUserId, CancellationToken ct = default)
    {
        var booking = await db.ResourceBookings
            .FirstOrDefaultAsync(b => b.Id == bookingId, ct).ConfigureAwait(false);

        if (booking is null) return;

        await ReleaseHoldAsync(booking, actorUserId, ct).ConfigureAwait(false);

        db.ResourceBookings.Remove(booking);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Toplantı taşındığında tutmaları da taşır. Yeni saatte dolu olan kaynak
    /// <b>serbest bırakılır</b> ve adı döner: sessizce eski saatte tutmaya devam
    /// etmek, kullanıcının odası olduğunu sanmasına yol açardı.
    /// </summary>
    /// <returns>Taşınamadığı için bırakılan kaynakların adları.</returns>
    public async Task<List<string>> RescheduleAsync(
        Guid eventId, Guid actorUserId, CancellationToken ct = default)
    {
        var meeting = await db.Events
            .FirstOrDefaultAsync(e => e.Id == eventId && e.DeletedAt == null, ct).ConfigureAwait(false);

        if (meeting is null) return [];

        var bookings = await db.ResourceBookings
            .Include(b => b.Resource)
            .Where(b => b.EventId == eventId && b.Status != ResourceBookingStatus.Declined)
            .ToListAsync(ct).ConfigureAwait(false);

        var released = new List<string>();

        foreach (var booking in bookings)
        {
            if (booking.StartUtc == meeting.StartUtc && booking.EndUtc == meeting.EndUtc) continue;

            var free = await IsFreeAsync(
                booking.ResourceId, meeting.StartUtc, meeting.EndUtc, eventId, ct).ConfigureAwait(false);

            if (!free)
            {
                released.Add(booking.Resource?.Name ?? "Kaynak");

                await ReleaseHoldAsync(booking, actorUserId, ct).ConfigureAwait(false);
                db.ResourceBookings.Remove(booking);
                continue;
            }

            booking.StartUtc = meeting.StartUtc;
            booking.EndUtc = meeting.EndUtc;

            if (booking.HoldEventId is { } holdId)
            {
                await events.UpdateAsync(holdId, null, SeriesEditScope.AllInSeries,
                    HoldInput(booking.Resource!, meeting, actorUserId), ct).ConfigureAwait(false);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return released;
    }

    /// <summary>Toplantı silindiğinde tuttuğu tüm kaynakları bırakır.</summary>
    public async Task ReleaseAllAsync(Guid eventId, Guid actorUserId, CancellationToken ct = default)
    {
        var bookings = await db.ResourceBookings
            .Where(b => b.EventId == eventId)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var booking in bookings)
        {
            await ReleaseHoldAsync(booking, actorUserId, ct).ConfigureAwait(false);
        }

        db.ResourceBookings.RemoveRange(bookings);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ==================================================================

    private static List<Resource> Filter(List<Resource> resources, ResourceQuery query)
        => query.RequiredFeatures == ResourceFeatures.None
            ? resources
            : [.. resources.Where(r => (r.Features & query.RequiredFeatures) == query.RequiredFeatures)];

    private async Task<List<ResourceAvailability>> BuildAvailabilityAsync(
        List<Resource> resources,
        Instant fromInclusive,
        Instant toExclusive,
        Guid? ignoreEventId,
        CancellationToken ct)
    {
        var result = new List<ResourceAvailability>(resources.Count);

        foreach (var resource in resources)
        {
            var conflict = await ConflictQuery(resource.Id, fromInclusive, toExclusive, ignoreEventId)
                .Select(b => b.Event!.Title)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            result.Add(new ResourceAvailability(resource, conflict is null, conflict));
        }

        // Boş olanlar önce; sonra ad sırası.
        return [.. result.OrderByDescending(a => a.IsFree).ThenBy(a => a.Resource.Name)];
    }

    /// <summary>
    /// Çakışan tutmalar. Yalnızca kabul edilmiş kayıtlar sayılır: onay bekleyen
    /// bir talep odayı işgal etmez.
    /// </summary>
    private IQueryable<ResourceBooking> ConflictQuery(
        Guid resourceId, Instant fromInclusive, Instant toExclusive, Guid? ignoreEventId)
        => db.ResourceBookings
            .AsNoTracking()
            .Include(b => b.Event)
            .Where(b => b.ResourceId == resourceId
                        && b.Status == ResourceBookingStatus.Accepted
                        && b.StartUtc < toExclusive
                        && b.EndUtc > fromInclusive
                        && (ignoreEventId == null || b.EventId != ignoreEventId));

    private async Task<Guid> CreateHoldAsync(
        Resource resource, Event meeting, Guid actorUserId, CancellationToken ct)
    {
        var created = await events
            .CreateAsync(HoldInput(resource, meeting, actorUserId), ct).ConfigureAwait(false);

        return created.PrimaryEventId;
    }

    /// <summary>
    /// Kaynak takvimine konacak tutma kaydı. Toplantının başlığını taşır ama
    /// açıklamasını taşımaz: oda takvimini görebilen herkes toplantının
    /// içeriğini görmemeli.
    /// </summary>
    private EventInput HoldInput(Resource resource, Event meeting, Guid actorUserId)
    {
        var zoneId = meeting.StartTimeZoneId ?? TimeZoneService.DefaultZoneId;

        return new EventInput
        {
            CalendarId = resource.CalendarId,
            Title = meeting.Title,
            LocationText = resource.Name,
            StartLocal = zones.ToLocal(meeting.StartUtc, zoneId),
            EndLocal = zones.ToLocal(meeting.EndUtc, zoneId),
            StartTimeZoneId = zoneId,
            EndTimeZoneId = zoneId,
            IsAllDay = meeting.IsAllDay,
            Availability = Availability.Busy,

            // Oda takvimi paylaşıldığında yalnızca doluluk görünsün.
            Visibility = EventVisibility.Private,
            ActorUserId = actorUserId,
        };
    }

    private async Task ReleaseHoldAsync(ResourceBooking booking, Guid actorUserId, CancellationToken ct)
    {
        if (booking.HoldEventId is not { } holdId) return;

        booking.HoldEventId = null;

        await events.DeleteAsync(holdId, null, SeriesEditScope.AllInSeries, actorUserId, ct)
            .ConfigureAwait(false);
    }
}
