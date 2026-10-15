# Sky

Sky, yayın platformlarındaki yayın tekrarlarını (VOD) parçalı video akışları üzerinden
asenkron ve yüksek hızda indiren, ardından tek bir MP4 dosyasında birleştiren modern bir
masaüstü uygulamasıdır.

## Desteklenen platformlar

| Platform | Kapsam | Çözümleme yöntemi |
| -------- | ------ | ----------------- |
| YouTube  | VOD + canlı yayın kaydı | yt-dlp ile HLS manifest çözümleme |
| Twitch   | VOD arşivi | GQL oynatma belirteci + HLS |
| Kick     | VOD + kanal canlı yayını | GraphQL oynatma adresi + HLS |
| HLS      | `.m3u8` / master playlist | doğrudan |

Tüm platformlarda indirme Sky'nın kendi paralel segment motoruyla yapılır.

## Özellikler

**İndirme**

- Çok iş parçacıklı segment indirme, otomatik yeniden deneme ve hız sınırı
- Doğrudan dosyalarda paralel parça indirme (`Range` başlıklı 4 MB bloklar)
- `EXT-X-KEY` (AES-128) şifreli akış çözme, `EXT-X-BYTERANGE` ve `EXT-X-MAP` desteği
- Kayıpsız MP4 çıktısı: `ffmpeg -c copy -movflags +faststart`, ayrı ses akışı varsa
  tek dosyada birleştirilir
- Çok dilli ses izleri (orijinal + dublajlar), Türkçe dahil 60+ dil adı
- Canlı yayınlarda oynatma listesi takibi ile kesintisiz kayıt
- İş kuyruğu: aynı anda N iş, sırayla başlatma
- Duraklat / devam ettir / durdur, devam eden segmentler yeniden indirilmez
- Dosya adı şablonu (`{title}`, `{quality}`, `{platform}`, `{date}`, `{time}`, `{ext}`)

**Arayüz**

- Sekmeli düzen: İndirme, Kuyruk, Geçmiş, Ayarlar
- Ayarlar ekranı: vurgu rengi (8 hazır palet + özel renk), açık/koyu/sistem teması,
  animasyon seviyesi ve 20+ ayar
- Canlı hız grafiği, anlık yüzde/haz/ETA/boyut göstergeleri
- Thumbnail önizlemeleri (yerel proxy üzerinden), kuyruk ve geçmiş kartlarında
- Geçmiş arama, platform filtresi, sıralama ve liste/ızgara görünümü
- Pencereye bağlantı sürükle-bırak ile çözümleme
- Klavye kısayolları: `Ctrl+V` yapıştır, `Ctrl+Enter` başlat, `Ctrl+,` ayarlar, `Ctrl+L` odak
- Sistem bildirimleri, bitince klasörü açma, dosya yolunu kopyalama
- İskelet yüklenme durumları, toast geri alma, azaltılmış animasyon desteği

## Gereksinimler

