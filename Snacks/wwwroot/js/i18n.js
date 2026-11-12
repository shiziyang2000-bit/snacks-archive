/**
 * Small, same-origin localization layer for the Razor + native-module UI.
 * English remains the source language; the Simplified Chinese resource only
 * changes visible UI strings. Input values, file/path labels, logs, and raw
 * error details are deliberately left alone.
 */
(() => {
    'use strict';

    const STORAGE_KEY = 'snacks.uiLanguage';
    const LANGUAGES = new Set(['zh-CN', 'en']);
    const originals = new WeakMap();
    const applied = new WeakMap();
    const attributeOriginals = new WeakMap();
    const attributeApplied = new WeakMap();
    const ENGLISH_SOURCE_PREFIX = '__en__:';
    const observableAttributes = ['title', 'placeholder', 'aria-label', 'alt'];
    let translations = Object.create(null);
    let rawTranslations = Object.create(null);
    let englishSourceTranslations = Object.create(null);
    let englishSourceTemplates = [];
    let templates = [];
    let prefixes = [];
    let language = readLanguage();
    let observer = null;

    function readLanguage() {
        try {
            const saved = localStorage.getItem(STORAGE_KEY);
            return LANGUAGES.has(saved) ? saved : 'zh-CN';
        } catch {
            return 'zh-CN';
        }
    }

    function normalize(text) {
        return String(text).replace(/\s+/g, ' ').trim();
    }

    function interpolate(template, args) {
        if (!args.length) return template;
        return template.replace(/\{(\d+)\}/g, (match, index) =>
            Number(index) < args.length ? String(args[Number(index)]) : match,
        );
    }

    function lookup(source, args = []) {
        const key = normalize(source);
        if (language === 'en') {
            const english = englishSourceTranslations[key];
            if (english !== undefined) return interpolate(english, args);
            for (const item of englishSourceTemplates) {
                // Match aliases against the original string so dynamic suffixes
                // such as filesystem errors and multiline tool output keep their
                // spacing and line breaks when they are passed through {0}.
                const match = item.pattern.exec(String(source));
                if (!match) continue;
                return item.value.replace(/\{(\d+)\}/g, (placeholder, index) =>
                    match[item.slots[index]] ?? placeholder,
                );
            }
            return interpolate(source, args);
        }
        if (Object.prototype.hasOwnProperty.call(translations, key))
            return interpolate(translations[key], args);

        for (const item of templates) {
            const match = item.pattern.exec(key);
            if (!match) continue;
            return item.value.replace(/\{(\d+)\}/g, (placeholder, index) =>
                match[item.slots[index]] ?? placeholder,
            );
        }

        for (const item of prefixes) {
            if (key.startsWith(item.source))
                return item.value + key.slice(item.source.length);
        }
        return source;
    }

    function t(source, ...args) {
        return lookup(String(source ?? ''), args);
    }

    function escapeRegex(text) {
        return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    }

    function compileTemplate(source, value) {
        const slots = Object.create(null);
        let cursor = 0;
        let group = 1;
        let regex = '^';
        for (const match of source.matchAll(/\{(\d+)\}/g)) {
            regex += escapeRegex(source.slice(cursor, match.index)) + '([\\s\\S]+?)';
            if (slots[match[1]] === undefined) slots[match[1]] = group;
            group++;
            cursor = match.index + match[0].length;
        }
        regex += escapeRegex(source.slice(cursor)) + '$';
        return { pattern: new RegExp(regex), value, slots };
    }

    function compileRules() {
        templates = [];
        prefixes = [];
        englishSourceTemplates = [];
        for (const [source, value] of Object.entries(rawTranslations)) {
            if (source.startsWith(ENGLISH_SOURCE_PREFIX)) {
                const aliasSource = normalize(source.slice(ENGLISH_SOURCE_PREFIX.length));
                if (aliasSource.includes('{')) englishSourceTemplates.push(compileTemplate(aliasSource, value));
                continue;
            }
            const normalizedSource = normalize(source);
            if (normalizedSource.includes('{')) {
                templates.push(compileTemplate(normalizedSource, value));
            }
            if (source.endsWith(': ')) {
                const prefix = `${normalize(source.replace(/\s+$/, ''))} `;
                prefixes.push({ source: prefix, value });
            }
        }
        templates.sort((a, b) => b.pattern.source.length - a.pattern.source.length);
        prefixes.sort((a, b) => b.source.length - a.source.length);
    }

    function isProtected(node) {
        const parent = node.parentElement;
        if (!parent) return true;
        if (parent.isContentEditable) return true;
        if (parent.closest('script, style, noscript, textarea, [data-i18n-skip], [data-user-content]')) return true;
        if (parent.closest('pre, code, samp, kbd, #logContent, .log-content, .log-entry, .raw-log')) return true;
        if (parent.closest('.dash-file, .dash-filename, .fname, .file-name, .filename, .source-path, .path-value, .file-item strong, .cluster-card strong, .codec-pill, .device-chip, [data-node-name]')) return true;
        if (parent.closest('.chip-list .chip, [data-template-key], [data-file-name], [data-path-value]')) return true;
        if (parent.closest('.work-item strong')) return true;
        return false;
    }

    function localizeTextNode(node) {
        if (isProtected(node)) return;
        const current = node.nodeValue;

        let source = originals.get(node);
        if (source === undefined || current !== applied.get(node)) {
            source = current;
            originals.set(node, source);
        }

        if (language === 'en') {
            if (current !== source) {
                node.nodeValue = source;
                applied.set(node, source);
            } else {
                applied.set(node, current);
            }
            return;
        }

        const leading = source.match(/^\s*/)?.[0] ?? '';
        const trailing = source.match(/\s*$/)?.[0] ?? '';
        const core = source.slice(leading.length, source.length - trailing.length || undefined);
        const translated = lookup(core);
        const next = translated === core ? source : `${leading}${translated}${trailing}`;
        if (next !== current) node.nodeValue = next;
        applied.set(node, next);
    }

    function localizeAttribute(element, name) {
        if (element.matches('[data-i18n-skip], [data-user-content]')) return;
        if (name === 'title' && element.closest('.dash-file, .fname, .file-name, .filename, .source-path, .work-item strong')) return;
        const current = element.getAttribute(name);
        if (current == null) return;

        let saved = attributeOriginals.get(element);
        if (!saved) {
            saved = Object.create(null);
            attributeOriginals.set(element, saved);
        }
        if (current !== attributeApplied.get(element)?.[name]) saved[name] = current;

        let last = attributeApplied.get(element);
        if (!last) {
            last = Object.create(null);
            attributeApplied.set(element, last);
        }
        const next = language === 'en' ? saved[name] : lookup(saved[name]);
        if (next !== current) element.setAttribute(name, next);
        last[name] = next;
    }

    function walk(root) {
        if (!root) return;
        if (root.nodeType === Node.TEXT_NODE) {
            localizeTextNode(root);
            return;
        }
        if (root.nodeType !== Node.ELEMENT_NODE && root !== document) return;

        const element = root.nodeType === Node.ELEMENT_NODE ? root : null;
        if (element) {
            for (const name of observableAttributes)
                if (element.hasAttribute(name)) localizeAttribute(element, name);
        }

        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        let node;
        while ((node = walker.nextNode())) localizeTextNode(node);
        const attributeRoot = element || (root.nodeType === Node.DOCUMENT_NODE ? root.documentElement : null);
        if (attributeRoot) {
            const descendants = attributeRoot.querySelectorAll(observableAttributes.map(name => `[${name}]`).join(','));
            for (const child of descendants)
                for (const name of observableAttributes)
                    if (child.hasAttribute(name)) localizeAttribute(child, name);
        }
    }

    function syncLanguagePicker() {
        const picker = document.getElementById('snacksLanguageSelect');
        if (picker && picker.value !== language) picker.value = language;
    }

    function setLanguage(next) {
        if (!LANGUAGES.has(next)) return language;
        language = next;
        try { localStorage.setItem(STORAGE_KEY, language); } catch { /* storage can be disabled */ }
        document.documentElement.lang = language;
        walk(document);
        syncLanguagePicker();
        const event = new CustomEvent('snacks:languagechange', {
            bubbles: true,
            detail: { language },
        });
        document.dispatchEvent(event);
        return language;
    }

    function installPicker() {
        const picker = document.getElementById('snacksLanguageSelect');
        if (!picker || picker.dataset.i18nBound === 'true') return;
        picker.dataset.i18nBound = 'true';
        picker.addEventListener('change', () => setLanguage(picker.value));
        syncLanguagePicker();
    }

    async function start() {
        const source = document.currentScript?.src || new URL('/js/i18n.js', location.href).href;
        const resourceFiles = /^\/docs(?:\/|$)/.test(location.pathname)
            ? ['zh-CN.json', 'docs.zh-CN.json']
            : ['zh-CN.json'];
        for (const file of resourceFiles) {
            try {
                const url = new URL(`../i18n/${file}`, source);
                const response = await fetch(url, { cache: 'no-cache' });
                if (response.ok) Object.assign(rawTranslations, await response.json());
            } catch (error) {
                console.error(`Unable to load Simplified Chinese UI resources (${file}):`, error);
            }
        }
        translations = Object.create(null);
        englishSourceTranslations = Object.create(null);
        for (const [key, value] of Object.entries(rawTranslations)) {
            if (key.startsWith(ENGLISH_SOURCE_PREFIX))
                englishSourceTranslations[normalize(key.slice(ENGLISH_SOURCE_PREFIX.length))] = value;
            else
                translations[normalize(key)] = value;
        }
        compileRules();
        document.documentElement.lang = language;
        walk(document);
        installPicker();
        if (document.body) document.body.classList.add('snacks-i18n-ready');
        document.documentElement.classList.remove('snacks-i18n-pending');

        if (observer) observer.disconnect();
        observer = new MutationObserver(records => {
            for (const record of records) {
                if (record.type === 'characterData') localizeTextNode(record.target);
                else if (record.type === 'childList')
                    for (const added of record.addedNodes) walk(added);
                else if (record.type === 'attributes' && observableAttributes.includes(record.attributeName))
                    localizeAttribute(record.target, record.attributeName);
            }
        });
        observer.observe(document.documentElement, {
            subtree: true,
            childList: true,
            characterData: true,
            attributes: true,
            attributeFilter: observableAttributes,
        });

        // Let the desktop menu initialize from the same persisted language.
        document.dispatchEvent(new CustomEvent('snacks:languagechange', {
            bubbles: true,
            detail: { language },
        }));
    }

    window.SnacksI18n = {
        t,
        setLanguage,
        get language() { return language; },
    };

    // Reveal the page even if a local static resource is unavailable.
    window.setTimeout(() => {
        if (!document.documentElement.classList.contains('snacks-i18n-pending')) return;
        document.documentElement.classList.remove('snacks-i18n-pending');
        document.body?.classList.add('snacks-i18n-ready');
    }, 1800);

    start();
})();
