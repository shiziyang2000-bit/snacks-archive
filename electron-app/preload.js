"use strict";

const { ipcRenderer, contextBridge } = require("electron");

// This value is read before page scripts so the preference survives backend
// port changes and is available to the ordinary browser localization code.
const language = ipcRenderer.sendSync("snacks:get-ui-language");
try { localStorage.setItem("snacks.uiLanguage", language); } catch { /* non-page frame */ }
contextBridge.exposeInMainWorld("snacksDesktop", Object.freeze({ language, selectArchiveFolder: () => ipcRenderer.invoke("snacks:select-archive-folder"), fileAction: request => ipcRenderer.invoke("snacks:file-action", request) }));
window.addEventListener("snacks:languagechange", event => {
    const value = event.detail?.language;
    if (value === "zh-CN" || value === "en") ipcRenderer.send("snacks:set-ui-language", value);
});
