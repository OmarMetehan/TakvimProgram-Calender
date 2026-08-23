# Takvim

Outlook ve Google Takvim'in işlevlerini harmanlayan masaüstü takvim uygulaması.

Windows masaüstü uygulamasıdır: tek bir `Takvim.exe` açılır, içinde gömülü bir
web sunucusu yerel adreste çalışır ve arayüz bir pencerede gösterilir. Kullanıcı
için sıradan bir masaüstü programıdır; içeride ise Faz 2 ve 3'teki CalDAV sunucusu,
REST API ve paylaşım özelliklerinin doğrudan üzerine kurulabileceği bir yapıdır.

---

## Çalıştırma

```
dotnet run --project src/Takvim.Desktop
```

Ya da derlenmiş hâli:

```
src\Takvim.Desktop\bin\Debug\net10.0-windows\Takvim.exe
```

Arayüzü tarayıcıda geliştirmek için:

```
dotnet run --project src/Takvim.Server
```

**Gereksinimler:** .NET 10 SDK, Windows 10/11, WebView2 çalışma zamanı
(Windows 11'de yerleşiktir).

### Veriler nerede

```
%LOCALAPPDATA%\Takvim\
├─ takvim.db      SQLite veritabanı
├─ ekler\         etkinlik ekleri
├─ yedekler\      yedekler
├─ webview\       tarayıcı bileşeninin verileri
└─ hata.log       açılış hataları
```

---

## Yapı

```
src/
├─ Takvim.Core/     Alan modeli, tekrarlama motoru, zaman dilimi, ICS, Türkçe yerelleştirme
├─ Takvim.Data/     EF Core + SQLite, servisler, değişiklik günlüğü
├─ Takvim.Server/   ASP.NET Core + Blazor arayüz (gömülü çalışır)
└─ Takvim.Desktop/  WPF + WebView2 kabuğu

tests/
├─ Takvim.Core.Tests/   123 test — tekrarlama, zaman dilimi, ICS, ayrıştırıcı, tatiller
└─ Takvim.Data.Tests/   38 test — seri düzenleme, silme, geri alma, hatırlatıcılar
```

Veri katmanı testleri gerçek SQLite üzerinde çalışır (bellek içi dosya), EF Core'un
InMemory sağlayıcısıyla değil: zaman dilimi dönüştürücüleri ve indeksler ancak
gerçek sağlayıcıda sınanabilir.

```
dotnet test
```

---

## Mimarideki üç temel karar

**Tekrarlama.** RFC 5545 modeliyle birebir: seri kökü ve istisnaları aynı tabloda
durur, aynı `Uid`'yi paylaşır, istisnalar `RecurrenceId` (RECURRENCE-ID) ile ayrışır.
Kural üretimi Ical.Net'e bırakılmıştır. Sonuç olarak ICS dışa aktarımı ve ileride
CalDAV hiçbir dönüştürme gerektirmez.

**Zaman.** Etkinlikler UTC olarak değil, **yerel saat + IANA zaman dilimi** olarak
saklanır (`StartLocal` + `StartTimeZoneId`). `StartUtc` yalnızca sorgu önbelleğidir.
Bir ülke yaz saati kuralını değiştirdiğinde kayıtlı etkinlikler kaymaz — Türkiye'nin
2016'daki kalıcı UTC+3 geçişi testlerde bu yüzden yer alır.

**İzinler.** Paylaşım seviyesi, vekil erişimi, etkinlik görünürlüğü ve kategori
gizliliği birbirini kesen dört boyuttur. Tüm kombinasyonların cevabı kod yazılmadan
önce [docs/izin-modeli.md](docs/izin-modeli.md) dosyasında tabloya döküldü.

---

## Faz 1 — durum

| Alan | Durum |
|---|---|
| Gün / hafta / iş haftası / N gün / ay / yıl / zamanlama görünümleri | ✅ |
| Takvimleri üst üste veya yan yana sütunlarda gösterme | ✅ |
| Mini takvim, geçerli saat çizgisi, yoğunluk, hafta sonu gizleme, koyu tema | ✅ |
| Görünüm durumunun adres çubuğunda saklanması | ✅ |
| Yıl görünümünde doluluk ısı haritası | ✅ |
| Yazdırma düzenleri | ✅ |
| Izgaraya tıklayarak oluşturma, sürükle-bırak taşıma, kenardan süre uzatma | ✅ |
| Türkçe doğal dil ayrıştırma (*"perşembe 14:00 Ahmet ile toplantı"*) | ✅ |
| Hızlı oluşturma kartı ve ayrıntılı düzenleyici | ✅ |
| Tüm gün ve çok günlü etkinlikler | ✅ |
| Renk paleti ve çoklu kategori etiketi | ✅ |
| Tekrar kuralları, üç kapsamlı düzenleme, istisna yönetimi | ✅ |
| Hatırlatıcılar (uygulama içi) ve erteleme | ✅ |
| Arama ve kategori süzme | ✅ |
| ICS içe / dışa aktarma | ✅ |
| Klavye kısayolları | ✅ |
| Türkçe arayüz, resmi tatiller, arefe yarım günleri | ✅ |
| Geri alma ve 30 günlük çöp kutusu | ✅ |
| Değişiklik günlüğü (denetim + senkronizasyon imleci) | ✅ |

### Faz 1'de bilinçli olarak yapılmayanlar

- **Uygulama kapalıyken hatırlatma.** Bildirimler yalnızca uygulama açıkken çalar.
  Sistem tepsisi, global kısayol ve Windows bildirimi masaüstü katmanının işidir;
  bu katman kapsam dışında bırakıldı.
- **E-posta ve mobil bildirim kanalları.** Şema destekliyor, gönderim yok.
- **Sessiz saatler.** Bildirim bölümünün geri kalanıyla birlikte Faz 2'de.
- **Zengin metin açıklama.** Şu an düz metin; alan HTML saklayacak biçimde tanımlı.
- **Dosya ekleri.** Klasör ve yol hazır, arayüz yok.
- **Katılımcı, oda, vekil tabloları.** Faz 2/3. `Event` üzerindeki ilgili sütunlar
  şimdiden var, çünkü sütun eklemek şema göçü demektir; yeni tablo eklemek ucuzdur.

---

## Dikkat edilmesi gereken

**Dini bayram tarihleri.** `src/Takvim.Core/Localization/dini-gunler.json` dosyası
Ramazan ve Kurban Bayramı'nın ilk günlerini tablo hâlinde tutar. Hesaplanan hicri
takvim ile Diyanet'in ilan ettiği tarih bazı yıllar bir gün ayrışır ve resmi tatil
Diyanet'e göre işler. **2024–2026 doğrulanmıştır; 2027–2032 tahminidir** ve arayüzde
"tahmini" olarak işaretlenir. Diyanet ilan ettikçe bu dosya güncellenmelidir.

**Mesai saatleri** şimdilik 09:00–18:00 sabittir (arefe günlerinde 13:00). Gün
bazında ayarlanabilir hâle gelmesi Faz 2'dedir.

---

## Kod düzeni

Sınıf ve alan adları İngilizce (kütüphanelerle uyum), yorumlar ve belgeler Türkçe,
arayüz tamamen Türkçe. Tarih biçimi `GG.AA.YYYY`, saat biçimi 24 saat, haftanın ilk
günü pazartesi.
