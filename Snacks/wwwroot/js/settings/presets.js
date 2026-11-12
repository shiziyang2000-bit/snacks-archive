/**
 * Complete processing presets. Built-in definitions and the saved/current
 * selection come from the server so the UI never guesses which profile is
 * active. Applying a preset is one server-side save of encoder + archive
 * settings; the returned snapshot is then reflected in the forms without
 * firing their ordinary auto-save handlers.
 */

import { presetsApi } from '../api.js';
import { escapeHtml } from '../utils/dom.js';
import { streamDownload } from '../utils/download.js';
import { showConfirmModal } from '../utils/modal-controller.js';
import {
    applyEncoderOptionsToForm,
    getEncoderOptions,
    markEncoderOptionsPersisted,
} from './encoder-form.js';
import { readArchiveSettingsFromForm } from '../archive/archive-ui.js';

const PREFIX = 'settings';
const MAIN_PRESET_KEY = 'my-archive';
const BUILTIN_NAMES = {
    'my-archive': 'My Archive Plan',
    'space-saver': 'Space Saver',
    balanced: 'Balanced',
    'quality-first': 'Quality First',
    'max-compat': 'Max Compatibility',
    'ipod-classic': 'iPod Classic',
};

// Presentation for the five existing profiles. Their complete definitions,
// values and active state still come from /api/settings/presets.
const ORIGINAL_PRESET_PRESENTATION = {
    'space-saver': {
        icon: 'fa-piggy-bank',
        desc: 'Small files for a media library, with slower encodes.',
        spec: 'H.265 · MKV · 2500 kbps · slow',
    },
    balanced: {
        icon: 'fa-scale-balanced',
        desc: 'The original Snacks defaults for a general-purpose library.',
        spec: 'H.265 · MKV · 3500 kbps · medium',
    },
    'quality-first': {
        icon: 'fa-gem',
        desc: 'Near-transparent quality for home-theater setups. Bigger files.',
        spec: 'H.265 · MKV · 6000 kbps · slow',
    },
    'max-compat': {
        icon: 'fa-plug',
        desc: 'H.264 in MP4 for broad device compatibility.',
        spec: 'H.264 · MP4 · 4500 kbps · medium',
    },
    'ipod-classic': {
        icon: 'fa-music',
        desc: '640×480 H.264 Baseline for older iPods. AAC stereo at 48 kHz.',
        spec: 'H.264 Baseline L3.0 · MP4 · 640×480 · 1500 kbps · AAC 160k',
    },
};

