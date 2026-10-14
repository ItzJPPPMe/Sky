# Sky

Sky, yayın platformlarındaki yayın tekrarlarını (VOD) parçalı video akışları üzerinden
asenkron ve yüksek hızda indiren, ardından tek bir dosyada birleştiren modern bir masaüstü
uygulamasıdır.

## Desteklenen platformlar

| Platform | Kapsam | Çözümleme yöntemi |
| -------- | ------ | ----------------- |
| YouTube  | VOD + canlı yayın kaydı | yt-dlp ile HLS manifest çözümleme |
| Twitch   | VOD arşivi | GQL oynatma belirteci + HLS |
| Kick     | VOD + kanal canlı yayını | GraphQL oynatma adresi + HLS |
| HLS      | `.m3u8` / master playlist | doğrudan |

Tüm platformlarda indirme Sky'nın kendi paralel segment motoruyla yapılır; aynı segment
indirme, hız ve ilerleme yolu kullanılır.

## Özellikler

- Çok iş parçacıklı segment indirme ve otomatik yeniden deneme
- Doğrudan dosyalarda (ses/progressive MP4) paralel parça indirme: `Range` başlıklı 4 MB bloklar
- `EXT-X-KEY` (AES-128) şifreli akış çözme, `EXT-X-BYTERANGE` ve `EXT-X-MAP` desteği
- **MP4 çıktısı**: HLS birleşimi sonrası kayıpsız remux (`-c copy`), ayrı ses akışı varsa
  ffmpeg ile tek dosyada birleştirilir (yeniden kodlama yapılmaz)
- Canlı yayınlarda oynatma listesi takibi ile kesintisiz kayıt
- Duraklat, devam ettir, durdur ve otomatik yeniden adlandırma
- Canlı indirme hızı, indirilen segment sayısı ve yüzde bazlı ilerleme göstergesi
- Kalıcı indirme geçmişi ve dosyayı klasörde gösterme
- F# tabanlı yerel API + WebSocket ve Electron tabanlı modern karanlık tema arayüz
- Platformlar arası çalışma desteği (Windows, macOS, Linux)

## Teknolojiler

| Katman    | Teknoloji                       |
| --------- | ------------------------------- |
| Back-end  | F# / .NET 8 (ASP.NET Core)      |
| Front-end | Electron, HTML, CSS, JavaScript |

## Gereksinimler

