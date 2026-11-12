const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const test = require('node:test');

async function fixture() {
    const nodes = new Map();
    for (const id of ['archiveStartTask', 'archivePreviewRowCount', 'archiveJobDetail']) nodes.set(id, { disabled: true, textContent: '', innerHTML: '', style: {} });
    global.document = { getElementById: id => nodes.get(id) ?? null };
    global.window = { SnacksI18n: { t: (text, ...args) => text.replace(/\{(\d+)\}/g, (_, n) => args[n]) } };
    const source = (await fs.readFile(path.resolve(__dirname, '../../Snacks/wwwroot/js/archive/archive-ui.js'), 'utf8'))
        .replace("import { pickFolder } from '../settings/folder-picker.js';", 'const pickFolder = (cb) => { globalThis.archivePicked = cb; };')
        .replace("import { archiveApi } from '../api.js';", 'const archiveApi = globalThis.archiveTestApi;')
        .replace("import { archiveFileButtons, performFileAction } from '../utils/file-actions.js';", 'const archiveFileButtons = () => ""; const performFileAction = () => {};')
        .replace("import { escapeHtml } from '../utils/dom.js';", 'const escapeHtml = x => String(x ?? "").replaceAll("<", "&lt;");')
        .replace("import { closeModal, openModal, showConfirmModal } from '../utils/modal-controller.js';", 'const closeModal = () => {}; const openModal = () => {}; const showConfirmModal = async () => true;');
    const requests = [];
    global.archiveTestApi = { startJob: async request => { requests.push(request); return { jobId: 'synthetic-job', queued: 0 }; } };
    const { ArchiveUI } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}#${Math.random()}`);
    const ui = new ArchiveUI();
    ui._renderPreviewRequirements = () => {};
    ui.loadJob = async () => {};
    ui.loadHistory = async () => {};
    return { ui, nodes, requests, ArchiveUI };
}

test('zero-ready directory preview starts the whole root with no row selection', async () => {
    const { ui, nodes, requests } = await fixture();
    ui._preview = { previewId: 'bounded', scope: 'directory', rows: [], batchSize: 32 };
    ui._updatePreviewActions();
    assert.equal(nodes.get('archiveStartTask').disabled, false);
    assert.match(nodes.get('archiveStartTask').textContent, /entire folder/);
    assert.match(nodes.get('archivePreviewRowCount').textContent, /entire root folder/);
    await ui.startTask();
    assert.equal(requests.length, 1);
    assert.equal(requests[0].selectedIds, null);
});

test('selected-file previews keep explicit row selection', async () => {
    const { ui, nodes, requests } = await fixture();
    ui._preview = { previewId: 'files', rows: [{ id: 'a', action: 'archive' }, { id: 'b', action: 'archive' }] };
    ui._updatePreviewActions();
    assert.equal(nodes.get('archiveStartTask').disabled, true);
    ui._selectedPreviewIds.add('b');
    await ui.startTask();
    assert.deepEqual(requests[0].selectedIds, ['b']);
});

test('unfinished discovery never shows a determinate 100 percent from a completed page', async () => {
    const { ui, nodes } = await fixture();
    ui._renderJob({ status: 'running', scope: 'directory', entries: [{ stage: 'completed' }], summary: { totalKnown: false, total: 32, completed: 32 } });
    const html = nodes.get('archiveJobDetail').innerHTML;
    assert.match(html, /total unknown/);
    assert.doesNotMatch(html, /aria-valuenow=/);
    assert.doesNotMatch(html, /100% complete/);
    assert.match(html, /Pause entire folder/);
});

test('completed discovery uses database totals rather than the latest page', async () => {
    const { ui, nodes } = await fixture();
    ui._renderJob({ status: 'running', entriesTruncated: true, entryLimit: 100, entries: [{ stage: 'completed' }], summary: { totalKnown: true, total: 150, completed: 50, pending: 100 } });
    const html = nodes.get('archiveJobDetail').innerHTML;
    assert.match(html, /aria-valuenow="33"/);
    assert.match(html, /totals include all 150/);
    assert.match(html, /browse every entry by page/);
});


test('job view hides the preview explanation even with Bootstrap display utilities', async () => {
    const { ui, nodes } = await fixture();
    const writes = [];
    nodes.set('archivePreviewTools', { style: { setProperty: (...args) => writes.push(args) } });
    ui._setPreviewView('job');
    assert.deepEqual(writes.at(-1), ['display', 'none', 'important']);
    assert.equal(nodes.get('archivePreviewRowCount').style.display, 'none');
});

test('the final update arriving during a slow detail request triggers a bounded follow-up', async () => {
    const { ui, nodes, ArchiveUI } = await fixture();
    ui.loadJob = ArchiveUI.prototype.loadJob.bind(ui);
    ui._currentJobId = 'slow-job';
    let releaseFirst;
    let calls = 0;
    global.archiveTestApi.getJob = async () => {
        calls++;
        if (calls === 1) return new Promise(resolve => { releaseFirst = resolve; });
        return { jobId: 'slow-job', status: 'completed', entries: [], summary: { totalKnown: true, total: 36, completed: 36 } };
    };
    const first = ui.loadJob('slow-job', { open: false });
    await ui.onJobUpdated({ jobId: 'slow-job' });
    await ui.onJobUpdated({ jobId: 'slow-job' });
    assert.equal(calls, 1);
    releaseFirst({ jobId: 'slow-job', status: 'running', entries: [], summary: { totalKnown: false, total: 32, completed: 32 } });
    await first;
    for (let i = 0; i < 30 && calls < 2; i++) await new Promise(resolve => setTimeout(resolve, 20));
    assert.equal(calls, 2);
    assert.equal(ui._currentJob.status, 'completed');
    assert.match(nodes.get('archiveJobDetail').innerHTML, /36\/36 processed/);
});

