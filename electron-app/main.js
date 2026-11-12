/**
 * Electron main process.
 *
 * Spawns the ASP.NET Core backend (`Snacks.exe`), waits for its health
 * endpoint to come up, and then creates the BrowserWindow that loads it.
 * Handles the full lifecycle:
 *
 *   - Clean restart:       backend exits with code 0 → relaunch and reload.
 *   - Crash during boot:   backend dies before the window exists → show a
 *                          fatal dialog and quit.
 *   - Crash while running: backend dies after the window is up → attempt
 *                          one auto-restart; escalate to a fatal dialog on
 *                          failure.
 *   - App quit:            before-quit hook waits for persistent archive shutdown
 *                          before stopping the backend.
 */

const { app, BrowserWindow, Menu, dialog, shell, session, nativeImage, ipcMain } = require("electron");
const { spawn } = require("child_process");
const path     = require("path");
const net      = require("net");
const http     = require("http");
const fs       = require("fs");
const { randomBytes } = require("crypto");
const desktopShutdownToken = randomBytes(32).toString("hex");

// A separate identity keeps the localized build alongside the official app.
const productName = "Snacks 归档版";
const dataDir = path.join(app.getPath("appData"), "Snacks-Archive");
app.setName(productName);
fs.mkdirSync(dataDir, { recursive: true });
app.setPath("userData", dataDir);
const languageFile = path.join(dataDir, "ui-language.json");
let uiLanguage = "zh-CN";
try {
    const saved = JSON.parse(fs.readFileSync(languageFile, "utf8"));
    if (saved.language === "en") uiLanguage = "en";
} catch { /* A new profile starts in Simplified Chinese. */ }
app.commandLine.appendSwitch("lang", uiLanguage);
const tr = (zh, en) => uiLanguage === "en" ? en : zh;

function setUiLanguage(language, updateRenderer = false) {
    if (language !== "zh-CN" && language !== "en") return;
    uiLanguage = language;
    fs.mkdirSync(dataDir, { recursive: true });
    fs.writeFileSync(languageFile, JSON.stringify({ language }) + "\n", "utf8");
    buildMenu();
    if (updateRenderer && mainWindow && !mainWindow.isDestroyed()) {
        mainWindow.webContents.executeJavaScript(
            `window.SnacksI18n?.setLanguage(${JSON.stringify(language)});`
        ).catch(err => console.error("Language update failed:", err));
    }
}

ipcMain.handle("snacks:select-archive-folder", async event => {
    if (event.sender !== mainWindow?.webContents) throw new Error("Unexpected folder picker sender");
    const result = await dialog.showOpenDialog(mainWindow, {
        properties: ["openDirectory"],
        title: tr("选择要处理的文件夹", "Choose folder to process"),
        buttonLabel: tr("开始处理", "Start processing"),
    });
    return result.canceled ? null : result.filePaths[0] ?? null;
});

const { createFileActionHandler } = require("./file-actions.cjs");
function resolveRegisteredFile(kind, id, target) {
    return new Promise((resolve, reject) => {
        const request = http.get({ hostname: "127.0.0.1", port: backendPort,
            path: `/api/desktop/files/${encodeURIComponent(kind)}/${encodeURIComponent(id)}/${target}`,
            headers: { "X-Snacks-Desktop-Token": desktopShutdownToken } }, response => {
            let body = "";
            response.setEncoding("utf8");
            response.on("data", chunk => { body += chunk; if (body.length > 65536) request.destroy(new Error("File response is too large")); });
            response.on("end", () => {
                try {
                    const result = JSON.parse(body);
                    if (response.statusCode !== 200) throw new Error(result.message || "无法读取文件记录。");
                    resolve(result);
                } catch (error) { reject(error); }
            });
        });
        request.setTimeout(15000, () => request.destroy(new Error("读取文件位置超时，请检查存储连接。")));
        request.on("error", reject);
    });
}
ipcMain.handle("snacks:file-action", createFileActionHandler({
    getWindow: () => mainWindow, getPort: () => backendPort,
    resolveFile: resolveRegisteredFile, shell, fs,
}));

