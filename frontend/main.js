'use strict';

const { app, BrowserWindow, dialog, ipcMain, shell, clipboard, Notification } = require('electron');
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const PORT = Number.parseInt(process.env.SKY_PORT ?? '47210', 10);
const HOST = process.env.SKY_HOST ?? '127.0.0.1';
const API_BASE = `http://${HOST}:${PORT}`;
const REQUEST_ATTEMPTS = 60;
const REQUEST_INTERVAL = 500;

let mainWindow = null;
let backendProcess = null;
let outputDirectory = process.env.SKY_OUTPUT_DIRECTORY ?? '';

function projectRoot() {
  return path.join(__dirname, '..');
}

function binaryName() {
  return process.platform === 'win32' ? 'Sky.exe' : 'Sky';
}

function ffmpegBinaryName() {
  return process.platform === 'win32' ? 'ffmpeg.exe' : 'ffmpeg';
}

function resolveFfmpeg() {
  if (app.isPackaged) {
    const bundled = path.join(process.resourcesPath, 'ffmpeg', ffmpegBinaryName());

    if (fs.existsSync(bundled)) {
      return bundled;
    }
  }

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

function resolveYtDlp() {
  if (app.isPackaged) {
    const bundled = path.join(process.resourcesPath, 'tools', ffmpegBinaryName().replace('ffmpeg', 'yt-dlp'));

    if (fs.existsSync(bundled)) {
      return bundled;
    }
  }

  return process.env.SKY_YTDLP_PATH || null;
}

function resolveBackendCommand() {
  if (app.isPackaged) {
    return {
      command: path.join(process.resourcesPath, 'backend', binaryName()),
      args: []
    };
  }

  const developmentBinary = path.join(
    projectRoot(),
    'backend',
    'bin',
    'Release',
    'net8.0',
    binaryName()
  );

  if (fs.existsSync(developmentBinary)) {
    return { command: developmentBinary, args: [] };
  }

  return {
    command: 'dotnet',
    args: ['run', '--project', path.join(projectRoot(), 'backend', 'Sky.fsproj'), '--configuration', 'Release']
  };
}

function send(channel, payload) {
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send(channel, payload);
  }
}

function startBackend() {
  const { command, args } = resolveBackendCommand();
  const ffmpegPath = resolveFfmpeg();
  const ytdlpPath = resolveYtDlp();

  const environment = { ...process.env, SKY_PORT: String(PORT), SKY_HOST: HOST };

  if (ffmpegPath) {
    environment.SKY_FFMPEG_PATH = ffmpegPath;
  }

  if (ytdlpPath) {
    environment.SKY_YTDLP_PATH = ytdlpPath;
  }

  backendProcess = spawn(command, args, {
    cwd: app.isPackaged ? process.resourcesPath : projectRoot(),
    env: environment,
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true
  });

  backendProcess.stdout.on('data', (chunk) => {
    const lines = chunk.toString().split(/\r?\n/).filter((line) => line.trim().length > 0);

    for (const line of lines) {
      send('sky:log', { stream: 'out', message: line });
    }
  });

  backendProcess.stderr.on('data', (chunk) => {
    const lines = chunk.toString().split(/\r?\n/).filter((line) => line.trim().length > 0);

    for (const line of lines) {
      send('sky:log', { stream: 'error', message: line });
    }
  });

  backendProcess.on('exit', (code) => {
    send('sky:log', { stream: 'error', message: `Sky back-end sonlandı (kod ${code}).` });
  });

  backendProcess.on('error', (error) => {
    send('sky:log', { stream: 'error', message: `Sky back-end başlatılamadı: ${error.message}` });
  });
}

function stopBackend() {
  if (!backendProcess) {
    return;
  }

  const child = backendProcess;
  backendProcess = null;

  if (process.platform === 'win32') {
    spawn('taskkill', ['/pid', String(child.pid), '/T', '/F'], { windowsHide: true });
  } else {
    child.kill('SIGTERM');
  }
}

