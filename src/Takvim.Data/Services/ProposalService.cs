using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Mail;
using Takvim.Core.Time;

namespace Takvim.Data.Services;

/// <summary>Bir posta kutusundan okunan iletinin ayrıştırılacak hâli.</summary>
/// <param name="MessageId">Sağlayıcıdaki ileti kimliği.</param>
/// <param name="Subject">Konu satırı.</param>
/// <param name="From">Gönderen.</param>
/// <param name="Body">Düz metne indirilmiş gövde.</param>
/// <param name="ReceivedAt">Alınma anı.</param>
public sealed record IncomingMessage(
    string MessageId, string Subject, string? From, string Body, Instant ReceivedAt);

/// <summary>
/// E-postalardan çıkarılan etkinlik önerileri.
/// <para>
/// Posta okuma katmanı (ağ, OAuth) sunucu tarafındadır; burada yalnızca
/// okunmuş iletiler alınır ve öneriye çevrilir. Bu ayrım sayesinde çıkarım
/// mantığı ağa çıkmadan sınanabilir.
/// </para>
/// <para>
/// Öneri <b>kendiliğinden takvime yazılmaz</b>: doğal dil ayrıştırıcısı
/// yanılabilir ve yanlış bir toplantıyı sessizce eklemek, hiç eklememekten
/// kötüdür.
/// </para>
/// </summary>
public sealed class ProposalService(TakvimDbContext db, TimeZoneService zones, IClock clock)
{
    /// <summary>Bir hesapta bekletilecek en fazla öneri; kutu dolup taşmasın.</summary>
    public const int MaxPendingPerAccount = 100;

    // ==================================================================
    // Hesaplar
    // ==================================================================

    public Task<List<MailAccount>> GetAccountsAsync(Guid userId, CancellationToken ct = default)
        => db.MailAccounts
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderBy(a => a.EmailAddress)
            .ToListAsync(ct);

