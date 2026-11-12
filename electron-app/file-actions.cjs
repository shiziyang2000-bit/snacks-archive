"use strict";
const path = require("path");
const MEDIA = new Set([".mp4", ".mov", ".mkv", ".mxf", ".m4v", ".avi", ".webm", ".mts", ".m2ts", ".ts", ".mpg", ".mpeg", ".m2v", ".3gp", ".flv", ".wmv", ".mp3", ".m4a", ".flac", ".wav", ".aac", ".ogg", ".opus"]);

function createFileActionHandler({ getWindow, getPort, resolveFile, shell, fs }) {
    return async (event, request) => {
        try {
            const window = getWindow();
            const origin = new URL(event.senderFrame?.url ?? "about:blank");
            if (!window || event.sender !== window.webContents || event.senderFrame !== window.webContents.mainFrame ||
                origin.protocol !== "http:" || !["localhost", "127.0.0.1"].includes(origin.hostname) || Number(origin.port) !== getPort())
                throw new Error("文件操作来源无效。");
            if (!request || !["archive", "work", "job"].includes(request.kind) ||
                typeof request.id !== "string" || !/^[a-zA-Z0-9-]{1,80}$/.test(request.id) ||
                !["play", "reveal", "backup-folder", "source-reveal", "recycle-folder"].includes(request.action) || "path" in request ||
                (request.kind === "job") !== (request.action === "recycle-folder"))
                throw new Error("文件操作参数无效。");
            const target = ["backup-folder", "recycle-folder"].includes(request.action) ? request.action : request.action === "source-reveal" ? "source" : "output";
            const result = await resolveFile(request.kind, request.id, target);
            if (typeof result.path !== "string" || !path.isAbsolute(result.path) || result.path.includes("\0"))
                throw new Error("记录没有有效的本机文件路径。");
            const stat = await fs.promises.lstat(result.path);
            if (stat.isSymbolicLink()) throw new Error("文件路径已变为符号链接，请在 Finder 中核对。");
            if (["backup-folder", "recycle-folder"].includes(request.action)) {
                if (!stat.isDirectory() || result.directory !== true) throw new Error("备份文件夹不存在。");
                const error = await shell.openPath(result.path);
                if (error) throw new Error(error);
            } else {
                if (!stat.isFile()) throw new Error("成片文件不存在。");
                if (request.action === "play") {
                    if (!MEDIA.has(path.extname(result.path).toLowerCase())) throw new Error("此记录不是支持的媒体文件。");
                    const error = await shell.openPath(result.path);
                    if (error) throw new Error(error);
                } else shell.showItemInFolder(result.path);
            }
            return { ok: true };
        } catch (error) {
            return { ok: false, message: error.code === "ENOENT" ? "文件已移动、删除或存储未连接，请刷新历史后重试。" : error.message };
        }
    };
}
module.exports = { createFileActionHandler };
