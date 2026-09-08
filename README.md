# Takvim

Outlook ve Google Takvim'in işlevlerini harmanlayan masaüstü takvim uygulaması.

Windows masaüstü uygulamasıdır: tek bir `Takvim.exe` açılır, içinde gömülü bir
web sunucusu yerel adreste çalışır ve arayüz bir pencerede gösterilir. Kullanıcı
için sıradan bir masaüstü programıdır; içeride ise CalDAV sunucusu, paylaşım ve
çok kullanıcılı toplantı yönetimi aynı sunucu üzerinde çalışır.

---

## Genel bakış

Bu program, Outlook ve Google Takvim'in günlük kullanımda gerçekten işe yarayan
yanlarını tek bir Windows uygulamasında topluyor. Arayüzü, tarih biçimleri,
resmi tatilleri ve doğal dil ayrıştırması tamamen Türkçe: *"perşembe 14:00 Ahmet
ile toplantı"* yazmak bir etkinlik oluşturmaya yetiyor.

**Veriler makinede kalıyor.** Bulut hesabı, abonelik ya da dışarıda çalışan bir
sunucu yok; her şey `%LOCALAPPDATA%\Takvim` altındaki tek bir SQLite dosyasında
duruyor. Telefon, tablet ve Thunderbird, uygulamanın kendi içindeki CalDAV
sunucusuna bağlanarak aynı takvimi görüyor — bu sunucu varsayılan olarak
kapalı, açılmadıkça hiçbir port dinlenmiyor.

Neler yapabildiği:

- **Görünümler** — gün, hafta, iş haftası, N gün, ay, yıl ve zamanlama; ikinci
  saat dilimi sütunu, yoğunluk ısı haritası, yazdırma düzenleri, koyu tema.
- **Etkinlikler** — tekrar kuralları ve istisnaları, sürükle-bırak, üç kapsamlı
  seri düzenleme, kategoriler, ekler, biçimli açıklama, şablonlar, ICS içe/dışa
  aktarma, geri alma ve 30 günlük çöp kutusu.
- **Toplantılar** — katılımcılar, RSVP, yeni saat önerme, zamanlama yardımcısı,
  oda ve ekipman rezervasyonu, randevu sayfaları.
- **Paylaşım** — beş kademeli izin, vekil erişimi, kategori bazlı gizlilik ve
  her okuma yolunda çalışan tek kapılı izin motoru.
- **Hatırlatma** — uygulama içi şerit, sistem tepsisi bildirimi, erteleme ve
  sessiz saatler.
- **Bakım** — günlük otomatik yedek, geri yükleme, değişiklik günlüğü.

### Kullanılan teknolojiler

| Katman | Teknoloji | Neden |
|---|---|---|
| Dil ve çalışma zamanı | **.NET 10**, C# | Tek dil, tek derleme; masaüstü ve sunucu aynı çözümde |
| Masaüstü kabuğu | **WPF** + **WebView2** (tepsi simgesi için Windows Forms `NotifyIcon`) | Arayüz web teknolojisiyle çizilir ama tarayıcı gerektirmez |
| Arayüz ve sunucu | **ASP.NET Core** (Kestrel) + **Blazor Server** | Arayüz, CalDAV ve randevu sayfaları aynı gömülü sunucuda |
| Veri | **EF Core 10** + **SQLite** | Tek dosyalık veritabanı, şema göçlerle sürümlenir |
| Zaman | **NodaTime** | Yerel saat ile IANA zaman dilimini ayrı tutar; yaz saati kuralı değişince kayıtlar kaymaz |
| Takvim biçimi | **Ical.Net** | RFC 5545 tekrar kuralları ve ICS okuma/yazma |
| Senkronizasyon | Kendi **CalDAV** sunucusu (RFC 4791 / WebDAV) | iPhone, iPad, macOS Takvim ve Thunderbird doğrudan bağlanır |
| Güvenlik | PBKDF2-SHA256 uygulama parolaları, DPAPI ile korunan belirteçler | Cihaz parolaları açık metin saklanmaz |
| Testler | **xUnit v3** — 889 test | Veri testleri gerçek SQLite üzerinde çalışır |

---

## Overview (English)

**Takvim** ("calendar" in Turkish) is a desktop calendar application for
Windows that blends what Outlook and Google Calendar actually get used for into
a single program. Its interface, date formats, public holidays and natural
language parsing are entirely Turkish: typing *"perşembe 14:00 Ahmet ile
toplantı"* is enough to create an event.

**Your data stays on your machine.** There is no cloud account, subscription or
external server: everything lives in a single SQLite file under
`%LOCALAPPDATA%\Takvim`. Phones, tablets and Thunderbird sync against the
CalDAV server embedded in the application itself — that server is off by
default and opens no port until you enable it.

What it does:

- **Views** — day, week, work week, N days, month, year and scheduling; a
  secondary time zone column, a year heat map, print layouts, dark theme.