ipcMain.on("snacks:get-ui-language", event => {
    event.returnValue = event.sender === mainWindow?.webContents ? uiLanguage : "zh-CN";
});
ipcMain.on("snacks:set-ui-language", (event, language) => {
    if (event.sender !== mainWindow?.webContents || language === uiLanguage) return;
    setUiLanguage(language);
});


// ---------------------------------------------------------------------------
// Module state
// ---------------------------------------------------------------------------

let mainWindow     = null;
let backendProcess = null;
let backendPort    = null;
let isQuitting     = false;


// ---------------------------------------------------------------------------
// Single-instance lock
// ---------------------------------------------------------------------------
//
// Two Snacks instances sharing the same per-user work directory will race on
// the SQLite DB and on `remote-jobs/<jobId>/` temp files (one instance's
// cleanup deletes the other's in-flight upload). Acquire the lock before any
// other lifecycle wiring so the duplicate process exits before it spawns the
// .NET backend. The .NET backend has its own file-lock fallback for launches
// that bypass Electron (tests, headless dev runs).

if (!app.requestSingleInstanceLock()) {
    app.quit();
    process.exit(0);
}

app.on("second-instance", () => {
    if (!mainWindow) return;
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.focus();
});


// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Resolves to an available TCP port to bind the backend to.
 *
 * Prefers `preferred` when it's free (firewall rules and per-user config
 * stay predictable between launches); falls back to OS-assigned random
 * port when the preferred one is taken.
 *
 * @param {number} [preferred=6769]
 * @returns {Promise<number>}
 */
function findFreePort(preferred = 6769) {
    return new Promise((resolve, reject) => {

        // Try the preferred port first.
        const srv = net.createServer();
        srv.listen(preferred, "127.0.0.1", () => {
            srv.close(() => resolve(preferred));
        });

        srv.on("error", () => {
            // Preferred port is taken — fall back to a random one.
            const srv2 = net.createServer();
            srv2.listen(0, "127.0.0.1", () => {
                const port = srv2.address().port;
                srv2.close(() => resolve(port));
            });
            srv2.on("error", reject);
        });
    });
}

/**
 * Returns the base directory where packaged resources live.
 *
 * In a packaged build this is `<install>/resources`; during development
 * we fall back to the electron-app directory itself so `npm start` works
 * without a prior `electron-builder` pack.
 *
 * @returns {string}
 */
function getResourceBase() {
    if (app.isPackaged) return process.resourcesPath;
    return __dirname;
}

/** Adds the platform-appropriate executable suffix (.exe on Windows, none elsewhere). */
const exeName = (name) => process.platform === "win32" ? `${name}.exe` : name;

/** Path to the bundled backend executable. */
function resolveBackendExe() {
    return path.join(getResourceBase(), "backend", exeName("Snacks"));
}

/** Path to the bundled ffmpeg executable. */
function resolveFfmpegPath() {
    return path.join(getResourceBase(), "ffmpeg", exeName("ffmpeg"));
}

/** Path to the bundled ffprobe executable. */
function resolveFfprobePath() {
    return path.join(getResourceBase(), "ffmpeg", exeName("ffprobe"));
}

/**
 * Returns the per-user working directory the backend should use for its
 * queue DB, temp files, and scan history.
 *
 * Stored below this build's isolated Electron userData directory, never
 * below the official Snacks profile.
 *
 * @returns {string}
 */
function getWorkDir() {
    return path.join(dataDir, "work");
}


// ---------------------------------------------------------------------------
// Backend lifecycle
// ---------------------------------------------------------------------------

/**
 * Spawns the backend exe bound to `port` and wires up its stdout/stderr
 * passthrough plus the exit-handler that distinguishes clean restarts,
 * boot-time crashes, and runtime crashes.
 *
 * @param {number} port
 */