const OPTION_GROUPS = [
    {
        title: 'Video defaults',
        fields: [
            ['Format', 'Output format'], ['Codec', 'Video codec'], ['Encoder', 'Video encoder'],
            ['HardwareAcceleration', 'Hardware acceleration'], ['TargetBitrate', 'Original-mode target bitrate'],
            ['StrictBitrate', 'Strict bitrate control'], ['FourKBitrateMultiplier', 'Original-mode 4K bitrate multiplier'],
            ['Skip4K', 'Skip 4K videos'], ['SkipPercentAboveTarget', 'Skip threshold'],
            ['FfmpegQualityPreset', 'FFmpeg preset'], ['DownscalePolicy', 'Downscale policy'],
            ['DownscaleTarget', 'Downscale target'], ['FixedFrameSize', 'Fixed frame size'],
            ['MaxFrameRate', 'Maximum frame rate'], ['TonemapHdrToSdr', 'Tonemap HDR to SDR'],
            ['RemoveBlackBorders', 'Remove black borders'], ['VideoProfile', 'Video profile'], ['VideoLevel', 'Video level'],
        ],
    },
    {
        title: 'Audio defaults',
        fields: [
            ['TwoChannelAudio', 'Two-channel audio'], ['AudioCodec', 'Legacy audio codec default'],
            ['AudioBitrateKbps', 'Legacy audio bitrate default'], ['AudioLanguagesToKeep', 'Audio languages to keep'],
            ['KeepOriginalLanguage', 'Keep original language'], ['OriginalLanguageProvider', 'Original-language provider'],
            ['PreserveOriginalAudio', 'Preserve original audio tracks'], ['AudioOutputs', 'Additional audio outputs'],
            ['AutoSetDefaultTrack', 'Auto-set default track'],
        ],
    },
    {
        title: 'Subtitle defaults',
        fields: [
            ['SubtitleLanguagesToKeep', 'Subtitle languages to keep'],
            ['ExtractSubtitlesToSidecar', 'Extract subtitles to sidecar files'],
            ['SidecarSubtitleFormat', 'Sidecar subtitle format'],
            ['ConvertImageSubtitlesToSrt', 'Convert image subtitles to SRT'],
            ['PassThroughImageSubtitlesMkv', 'Pass through image subtitles'],
            ['ExcludeSdhSubtitles', 'Exclude hearing-impaired subtitles'],
        ],
    },
    {
        title: 'Queue and file defaults',
        fields: [
            ['EncodingMode', 'Encoding mode'], ['MuxStreams', 'Mux streams'], ['DeleteOriginalFile', 'Replace original files'],
            ['RetryOnFail', 'Retry on failure'], ['QueueNewestFirst', 'Newest files first'],
            ['EncodingLogRetentionDays', 'Encoding log retention (days)'], ['VerifyFilesPerDay', 'Files to verify per day'],
        ],
    },
    {
        title: 'Music defaults',
        fields: [
            ['Format', 'Music format'], ['Codec', 'Music codec'], ['BitrateKbps', 'Music bitrate'],
            ['VbrQuality', 'Music VBR quality'], ['SampleRatePolicy', 'Music sample rate'],
            ['ChannelPolicy', 'Music channel layout'], ['SkipIfAlreadyTargetCodec', 'Skip matching music codec'],
            ['BitrateMatchTolerancePct', 'Music bitrate tolerance'], ['CopyMetadataAndArt', 'Copy music metadata and art'],
            ['DeleteOriginalFile', 'Replace original music files'], ['MasterMusicConcurrency', 'Music concurrency'],
            ['DispatchToCluster', 'Dispatch music to cluster'],
        ],
        nested: 'Music',
    },
    {
        title: 'Advanced video defaults',
        fields: [
            ['Version', 'Version'], ['Enabled', 'Enabled'], ['Profiles', 'Profiles'],
            ['Rules', 'Rules'], ['DefaultAction', 'Default action'], ['DefaultProfileId', 'Default profile'],
        ],
        nested: 'AdvancedVideo',
    },
];

let builtins = [];
let userPresets = [];
let currentPreset = null;
let defaultKey = MAIN_PRESET_KEY;
let encoderRestored = false;
let archiveRestored = false;
let catalogLoaded = false;

const t = (source, ...args) => window.SnacksI18n?.t?.(source, ...args) ?? source;
const CUSTOM_MODE_NOTICE = 'Custom Archive preserves the source container and filename extension. Eligible videos are encoded as H.265 under their source-codec bitrate rules; other files are left unchanged or skipped. The output format and encoding settings below apply only to Original mode.';
const ORIGINAL_MODE_NOTICE = 'Original mode is active: the output format, codec, and processing settings below apply to Original mode. Custom Archive source-container and H.265 rules are inactive.';
const UNKNOWN_MODE_NOTICE = 'Archive processing mode is not confirmed. Save or reload archive settings to verify which settings apply.';
const FOLDER_WATCH_NOTICE = 'Applying a preset does not enable or change watched-folder monitoring.';

function valueFor(object, key) {
    if (!object || typeof object !== 'object') return undefined;
    if (Object.prototype.hasOwnProperty.call(object, key)) return object[key];
    const camel = key.charAt(0).toLowerCase() + key.slice(1);
    if (Object.prototype.hasOwnProperty.call(object, camel)) return object[camel];
    const found = Object.keys(object).find(candidate => candidate.toLowerCase() === key.toLowerCase());
    return found ? object[found] : undefined;
}

function clone(value) {
    return value == null ? value : JSON.parse(JSON.stringify(value));
}

