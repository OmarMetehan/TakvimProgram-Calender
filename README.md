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
├─ Takvim.Core.Tests/   179 test — tekrarlama, zaman dilimi, ICS, ayrıştırıcı,
│                       tatiller, izin motoru, müsaitlik hesabı
└─ Takvim.Data.Tests/   122 test — seri düzenleme, silme, geri alma, hatırlatıcılar,
                        çalışma düzeni, zamanlama, paylaşım yalıtımı, RSVP,
                        taşınan toplantıda eskiyen yanıtlar
```

Veri katmanı testleri gerçek SQLite üzerinde çalışır (bellek içi dosya), EF Core'un
InMemory sağlayıcısıyla değil: zaman dilimi dönüştürücüleri ve indeksler ancak
gerçek sağlayıcıda sınanabilir.

```
Testler.cmd
```

> **Smart App Control notu.** Bu makinede Smart App Control açık
> (`VerifiedAndReputablePolicyState = 1`). Microsoft imzalı `testhost.exe`
> içine yüklenen imzasız test derlemelerini engelliyor (`0x800711C7`), bu yüzden
> `dotnet test` çalışmıyor. Çözüm: test projeleri **xUnit v3** kullanıyor ve
> kendi süreçleri olarak çalışıyor — uygulamanın kendisi (`Takvim.exe`) de aynı
> nedenle sorunsuz açılıyor. `Testler.cmd` iki test yürütülebilirini doğrudan
> çağırır.

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

Uygulaması `Takvim.Core/Permissions` içindedir ve **tek kapı kuralı** geçerlidir:
etkinlik okuyan her yol `CalendarQueryService` üzerinden geçer, orada her örnek
izin çözümlemesinden geçirilir. Görünmemesi gerekenler elenir, kısıtlı olanların
kaynağı **karartılmış bir kopyayla değiştirilir** — arayüz yanlışlıkla ham
başlığı okusa bile gizli veri sızmaz. Bu davranış testlerle korunur.

---

## Faz 2 — durum

| Alan | Durum |
|---|---|
| İzin motoru — dört boyutlu karar tablosunun uygulaması | ✅ |
| Gün bazında çalışma saatleri ve öğle arası | ✅ |
| Çalışma konumu (ofis / evden / şube), gün başlığında görünür | ✅ |
| Tüm etkinlik durumları (meşgul, müsait, belirsiz, ofis dışı, odak, başka yerde) | ✅ |
| Denetim kaydı | ✅ (Faz 1) |
| Zamanlama yardımcısı — katılımcıların müsaitliği ve önerilen aralıklar | ✅ |
| Katılımcılar, zorunlu/isteğe bağlı ayrımı, katılımcı yetkileri | ✅ |
| RSVP (katılacağım / belki / katılmayacağım), açıklama notu, katılım şekli | ✅ |
| Yanıt takip paneli, sayaçlı özet | ✅ |
| Yeni zaman önerme, organizatörün tek tıkla kabulü | ✅ |
| Toplantı taşınınca yanıtların "eski saate göre" işaretlenmesi | ✅ |
| Takvim paylaşımı, beş kademeli izin seviyesi | ✅ |
| Vekil erişimi ve özel öğelerin vekilden gizlenmesi | ✅ |
| CalDAV sunucusu | ⏸ |
| E-posta ile davet (iTIP) | ⏸ tasarım gereği yok |

### Toplantı taşınınca yanıtlara ne olur

Yanıtlar **silinmez**. Her yanıtla birlikte "toplantı o an hangi saatteydi"
kaydedilir; toplantı sonradan taşınırsa yanıt *eski saate göre* diye işaretlenir
ve panelde uyarı rozetiyle görünür. Davetlinin ekranında da "saat değişti,
yanıtınızı güncelleyin" uyarısı çıkar.

Gerekçe: yanıtı silmek bilgiyi yok etmektir. *"Katılacak (eski saate göre)"*,
*"yanıt yok"*tan daha fazlasını söyler — organizatör kimin zaten hevesli
olduğunu, kime ayrıca sorması gerektiğini ayırt edebilir. Ayrıca toplantıyı on
beş dakika kaydırmak sekiz kişinin cevabını birden silmez.

Herkese yeniden sormak isteyen organizatör bunu düzenleyicideki **"Yanıtları
sıfırla, herkese yeniden sor"** düğmesiyle açıkça yapar.

### Çok kullanıcılı model

Uygulama **tek makinede, yerel hesaplarla** çalışır. Bu seçimin iki somut sonucu var:

**Paylaşımlı model, kopya modeli değil.** Tek veritabanı olduğu için davet,
katılımcının takvimine ayrı bir kopya yazmaz: tek bir etkinlik satırı vardır ve
katılımcılar ona bağlanır. Bir kişinin yanıtı ötekilerde anında görünür,
kopyalar arasında eşitleme diye bir sorun yoktur. E-posta ile davete (iTIP)
geçilirse her katılımcının kendi kopyası gerekir; `Attendee` tablosu o zaman
kopyalar arası eşleştirmeyi taşıyacak biçimde genişletilir.

**Hesap değiştirerek çalışılır.** Üst çubuktaki hesap düğmesinden geçiş yapılır;
davetleri, paylaşımları ve yanıtları görmenin yolu budur. Yeni hesap açmak
kendisine bir kişisel takvim ve varsayılan mesai düzeni de kurar.

Bu makinede hesabı olmayan biri de e-posta adresiyle davet edilebilir, ancak
davet ona ulaşmaz ve müsaitliği bilinemez; arayüz bunu açıkça belirtir.

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
| Takvimleri yan yana sütunlarda gösterme | ✅ |

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

**Mesai saatleri** artık gün bazında ayarlanabilir (kenar çubuğu → Çalışma
düzeni). Resmi tatiller bu tanımı ezer: tam gün tatilde çalışılmaz, arefe
günlerinde mesai 13:00'te biter.

---

## Kod düzeni

Sınıf ve alan adları İngilizce (kütüphanelerle uyum), yorumlar ve belgeler Türkçe,
arayüz tamamen Türkçe. Tarih biçimi `GG.AA.YYYY`, saat biçimi 24 saat, haftanın ilk
günü pazartesi.
