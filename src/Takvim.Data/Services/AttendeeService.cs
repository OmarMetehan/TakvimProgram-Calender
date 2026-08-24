using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;

namespace Takvim.Data.Services;

/// <summary>Katılımcı eklerken verilen bilgiler.</summary>
public sealed record AttendeeInput(
    string Email,
    string DisplayName,
    Guid? UserId = null,
    AttendeeRole Role = AttendeeRole.Required,
    bool CanEdit = false,
    bool CanInviteOthers = false,
    bool CanSeeGuestList = true);

/// <summary>Yanıt takip panelinin özeti.</summary>
/// <param name="Accepted">Kabul eden sayısı.</param>
/// <param name="Declined">Reddeden sayısı.</param>
/// <param name="Tentative">Belirsiz bırakan sayısı.</param>
/// <param name="NoResponse">Henüz yanıtlamayan sayısı.</param>
/// <param name="Proposals">Yeni zaman öneren sayısı.</param>
/// <param name="Stale">Toplantının şimdiki saatinden başka bir saate yanıt vermiş kişi sayısı.</param>
public readonly record struct ResponseSummary(
    int Accepted, int Declined, int Tentative, int NoResponse, int Proposals, int Stale)
{
    public int Total => Accepted + Declined + Tentative + NoResponse;

    /// <summary>Kullanıcıya gösterilen tek satırlık özet.</summary>
    public string Text
    {
        get
        {
            if (Total == 0) return "Katılımcı yok";

            var text = $"{Accepted} kabul · {Declined} ret · {Tentative} belirsiz · {NoResponse} yanıt yok";
            return Stale > 0 ? $"{text} · {Stale} yanıt eski saate göre" : text;
        }
    }
}

/// <summary>
/// Katılımcı ve RSVP işlemleri.
/// <para>
/// Paylaşımlı model: tek etkinlik satırı, ona bağlı katılımcılar. Bir kişinin
/// yanıtı aynı kaydı değiştirdiği için ötekiler anında görür; kopyalar arasında
/// eşitleme gerekmez.
/// </para>
/// </summary>
public sealed class AttendeeService(TakvimDbContext db, IClock clock)
{
    private readonly ChangeLogWriter _log = new(db);

    /// <summary>
    /// Etkinliğin katılımcıları; organizatör önce, sonra zorunlular.
    /// Etkinlik de yüklenir: <see cref="Attendee.IsResponseStale"/> onsuz hesaplanamaz.
    /// </summary>
    public Task<List<Attendee>> GetAsync(Guid eventId, CancellationToken ct = default)
        => db.Attendees
            .AsNoTracking()
            .Include(a => a.User)
            .Include(a => a.Event)
            .Where(a => a.EventId == eventId)
            .OrderBy(a => a.Role)
            .ThenBy(a => a.DisplayName)
            .ToListAsync(ct);

    /// <summary>
    /// Etkinliğin katılımcı listesini verilen listeye eşitler.
    /// Var olanların yanıtları korunur; yalnızca çıkarılanlar silinir.
    /// </summary>
    public async Task SyncAsync(
        Guid eventId,
        IReadOnlyList<AttendeeInput> attendees,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attendees);

        var existing = await db.Attendees
            .Where(a => a.EventId == eventId)
            .ToListAsync(ct).ConfigureAwait(false);