function canonicalKey(key) {
    return key.charAt(0).toLowerCase() + key.slice(1);
}

function mergeSnapshot(base, overlay) {
    if (!overlay || typeof overlay !== 'object' || Array.isArray(overlay)) return clone(overlay);
    const result = {};
    for (const [key, value] of Object.entries(base && typeof base === 'object' && !Array.isArray(base) ? base : {}))
        result[canonicalKey(key)] = clone(value);
    for (const [key, value] of Object.entries(overlay)) {
        const normalized = canonicalKey(key);
        const existingKey = Object.keys(result).find(candidate => candidate.toLowerCase() === normalized.toLowerCase()) ?? normalized;
        result[existingKey] = value && typeof value === 'object' && !Array.isArray(value)
            ? mergeSnapshot(result[existingKey], value)
            : clone(value);
    }
    return result;
}

function ignoredPaths() {
    const paths = currentPreset?.scope?.ignoredForMatching ?? [];
    return new Set(paths.map(path => String(path).toLowerCase()));
}

const PATH_FIELD_NAMES = new Set([
    'outputdirectory', 'encodedirectory', 'backupdirectory', 'cachedirectory',
    'hardwaredevicepath', 'devicepath',
]);

function normalizeForMatch(value, path, ignored) {
    const normalizedPath = path.map(part => String(part).toLowerCase()).join('.');
    const last = path.length ? String(path.at(-1)).toLowerCase() : '';
    if (ignored.has(normalizedPath) || ignored.has(normalizedPath.replace(/^options\./, ''))
        || ignored.has(normalizedPath.replace(/^archive\./, '')) || PATH_FIELD_NAMES.has(last)
        || last === 'revision') return undefined;

    if (Array.isArray(value)) return value.map((item, index) => normalizeForMatch(item, [...path, String(index)], ignored));
    if (value && typeof value === 'object') {
        const result = {};
        for (const key of Object.keys(value).sort((a, b) => a.localeCompare(b))) {
            const normalizedKey = canonicalKey(key);
            const item = normalizeForMatch(value[key], [...path, normalizedKey], ignored);
            if (item !== undefined) result[normalizedKey] = item;
        }
        return result;
    }
    if (value === '') return null;
    return value;
}

function hasUnsavedFormChanges() {
    if (!currentPreset || !encoderRestored || !archiveRestored) return false;
    try {
        const savedOptions = mergeSnapshot({}, currentPreset.options ?? {});
        const savedArchive = mergeSnapshot({}, currentPreset.archive ?? {});
        const formOptions = mergeSnapshot(savedOptions, getEncoderOptions(PREFIX, false));
        const formArchive = mergeSnapshot(savedArchive, readArchiveSettingsFromForm(savedArchive));
        const ignored = ignoredPaths();
        return JSON.stringify(normalizeForMatch({ options: savedOptions, archive: savedArchive }, [], ignored))
            !== JSON.stringify(normalizeForMatch({ options: formOptions, archive: formArchive }, [], ignored));
    } catch {
        // An invalid value in an open editor is still an unapplied change.
        return true;
    }
}

function effectiveActiveKey() {
    if (hasUnsavedFormChanges()) return null;
    return currentPreset?.activeKey ?? null;
}

function setBadge(card, text, kind = 'secondary') {
    const node = card?.querySelector('[data-preset-status]');
    if (!node) return;
    node.textContent = t(text);
    node.className = `badge bg-${kind}`;
    node.hidden = !text;
}

