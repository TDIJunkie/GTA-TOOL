'use strict';
const { contextBridge, ipcRenderer, webUtils } = require('electron');

contextBridge.exposeInMainWorld('api', {
  invoke: (channel, args) => ipcRenderer.invoke(channel, args),
  on: (channel, fn) => {
    const allowed = ['progress', 'keys', 'blender', 'tex:updated', 'audio:updated', 'toast', 'app:closing', 'setup', 'update'];
    if (!allowed.includes(channel)) return;
    ipcRenderer.on(channel, (_e, data) => fn(data));
  },
  confirmClose: () => ipcRenderer.invoke('app:close-confirmed'),
  pathForFile: (file) => webUtils.getPathForFile(file),
});
