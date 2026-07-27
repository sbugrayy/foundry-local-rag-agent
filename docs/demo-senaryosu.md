# 90 Saniyelik Demo Senaryosu

**Konu:** Ne yaptım, ne öğrendim
**Süre:** 1 dakika 30 saniye
**Kayıt:** Xbox Game Bar (`Win+Alt+R` başlat/bitir, mikrofon açık) — yüzünü de göstermek istersen OBS Studio

---

## Çekim öncesi kontrol listesi

Sırayla yap, hiçbirini atlama — videoyu kurtaran kısım burası:

- [ ] Uygulamayı **5 dakika önce** başlat (`dotnet run --project src/FoundryRag.Api`), **Durum** sekmesinde yeşil "hazır" ışığını gör. İlk açılış modelleri indirir; kamerada bunu yaşamak istemezsin.
- [ ] Belgeler **önceden yüklü** olsun ve durumları "hazır" görünsün.
- [ ] **Raporlar** sekmesinde önceden üretilmiş bir Word raporu hazır dursun (üretim 45–56 sn sürüyor, canlı beklemek 90 saniyeye sığmaz).
- [ ] Sohbeti **Temizle** düğmesiyle boşalt — temiz bir ekranla başla.
- [ ] Soracağın soruyu **bir kez önceden dene**, cevabın doğru geldiğini gör. Kamerada doğaçlama soru sorma.
- [ ] **Wi-Fi'yi kapat** ve uygulamanın çalıştığını bir soruyla teyit et. Videodaki en vurucu kanıt bu.
- [ ] Bildirimleri kapat (Rahatsız Etme / Odaklanma), gereksiz sekmeleri kapat, tarayıcıyı **%110–125** yakınlaştır ki yazılar okunsun.
- [ ] Word'ü önceden aç ve kapat (ilk açılış yavaş olabilir; raporu gösterirken beklemek istemezsin).

---

## Zaman çizelgesi

| Süre | Ekranda | Söylenecek |
|------|---------|------------|
| **0:00–0:11** | Panel açık, Sohbet sekmesi | "Merhaba, ben Buğra. Bu proje, belgelerinizle konuşmanızı sağlayan bir belge asistanı — ve tamamen bilgisayarımda çalışıyor. Şu an internetim kapalı: bulut yok, API anahtarı yok." |
| **0:11–0:25** | Soruyu yaz, gönder; cevap akmaya başlar | "Yüklediğim belgelere soru soruyorum. Cevap kelime kelime akıyor…" *(cevabın akmasını izlet, konuşmayı kes)* |
| **0:25–0:33** | Kaynak parçası düğmesine tıkla, açılan alıntıyı göster | "…ve altında cevabın hangi belge parçasından geldiği yazıyor. Yani uydurmuyor, kaynağını gösteriyor." |
| **0:33–0:45** | Raporlar sekmesi → hazır Word raporunu indir → Word'de aç | "Aynı belgelerden gerçek bir Word raporu üretebiliyor; Excel ve PDF de olabiliyor. Bunu üretmesi yaklaşık bir dakika sürüyor — tamamen yerel bir modelle." |
| **0:45–1:02** | Durum sekmesi (model adları, SQLite sayaçları görünür) | ".NET 10 ve Semantic Kernel kullandım; modeli Microsoft Foundry Local ile uygulamanın içinde çalıştırıyorum. Belgeler parçalanıp embedding'e çevriliyor, SQLite'ta vektör olarak saklanıyor; soru gelince en yakın parçalar modele bağlam olarak veriliyor." |
| **1:02–1:26** | Sohbet sekmesine dön, `@` menüsünü bir an göster | "İki şey öğrendim. Birincisi, küçük yerel modeller büyükler gibi davranmıyor: sistem istemine örnek olarak yazdığım dosya adını model gerçek kaynak sanıp cevaba kopyaladı — örneği kaldırınca uydurma bitti. İkincisi, RAG'de arama filtresiz olmuyor: küçük bir arşivde 'en iyi 10 parçayı getir' deyince arşivdeki her şey bağlama giriyor ve alakasız bir belge rapora karışıyordu. Benzerlik eşiği ve @ ile belge kapsamı ekleyerek çözdüm." |
| **1:26–1:30** | Panel genel görünüm | "Kod GitHub'da, tamamen çevrimdışı çalışıyor. Teşekkürler." |

Konuşma metni toplam ~190 kelime. Türkçede ~145 kelime/dakika hızla okuyunca doğal duraklamalarla 90 saniyeye oturur. Hızlanma eğilimin varsa 0:11–0:25 aralığında sus, görüntü kendini anlatsın.

---

## Kayıt ipuçları

**Ölçülmüş süreler** (senaryoyu bunlara göre kurdum): sohbet cevabı 13–20 saniye, rapor üretimi 45–56 saniye, uygulamanın önbellekten açılışı ~25 saniye.

**İddialı alternatif:** raporu canlı üretmek istersen "Rapor Oluştur"a bas ve o ~50 saniyede "ne yaptım" bölümünü anlat, sonra dosyayı açıp göster. Riski şu: üretim beklediğinden uzun sürerse video dağılır. Güvenli yol, hazır raporu göstermek.

**Ses ve tempo:** mikrofona yakın konuş, fare hareketlerini yavaş yap. 2–3 çekim yap, en iyisini gönder. Ufak bir tutukluk robotik tekrardan daha insani durur — cümlenin ortasında kaybolmadıkça devam et.

**Söylemekten kaçın:** "basit bir proje", "sadece bir demo" gibi küçültücü ifadeler. Yaptığın şey uçtan uca çalışan, kaynak gösteren, dosya üreten çevrimdışı bir RAG sistemi.