function startBackend(port) {
    const exe = resolveBackendExe();
    console.log(`Starting backend: ${exe} on port ${port}`);

    const workDir = getWorkDir();
    fs.mkdirSync(workDir, { recursive: true });

    const env = Object.assign({}, process.env, {
        ASPNETCORE_URLS:        `http://localhost:${port}`,
        ASPNETCORE_ENVIRONMENT: "Production",
        FFMPEG_PATH:            resolveFfmpegPath(),
        FFPROBE_PATH:           resolveFfprobePath(),
        // Required only for the verified Sony MP4 rtmd preservation path.
        MP4BOX_PATH:            process.env.MP4BOX_PATH || path.join(getResourceBase(), "gpac", exeName("MP4Box")),
        SNACKS_WORK_DIR:        workDir,
        SNACKS_DESKTOP_SHUTDOWN_TOKEN: desktopShutdownToken,
        SNACKS_ALLOW_ALL_PATHS: "true",
    });

    backendProcess = spawn(exe, ["--urls", `http://localhost:${port}`], {
        env,
        cwd:         path.dirname(exe),
        stdio:       ["ignore", "pipe", "pipe"],
        windowsHide: true,
    });

    backendProcess.stdout.on("data", (d) => process.stdout.write(`[backend] ${d}`));
    backendProcess.stderr.on("data", (d) => process.stderr.write(`[backend:err] ${d}`));

    backendProcess.on("exit", (code) => {
        console.log(`Backend exited with code ${code}`);
        backendProcess = null;

        if (isQuitting) return;

        // Backend died before the window was ever created — fatal.
        if (mainWindow === null) {
            dialog.showErrorBox(
                tr("Snacks — 后台启动失败", "Snacks - Backend crashed"),
                tr(`后台在窗口打开前退出（代码 ${code}）。请检查安装包是否完整。\n\nBackend exited with code ${code} before the window could open.`, `Backend exited with code ${code} before the window could open.\n\nCheck that the backend published correctly.`),
            );
            app.quit();
            return;
        }

        // Clean exit (triggered by an in-app restart request) — relaunch.
        if (code === 0) {
            console.log("Backend exited cleanly — restarting...");
            startBackend(backendPort);
            pollHealth(backendPort)
                .then(() => { if (mainWindow) mainWindow.reload(); })
                .catch(() => {
                    dialog.showErrorBox(productName, tr("后台重启失败。\n\nBackend failed to restart.", "Backend failed to restart."));
                    app.quit();
                });
            return;
        }

        // Non-zero exit while the window is open — attempt auto-restart, escalate on failure.
        console.error(`Backend crashed with code ${code} — attempting restart...`);
        startBackend(backendPort);
        pollHealth(backendPort)
            .then(() => { if (mainWindow) mainWindow.reload(); })
            .catch(() => {
                dialog.showErrorBox(
                    tr("Snacks — 后台异常退出", "Snacks - Backend crashed"),
                    tr(`后台异常退出（代码 ${code}），应用即将关闭。\n\nBackend exited with code ${code}.`, `Backend exited with code ${code}.\n\nThe application will now close.`),
                );
                app.quit();
            });
    });
}

/**
 * Polls `/api/health` until it returns 200 or `timeoutMs` elapses.
 *
 * @param {number} port
 * @param {number} [timeoutMs=30000]
 * @param {number} [intervalMs=500]
 * @returns {Promise<void>} Resolves when the backend is healthy;
 *                          rejects when the timeout is exceeded.
 */
function pollHealth(port, timeoutMs = 30000, intervalMs = 500) {
    const url   = `http://localhost:${port}/api/health`;
    const start = Date.now();

    return new Promise((resolve, reject) => {

        const attempt = () => {
            if (Date.now() - start > timeoutMs) {
                return reject(new Error(tr("后台启动超时。\n\nBackend did not become healthy within timeout", "Backend did not become healthy within timeout")));
            }

            http.get(url, (res) => {
                if (res.statusCode === 200) {
                    res.resume();
                    return resolve();
                }
                res.resume();
                setTimeout(attempt, intervalMs);
            }).on("error", () => {
                setTimeout(attempt, intervalMs);
            });
        };

        attempt();
    });
}

