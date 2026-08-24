using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;

namespace Takvim.Data.Services;

/// <summary>Yeni üretilmiş parola. Açık metin yalnızca bu anda görülebilir.</summary>
/// <param name="Record">Saklanan kayıt.</param>
/// <param name="PlainText">Kullanıcıya gösterilecek parola; hiçbir yerde saklanmaz.</param>
public sealed record IssuedAppPassword(AppPassword Record, string PlainText);

/// <summary>
/// Uygulama parolalarını üretir ve doğrular.
/// <para>
/// Parola açık metin olarak hiç saklanmaz. PBKDF2-SHA256 ile 210.000 tur
/// türetilir; bu, OWASP'ın 2023 önerisidir ve yerel bir veritabanı dosyası
/// başkasının eline geçse bile parolaların kaba kuvvetle çözülmesini pahalı kılar.
/// </para>
/// </summary>
public sealed class AppPasswordService(TakvimDbContext db, IClock clock)
{
    /// <summary>PBKDF2 tur sayısı.</summary>
    private const int Iterations = 210_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>
    /// Parola alfabesi. Karıştırılabilecek karakterler (0/O, 1/l/I) çıkarıldı:
    /// kullanıcı bunu telefonuna elle yazacak.
    /// </summary>
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>Üretilecek parolanın karakter sayısı.</summary>
    private const int Length = 20;

    /// <summary>Kullanıcının parolaları, en yeni önce.</summary>
    public Task<List<AppPassword>> GetAsync(Guid userId, CancellationToken ct = default)
        => db.AppPasswords
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

    /// <summary>
    /// Yeni bir parola üretir. Açık metin yalnızca dönen nesnede bulunur ve
    /// saklanmaz; kullanıcı kaydetmezse yenisini üretmek gerekir.
    /// </summary>
    public async Task<IssuedAppPassword> IssueAsync(
        Guid userId, string label, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var plain = Generate();
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);

        var record = new AppPassword
        {
            UserId = userId,
            Label = label.Trim(),
            Salt = salt,
            Hash = Derive(plain, salt),
            Prefix = plain[..4],
        };

        db.AppPasswords.Add(record);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new IssuedAppPassword(record, plain);
    }

    /// <summary>Parolayı iptal eder. İptal edilen parola bir daha kabul edilmez.</summary>
    public async Task RevokeAsync(Guid passwordId, CancellationToken ct = default)
    {
        var record = await db.AppPasswords
            .FirstOrDefaultAsync(p => p.Id == passwordId, ct).ConfigureAwait(false);

        if (record is null || record.RevokedAt is not null) return;

        record.RevokedAt = clock.GetCurrentInstant().ToDateTimeOffset();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// E-posta ve parolayı doğrular; başarılıysa kullanıcıyı döner.
    /// <para>
    /// Kullanıcı bulunamadığında da özet hesaplanır: aksi hâlde yanıt süresi
    /// e-postanın kayıtlı olup olmadığını ele verir.
    /// </para>
    /// </summary>
    public async Task<User?> AuthenticateAsync(
        string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return null;

        var normalized = email.Trim().ToLowerInvariant();

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == normalized, ct).ConfigureAwait(false);

        if (user is null)
        {
            // Sahte bir doğrulama yaparak süreyi eşitleriz.
            Derive(password, RandomNumberGenerator.GetBytes(SaltBytes));
            return null;
        }

        var candidates = await db.AppPasswords
            .Where(p => p.UserId == user.Id && p.RevokedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            var derived = Derive(password, candidate.Salt);

            // Sabit süreli karşılaştırma: eşleşmeyen baytın yeri sızmasın.
            if (!CryptographicOperations.FixedTimeEquals(derived, candidate.Hash)) continue;

            candidate.LastUsedAt = clock.GetCurrentInstant().ToDateTimeOffset();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return user;
        }

        return null;
    }

    /// <summary>Kullanıcının etkin parolası var mı; yoksa CalDAV'a bağlanamaz.</summary>
    public Task<bool> HasActiveAsync(Guid userId, CancellationToken ct = default)
        => db.AppPasswords.AnyAsync(p => p.UserId == userId && p.RevokedAt == null, ct);

    // ------------------------------------------------------------------

    /// <summary>Okunması ve elle yazılması kolay bir parola üretir: "abcd-efgh-ijkl-mnpq-rstu".</summary>
    private static string Generate()
    {
        var raw = new char[Length];
        for (var i = 0; i < Length; i++)
        {
            raw[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        var grouped = new StringBuilder(Length + Length / 4);
        for (var i = 0; i < Length; i++)
        {
            if (i > 0 && i % 4 == 0) grouped.Append('-');
            grouped.Append(raw[i]);
        }

        return grouped.ToString();
    }

    private static byte[] Derive(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
}
