const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const test = require('node:test');

async function fixture() {
    let payload = { nodeId: 'local', localActiveJobs: [], archiveSlotUsage: { limit: 3, used: 0 } };
    let calls = 0;
    let mounted = true;
    global.document = { getElementById: id => mounted && id === 'clusterPanel' ? {} : null };
    global.clusterDashboardApi = {
        getStatus: async () => { calls++; return payload; },
        getNodeSettings: async () => ({ nodes: {} }),
    };
    const source = (await fs.readFile(path.resolve(__dirname, '../../Snacks/wwwroot/js/cluster/cluster-dashboard.js'), 'utf8'))
        .replace("import { clusterApi } from '../api.js';", 'const clusterApi = globalThis.clusterDashboardApi;')
        .replace("import { escapeHtml } from '../utils/dom.js';", 'const escapeHtml = x => String(x ?? "").replaceAll("<", "&lt;");');
    const { ClusterDashboard } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}#${Math.random()}`);
    const ui = new ClusterDashboard({}); ui._nodeId = 'local'; ui.render = () => {};
    return { ui, calls: () => calls, set: value => { payload = value; }, mount: value => { mounted = value; } };
}

async function until(condition) {
    for (let i = 0; i < 100; i++) { if (condition()) return; await new Promise(resolve => setTimeout(resolve, 10)); }
    assert.fail('status refresh did not settle');
}

test('standalone disabled-cluster cards refresh starts and finishes using actual shared slot usage', async () => {
    const f = await fixture();
    f.set({ nodeId: 'local', localActiveJobs: [{ jobId: 'apple', deviceId: 'apple', progress: 76 }, { jobId: 'cpu', deviceId: 'cpu', progress: 20 }], archiveSlotUsage: { used: 2, limit: 3 } });
    assert.equal(f.ui.enabled, false); assert.equal(f.ui.role, 'standalone');
    f.ui.onWorkItemUpdated({ id: 'apple', progress: 76 });
    f.ui.onWorkItemUpdated({ id: 'cpu', progress: 20 });
    await until(() => f.calls() === 1);
    assert.equal(f.ui._selfActive.length, 2); assert.deepEqual(f.ui._archiveSlotUsage, { used: 2, limit: 3 });
    f.set({ nodeId: 'local', localActiveJobs: [{ jobId: 'cpu', deviceId: 'cpu', progress: 25 }], archiveSlotUsage: { used: 1, limit: 3 } });
    f.ui.onWorkItemUpdated({ id: 'apple', status: 'Completed', progress: 100 });
    await until(() => f.calls() === 2);
    assert.deepEqual(f.ui._selfActive.map(j => j.deviceId), ['cpu']);
    assert.equal(f.ui._archiveSlotUsage.used, 1);
});

test('frequent local updates coalesce without indefinitely postponing a status request', async () => {
    const f = await fixture();
    f.ui.onWorkItemUpdated({ id: 'a' });
    const timer = f.ui._refreshHandle;
    for (let i = 0; i < 20; i++) f.ui.onWorkItemUpdated({ id: `item-${i}` });
    assert.equal(f.ui._refreshHandle, timer);
    await until(() => f.calls() === 1);
});

test('remote events stay remote while a local-node assignment refreshes the self card', async () => {
    const f = await fixture();
    f.ui.onWorkItemUpdated({ id: 'remote', assignedNodeId: 'another-node' });
    assert.equal(f.ui._refreshHandle, undefined);
    f.ui.onWorkItemUpdated({ id: 'local', assignedNodeId: 'local' });
    await until(() => f.calls() === 1);
});

test('unmounted cards do not add background status polling', async () => {
    const f = await fixture(); f.mount(false);
    f.ui.onWorkItemUpdated({ id: 'local' });
    await until(() => f.ui._refreshHandle === null);
    assert.equal(f.calls(), 0);
});

test('archive cards show one shared limit and both Apple and CPU active counts', async () => {
    const { ui } = await fixture();
    ui._nodeSettings = { local: { deviceSettings: { apple: { maxConcurrency: 1 } } } };
    const active = [{ deviceId: 'apple' }, { deviceId: 'cpu' }];
    const html = ui._renderDeviceSummary('local', { devices: [{ deviceId: 'apple', defaultConcurrency: 1 }, { deviceId: 'cpu', defaultConcurrency: 1 }] }, active, { used: 2, limit: 3 });
    assert.match(html, /归档并行 2\/3/); assert.match(html, />APPLE 1</); assert.match(html, />CPU 1</);
    assert.doesNotMatch(html, /APPLE 1\/1|CPU 1\/1|2\/6/);
    const beforeHardwareDetection = ui._renderDeviceSummary('local', null, [{ deviceId: 'cpu' }], { used: 1, limit: 3 });
    assert.match(beforeHardwareDetection, /归档并行 1\/3/); assert.match(beforeHardwareDetection, />CPU 1</);
});

test('ordinary and remote cards retain per-device overrides and include active CPU fallback', async () => {
    const { ui } = await fixture();
    ui._nodeSettings = { remote: { deviceSettings: { apple: { maxConcurrency: 2 } } } };
    const html = ui._renderDeviceSummary('remote', { devices: [{ deviceId: 'apple', defaultConcurrency: 1 }, { deviceId: 'cpu', defaultConcurrency: 4 }] }, [{ deviceId: 'apple' }, { deviceId: 'cpu' }]);
    assert.match(html, /APPLE 1\/2/); assert.match(html, /CPU 1\/4/); assert.doesNotMatch(html, /归档并行/);
});
