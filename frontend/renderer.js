'use strict';

const elements = {
  form: document.getElementById('downloadForm'),
  urlInput: document.getElementById('urlInput'),
  urlError: document.getElementById('urlError'),
  resolveButton: document.getElementById('resolveButton'),
  resolutionCard: document.getElementById('resolutionCard'),
  resolutionTitle: document.getElementById('resolutionTitle'),
  resolutionMeta: document.getElementById('resolutionMeta'),
  resolutionBadge: document.getElementById('resolutionBadge'),
  resolutionError: document.getElementById('resolutionError'),
  platformChips: document.getElementById('platformChips'),
  qualitySelect: document.getElementById('qualitySelect'),
  languageSelect: document.getElementById('languageSelect'),
  directoryInput: document.getElementById('directoryInput'),
  directoryButton: document.getElementById('directoryButton'),
  fileNameInput: document.getElementById('fileNameInput'),
  concurrencyInput: document.getElementById('concurrencyInput'),
  concurrencyValue: document.getElementById('concurrencyValue'),
  pasteButton: document.getElementById('pasteButton'),
  startButton: document.getElementById('startButton'),
  pauseButton: document.getElementById('pauseButton'),
  stopButton: document.getElementById('stopButton'),
  serviceBadge: document.getElementById('serviceBadge'),
  serviceBadgeText: document.getElementById('serviceBadgeText'),
  historyRefreshButton: document.getElementById('historyRefreshButton'),
  historyClearButton: document.getElementById('historyClearButton'),
  historyList: document.getElementById('historyList'),
  historyEmpty: document.getElementById('historyEmpty'),
  emptyState: document.getElementById('emptyState'),
  progressPanel: document.getElementById('progressPanel'),
  progressTitle: document.getElementById('progressTitle'),
  progressMeta: document.getElementById('progressMeta'),
  progressState: document.getElementById('progressState'),
  progressBar: document.getElementById('progressBar'),
  progressError: document.getElementById('progressError'),
  progressWarning: document.getElementById('progressWarning'),
  progressTrack: document.querySelector('.progress-track'),
  metricPercent: document.getElementById('metricPercent'),
  metricSpeed: document.getElementById('metricSpeed'),
  metricEta: document.getElementById('metricEta'),
  metricSize: document.getElementById('metricSize'),
  footerEndpoint: document.getElementById('footerEndpoint'),
  footerTools: document.getElementById('footerTools'),
  footerOutput: document.getElementById('footerOutput'),
  toastContainer: document.getElementById('toastContainer')
};

const stateLabels = {
  idle: 'Bekliyor',
  queued: 'Kuyrukta',
  resolving: 'Çözümleniyor',
  downloading: 'İndiriliyor',
  paused: 'Duraklatıldı',
  merging: 'Birleştiriliyor',
  muxing: 'MP4 oluşturuluyor',
  completed: 'Tamamlandı',
  failed: 'Başarısız',
  cancelled: 'İptal edildi'
};

const activeStates = new Set(['queued', 'resolving', 'downloading', 'merging', 'muxing']);

const state = {
  config: { socketUrl: 'ws://127.0.0.1:47210/ws', outputDirectory: '' },
  resolution: null,
  activeJobId: null,
  job: null,
  history: [],
  socket: null,
  reconnectDelay: 800,
  resolving: false
};

function formatBytes(bytes) {
  const value = Number(bytes) || 0;

  if (value <= 0) {
    return '0 B';
  }

  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1);
  const size = value / 1024 ** index;

  return index === 0 ? `${Math.round(size)} B` : `${size.toFixed(1)} ${units[index]}`;
}

function formatDuration(seconds) {
  const total = Number(seconds) || 0;

  if (total <= 0) {
    return '--:--';
  }

  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const rest = Math.floor(total % 60);
  const pad = (value) => String(value).padStart(2, '0');

  return hours > 0 ? `${pad(hours)}:${pad(minutes)}:${pad(rest)}` : `${pad(minutes)}:${pad(rest)}`;
}

function formatDate(value) {
  if (!value) {
    return '-';
  }

  const date = new Date(value);

  if (Number.isNaN(date.getTime())) {
    return '-';
  }

  return date.toLocaleString('tr-TR', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit'
  });
}

