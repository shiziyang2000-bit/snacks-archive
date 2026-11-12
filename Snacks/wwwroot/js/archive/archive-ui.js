/**
 * Archive settings, explicit previews, durable task history and restore UI.
 *
 * The archive API owns decisions and filesystem operations. This module only
 * renders server responses and submits an explicit user selection.
 */

import { archiveApi } from '../api.js';
import { pickFolder } from '../settings/folder-picker.js';
import { escapeHtml } from '../utils/dom.js';
import { archiveFileButtons, performFileAction } from '../utils/file-actions.js';
import { closeModal, openModal, showConfirmModal } from '../utils/modal-controller.js';


const PREVIEW_MODAL = 'archivePreviewModal';
const RESTORE_MODAL = 'archiveRestoreModal';
const HISTORY_PAGE_SIZE = 50;

const el = id => document.getElementById(id);
const t = (source, ...args) => window.SnacksI18n?.t?.(source, ...args) ?? source;
const escaped = (source, ...args) => escapeHtml(t(source, ...args));
const backendText = source => source == null ? '' : t(String(source));

export function outputChoiceSummary(choice = {}) {
    if (choice.outputMode === 'saveAs') return t('Keep originals; save compressed output to another folder.');
    return choice.backupEnabled
        ? t('Replace original files; keep an extra full-size original backup.')
        : t('Replace original files; keep only compressed output after success.');
}

/** Reads the archive form into the API's camelCase settings shape. */
export function readArchiveSettingsFromForm(base = {}) {
    const targetMbps = Number(el('archiveTargetMbps')?.value);
    const reminderDays = Number.parseInt(el('archiveBackupReminderDays')?.value, 10);
    const cacheLimitGb = Number(el('archiveCacheLimitGb')?.value);
    const concurrency = Number.parseInt(el('archiveConcurrencyNumber')?.value, 10);
    if (!Number.isFinite(targetMbps) || targetMbps <= 0)
        throw new Error(t('Target bitrate must be a positive number.'));
    if (el('archiveMode')?.value === 'custom' && el('archiveOutputMode')?.value === 'saveAs' && !el('archiveOutputDirectory')?.value.trim())
        throw new Error(t('Choose an output folder before saving.'));
    if (!Number.isInteger(reminderDays) || reminderDays < 1)
        throw new Error(t('Backup reminder days must be at least 1.'));
    if (!Number.isFinite(cacheLimitGb) || cacheLimitGb <= 0)
        throw new Error(t('Cache capacity must be a positive number.'));
    if (!Number.isInteger(concurrency) || concurrency < 1 || concurrency > 20)
        throw new Error(t('Concurrent transcodes must be between 1 and 20.'));

    return {
        ...base,
        mode: el('archiveMode')?.value,
        targetMbps,
        outputMode: el('archiveOutputMode')?.value,
        outputDirectory: el('archiveOutputDirectory')?.value.trim() || null,
        backupEnabled: !!el('archiveBackupEnabled')?.checked,
        backupLocation: el('archiveBackupLocation')?.value,
        backupDirectory: el('archiveBackupDirectory')?.value.trim() || null,
        backupReminderDays: reminderDays,
        cacheDirectory: el('archiveCacheDirectory')?.value.trim() || null,
        cacheLimitGb,
        concurrency,
    };
}

function errorText(error) {
    const message = String(error?.message ?? error ?? '');
    const responsePrefix = message.match(/^(.* → \d+: )(.*)$/s);
    return responsePrefix
        ? `${responsePrefix[1]}${backendText(responsePrefix[2])}`
        : backendText(message);
}

function showToastSafe(message, type = 'info') {
    if (typeof window.showToast === 'function') window.showToast(message, type);
}