async function request(method, route, body) {
  let lastError = null;

  for (let attempt = 0; attempt < REQUEST_ATTEMPTS; attempt += 1) {
    try {
      const response = await fetch(`${API_BASE}${route}`, {
        method,
        headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body)
      });

      if (response.status === 204) {
        return { ok: response.ok, status: response.status, data: null };
      }

      const text = await response.text();
      const data = text.length === 0 ? null : JSON.parse(text);

      return { ok: response.ok, status: response.status, data };
    } catch (error) {
      lastError = error;
      await new Promise((resolve) => setTimeout(resolve, REQUEST_INTERVAL));
    }
  }

  throw lastError ?? new Error('Sky back-end ile iletişim kurulamadı.');
}

function toFailure(result) {
  if (result.ok) {
    return null;
  }

  const detail = result.data && result.data.detail ? result.data.detail : 'İşlem tamamlanamadı.';
  return { message: detail, status: result.status };
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1180,
    height: 820,
    minWidth: 940,
    minHeight: 680,
    show: false,
    backgroundColor: '#0b1020',
    title: 'Sky',
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      spellcheck: false
    }
  });

  mainWindow.removeMenu();
  mainWindow.loadFile(path.join(__dirname, 'index.html'));

  mainWindow.once('ready-to-show', () => {
    mainWindow.show();
  });

  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    shell.openExternal(url);
    return { action: 'deny' };
  });

  mainWindow.on('closed', () => {
    mainWindow = null;
  });
}

