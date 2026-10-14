'use strict';

const fs = require('node:fs');
const path = require('node:path');

const toolsDirectory = path.join(__dirname, '..', 'tools');

function bundledFfmpeg() {
  try {
    const resolved = require('ffmpeg-static');

    if (typeof resolved === 'string' && fs.existsSync(resolved)) {
      return resolved;
    }
  } catch {
    return null;
  }

  return null;
}

function report(name, candidates) {
  const found = candidates.filter((candidate) => candidate && fs.existsSync(candidate));

  if (found.length > 0) {
    console.log(`${name}: bulundu -> ${found[0]}`);
    return true;
  }

  console.log(`${name}: bulunamadi`);
  return false;
}

function main() {
  const executable = process.platform === 'win32' ? '.exe' : '';

  console.log('Sky arac denetimi');
  console.log('Hedef klasor:', toolsDirectory);

  if (!fs.existsSync(toolsDirectory)) {
    fs.mkdirSync(toolsDirectory, { recursive: true });
    console.log('tools klasoru olusturuldu');
  }

  const ytdlp = report('yt-dlp (YouTube icin gerekli)', [
    process.env.SKY_YTDLP_PATH ?? '',
    path.join(toolsDirectory, `yt-dlp${executable}`)
  ]);

  const ffmpeg = report('ffmpeg (MP4 birlestirme icin gerekli)', [
    process.env.SKY_FFMPEG_PATH ?? '',
    bundledFfmpeg(),
    path.join(toolsDirectory, `ffmpeg${executable}`)
  ]);

  if (!ytdlp) {
    console.log('');
    console.log('YouTube icin yt-dlp kurun: pip install yt-dlp');
    console.log('Ardindan dosyayi tools/ klasorune kopyalayin veya SKY_YTDLP_PATH ayarlayin.');
  }

  if (!ffmpeg) {
    console.log('');
    console.log('ffmpeg bulunamadi. HLS akislari .ts olarak kaydedilir.');
    console.log('ffmpeg-static paketini kurun (npm install ffmpeg-static) veya');
    console.log('sistemdeki ffmpeg dosyasini tools/ klasorune kopyalayin.');
  } else {
    console.log('');
    console.log('MP4 (ses + video) birlestirme etkin.');
  }
}

main();