- Node.js 18 veya üzeri
- .NET 8 SDK veya üzeri
- YouTube desteği için [yt-dlp](https://github.com/yt-dlp/yt-dlp) (`pip install yt-dlp`)
- MP4 çıktısı için ffmpeg (`ffmpeg-static` paketi isteğe bağlı bağımlılık olarak gelir ve
  paketlenmiş uygulamaya gömülür)

Araçların durumunu kontrol etmek için:

```bash
npm run tools
```

## Kurulum ve çalıştırma

```bash
npm install
npm start
```

Back-end bağımsız çalıştırılabilir:

```bash
dotnet run --project backend/Sky.fsproj
```

## Paketleme

```bash
npm run build
npm run dist
```

## Ayarlar

Ayarlar `%APPDATA%\Sky\settings.json` (Windows), `~/Library/Application Support/Sky`
(macOS), `~/.config/Sky` (Linux) altında saklanır ve uygulama içinden düzenlenir.

| Ayar | Açıklama |
| ---- | -------- |
| Vurgu rengi | Arayüz vurgu rengi, anında uygulanır |
| Tema | Koyu / açık / sistem |
| Animasyon seviyesi | Tam veya azaltılmış |
| Eşzamanlı segment | Aynı anda indirilen segment sayısı |
| Aynı anda çalışan iş | Kuyruk limiti |
| Hız sınırı | KB/sn, 0 sınırsız |
| Yeniden deneme | Segment hatalarında tekrar sayısı |
| Dosya adı şablonu | `{title}`, `{platform}`, `{quality}`, `{date}`, `{time}`, `{ext}` |
| Tercih edilen ses dili | Varsa otomatik seçilir |
| Varsayılan klasör | Ayarlanmazsa Videolar/Sky |
| MP4'e çevir | HLS çıktısını kayıpsız birleştir |
| Klasörü aç / bildirim | Tamamlanınca otomatik eylemler |
| yt-dlp / ffmpeg yolu | Boşsa otomatik aranır |
| HTTP vekil | İstekler için proxy |
| Zaman aşımı, canlı yoklama, geçmiş sınırı | Gelişmiş ayarlar |

Ortam değişkenleriyle de ayarlanabilir: `SKY_PORT`, `SKY_HOST`, `SKY_OUTPUT_DIRECTORY`,
`SKY_SEGMENT_CONCURRENCY`, `SKY_SEGMENT_RETRIES`, `SKY_SEGMENT_RETRY_DELAY`,
`SKY_SEGMENT_CEILING`, `SKY_LIVE_POLL_DIVISOR`, `SKY_CONNECTION_LIMIT`,
`SKY_REQUEST_TIMEOUT`, `SKY_RESOLVE_SESSION_SECONDS`, `SKY_YTDLP_PATH`, `SKY_FFMPEG_PATH`,
`SKY_PREFERRED_LANGUAGE`, `SKY_COOKIES_FROM_BROWSER`, `SKY_USER_AGENT`.

## API

| Metod  | Yol                        | Açıklama |
| ------ | -------------------------- | -------- |
| GET    | `/api/health`              | Servis sağlık durumu |
| GET    | `/api/platforms`           | Desteklenen platformlar |
| GET    | `/api/tools`               | yt-dlp ve ffmpeg durumu |
| POST   | `/api/tools/refresh`       | Araçları yeniden tara |
| GET    | `/api/settings`            | Ayarları oku |
| POST   | `/api/settings`            | Ayarları kaydet |
| POST   | `/api/settings/reset`      | Ayarları sıfırla |
| POST   | `/api/resolve`             | Bağlantıyı çözümle |
| POST   | `/api/jobs`                | İndirme işi başlat |
| GET    | `/api/jobs`                | İşler |
| GET    | `/api/jobs/{id}`           | İş detayı |
| DELETE | `/api/jobs/{id}`           | İşi iptal et |
| POST   | `/api/jobs/{id}/pause`     | İşi duraklat |
| POST   | `/api/jobs/{id}/resume`    | İşi sürdür |
| GET    | `/api/history`             | Geçmiş |
| DELETE | `/api/history/{id}`        | Geçmiş kaydını sil |
| GET    | `/api/thumbnail?url=`      | Thumbnail proxy |
| WS     | `/ws`                      | Canlı olay akışı |

### Çözümleme gövdesi

```json
{ "url": "https://www.youtube.com/watch?v=..." }
```

### İndirme gövdesi

```json
{
  "url": "https://www.youtube.com/watch?v=...",
  "sessionId": "çözümlemeden gelen oturum",
  "qualityId": "312",
  "audioTrackId": "140-2",
  "outputDirectory": "",
  "fileName": "",
  "segmentConcurrency": 8
}
```

## Proje Yapısı

```text
sky/
├── backend/
│   ├── Sky.fsproj
│   ├── Types.fs
│   ├── Settings.fs
│   ├── M3U8.fs
│   ├── Tools.fs
│   ├── Http.fs
│   ├── Sessions.fs
│   ├── Twitch.fs
│   ├── Kick.fs
│   ├── YouTube.fs
│   ├── Resolvers.fs
│   ├── Downloader.fs
│   └── Program.fs
├── frontend/
│   ├── main.js
│   ├── preload.js
│   ├── index.html
│   ├── styles.css
│   └── renderer.js
├── scripts/
│   └── tools.js
├── package.json
└── README.md
```

## Yasal Not

Yalnızca erişim yetkiniz olan ve platform kullanım koşullarına uygun içerikleri indirin.
Sky, DRM korumalı içeriklerin atlatılmasını amaçlamaz.

## Lisans

MIT