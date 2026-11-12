const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');
const repo = path.resolve(__dirname, '../..');

function dashboard() {
    const dictionary = JSON.parse(fs.readFileSync(path.join(repo, 'Snacks/wwwroot/i18n/zh-CN.json')));
    const nodes = {}, listeners = {}, actions = [];
    const context = {
        window: { SnacksI18n: { t: (s, ...args) => (dictionary[s] || s).replace(/\{(\d+)\}/g, (_, n) => args[n] ?? '') } },
        localStorage: { getItem: () => null }, registerPage() {},
        getComputedStyle: () => ({ getPropertyValue: () => '#888' }),
        escapeHtml: s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])),
        performFileAction: (...args) => actions.push(args),
        document: { documentElement: {}, querySelector: id => nodes[id] ||= {},
            getElementById: id => nodes[id] ||= {}, addEventListener: (name, listener) => { listeners[name] = listener; } }
    };
    vm.createContext(context);
    const source = fs.readFileSync(path.join(repo, 'Snacks/wwwroot/js/dashboard/dashboard.js'), 'utf8')
        .replace(/^import .*;\s*$/gm, '').replace(/^export /gm, '');
    vm.runInContext(source, context);
    return { context, nodes, listeners, actions };
}

test('recent and top savings expose translated output actions by durable job ID, with escaped file labels', () => {
    const { context, nodes, listeners, actions } = dashboard();
    const row = { id: 0, jobId: 'full-archive-entry-id', fileName: '<img src=x>', filePath: '/metadata-only/name.mp4',
        outcome: 'Completed', originalSizeBytes: 500, encodedSizeBytes: 0, bytesSaved: 0, encodeSeconds: 3.5, completedAt: new Date().toISOString() };
    context.renderRecentTable([row]); context.renderTopSavingsTable([row]);
    for (const selector of ['#recentTable tbody', '#topSavingsTable tbody']) {
        const html = nodes[selector].innerHTML;
        assert.match(html, /播放视频/); assert.match(html, /在 Finder 中显示/);
        assert.match(html, /data-job-id="full-archive-entry-id"/); assert.doesNotMatch(html, /data-job-id="0"/);
        assert.match(html, /&lt;img src=x&gt;/); assert.doesNotMatch(html, /<img src=x>/);
        assert.match(html, /→ —/);
    }
    assert.match(nodes['#recentTable tbody'].innerHTML, /转码：3.5秒/);
    assert.match(nodes['#recentTable tbody'].innerHTML, /完成于\d+秒前/);
    listeners.click({ target: { closest: () => ({ dataset: { jobId: row.jobId, completedFileAction: 'reveal' } }) } });
    assert.deepEqual(actions, [['work', row.jobId, 'reveal']]);
});

test('recorded time shows coverage and average speed uses the paired denominator', () => {
    const { context, nodes } = dashboard();
    context.renderHero({ totalBytesSaved: 1000, totalOriginalBytes: 2000, totalEncodes: 4, videoEncodes: 4,
        totalEncodeSeconds: 200, timedContentSeconds: 60, speedSampleEncodeSeconds: 20,
        unknownEncodeTimeEncodes: 1, recoveredEncodeTimeEncodes: 2, fourKEncodes: 0 }, []);
    assert.equal(nodes.heroHours.textContent, '3分钟');
    assert.match(nodes.heroHoursSub.textContent, /已记录 3\/4 个文件/);
    assert.match(nodes.heroHoursSub.textContent, /其中 2 个来自历史日志/);
    assert.match(nodes.heroHoursSub.textContent, /平均 3.0×/);
    assert.match(nodes.heroHoursSub.textContent, /1 个文件的用时缺失或不完整/);
});

test('actual global translator preserves Snacks branding and protected paths while translating full duration phrases', async () => {
    const dictionary = JSON.parse(fs.readFileSync(path.join(repo, 'Snacks/wwwroot/i18n/zh-CN.json')));
    const brandParent = { isContentEditable: false, closest: () => null };
    const brand = { nodeType: 3, nodeValue: 'Snacks', parentElement: brandParent };
    const plainWord = { nodeType: 3, nodeValue: 'Program', parentElement: brandParent };
    const filePath = '/media/Fixture/Snacks/Program/Refresh.m';
    const protectedPath = { nodeType: 3, nodeValue: filePath, parentElement: { closest: () => ({}) } };
    const alt = { value: 'Snacks', matches: () => false, closest: () => null,
        hasAttribute: name => name === 'alt', getAttribute() { return this.value; }, setAttribute(_, value) { this.value = value; } };
    const context = { URL, console, location: { href: 'http://localhost/Dashboard', pathname: '/Dashboard' },
        localStorage: { getItem: () => 'zh-CN', setItem() {} }, Node: { TEXT_NODE: 3, ELEMENT_NODE: 1, DOCUMENT_NODE: 9 },
        NodeFilter: { SHOW_TEXT: 4 }, CustomEvent: class {}, MutationObserver: class { observe() {} disconnect() {} },
        fetch: async () => ({ ok: true, json: async () => dictionary }), window: { setTimeout: () => 0 },
        document: { nodeType: 9, currentScript: null, body: { classList: { add() {} } },
            documentElement: { classList: { add() {}, remove() {}, contains: () => false }, querySelectorAll: () => [alt] },
            createTreeWalker: () => { const nodes = [brand, plainWord, protectedPath]; return { nextNode: () => nodes.shift() }; },
            getElementById: () => null, dispatchEvent() {} } };
    vm.createContext(context);
    vm.runInContext(fs.readFileSync(path.join(repo, 'Snacks/wwwroot/js/i18n.js'), 'utf8'), context);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(brand.nodeValue, 'Snacks'); assert.equal(alt.value, 'Snacks');
    assert.equal(plainWord.nodeValue, 'Program'); assert.equal(protectedPath.nodeValue, filePath);
    assert.equal(context.window.SnacksI18n.t('Snacks'), 'Snacks');
    assert.equal(context.window.SnacksI18n.t(filePath), filePath);
    assert.equal(context.window.SnacksI18n.t('{0} seconds', 3.5), '3.5秒');
    assert.equal(context.window.SnacksI18n.t('3.5 seconds'), '3.5秒');
    assert.equal(context.window.SnacksI18n.t('{0} hours {1} minutes', 2, 5), '2小时 5分钟');
    for (const key of ['s', 'm', 'h', '{0}s', '{0}m', '{0}h', '{0}h {1}m']) assert.equal(dictionary[key], undefined);
});
