# CLAUDE.md

Bu dosya, bu depoda çalışırken Claude Code için rehberdir.

## Proje Özeti

**Foundry Local RAG Agent** — %100 yerel çalışan, agentic RAG tabanlı belge zekâsı paneli.
Microsoft Foundry Local (in-process WinML SDK) + Semantic Kernel + .NET 10 ile yazılmıştır.
İnternet bağlantısı ya da bulut API anahtarı gerektirmez; tüm çıkarım (chat + embedding) cihaz
üzerinde çalışır. Kullanıcı arayüzü ve tüm ürün metinleri **Türkçe**dir.

Ana yetenekler: belge içe aktarma (docx/pdf/xlsx/csv/txt/md), kaynak göstermeli sohbet, özetleme,
Word/Excel/PDF rapor üretimi, `@bahsetme` ile belge kapsamlama, agentic JSON router (function
calling yerine düşük sıcaklıklı sınıflandırma).

Detaylı mimari ve API tablosu için `README.md`'ye bakın — burada tekrarlanmıyor.

## Çalıştırma

```bash
dotnet run --project src/FoundryRag.Api
```

- Uygulama `http://localhost:8743` üzerinde açılır (bkz. `appsettings.json` → `Urls`).
- Frontend derleme adımı **yok**: `wwwroot/` altındaki dosyalar ASP.NET Core `UseStaticFiles` ile
  doğrudan servis edilir (bundler, npm, build stepi yok). Bir dosyayı kaydettikten sonra sadece
  tarayıcıyı yenilemek yeterli.
- Windows-only hedef: `net10.0-windows10.0.18362.0`, `RuntimeIdentifier: win-x64` (Foundry Local
  WinML bağımlılığı nedeniyle).
- İlk çalıştırmada Foundry Local modelleri indirir (`phi-4-mini` ≈3.5GB, `qwen3-embedding-0.6b`
  ≈0.6GB); ilerleme **Durum** sekmesinden izlenir.

## Proje Yapısı

```
src/FoundryRag.Api/
├── Program.cs                  # Minimal API uç noktaları + DI (tüm route'lar burada, controller yok)
├── appsettings.json             # Foundry model alias'ları, RAG parametreleri (chunk/topK/eşikler)
├── Models/ApiModels.cs          # Request/response DTO'ları
├── Services/
│   ├── FoundryService.cs        # Foundry Local yaşam döngüsü — SDK'ya tek temas noktası
│   ├── RagService.cs            # Semantic Kernel: RAG cevaplama + özetleme
│   ├── AgentOrchestrator.cs     # JSON router → araç seçimi + rapor akışı + SSE stream
│   ├── IngestionService.cs      # Arka plan içe aktarma kuyruğu (BackgroundService)
│   ├── DocumentParsers.cs       # docx/pdf/xlsx/csv/txt/md ayrıştırıcıları
│   ├── Chunker.cs               # Bindirmeli metin parçalama
│   ├── EmbeddingService.cs      # Vektörleme + normalizasyon
│   ├── VectorStore.cs           # SQLite: belgeler/parçalar/raporlar + kaba kuvvet kosinüs arama
│   └── ReportService.cs         # Markdown/JSON → Word (OpenXML) / Excel (ClosedXML) / PDF (QuestPDF)
└── wwwroot/                     # Yönetim paneli — vanilla HTML/CSS/JS, framework yok
    ├── index.html                # 4 görünüm: Sohbet, Belgeler, Raporlar, Durum (SPA-style, tek sayfa)
    ├── styles.css                # Koyu tema, CSS custom properties (:root değişkenleri)
    └── app.js                    # Tüm frontend mantığı — fetch + DOM manipülasyonu, framework yok
```

Tüm servisler `Program.cs`'te singleton olarak DI'a kayıtlıdır; `FoundryService` ve
`IngestionService` aynı zamanda `IHostedService` olarak arka planda çalışır.

## Frontend Notları (önemli — kırılgan bağımlılıklar)

`wwwroot/app.js` DOM'a **id/class isimleriyle sıkı bağlıdır** (jQuery benzeri `$`/`$$` seçicileri,
framework/state yönetimi yok). `index.html` veya `styles.css` üzerinde yapısal bir değişiklik
(yeniden tasarım dahil) yapılırken şu kancaların ya birebir korunması ya da `app.js`'te karşılık
gelen satırların güncellenmesi gerekir:

