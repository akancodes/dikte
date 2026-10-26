<p align="center"><img src="docs/icon.png" width="96" alt=""></p>

<h1 align="center">Dikte</h1>

<p align="center">Tuşa bas, konuş, bırak: söylediklerin o an yazdığın yere düşer.<br>
Kendi OpenAI API anahtarınla çalışan, Windows 11 ve macOS için küçük ve açık kaynak bir sesle yazma aracı.</p>

---

Dikte, araçlarıyla konuşanlar için: terminaldeki Claude Code, bir sohbet kutusu, bir e-posta, bir commit mesajı.
Bildirim alanında (Mac'te menü çubuğunda) durur, yalnızca kısayolu basılı tuttuğun sürece dinler, kaydı bir konuşmadan yazıya modeline gönderir
ve sonucu öndeki pencereye yapıştırır. `gpt-transcribe` ile bir saatlik konuşma yaklaşık **0,27 $**.

## Kullanım

**Windows 11**

1. [Son sürümden](../../releases/latest/download/Dikte.exe) `Dikte.exe`'yi indirip çalıştır. İmzalı olmadığı için
   SmartScreen sorabilir: **Ek bilgi → Yine de çalıştır**.
2. İstediğinde API anahtarını yapıştır. Denenir, sonra Windows kullanıcına özel şifrelenerek saklanır.
3. Bir metin alanına tıkla, **`Ctrl + Win`'i basılı tut**, konuş, bırak.

**macOS 13 ve sonrası** (Apple silicon ve Intel)

1. [Son sürümden](../../releases/latest/download/Dikte-mac.zip) `Dikte-mac.zip`'i indir, aç, **Dikte**'yi
   **Uygulamalar** klasörüne sürükle.
2. Apple onaylı (notarized) olmadığı için ilk açılış bir kez reddedilir: **Sistem Ayarları → Gizlilik ve Güvenlik**'te
   aşağı kaydır, Dikte'nin yanındaki **Yine de Aç**'a tıkla.
3. Sorduğunda **Erişilebilirlik**'te Dikte'yi aç (kısayolu duyar, onunla yapıştırır) ve **mikrofona** izin ver.
   Sonra API anahtarını yapıştır; giriş anahtar zincirinde saklanır.
4. Bir metin alanına tıkla, **`Fn`'yi (🌐) basılı tut**, konuş, bırak. 🌐 tuşu emoji açıyor ya da klavye dilini
   değiştiriyorsa **Sistem Ayarları → Klavye → 🌐 tuşuna basınca → Hiçbir Şey Yapma** yap; Dikte'nin penceresi de söyler.

Güncellemeden sonra macOS imzasız uygulamanın Erişilebilirlik iznini unutur: Dikte'yi listeden **−** ile kaldırıp
yeniden aç.

| Ne yaparsan | Ne olur |
|---|---|
| `Ctrl + Win` (Mac: `Fn`) basılı | Basılı tuttukça dinler, bırakınca yapıştırır |
| Basılıyken `Space` | Eller serbest: konuşmaya devam et, kısayola tekrar basınca gönderir |
| `Esc` | Kaydı ya da yoldaki yazıyı iptal eder |
| Tepsi simgesine sol tık (Mac: menü çubuğu simgesinin menüsü) | Eller serbest dikteyi fareyle başlatır/bitirir |

**Ses araçları** (Windows; her biri için önce Ayarlar'da bir kısayol seç):

| Ne yaparsan | Ne olur |
|---|---|
| Çeviri kısayolunu basılı tutup herhangi bir dilde konuş | İngilizce yazılır |
| Metni seç, düzenleme kısayolunu basılı tut, "kısalt" / "daha resmi yap" de | Seçili metin düzenlenmiş haliyle değişir; seçim yoksa istediğini yazar |
| Tekrar yapıştır kısayoluna dokun | Son dikteyi bulunduğun yere tekrar yapıştırır |
| "Yeni satır" / "yeni paragraf" de | Kelimeler yerine satır atlar |
| Sonunda ayrı bir cümle olarak "gönder" de | Metinden sonra Enter'a basar: Claude Code'da prompt'u gönderir |
| Sadece "sil" de | Son dikteyi geri siler |

**Modlar** (Windows, Mac ve iPhone): sözlerinin nasıl yazılacağı. Her mod kendi cümlelerinle yazdığın bir talimattır;
bir sohbet modeli onu konuşmana uygular, konuşmanı asla cevaplamaz. Beş tanesi hazır gelir, düzenlenir, silinir, yenisi eklenir:
Olduğu gibi (modelsiz, varsayılan), Clean up (temizle), Prompt (Claude Code için düzgün bir prompt), List (madde listesi),
Commit message, Email.

Hangisinin kullanılacağı: modun kendi kısayolu önce gelir, sonra öndeki uygulama (Prompt; Windows Terminal, VS Code,
Cursor, Claude uygulaması, Terminal, iTerm2 gibi yerlerde kendiliğinden devrede), sonra aktif mod. Aktif modu pencerenin
ana sayfasından, tepsi/menü çubuğu menüsünden, sonraki mod kısayoluyla ya da iPhone'da klavyenin üst şeridinden seçersin.

**Toplantı notları** (Windows 11): Dikte bir aramayı dinleyip yazıya döker. Discord hazır gelir; başka arama
uygulamaları Ayarlar'dan süreç adıyla eklenir (`Zoom`, `ms-teams`, `slack`).

| Ne yaparsan | Ne olur |
|---|---|
| Discord'da bir aramaya katıl | Bir bildirim not almayı önerir: tıkla. Tıklamadan hiçbir şey kaydedilmez |
| Ya da Meetings sekmesinden, tepsi menüsünden veya toplantı kısayolundan başlat | Açık arama uygulaması yoksa yalnızca mikrofon dinler: odadaki bir toplantı |
| Aramayı kapat ya da **Stop and write notes** | Birkaç saniye sonra notlar açılır: kısa özet, kararlar, kimin ne yapacağı, önemli noktalar, açık sorular; altında tüm döküm |

Windows, arama uygulamasının sesini diğer seslerden ayrı kaydeder (başka yerde çalan müzik ya da video karışmaz),
mikrofonu da ayrı: mikrofon sensin, uygulama karşı taraf; döküm kimin ne dediğini böyle bilir. Arama sürerken kimlerin
olduğunu yazarsan karşı taraf adıyla geçer. Sessizlik hiç gönderilmez; notları, toplantı hangi dildeyse o dilde bir
sohbet modeli (`gpt-6-luna`, bir saatlik toplantı için bir sentin altında) yazar. Her toplantı `Belgeler\Dikte Meetings`
klasöründe (ya da seçtiğin, eşitlenen bir klasörde) bir Markdown dosyasıdır. Karşı tarafı da kaydettiğini, başlamadan söyle.

Türkçe ve İngilizceyi cümle içinde karıştırabilirsin; `language` boş kalsın. Ayarlar, gizlilik ve derleme için
[İngilizce README](README.md).

## Lisans

MIT © 2026 Bayram Akan
