# İzin modeli — karar tablosu

Bu belge kod yazılmadan önce sabitlenmiştir. Dört boyut birbirini keser ve tek bir
fonksiyondan geçer:

```
EffectiveAccess(viewer, event) -> (DetailLevel, CanEdit)
```

**Kural:** Etkinlik okuyan her yol bu fonksiyondan geçmek zorundadır. Sorgu katmanında
ikinci bir "şunları da göster" yolu açılırsa model çöker.

---

## Boyutlar

| # | Boyut | Değerler |
|---|-------|----------|
| 1 | Takvim paylaşım seviyesi | `FreeBusy` · `TitleLocation` · `FullDetails` · `CanEdit` · `FullControl` |
| 2 | Vekil erişimi | yok · vekil · vekil + *özel öğeleri görebilir* |
| 3 | Etkinlik görünürlüğü | `Default` · `Public` · `Private` |
| 4 | Kategori gizliliği | normal · en az bir `IsPrivate` kategori |

**Detay seviyeleri (çıktı):** `None` < `BusyOnly` < `TitleLocation` < `FullDetails`

---

## Çözümleme sırası

Sırayla uygulanır, **her adım yalnızca daraltabilir veya belirtilen yerde genişletebilir**.

### 0. Sahiplik
Görüntüleyen etkinliğin takviminin sahibiyse → `FullDetails` + `CanEdit`. Diğer adımlar atlanır.

### 1. Taban: takvim paylaşım seviyesi

| Paylaşım seviyesi | Taban detay | Düzenleyebilir | Kullanıcı adına davet |
|---|---|---|---|
| `FreeBusy` | `BusyOnly` | hayır | hayır |
| `TitleLocation` | `TitleLocation` | hayır | hayır |
| `FullDetails` | `FullDetails` | hayır | hayır |
| `CanEdit` | `FullDetails` | evet | hayır |
| `FullControl` | `FullDetails` | evet | **evet** |

Takvim hiç paylaşılmamışsa → `None`, işlem burada biter.

### 2. Etkinlik görünürlüğü

| Görünürlük | Etki |
|---|---|
| `Default` | Taban değişmez. |
| `Public` | Tabanı `FullDetails`'e **yükseltir**. Takvim yalnızca serbest/meşgul paylaşılsa bile bu etkinliğin detayı görünür. |
| `Private` | Tabanı `BusyOnly`'ye **düşürür**. Düzenleme yetkisi de kalkar. |

### 3. Kategori gizliliği

Etkinlikte `IsPrivate` işaretli en az bir kategori varsa → `BusyOnly`, düzenleme kapalı.

> **Çakışma kuralı:** `Public` görünürlük + gizli kategori = `BusyOnly`.
> Daraltma her zaman genişletmeyi yener. Gizli kategori mutlaktır; kullanıcı bir
> etkinliği hem "herkese açık" hem "gizli kategoride" işaretlerse gizlilik kazanır.
> Arayüz bu durumda uyarı gösterir.

### 4. Vekil erişimi

Vekil, adına hareket ettiği kullanıcının izinlerini devralır, **iki istisna dışında**:

| Durum | Sonuç |
|---|---|
| Vekil, `Private` etkinliğe bakıyor, *özel öğeleri görebilir* kapalı | `BusyOnly` |
| Vekil, gizli kategorili etkinliğe bakıyor, *özel öğeleri görebilir* kapalı | `BusyOnly` |
| *Özel öğeleri görebilir* açık | Sahip gibi davranır: `FullDetails` |

Vekilin yaptığı her işlem değişiklik günlüğüne `ActorUserId` = vekil,
`OnBehalfOfUserId` = asıl kullanıcı olarak yazılır.

---

## Kesişim tablosu — tüm anlamlı kombinasyonlar

Sütunlar: takvim paylaşım seviyesi. Satırlar: etkinliğin görünürlük ve kategori durumu.

| Etkinlik durumu | FreeBusy | TitleLocation | FullDetails | CanEdit | FullControl |
|---|---|---|---|---|---|
| Default, normal kategori | BusyOnly | TitleLocation | FullDetails | FullDetails **+düzenle** | FullDetails **+düzenle+davet** |
| Public, normal kategori | FullDetails | FullDetails | FullDetails | FullDetails **+düzenle** | FullDetails **+düzenle+davet** |
| Private, normal kategori | BusyOnly | BusyOnly | BusyOnly | BusyOnly | BusyOnly |
| Default, gizli kategori | BusyOnly | BusyOnly | BusyOnly | BusyOnly | BusyOnly |
| Public, gizli kategori | BusyOnly | BusyOnly | BusyOnly | BusyOnly | BusyOnly |
| Private, gizli kategori | BusyOnly | BusyOnly | BusyOnly | BusyOnly | BusyOnly |

Vekil erişimi (*özel öğeleri görebilir* **kapalı**) — asıl kullanıcının takvimine bakarken:

| Etkinlik durumu | Vekilin gördüğü |
|---|---|
| Default, normal kategori | FullDetails + vekile verilen düzenleme yetkisi |
| Public, normal kategori | FullDetails + vekile verilen düzenleme yetkisi |
| Private, herhangi | BusyOnly |
| Gizli kategori, herhangi | BusyOnly |

*Özel öğeleri görebilir* **açık** ise vekil her satırda `FullDetails` görür.

---

## Kenar durumlar ve verilen kararlar

| Soru | Karar |
|---|---|
| `BusyOnly` görünen etkinliğin başlığı ne yazar? | "Meşgul". Süre, çakışma ve renk görünür; başlık, konum, açıklama, katılımcı görünmez. |
| `Availability = Free` olan `Private` etkinlik ızgarada görünür mü? | Hayır. Serbest gösterilen ve detayı gizlenen etkinlik hiçbir bilgi taşımaz, gösterilmez. |
| Katılımcı olduğum ama takvimini görmediğim kişinin etkinliği | Katılımcılık paylaşımdan bağımsız bir erişim yoludur: katılımcı her zaman `FullDetails` görür. Paylaşım seviyesi bunu kısıtlamaz. |
| `IsForwardable = false` etkinliği başkasına iletmek | Davet iletme engellenir; ancak zaten `FullDetails` gören biri içeriği elle kopyalayabilir. Bu bir politika işareti, teknik kilit değil. |
| Tekrarlayan serinin tek örneği `Private` yapılabilir mi? | Evet. İstisna satırı kendi `Visibility` değerini taşır; seri kökününkini geçersiz kılar. |
| Silinmiş (çöp kutusundaki) etkinliği kim görür? | Yalnızca sahibi ve `CanEdit`+ yetkili vekil. Paylaşımlarda hiç görünmez. |
| Kaynak/oda takvimi | Rezervasyon, oda takvimine `Private` bir tutma kaydı olarak yazılır: paylaşıldığında yalnızca doluluk görünür, toplantının başlığı ve açıklaması görünmez. Ayarlanabilir yapılmadı — odanın kimin neyi için tuttuğunu herkese açmak, en dar seçeneği varsayılan yapmaktan daha risklidir. |