- **Navigasyon:** `.nav-item[data-view]` + `#view-{chat|documents|reports|status}` — görünüm
  değiştirme mantığı `data-view` değerine göre çalışır (`app.js:108-113`).
- **Durum pili:** `#pillDot` (`.ready/.error/.loading` class'ları), `#pillText`, `#docBadge`,
  `#reportBadge`.
- **Sohbet:** `#chatMessages`, `#chatWelcome`, `#chatForm`, `#chatText`, `#chatSend`,
  `#chatClearBtn` (geçmişi temizle), `#mentionMenu` + `.mention-item[data-name]` (@bahsetme
  menüsü), `.chip[data-example]` (örnek sorular), mesaj balonları `.msg-bubble` üretimi JS
  içinde inline HTML olarak yapılıyor.
  Not: `#chatWelcome`'ın `outerHTML`'i sayfa yüklenirken saklanır ve geçmiş temizlenince geri
  basılır (`app.js` → `chatWelcomeHtml`); bu yüzden `.chip` dinleyicileri `bindChipButtons()`
  ile yeniden bağlanır — karşılama bloğunun yapısını değiştirirsen bu iki noktayı da güncelle.
- **Belgeler:** `#dropzone`, `#fileInput`, `#browseBtn`, `#docsBody`, `#docsEmpty`.
- **Raporlar:** `#formatSeg .seg-item[data-format]`, `#reportScope`, `#reportInstruction`,
  `#reportCreate`, `#reportProgress`, `#reportsBody`, `#reportsEmpty`.
- **Durum sayfası:** `#stateValue`, `#stateMsg`, `#retryBtn`, `#chatModelValue`/`#chatModelBar`,
  `#embedModelValue`/`#embedModelBar`, `#storeValue`/`#storeMsg`, `#techRows`.
- **CSS custom properties** (`styles.css:2-18`, `:root`) tüm renk/spacing kararlarının tek
  kaynağı: `--bg`, `--panel`, `--accent`, `--accent-2`, `--danger`, `--warn`, `--ok`, `--radius`.
  Yeniden tasarımda bu token'ların isimleri korunursa (sadece değerleri değişse) `app.js`'teki
  inline `style.color = "var(--ok)"` gibi referanslar (örn. `app.js:146`) kırılmaz.

**Tasarım Stitch tasarım sistemiyle yenilendi** (commit `052ff06`): jenerik "AI ürünü" dili
(mor→turkuaz gradient, emoji ikonlar, cam efektli kartlar) yerine teknik hassasiyet dili
benimsendi; bilgi mimarisi (4 görünüm) ve DOM kancaları korundu. Açık/koyu tema geçişi var —
yeni bileşen eklerken rengi doğrudan yazmayın, `:root` token'larını kullanın ki iki temada da
doğru görünsün.

## Kod Kuralları

- **Dil:** Kod içi yorumlar ve UI metinleri Türkçe; kod (değişken/metot adları) İngilizce.
- **Backend:** Minimal API stili — yeni uç nokta eklerken controller değil, `Program.cs`'e
  `api.MapGet/MapPost/...` satırı ekleyin; DTO'lar `Models/ApiModels.cs`'e.
  Hata mesajları kullanıcıya döndürülen JSON'larda Türkçedir (`{ error = "..." }`).
- **Servisler tek sorumluluk:** Foundry Local'a dokunan tek yer `FoundryService`; vektör
  DB'ye dokunan tek yer `VectorStore`. Yeni özellik eklerken bu sınırları koruyun.
- **Frontend:** Build stepi olmadığı için framework/npm paketi eklemeyin — vanilla JS/CSS'te
  kalın. Yeni görünüm eklerken mevcut `.view`/`.nav-item[data-view]` desenini takip edin.
- **Test altyapısı yok** — depo içinde otomatik test bulunmuyor; değişiklik sonrası
  `dotnet build` ile derleme hatası kontrolü yapın, UI değişikliklerini tarayıcıda elle doğrulayın.

## Yol Haritası (README'den, güncel öncelik sırası)

- [x] Sohbet geçmişinin SQLite'ta kalıcılaştırılması (`chat_messages` tablosu)
- [ ] Var olan Word/Excel dosyasında yerinde düzenleme
- [ ] Native function calling'e geçiş (Foundry Local model desteği yaygınlaşınca)
- [ ] `sqlite-vec` eklentisiyle ANN indeksleme