function syncPresetCards() {
    const activeKey = effectiveActiveKey();
    const archiveMode = String(currentPreset?.archive?.mode ?? '').toLowerCase();
    const localDirty = hasUnsavedFormChanges();

    document.querySelectorAll('[data-preset-key]').forEach(card => {
        const key = card.dataset.presetKey;
        const isMain = key === MAIN_PRESET_KEY;
        const modeMatches = isMain ? archiveMode === 'custom' : archiveMode === 'original';
        const active = !localDirty && modeMatches && activeKey === key && !currentPreset?.modified;
        card.classList.toggle('active', active);
        card.setAttribute('aria-pressed', active ? 'true' : 'false');
        if (active) setBadge(card, 'In use', 'success');
        else if (currentPreset?.selectedKey === key && (currentPreset.modified || localDirty))
            setBadge(card, localDirty ? 'Unsaved changes' : 'Modified settings', 'warning');
        else setBadge(card, isMain ? 'Complete plan' : 'Original mode', 'secondary');
    });
    document.querySelector('[data-main-preset-card]')?.classList.toggle(
        'active', !localDirty && activeKey === MAIN_PRESET_KEY && archiveMode === 'custom' && !currentPreset?.modified);

    const summary = document.getElementById('presetCurrentSummary');
    if (summary) {
        const builtInName = Object.prototype.hasOwnProperty.call(BUILTIN_NAMES, currentPreset?.selectedKey);
        const status = localDirty ? 'Unsaved changes'
            : currentPreset?.modified ? 'Modified settings'
                : activeKey ? 'In use' : 'Custom settings';
        const nameMarkup = currentPreset?.selectedName && !builtInName
            ? `<span data-user-content>${escapeHtml(currentPreset.selectedName)}</span>`
            : escapeHtml(t(BUILTIN_NAMES[currentPreset?.selectedKey]
                ?? (currentPreset?.selectedKey === defaultKey ? BUILTIN_NAMES[MAIN_PRESET_KEY] : 'Custom settings')));
        summary.innerHTML = `<span>${escapeHtml(t('Current plan'))}</span>: ${nameMarkup} · <span>${escapeHtml(t(status))}</span>`;
    }
}

function getMainDefinition() {
    return builtins.find(preset => preset.key === MAIN_PRESET_KEY)
        ?? currentPreset
        ?? { key: MAIN_PRESET_KEY, name: 'My Archive Plan', options: {}, archive: {} };
}

function optionValue(options, group, key) {
    const nested = group.nested ? valueFor(options, group.nested) : options;
    return valueFor(nested, key);
}

function displayOption(value, key) {
    if (value === undefined) return '';
    if (value === null || value === '') return t('Default');
    if (typeof value === 'boolean') return t(value ? 'On' : 'Off');
    if (Array.isArray(value)) {
        if (!value.length) return t('None');
        return value.map(item => typeof item === 'object' ? JSON.stringify(item) : String(item)).join(', ');
    }
    if (typeof value === 'object') return JSON.stringify(value);
    if (key === 'TargetBitrate' || key === 'AudioBitrateKbps' || key === 'BitrateKbps') return `${value} kbps`;
    if (key === 'FourKBitrateMultiplier') return `${value}×`;
    if (key === 'MaxFrameRate' && Number(value) === 0) return t('No limit');
    return String(value);
}

function renderOptionDefaults(options) {
    return OPTION_GROUPS.map(group => {
        const rows = group.fields.map(([key, label]) => {
            const value = optionValue(options, group, key);
            if (value === undefined) return '';
            const formatted = escapeHtml(displayOption(value, key));
            return `<tr><th scope="row" class="fw-normal">${escapeHtml(t(label))}</th><td><code>${formatted}</code></td></tr>`;
        }).filter(Boolean).join('');
        return rows ? `<div class="mb-3"><h6 class="small fw-bold">${escapeHtml(t(group.title))}</h6><div class="table-responsive"><table class="table table-sm mb-0"><tbody>${rows}</tbody></table></div></div>` : '';
    }).join('');
}

function archiveSummaryRows(archive = {}) {
    const location = t(archive.backupLocation === 'sameVolume' ? 'Same volume as the source' : 'Selected folder');
    const values = [
        ['Target video bitrate cap (Mbps)', `${archive.targetMbps ?? 20} Mbps`],
        ['Output mode', t(archive.outputMode === 'inPlace' ? 'Archive in place' : 'Save to another folder')],
        ['Keep an original backup', t(archive.backupEnabled === false ? 'Off' : 'On')],
        ['Backup location', location],
        ['Backup reminder days', archive.backupReminderDays ?? 3],
        ['Sample cache capacity', `${archive.cacheLimitGb ?? 5} GB`],
        ['Concurrent transcodes', archive.concurrency ?? 10],
    ];
    return values.map(([label, value]) => `<tr><th scope="row" class="fw-normal">${escapeHtml(t(label))}</th><td><code>${escapeHtml(String(value))}</code></td></tr>`).join('');
}

