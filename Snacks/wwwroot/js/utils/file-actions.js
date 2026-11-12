import { escapeHtml } from './dom.js';
const t = source => window.SnacksI18n?.t?.(source) ?? source;

export async function performFileAction(kind, id, action) {
    try {
        if (!window.snacksDesktop?.fileAction) throw new Error(t('Open this task in the desktop app to play or locate its files.'));
        const result = await window.snacksDesktop.fileAction({ kind, id, action });
        if (!result?.ok) throw new Error(result?.message || t('Unable to open this file.'));
        return true;
    } catch (error) {
        if (window.showToast) window.showToast(error.message, 'danger');
        else window.alert(error.message);
        return false;
    }
}

export function archiveFileButtons(entry) {
    const id = escapeHtml(entry.id);
    const button = (action, icon, label, title = '') => `<button type="button" class="btn btn-sm btn-outline-primary" data-file-action="${action}" data-entry-id="${id}" title="${escapeHtml(title)}"><i class="fas ${icon} me-1"></i>${escapeHtml(t(label))}</button>`;
    const actions = entry.stage === 'completed'
        ? button('play', 'fa-play', 'Play video') + button('reveal', 'fa-folder-open', 'Show in Finder')
        : button('source-reveal', 'fa-folder-open', 'Locate source');
    const backup = entry.backupPath
        ? button('backup-folder', 'fa-folder-open', 'Open original backup folder', entry.backupPath)
          + `<span class="small text-muted">${escapeHtml(t(entry.backupAvailable ? 'Original backup available; delete it manually when no longer needed.' : 'Recorded backup is missing or unavailable.'))}</span>`
        : `<span class="small text-muted">${escapeHtml(t('No extra original backup recorded.'))}</span>`;
    return `<div class="d-flex flex-wrap gap-1">${actions}${backup}</div>`;
}