        var wanted = attendees
            .GroupBy(a => a.Email.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Listeden çıkarılanlar silinir.
        foreach (var attendee in existing.Where(a => !wanted.ContainsKey(a.Email)))
        {
            db.Attendees.Remove(attendee);
        }

        var byEmail = existing.ToDictionary(a => a.Email, StringComparer.Ordinal);

        foreach (var (email, input) in wanted)
        {
            if (byEmail.TryGetValue(email, out var current))
            {
                // Yanıtı sıfırlamadan yalnızca yetki ve rol güncellenir; kişi
                // zaten kabul etmişse rolü değişti diye yeniden sorulmaz.
                current.DisplayName = input.DisplayName;
                current.Role = input.Role;
                current.CanEdit = input.CanEdit;
                current.CanInviteOthers = input.CanInviteOthers;
                current.CanSeeGuestList = input.CanSeeGuestList;
                continue;
            }

            db.Attendees.Add(new Attendee
            {
                EventId = eventId,
                UserId = input.UserId,
                Email = email,
                DisplayName = input.DisplayName,
                Role = input.Role,
                CanEdit = input.CanEdit,
                CanInviteOthers = input.CanInviteOthers,
                CanSeeGuestList = input.CanSeeGuestList,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ==================================================================
    // RSVP
    // ==================================================================

    /// <summary>Davete yanıt verir.</summary>
    public async Task<bool> RespondAsync(
        Guid eventId,
        Guid userId,
        ResponseStatus response,
        string? comment = null,
        AttendanceMode mode = AttendanceMode.Unspecified,
        CancellationToken ct = default)
    {
        var attendee = await db.Attendees
            .Include(a => a.Event)
            .FirstOrDefaultAsync(a => a.EventId == eventId && a.UserId == userId, ct)
            .ConfigureAwait(false);

        if (attendee is null) return false;

        attendee.Response = response;
        attendee.ResponseComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        attendee.RespondedAt = clock.GetCurrentInstant().ToDateTimeOffset();
        attendee.Mode = mode;

        // Yanıtın hangi saate verildiği kaydedilir; toplantı sonradan taşınırsa
        // bu yanıt "eski" olarak işaretlenebilsin.
        attendee.RespondedForStartLocal = attendee.Event?.StartLocal;

        // Yanıt vermek bekleyen zaman önerisini geçersiz kılar.
        ClearProposal(attendee);

        _log.Record(Guid.NewGuid(), ChangeOperation.Update, nameof(Attendee), attendee.Id,
            calendarId: null, beforeJson: null, afterJson: null, userId,
            summary: $"{attendee.DisplayName}: {Describe(response)}");

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Reddetmek yerine alternatif saat önerir. Yanıt "belirsiz" olur:
    /// davetli katılmayı reddetmiyor, başka bir saat istiyor.
    /// </summary>
    public async Task<bool> ProposeNewTimeAsync(
        Guid eventId,
        Guid userId,
        LocalDateTime start,
        LocalDateTime end,
        string? note = null,
        CancellationToken ct = default)
    {
        var attendee = await db.Attendees
            .Include(a => a.Event)
            .FirstOrDefaultAsync(a => a.EventId == eventId && a.UserId == userId, ct)
            .ConfigureAwait(false);

        if (attendee is null) return false;
        if (end <= start) throw new ArgumentException("Bitiş, başlangıçtan sonra olmalı.", nameof(end));

        attendee.ProposedStartLocal = start;
        attendee.ProposedEndLocal = end;
        attendee.ProposalNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        attendee.Response = ResponseStatus.Tentative;
        attendee.RespondedAt = clock.GetCurrentInstant().ToDateTimeOffset();
        attendee.RespondedForStartLocal = attendee.Event?.StartLocal;

        _log.Record(Guid.NewGuid(), ChangeOperation.Update, nameof(Attendee), attendee.Id,
            calendarId: null, beforeJson: null, afterJson: null, userId,
            summary: $"{attendee.DisplayName} yeni zaman önerdi: {start:dd.MM.yyyy HH:mm}");

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Öneriyi geri çeker.</summary>
    public async Task WithdrawProposalAsync(Guid attendeeId, CancellationToken ct = default)
    {
        var attendee = await db.Attendees.FirstOrDefaultAsync(a => a.Id == attendeeId, ct).ConfigureAwait(false);
        if (attendee is null) return;

        ClearProposal(attendee);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Organizatör bir öneriyi kabul ettiğinde, önerinin bilgilerini döner.
    /// Etkinliği taşımak <see cref="EventService"/>'in işidir; burada yalnızca
    /// öneri temizlenir ve öneren kişi kabul etmiş sayılır.
    /// </summary>
    public async Task<(LocalDateTime Start, LocalDateTime End)?> AcceptProposalAsync(
        Guid attendeeId, CancellationToken ct = default)
    {
        var attendee = await db.Attendees.FirstOrDefaultAsync(a => a.Id == attendeeId, ct).ConfigureAwait(false);

        if (attendee?.ProposedStartLocal is not { } start || attendee.ProposedEndLocal is not { } end)
            return null;

        ClearProposal(attendee);
        attendee.Response = ResponseStatus.Accepted;
        attendee.RespondedAt = clock.GetCurrentInstant().ToDateTimeOffset();

        // Öneren kişi yeni saati zaten kabul etmiş sayılır; yanıtı yeni saate
        // verilmiştir. Diğerlerinin yanıtları silinmez: toplantı taşınınca
        // kendiliğinden "eski saate göre" işaretlenirler. Böylece organizatör
        // "Katılacak (eski saate göre)" ile "yanıt yok" arasındaki farkı görür.
        attendee.RespondedForStartLocal = start;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (start, end);
    }

    /// <summary>
    /// Tüm yanıtları siler ve herkese yeniden sorar.
    /// <para>
    /// Saat değişikliğinde <b>kendiliğinden çağrılmaz</b>: taşınan bir toplantıda
    /// yanıtlar korunur ve eski olarak işaretlenir. Bu yordam, organizatörün
    /// bilinçli olarak "herkese yeniden sor" dediği durum içindir.
    /// </para>
    /// </summary>
    public async Task ResetResponsesAsync(Guid eventId, CancellationToken ct = default)
    {
        var attendees = await db.Attendees
            .Where(a => a.EventId == eventId && a.Response != ResponseStatus.NeedsAction)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var attendee in attendees)
        {
            attendee.Response = ResponseStatus.NeedsAction;
            attendee.ResponseComment = null;
            attendee.RespondedAt = null;
            attendee.RespondedForStartLocal = null;
            ClearProposal(attendee);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ==================================================================
    // Sorgular
    // ==================================================================

    /// <summary>Yanıt sayıları.</summary>
    public static ResponseSummary Summarize(IEnumerable<Attendee> attendees)
    {
        ArgumentNullException.ThrowIfNull(attendees);

        var list = attendees.ToList();

        return new ResponseSummary(
            list.Count(a => a.Response == ResponseStatus.Accepted),
            list.Count(a => a.Response == ResponseStatus.Declined),
            list.Count(a => a.Response == ResponseStatus.Tentative),
            list.Count(a => a.Response == ResponseStatus.NeedsAction),
            list.Count(a => a.HasProposal),
            list.Count(a => a.IsResponseStale));
    }

    /// <summary>Kullanıcının yanıtlamadığı davetler.</summary>
    public Task<List<Attendee>> GetPendingInvitationsAsync(Guid userId, CancellationToken ct = default)
        => db.Attendees
            .AsNoTracking()
            .Include(a => a.Event!).ThenInclude(e => e.Calendar)
            .Where(a => a.UserId == userId
                        && a.Response == ResponseStatus.NeedsAction
                        && a.Event!.DeletedAt == null
                        && a.Event.Status != EventStatus.Cancelled)
            .OrderBy(a => a.Event!.StartUtc)
            .ToListAsync(ct);

    private static void ClearProposal(Attendee attendee)
    {
        attendee.ProposedStartLocal = null;
        attendee.ProposedEndLocal = null;
        attendee.ProposalNote = null;
    }

    public static string Describe(ResponseStatus status) => status switch
    {
        ResponseStatus.Accepted => "Katılacak",
        ResponseStatus.Declined => "Katılmayacak",
        ResponseStatus.Tentative => "Belki",
        _ => "Yanıt bekleniyor",
    };
}