- Node.js 18 veya üzeri
- .NET 8 SDK veya üzeri
- YouTube desteği için [yt-dlp](https://github.com/yt-dlp/yt-dlp) (`pip install yt-dlp`)
- MP4 çıktısı için ffmpeg (proje `ffmpeg-static` paketini isteğe bağlı bağımlılık olarak
  getirir ve paketlenmiş uygulamaya gömer)

Sky `yt-dlp` aracını şu sırayla arar: `SKY_YTDLP_PATH` ortam değişkeni, uygulama klasöründeki
`tools/yt-dlp`, sistem `PATH` ve son olarak `python -m yt_dlp`. ffmpeg için sıra:
`SKY_FFMPEG_PATH`, paketlenmiş uygulamada `resources/ffmpeg`, `ffmpeg-static`, `tools/ffmpeg`
ve sistem `PATH`. Twitch ve Kick desteği için yt-dlp gerekmez.

Araçların durumunu kontrol etmek için:

```bash
npm run tools
```

## Kurulum

```bash
npm install
```

## Çalıştırma

```bash
npm start
```

Uygulama ilk açılışta F# back-end bileşenini derleyip başlatır. Back-end bağımsız olarak
çalıştırılabilir:

```bash
dotnet run --project backend/Sky.fsproj
```

## Paketleme

```bash
npm run build
npm run dist
```

`npm run build:backend` F# projesini `dist/backend` altına yayınlar, `npm run build:frontend`
ise Electron paketini oluşturur.

## Ses Dili Seçimi

YouTube videolarında birden fazla ses izi bulunabilir (orijinal dil + otomatik ya da elle
yüklenmiş dublajlar). Sky bu izleri `yt-dlp` çıktısındaki dil bilgisiyle listeler ve
arayüzde **Ses dili** seçicisi sunar:

- Her dil için en iyi ses (AAC/m4a tercih edilir, `-drc` formatları atlanır)
- Orijinal ses ve önerilen ses işaretlenir
- `SKY_PREFERRED_LANGUAGE` (varsayılan `tr`) dili varsa otomatik seçilir; yoksa orijinal
  ses kullanılır
- Seçilen dil YouTube dışındaki platformlar için değişmez (Twitch/Kick akışları sesi
  içinde barındırır)

Örnek: `SKY_PREFERRED_LANGUAGE=en` ile İngilizce izleri olan bir videoda İngilizce ses
otomatik seçilir.

## MP4 Çıktısı

HLS akışları MPEG-TS parçalarla gelir; bu parçalar doğrudan birleştirilirse dosya `.ts`
kalır. Sky bu aşamadan sonra otomatik olarak:

1. Gerekirse ayrı ses akışını paralel parçalar halinde indirir (YouTube HLS'i sessizdir,
   ses ayrı formatta gelir; `-drc` formatları atlanır, AAC/m4a tercih edilir),
2. `ffmpeg -c copy -movflags +faststart` ile video + sesi kayıpsız tek bir `.mp4` içinde
   birleştirir (yeniden kodlama yok, kalite kaybı yok),
3. Ara-konum `.ts` dosyasını siler.

ffmpeg bulunamazsa indirme yine tamamlanır, dosya `.ts` olarak kalır ve arayüzde turuncu
uyarı bandı gösterilir. Fragmented MP4 (`.m4s`) akışlarında ek işlem gerekmez.

## Proje Yapısı

```text
sky/
├── backend/
│   ├── Sky.fsproj
│   ├── Types.fs
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
├── package.json
└── README.md
```

## API

Back-end varsayılan olarak `http://127.0.0.1:47210` adresinde çalışır ve WebSocket destekler.

| Metod  | Yol                     | Açıklama                       |
| ------ | ----------------------- | ------------------------------ |
| GET    | `/api/health`           | Servis sağlık durumu           |
| GET    | `/api/platforms`        | Desteklenen platformlar        |
| GET    | `/api/tools`            | yt-dlp ve ffmpeg durumu        |
| POST   | `/api/resolve`          | Bağlantıyı çözümle, kaliteleri getir |
| POST   | `/api/jobs`             | İndirme işi başlat             |
| GET    | `/api/jobs`             | Aktif ve geçmiş işler          |
| GET    | `/api/jobs/{id}`        | Tek iş detayı                  |
| DELETE | `/api/jobs/{id}`        | İş iptal et                    |
| POST   | `/api/jobs/{id}/pause`  | İşi duraklat                   |
| POST   | `/api/jobs/{id}/resume` | Duraklatılmış işi devam ettir |
| GET    | `/api/history`          | Tamamlanan indirmeler         |
| DELETE | `/api/history/{id}`     | Geçmiş kaydını sil             |
| WS     | `/ws`                   | Canlı olay akışı              |

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

## Yapılandırma

Tüm ayarlar ortam değişkenleri ile verilir.

| Değişken                   | Varsayılan   | Açıklama                          |
| -------------------------- | ------------ | --------------------------------- |
| `SKY_PORT`                 | `47210`      | API ve WebSocket bağlantı noktası |
| `SKY_HOST`                 | `127.0.0.1`  | Dinlenen adres                    |
| `SKY_OUTPUT_DIRECTORY`     | Videolar/Sky | İndirilen dosyaların klasörü      |
| `SKY_SEGMENT_CONCURRENCY`  | `8`          | Eşzamanlı segment sayısı          |
| `SKY_SEGMENT_RETRIES`      | `3`          | Segment yeniden deneme sayısı     |
| `SKY_SEGMENT_RETRY_DELAY`  | `750`        | Yeniden deneme aralığı (ms)       |
| `SKY_SEGMENT_CEILING`      | `16`         | Canlı akışlarda segment tavanı    |
| `SKY_LIVE_POLL_DIVISOR`    | `2`          | Canlı liste yoklama böleni        |
| `SKY_CONNECTION_LIMIT`     | `24`         | Sunucu başına bağlantı sınırı     |
| `SKY_REQUEST_TIMEOUT`      | `30`         | İstek zaman aşımı (saniye)        |
| `SKY_RESOLVE_SESSION_SECONDS` | `1800`    | Çözümleme oturumu ömrü (saniye)  |
| `SKY_YTDLP_PATH`           | boş          | yt-dlp yolunu sabitle             |
| `SKY_FFMPEG_PATH`          | boş          | ffmpeg yolunu sabitle            |
| `SKY_COOKIES_FROM_BROWSER` | boş          | yt-dlp için tarayıcı çerez kaynağı |
| `SKY_PREFERRED_LANGUAGE`   | `tr`         | Tercih edilen ses dili kodu      |
| `SKY_USER_AGENT`           | Chrome UA    | Gönderilen kullanıcı aracısı      |

## Yasal Not

Yalnızca erişim yetkiniz olan ve platform kullanım koşullarına uygun içerikleri indirin.
Sky, DRM korumalı içeriklerin atlatılmasını amaçlamaz.

## Lisans

MIT