function renderStringList(title, items) {
    if (!Array.isArray(items) || !items.length) return '';
    return `<div class="mb-3"><h6 class="small fw-bold">${escapeHtml(title)}</h6><ul class="small mb-0">${items.map(item => `<li>${escapeHtml(String(item))}</li>`).join('')}</ul></div>`;
}

function renderMainPlan() {
    const root = document.getElementById('presetMainPlan');
    if (!root) return;
    const preset = getMainDefinition();
    const requirements = preset.requirements ?? currentPreset?.requirements ?? {};
    const scope = preset.scope ?? currentPreset?.scope ?? {};
    const items = Array.isArray(requirements.items) ? requirements.items : [];
    const rules = items.map(item => `<li class="mb-2"><strong>${escapeHtml(String(item.title ?? ''))}</strong><br><span class="text-muted">${escapeHtml(String(item.description ?? ''))}</span></li>`).join('');
    const defaults = renderOptionDefaults(preset.options ?? currentPreset?.options ?? {});
    const active = effectiveActiveKey() === MAIN_PRESET_KEY && currentPreset?.archive?.mode === 'custom' && !currentPreset?.modified;

    root.innerHTML = `
        <div class="card preset-card preset-card-main ${active ? 'active' : ''}" data-main-preset-card>
            <div class="card-body">
                <div class="d-flex justify-content-between align-items-start gap-3 flex-wrap">
                    <button type="button" class="btn p-0 text-start flex-grow-1" data-preset-key="${MAIN_PRESET_KEY}" aria-pressed="${active ? 'true' : 'false'}">
                        <span class="d-flex align-items-center gap-2 flex-wrap">
                            <span class="h5 mb-0"><i class="fas fa-shield-halved me-2 text-primary"></i>${escapeHtml(t(BUILTIN_NAMES[MAIN_PRESET_KEY]))}</span>
                            <span class="badge bg-secondary" data-preset-status></span>
                        </span>
                        <span class="badge bg-info text-dark">${escapeHtml(t('Preserve source container · H.265'))}</span>
                        <span class="d-block small text-muted mt-2">${escapeHtml(t('A source-container archive plan with a manual preview, protected originals, and codec-aware H.265 encoding.'))}</span>
                    </button>
                    <span class="badge bg-primary">Default plan</span>
                </div>
                <div id="presetCurrentSummary" class="small text-muted mt-3" aria-live="polite"></div>
                <details class="mt-3">
                    <summary class="small fw-semibold">Show the complete plan and saved defaults</summary>
                    <div class="pt-3">
                        <h6 class="small fw-bold">Archive settings</h6>
                        <div class="table-responsive"><table class="table table-sm mb-3"><tbody>${archiveSummaryRows(preset.archive ?? currentPreset?.archive ?? {})}</tbody></table></div>
                        ${rules ? `<h6 class="small fw-bold">Video and archive rules</h6><ul class="small ps-3">${rules}</ul>` : ''}
                        ${renderStringList('Included in this preset', scope.included)}
                        ${renderStringList('Kept from this installation', scope.preserved)}
                        ${defaults ? `<h6 class="small fw-bold mt-3">Original-mode defaults stored in this preset</h6>${defaults}` : ''}
                    </div>
                </details>
            </div>
        </div>`;
    syncPresetCards();
}