function sanitizeFileName(value) {
  const cleaned = String(value ?? '')
    .replace(/[\\/:*?"<>|]+/g, '-')
    .replace(/\s+/g, ' ')
    .trim();

  return cleaned.length === 0 ? '' : cleaned;
}

function validateUrl(raw) {
  const value = String(raw ?? '').trim();

  if (value.length === 0) {
    return { valid: false, message: 'Yayın bağlantısı boş olamaz.' };
  }

  let parsed = null;

  try {
    parsed = new URL(value);
  } catch {
    return { valid: false, message: 'Geçerli bir bağlantı girin (https://...).' };
  }

  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    return { valid: false, message: 'Bağlantı http veya https ile başlamalıdır.' };
  }

  return { valid: true, message: '' };
}

function setServiceStatus(kind, label) {
  elements.serviceBadge.className = `badge badge-${kind}`;
  elements.serviceBadgeText.textContent = label;
}

function toast(message, kind = 'info', timeout = 5000) {
  const node = document.createElement('div');
  node.className = `toast toast-${kind}`;
  node.textContent = message;
  elements.toastContainer.append(node);

  window.setTimeout(() => {
    node.classList.add('is-leaving');
    window.setTimeout(() => node.remove(), 240);
  }, timeout);
}

function renderPlatformChips(platforms) {
  elements.platformChips.replaceChildren();

  if (!Array.isArray(platforms)) {
    return;
  }

  for (const platform of platforms) {
    const chip = document.createElement('span');
    chip.className = 'chip';
    chip.textContent = platform.name;
    chip.dataset.id = platform.id;
    elements.platformChips.append(chip);
  }
}

function markActiveChip(platformId) {
  for (const chip of elements.platformChips.querySelectorAll('.chip')) {
    chip.classList.toggle('is-active', chip.dataset.id === String(platformId ?? '').toLowerCase());
  }
}

function renderResolution(resolution) {
  state.resolution = resolution;

  if (!resolution) {
    elements.resolutionCard.hidden = true;
    elements.qualitySelect.disabled = true;
    elements.qualitySelect.replaceChildren();
    return;
  }

  elements.resolutionCard.hidden = false;
  elements.resolutionTitle.textContent = resolution.title || 'Yayın tekrarı';

  const parts = [];

  if (resolution.uploader) {
    parts.push(resolution.uploader);
  }

  if (Number(resolution.durationSeconds) > 0) {
    parts.push(formatDuration(resolution.durationSeconds));
  }

  parts.push(`${resolution.qualities.length} kalite`);

  elements.resolutionMeta.textContent = parts.join(' · ');
  elements.resolutionBadge.textContent = resolution.isLive ? 'Canlı kayıt' : resolution.platform;
  elements.resolutionBadge.className = `state-pill ${resolution.isLive ? 'state-merging' : 'state-completed'}`;
  elements.resolutionError.hidden = true;

  elements.qualitySelect.replaceChildren();
  elements.qualitySelect.disabled = false;

  for (const quality of resolution.qualities) {
    const option = document.createElement('option');
    const details = [];

    if (Number(quality.width) > 0 && Number(quality.height) > 0) {
      details.push(`${quality.width}x${quality.height}`);
    }

    if (Number(quality.frameRate) > 0) {
      details.push(`${Math.round(Number(quality.frameRate))}fps`);
    }

    if (Number(quality.bitrateKbps) > 0) {
      details.push(`${Math.round(Number(quality.bitrateKbps))} kbps`);
    }

    if (quality.isAudio) {
      details.push('yalnızca ses');
    }

    option.value = quality.id;
    option.textContent = details.length > 0 ? `${quality.label} · ${details.join(' · ')}` : quality.label;
    elements.qualitySelect.append(option);
  }

  renderAudioTracks(resolution);

  const preferred = resolution.qualities[0];

  if (preferred) {
    elements.qualitySelect.value = preferred.id;
    elements.fileNameInput.value = sanitizeFileName(resolution.title);
  }

  markActiveChip(String(resolution.platform ?? '').toLowerCase());
}

function renderAudioTracks(resolution) {
  const tracks = Array.isArray(resolution.audioTracks) ? resolution.audioTracks : [];
  elements.languageSelect.replaceChildren();

  if (tracks.length === 0) {
    const option = document.createElement('option');
    option.value = '';
    option.textContent = 'OrIJinal ses (akışa gömülü)';
    elements.languageSelect.append(option);
    elements.languageSelect.disabled = true;
    return;
  }

  if (tracks.length === 1) {
    const only = tracks[0];
    const option = document.createElement('option');
    option.value = only.id;
    option.textContent = only.label;
    elements.languageSelect.append(option);
    elements.languageSelect.disabled = true;
    return;
  }

  elements.languageSelect.disabled = false;

  for (const track of tracks) {
    const option = document.createElement('option');
    const marks = [];

    if (track.isOriginal) {
      marks.push('orijinal');
    }

    if (track.isRecommended) {
      marks.push('önerilen');
    }

    option.value = track.id;
    option.textContent = marks.length > 0 ? track.label + ' (' + marks.join(', ') + ')' : track.label;
    elements.languageSelect.append(option);
  }

  const recommended = tracks.find((track) => track.isRecommended) ?? tracks[0];
  elements.languageSelect.value = recommended.id;
}

function showResolutionError(message) {
  state.resolution = null;
  elements.resolutionCard.hidden = false;
  elements.resolutionTitle.textContent = 'Çözümlenemedi';
  elements.resolutionMeta.textContent = 'Bağlantıyı kontrol edip tekrar deneyin';
  elements.resolutionBadge.textContent = 'Hata';
  elements.resolutionBadge.className = 'state-pill state-failed';
  elements.resolutionError.textContent = message;
  elements.resolutionError.hidden = false;
  elements.qualitySelect.disabled = true;
  elements.qualitySelect.replaceChildren();
  elements.languageSelect.disabled = true;
  elements.languageSelect.replaceChildren();
}

async function resolveLink() {
  const validation = validateUrl(elements.urlInput.value);

  elements.urlError.textContent = validation.message;

  if (!validation.valid) {
    elements.urlInput.focus();
    return null;
  }

  state.resolving = true;
  elements.resolveButton.disabled = true;
  elements.resolveButton.textContent = 'Çözümleniyor';

  try {
    const response = await window.sky.resolveLink({ url: elements.urlInput.value.trim() });

    if (!response.ok) {
      showResolutionError(response.error ?? 'Bağlantı çözümlenemedi.');
      toast(response.error ?? 'Bağlantı çözümlenemedi.', 'error', 7000);
      return null;
    }

    renderResolution(response.data);
    return response.data;
  } finally {
    state.resolving = false;
    elements.resolveButton.disabled = false;
    elements.resolveButton.textContent = 'Çözümle';
  }
}

function renderControls(job) {
  const isActive = Boolean(job) && activeStates.has(job.state);
  const isPaused = Boolean(job) && job.state === 'paused';

  elements.startButton.disabled = isActive || isPaused || state.resolving;
  elements.pauseButton.disabled = !isActive && !isPaused;
  elements.stopButton.disabled = !job || !isActive;

  elements.pauseButton.querySelector('span').textContent = isPaused ? 'Devam Et' : 'Duraklat';
}

function renderJob(job) {
  state.job = job;
  state.activeJobId = job ? job.id : null;

  if (!job) {
    elements.progressPanel.hidden = true;
    elements.emptyState.hidden = false;
    renderControls(null);
    return;
  }

  const percent = Math.max(0, Math.min(100, Number(job.progressPercent) || 0));

  elements.emptyState.hidden = true;
  elements.progressPanel.hidden = false;
  elements.progressTitle.textContent = job.title || job.url || 'Yayın tekrarı';
  elements.progressMeta.textContent = `${job.platform} · ${job.completedSegments ?? 0} / ${job.totalSegments ?? 0} segment · ${job.qualityLabel ?? '-'}`;
  elements.progressState.textContent = job.isLive ? `${stateLabels[job.state] ?? job.state} (canlı)` : stateLabels[job.state] ?? job.state;
  elements.progressState.className = `state-pill state-${job.state ?? 'idle'}`;
  elements.progressBar.style.width = `${percent}%`;
  elements.progressTrack.setAttribute('aria-valuenow', String(Math.round(percent)));
  elements.metricPercent.textContent = `${percent.toFixed(1)}%`;
  elements.metricSpeed.textContent = `${formatBytes(job.bytesPerSecond)}/sn`;
  elements.metricEta.textContent = formatDuration(job.etaSeconds);
  elements.metricSize.textContent = formatBytes(job.bytesDownloaded);
  elements.progressPanel.classList.toggle('is-idle', !activeStates.has(job.state));

  const hasError = Boolean(job.errorMessage) && job.state === 'failed';

  if (hasError) {
    elements.progressError.textContent = job.errorMessage;
    elements.progressError.hidden = false;
  } else {
    elements.progressError.hidden = true;
    elements.progressError.textContent = '';
  }

  const hasWarning = Boolean(job.warning);

  if (hasWarning) {
    elements.progressWarning.textContent = job.warning;
    elements.progressWarning.hidden = false;
  } else {
    elements.progressWarning.hidden = true;
    elements.progressWarning.textContent = '';
  }

  renderControls(job);
}

function renderHistory(entries) {
  state.history = Array.isArray(entries) ? entries : [];
  elements.historyList.replaceChildren();
  elements.historyEmpty.hidden = state.history.length > 0;
  elements.historyClearButton.disabled = state.history.length === 0;

  for (const entry of state.history) {
    const card = document.createElement('article');
    card.className = 'history-card';

    const title = document.createElement('h3');
    title.textContent = entry.title || entry.url || 'İndirilen yayın';

    const meta = document.createElement('div');
    meta.className = 'history-meta';

    const tags = [entry.platform, entry.qualityLabel, formatBytes(entry.sizeBytes), formatDate(entry.completedAtUtc)];

    for (const value of tags) {
      if (!value) {
        continue;
      }

      const tag = document.createElement('span');
      tag.className = 'history-tag';
      tag.textContent = value;
      meta.append(tag);
    }

    const actions = document.createElement('div');
    actions.className = 'history-actions';

    const revealButton = document.createElement('button');
    revealButton.type = 'button';
    revealButton.className = 'ghost-button';
    revealButton.textContent = 'Klasörde göster';
    revealButton.addEventListener('click', () => window.sky.revealFile(entry.filePath));

    const removeButton = document.createElement('button');
    removeButton.type = 'button';
    removeButton.className = 'ghost-button';
    removeButton.textContent = 'Kaydı sil';
    removeButton.addEventListener('click', () => removeHistoryEntry(entry.id));

    actions.append(revealButton, removeButton);
    card.append(title, meta, actions);
    elements.historyList.append(card);
  }
}

async function removeHistoryEntry(id) {
  const response = await window.sky.removeHistoryEntry(id);

  if (!response.ok) {
    toast(response.error ?? 'Geçmiş kaydı silinemedi.', 'error');
    return;
  }

  renderHistory(state.history.filter((entry) => entry.id !== id));
}

async function loadTools() {
  const response = await window.sky.getTools();

  if (!response.ok || !Array.isArray(response.data)) {
    elements.footerTools.textContent = 'Araç durumu bilinmiyor';
    return;
  }

  const parts = response.data.map((tool) => `${tool.name}: ${tool.available ? tool.version || 'hazır' : 'yok'}`);
  elements.footerTools.textContent = parts.join(' · ');
  elements.footerTools.classList.toggle('is-warning', response.data.some((tool) => tool.name === 'yt-dlp' && !tool.available));
}

async function loadJobs() {
  const response = await window.sky.getJobs();

  if (!response.ok) {
    return;
  }

  const jobs = Array.isArray(response.data) ? response.data : [];
  const current = jobs.find((job) => job.id === state.activeJobId);
  const latest = jobs.find((job) => activeStates.has(job.state) || job.state === 'paused');

  renderJob(current ?? latest ?? jobs[jobs.length - 1] ?? null);
}

async function loadHistory() {
  const response = await window.sky.getHistory();

  if (!response.ok) {
    return;
  }

  renderHistory(response.data);
}

function connectSocket() {
  const socket = new WebSocket(state.config.socketUrl);
  state.socket = socket;

  socket.addEventListener('open', () => {
    state.reconnectDelay = 800;
    setServiceStatus('ready', 'Servis bağlı');
    socket.send('ping');
  });

  socket.addEventListener('message', (event) => {
    let payload = null;

    try {
      payload = JSON.parse(event.data);
    } catch {
      return;
    }

    handleSocketMessage(payload);
  });

  socket.addEventListener('close', () => {
    setServiceStatus('offline', 'Bağlantı koptu');

    window.setTimeout(() => {
      state.reconnectDelay = Math.min(state.reconnectDelay * 1.6, 8000);
      connectSocket();
    }, state.reconnectDelay);
  });

  socket.addEventListener('error', () => {
    socket.close();
  });
}

function handleSocketMessage(payload) {
  if (!payload || typeof payload !== 'object') {
    return;
  }

  if (payload.kind === 'history') {
    renderJob(Array.isArray(payload.jobs) ? payload.jobs[payload.jobs.length - 1] ?? null : null);
    renderHistory(payload.history);
    return;
  }

  if (!payload.status || typeof payload.status !== 'object') {
    return;
  }

  const job = payload.status;

  if (payload.kind === 'finished' && job.state === 'completed') {
    toast(`İndirme tamamlandı: ${job.title ?? job.url}`, 'success');
  }

  if (job.state === 'failed' && job.errorMessage) {
    toast(job.errorMessage, 'error', 7000);
  }

  if (state.activeJobId && job.id !== state.activeJobId) {
    return;
  }

  if (!state.activeJobId && !activeStates.has(job.state) && job.state !== 'paused') {
    return;
  }

  renderJob(job);

  if (payload.entry) {
    const filtered = state.history.filter((entry) => entry.id !== payload.entry.id);
    renderHistory([payload.entry, ...filtered]);
  }
}

async function startDownload(event) {
  event.preventDefault();

  let resolution = state.resolution;

  if (!resolution) {
    resolution = await resolveLink();
  }

  if (!resolution) {
    return;
  }

  const response = await window.sky.startDownload({
    url: elements.urlInput.value.trim(),
    sessionId: resolution.sessionId,
    qualityId: elements.qualitySelect.value,
    audioTrackId: elements.languageSelect.value,
    outputDirectory: elements.directoryInput.value.trim(),
    fileName: sanitizeFileName(elements.fileNameInput.value),
    segmentConcurrency: Number(elements.concurrencyInput.value)
  });

  if (!response.ok) {
    elements.urlError.textContent = response.error ?? 'İndirme başlatılamadı.';
    toast(response.error ?? 'İndirme başlatılamadı.', 'error', 7000);
    renderControls(null);
    return;
  }

  toast('İndirme başlatıldı.', 'success');
  renderJob(response.data);
}

async function togglePause() {
  if (!state.job) {
    return;
  }

  const response = state.job.state === 'paused'
    ? await window.sky.resumeJob(state.job.id)
    : await window.sky.pauseJob(state.job.id);

  if (!response.ok) {
    toast(response.error ?? 'İşlem uygulanamadı.', 'error');
    return;
  }

  await loadJobs();
}

async function stopDownload() {
  if (!state.job) {
    return;
  }

  const response = await window.sky.cancelJob(state.job.id);

  if (!response.ok) {
    toast(response.error ?? 'İndirme durdurulamadı.', 'error');
    return;
  }

  toast('İndirme durduruldu.', 'info');
  renderJob({ ...state.job, state: 'cancelled', bytesPerSecond: 0, etaSeconds: 0 });
}

async function clearHistory() {
  if (state.history.length === 0) {
    return;
  }

  for (const entry of [...state.history]) {
    await removeHistoryEntry(entry.id);
  }

  renderHistory([]);
}

async function chooseDirectory() {
  const selection = await window.sky.chooseDirectory();

  if (!selection.ok) {
    return;
  }

  elements.directoryInput.value = selection.data ?? '';
  elements.footerOutput.textContent = `Çıktı: ${selection.data}`;
}

async function pasteFromClipboard() {
  try {
    const text = await navigator.clipboard.readText();

    if (typeof text === 'string' && text.trim().length > 0) {
      elements.urlInput.value = text.trim();
      elements.urlError.textContent = '';
      await resolveLink();
    }
  } catch {
    toast('Pano okunamadı, bağlantıyı elle yapıştırın.', 'error');
  }
}

async function bootstrap() {
  elements.form.addEventListener('submit', startDownload);
  elements.resolveButton.addEventListener('click', resolveLink);
  elements.pauseButton.addEventListener('click', togglePause);
  elements.stopButton.addEventListener('click', stopDownload);
  elements.pasteButton.addEventListener('click', pasteFromClipboard);
  elements.directoryButton.addEventListener('click', chooseDirectory);
  elements.historyRefreshButton.addEventListener('click', async () => {
    await loadHistory();
    await loadJobs();
  });
  elements.historyClearButton.addEventListener('click', clearHistory);
  elements.concurrencyInput.addEventListener('input', () => {
    elements.concurrencyValue.textContent = elements.concurrencyInput.value;
  });
  elements.urlInput.addEventListener('input', () => {
    elements.urlError.textContent = '';
  });
  elements.urlInput.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      resolveLink();
    }
  });

  window.sky.onLog(({ message }) => {
    if (typeof message === 'string' && message.trim().length > 0) {
      console.info('[sky]', message);
    }
  });

  const config = await window.sky.getConfig();

  state.config = { ...state.config, ...config };
  elements.directoryInput.value = config.outputDirectory ?? '';
  elements.footerEndpoint.textContent = `Sky back-end · ${config.host}:${config.port}`;
  elements.footerOutput.textContent = config.outputDirectory
    ? `Çıktı: ${config.outputDirectory}`
    : 'Çıktı klasörü sunucu tarafından belirlendi';

  setServiceStatus(config.ready ? 'ready' : 'pending', config.ready ? 'Servis hazır' : 'Bağlanıyor');

  const platforms = await window.sky.getPlatforms();

  if (platforms.ok) {
    renderPlatformChips(platforms.data);
  }

  await Promise.all([loadTools(), loadJobs(), loadHistory()]);
  connectSocket();
}

bootstrap();