    public Task<MailAccount?> FindAccountAsync(Guid accountId, CancellationToken ct = default)
        => db.MailAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);

    /// <summary>Taranması gereken hesaplar; arka plan görevi bunu okur.</summary>
    public Task<List<MailAccount>> GetScannableAsync(CancellationToken ct = default)
        => db.MailAccounts.AsNoTracking().Where(a => a.IsActive).ToListAsync(ct);

    /// <summary>
    /// Kutuyu bağlar ya da var olan bağlantıyı tazeler. Aynı adres ikinci kez
    /// bağlanınca yeni satır açılmaz; anahtar yenilenir.
    /// </summary>
    public async Task<MailAccount> ConnectAsync(
        Guid userId,
        MailProvider provider,
        string emailAddress,
        byte[] protectedRefreshToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emailAddress);
        ArgumentNullException.ThrowIfNull(protectedRefreshToken);

        var address = emailAddress.Trim();

        var existing = await db.MailAccounts
            .FirstOrDefaultAsync(a => a.UserId == userId
                                   && a.Provider == provider
                                   && a.EmailAddress == address, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            existing.ProtectedRefreshToken = protectedRefreshToken;
            existing.IsActive = true;
            existing.LastError = null;

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return existing;
        }

        var account = new MailAccount
        {
            UserId = userId,
            Provider = provider,
            EmailAddress = address,
            ProtectedRefreshToken = protectedRefreshToken,
            ConnectedAt = clock.GetCurrentInstant().ToDateTimeOffset(),
        };

        db.MailAccounts.Add(account);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return account;
    }

    public async Task SetActiveAsync(Guid accountId, bool isActive, CancellationToken ct = default)
    {
        var account = await db.MailAccounts
            .FirstOrDefaultAsync(a => a.Id == accountId, ct).ConfigureAwait(false);

        if (account is null) return;

        account.IsActive = isActive;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SetLookbackAsync(Guid accountId, int days, CancellationToken ct = default)
    {
        var account = await db.MailAccounts
            .FirstOrDefaultAsync(a => a.Id == accountId, ct).ConfigureAwait(false);

        if (account is null) return;

        account.LookbackDays = Math.Clamp(days, 1, 90);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Kutunun bağlantısını keser. Önerileri de gider.</summary>
    public async Task DisconnectAsync(Guid accountId, CancellationToken ct = default)
        => await db.MailAccounts
            .Where(a => a.Id == accountId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

    /// <summary>Tarama sonucunu hesaba işler.</summary>
    public async Task RecordScanAsync(
        Guid accountId, Instant scannedThrough, string? error, CancellationToken ct = default)
    {
        var account = await db.MailAccounts
            .FirstOrDefaultAsync(a => a.Id == accountId, ct).ConfigureAwait(false);

        if (account is null) return;

        account.LastScanAt = clock.GetCurrentInstant().ToDateTimeOffset();
        account.LastError = error;

        // Başarısız tarama sınırı ilerletmez; aynı aralık sonra yeniden denenir.
        if (error is null) account.ScannedThrough = scannedThrough;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Taramanın hangi andan başlayacağı.</summary>
    public Instant ScanFrom(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        // İlk bağlanışta geçmişin tamamı taranmaz; kullanıcının belirlediği
        // kadar geriye bakılır.
        return account.ScannedThrough
            ?? clock.GetCurrentInstant().Minus(Duration.FromDays(account.LookbackDays));
    }

    // ==================================================================
    // Öneriler
    // ==================================================================

    /// <summary>
    /// Okunmuş iletilerden öneri üretir. Daha önce görülmüş bir ileti ikinci
    /// kez önerilmez — kullanıcı reddettiyse de.
    /// </summary>
    /// <returns>Yeni açılan öneri sayısı.</returns>
    public async Task<int> IngestAsync(
        Guid accountId,
        IReadOnlyList<IncomingMessage> messages,
        string zoneId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) return 0;

        var incomingIds = messages.Select(m => m.MessageId).ToList();

        var known = await db.EventProposals
            .AsNoTracking()
            .Where(p => p.MailAccountId == accountId && incomingIds.Contains(p.MessageId))
            .Select(p => p.MessageId)
            .ToListAsync(ct).ConfigureAwait(false);

        var seen = known.ToHashSet(StringComparer.Ordinal);

        var pending = await db.EventProposals
            .CountAsync(p => p.MailAccountId == accountId && p.Status == ProposalStatus.Pending, ct)
            .ConfigureAwait(false);

        var created = 0;

        foreach (var message in messages)
        {
            if (pending + created >= MaxPendingPerAccount) break;
            if (!seen.Add(message.MessageId)) continue;

            var now = zones.ToLocal(message.ReceivedAt, zoneId);
            var extracted = MailEventExtractor.Extract(message.Subject, message.Body, now);

            // Her postadan öneri üretmek, öneri kutusunu işe yaramaz kılardı.
            if (extracted is null) continue;

            db.EventProposals.Add(new EventProposal
            {
                MailAccountId = accountId,
                MessageId = message.MessageId,
                Subject = message.Subject,
                From = message.From,
                Snippet = extracted.Evidence,
                ReceivedAt = message.ReceivedAt,
                Title = extracted.Title,
                StartLocal = extracted.Start,
                EndLocal = extracted.End,
                IsAllDay = extracted.IsAllDay,
                LocationText = extracted.LocationText,
                OnlineMeetingUrl = extracted.OnlineMeetingUrl,
                RecognizedSchedule = extracted.RecognizedSchedule,
                CreatedAt = clock.GetCurrentInstant().ToDateTimeOffset(),
            });

            created++;
        }

        if (created > 0) await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return created;
    }

    /// <summary>Kullanıcının bekleyen önerileri; yeniden eskiye.</summary>
    public Task<List<EventProposal>> GetPendingAsync(Guid userId, CancellationToken ct = default)
        => db.EventProposals
            .AsNoTracking()
            .Include(p => p.Account)
            .Where(p => p.Account!.UserId == userId && p.Status == ProposalStatus.Pending)
            .OrderByDescending(p => p.ReceivedAt)
            .ToListAsync(ct);

    public Task<int> CountPendingAsync(Guid userId, CancellationToken ct = default)
        => db.EventProposals
            .CountAsync(p => p.Account!.UserId == userId && p.Status == ProposalStatus.Pending, ct);

    public Task<EventProposal?> FindAsync(Guid proposalId, CancellationToken ct = default)
        => db.EventProposals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == proposalId, ct);

    /// <summary>Öneri takvime eklendi olarak işaretlenir.</summary>
    public async Task MarkAcceptedAsync(
        Guid proposalId, Guid createdEventId, CancellationToken ct = default)
    {
        var proposal = await db.EventProposals
            .FirstOrDefaultAsync(p => p.Id == proposalId, ct).ConfigureAwait(false);

        if (proposal is null) return;

        proposal.Status = ProposalStatus.Accepted;
        proposal.CreatedEventId = createdEventId;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Öneri reddedilir. Kayıt silinmez: aynı posta bir sonraki taramada
    /// yeniden önerilirse kullanıcı aynı şeyi ikinci kez reddetmek zorunda kalır.
    /// </summary>
    public async Task DismissAsync(Guid proposalId, CancellationToken ct = default)
    {
        var proposal = await db.EventProposals
            .FirstOrDefaultAsync(p => p.Id == proposalId, ct).ConfigureAwait(false);

        if (proposal is null) return;

        proposal.Status = ProposalStatus.Dismissed;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Bekleyen tüm önerileri reddeder.</summary>
    public async Task DismissAllAsync(Guid userId, CancellationToken ct = default)
        => await db.EventProposals
            .Where(p => p.Account!.UserId == userId && p.Status == ProposalStatus.Pending)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.Status, ProposalStatus.Dismissed), ct)
            .ConfigureAwait(false);
}
