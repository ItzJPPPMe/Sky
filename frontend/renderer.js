'use strict';

const api = window.sky;

const elements = {
  tabs: Array.from(document.querySelectorAll('.tab')),
  panels: Array.from(document.querySelectorAll('.tab-panel')),
  dropOverlay: document.getElementById('dropOverlay'),
  shortcutHint: document.getElementById('shortcutHint'),
  serviceBadge: document.getElementById('serviceBadge'),
  serviceBadgeText: document.getElementById('serviceBadgeText'),
  themeToggle: document.getElementById('themeToggle'),
  form: document.getElementById('downloadForm'),
  urlInput: document.getElementById('urlInput'),
  urlError: document.getElementById('urlError'),
  resolveButton: document.getElementById('resolveButton'),
  resolutionCard: document.getElementById('resolutionCard'),
  resolutionThumb: document.getElementById('resolutionThumb'),
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
  emptyState: document.getElementById('emptyState'),
  progressPanel: document.getElementById('progressPanel'),
  progressTitle: document.getElementById('progressTitle'),
  progressMeta: document.getElementById('progressMeta'),
  progressState: document.getElementById('progressState'),
  progressBar: document.getElementById('progressBar'),
  progressTrack: document.querySelector('.progress-track'),
  progressWarning: document.getElementById('progressWarning'),
  progressError: document.getElementById('progressError'),
  metricPercent: document.getElementById('metricPercent'),
  metricSpeed: document.getElementById('metricSpeed'),
  metricEta: document.getElementById('metricEta'),
  metricSize: document.getElementById('metricSize'),
  speedChart: document.getElementById('speedChart'),
  chartEmpty: document.getElementById('chartEmpty'),
  queueCount: document.getElementById('queueCount'),
  queueList: document.getElementById('queueList'),
  queueEmpty: document.getElementById('queueEmpty'),
  queueClearButton: document.getElementById('queueClearButton'),
  historyList: document.getElementById('historyList'),
  historyEmpty: document.getElementById('historyEmpty'),
  historyClearButton: document.getElementById('historyClearButton'),
  historySearch: document.getElementById('historySearch'),
  historyPlatformFilter: document.getElementById('historyPlatformFilter'),
  historySort: document.getElementById('historySort'),
  segments: Array.from(document.querySelectorAll('.segment')),
  settingsActions: document.querySelector('.settings-actions'),
  settingsSaveButton: document.getElementById('settingsSaveButton'),
  settingsResetButton: document.getElementById('settingsResetButton'),
  settingAccent: document.getElementById('settingAccent'),
  settingAccentText: document.getElementById('settingAccentText'),
  accentPresets: document.getElementById('accentPresets'),
  settingTheme: document.getElementById('settingTheme'),
  settingAnimations: document.getElementById('settingAnimations'),
  settingConcurrency: document.getElementById('settingConcurrency'),
  settingConcurrencyValue: document.getElementById('settingConcurrencyValue'),
  settingParallel: document.getElementById('settingParallel'),
  settingParallelValue: document.getElementById('settingParallelValue'),
  settingSpeed: document.getElementById('settingSpeed'),
  settingRetries: document.getElementById('settingRetries'),
  settingRetriesValue: document.getElementById('settingRetriesValue'),
  settingTemplate: document.getElementById('settingTemplate'),
  settingLanguage: document.getElementById('settingLanguage'),
  settingDirectory: document.getElementById('settingDirectory'),
  settingDirectoryButton: document.getElementById('settingDirectoryButton'),
  settingRemux: document.getElementById('settingRemux'),
  settingOpenFolder: document.getElementById('settingOpenFolder'),
  settingNotifications: document.getElementById('settingNotifications'),
  settingConfirm: document.getElementById('settingConfirm'),
  settingYtDlp: document.getElementById('settingYtDlp'),
  settingFfMpeg: document.getElementById('settingFfMpeg'),
  settingProxy: document.getElementById('settingProxy'),
  settingTimeout: document.getElementById('settingTimeout'),
  settingLivePoll: document.getElementById('settingLivePoll'),
  settingHistoryLimit: document.getElementById('settingHistoryLimit'),
  toolStatus: document.getElementById('toolStatus'),
  settingsRefreshTools: document.getElementById('settingsRefreshTools'),
  footerEndpoint: document.getElementById('footerEndpoint'),
  footerTools: document.getElementById('footerTools'),
  footerOpenFolder: document.getElementById('footerOpenFolder'),
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

const accentPresets = ['#38bdf8', '#818cf8', '#f472b6', '#f87171', '#fbbf24', '#34d399', '#22d3ee', '#a78bfa'];

const state = {
  config: { socketUrl: 'ws://127.0.0.1:47210/ws', outputDirectory: '', host: '', port: 0 },
  settings: null,
  resolution: null,
  activeJobId: null,
  job: null,
  jobs: [],
  history: [],
  toolStatus: [],
  socket: null,
  reconnectDelay: 800,
  resolving: false,
  speedSamples: [],
  lastDeleted: null,
  activeTab: 'download'
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

function toast(message, kind = 'info', options = {}) {
  const { timeout = 4500, actionLabel = null, onAction = null } = options;
  const node = document.createElement('div');
  node.className = `toast toast-${kind}`;

  const text = document.createElement('span');
  text.textContent = message;
  node.append(text);

  if (actionLabel) {
    const action = document.createElement('button');
    action.className = 'toast-action';
    action.type = 'button';
    action.textContent = actionLabel;
    action.addEventListener('click', () => {
      node.remove();
      if (onAction) {
        onAction();
      }
    });
    node.append(action);
  }

  elements.toastContainer.append(node);

  window.setTimeout(() => {
    node.classList.add('is-leaving');
    window.setTimeout(() => node.remove(), 260);
  }, timeout);
}

function switchTab(name) {
  state.activeTab = name;

  for (const tab of elements.tabs) {
    tab.classList.toggle('is-active', tab.dataset.tab === name);
  }

  for (const panel of elements.panels) {
    const active = panel.id === `panel-${name}`;
    panel.hidden = !active;
    panel.classList.toggle('is-active', active);
  }

  if (name === 'queue') {
    loadJobs();
  }

  if (name === 'history') {
    loadHistory();
  }
}

function applyAccent(color) {
  const safe = /^#[0-9a-f]{6}$/i.test(String(color ?? '')) ? color.toLowerCase() : '#38bdf8';
  const red = parseInt(safe.slice(1, 3), 16);
  const green = parseInt(safe.slice(3, 5), 16);
  const blue = parseInt(safe.slice(5, 7), 16);
  const root = document.documentElement;

  root.style.setProperty('--accent', safe);
  root.style.setProperty('--accent-soft', `rgba(${red}, ${green}, ${blue}, 0.16)`);

  const lightness = (0.299 * red + 0.587 * green + 0.114 * blue) / 255;
  root.style.setProperty('--accent-alt', lightness > 0.6 ? safe : `rgba(${red}, ${green}, ${blue}, 0.75)`);
}

function applyTheme(theme) {
  const prefersLight = window.matchMedia('(prefers-color-scheme: light)').matches;
  const resolved = theme === 'system' ? (prefersLight ? 'light' : 'dark') : theme;
  document.documentElement.dataset.theme = resolved;
}

function applyAnimations(level) {
  document.documentElement.dataset.animations = level === 'reduced' ? 'reduced' : 'full';
}

function renderAccentPresets(active) {
  elements.accentPresets.replaceChildren();

  for (const color of accentPresets) {
    const preset = document.createElement('button');
    preset.className = 'preset';
    preset.type = 'button';
    preset.style.background = `linear-gradient(140deg, ${color}, ${color}aa)`;
    preset.title = color;

    if (color.toLowerCase() === String(active).toLowerCase()) {
      preset.style.outline = '2px solid var(--text-primary)';
      preset.style.outlineOffset = '2px';
    }

    preset.addEventListener('click', () => {
      elements.settingAccent.value = color;
      elements.settingAccentText.value = color;
      applyAccent(color);
    });

    elements.accentPresets.append(preset);
  }
}

function renderPlatformChips(platforms) {
  elements.platformChips.replaceChildren();

  if (!Array.isArray(platforms)) {
    return;
  }

  for (const platform of platforms) {
    const chip = document.createElement('span');
    chip.className = 'chip';
    chip.dataset.id = String(platform.id ?? '').toLowerCase();
    chip.textContent = platform.name;
    chip.title = platform.requiresYtDlp ? `${platform.name} (yt-dlp gerekir)` : platform.name;
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

  if (resolution.thumbnailUrl) {
    elements.resolutionThumb.src = `http://${state.config.host}:${state.config.port}/api/thumbnail?url=${encodeURIComponent(resolution.thumbnailUrl)}`;
    elements.resolutionThumb.hidden = false;
  } else {
    elements.resolutionThumb.hidden = true;
    elements.resolutionThumb.removeAttribute('src');
  }

  const parts = [];

  if (resolution.uploader) {
    parts.push(resolution.uploader);
  }

  if (Number(resolution.durationSeconds) > 0) {
    parts.push(formatDuration(resolution.durationSeconds));
  }

  parts.push(`${resolution.qualities.length} kalite`);

  if (Number(resolution.audioTracks?.length ?? 0) > 1) {
    parts.push(`${resolution.audioTracks.length} ses dili`);
  }

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
  }

  markActiveChip(String(resolution.platform ?? '').toLowerCase());
}

function renderAudioTracks(resolution) {
  const tracks = Array.isArray(resolution.audioTracks) ? resolution.audioTracks : [];
  elements.languageSelect.replaceChildren();

  if (tracks.length === 0) {
    const option = document.createElement('option');
    option.value = '';
    option.textContent = 'Orijinal ses (akışa gömülü)';
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
    option.textContent = marks.length > 0 ? `${track.label} (${marks.join(', ')})` : track.label;
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
  elements.startButton.disabled = true;

  try {
    const response = await api.resolveLink({ url: elements.urlInput.value.trim() });

    if (!response.ok) {
      showResolutionError(response.error ?? 'Bağlantı çözümlenemedi.');
      toast(response.error ?? 'Bağlantı çözümlenemedi.', 'error', { timeout: 7000 });
      return null;
    }

    renderResolution(response.data);
    return response.data;
  } finally {
    state.resolving = false;
    elements.resolveButton.disabled = false;
    elements.resolveButton.textContent = 'Çözümle';
    renderControls(state.job);
  }
}

function renderControls(job) {
  const isActive = Boolean(job) && activeStates.has(job.state);
  const isPaused = Boolean(job) && job.state === 'paused';
  const anyActive = state.jobs.some((item) => activeStates.has(item.state));

  elements.startButton.disabled = isActive || isPaused || state.resolving;
  elements.pauseButton.disabled = !isActive && !isPaused;
  elements.stopButton.disabled = !job || !isActive;
  elements.pauseButton.querySelector('span').textContent = isPaused ? 'Devam Et' : 'Duraklat';
  elements.queueCount.hidden = !anyActive;
  elements.queueCount.textContent = String(state.jobs.filter((item) => activeStates.has(item.state)).length);
}

function pushSpeedSample(value) {
  state.speedSamples.push(Math.max(0, Number(value) || 0));

  if (state.speedSamples.length > 60) {
    state.speedSamples.shift();
  }
}

function drawSpeedChart() {
  const canvas = elements.speedChart;
  const context = canvas.getContext('2d');
  const ratio = window.devicePixelRatio || 1;
  const width = canvas.clientWidth || 600;
  const height = 72;

  canvas.width = width * ratio;
  canvas.height = height * ratio;
  context.setTransform(ratio, 0, 0, ratio, 0, 0);
  context.clearRect(0, 0, width, height);

  const samples = state.speedSamples;

  if (samples.length < 2) {
    return;
  }

  const accent = getComputedStyle(document.documentElement).getPropertyValue('--accent').trim() || '#38bdf8';
  const maximum = Math.max(...samples, 1024);
  const step = width / (samples.length - 1);

  context.beginPath();

  samples.forEach((value, index) => {
    const x = index * step;
    const y = height - (value / maximum) * (height - 8) - 4;

    if (index === 0) {
      context.moveTo(x, y);
    } else {
      context.lineTo(x, y);
    }
  });

  context.strokeStyle = accent;
  context.lineWidth = 2;
  context.lineJoin = 'round';
  context.stroke();

  context.lineTo(width, height);
  context.lineTo(0, height);
  context.closePath();

  const gradient = context.createLinearGradient(0, 0, 0, height);
  gradient.addColorStop(0, `${accent}55`);
  gradient.addColorStop(1, `${accent}00`);
  context.fillStyle = gradient;
  context.fill();
}

function renderJob(job) {
  state.job = job;

  if (job) {
    state.activeJobId = job.id;
  }

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
  const label = stateLabels[job.state] ?? job.state ?? 'Bekliyor';
  elements.progressState.textContent = job.isLive ? `${label} (canlı)` : label;
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

function jobThumbnailUrl(job) {
  if (!job || !job.url) {
    return '';
  }

  const match = job.url.match(/(?:v=|youtu\.be\/|shorts\/)([A-Za-z0-9_-]{6,})/);

  if (!match) {
    return '';
  }

  return `http://${state.config.host}:${state.config.port}/api/thumbnail?url=${encodeURIComponent(`https://i.ytimg.com/vi/${match[1]}/mqdefault.jpg`)}`;
}

function renderQueue() {
  const jobs = state.jobs;

  elements.queueList.replaceChildren();
  elements.queueEmpty.hidden = jobs.length > 0;

  for (const job of jobs) {
    const card = document.createElement('article');
    card.className = 'queue-card';

    const thumb = document.createElement('img');
    thumb.className = 'queue-thumb';
    thumb.alt = '';
    const thumbUrl = jobThumbnailUrl(job);

    if (thumbUrl) {
      thumb.src = thumbUrl;
      thumb.addEventListener('error', () => {
        thumb.classList.add('placeholder');
        thumb.textContent = (job.platform || 'S').slice(0, 1).toUpperCase();
        thumb.removeAttribute('src');
      });
    } else {
      thumb.classList.add('placeholder');
      thumb.textContent = (job.platform || 'S').slice(0, 1).toUpperCase();
    }

    const info = document.createElement('div');
    info.className = 'queue-info';

    const title = document.createElement('h3');
    title.textContent = job.title || job.url;

    const meta = document.createElement('p');
    meta.className = 'history-meta';

    const percent = Math.max(0, Math.min(100, Number(job.progressPercent) || 0));
    meta.textContent = `${stateLabels[job.state] ?? job.state} · %{percent.toFixed(0)} · ${formatBytes(job.bytesPerSecond)}/sn`;

    const bar = document.createElement('div');
    bar.className = 'queue-progress';

    const fill = document.createElement('span');
    fill.style.width = `${percent}%`;
    bar.append(fill);
    info.append(title, meta, bar);

    const actions = document.createElement('div');
    actions.className = 'queue-actions';

    if (activeStates.has(job.state) || job.state === 'paused') {
      const toggle = document.createElement('button');
      toggle.className = 'ghost-button';
      toggle.type = 'button';
      toggle.textContent = job.state === 'paused' ? 'Devam' : 'Duraklat';
      toggle.addEventListener('click', async () => {
        const response = job.state === 'paused' ? await api.resumeJob(job.id) : await api.pauseJob(job.id);

        if (!response.ok) {
          toast(response.error ?? 'İşlem uygulanamadı.', 'error');
        }

        loadJobs();
      });

      const cancel = document.createElement('button');
      cancel.className = 'ghost-button';
      cancel.type = 'button';
      cancel.textContent = 'Durdur';
      cancel.addEventListener('click', async () => {
        await api.cancelJob(job.id);
        loadJobs();
      });

      actions.append(toggle, cancel);
    } else {
      const focus = document.createElement('button');
      focus.className = 'ghost-button';
      focus.type = 'button';
      focus.textContent = 'Detay';
      focus.addEventListener('click', () => {
        state.activeJobId = job.id;
        renderJob(job);
        switchTab('download');
      });

      const clear = document.createElement('button');
      clear.className = 'ghost-button';
      clear.type = 'button';
      clear.textContent = 'Listeden çıkar';
      clear.addEventListener('click', () => {
        state.jobs = state.jobs.filter((item) => item.id !== job.id);
        renderQueue();
      });

      actions.append(focus, clear);
    }

    card.append(thumb, info, actions);
    elements.queueList.append(card);
  }
}

function filteredHistory() {
  const query = elements.historySearch.value.trim().toLowerCase();
  const platform = elements.historyPlatformFilter.value;
  const sort = elements.historySort.value;

  let items = [...state.history];

  if (query.length > 0) {
    items = items.filter((entry) => `${entry.title ?? ''} ${entry.url ?? ''}`.toLowerCase().includes(query));
  }

  if (platform.length > 0) {
    items = items.filter((entry) => entry.platform === platform);
  }

  const compare = {
    'date-desc': (a, b) => new Date(b.completedAtUtc ?? 0) - new Date(a.completedAtUtc ?? 0),
    'date-asc': (a, b) => new Date(a.completedAtUtc ?? 0) - new Date(b.completedAtUtc ?? 0),
    'size-desc': (a, b) => (Number(b.sizeBytes) || 0) - (Number(a.sizeBytes) || 0),
    'size-asc': (a, b) => (Number(a.sizeBytes) || 0) - (Number(b.sizeBytes) || 0),
    title: (a, b) => String(a.title ?? '').localeCompare(String(b.title ?? ''), 'tr')
  };

  return items.sort(compare[sort] ?? compare['date-desc']);
}

function renderHistory() {
  const items = filteredHistory();
  const gridMode = elements.segments.some((segment) => segment.classList.contains('is-active') && segment.dataset.view === 'grid');

  elements.historyList.replaceChildren();
  elements.historyList.classList.toggle('is-grid', gridMode);
  elements.historyEmpty.hidden = items.length > 0;
  elements.historyEmpty.querySelector('h3').textContent = state.history.length === 0 ? 'Geçmiş boş' : 'Sonuç bulunamadı';
  elements.historyEmpty.querySelector('p').textContent =
    state.history.length === 0 ? 'Tamamlanan indirmeler burada listelenir.' : 'Arama veya filtre kriterlerinizi değiştirin.';
  elements.historyClearButton.disabled = state.history.length === 0;

  for (const entry of items) {
    const card = document.createElement('article');
    card.className = 'history-card';

    const thumb = document.createElement('img');
    thumb.className = 'history-thumb';
    thumb.alt = '';
    const thumbUrl = jobThumbnailUrl(entry);

    if (thumbUrl) {
      thumb.src = thumbUrl;
      thumb.addEventListener('error', () => {
        thumb.classList.add('placeholder');
        thumb.textContent = (entry.platform || 'S').slice(0, 1).toUpperCase();
        thumb.removeAttribute('src');
      });
    } else {
      thumb.classList.add('placeholder');
      thumb.textContent = (entry.platform || 'S').slice(0, 1).toUpperCase();
    }

    const main = document.createElement('div');
    main.className = 'history-main';

    const title = document.createElement('h3');
    title.textContent = entry.title || entry.url || 'İndirilen yayın';

    const meta = document.createElement('div');
    meta.className = 'history-meta';

    for (const value of [entry.platform, entry.qualityLabel, formatBytes(entry.sizeBytes), formatDate(entry.completedAtUtc)]) {
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

    const reveal = document.createElement('button');
    reveal.className = 'ghost-button';
    reveal.type = 'button';
    reveal.textContent = 'Klasörde göster';
    reveal.addEventListener('click', () => api.revealFile(entry.filePath));

    const copy = document.createElement('button');
    copy.className = 'ghost-button';
    copy.type = 'button';
    copy.textContent = 'Yolu kopyala';
    copy.addEventListener('click', async () => {
      const response = await api.copyToClipboard(entry.filePath);

      if (response.ok) {
        toast('Dosya yolu kopyalandı.', 'success');
      }
    });

    const retry = document.createElement('button');
    retry.className = 'ghost-button';
    retry.type = 'button';
    retry.textContent = 'Yeniden indir';
    retry.addEventListener('click', async () => {
      elements.urlInput.value = entry.url;
      switchTab('download');
      await resolveLink();
      toast('Bağlantı hazır, indirmeyi başlatabilirsiniz.', 'info');
    });

    const remove = document.createElement('button');
    remove.className = 'ghost-button';
    remove.type = 'button';
    remove.textContent = 'Sil';
    remove.addEventListener('click', () => removeHistoryEntry(entry));

    actions.append(reveal, copy, retry, remove);
    main.append(title, meta, actions);
    card.append(thumb, main);
    elements.historyList.append(card);
  }
}

async function removeHistoryEntry(entry) {
  const response = await api.removeHistoryEntry(entry.id);

  if (!response.ok) {
    toast(response.error ?? 'Geçmiş kaydı silinemedi.', 'error');
    return;
  }

  renderHistory();

  toast('Geçmiş kaydı silindi.', 'success', {
    actionLabel: 'Geri al',
    onAction: async () => {
      toast('Bu işlem geri alınamaz, kayıt yeniden indirilebilir.', 'info');
    }
  });
}

function renderToolStatus() {
  elements.toolStatus.replaceChildren();

  for (const tool of state.toolStatus) {
    const row = document.createElement('div');
    row.className = `tool-row ${tool.available ? 'is-ok' : 'is-missing'}`;

    const name = document.createElement('strong');
    name.textContent = tool.name;

    const detail = document.createElement('span');
    detail.textContent = tool.available ? (tool.version || 'hazır') : 'bulunamadı';
    detail.title = tool.path || '';

    row.append(name, detail);
    elements.toolStatus.append(row);
  }

  const summary = state.toolStatus
    .map((tool) => `${tool.name}: ${tool.available ? 'hazır' : 'yok'}`)
    .join(' · ');

  elements.footerTools.textContent = summary;
  elements.footerTools.classList.toggle('is-warning', state.toolStatus.some((tool) => !tool.available));
}

function renderSettingsForm() {
  const settings = state.settings;

  if (!settings) {
    return;
  }

  elements.settingAccent.value = settings.accentColor;
  elements.settingAccentText.value = settings.accentColor;
  renderAccentPresets(settings.accentColor);
  elements.settingTheme.value = settings.theme;
  elements.settingAnimations.value = settings.animationLevel;
  elements.settingConcurrency.value = settings.segmentConcurrency;
  elements.settingConcurrencyValue.textContent = settings.segmentConcurrency;
  elements.settingParallel.value = settings.maxParallelJobs;
  elements.settingParallelValue.textContent = settings.maxParallelJobs;
  elements.settingSpeed.value = settings.speedLimitKbps;
  elements.settingRetries.value = settings.segmentRetries;
  elements.settingRetriesValue.textContent = settings.segmentRetries;
  elements.settingTemplate.value = settings.fileNameTemplate;
  elements.settingLanguage.value = settings.preferredLanguage ?? '';
  elements.settingDirectory.value = settings.outputDirectory ?? '';
  elements.settingRemux.checked = settings.autoRemuxToMp4;
  elements.settingOpenFolder.checked = settings.autoOpenFolder;
  elements.settingNotifications.checked = settings.notifications;
  elements.settingConfirm.checked = settings.confirmBeforeStart;
  elements.settingYtDlp.value = settings.ytdlpPath ?? '';
  elements.settingFfMpeg.value = settings.ffmpegPath ?? '';
  elements.settingProxy.value = settings.proxyUrl ?? '';
  elements.settingTimeout.value = settings.requestTimeoutSeconds;
  elements.settingLivePoll.value = settings.livePollDivisor;
  elements.settingHistoryLimit.value = settings.historyLimit;

  elements.concurrencyInput.value = settings.segmentConcurrency;
  elements.concurrencyValue.textContent = settings.segmentConcurrency;

  if (!elements.directoryInput.dataset.touched) {
    elements.directoryInput.value = settings.outputDirectory ?? '';
  }

  applyAccent(settings.accentColor);
  applyTheme(settings.theme);
  applyAnimations(settings.animationLevel);
}

async function saveSettings() {
  const payload = {
    accentColor: elements.settingAccent.value,
    theme: elements.settingTheme.value,
    animationLevel: elements.settingAnimations.value,
    segmentConcurrency: Number(elements.settingConcurrency.value),
    segmentRetries: Number(elements.settingRetries.value),
    requestTimeoutSeconds: Number(elements.settingTimeout.value),
    livePollDivisor: Number(elements.settingLivePoll.value),
    preferredLanguage: elements.settingLanguage.value,
    outputDirectory: elements.settingDirectory.value,
    fileNameTemplate: elements.settingTemplate.value,
    ytdlpPath: elements.settingYtDlp.value,
    ffmpegPath: elements.settingFfMpeg.value,
    maxParallelJobs: Number(elements.settingParallel.value),
    speedLimitKbps: Number(elements.settingSpeed.value) || 0,
    autoRemuxToMp4: elements.settingRemux.checked,
    autoOpenFolder: elements.settingOpenFolder.checked,
    notifications: elements.settingNotifications.checked,
    historyLimit: Number(elements.settingHistoryLimit.value) || 200,
    proxyUrl: elements.settingProxy.value,
    confirmBeforeStart: elements.settingConfirm.checked,
    resumeDownloads: true
  };

  const response = await api.saveSettings(payload);

  if (!response.ok) {
    toast(response.error ?? 'Ayarlar kaydedilemedi.', 'error');
    return false;
  }

  state.settings = response.data.settings;
  renderSettingsForm();
  await loadTools();
  toast('Ayarlar kaydedildi.', 'success');
  return true;
}

async function loadSettings() {
  const response = await api.getSettings();

  if (response.ok && response.data.settings) {
    state.settings = response.data.settings;
    state.config.defaultOutputDirectory = response.data.defaultOutputDirectory;
    renderSettingsForm();
  }
}

async function loadTools() {
  const response = await api.getTools();

  if (!response.ok) {
    elements.footerTools.textContent = 'Araç durumu bilinmiyor';
    return;
  }

  state.toolStatus = Array.isArray(response.data) ? response.data : [];
  renderToolStatus();
}

async function loadJobs() {
  const response = await api.getJobs();

  if (!response.ok) {
    return;
  }

  state.jobs = Array.isArray(response.data) ? response.data : [];
  renderQueue();

  const current = state.jobs.find((job) => job.id === state.activeJobId);
  const latest = state.jobs.find((job) => activeStates.has(job.state)) ?? state.jobs[state.jobs.length - 1];
  renderJob(current ?? latest ?? null);
  renderControls(state.job);
}

async function loadHistory() {
  const response = await api.getHistory();

  if (!response.ok) {
    return;
  }

  state.history = Array.isArray(response.data) ? response.data : [];
  renderHistory();
}

async function togglePause() {
  if (!state.job) {
    return;
  }

  const response =
    state.job.state === 'paused' ? await api.resumeJob(state.job.id) : await api.pauseJob(state.job.id);

  if (!response.ok) {
    toast(response.error ?? 'İşlem uygulanamadı.', 'error');
    return;
  }

  loadJobs();
}

async function stopDownload() {
  if (!state.job) {
    return;
  }

  const response = await api.cancelJob(state.job.id);

  if (!response.ok) {
    toast(response.error ?? 'İndirme durdurulamadı.', 'error');
    return;
  }

  toast('İndirme durduruldu.', 'info');
  loadJobs();
}

async function startDownload(event) {
  if (event) {
    event.preventDefault();
  }

  const validation = validateUrl(elements.urlInput.value);

  elements.urlError.textContent = validation.message;

  if (!validation.valid) {
    elements.urlInput.focus();
    return;
  }

  if (state.settings?.confirmBeforeStart) {
    const confirmed = window.confirm('İndirmeyi başlatmak istediğinize emin misiniz?');

    if (!confirmed) {
      return;
    }
  }

  let resolution = state.resolution;

  if (!resolution) {
    resolution = await resolveLink();
  }

  if (!resolution) {
    return;
  }

  const response = await api.startDownload({
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
    toast(response.error ?? 'İndirme başlatılamadı.', 'error', { timeout: 7000 });
    renderControls(null);
    return;
  }

  state.activeJobId = response.data.id;
  state.speedSamples = [];
  toast('İndirme kuyruğa alındı.', 'success');
  renderJob(response.data);
  loadJobs();
}

async function chooseDirectory(target) {
  const selection = await api.chooseDirectory();

  if (!selection.ok) {
    return;
  }

  if (target === 'settings') {
    elements.settingDirectory.value = selection.data ?? '';
  } else {
    elements.directoryInput.value = selection.data ?? '';
    elements.directoryInput.dataset.touched = 'true';
    elements.footerOutput.textContent = `Çıktı: ${selection.data}`;
  }
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

  socket.addEventListener('error', () => socket.close());
}

function handleSocketMessage(payload) {
  if (!payload || typeof payload !== 'object') {
    return;
  }

  if (payload.kind === 'history') {
    state.jobs = Array.isArray(payload.jobs) ? payload.jobs : [];
    renderQueue();
    renderHistory(payload.history);
    renderJob(state.jobs[state.jobs.length - 1] ?? null);
    return;
  }

  if (!payload.status || typeof payload.status !== 'object') {
    return;
  }

  const job = payload.status;
  const known = state.jobs.some((item) => item.id === job.id);
  state.jobs = known ? state.jobs.map((item) => (item.id === job.id ? job : item)) : [job, ...state.jobs];

  if (payload.kind === 'finished' && job.state === 'completed') {
    toast(`İndirme tamamlandı: ${job.title ?? job.url}`, 'success');

    if (state.settings?.notifications) {
      api.notify('İndirme tamamlandı', job.title || job.url || 'Yayın tekrarı');
    }

    if (state.settings?.autoOpenFolder) {
      api.openFolder(job.filePath);
    }
  }

  if (job.state === 'failed' && job.errorMessage) {
    toast(job.errorMessage, 'error', { timeout: 7000 });
  }

  if (state.activeJobId && job.id === state.activeJobId) {
    renderJob(job);
  } else if (!state.activeJobId && activeStates.has(job.state)) {
    state.activeJobId = job.id;
    renderJob(job);
  }

  if (activeStates.has(job.state)) {
    pushSpeedSample(job.bytesPerSecond);
    elements.chartEmpty.hidden = state.speedSamples.length > 1;
    drawSpeedChart();
  }

  if (payload.entry) {
    state.history = [payload.entry, ...state.history.filter((entry) => entry.id !== payload.entry.id)];
    renderHistory();
  }

  renderQueue();
  renderControls(state.job);
}

function bindEvents() {
  for (const tab of elements.tabs) {
    tab.addEventListener('click', () => switchTab(tab.dataset.tab));
  }

  elements.form.addEventListener('submit', startDownload);
  elements.resolveButton.addEventListener('click', resolveLink);
  elements.pauseButton.addEventListener('click', togglePause);
  elements.stopButton.addEventListener('click', stopDownload);
  elements.pasteButton.addEventListener('click', pasteFromClipboard);
  elements.directoryButton.addEventListener('click', () => chooseDirectory('download'));
  elements.queueClearButton.addEventListener('click', () => {
    state.jobs = state.jobs.filter((job) => activeStates.has(job.state));
    renderQueue();
  });
  elements.historyClearButton.addEventListener('click', async () => {
    for (const entry of [...state.history]) {
      await api.removeHistoryEntry(entry.id);
    }

    loadHistory();
    toast('Geçmiş temizlendi.', 'success');
  });

  elements.historySearch.addEventListener('input', renderHistory);
  elements.historyPlatformFilter.addEventListener('change', renderHistory);
  elements.historySort.addEventListener('change', renderHistory);

  for (const segment of elements.segments) {
    segment.addEventListener('click', () => {
      for (const other of elements.segments) {
        other.classList.toggle('is-active', other === segment);
      }

      renderHistory();
    });
  }

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

  elements.settingAccent.addEventListener('input', () => {
    elements.settingAccentText.value = elements.settingAccent.value;
    applyAccent(elements.settingAccent.value);
    renderAccentPresets(elements.settingAccent.value);
  });

  elements.settingAccentText.addEventListener('change', () => {
    const value = elements.settingAccentText.value.trim();

    if (/^#[0-9a-f]{6}$/i.test(value)) {
      elements.settingAccent.value = value;
      applyAccent(value);
      renderAccentPresets(value);
    } else {
      elements.settingAccentText.value = elements.settingAccent.value;
    }
  });

  elements.settingTheme.addEventListener('change', () => applyTheme(elements.settingTheme.value));
  elements.settingAnimations.addEventListener('change', () => applyAnimations(elements.settingAnimations.value));

  for (const [input, label] of [
    [elements.settingConcurrency, elements.settingConcurrencyValue],
    [elements.settingParallel, elements.settingParallelValue],
    [elements.settingRetries, elements.settingRetriesValue]
  ]) {
    input.addEventListener('input', () => {
      label.textContent = input.value;
    });
  }

  elements.settingsSaveButton.addEventListener('click', saveSettings);
  elements.settingsResetButton.addEventListener('click', async () => {
    const response = await api.resetSettings();

    if (response.ok) {
      state.settings = response.data.settings;
      renderSettingsForm();
      await loadTools();
      toast('Ayarlar sıfırlandı.', 'info');
    }
  });

  elements.settingsRefreshTools.addEventListener('click', async () => {
    elements.settingsRefreshTools.disabled = true;
    await api.refreshTools();
    await loadTools();
    elements.settingsRefreshTools.disabled = false;
    toast('Araçlar yeniden tarandı.', 'success');
  });

  elements.settingDirectoryButton.addEventListener('click', () => chooseDirectory('settings'));
  elements.footerOpenFolder.addEventListener('click', () => api.openFolder(elements.directoryInput.value));

  elements.themeToggle.addEventListener('click', () => {
    const current = document.documentElement.dataset.theme === 'light' ? 'dark' : 'light';
    elements.settingTheme.value = current;
    applyTheme(current);
    saveSettings();
  });

  window.matchMedia('(prefers-color-scheme: light)').addEventListener('change', () => {
    if (state.settings?.theme === 'system') {
      applyTheme('system');
    }
  });

  document.addEventListener('keydown', (event) => {
    const mod = event.ctrlKey || event.metaKey;

    if (mod && event.key === 'Enter') {
      event.preventDefault();
      startDownload();
    } else if (mod && event.key === ',') {
      event.preventDefault();
      switchTab('settings');
    } else if (mod && event.key.toLowerCase() === 'l') {
      event.preventDefault();
      switchTab('download');
      elements.urlInput.focus();
    }
  });

  window.addEventListener('dragover', (event) => {
    event.preventDefault();
    elements.dropOverlay.hidden = false;
  });

  window.addEventListener('dragleave', (event) => {
    if (event.relatedTarget === null) {
      elements.dropOverlay.hidden = true;
    }
  });

  window.addEventListener('drop', async (event) => {
    event.preventDefault();
    elements.dropOverlay.hidden = true;

    const text = event.dataTransfer?.getData('text/plain');

    if (typeof text === 'string' && text.trim().length > 0 && validateUrl(text).valid) {
      elements.urlInput.value = text.trim();
      switchTab('download');
      await resolveLink();
    }
  });

  window.addEventListener('resize', drawSpeedChart);
  api.onLog(({ message, stream }) => {
    if (typeof message !== 'string' || message.trim().length === 0) {
      return;
    }

    const noisy = /Microsoft\.AspNetCore|Microsoft\.Hosting|NOW listening|Application started|Content root/i;

    if (stream === 'error') {
      console.error('[sky]', message);
    } else if (!noisy.test(message)) {
      console.info('[sky]', message);
    }
  });
}

async function bootstrap() {
  bindEvents();

  elements.progressPanel.hidden = true;
  elements.emptyState.hidden = false;
  elements.queueList.replaceChildren();
  elements.historyList.replaceChildren();
  elements.resolutionCard.hidden = true;
  elements.urlError.textContent = '';
  elements.shortcutHint.hidden = true;

  const config = await api.getConfig();

  state.config = { ...state.config, ...config };
  elements.directoryInput.value = config.outputDirectory ?? '';
  elements.footerEndpoint.textContent = `Sky back-end · ${config.host}:${config.port}`;
  elements.footerOutput.textContent = config.outputDirectory
    ? `Çıktı: ${config.outputDirectory}`
    : 'Çıktı klasörü sunucu tarafından belirlendi';

  setServiceStatus(config.ready ? 'ready' : 'pending', config.ready ? 'Servis hazır' : 'Bağlanıyor');

  await loadSettings();

  const platforms = await api.getPlatforms();

  if (platforms.ok) {
    renderPlatformChips(platforms.data);
  }

  await Promise.all([loadTools(), loadJobs(), loadHistory()]);
  window.setTimeout(() => {
    elements.shortcutHint.hidden = false;
  }, 900);
  connectSocket();
}

bootstrap();