function renderOriginalPresets() {
    const row = document.getElementById('presetBuiltinRow');
    if (!row) return;
    const originals = builtins.filter(preset => preset.key !== MAIN_PRESET_KEY);
    row.innerHTML = originals.map(preset => {
        const presentation = ORIGINAL_PRESET_PRESENTATION[preset.key] ?? {};
        const desc = preset.description ?? preset.desc ?? presentation.desc ?? '';
        const spec = preset.spec ?? presentation.spec ?? '';
        const icon = presentation.icon ?? preset.icon ?? 'fa-sliders';
        return `
            <div class="col-12 col-md-6 col-xl-3">
                <button type="button" class="card w-100 h-100 text-start preset-card" data-preset-key="${escapeHtml(preset.key)}" aria-pressed="false">
                    <div class="card-body p-2">
                        <div class="d-flex align-items-center justify-content-between gap-2">
                            <span class="fw-bold"><i class="fas ${escapeHtml(icon)} me-2 text-primary"></i>${escapeHtml(BUILTIN_NAMES[preset.key] ? t(BUILTIN_NAMES[preset.key]) : String(preset.name ?? preset.key))}</span>
                            <span class="badge bg-secondary" data-preset-status>Original mode</span>
                        </div>
                        <div class="small text-muted mt-1" style="min-height: 2.5em;">${escapeHtml(t(String(desc)))}</div>
                        <div class="small text-info mt-1">${escapeHtml(String(spec))}</div>
                    </div>
                </button>
            </div>`;
    }).join('');
    syncPresetCards();
}

function renderUserPresets() {
    const section = document.getElementById('presetUserSection');
    const list = document.getElementById('presetUserList');
    if (!section || !list) return;
    section.style.display = userPresets.length ? '' : 'none';
    list.innerHTML = userPresets.map(preset => `
        <div class="btn-group btn-group-sm" role="group" data-user-preset="${escapeHtml(preset.name)}">
            <button type="button" class="btn btn-outline-primary" data-preset-action="apply" title="Apply this complete preset">
                <i class="fas fa-bookmark me-1"></i><span data-user-content>${escapeHtml(preset.name)}</span>
            </button>
            <span class="btn btn-outline-secondary disabled" aria-label="${preset.legacy || !preset.archive ? 'Legacy preset, Original mode only' : 'Complete encoder and archive snapshot'}">
                ${preset.legacy || !preset.archive ? 'Legacy · Original mode only' : 'Complete snapshot'}
            </span>
            <button type="button" class="btn btn-outline-secondary" data-preset-action="export" title="Download as a shareable file">
                <i class="fas fa-file-export"></i>
            </button>
            <button type="button" class="btn btn-outline-danger" data-preset-action="delete" title="Delete this preset">
                <i class="fas fa-trash"></i>
            </button>
        </div>`).join('');
}

function renderAll() {
    renderGeneralModeNotice();
    renderMainPlan();
    renderOriginalPresets();
    renderUserPresets();
    syncPresetCards();
}

function renderGeneralModeNotice() {
    const notice = document.querySelector('#generalTab > .alert.alert-info.py-2.small.mb-4');
    if (!notice) return;

    const mode = document.getElementById('archiveMode')?.value;
    const message = mode === 'custom' ? CUSTOM_MODE_NOTICE
        : mode === 'original' ? ORIGINAL_MODE_NOTICE
            : UNKNOWN_MODE_NOTICE;
    const icon = notice.querySelector('i')?.cloneNode(true);
    notice.replaceChildren();
    if (icon) notice.append(icon);
    notice.append(document.createTextNode(' '), document.createTextNode(`${t(message)} ${t(FOLDER_WATCH_NOTICE)}`));
}

function publishCurrent() {
    document.dispatchEvent(new CustomEvent('snacks:preset-current-changed', { detail: { current: currentPreset } }));
}

async function loadCatalog() {
    try {
        const data = await presetsApi.list();
        userPresets = Array.isArray(data.presets) ? data.presets : [];
        builtins = Array.isArray(data.builtins) ? data.builtins : [];
        defaultKey = data.defaultKey ?? MAIN_PRESET_KEY;
        currentPreset = data.current ?? null;
        if (!currentPreset) currentPreset = await presetsApi.current().catch(() => null);
        if (!builtins.some(preset => preset.key === MAIN_PRESET_KEY)) {
            const mainDefinition = await presetsApi.definition(MAIN_PRESET_KEY).catch(() => null);
            if (mainDefinition) builtins.unshift(mainDefinition);
        }
        catalogLoaded = true;
    } catch (error) {
        console.error('Failed to load processing presets', error);
        userPresets = [];
        builtins = [];
        currentPreset = null;
    }
    renderAll();
    publishCurrent();
}