function registerHandlers() {
  ipcMain.handle('sky:config', async () => {
    const health = await request('GET', '/api/health');
    const failure = toFailure(health);

    if (health.data && typeof health.data.outputDirectory === 'string' && health.data.outputDirectory.length > 0) {
      outputDirectory = health.data.outputDirectory;
    }

    return {
      host: HOST,
      port: PORT,
      socketUrl: `ws://${HOST}:${PORT}/ws`,
      outputDirectory,
      platform: process.platform,
      ready: failure === null && health.ok
    };
  });

  ipcMain.handle('sky:platforms', async () => {
    const result = await request('GET', '/api/platforms');
    const failure = toFailure(result);

    return { ok: failure === null, data: result.data, error: failure ? failure.message : null };
  });

  ipcMain.handle('sky:tools', async () => {
    const result = await request('GET', '/api/tools');
    const failure = toFailure(result);

    return { ok: failure === null, data: result.data, error: failure ? failure.message : null };
  });

  ipcMain.handle('sky:tools/refresh', async () => {
    const result = await request('POST', '/api/tools/refresh');
    const failure = toFailure(result);

    return { ok: failure === null, data: result.data, error: failure ? failure.message : null };
  });

  ipcMain.handle('sky:settings:get', async () => {
    const result = await request('GET', '/api/settings');
    const failure = toFailure(result);

    return { ok: failure === null, data: result.data, error: failure ? failure.message : null };
  });

  ipcMain.handle('sky:settings:save', async (_event, payload) => {
    const result = await request('POST', '/api/settings', payload);
    const failure = toFailure(result);

    return { ok: failure === null, data: result.data, error: failure ? failure.message : null };
  });

  ipcMain.handle('sky:settings/reset', async () => {
    const result = await request('POST', '/api/settings/reset');
    const failure = toFailure(result);

    return { ok: failure === null, data: result.data, error: failure ? failure.message : null };
  });

  ipcMain.handle('sky:resolve', async (_event, payload) => {
    const result = await request('POST', '/api/resolve', payload);
    const failure = toFailure(result);

    return { ok: failure === null, data: result.data, error: failure ? failure.message : null };
  });

  ipcMain.handle('sky:qualities', async () => {
    const result = await request('GET', '/api/qualities');
    const failure = toFailure(result);

    return { ok: failure === null, data: result.data, error: failure ? failure.message : null };
  });

  ipcMain.handle('sky:jobs', async () => {
    const result = await request('GET', '/api/jobs');
    const failure = toFailure(result);

    return { ok: failure === null, data: result.data, error: failure ? failure.message : null };
  });

  ipcMain.handle('sky:jobs:start', async (_event, payload) => {
    const result = await request('POST', '/api/jobs', payload);
    const failure = toFailure(result);

    return { ok: failure === null, data: result.data, error: failure ? failure.message : null };
  });

  ipcMain.handle('sky:jobs:cancel', async (_event, id) => {
    const result = await request('DELETE', `/api/jobs/${encodeURIComponent(id)}`);
    const failure = toFailure(result);

    return { ok: failure === null, data: null, error: failure ? failure.message : null };
  });

  ipcMain.handle('sky:jobs:pause', async (_event, id) => {
    const result = await request('POST', `/api/jobs/${encodeURIComponent(id)}/pause`);
    const failure = toFailure(result);

    return { ok: failure === null, data: null, error: failure ? failure.message : null };
  });

  ipcMain.handle('sky:jobs:resume', async (_event, id) => {
    const result = await request('POST', `/api/jobs/${encodeURIComponent(id)}/resume`);
    const failure = toFailure(result);

    return { ok: failure === null, data: null, error: failure ? failure.message : null };
  });

  ipcMain.handle('sky:history', async () => {
    const result = await request('GET', '/api/history');
    const failure = toFailure(result);

    return { ok: failure === null, data: result.data, error: failure ? failure.message : null };
  });

  ipcMain.handle('sky:history:remove', async (_event, id) => {
    const result = await request('DELETE', `/api/history/${encodeURIComponent(id)}`);
    const failure = toFailure(result);

    return { ok: failure === null, data: null, error: failure ? failure.message : null };
  });

  ipcMain.handle('sky:directory:choose', async () => {
    const selection = await dialog.showOpenDialog(mainWindow, {
      title: 'İndirme klasörünü seçin',
      defaultPath: outputDirectory,
      properties: ['openDirectory', 'createDirectory']
    });

    if (selection.canceled || selection.filePaths.length === 0) {
      return { ok: false, data: null };
    }

    outputDirectory = selection.filePaths[0];

    return { ok: true, data: outputDirectory };
  });

  ipcMain.handle('sky:shell:reveal', async (_event, target) => {
    if (typeof target === 'string' && target.length > 0) {
      shell.showItemInFolder(target);
      return { ok: true };
    }

    return { ok: false };
  });

  ipcMain.handle('sky:shell:open', async (_event, target) => {
    let candidate = typeof target === 'string' ? target : '';

    if (candidate.length === 0) {
      candidate = outputDirectory;
    }

    if (candidate.length === 0) {
      return { ok: false };
    }

    const error = await shell.openPath(candidate);

    return { ok: error === '' };
  });

  ipcMain.handle('sky:clipboard:write', async (_event, target) => {
    if (typeof target !== 'string' || target.length === 0) {
      return { ok: false };
    }

    clipboard.writeText(target);
    return { ok: true };
  });

  ipcMain.handle('sky:notify', async (_event, payload) => {
    if (!Notification.isSupported()) {
      return { ok: false };
    }

    const notification = new Notification({
      title: payload && payload.title ? String(payload.title) : 'Sky',
      body: payload && payload.body ? String(payload.body) : ''
    });

    notification.on('click', () => {
      if (mainWindow) {
        if (mainWindow.isMinimized()) {
          mainWindow.restore();
        }

        mainWindow.focus();
      }
    });

    notification.show();
    return { ok: true };
  });
}

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (mainWindow) {
      if (mainWindow.isMinimized()) {
        mainWindow.restore();
      }

      mainWindow.focus();
    }
  });

  app.whenReady().then(() => {
    registerHandlers();
    startBackend();
    createWindow();

    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) {
        createWindow();
      }
    });
  });

  app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') {
      app.quit();
    }
  });

  app.on('before-quit', () => {
    stopBackend();
  });
}