using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Ics;
using Takvim.Core.Permissions;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// CalDAV veri katmanı.
/// <para>
/// Sınanan asıl kural: bir CalDAV kaynağı = bir UID'ye ait <b>tüm</b> satırlar.
/// Seri kökü ve istisnaları tek dosyada birlikte okunur, birlikte yazılır ve
/// etiketleri birlikte değişir. Yalnızca kökün etiketi kullanılsaydı, bir
/// istisna değiştiğinde istemci kaynağın değiştiğini fark etmezdi.
/// </para>
/// </summary>
public class CalDavTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly CalDavStore _store;

    public CalDavTests()
        => _store = new CalDavStore(_t.Db, _t.Permissions, new IcsSerializer(_t.Zones), _t.Clock);

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    private const string Uid = "sinama-1@takvim.local";

    private static string SeriesIcs(string summary = "Haftalık toplantı") => $"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Sınama//TR
        BEGIN:VEVENT
        UID:{Uid}
        DTSTAMP:20260302T090000Z
        DTSTART;TZID=Europe/Istanbul:20260302T090000
        DTEND;TZID=Europe/Istanbul:20260302T100000
        SUMMARY:{summary}
        RRULE:FREQ=WEEKLY;BYDAY=MO
        END:VEVENT
        END:VCALENDAR
        """;

    private static string SeriesWithExceptionIcs() => $"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Sınama//TR
        BEGIN:VEVENT
        UID:{Uid}
        DTSTAMP:20260302T090000Z
        DTSTART;TZID=Europe/Istanbul:20260302T090000
        DTEND;TZID=Europe/Istanbul:20260302T100000
        SUMMARY:Haftalık toplantı
        RRULE:FREQ=WEEKLY;BYDAY=MO
        END:VEVENT
        BEGIN:VEVENT
        UID:{Uid}
        RECURRENCE-ID;TZID=Europe/Istanbul:20260309T090000
        DTSTAMP:20260302T090000Z
        DTSTART;TZID=Europe/Istanbul:20260309T140000
        DTEND;TZID=Europe/Istanbul:20260309T150000
        SUMMARY:Bu hafta ertelendi
        END:VEVENT
        END:VCALENDAR
        """;

    private Task<CalDavWriteResult> PutAsync(string ics, string? ifMatch = null)
        => _store.PutResourceAsync(_t.UserId, _t.CalendarId, Uid, ics, ifMatch);

    // ==================================================================
    // Koleksiyonlar
    // ==================================================================

    [Fact]
    public async Task Kendi_takvimleri_yazilabilir_gelir()
    {
        var collections = await _store.GetCollectionsAsync(_t.UserId);

        Assert.Single(collections);
        Assert.False(collections[0].IsReadOnly);
    }

    [Fact]
    public async Task Paylasilan_takvim_yetkiye_gore_salt_okunur_olur()
    {
        var (ayse, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        _t.Share(_t.CalendarId, ayse, SharingLevel.FullDetails);

        var collections = await _store.GetCollectionsAsync(ayse);
        var shared = collections.Single(c => c.Calendar.Id == _t.CalendarId);

        Assert.True(shared.IsReadOnly);
    }

    [Fact]
    public async Task Duzenleme_yetkili_paylasim_yazilabilir_gelir()
    {
        var (ayse, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        _t.Share(_t.CalendarId, ayse, SharingLevel.CanEdit);

        var collections = await _store.GetCollectionsAsync(ayse);

        Assert.False(collections.Single(c => c.Calendar.Id == _t.CalendarId).IsReadOnly);
    }

    [Fact]
    public async Task Paylasilmamis_takvim_hic_gorunmez()
    {
        var (ayse, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        var collections = await _store.GetCollectionsAsync(ayse);

        Assert.DoesNotContain(collections, c => c.Calendar.Id == _t.CalendarId);
    }

    // ==================================================================
    // Yazma
    // ==================================================================

    [Fact]
    public async Task Yeni_kaynak_olusturulur()
    {
        var result = await PutAsync(SeriesIcs());
        _t.Detach();

        Assert.Equal(CalDavWriteOutcome.Created, result.Outcome);
        Assert.NotNull(result.ETag);

        var stored = await _t.Db.Events.SingleAsync();
        Assert.Equal("Haftalık toplantı", stored.Title);
        Assert.Equal(Uid, stored.Uid);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", stored.RecurrenceRule);
    }

    [Fact]
    public async Task Ikinci_yazma_guncelleme_sayilir()
    {
        await PutAsync(SeriesIcs());
        _t.Detach();

        var result = await PutAsync(SeriesIcs("Yeni başlık"));
        _t.Detach();

        Assert.Equal(CalDavWriteOutcome.Updated, result.Outcome);
        Assert.Equal("Yeni başlık", (await _t.Db.Events.SingleAsync()).Title);
    }

    [Fact]
    public async Task Istisna_veritabaninda_seri_kokune_baglanir()
    {
        // Gelen dosyadaki kimlikler ayrıştırma sırasında yeni üretilir; istisna
        // veritabanındaki köke bağlanmazsa yabancı anahtar kırılır.
        await PutAsync(SeriesIcs());
        _t.Detach();
        await PutAsync(SeriesWithExceptionIcs());
        _t.Detach();

        var rows = await _t.Db.Events.OrderBy(e => e.RecurrenceId).ToListAsync();

        Assert.Equal(2, rows.Count);

        var root = rows.Single(e => e.RecurrenceId is null);
        var exception = rows.Single(e => e.RecurrenceId is not null);

        Assert.Equal(root.Id, exception.SeriesId);
        Assert.Equal(TestDatabase.Parse("2026-03-09 09:00"), exception.RecurrenceId);
        Assert.Equal("Bu hafta ertelendi", exception.Title);
    }

    [Fact]
    public async Task Dosyadan_cikarilan_istisna_silinir()
    {
        await PutAsync(SeriesWithExceptionIcs());
        _t.Detach();
        Assert.Equal(2, await _t.Db.Events.CountAsync());

        // İstisna olmadan yeniden yazılırsa örnek seriye geri döner.
        await PutAsync(SeriesIcs());
        _t.Detach();

        Assert.Single(await _t.Db.Events.ToListAsync());
    }

    [Fact]
    public async Task Adresteki_uid_dosyadakini_ezer()
    {
        // Bazı istemciler dosyada başka bir UID gönderir; adres esastır.
        var ics = SeriesIcs().Replace(Uid, "baska-uid@ornek", StringComparison.Ordinal);

        await PutAsync(ics);
        _t.Detach();

        Assert.Equal(Uid, (await _t.Db.Events.SingleAsync()).Uid);
    }

    [Fact]
    public async Task Bozuk_ics_reddedilir()
    {
        var result = await PutAsync("bu bir takvim dosyası değil");

        Assert.Equal(CalDavWriteOutcome.Invalid, result.Outcome);
        Assert.Empty(await _t.Db.Events.ToListAsync());
    }

    [Fact]
    public async Task Salt_okunur_koleksiyona_yazilamaz()
    {
        var (ayse, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        _t.Share(_t.CalendarId, ayse, SharingLevel.FullDetails);

        var result = await _store.PutResourceAsync(ayse, _t.CalendarId, Uid, SeriesIcs(), null);

        Assert.Equal(CalDavWriteOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task Erisilemeyen_takvime_yazilamaz()
    {
        var (ayse, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        var result = await _store.PutResourceAsync(ayse, _t.CalendarId, Uid, SeriesIcs(), null);

        Assert.Equal(CalDavWriteOutcome.NotFound, result.Outcome);
    }

    // ==================================================================
    // Etiketler ve çakışma
    // ==================================================================

    [Fact]
    public async Task Yanlis_etiketle_yazma_reddedilir()
    {
        await PutAsync(SeriesIcs());
        _t.Detach();

        var result = await PutAsync(SeriesIcs("Değişik"), ifMatch: "\"yanlis\"");

        Assert.Equal(CalDavWriteOutcome.Conflict, result.Outcome);
        Assert.Equal("Haftalık toplantı", (await _t.Db.Events.SingleAsync()).Title);
    }

    [Fact]
    public async Task Dogru_etiketle_yazma_gecer()
    {
        var created = await PutAsync(SeriesIcs());
        _t.Detach();

        var result = await PutAsync(SeriesIcs("Değişik"), ifMatch: $"\"{created.ETag}\"");

        Assert.Equal(CalDavWriteOutcome.Updated, result.Outcome);
    }

    [Fact]
    public async Task Yildiz_etiketi_var_olan_kaynakla_eslesir()
    {
        await PutAsync(SeriesIcs());
        _t.Detach();

        Assert.Equal(CalDavWriteOutcome.Updated, (await PutAsync(SeriesIcs("Değişik"), ifMatch: "*")).Outcome);
    }

    [Fact]
    public async Task Istisna_degisince_kaynak_etiketi_degisir()
    {
        // Etiket yalnızca kökten türetilseydi bu değişiklik istemciye görünmezdi.
        var first = await PutAsync(SeriesIcs());
        _t.Detach();

        var second = await PutAsync(SeriesWithExceptionIcs());
        _t.Detach();

        Assert.NotEqual(first.ETag, second.ETag);
    }

    // ==================================================================
    // Okuma
    // ==================================================================

    [Fact]
    public async Task Kaynak_kok_ve_istisnayi_birlikte_dondurur()
    {
        await PutAsync(SeriesWithExceptionIcs());
        _t.Detach();

        var content = await _store.GetResourceAsync(_t.CalendarId, Uid);

        Assert.NotNull(content);

        var vevents = content.Value.Ics.Split("BEGIN:VEVENT").Length - 1;
        Assert.Equal(2, vevents);
        Assert.Contains("RECURRENCE-ID", content.Value.Ics, StringComparison.Ordinal);
        Assert.Contains("Bu hafta ertelendi", content.Value.Ics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Olmayan_kaynak_null_doner()
        => Assert.Null(await _store.GetResourceAsync(_t.CalendarId, "yok@ornek"));

    [Fact]
    public async Task Kaynak_listesi_uid_basina_tek_satir_verir()
    {
        await PutAsync(SeriesWithExceptionIcs());
        _t.Detach();

        var resources = await _store.ListResourcesAsync(_t.CalendarId);

        // İki veritabanı satırı var ama tek bir CalDAV kaynağı.
        Assert.Single(resources);
        Assert.Equal(Uid, resources[0].Uid);
    }

    // ==================================================================
    // Silme
    // ==================================================================

    [Fact]
    public async Task Silme_tum_satirlari_cop_kutusuna_atar()
    {
        await PutAsync(SeriesWithExceptionIcs());
        _t.Detach();

        var result = await _store.DeleteResourceAsync(_t.UserId, _t.CalendarId, Uid, null);
        _t.Detach();

        Assert.Equal(CalDavWriteOutcome.Deleted, result.Outcome);
        Assert.Empty(await _store.ListResourcesAsync(_t.CalendarId));

        // Telefondan yanlışlıkla silinen etkinlik geri alınabilmeli.
        Assert.Equal(2, await _t.Db.Events.CountAsync(e => e.DeletedAt != null));
    }

    [Fact]
    public async Task Olmayan_kaynagi_silmek_bulunamadi_doner()
        => Assert.Equal(CalDavWriteOutcome.NotFound,
            (await _store.DeleteResourceAsync(_t.UserId, _t.CalendarId, "yok@ornek", null)).Outcome);

    [Fact]
    public async Task Yanlis_etiketle_silme_reddedilir()
    {
        await PutAsync(SeriesIcs());
        _t.Detach();

        var result = await _store.DeleteResourceAsync(_t.UserId, _t.CalendarId, Uid, "\"yanlis\"");

        Assert.Equal(CalDavWriteOutcome.Conflict, result.Outcome);
        Assert.Single(await _store.ListResourcesAsync(_t.CalendarId));
    }

    // ==================================================================
    // Artımlı senkronizasyon
    // ==================================================================

    [Fact]
    public async Task Ilk_senkron_her_seyi_degismis_sayar()
    {
        await PutAsync(SeriesIcs());
        _t.Detach();

        var changes = await _store.GetChangesAsync(_t.CalendarId, since: 0);

        Assert.Single(changes.Changed);
        Assert.Empty(changes.Removed);
        Assert.True(changes.SyncToken > 0);
    }

    [Fact]
    public async Task Degisiklik_yoksa_bos_doner()
    {
        await PutAsync(SeriesIcs());
        _t.Detach();

        var first = await _store.GetChangesAsync(_t.CalendarId, since: 0);
        var second = await _store.GetChangesAsync(_t.CalendarId, first.SyncToken);

        Assert.Empty(second.Changed);
        Assert.Empty(second.Removed);
    }

    [Fact]
    public async Task Guncelleme_artimli_senkronda_gorunur()
    {
        await PutAsync(SeriesIcs());
        _t.Detach();

        var token = (await _store.GetChangesAsync(_t.CalendarId, 0)).SyncToken;

        await PutAsync(SeriesIcs("Yeni başlık"));
        _t.Detach();

        var changes = await _store.GetChangesAsync(_t.CalendarId, token);

        Assert.Single(changes.Changed);
        Assert.Equal(Uid, changes.Changed[0].Uid);
    }

    [Fact]
    public async Task Silinen_kaynak_artimli_senkronda_bildirilir()
    {
        await PutAsync(SeriesIcs());
        _t.Detach();

        var token = (await _store.GetChangesAsync(_t.CalendarId, 0)).SyncToken;

        await _store.DeleteResourceAsync(_t.UserId, _t.CalendarId, Uid, null);
        _t.Detach();

        var changes = await _store.GetChangesAsync(_t.CalendarId, token);

        Assert.Empty(changes.Changed);
        Assert.Single(changes.Removed);
        Assert.Equal(Uid, changes.Removed[0]);
    }

    // ==================================================================
    // Koleksiyon etiketi
    // ==================================================================

    [Fact]
    public async Task Koleksiyon_etiketi_degisiklikle_ilerler()
    {
        var before = await _store.CTagAsync(_t.CalendarId);

        await PutAsync(SeriesIcs());
        _t.Detach();

        Assert.NotEqual(before, await _store.CTagAsync(_t.CalendarId));
    }

    [Fact]
    public async Task Aralik_sorgusu_pencere_disini_elemez_ama_seriyi_tutar()
    {
        // Tekrarlayan seri, başlangıcı pencereden önce olsa da örnek üretebilir.
        await PutAsync(SeriesIcs());
        _t.Detach();

        var inRange = await _store.ListResourcesInRangeAsync(
            _t.CalendarId, _t.Utc("2026-06-01 00:00"), _t.Utc("2026-06-08 00:00"));

        Assert.Single(inRange);
    }

    [Fact]
    public async Task Tekrarlamayan_etkinlik_pencere_disinda_elenir()
    {
        const string single = $"""
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Sınama//TR
            BEGIN:VEVENT
            UID:tekil@ornek
            DTSTAMP:20260302T090000Z
            DTSTART;TZID=Europe/Istanbul:20260302T090000
            DTEND;TZID=Europe/Istanbul:20260302T100000
            SUMMARY:Tek seferlik
            END:VEVENT
            END:VCALENDAR
            """;

        await _store.PutResourceAsync(_t.UserId, _t.CalendarId, "tekil@ornek", single, null);
        _t.Detach();

        var inRange = await _store.ListResourcesInRangeAsync(
            _t.CalendarId, _t.Utc("2026-06-01 00:00"), _t.Utc("2026-06-08 00:00"));

        Assert.Empty(inRange);
    }
}
