const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { createFileActionHandler } = require('../file-actions.cjs');

function nativeFixture(root, resolver) {
    const frame = { url: 'http://localhost:6769/' };
    const window = { webContents: { mainFrame: frame } };
    const calls = [];
    const handler = createFileActionHandler({ getWindow: () => window, getPort: () => 6769, fs,
        resolveFile: resolver ?? (async (_kind, _id, target) => ({ path: target.endsWith('folder') ? root : path.join(root, '成片 & one.mp4'), directory: target.endsWith('folder') })),
        shell: { openPath: async p => { calls.push(['open', p]); return ''; }, showItemInFolder: p => calls.push(['reveal', p]) } });
    return { handler, calls, event: { sender: window.webContents, senderFrame: frame } };
}

test('native file actions use OS player and Finder for only resolved registered paths', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'snacks-native-'));
    try {
        fs.writeFileSync(path.join(root, '成片 & one.mp4'), 'synthetic');
        const { handler, calls, event } = nativeFixture(root);
        for (const action of ['play', 'reveal', 'backup-folder']) assert.equal((await handler(event, { kind: 'archive', id: 'entry-1', action })).ok, true);
        assert.equal((await handler(event, { kind: 'job', id: 'job-1', action: 'recycle-folder' })).ok, true);
        assert.deepEqual(calls.map(x => x[0]), ['open', 'reveal', 'open', 'open']);
        assert.equal(calls[0][1], path.join(root, '成片 & one.mp4'));
    } finally { fs.rmSync(root, { recursive: true }); }
});

test('native IPC rejects arbitrary paths, foreign frames, URLs and executable playback', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'snacks-native-'));
    try {
        const f = nativeFixture(root);
        assert.equal((await f.handler(f.event, { kind: 'work', id: 'x', action: 'play', path: '/bin/sh' })).ok, false);
        assert.equal((await f.handler({ ...f.event, senderFrame: { url: 'https://external.example/' } }, { kind: 'work', id: 'x', action: 'play' })).ok, false);
        assert.equal((await f.handler(f.event, { kind: 'work', id: '../escape', action: 'play' })).ok, false);
        const url = nativeFixture(root, async () => ({ path: 'https://example.com/video.mp4' }));
        assert.equal((await url.handler(url.event, { kind: 'work', id: 'x', action: 'play' })).ok, false);
        const exe = path.join(root, 'run.command'); fs.writeFileSync(exe, 'echo no');
        const executable = nativeFixture(root, async () => ({ path: exe }));
        assert.equal((await executable.handler(executable.event, { kind: 'work', id: 'x', action: 'play' })).ok, false);
        assert.deepEqual(f.calls, []); assert.deepEqual(executable.calls, []);
        assert.equal((await f.handler(f.event, { kind: 'work', id: 'x', action: 'reveal' })).ok, false);
    } finally { fs.rmSync(root, { recursive: true }); }
});

async function browserModule(rel, replacements = []) {
    let source = fs.readFileSync(path.resolve(__dirname, '../../', rel), 'utf8');
    for (const [before, after] of replacements) source = source.replace(before, after);
    return import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}#${Math.random()}`);
}
const domImport = ["import { escapeHtml } from '../utils/dom.js';", 'const escapeHtml = x => String(x ?? "").replaceAll("<", "&lt;");'];

test('completed cards expose play and Finder text while keeping logs secondary', async () => {
    const module = await browserModule('Snacks/wwwroot/js/queue/work-item-renderer.js', [domImport]);
    const html = module.getActionButtons({ status: 'Completed' });
    assert.match(html, /data-action="play"/); assert.match(html, />Play video</); assert.match(html, /Show in Finder/);
    assert.match(html, />Log</); assert.doesNotMatch(html, /<video|https?:\/\/|data-action="remove"/);
});

test('archive file buttons track manual backup deletion and never render a browser player', async () => {
    global.window = {};
    const module = await browserModule('Snacks/wwwroot/js/utils/file-actions.js', [["import { escapeHtml } from './dom.js';", 'const escapeHtml = x => String(x ?? "").replaceAll("<", "&lt;");']]);
    const html = module.archiveFileButtons({ id: 'entry', stage: 'completed', backupPath: '/hidden/.originals/a.mp4', backupAvailable: false });
    assert.match(html, /data-file-action="play"/); assert.match(html, /data-file-action="reveal"/); assert.match(html, /Open original backup folder/);
    assert.match(html, /missing or unavailable/); assert.doesNotMatch(html, /<video|delete-backup/);
});

test('a size-skipped archive dispatch displays Skipped and its reason without playback controls', async () => {
    global.formatFileSize = value => String(value); global.formatBitrate = value => String(value); global.formatDuration = value => String(value);
    const module = await browserModule('Snacks/wwwroot/js/queue/work-item-renderer.js', [domImport]);
    const item = { status: 'Stopped', archiveEntryId: 'small', archiveStage: 'skipped', waitReason: 'Source smaller than 10 MB; unchanged.', fileName: 'small.mkv', size: 100, createdAt: new Date().toISOString() };
    const html = module.getWorkItemHtml(item, false);
    assert.match(html, />Skipped<\/span>/); assert.match(html, /smaller than 10 MB/);
    assert.doesNotMatch(html, />Stopped<\/span>|data-action="play"|data-action="retry"/);
});

test('output adoption keeps the clicked job scope while settings are loading', async () => {
    const calls = []; let completeSettings;
    global.document = { getElementById: () => null };
    global.window = { showToast: () => {} };
    global.fileActionsTestApi = {
        getSettings: () => new Promise(resolve => { completeSettings = resolve; }),
        applyPendingOutput: async (id, choice) => { calls.push([id, choice]); return { jobId: id, updated: 1, protectedTransactions: 0 }; }
    };
    const { ArchiveUI } = await browserModule('Snacks/wwwroot/js/archive/archive-ui.js', [domImport,
        ["import { archiveApi } from '../api.js';", 'const archiveApi = globalThis.fileActionsTestApi;'],
        ["import { archiveFileButtons, performFileAction } from '../utils/file-actions.js';", 'const archiveFileButtons = () => ""; const performFileAction = () => {};'],
        ["import { pickFolder } from '../settings/folder-picker.js';", 'const pickFolder = () => {};'],
        ["import { closeModal, openModal, showConfirmModal } from '../utils/modal-controller.js';", 'const closeModal = () => {}; const openModal = () => {}; const showConfirmModal = async () => true;']]);
    const ui = new ArchiveUI(); ui._currentJobId = 'clicked-A'; ui.loadJob = async id => calls.push(['refresh', id]);
    const applying = ui.applyPendingOutput(); ui._currentJobId = 'new-B';
    completeSettings({ outputMode: 'inPlace', backupEnabled: false }); await applying;
    assert.equal(calls.length, 1); assert.equal(calls[0][0], 'clicked-A'); assert.equal(calls[0][1].backupEnabled, false);
});