- **Events** — recurrence rules and exceptions, drag and drop, three-scope
  series editing, categories, attachments, rich text, templates, ICS import and
  export, undo and a 30-day trash.
- **Meetings** — attendees, RSVP, proposing a new time, a scheduling assistant,
  room and equipment booking, bookable appointment pages.
- **Sharing** — five permission levels, delegate access, category-level privacy,
  enforced by a single-gate permission engine every read path passes through.
- **Reminders** — in-app toasts, system tray notifications, snoozing and quiet
  hours.
- **Maintenance** — daily automatic backups, restore, an audit log.

### Technology stack

| Layer | Technology | Why |
|---|---|---|
| Language and runtime | **.NET 10**, C# | One language and one build for both desktop and server |
| Desktop shell | **WPF** + **WebView2** (Windows Forms `NotifyIcon` for the tray) | The UI is drawn with web technology without requiring a browser |
| UI and server | **ASP.NET Core** (Kestrel) + **Blazor Server** | UI, CalDAV and appointment pages run on the same embedded server |
| Data | **EF Core 10** + **SQLite** | A single-file database, versioned through migrations |
| Time | **NodaTime** | Keeps local time and IANA zone separate, so records don't shift when DST rules change |
| Calendar format | **Ical.Net** | RFC 5545 recurrence rules and ICS read/write |
| Sync | A hand-written **CalDAV** server (RFC 4791 / WebDAV) | iPhone, iPad, macOS Calendar and Thunderbird connect directly |
| Security | PBKDF2-SHA256 app passwords, DPAPI-protected tokens | Device passwords are never stored in clear text |
| Tests | **xUnit v3** — 889 tests | Data tests run against real SQLite, not an in-memory provider |

> The rest of this document is in Turkish, as are the code comments; class and
> field names are English.

---

## Çalıştırma

```
Takvim.cmd
```

Yayın derlemesini yapar ve uygulamayı başlatır. Derlenmiş hâli doğrudan da
açılabilir:

```
src\Takvim.Desktop\bin\Release\net10.0-windows\Takvim.exe
```

Geliştirirken:

```
dotnet run --project src/Takvim.Desktop     gerçek kabuk, tek pencere
dotnet run --project src/Takvim.Server      arayüz tarayıcıda
```