test('native folder selection starts immediately without a preview or second confirmation', async () => {
    const { ui } = await fixture();
    const starts = [];
    global.window.snacksDesktop = { selectArchiveFolder: async () => '/synthetic/final-root' };
    global.archiveTestApi.startFolder = async path => { starts.push(path); return { jobId: 'direct' }; };
    await ui.chooseFolder();
    assert.deepEqual(starts, ['/synthetic/final-root']);
    assert.equal(ui._currentJobId, 'direct');
    assert.equal(ui._preview, null);
});

test('web folder picker navigation does not start ancestors; final selection starts once', async () => {
    const { ui } = await fixture();
    const starts = [];
    global.archiveTestApi.startFolder = async path => { starts.push(path); return { jobId: 'web-direct' }; };
    await ui.chooseFolder();
    assert.deepEqual(starts, []);
    await global.archivePicked('/synthetic/selected-child');
    assert.deepEqual(starts, ['/synthetic/selected-child']);
});

test('legacy directory actions bypass preview and enter the direct folder flow', async () => {
    const { ui } = await fixture();
    const starts = [];
    global.archiveTestApi.startFolder = async path => { starts.push(path); return { jobId: 'direct' }; };
    await ui.openPreview({ directoryPath: '/synthetic/root', recursive: false });
    assert.deepEqual(starts, ['/synthetic/root']);
    assert.equal(ui._preview, null);
});

test('user-paused folder progress has no repair warning, rate, or historical score columns', async () => {
    const { ui, nodes } = await fixture();
    ui._renderJob({ jobId: 'p', scope: 'directory', status: 'paused_storage', pausedByUser: true, canContinue: true, sourceRoot: '/fixture', entriesTruncated: true, entryLimit: 100, summary: { totalKnown: false, total: 130 }, entries: [{ stage: 'paused_storage', path: '/fixture/a.mkv' }, { stage: 'completed', path: '/fixture/sub/b.mkv', scoreError: 'obsolete score message' }, { stage: 'failed', path: '/fixture/sub/c.mkv', error: 'encoding failed' }] });
    const html = nodes.get('archiveJobDetail').innerHTML;
    assert.match(html, /Paused/);
    assert.doesNotMatch(html, /Repair the drive/);
    assert.doesNotMatch(html, /Historical sample score|Actual rate|Saved rule snapshot/);
    assert.doesNotMatch(html, /obsolete score message|Open history to browse/);
    assert.match(html, /Showing the latest 100 entries; totals include all 130\./);
    assert.match(html, /<td class="text-nowrap">Paused<\/td>/);
    assert.match(html, /title="\/fixture\/sub\/b\.mkv"/);
    assert.match(html, /class="dash-filename">sub\/b\.mkv<\/div>/);
    assert.match(html, /data-user-content>—<\/td>/);
    assert.match(html, /encoding failed/);
});

test('main screen restores paused folder status and error using a read-only request', async () => {
    const { ui, nodes } = await fixture();
    nodes.set('archiveFolderStatus', { innerHTML: '', style: {} });
    let reads = 0;
    global.archiveTestApi.getFolderStatus = async () => {
        reads++;
        return { jobs: [{ jobId: 'paused-job', status: 'paused_storage', sourceRoot: '/NAS/<source>', error: 'Missing <file>' }] };
    };
    global.archiveTestApi.startFolder = async () => { throw new Error('must not start'); };
    global.archiveTestApi.continueJob = async () => { throw new Error('must not continue'); };
    await ui.loadFolderStatus();
    const banner = nodes.get('archiveFolderStatus');
    assert.equal(reads, 1);
    assert.equal(banner.style.display, '');
    assert.match(banner.innerHTML, /Paused by storage issue/);
    assert.match(banner.innerHTML, /Missing &lt;file>/);
    assert.match(banner.innerHTML, /\/NAS\/&lt;source>/);
    assert.match(banner.innerHTML, /data-folder-progress="paused-job"/);
    assert.match(banner.innerHTML, /View folder progress/);
});

test('folder status updates coalesce push events and clear when work completes', async () => {
    const { ui, nodes } = await fixture();
    nodes.set('archiveFolderStatus', { innerHTML: '', style: {} });
    let reads = 0;
    global.archiveTestApi.getFolderStatus = async () => { reads++; return { jobs: [] }; };
    for (let i = 0; i < 10; i++) await ui.onJobUpdated({ jobId: 'finishing-job' });
    assert.equal(reads, 0);
    for (let i = 0; i < 80 && !reads; i++) await new Promise(resolve => setTimeout(resolve, 20));
    assert.equal(reads, 1);
    assert.equal(nodes.get('archiveFolderStatus').style.display, 'none');
    assert.equal(nodes.get('archiveFolderStatus').innerHTML, '');
});