/**
 * Tree-kills the backend process (SIGTERM) so nothing is orphaned at quit.
 * Resolves once the kill signal has been dispatched; the actual process
 * exit is observed via the `exit` handler on `backendProcess`.
 *
 * @returns {Promise<void>}
 */
async function safelyStopBackend() {
    const child = backendProcess;
    if (!child || child.exitCode !== null) return;
    await new Promise((resolve, reject) => {
        const request = http.request({
            hostname: "127.0.0.1", port: backendPort, path: "/api/archive/shutdown", method: "POST",
            headers: { "X-Snacks-Desktop-Token": desktopShutdownToken, "Content-Length": "0" },
        }, response => {
            let body = "";
            response.setEncoding("utf8");
            response.on("data", chunk => { body += chunk; });
            response.on("end", () => {
                try {
                    if (response.statusCode !== 200 || JSON.parse(body).safeToQuit !== true)
                        throw new Error(`Safe shutdown returned HTTP ${response.statusCode}`);
                    resolve();
                } catch (error) { reject(error); }
            });
        });
        request.on("error", error => child.exitCode !== null ? resolve() : reject(error));
        request.end();
    });
    if (child.exitCode !== null) return;
    await new Promise(resolve => {
        child.once("exit", resolve);
        child.kill("SIGTERM"); // all archive commits/encodes have drained; host StopAsync also awaits them
    });
}


// ---------------------------------------------------------------------------
// Window / Menu
// ---------------------------------------------------------------------------

/**
 * Provides native editing commands and a language switch; media commands
 * remain in the original web UI.
 */
function buildMenu() {
    const template = [
        {
            label: productName,
            submenu: [
                { role: "about", label: tr("关于 Snacks 归档版", "About Snacks Archive Edition") },
                { type: "separator" },
                { role: "hide", label: tr("隐藏 Snacks 归档版", "Hide Snacks Archive Edition") },
                { role: "hideOthers", label: tr("隐藏其他", "Hide Others") },
                { role: "unhide", label: tr("显示全部", "Show All") },
                { type: "separator" },
                { role: "quit", label: tr("退出 Snacks 归档版", "Quit Snacks Archive Edition") },
            ],
        },
        {
            label: tr("编辑", "Edit"),
            submenu: [
                { role: "undo", label: tr("撤销", "Undo") },
                { role: "redo", label: tr("重做", "Redo") },
                { type: "separator" },
                { role: "cut", label: tr("剪切", "Cut") },
                { role: "copy", label: tr("复制", "Copy") },
                { role: "paste", label: tr("粘贴", "Paste") },
                { role: "selectAll", label: tr("全选", "Select All") },
            ],
        },
        {
            label: tr("显示", "View"),
            submenu: [
                { role: "reload", label: tr("重新载入", "Reload") },
                { role: "resetZoom", label: tr("实际大小", "Actual Size") },
                { role: "zoomIn", label: tr("放大", "Zoom In") },
                { role: "zoomOut", label: tr("缩小", "Zoom Out") },
                { role: "togglefullscreen", label: tr("切换全屏幕", "Toggle Full Screen") },
            ],
        },
        {
            label: tr("语言", "Language"),
            submenu: [
                { label: "简体中文", type: "radio", checked: uiLanguage === "zh-CN", click: () => setUiLanguage("zh-CN", true) },
                { label: "English", type: "radio", checked: uiLanguage === "en", click: () => setUiLanguage("en", true) },
            ],
        },
    ];
    Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}

/**
 * Creates the main BrowserWindow pointed at the running backend.
 *
 * @param {number} port
 */