async function refreshCurrent() {
    try {
        currentPreset = await presetsApi.current();
        renderAll();
        publishCurrent();
    } catch (error) {
        console.error('Failed to refresh current preset state', error);
        syncPresetCards();
    }
}

function portableOptions(options) {
    const result = clone(options ?? {});
    const redactPaths = object => {
        if (!object || typeof object !== 'object') return;
        for (const key of Object.keys(object)) {
            if (key.toLowerCase() === '_envlocked') delete object[key];
            else if (PATH_FIELD_NAMES.has(key.toLowerCase())) object[key] = null;
            else if (object[key] && typeof object[key] === 'object') redactPaths(object[key]);
        }
    };
    redactPaths(result);
    return result;
}

function portableArchive(archive) {
    const result = clone(archive ?? {});
    for (const key of ['outputDirectory', 'backupDirectory', 'cacheDirectory', 'revision']) delete result[key];
    return result;
}

async function applyPreset(selection, label) {
    try {
        const response = await presetsApi.apply(selection);
        const current = response?.current ?? await presetsApi.current();
        if (!current?.options || !current?.archive)
            throw new Error(t('The saved preset did not return its complete settings.'));

        currentPreset = current;
        applyEncoderOptionsToForm(PREFIX, current.options);
        markEncoderOptionsPersisted(PREFIX);
        archiveRestored = true;
        document.dispatchEvent(new CustomEvent('snacks:archive-preset-applied', { detail: { archive: current.archive } }));
        renderAll();
        publishCurrent();
        document.dispatchEvent(new CustomEvent('snacks:preset-applied', { detail: { current } }));
        showToast(t('Applied preset'), 'success');
    } catch (error) {
        showToast(`${t('Failed to apply preset')}: ${error.message}`, 'danger');
    }
}

async function confirmAndApplyPreset(selection, label) {
    const displayLabel = selection?.key && BUILTIN_NAMES[selection.key]
        ? t(BUILTIN_NAMES[selection.key])
        : label;
    const ok = await showConfirmModal(
        'Apply complete preset',
        `Apply "${escapeHtml(displayLabel)}"? This saves the full encoder and archive settings together.`,
        'Apply');
    if (ok) await applyPreset(selection, displayLabel);
}

async function onSaveConfirm() {
    const input = document.getElementById('presetNameInput');
    const name = (input?.value || '').trim();
    if (!name) { showToast(t('Give the preset a name first'), 'warning'); return; }

    try {
        const fullOptions = mergeSnapshot(currentPreset?.options ?? {}, getEncoderOptions(PREFIX, false));
        const archive = readArchiveSettingsFromForm(currentPreset?.archive ?? {});
        await presetsApi.save(name, portableOptions(fullOptions), portableArchive(archive));
        document.getElementById('presetSaveRow').style.display = 'none';
        input.value = '';
        showToast(t('Saved preset'), 'success');
        await loadCatalog();
    } catch (error) {
        showToast(`${t('Failed to save preset')}: ${error.message}`, 'danger');
    }
}

async function onImportFile(file) {
    try {
        const parsed = JSON.parse(await file.text());
        await presetsApi.importFile(parsed);
        showToast(t('Imported preset'), 'success');
        await loadCatalog();
    } catch (error) {
        showToast(`${t('Import failed')}: ${error.message || t('not a valid preset file')}`, 'danger');
    }
}

