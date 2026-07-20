# Uygulama Planı

Bu, canlı bir belge — fazlara zaman içinde madde eklenir/çıkarılır, tamamlanan maddeler
işaretlenir. Kaynak: proje sohbetinde belirlenen "hızlı kazanımlar / ürün derinliği /
güvenilirlik-altyapı" önceliklendirmesi (bkz. `CLAUDE.md` genel bağlam için).

Durum sembolleri: ⬜ planlandı · 🔄 devam ediyor · ✅ tamamlandı

---

## Faz 1 — Hızlı Kazanımlar

Düşük efor, gözle görülür fayda. Devam eden tasarım yenileme işiyle aynı yüzeyde (frontend,
`wwwroot/`) olduğu için onunla birlikte yürütülebilir.

| # | Madde | Ne / Neden | Kapsam | Efor |
|---|-------|-----------|--------|------|
| 1.1 | ⬜ Belgeler tablosunda arama/filtre/sıralama | Belge sayısı arttıkça düz liste kullanışsız kalıyor; dosya adına göre anlık filtre + Durum/Boyut/Tarih'e göre sıralama | `wwwroot/app.js` (`loadDocuments`), `index.html` (tablo başlığı üzerine arama kutusu) — backend değişmez, filtre client-side | S |
| 1.2 | ⬜ Belgeler'de toplu işlem | Şu an satır başına tek tek sil/özetle var; checkbox ile çoklu seçip toplu sil (ve mümkünse toplu özetle) | `index.html`/`app.js` (checkbox kolonu + toplu aksiyon çubuğu), backend'de mevcut `DELETE /api/documents/{id}` döngüyle çağrılabilir, gerekirse `POST /api/documents/bulk-delete` eklenir | M |
| 1.3 | ⬜ Açık/koyu tema geçişi | Tasarım yenilemesiyle doğal olarak örtüşüyor; `styles.css` zaten CSS custom property (`:root`) tabanlı, ikinci bir `[data-theme="light"]` token seti + sidebar'a toggle eklemek yeterli | `styles.css`, `index.html` (toggle butonu), `app.js` (localStorage'da tercih saklama) | S |

## Faz 2 — Ürün Derinliği

Çekirdek RAG deneyimini güçlendiren, backend'e de dokunan maddeler.

| # | Madde | Ne / Neden | Kapsam | Efor |
|---|-------|-----------|--------|------|
| 2.1 | ⬜ Kaynak alıntısını belgedeki konuma bağlama | Şu an "📎 3 kaynak parçası" düz metin — doğrulanabilirlik zayıf. Parça meta verisine sayfa/bölüm bilgisi eklenip tıklanınca gösterilmeli | `Chunker.cs`/`DocumentParsers.cs` (parça başına sayfa/konum meta verisi), `VectorStore.cs` (şema eklemesi), `RagService.cs` (kaynak nesnesine konum ekleme), frontend'de kaynak kartına gösterim | M |
| 2.2 | ⬜ Taranmış/görsel PDF için OCR | `PdfPig` yalnızca metin katmanlı PDF okuyor; taranmış belge sessizce boş/kalitesiz sonuç veriyor — kullanıcı bunu fark etmiyor. Önce en azından "bu PDF'te metin katmanı yok" uyarısı, sonra (istenirse) yerel OCR entegrasyonu | `DocumentParsers.cs` (metin katmanı boş kontrolü + uyarı durumu), OCR için yeni bağımlılık (ör. Tesseract .NET sarmalayıcı) — ayrı bir alt-karar gerektirir | M (uyarı) / L (tam OCR) |
| 2.3 | ⬜ Rapor şablonları | Üretilen Word/Excel raporları şu an düz stil; kurumsal kullanımda logo/başlık/stil şablonu isteniyor olabilir | `ReportService.cs` (OpenXML/ClosedXML şablon enjeksiyonu), yeni bir `Templates/` klasörü, Raporlar formuna şablon seçici | M |

## Faz 3 — Güvenilirlik / Altyapı

Görünürde daha az "parlak" ama regresyon riskini ve operasyonel sürtünmeyi azaltan maddeler.

| # | Madde | Ne / Neden | Kapsam | Efor |
|---|-------|-----------|--------|------|
| 3.1 | ⬜ Otomatik test altyapısı | Depoda hiç test yok — `Chunker` (bindirmeli parçalama sınırları) ve `VectorStore` (kosinüs arama doğruluğu) gibi sessiz regresyona en açık yerlerden başlanmalı | Yeni `tests/FoundryRag.Tests` projesi (xUnit), `.slnx`'e `<Project>` girişi eklenir; ileride `Program.cs` uç noktaları için `WebApplicationFactory` entegrasyon testleri | M (kurulum) sonra artımlı |
| 3.2 | ⬜ Foundry Local çağrılarına retry/timeout | Şu an hata durumunda kullanıcı Durum sekmesine gidip elle "Yeniden Dene"ye basıyor; geçici hatalarda (model henüz ısınıyor vb.) otomatik, sınırlı retry deneyimi iyileştirir | `FoundryService.cs`, `RagService.cs` çağrı noktaları | S/M |
| 3.3 | ⬜ Durum sayfasından model/parametre yapılandırma | Chat model alias, chunk size, TopK gibi ayarlar şu an yalnızca `appsettings.json` düzenlenerek değişiyor; Durum sekmesinden görüntüleme + (en azından) yeniden başlatma gerektiren ayarlar için düzenleme arayüzü | `Program.cs` (yeni `/api/config` uç noktaları), `VectorStore` veya ayrı config dosyası ile kalıcılık, `index.html`/`app.js` (Durum sekmesine form) | M |

---

## Nasıl ilerliyoruz

- Fazların sırası (1 → 2 → 3) öncelik sırasını yansıtıyor, katı bir kapı değil — istersen bir
  fazdaki tek bir maddeyi öne çekip tek başına yapabiliriz.
- Yeni madde eklemek istediğinde ("şuna da ihtiyacım var" dediğinde) ilgili faza satır olarak
  eklenir; bu dosya güncellenir.
- Bir maddeye başlarken durumu 🔄, bitince ✅ olarak işaretlenir.