function createWindow(port) {
    const iconFile = process.platform === "darwin" ? "snacks.icns" : "snacks.ico";
    mainWindow = new BrowserWindow({
        width:           1280,
        height:          900,
        icon:            path.join(__dirname, "icons", iconFile),
        autoHideMenuBar: true,
        webPreferences: {
            nodeIntegration:  false,
            contextIsolation: true,
            preload: path.join(__dirname, "preload.js"),
        },
    });

    mainWindow.loadURL(`http://localhost:${port}`);

    // F12 preserves the original DevTools shortcut.
    mainWindow.webContents.on("before-input-event", (event, input) => {
        if (input.key === "F12") mainWindow.webContents.toggleDevTools();
    });

    // External URLs (target="_blank", window.open, in-page nav, redirects) all
    // open in the system browser — never inside the Electron window.
    const openExternal = (url) => {
        shell.openExternal(url).catch((err) => console.error(`openExternal failed for ${url}:`, err));
    };
    mainWindow.webContents.setWindowOpenHandler(({ url }) => {
        openExternal(url);
        return { action: "deny" };
    });
    const interceptNav = (event, url) => {
        if (url.startsWith(`http://localhost:${port}`)) return;
        event.preventDefault();
        openExternal(url);
    };
    mainWindow.webContents.on("will-navigate", interceptNav);
    mainWindow.webContents.on("will-redirect",  interceptNav);

    mainWindow.on("closed", () => {
        mainWindow = null;
    });
}


// ---------------------------------------------------------------------------
// App lifecycle
// ---------------------------------------------------------------------------

app.on("ready", async () => {
    try {
        // Override the process name + dock icon so dev runs (`npx electron .`) show
        // "Snacks" with our icon instead of the generic Electron defaults — packaged
        // builds get this from the .app's Info.plist, but unpackaged dev does not.
        if (!app.isPackaged) {
            app.setName(productName);
            // Use the PNG, not the .icns — Electron's nativeImage is finicky about
            // some .icns variants and silently produces an empty image. PNG always works.
            if (process.platform === "darwin" && app.dock) {
                const iconPath = path.join(__dirname, "icons", "snacks.png");
                if (fs.existsSync(iconPath)) {
                    app.dock.setIcon(nativeImage.createFromPath(iconPath));
                }
            }
        }

        // Clear cached JS/CSS so the renderer always loads fresh files from
        // the newly-spawned backend. Without this, an older build's assets
        // can linger after an update.
        await session.defaultSession.clearCache();
        await session.defaultSession.clearStorageData({
            storages: ["cachestorage", "serviceworkers"],
        });

        backendPort = await findFreePort();
        startBackend(backendPort);
        await pollHealth(backendPort);
        buildMenu();
        createWindow(backendPort);
    } catch (err) {
        console.error("Failed to start:", err);
        dialog.showErrorBox(tr("Snacks — 启动失败", "Snacks - Failed to start"), tr("应用未能启动，请查看以下错误详情：\n\n", "The application could not start:\n\n") + (err.message || String(err)));
        app.quit();
    }
});

app.on("window-all-closed", () => {
    app.quit();
});

// Intercept the first quit attempt to kill the backend cleanly before the
// app actually exits. Subsequent quit events (after we call app.quit()
// below) are allowed through because `quitHandled` is true.
let quitHandled = false;
let quitInProgress = false;
app.on("before-quit", async event => {
    if (quitHandled || !backendProcess || backendProcess.exitCode !== null) {
        isQuitting = true;
        return;
    }
    event.preventDefault();
    if (quitInProgress) return;
    quitInProgress = true;
    isQuitting = true;
    try {
        await safelyStopBackend();
        quitHandled = true;
        app.quit();
    } catch (error) {
        isQuitting = false;
        quitInProgress = false;
        dialog.showErrorBox(tr("尚未安全退出", "Safe shutdown is incomplete"),
            tr("无法确认文件事务已安全收尾，软件保持打开。请解决连接或存储问题后再次退出。\n\n", "The application remains open because safe completion of file transactions could not be confirmed. Resolve the connection or storage issue, then quit again.\n\n") + error.message);
    }
});