async function onUserPresetAction(group, action) {
    const name = group.dataset.userPreset;
    const preset = userPresets.find(item => item.name === name);
    if (!preset) return;

    if (action === 'apply') {
        await confirmAndApplyPreset({ name: preset.name }, preset.name);
    } else if (action === 'export') {
        const button = group.querySelector('[data-preset-action="export"]');
        try { await streamDownload(presetsApi.exportUrl(name), button, `${name}.snacks-preset.json`); }
        catch { /* streamDownload already toasts */ }
    } else if (action === 'delete') {
        const ok = await showConfirmModal('Delete Preset', `Delete the preset "${escapeHtml(name)}"? This cannot be undone.`, 'Delete');
        if (!ok) return;
        try {
            await presetsApi.remove(name);
            showToast(t('Deleted preset'), 'success');
            await loadCatalog();
        } catch (error) {
            showToast(`${t('Failed to delete preset')}: ${error.message}`, 'danger');
        }
    }
}

function markFormDirty() {
    if (!catalogLoaded) return;
    window.clearTimeout(markFormDirty.timer);
    markFormDirty.timer = window.setTimeout(syncPresetCards, 0);
}

/** Wires complete presets and listens for settings saves across all tabs. */
export function initPresets() {
    const row = document.getElementById('presetBuiltinRow');
    if (!row) return;
    renderGeneralModeNotice();
    document.getElementById('archiveMode')?.addEventListener('change', renderGeneralModeNotice);
    window.addEventListener('snacks:archive-mode-changed', renderGeneralModeNotice);
    loadCatalog();

    document.getElementById('presetMainPlan')?.addEventListener('click', e => {
        const card = e.target.closest('[data-preset-key]');
        if (card?.dataset.presetKey !== MAIN_PRESET_KEY) return;
        confirmAndApplyPreset({ key: MAIN_PRESET_KEY }, BUILTIN_NAMES[MAIN_PRESET_KEY]);
    });
    row.addEventListener('click', e => {
        const card = e.target.closest('[data-preset-key]');
        if (!card) return;
        const preset = builtins.find(item => item.key === card.dataset.presetKey);
        if (preset) confirmAndApplyPreset({ key: preset.key }, BUILTIN_NAMES[preset.key] ?? preset.name ?? preset.key);
    });

    document.getElementById('presetUserList')?.addEventListener('click', e => {
        const button = e.target.closest('[data-preset-action]');
        const group = e.target.closest('[data-user-preset]');
        if (button && group) onUserPresetAction(group, button.dataset.presetAction);
    });

    document.getElementById('presetSaveBtn')?.addEventListener('click', () => {
        const saveRow = document.getElementById('presetSaveRow');
        saveRow.style.display = saveRow.style.display === 'none' ? 'flex' : 'none';
        if (saveRow.style.display !== 'none') document.getElementById('presetNameInput')?.focus();
    });
    document.getElementById('presetSaveConfirmBtn')?.addEventListener('click', onSaveConfirm);
    document.getElementById('presetSaveCancelBtn')?.addEventListener('click', () => {
        document.getElementById('presetSaveRow').style.display = 'none';
    });
    document.getElementById('presetNameInput')?.addEventListener('keydown', e => {
        if (e.key === 'Enter') { e.preventDefault(); onSaveConfirm(); }
    });
    document.getElementById('presetImportBtn')?.addEventListener('click', () => document.getElementById('presetImportFile')?.click());
    document.getElementById('presetImportFile')?.addEventListener('change', e => {
        const file = e.target.files?.[0];
        e.target.value = '';
        if (file) onImportFile(file);
    });

    document.getElementById('settingsModal')?.addEventListener('input', markFormDirty);
    document.getElementById('settingsModal')?.addEventListener('change', markFormDirty);
    document.addEventListener('snacks:settings-restored', e => {
        if (e.detail?.prefix && e.detail.prefix !== PREFIX) return;
        encoderRestored = true;
        refreshCurrent();
    });
    document.addEventListener('snacks:archive-settings-restored', () => {
        archiveRestored = true;
        refreshCurrent();
    });
    document.addEventListener('snacks:encoder-settings-saved', refreshCurrent);
    document.addEventListener('snacks:archive-settings-saved', refreshCurrent);
    document.addEventListener('snacks:languagechange', renderAll);
    document.getElementById('openSettingsBtn')?.addEventListener('click', refreshCurrent);
}