**Gereksinimler:** .NET 10 SDK, Windows 10/11, WebView2 çalışma zamanı
(Windows 11'de yerleşiktir).

### Veriler nerede

```
%LOCALAPPDATA%\Takvim\
├─ takvim.db      SQLite veritabanı
├─ caldav.json    CalDAV sunucu ayarları (kapalıysa dosya oluşmaz)
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
├─ Takvim.Core.Tests/   310 test — tekrarlama, zaman dilimi, ICS, ayrıştırıcı,
│                       tatiller, izin motoru, müsaitlik hesabı, biçimli metin,
│                       ikinci saat dilimi, sessiz saatler
└─ Takvim.Data.Tests/   579 test — seri düzenleme, silme, geri alma, hatırlatıcılar,
                        bildirim tercihleri, çalışma düzeni, zamanlama, paylaşım
                        yalıtımı, RSVP, eskiyen yanıtlar, paylaşım denetimi,
                        uygulama parolaları, CalDAV kaynak yönetimi, görevler,
                        kaynak rezervasyonu, randevu sayfaları, abonelikler,
                        posta önerileri, takvim ve kategori yönetimi, denetim
                        günlüğü, yedekleme
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

## Faz 3 — durum

| Alan | Durum |
|---|---|
| Görevler: bitiş tarihi, tekrar, gün başlığında listelenme | ✅ |
| Oda ve ekipman rezervasyonu, çakışma denetimi | ✅ |
| Randevu sayfaları — dışarıdan saat ayırtma | ✅ |
| Dış ICS beslemelerine abonelik (salt okunur takvimler) | ✅ |
| Posta kutusundan etkinlik çıkarma (Gmail / Outlook, öneri olarak) | ✅ |
| Dosya ekleri | ✅ |
| Biçimli açıklama, gündem ve organizatöre özel notlar | ✅ |
| Kayıtlı konumlar ve tek tıkla toplantı bağlantısı | ✅ |
| Etkinlik şablonları | ✅ |
| Gelişmiş arama ve kayıtlı aramalar | ✅ |
| Takvim sahipliğinin devri | ✅ |
| Takvim ve kategori yönetimi arayüzleri | ✅ |
| Sistem tepsisi simgesi ve Windows bildirimi | ✅ |
| Yedekleme, geri yükleme ve günlük otomatik yedek | ✅ |
| Izgarada ikinci saat dilimi sütunu | ✅ |
| Denetim günlüğü görünümü | ✅ |
| Bildirim tercihleri ve sessiz saatler | ✅ |

### Bildirimler ve sessiz saatler

Hatırlatıcılar uygulama açık olduğu sürece çalar. Pencere kapatılmak yerine
tepsiye iner: pencereyi kapatmak çıkmak değildir, çünkü çıksaydı hatırlatıcılar
da dururdu. Pencere görünmüyorken uyarı Windows bildirimi olarak çıkar, açıkken
arayüzdeki şerit olarak — ikisi birden değil.

Kenar çubuğu → **Bildirim ayarları** iki şeyi düzenler: hatırlatıcıların tümden
kapatılması ve sessiz saatler (varsayılan öneri 22:00–08:00, istenirse
çalışılmayan günlerde bütün gün).

**Sessizlik hatırlatıcıyı tüketmez.** Susturulan bir uyarı "gösterildi" sayılıp
kapatılmaz; yalnızca o turda çizilmez. Pencere kapandığında etkinlik hâlâ
yaklaşıyorsa uyarı o an çıkar — 08:30'daki toplantının bir saat önceden kurulmuş
hatırlatıcısı 07:30'da susar, 08:00'de görünür. Sabaha karşı birikmiş bir yığın
oluşmaz: hatırlatıcı motoru zaten başlangıcının üzerinden yarım saatten fazla
geçen uyarıları eler.

**Sessiz saatler hesaba özeldir** ve kullanıcının kendi zaman diliminde
hesaplanır — duvar saatidir, UTC değil. Aynı nedenle hatırlatıcı **takvim
sahibine** çalar: paylaşılan bir takvimin uyarısı, o takvimi görebilen herkesin
ekranında değil sahibinin ekranında çıkar.

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
| CalDAV sunucusu | ✅ |
| E-posta ile davet (iTIP) | ⏸ tasarım gereği yok |

### Cihaz senkronizasyonu (CalDAV)

iPhone, iPad, macOS Takvim ve Thunderbird bu takvime doğrudan bağlanabilir.
Kenar çubuğu → **Cihaz senkronizasyonu**.

**Varsayılan olarak kapalıdır** ve kapalıyken hiçbir port açılmaz. Açarken iki
seçenek var: *yalnızca bu bilgisayar* (127.0.0.1 — aynı makinedeki Thunderbird
için) ya da *bu ağdaki cihazlar* (telefon için). Port sabittir (varsayılan 5232),
çünkü istemciler adresi bir kez kaydeder.

**Kimlik doğrulama zorunludur.** Kullanıcı adı e-posta, parola ise her cihaz için
ayrı üretilen bir uygulama parolasıdır. Parolalar açık metin saklanmaz
(PBKDF2-SHA256, 210.000 tur) ve üretildikleri an bir kez gösterilir. Cihaz
kaybolursa yalnızca onun parolası iptal edilir.

**CalDAV portundan arayüz görünmez.** Ağa açıldığında o porttan yalnızca `/dav`
yolları yanıtlanır; arayüzün kendisi kimlik doğrulaması istemediği için o porta
hiç çıkmaz.

Desteklenen işlemler: `OPTIONS`, `PROPFIND` (keşif, takvim listesi, koleksiyon),
`GET`, `PUT`, `DELETE`, ve `REPORT` altında `calendar-query`,
`calendar-multiget`, `sync-collection`. Çakışma denetimi `If-Match` ile çalışır:
iki istemci aynı anda düzenlerse biri sessizce ötekini ezmez.

Bir CalDAV **kaynağı**, bir UID'ye ait *tüm* satırlardır: seri kökü ve
RECURRENCE-ID taşıyan istisnaları tek `.ics` dosyasında birlikte bulunur.
Kaynağın etiketi bu satırların hepsini kapsar — yalnızca kökünki kullanılsaydı,
bir istisna değiştiğinde istemci fark etmezdi.

Telefondan silinen etkinlik çöp kutusuna gider, kalıcı olarak silinmez.

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

### Bilinçli olarak yapılmayanlar

Faz 1'in bu listesi Faz 3'te büyük ölçüde kapandı — dosya ekleri, biçimli
açıklama, sessiz saatler, katılımcı/oda/vekil tabloları ve tepsi bildirimi
yapıldı. Bugün hâlâ kapsam dışı olanlar:

- **Uygulama hiç açık değilken hatırlatma.** Pencere kapalıyken tepsiden bildirim
  gelir, ama uygulamadan çıkıldığında hiçbir şey çalmaz. Bunun için Windows
  Görev Zamanlayıcısı'na kayıt ya da bir hizmet gerekirdi; makinede sessizce
  çalışan bir arka plan süreci, kullanıcının açıkça istemediği bir şeydir.
- **E-posta ve mobil bildirim kanalları.** Şema destekliyor, gönderim yok.
  Davetler de e-postayla gitmez (iTIP); gerekçesi *Çok kullanıcılı model*
  bölümündedir.
- **Kurulum paketi.** Program dağıtılmıyor, bu makinede derlenip çalışıyor.
  İmzasız bir kurulum paketi Smart App Control'ün önündeki engeli büyütmekten
  başka bir işe yaramazdı.

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