function requestId() {
    return globalThis.crypto?.randomUUID?.() ?? `archive-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

function formatBytes(value) {
    const bytes = Number(value);
    if (!Number.isFinite(bytes) || bytes < 0) return '—';
    if (bytes < 1000) return `${Math.round(bytes)} B`;
    const units = ['kB', 'MB', 'GB', 'TB'];
    let size = bytes / 1000;
    let unit = 0;
    while (size >= 1000 && unit < units.length - 1) { size /= 1000; unit++; }
    return `${size.toFixed(size >= 100 ? 0 : size >= 10 ? 1 : 2)} ${units[unit]}`;
}

function formatRate(value) {
    const rate = Number(value);
    return value != null && Number.isFinite(rate) ? `${rate.toFixed(2)} Mbps` : '—';
}

function formatScore(value) {
    const score = Number(value);
    return value != null && Number.isFinite(score) ? `${score.toFixed(2)}` : '—';
}

function statusLabel(status) {
    const labels = {
        completed: 'Completed', failed: 'Failed', restored: 'Restored original',
        skipped: 'Skipped', paused_storage: 'Paused by storage issue',
        interrupted: 'Paused', queued: 'Queued', running: 'Running', preparing: 'Starting folder…', waiting: 'Waiting', waiting_retry: 'Waiting',
        encoding: 'Encoding', muxing: 'Preserving tracks', validating: 'Legacy checking stage', backing_up: 'Backing up',
        committing: 'Committing', restoring: 'Restoring', rescoring: 'Scoring samples',
    };
    return labels[String(status ?? '').toLowerCase()] ? t(labels[String(status).toLowerCase()]) : String(status ?? '—');
}

function entryProgressLabel(entry) {
    const stage = String(entry.stage ?? entry.status ?? '').toLowerCase();
    return ['waiting', 'waiting_retry', 'muxing', 'backing_up', 'committing', 'restoring', 'interrupted', 'paused_storage'].includes(stage)
        ? statusLabel(stage) : `${Math.max(0, Math.min(100, Number(entry.progress) || 0))}%`;
}

function folderRelativePath(path, sourceRoot) {
    const value = String(path ?? '');
    if (!sourceRoot) return value;
    const prefix = `${String(sourceRoot).replace(/[\\/]+$/, '')}/`.replace(/\\/g, '/');
    return value.replace(/\\/g, '/').toLowerCase().startsWith(prefix.toLowerCase()) ? value.slice(prefix.length) : value;
}


export class ArchiveUI {

    constructor() {
        this._settings = null;
        this._settingsLoad = null;
        this._preview = null;
        this._previewRequest = null;
        this._previewTarget = '';
        this._previewHeadingKey = 'Archive preview';
        this._selectedPreviewIds = new Set();
        this._restorePreview = null;
        this._historySkip = 0;
        this._historyTotal = 0;
        this._historyItems = [];
        this._selectedHistory = new Map();
        this._currentJobId = null;
        this._jobLoadGeneration = 0;
        this._taskRequestId = null;
        this._restoreRequestId = null;
        this._currentJob = null;
        this._folderStatusJobs = [];
        this._folderStatusLoad = null;
        this._folderStatusTimer = null;
        this._settingsStatusKey = null;
        this._settingsStatusError = null;
        this._settingsError = null;
    }

    init() {
        el('archiveStartFolder')?.addEventListener('click', () => this.chooseFolder());
        el('archiveFolderStatus')?.addEventListener('click', e => {
            const button = e.target.closest('[data-folder-progress]');
            if (button) this.openJob(button.dataset.folderProgress);
        });
        el('archiveSettingsSave')?.addEventListener('click', () => this.saveSettings());
        el('archiveCacheUseDefault')?.addEventListener('click', () => {
            if (el('archiveCacheDirectory')) el('archiveCacheDirectory').value = '';
        });
        el('archiveCacheClear')?.addEventListener('click', () => this.clearCache());
        el('archiveMode')?.addEventListener('change', () => this._syncSettingsVisibility());
        el('archiveEditOutput')?.addEventListener('click', () => { document.querySelector('[data-bs-target="#archiveTab"]')?.click(); this.showSettingsPanel(); el('archiveOutputMode')?.focus(); });
        el('archiveBackupEnabled')?.addEventListener('change', () => this._syncSettingsVisibility());
        el('archiveBackupLocation')?.addEventListener('change', () => this._syncSettingsVisibility());
        el('archiveOutputMode')?.addEventListener('change', () => this._syncSettingsVisibility());
        el('archiveBackupDirectory')?.addEventListener('input', () => this._syncSettingsVisibility());
        el('archiveConcurrency')?.addEventListener('input', e => {
            if (el('archiveConcurrencyNumber')) el('archiveConcurrencyNumber').value = e.target.value;
        });
        el('archiveConcurrencyNumber')?.addEventListener('input', e => {
            if (el('archiveConcurrency')) el('archiveConcurrency').value = e.target.value;
        });

        el('archiveShowSettings')?.addEventListener('click', () => this.showSettingsPanel());
        el('archiveShowHistory')?.addEventListener('click', () => { this._historyJobId = null; this.showHistoryPanel(); });
        el('archiveHistoryRefresh')?.addEventListener('click', () => this.loadHistory({ reset: true }));
        el('archiveHistoryStatus')?.addEventListener('change', () => {
            this._selectedHistory.clear();
            this._historySkip = 0;
            this.loadHistory();
        });
        el('archiveHistoryPrevious')?.addEventListener('click', () => {
            this._historySkip = Math.max(0, this._historySkip - HISTORY_PAGE_SIZE);
            this.loadHistory();
        });
        el('archiveHistoryNext')?.addEventListener('click', () => {
            this._historySkip += HISTORY_PAGE_SIZE;
            this.loadHistory();
        });
        el('archiveHistorySelectPage')?.addEventListener('change', e => {
            for (const item of this._historyItems) {
                if (e.target.checked) this._selectedHistory.set(item.id, item);
                else this._selectedHistory.delete(item.id);
            }
            this._renderHistorySelection();
        });
        el('archiveHistoryRows')?.addEventListener('change', e => {
            const checkbox = e.target.closest('[data-history-select]');
            if (!checkbox) return;
            const item = this._historyItems.find(row => row.id === checkbox.value);
            if (checkbox.checked && item) this._selectedHistory.set(item.id, item);
            else this._selectedHistory.delete(checkbox.value);
            this._renderHistorySelection();
        });
        el('archiveHistoryRows')?.addEventListener('click', e => {
            const file = e.target.closest('[data-file-action]');
            if (file) { performFileAction('archive', file.dataset.entryId, file.dataset.fileAction); return; }
            const directoryJob = e.target.closest('[data-directory-job]');
            if (directoryJob) { this.openJob(directoryJob.dataset.directoryJob); return; }
            const button = e.target.closest('[data-history-action]');
            if (!button) return;
            const item = this._historyItems.find(row => row.id === button.dataset.entryId);
            if (!item) return;
            if (button.dataset.historyAction === 'details' && item.jobId) this.openJob(item.jobId);
            if (button.dataset.historyAction === 'restore') this.previewRestore([item.id]);
        });
        el('archiveRetrySelected')?.addEventListener('click', () => this.previewSelected('retryFailed'));
        el('archiveRearchiveSelected')?.addEventListener('click', () => this.previewSelected('rearchive'));
        el('archiveRescoreSelected')?.addEventListener('click', () => this.rescoreSelected());
        el('archiveRestoreSelected')?.addEventListener('click', () => this.previewRestore([...this._selectedHistory.keys()]));

        el('archivePreviewRows')?.addEventListener('change', e => {
            const checkbox = e.target.closest('[data-preview-select]');
            if (!checkbox) return;
            if (checkbox.checked) this._selectedPreviewIds.add(checkbox.value);
            else this._selectedPreviewIds.delete(checkbox.value);
            this._updatePreviewActions();
        });
        el('archivePreviewSelectReady')?.addEventListener('click', () => {
            for (const row of this._preview?.rows ?? [])
                if (row.action === 'archive') this._selectedPreviewIds.add(row.id);
            this._renderPreviewRows();
            this._updatePreviewActions();
        });
        el('archivePreviewSelectPage')?.addEventListener('change', e => {
            for (const row of this._preview?.rows ?? []) {
                if (row.action !== 'archive') continue;
                if (e.target.checked) this._selectedPreviewIds.add(row.id);
                else this._selectedPreviewIds.delete(row.id);
            }
            this._renderPreviewRows();
            this._updatePreviewActions();
        });
        el('archiveStartTask')?.addEventListener('click', () => this.startTask());
        el('archiveOpenHistory')?.addEventListener('click', () => this.openHistory());
        el('archiveJobDetail')?.addEventListener('click', e => {
            const file = e.target.closest('[data-file-action]');
            if (file) { performFileAction('archive', file.dataset.entryId, file.dataset.fileAction); return; }
            if (e.target.closest('#archiveJobApplyOutput')) this.applyPendingOutput();
            if (e.target.closest('#archiveJobRecycle')) performFileAction('job', this._currentJobId, 'recycle-folder');
            if (e.target.closest('#archiveJobBackups')) this.openHistory({ jobId: this._currentJobId, status: 'completed' });
            if (e.target.closest('#archiveJobContinue')) this.continueJob();
            if (e.target.closest('#archiveJobPause')) this.pauseJob();
        });
        el('archiveRestoreConfirm')?.addEventListener('click', () => this.confirmRestore());
        document.addEventListener('snacks:languagechange', () => this._refreshLocalizedViews());
        document.addEventListener('snacks:archive-preset-applied', e => {
            if (e.detail?.archive) this._applySettings(e.detail.archive);
            this.loadCache().catch(() => {});
        });

        this._syncSettingsVisibility();
        this.loadSettings().catch(() => {});
        this.loadFolderStatus();
    }

    async loadFolderStatus() {
        if (!el('archiveFolderStatus')) return;
        if (this._folderStatusLoad) return this._folderStatusLoad;
        this._folderStatusLoad = (async () => {
            try {
                const result = await archiveApi.getFolderStatus();
                this._folderStatusJobs = result.jobs ?? [];
                this._renderFolderStatus();
            } catch { /* Keep the last known state; connection status is shown by the shell. */ }
            finally { this._folderStatusLoad = null; }
        })();
        return this._folderStatusLoad;
    }

    _renderFolderStatus() {
        const banner = el('archiveFolderStatus');
        if (!banner) return;
        const jobs = this._settings?.mode === 'original' ? [] : this._folderStatusJobs;
        banner.style.display = jobs.length ? '' : 'none';
        banner.innerHTML = jobs.map(job => {
            const paused = !['preparing', 'queued', 'running'].includes(job.status);
            const status = job.pausedByUser ? t('Paused') : statusLabel(job.status);
            return `<div class="alert ${paused ? 'alert-warning' : 'alert-info'} py-2 mb-2">
                <div class="d-flex align-items-center justify-content-between gap-2 flex-wrap">
                    <div class="small"><strong>${escaped('Folder task')}: ${escapeHtml(status)}</strong> · <span data-user-content>${escapeHtml(job.sourceRoot)}</span></div>
                    <button class="btn btn-sm btn-outline-secondary" data-folder-progress="${escapeHtml(job.jobId)}">${escaped('View folder progress')}</button>
                </div>
                ${job.error ? `<div class="small mt-1 text-break" data-user-content>${escapeHtml(backendText(job.error))}</div>` : ''}
            </div>`;
        }).join('');
    }

    async getMode() {
        if (!this._settings) await this.loadSettings();
        const mode = this._settings?.mode;
        if (mode !== 'custom' && mode !== 'original') throw new Error(t('Archive processing mode could not be loaded. Save or reload archive settings before continuing.'));
        return mode;
    }

    async loadSettings() {
        if (this._settingsLoad) return this._settingsLoad;
        this._settingsLoad = (async () => {
            try {
                const settings = await archiveApi.getSettings();
                if (!settings || !['custom', 'original'].includes(settings.mode))
                    throw new Error(t('Archive settings returned an unknown processing mode.'));
                this._applySettings(settings);
                this._settingsError = null;
                this._hideUnavailable();
                this.loadCache().catch(() => {});
                document.dispatchEvent(new CustomEvent('snacks:archive-settings-restored', { detail: { settings } }));
                return settings;
            } catch (error) {
                this._settings = null;
                this._showUnavailable(error);
                throw error;
            } finally {
                this._settingsLoad = null;
            }
        })();
        return this._settingsLoad;
    }

    _applySettings(settings) {
        this._settings = { ...settings };
        this._renderFolderStatus();
        const values = {
            archiveMode: settings.mode,
            archiveTargetMbps: settings.targetMbps,
            archiveOutputMode: settings.outputMode,
            archiveOutputDirectory: settings.outputDirectory ?? '',
            archiveBackupEnabled: !!settings.backupEnabled,
            archiveBackupLocation: settings.backupLocation,
            archiveBackupDirectory: settings.backupDirectory ?? '',
            archiveBackupReminderDays: settings.backupReminderDays,
            archiveCacheDirectory: settings.cacheDirectory ?? '',
            archiveCacheLimitGb: settings.cacheLimitGb,
            archiveConcurrency: settings.concurrency,
            archiveConcurrencyNumber: settings.concurrency,
        };
        for (const [id, value] of Object.entries(values)) {
            const input = el(id);
            if (!input) continue;
            if (input.type === 'checkbox') input.checked = !!value;
            else input.value = value;
        }
        this._syncSettingsVisibility();
        window.dispatchEvent(new CustomEvent('snacks:archive-mode-changed', { detail: { mode: settings.mode } }));
    }

    _readSettingsForm() {
        return readArchiveSettingsFromForm(this._settings ?? {});
    }

    async saveSettings() {
        const button = el('archiveSettingsSave');
        const status = el('archiveSettingsStatus');
        if (button) button.disabled = true;
        if (status) status.textContent = '';
        this._settingsStatusKey = null;
        this._settingsStatusError = null;
        try {
            const settings = this._readSettingsForm();
            const saved = await archiveApi.saveSettings(settings);
            this._applySettings(saved);
            this._settingsStatusKey = 'Archive settings saved.';
            if (status) status.textContent = t(this._settingsStatusKey);
            showToastSafe(t('Archive settings saved.'), 'success');
            document.dispatchEvent(new CustomEvent('snacks:archive-settings-saved', { detail: { settings: saved } }));
            await this.loadCache();
        } catch (error) {
            this._settingsStatusKey = null;
            this._settingsStatusError = error;
            if (status) status.textContent = errorText(error);
            showToastSafe(errorText(error), 'danger');
        } finally {
            if (button) button.disabled = false;
        }
    }

    _syncSettingsVisibility() {
        const custom = el('archiveMode')?.value === 'custom';
        for (const group of document.querySelectorAll('[data-original-mode-controls]')) {
            group.hidden = custom; group.style.display = custom ? 'none' : ''; group.disabled = custom;
        }
        if (el('archiveGeneralOutput')) el('archiveGeneralOutput').hidden = !custom;
        if (el('archiveGeneralOutputSummary')) el('archiveGeneralOutputSummary').textContent = outputChoiceSummary(this._settings ?? {});
        const backupEnabled = !!el('archiveBackupEnabled')?.checked;
        const selectedDirectory = el('archiveBackupLocation')?.value === 'selectedDirectory';
        const inPlace = el('archiveOutputMode')?.value === 'inPlace';
        const showBackup = backupEnabled && inPlace;
        if (el('archiveBackupOptions')) el('archiveBackupOptions').style.display = showBackup ? '' : 'none';
        if (el('archiveBackupDirectoryGroup'))
            el('archiveBackupDirectoryGroup').style.display = showBackup && selectedDirectory ? '' : 'none';
        if (el('archiveOutputDirectoryGroup'))
            el('archiveOutputDirectoryGroup').style.display = inPlace ? 'none' : '';
        if (el('archiveBackupPathRequired'))
            el('archiveBackupPathRequired').style.display = showBackup && selectedDirectory && !el('archiveBackupDirectory')?.value.trim() ? '' : 'none';
    }

    _showUnavailable(error) {
        this._settingsError = error;
        const banner = el('archiveUiUnavailable');
        if (!banner) return;
        banner.textContent = `${t('Archive settings are unavailable. Processing is paused until the mode can be verified.')} ${errorText(error)}`;
        banner.style.display = '';
    }

    _hideUnavailable() {
        this._settingsError = null;
        const banner = el('archiveUiUnavailable');
        if (banner) { banner.textContent = ''; banner.style.display = 'none'; }
    }

    async loadCache() {
        const usage = el('archiveCacheUsage');
        if (usage) usage.textContent = t('Loading cache usage…');
        try {
            const data = await archiveApi.getCache();
            const info = typeof data === 'string' ? { directory: data } : (data ?? {});
            const hasUsage = ['usedBytes', 'bytesUsed', 'sizeBytes', 'limitBytes', 'capacityBytes', 'fileCount']
                .some(key => info[key] != null);
            const bytes = info.usedBytes ?? info.bytesUsed ?? info.sizeBytes ?? 0;
            const limit = info.limitBytes ?? info.capacityBytes;
            const files = info.fileCount ?? null;
            const releasable = info.releasableBytes;
            const usedText = formatBytes(bytes);
            const limitText = limit == null ? `${Number(this._settings?.cacheLimitGb ?? 5)} GB` : formatBytes(limit);
            if (usage) usage.textContent = hasUsage
                ? `${files == null
                    ? t('Using {0} of {1}', usedText, limitText)
                    : t('Using {0} of {1} across {2} cache files', usedText, limitText, files)}${releasable == null ? '' : ` · ${t('Releasable: {0}', formatBytes(releasable))}`}`
                : t('Cache usage details are not available from the server.');
            const directory = info.directory ?? info.cacheDirectory ?? info.resolvedPath ?? info.path ?? '';
            if (el('archiveCacheEffectiveDirectory')) el('archiveCacheEffectiveDirectory').textContent = directory;
        } catch (error) {
            if (usage) usage.textContent = `${t('Cache usage could not be loaded.')} ${errorText(error)}`;
            throw error;
        }
    }

    async clearCache() {
        const confirmed = await showConfirmModal(
            t('Clear unused sample cache'),
            `<p>${escaped('Clear unused sample cache?')}</p><p class="small text-muted mb-0">${escaped('Task history and completed files are kept.')}</p>`,
            t('Clear cache'),
        );
        if (!confirmed) return;
        try {
            const result = await archiveApi.clearCache() ?? {};
            showToastSafe(t('Unused sample cache cleared ({0}).', formatBytes(result.freedBytes ?? result.deletedBytes ?? result.clearedBytes ?? 0)), 'success');
            await this.loadCache();
        } catch (error) {
            showToastSafe(errorText(error), 'danger');
            if (el('archiveCacheUsage')) el('archiveCacheUsage').textContent = `${t('Cache cleanup failed.')} ${errorText(error)}`;
        }
    }

    showSettingsPanel() {
        if (el('archiveSettingsPanel')) el('archiveSettingsPanel').style.display = '';
        if (el('archiveHistoryPanel')) el('archiveHistoryPanel').style.display = 'none';
    }

    showHistoryPanel() {
        if (el('archiveSettingsPanel')) el('archiveSettingsPanel').style.display = 'none';
        if (el('archiveHistoryPanel')) el('archiveHistoryPanel').style.display = '';
        this.loadHistory({ reset: true });
    }

    async chooseFolder() {
        try {
            if (window.snacksDesktop?.selectArchiveFolder) {
                const path = await window.snacksDesktop.selectArchiveFolder();
                if (path) await this.startFolder(path);
            } else pickFolder(path => this.startFolder(path), '', 'Start processing');
        } catch (error) { showToastSafe(errorText(error), 'danger'); }
    }

    async startFolder(directoryPath) {
        if (!directoryPath) return;
        this._preview = null;
        this._currentJob = null;
        this._currentJobId = null;
        this._setPreviewView('job');
        this._setPreviewHeading('Processing progress');
        this._setPreviewTarget(directoryPath);
        if (el('archiveJobDetail')) el('archiveJobDetail').innerHTML = `<div class="py-4"><span class="spinner-border spinner-border-sm me-2"></span>${escaped('Starting folder…')}</div>`;
        if (el('archiveOpenHistory')) el('archiveOpenHistory').style.display = 'none';
        openModal(PREVIEW_MODAL);
        try {
            const result = await archiveApi.startFolder(directoryPath);
            this._currentJobId = result.jobId;
            await this.loadJob(result.jobId, { open: false });
        } catch (error) {
            if (el('archiveJobDetail')) el('archiveJobDetail').innerHTML = `<div class="alert alert-warning" data-user-content>${escapeHtml(errorText(error))}</div>`;
        }
    }

    async openPreview(request, label = '') {
        if (request.directoryPath) return this.startFolder(request.directoryPath);
        let mode;
        try { mode = await this.getMode(); }
        catch (error) { showToastSafe(errorText(error), 'danger'); return false; }
        if (mode !== 'custom') {
            showToastSafe(t('Save Custom archive rules before starting an archive preview.'), 'warning');
            return false;
        }
        this._previewRequest = { ...request };
        this._preview = null;
        this._taskRequestId = requestId();
        this._selectedPreviewIds.clear();
        this._currentJobId = null;
        this._setPreviewView('loading');
        this._setPreviewHeading('Archive preview');
        this._setPreviewTarget(label);
        if (el('archiveStartTask')) el('archiveStartTask').disabled = true;
        if (el('archiveOpenHistory')) el('archiveOpenHistory').style.display = 'none';
        openModal(PREVIEW_MODAL);

        try {
            const preview = await archiveApi.preview(request);
            this._preview = preview;
            if (el('archivePreviewError')) el('archivePreviewError').textContent = '';
            this._renderPreview(preview);
            this._setPreviewView('preview');
            return true;
        } catch (error) {
            this._setPreviewView('error');
            if (el('archivePreviewError')) {
                el('archivePreviewError').textContent = errorText(error);
                el('archivePreviewError').style.display = '';
            }
            if (el('archiveStartTask')) el('archiveStartTask').disabled = true;
            return false;
        }
    }

    async onConfirmationRequired(preview) {
        let mode;
        try { mode = await this.getMode(); }
        catch (error) { showToastSafe(errorText(error), 'danger'); return false; }
        if (mode !== 'custom' || !preview?.previewId || !Array.isArray(preview.rows)) return false;
        this._previewRequest = null;
        this._preview = preview;
        this._taskRequestId = requestId();
        this._selectedPreviewIds.clear();
        this._currentJobId = null;
        const firstPath = preview.rows.length === 1 ? preview.rows[0].path : '';
        this._setPreviewHeading('Archive confirmation required');
        this._setPreviewTarget(firstPath || { source: '{0} file(s) need your confirmation.', args: [preview.rows.length] });
        if (el('archiveOpenHistory')) el('archiveOpenHistory').style.display = 'none';
        this._renderPreview(preview);
        this._setPreviewView('preview');
        openModal(PREVIEW_MODAL);
        return true;
    }

    _setPreviewView(view) {
        const loading = el('archivePreviewLoading');
        const summary = el('archivePreviewSummary');
        const tools = el('archivePreviewTools');
        const table = el('archivePreviewTableWrap');
        const started = el('archiveStartedJob');
        const error = el('archivePreviewError');
        const requirements = el('archivePreviewRequirements');
        if (loading) loading.style.display = view === 'loading' ? '' : 'none';
        if (summary) summary.style.display = view === 'preview' ? '' : 'none';
        // Bootstrap d-flex uses !important; ordinary display:none leaves preview tools visible.
        if (tools) tools.style.setProperty('display', view === 'preview' ? 'flex' : 'none', 'important');
        if (el('archivePreviewRowCount')) el('archivePreviewRowCount').style.display = view === 'preview' ? '' : 'none';
        if (table) table.style.display = view === 'preview' ? '' : 'none';
        if (started) started.style.display = view === 'job' ? '' : 'none';
        if (error) error.style.display = view === 'error' ? '' : 'none';
        if (requirements && view !== 'preview') requirements.style.display = 'none';
        if (el('archiveStartTask')) el('archiveStartTask').style.display = view === 'preview' ? '' : 'none';
    }

    _setPreviewTarget(target = this._previewTarget) {
        this._previewTarget = target;
        const text = target && typeof target === 'object' && target.source
            ? t(target.source, ...(target.args ?? []))
            : String(target ?? '');
        if (el('archivePreviewTarget')) el('archivePreviewTarget').textContent = text;
    }

    _setPreviewHeading(key = this._previewHeadingKey) {
        this._previewHeadingKey = key;
        if (el('archivePreviewHeading')) el('archivePreviewHeading').textContent = t(key);
    }

    _renderPreview(preview) {
        const summary = preview.summary ?? {};
        const cards = [
            ['Files', summary.total ?? preview.rows?.length ?? 0],
            ['Ready', summary.ready ?? 0],
            ['Skipped', summary.skipped ?? 0],
            ['Blocked', summary.blocked ?? 0],
        ];
        if (el('archivePreviewSummary')) el('archivePreviewSummary').innerHTML = cards.map(([label, value]) => `
            <div class="col-6 col-md-3"><div class="card h-100"><div class="card-body p-2">
                <div class="small text-muted text-uppercase">${escaped(label)}</div><div class="h4 mb-0">${Number(value) || 0}</div>
            </div></div></div>`).join('');
        if (el('archivePreviewRowCount'))
            el('archivePreviewRowCount').textContent = t('{0} rows. Select the files to start.', preview.rows?.length ?? 0);
        const directory = preview.scope === 'directory';
        if (el('archivePreviewSelectReady')) el('archivePreviewSelectReady').style.display = directory ? 'none' : '';
        this._renderPreviewRows();
        this._renderPreviewRequirements();
        this._updatePreviewActions();
    }

    _renderPreviewRows() {
        const rows = this._preview?.rows ?? [];
        const body = el('archivePreviewRows');
        if (!body) return;
        body.innerHTML = rows.map(row => {
            const ready = row.action === 'archive';
            const decision = row.action === 'archive' ? t('Ready') : row.action === 'blocked' ? t('Blocked') : t('Skipped');
            return `<tr>
                <td><input type="checkbox" data-preview-select value="${escapeHtml(row.id)}" aria-label="${escaped('Select file')}" ${ready && this._preview?.scope !== 'directory' ? '' : 'disabled'} ${this._selectedPreviewIds.has(row.id) ? 'checked' : ''}></td>
                <td class="dash-file" data-user-content title="${escapeHtml(row.path)}"><div class="dash-file-inner"><span class="dash-fileicon"><i class="fas fa-file-video"></i></span><div class="dash-filename">${escapeHtml(row.path)}</div></div></td>
                <td class="small" data-user-content>${escapeHtml(row.codec ?? '—')}<br>${escapeHtml(formatRate(row.sourceMbps))}<br>${escapeHtml(formatBytes(row.sourceBytes))}</td>
                <td class="small" data-user-content>${escapeHtml(formatRate(row.targetMbps))}<br>${escapeHtml(row.encoderMode ?? '—')}</td>
                <td><span class="badge ${ready ? 'bg-success' : row.action === 'blocked' ? 'bg-danger' : 'bg-secondary'}">${escapeHtml(decision)}</span><div class="small text-muted mt-1" data-user-content>${escapeHtml(backendText(row.reason ?? row.reasonCode ?? ''))}</div></td>
                <td class="small text-break" data-user-content>${escapeHtml(row.backupPath ?? '—')}</td>
                <td class="small text-break" data-user-content>${escapeHtml(row.outputPath ?? '—')}</td>
            </tr>`;
        }).join('') || `<tr><td colspan="7" class="text-muted text-center py-4">${escaped('No files to preview.')}</td></tr>`;
        const pageToggle = el('archivePreviewSelectPage');
        if (pageToggle) {
            const readyRows = rows.filter(row => row.action === 'archive');
            pageToggle.disabled = this._preview?.scope === 'directory' || readyRows.length === 0;
            pageToggle.checked = readyRows.length > 0 && readyRows.every(row => this._selectedPreviewIds.has(row.id));
            pageToggle.indeterminate = readyRows.some(row => this._selectedPreviewIds.has(row.id)) && !pageToggle.checked;
        }
    }

    _renderPreviewRequirements() {
        const container = el('archivePreviewRequirements');
        if (!container || !this._preview) return;
        const wasNoBackupChecked = !!el('archiveConfirmNoBackup')?.checked;
        const wasCurrentOutputChecked = !!el('archiveConfirmUseCurrentOutput')?.checked;
        const required = new Set(this._preview.requiredConfirmations ?? []);
        const selectedRows = (this._preview.rows ?? []).filter(row => this._selectedPreviewIds.has(row.id) && row.action === 'archive');
        const hasCurrentOutput = selectedRows.some(row => row.requiresCurrentOutputConfirmation
            || String(row.reasonCode ?? '').includes('current_output'))
            || (required.has('useCurrentOutput') && selectedRows.length > 0);
        const prompts = [];
        if (required.has('noBackup')) prompts.push(`
            <div class="form-check"><input class="form-check-input" type="checkbox" id="archiveConfirmNoBackup">
                <label class="form-check-label" for="archiveConfirmNoBackup">${escaped('I confirm this task will not keep an original backup.')}</label></div>`);
        if (hasCurrentOutput) prompts.push(`
            <div class="form-check"><input class="form-check-input" type="checkbox" id="archiveConfirmUseCurrentOutput">
                <label class="form-check-label" for="archiveConfirmUseCurrentOutput">${escaped('I confirm this task will process the current compressed output.')}</label></div>`);
        if (!prompts.length) {
            container.innerHTML = '';
            container.style.display = 'none';
            return;
        }
        container.innerHTML = `<div class="fw-semibold mb-1">${escaped('Separate confirmation required for this task')}</div>${prompts.join('')}`;
        container.style.display = '';
        if (el('archiveConfirmNoBackup')) el('archiveConfirmNoBackup').checked = wasNoBackupChecked;
        if (el('archiveConfirmUseCurrentOutput')) el('archiveConfirmUseCurrentOutput').checked = wasCurrentOutputChecked;
        container.querySelectorAll('input').forEach(input => input.addEventListener('change', () => this._updatePreviewActions()));
    }

    _updatePreviewActions() {
        if (!this._preview) return;
        this._renderPreviewRequirements();
        const selected = (this._preview.rows ?? []).filter(row => this._selectedPreviewIds.has(row.id) && row.action === 'archive');
        const needNoBackup = (this._preview.requiredConfirmations ?? []).includes('noBackup');
        const needCurrentOutput = !!el('archiveConfirmUseCurrentOutput');
        const confirmed = (!needNoBackup || !!el('archiveConfirmNoBackup')?.checked)
            && (!needCurrentOutput || !!el('archiveConfirmUseCurrentOutput')?.checked);
        const button = el('archiveStartTask');
        const directory = this._preview.scope === 'directory';
        if (button) {
            button.disabled = (!directory && selected.length === 0) || !confirmed;
            button.textContent = directory ? t('Start entire folder (automatic batches)') : t('Start archive task');
        }
        const label = el('archivePreviewRowCount');
        if (label) label.textContent = directory
            ? t(this._preview.recursive === false ? 'Current folder only' : 'Includes subfolders') + ' · ' + t('Preview shows only the first {0} items. Start processes the entire root folder automatically, up to {1} files per batch; discovery continues afterwards.', (this._preview.rows ?? []).length, this._preview.batchSize ?? 32)
            : t('{0} ready file(s) selected.', selected.length);
    }

    async startTask() {
        if (!this._preview) return;
        const rows = this._preview.rows ?? [];
        const selected = rows.filter(row => this._selectedPreviewIds.has(row.id) && row.action === 'archive');
        if (selected.length === 0 && this._preview.scope !== 'directory') return;
        const required = new Set(this._preview.requiredConfirmations ?? []);
        const confirmedNoBackup = required.has('noBackup') && !!el('archiveConfirmNoBackup')?.checked;
        const confirmedUseCurrentOutput = !!el('archiveConfirmUseCurrentOutput')?.checked;
        if (required.has('noBackup') && !confirmedNoBackup) {
            showToastSafe(t('Confirm that no original backup will be kept for this task.'), 'warning');
            return;
        }
        if (el('archiveConfirmUseCurrentOutput') && !confirmedUseCurrentOutput) {
            showToastSafe(t('Confirm that this task will process the current compressed output.'), 'warning');
            return;
        }

        const button = el('archiveStartTask');
        if (button) button.disabled = true;
        try {
            const result = await archiveApi.startJob({
                requestId: this._taskRequestId ?? requestId(),
                previewId: this._preview.previewId,
                selectedIds: this._preview.scope === 'directory' ? null : selected.map(row => row.id),
                confirmedNoBackup,
                confirmedUseCurrentOutput,
            });
            this._taskRequestId = null;
            this._currentJobId = result.jobId;
            if (el('archivePreviewError')) el('archivePreviewError').textContent = '';
            this._setPreviewView('job');
            this._setPreviewHeading('Archive task');
            if (el('archiveOpenHistory')) el('archiveOpenHistory').style.display = '';
            showToastSafe(t('Archive task started: {0} file(s) queued.', result.queued ?? selected.length), 'success');
            await this.loadJob(result.jobId);
            this.loadHistory({ reset: true });
        } catch (error) {
            if (el('archivePreviewError')) {
                el('archivePreviewError').textContent = errorText(error);
                el('archivePreviewError').style.display = '';
            }
            showToastSafe(errorText(error), 'danger');
            if (button) button.disabled = false;
        }
    }

    async onJobUpdated(payload) {
        const jobId = typeof payload === 'string' ? payload : payload?.jobId;
        // Reuse existing push events; no polling or automatic task continuation.
        if (el('archiveFolderStatus') && !this._folderStatusTimer) {
            this._folderStatusTimer = setTimeout(() => {
                this._folderStatusTimer = null;
                this.loadFolderStatus();
            }, 1000);
        }
        if (jobId && this._currentJobId === jobId) await this.loadJob(jobId, { open: false });
        if (el('archiveHistoryPanel')?.style.display !== 'none') this.loadHistory({ reset: true });
    }

    async openJob(jobId) {
        this._currentJobId = jobId;
        this._setPreviewView('job');
        this._setPreviewHeading('Archive task details');
        this._setPreviewTarget('');
        if (el('archiveOpenHistory')) el('archiveOpenHistory').style.display = '';
        openModal(PREVIEW_MODAL);
        await this.loadJob(jobId, { open: false });
    }

    async loadJob(jobId, { open = true } = {}) {
        if (this._loadingJob === jobId) { this._pendingJobReload = jobId; return; }
        this._loadingJob = jobId;
        const generation = ++this._jobLoadGeneration;
        if (open) openModal(PREVIEW_MODAL);
        const detail = el('archiveJobDetail');
        if (!detail) { this._loadingJob = null; return; }
        detail.innerHTML = `<div class="text-center py-4"><span class="spinner-border spinner-border-sm me-2"></span>${escaped('Loading task details…')}</div>`;
        try {
            const job = await archiveApi.getJob(jobId);
            if (generation !== this._jobLoadGeneration || this._currentJobId !== jobId) return;
            this._currentJob = job;
            this._renderJob(job);
        } catch (error) {
            if (generation !== this._jobLoadGeneration) return;
            detail.innerHTML = `<div class="alert alert-danger mb-0">${escapeHtml(errorText(error))}</div>`;
        } finally {
            if (this._loadingJob === jobId) this._loadingJob = null;
            if (this._pendingJobReload === jobId) {
                this._pendingJobReload = null;
                setTimeout(() => { if (this._currentJobId === jobId) this.loadJob(jobId, { open: false }); }, 200);
            }
        }
    }

    _renderJob(job) {
        const detail = el('archiveJobDetail');
        if (!detail) return;
        const entries = job.entries ?? [];
        const summary = job.summary ?? {};
        const totalKnown = summary.totalKnown !== false;
        const processed = (summary.completed ?? 0) + (summary.restored ?? 0) + (summary.failed ?? 0) + (summary.skipped ?? 0);
        const progress = totalKnown && summary.total ? Math.round(100 * processed / summary.total) : 0;
        const canContinue = !!job.canContinue;
        const userPaused = !!job.pausedByUser;
        const directory = job.scope === 'directory';
        const status = userPaused ? t('Paused') : statusLabel(job.status);
        const rules = job.rules ?? {};
        const choice = job.pendingOutputPolicy ?? rules;
        const storagePaused = String(job.status).toLowerCase() === 'paused_storage' && !userPaused;
        const backupNote = storagePaused
            ? `<div class="alert alert-warning py-2 small">${escaped('Processing is paused. Check the specific error and file path above before continuing.')}</div>`
            : String(job.status).toLowerCase() === 'interrupted'
                ? `<div class="alert alert-warning py-2 small">${escaped(directory ? 'Paused. Press Continue when ready.' : 'This task was interrupted. Check the source, output and backup folders before manually continuing.')}</div>`
                : '';
        detail.innerHTML = `
            <div class="d-flex justify-content-between align-items-start gap-3 flex-wrap mb-2">
                <div><h6 class="mb-1" data-user-content>${job.scope === 'directory' ? escapeHtml(job.sourceRoot ?? '') : escaped('Task {0}', job.jobId ?? job.id ?? '')}</h6>
                    <div class="small text-muted">${escaped('Status')}: ${escapeHtml(status)} · ${escaped('Created')}: ${escapeHtml(job.createdAt ?? '—')}</div></div>
                ${job.scope === 'directory' && ['preparing', 'queued', 'running'].includes(job.status) ? `<button type="button" class="btn btn-sm btn-outline-secondary" id="archiveJobPause">${escaped('Pause entire folder')}</button>` : ''}
                ${canContinue ? `<button type="button" class="btn btn-sm btn-outline-warning" id="archiveJobContinue"><i class="fas fa-play me-1"></i>${escaped('Continue')}</button>` : ''}
            </div>
            ${job.error ? `<div class="alert alert-warning py-2 small" data-user-content>${escapeHtml(backendText(job.error))}</div>` : ''}
            ${backupNote}
            <div class="encoder-panel rounded p-3 mb-3">
                <div class="fw-semibold">${escapeHtml(outputChoiceSummary(choice))}</div>
                <div class="small text-muted mb-2">${escaped('Completed files and existing file transactions retain their original choices. Backups are removed manually.')}</div>
                <div class="d-flex flex-wrap gap-2">
                    ${canContinue ? `<button class="btn btn-sm btn-outline-primary" id="archiveJobApplyOutput">${escaped('Apply saved output choices to pending files')}</button>` : ''}
                    <button class="btn btn-sm btn-outline-primary" id="archiveJobBackups"><i class="fas fa-folder-open me-1"></i>${escaped('Browse completed outputs and original backup folders')}</button>
                    ${job.recycleFolderAvailable ? `<button class="btn btn-sm btn-outline-secondary" id="archiveJobRecycle">${escaped('Open NAS recycle bin')}</button>` : ''}
                </div>
                ${job.recycleFolderAvailable ? `<div class="small text-muted mt-2">${escaped('Deleted originals may remain in the NAS recycle bin and continue to use space. Manage this folder manually.')}</div>` : ''}
                ${this._policyResult && this._policyResult.jobId === (job.jobId ?? job.id) ? `<div class="small mt-2">${escaped('Updated {0} pending files; {1} existing transactions kept their choices.', this._policyResult.updated, this._policyResult.protectedTransactions)}</div>` : ''}
            </div>
            <div class="progress mb-2" style="height:.8rem"><div class="progress-bar ${totalKnown ? '' : 'progress-bar-striped progress-bar-animated'}" role="progressbar" style="width:${totalKnown ? progress : 100}%" ${totalKnown ? `aria-valuenow="${progress}" aria-valuemin="0" aria-valuemax="100"` : `aria-label="${escaped('Discovering in batches; total unknown')}"`}></div></div>
            <div class="small text-muted mb-3">${totalKnown ? escaped('{0}% complete · {1}/{2} processed · {3} queued · {4} failed', progress, processed, summary.total ?? 0, (summary.remaining ?? summary.pending ?? 0), summary.failed ?? 0) : escaped('Discovery unfinished, total unknown · {0} found · {1} processed · {2} queued · {3} failed', summary.total ?? 0, processed, (summary.remaining ?? summary.pending ?? 0), summary.failed ?? 0)}</div>

            ${job.entriesTruncated ? `<div class="alert alert-info py-2 small">${escaped(directory ? 'Showing the latest {0} entries; totals include all {1}.' : 'Showing the latest {0} entries; totals include all {1}. Open history to browse every entry by page.', job.entryLimit ?? 100, summary.total ?? 0)}</div>` : ''}
            <div class="table-responsive dash-table-wrap border rounded" style="max-height:40vh"><table class="dash-table">
                <thead><tr><th>${escaped('Status')}</th><th>${escaped('File')}</th><th>${escaped('Progress')}</th>${directory ? '' : `<th>${escaped('Actual rate')}</th><th>${escaped('Historical sample score')}</th>`}<th>${escaped('Reason')}</th><th>${escaped('Files')}</th></tr></thead>
                <tbody>${entries.map(entry => `<tr>
                    <td class="text-nowrap">${escapeHtml(userPaused && entry.stage === 'paused_storage' ? t('Paused') : statusLabel(entry.stage ?? entry.status))}</td>
                    <td class="dash-file" data-user-content title="${escapeHtml(entry.path ?? '')}"><div class="dash-file-inner"><span class="dash-fileicon"><i class="fas fa-file-video"></i></span><div class="dash-filename">${escapeHtml(directory ? folderRelativePath(entry.path, job.sourceRoot) : entry.path ?? '')}</div></div></td>
                    <td>${escapeHtml(userPaused && entry.stage === 'paused_storage' ? t('Paused') : entryProgressLabel(entry))}</td>
                    ${directory ? '' : `<td>${escapeHtml(formatRate(entry.actualMbps))}</td><td>${escapeHtml(formatScore(entry.score))}</td>`}
                    <td class="small text-muted" data-user-content>${escapeHtml(backendText(directory ? (entry.reason || entry.error || '—') : (entry.reason ?? entry.error ?? entry.scoreError ?? '')))}</td>
                    <td>${archiveFileButtons(entry)}</td>
                </tr>`).join('') || `<tr><td colspan="${directory ? 5 : 7}" class="text-muted text-center py-3">${escaped('No task entries.')}</td></tr>`}</tbody>
            </table></div>
            ${job.scope === 'directory' ? '' : `<div class="small text-muted mt-2">${escaped('Saved rule snapshot')}: ${escapeHtml(`${rules.targetMbps ?? '—'} Mbps · ${rules.outputMode ?? '—'} · ${rules.backupEnabled ? t('backup enabled') : t('no backup')}`)}</div>`}`;
    }

    async applyPendingOutput() {
        const jobId = this._currentJobId;
        if (!jobId) return;
        const button = el('archiveJobApplyOutput');
        if (button) button.disabled = true;
        try {
            const settings = await archiveApi.getSettings();
            const choice = Object.fromEntries(['outputMode', 'outputDirectory', 'backupEnabled', 'backupLocation', 'backupDirectory'].map(key => [key, settings[key]]));
            this._policyResult = await archiveApi.applyPendingOutput(jobId, choice);
            showToastSafe(t('Updated {0} pending files; {1} existing transactions kept their choices.', this._policyResult.updated, this._policyResult.protectedTransactions), 'success');
            if (this._currentJobId === jobId) await this.loadJob(jobId, { open: false });
        } catch (error) { showToastSafe(errorText(error), 'danger'); }
        finally { if (button) button.disabled = false; }
    }

    async pauseJob() {
        const jobId = this._currentJobId;
        if (!jobId) return;
        try {
            await archiveApi.pauseJob(jobId);
            await this.loadJob(jobId, { open: false });
        } catch (error) { showToastSafe(errorText(error), 'danger'); }
    }

    async continueJob() {
        const jobId = this._currentJobId;
        if (!jobId) return;
        try {
            await archiveApi.continueJob(jobId);
            showToastSafe(t('Archive task continued.'), 'success');
            await this.loadJob(jobId, { open: false });
        } catch (error) {
            showToastSafe(errorText(error), 'danger');
        }
    }

    async loadHistory({ reset = false } = {}) {
        if (reset) this._historySkip = 0;
        const body = el('archiveHistoryRows');
        if (!body) return;
        body.innerHTML = `<tr><td colspan="9" class="text-muted text-center py-4"><span class="spinner-border spinner-border-sm me-2"></span>${escaped('Loading task history…')}</td></tr>`;
        try {
            const status = el('archiveHistoryStatus')?.value || null;
            const result = await archiveApi.getHistory({ jobId: this._historyJobId ?? null, status, skip: this._historySkip, take: HISTORY_PAGE_SIZE });
            this._historyItems = result.items ?? [];
            this._directoryJobs = result.directoryJobs ?? [];
            this._historyTotal = Number(result.total ?? 0);
            this._historySkip = Number(result.skip ?? this._historySkip);
            this._renderHistory();
        } catch (error) {
            body.innerHTML = `<tr><td colspan="9" class="text-danger text-center py-4">${escapeHtml(errorText(error))}</td></tr>`;
            if (el('archiveHistoryPageLabel')) el('archiveHistoryPageLabel').textContent = '';
        }
    }

    _renderHistory() {
        const body = el('archiveHistoryRows');
        if (!body) return;
        body.innerHTML = this._historyItems.map(item => {
            const status = String(item.status ?? item.stage ?? '').toLowerCase();
            const outputSize = item.outputBytes == null ? '' : ` → ${formatBytes(item.outputBytes)}`;
            const canRestore = item.canRestore !== false && !!(item.backupPath || item.backupAvailable);
            return `<tr>
                <td><input type="checkbox" data-history-select value="${escapeHtml(item.id)}" aria-label="${escaped('Select history entry')}" ${this._selectedHistory.has(item.id) ? 'checked' : ''}></td>
                <td><span class="badge ${status === 'completed' ? 'bg-success' : status === 'failed' ? 'bg-danger' : status === 'paused_storage' ? 'bg-warning text-dark' : 'bg-secondary'}">${escapeHtml(statusLabel(status))}</span></td>
                <td class="dash-file" data-user-content title="${escapeHtml(item.path ?? '')}"><div class="dash-file-inner"><span class="dash-fileicon"><i class="fas fa-file-video"></i></span><div class="dash-filename">${escapeHtml(item.path ?? '')}</div></div></td>
                <td>${escapeHtml(entryProgressLabel(item))}</td>
                <td>${escapeHtml(formatRate(item.actualMbps ?? item.sourceMbps))}</td>
                <td>${escapeHtml(formatBytes(item.sourceBytes))}${escapeHtml(outputSize)}</td>
                <td data-user-content title="${escapeHtml((item.samples ?? []).map(sample => `${sample.start}s +${sample.duration}s: ${formatScore(sample.score)}`).join('\n'))}">${escapeHtml(formatScore(item.score))}</td>
                <td class="small text-muted" data-user-content>${escapeHtml(backendText(item.reason ?? item.error ?? item.retryReason ?? item.rearchiveReason ?? item.rescoreReason ?? item.restoreReason ?? item.scoreError ?? ''))}</td>
                <td>${archiveFileButtons(item)}<button type="button" class="btn btn-sm btn-outline-secondary mt-1" data-history-action="details" data-entry-id="${escapeHtml(item.id)}">${escaped('Details')}</button>
                    ${canRestore ? `<button type="button" class="btn btn-sm btn-outline-warning ms-1" data-history-action="restore" data-entry-id="${escapeHtml(item.id)}">${escaped('Restore')}</button>` : ''}</td>
            </tr>`;
        }).join('') || `<tr><td colspan="9" class="text-muted text-center py-4">${escaped('No archive history found.')}</td></tr>`;

        body.innerHTML = (this._directoryJobs ?? []).map(job => `<tr><td colspan="9"><button class="btn btn-sm btn-outline-secondary me-2" data-directory-job="${escapeHtml(job.jobId)}">${escaped('Open unfinished folder task')}</button>${escapeHtml(statusLabel(job.status))} · <span data-user-content>${escapeHtml(job.sourceRoot)}</span>${job.scanComplete ? '' : ' · ' + escaped('Total unknown')}</td></tr>`).join('') + body.innerHTML;
        const start = this._historyTotal === 0 ? 0 : this._historySkip + 1;
        const end = Math.min(this._historySkip + this._historyItems.length, this._historyTotal);
        if (el('archiveHistoryPageLabel')) el('archiveHistoryPageLabel').textContent = t('{0}–{1} of {2}', start, end, this._historyTotal);
        if (el('archiveHistoryPrevious')) el('archiveHistoryPrevious').disabled = this._historySkip <= 0;
        if (el('archiveHistoryNext')) el('archiveHistoryNext').disabled = this._historySkip + this._historyItems.length >= this._historyTotal;
        const pageToggle = el('archiveHistorySelectPage');
        if (pageToggle) {
            pageToggle.checked = this._historyItems.length > 0 && this._historyItems.every(item => this._selectedHistory.has(item.id));
            pageToggle.indeterminate = this._historyItems.some(item => this._selectedHistory.has(item.id)) && !pageToggle.checked;
        }
        this._renderHistorySelection();
    }

    _renderHistorySelection() {
        const selected = [...this._selectedHistory.values()];
        const failed = selected.length > 0 && selected.every(item =>
            String(item.status ?? item.stage ?? '').toLowerCase() === 'failed' && item.canRetry !== false);
        const canRearchive = selected.length > 0 && selected.every(item => item.canRearchive !== false);
        const canRescore = selected.length > 0 && selected.every(item => item.canRescore !== false);
        const buttons = {
            archiveRetrySelected: failed,
            archiveRearchiveSelected: canRearchive,
            archiveRescoreSelected: canRescore,
            archiveRestoreSelected: selected.some(item => item.canRestore !== false && (item.backupPath || item.backupAvailable)),
        };
        for (const [id, enabled] of Object.entries(buttons)) if (el(id)) el(id).disabled = !enabled;
    }

    async previewSelected(intent) {
        const selected = [...this._selectedHistory.values()];
        if (!selected.length) return;
        if (intent === 'retryFailed' && !selected.every(item => String(item.status ?? item.stage ?? '').toLowerCase() === 'failed' && item.canRetry !== false)) {
            showToastSafe(t('Select failed entries to retry.'), 'warning');
            return;
        }
        const ids = selected.map(item => item.id);
        await this.openPreview({ entryIds: ids, intent }, t(intent === 'retryFailed' ? 'Retry selected failures' : 'Rearchive selected entries'));
    }

    async openRetryPath(filePath) {
        let mode;
        try { mode = await this.getMode(); }
        catch (error) { showToastSafe(errorText(error), 'danger'); return false; }
        if (mode !== 'custom') return false;

        const normalized = String(filePath ?? '').replace(/\\/g, '/');
        try {
            let skip = 0;
            let match = null;
            let total = 0;
            do {
                const page = await archiveApi.getHistory({ status: 'failed', skip, take: 100 });
                total = Number(page.total ?? 0);
                match = (page.items ?? []).find(item => String(item.path ?? '').replace(/\\/g, '/') === normalized) ?? null;
                if (match || (page.items ?? []).length === 0) break;
                skip += page.items.length;
            } while (skip < total && skip < 5000);

            const request = match
                ? { entryIds: [match.id], intent: 'retryFailed' }
                : { filePaths: [filePath], intent: 'retryFailed' };
            return this.openPreview(request, filePath);
        } catch (error) {
            showToastSafe(errorText(error), 'danger');
            return false;
        }
    }

    async rescoreSelected() {
        const ids = [...this._selectedHistory.keys()];
        if (!ids.length) return;
        try {
            const result = await archiveApi.rescore(ids);
            showToastSafe(t('Sample scoring queued for {0} file(s).', result.queued ?? ids.length), 'success');
            this.loadHistory({ reset: true });
        } catch (error) { showToastSafe(errorText(error), 'danger'); }
    }

    async previewRestore(entryIds) {
        if (!entryIds?.length) return;
        try {
            this._restorePreview = await archiveApi.previewRestore({ entryIds });
            this._restoreRequestId = requestId();
            this._renderRestorePreview();
            openModal(RESTORE_MODAL);
        } catch (error) { showToastSafe(errorText(error), 'danger'); }
    }

    _renderRestorePreview() {
        const preview = this._restorePreview;
        if (!preview) return;
        const rows = preview.rows ?? [];
        const conflicts = preview.conflicts ?? [];
        const conflictCount = Array.isArray(conflicts) ? conflicts.length : Math.max(0, Number(conflicts) || 0);
        if (el('archiveRestoreRows')) el('archiveRestoreRows').innerHTML = rows.map(row => `
            <tr><td class="dash-file" data-user-content title="${escapeHtml(row.path ?? '')}"><div class="dash-filename">${escapeHtml(row.path ?? '')}</div></td>
                <td class="small text-break" data-user-content>${escapeHtml(row.backupPath ?? '—')}</td>
                <td>${escapeHtml(row.action ?? '')}</td><td class="small text-muted" data-user-content>${escapeHtml(backendText(row.reason ?? row.reasonCode ?? ''))}</td></tr>`).join('')
            || `<tr><td colspan="4" class="text-muted text-center py-3">${escaped('No restoreable originals were found.')}</td></tr>`;
        const warning = el('archiveRestoreWarning');
        if (warning) {
            warning.textContent = t('{0} restore item(s) found. Restore starts only after this preview.', rows.length);
            warning.style.display = '';
        }
        const conflictBox = el('archiveRestoreConflicts');
        if (conflictBox) {
            conflictBox.textContent = conflictCount
                ? t('{0} conflict(s) found. These files will not be overwritten; resolve the conflicts before restoring.', conflictCount)
                : '';
            conflictBox.style.display = conflictCount ? '' : 'none';
        }
        const restoreRows = rows.filter(row => row.action === 'restore' || row.action === 'ready');
        if (el('archiveRestoreConfirm')) el('archiveRestoreConfirm').disabled = conflictCount > 0 || restoreRows.length === 0;
    }

    async confirmRestore() {
        const preview = this._restorePreview;
        const conflicts = preview?.conflicts ?? [];
        if (!preview || (Array.isArray(conflicts) ? conflicts.length : Number(conflicts) || 0) > 0) return;
        const confirmed = await showConfirmModal(
            t('Restore original files'),
            `<p>${escaped('Restore the original files from the backups shown in this preview?')}</p><p class="small text-muted mb-0">${escaped('Files that conflict are never overwritten.')}</p>`,
            t('Restore originals'),
        );
        if (!confirmed) return;
        const button = el('archiveRestoreConfirm');
        if (button) button.disabled = true;
        try {
            const result = await archiveApi.restore({ requestId: this._restoreRequestId ?? requestId(), restorePreviewId: preview.restorePreviewId, confirmed: true });
            this._restoreRequestId = null;
            closeModal(RESTORE_MODAL);
            showToastSafe(t('Restored {0} file(s); {1} failed.', result.restored ?? 0, result.failed ?? 0), result.failed ? 'warning' : 'success');
            this.loadHistory({ reset: true });
        } catch (error) {
            showToastSafe(errorText(error), 'danger');
            if (button) button.disabled = false;
        }
    }

    showHistory() {
        this.openHistory();
    }

    openHistory({ jobId = null, status = null } = {}) {
        this._historyJobId = jobId;
        if (el('archiveHistoryStatus')) el('archiveHistoryStatus').value = status ?? '';
        closeModal(PREVIEW_MODAL);
        const settingsModal = el('settingsModal');
        settingsModal?.classList.add('open');
        document.querySelector('[data-bs-target="#archiveTab"]')?.click();
        this.showHistoryPanel();
    }

    _refreshLocalizedViews() {
        this._renderFolderStatus();
        if (this._settingsStatusKey && el('archiveSettingsStatus'))
            el('archiveSettingsStatus').textContent = t(this._settingsStatusKey);
        else if (this._settingsStatusError && el('archiveSettingsStatus'))
            el('archiveSettingsStatus').textContent = errorText(this._settingsStatusError);
        if (this._settingsError) this._showUnavailable(this._settingsError);
        this._setPreviewHeading();
        this._setPreviewTarget();
        if (this._preview) this._renderPreview(this._preview);
        if (this._currentJob) this._renderJob(this._currentJob);
        if (this._historyItems.length) this._renderHistory();
        if (this._restorePreview) this._renderRestorePreview();
        if (el('archiveHistoryPanel')?.style.display !== 'none') this.loadHistory({ reset: true });
        if (el('archiveCacheUsage')) this.loadCache().catch(() => {});
    }
}

