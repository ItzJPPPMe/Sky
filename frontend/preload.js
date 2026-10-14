'use strict';

const { contextBridge, ipcRenderer } = require('electron');

const api = {
  getConfig: () => ipcRenderer.invoke('sky:config'),
  getPlatforms: () => ipcRenderer.invoke('sky:platforms'),
  getTools: () => ipcRenderer.invoke('sky:tools'),
  resolveLink: (payload) => ipcRenderer.invoke('sky:resolve', payload),
  getJobs: () => ipcRenderer.invoke('sky:jobs'),
  startDownload: (payload) => ipcRenderer.invoke('sky:jobs:start', payload),
  cancelJob: (id) => ipcRenderer.invoke('sky:jobs:cancel', id),
  pauseJob: (id) => ipcRenderer.invoke('sky:jobs:pause', id),
  resumeJob: (id) => ipcRenderer.invoke('sky:jobs:resume', id),
  getHistory: () => ipcRenderer.invoke('sky:history'),
  removeHistoryEntry: (id) => ipcRenderer.invoke('sky:history:remove', id),
  chooseDirectory: () => ipcRenderer.invoke('sky:directory:choose'),
  revealFile: (target) => ipcRenderer.invoke('sky:shell:reveal', target),
  onLog: (callback) => {
    const listener = (_event, payload) => callback(payload);
    ipcRenderer.on('sky:log', listener);

    return () => {
      ipcRenderer.removeListener('sky:log', listener);
    };
  }
};

contextBridge.exposeInMainWorld('sky', Object.freeze(api));