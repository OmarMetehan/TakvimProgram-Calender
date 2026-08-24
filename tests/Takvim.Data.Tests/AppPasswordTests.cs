using Microsoft.EntityFrameworkCore;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Uygulama parolaları. CalDAV ağa açılabildiği için buradaki davranış
/// güvenlik sınırıdır: parola açık metin saklanmamalı, iptal edilen parola
/// çalışmamalı, yanlış e-posta ile doğru parola geçmemeli.
/// </summary>
public class AppPasswordTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly AppPasswordService _passwords;

    public AppPasswordTests()
        => _passwords = new AppPasswordService(_t.Db, _t.Clock);

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    private string Email => _t.Db.Users.AsNoTracking().First(u => u.Id == _t.UserId).Email;

    // ==================================================================
    // Üretim
    // ==================================================================

    [Fact]
    public async Task Uretilen_parola_okunabilir_bicimde_gelir()
    {
        var issued = await _passwords.IssueAsync(_t.UserId, "iPhone");

        // "abcd-efgh-ijkl-mnpq-rstu": 20 karakter + 4 tire.
        Assert.Equal(24, issued.PlainText.Length);
        Assert.Equal(4, issued.PlainText.Count(c => c == '-'));

        // Karıştırılabilecek karakterler alfabede yok.
        Assert.DoesNotContain('0', issued.PlainText);
        Assert.DoesNotContain('1', issued.PlainText);
        Assert.DoesNotContain('l', issued.PlainText);
    }

    [Fact]
    public async Task Her_parola_farkli_uretilir()
    {
        var first = await _passwords.IssueAsync(_t.UserId, "Bir");
        var second = await _passwords.IssueAsync(_t.UserId, "İki");

        Assert.NotEqual(first.PlainText, second.PlainText);
        Assert.NotEqual(first.Record.Salt, second.Record.Salt);
    }

    [Fact]
    public async Task Parolanin_kendisi_saklanmaz()
    {
        var issued = await _passwords.IssueAsync(_t.UserId, "iPhone");
        _t.Detach();

        var stored = await _t.Db.AppPasswords.AsNoTracking().SingleAsync();
        var storedText = System.Text.Encoding.UTF8.GetString(stored.Hash);

        Assert.DoesNotContain(issued.PlainText, storedText, StringComparison.Ordinal);
        // Yalnızca ilk dört karakter, ayırt etmek için tutulur.
        Assert.Equal(issued.PlainText[..4], stored.Prefix);
    }

    [Fact]
    public async Task Etiket_kaydedilir()
    {
        await _passwords.IssueAsync(_t.UserId, "  İş dizüstü  ");
        _t.Detach();

        Assert.Equal("İş dizüstü", (await _passwords.GetAsync(_t.UserId))[0].Label);
    }

    [Fact]
    public async Task Bos_etiket_reddedilir()
        => await Assert.ThrowsAsync<ArgumentException>(() => _passwords.IssueAsync(_t.UserId, "   "));

    // ==================================================================
    // Doğrulama
    // ==================================================================

    [Fact]
    public async Task Dogru_parola_kullaniciyi_dondurur()
    {
        var issued = await _passwords.IssueAsync(_t.UserId, "iPhone");
        _t.Detach();

        var user = await _passwords.AuthenticateAsync(Email, issued.PlainText);

        Assert.NotNull(user);
        Assert.Equal(_t.UserId, user.Id);
    }

    [Fact]
    public async Task Yanlis_parola_reddedilir()
    {
        await _passwords.IssueAsync(_t.UserId, "iPhone");
        _t.Detach();

        Assert.Null(await _passwords.AuthenticateAsync(Email, "abcd-efgh-ijkl-mnpq-rstu"));
    }

    [Fact]
    public async Task Bilinmeyen_eposta_reddedilir()
    {
        var issued = await _passwords.IssueAsync(_t.UserId, "iPhone");
        _t.Detach();

        Assert.Null(await _passwords.AuthenticateAsync("yok@ornek.local", issued.PlainText));
    }

    [Fact]
    public async Task Baskasinin_parolasi_kabul_edilmez()
    {
        var (ayse, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        var issued = await _passwords.IssueAsync(ayse, "Ayşe telefonu");
        _t.Detach();

        // Ayşe'nin parolası benim e-postamla çalışmamalı.
        Assert.Null(await _passwords.AuthenticateAsync(Email, issued.PlainText));
    }

    [Fact]
    public async Task Eposta_buyuk_kucuk_harf_duyarsizdir()
    {
        var issued = await _passwords.IssueAsync(_t.UserId, "iPhone");
        _t.Detach();

        Assert.NotNull(await _passwords.AuthenticateAsync(Email.ToUpperInvariant(), issued.PlainText));
    }

    [Theory]
    [InlineData("", "parola")]
    [InlineData("a@b.c", "")]
    [InlineData("   ", "   ")]
    public async Task Bos_bilgi_reddedilir(string email, string password)
        => Assert.Null(await _passwords.AuthenticateAsync(email, password));

    [Fact]
    public async Task Birden_cok_parola_ayri_ayri_calisir()
    {
        var phone = await _passwords.IssueAsync(_t.UserId, "iPhone");
        var laptop = await _passwords.IssueAsync(_t.UserId, "Dizüstü");
        _t.Detach();

        Assert.NotNull(await _passwords.AuthenticateAsync(Email, phone.PlainText));
        Assert.NotNull(await _passwords.AuthenticateAsync(Email, laptop.PlainText));
    }

    // ==================================================================
    // İptal
    // ==================================================================

    [Fact]
    public async Task Iptal_edilen_parola_calismaz()
    {
        var issued = await _passwords.IssueAsync(_t.UserId, "Kaybolan telefon");
        _t.Detach();

        await _passwords.RevokeAsync(issued.Record.Id);
        _t.Detach();

        Assert.Null(await _passwords.AuthenticateAsync(Email, issued.PlainText));
    }

    [Fact]
    public async Task Bir_parolayi_iptal_etmek_otekileri_etkilemez()
    {
        var phone = await _passwords.IssueAsync(_t.UserId, "iPhone");
        var laptop = await _passwords.IssueAsync(_t.UserId, "Dizüstü");
        _t.Detach();

        await _passwords.RevokeAsync(phone.Record.Id);
        _t.Detach();

        Assert.Null(await _passwords.AuthenticateAsync(Email, phone.PlainText));
        Assert.NotNull(await _passwords.AuthenticateAsync(Email, laptop.PlainText));
    }

    [Fact]
    public async Task Iptal_edilen_parola_listede_kalir()
    {
        var issued = await _passwords.IssueAsync(_t.UserId, "Kaybolan");
        _t.Detach();
        await _passwords.RevokeAsync(issued.Record.Id);
        _t.Detach();

        var all = await _passwords.GetAsync(_t.UserId);

        // Kayıt silinmez; kullanıcı geçmişi görebilmeli.
        Assert.Single(all);
        Assert.False(all[0].IsActive);
        Assert.NotNull(all[0].RevokedAt);
    }

    [Fact]
    public async Task Olmayan_parolayi_iptal_etmek_hata_vermez()
        => await _passwords.RevokeAsync(Guid.NewGuid());

    // ==================================================================
    // Kullanım izi
    // ==================================================================

    [Fact]
    public async Task Basarili_dogrulama_son_kullanimi_isaretler()
    {
        var issued = await _passwords.IssueAsync(_t.UserId, "iPhone");
        _t.Detach();

        Assert.Null((await _passwords.GetAsync(_t.UserId))[0].LastUsedAt);

        await _passwords.AuthenticateAsync(Email, issued.PlainText);
        _t.Detach();

        Assert.NotNull((await _passwords.GetAsync(_t.UserId))[0].LastUsedAt);
    }

    [Fact]
    public async Task Etkin_parola_yoksa_bildirilir()
    {
        Assert.False(await _passwords.HasActiveAsync(_t.UserId));

        var issued = await _passwords.IssueAsync(_t.UserId, "iPhone");
        _t.Detach();
        Assert.True(await _passwords.HasActiveAsync(_t.UserId));

        await _passwords.RevokeAsync(issued.Record.Id);
        _t.Detach();
        Assert.False(await _passwords.HasActiveAsync(_t.UserId));
    }
}
