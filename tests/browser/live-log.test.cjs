const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { test } = require('node:test');
const vm = require('node:vm');

const source = readFileSync(require('node:path').join(__dirname, '../../src/CaddyUi.Web/wwwroot/js/live-log.js'), 'utf8');

function setup(hidden = false) {
    const documentEvents = new Map();
    const windowEvents = new Map();
    const streams = [];
    const state = {};
    const rows = {
        children: [],
        prepend(row) { this.children.unshift(row); },
        get lastElementChild() { return { remove: () => this.children.pop() }; },
    };
    const document = {
        hidden,
        currentScript: { dataset: { streamUrl: '/events/live?host=test', liveLabel: 'Live', connectingLabel: 'Connecting', reconnectLabel: 'Retrying' } },
        documentElement: { lang: 'en' },
        getElementById: id => id === 'live-rows' ? rows : state,
        createElement: () => ({}),
        querySelector: () => null,
        addEventListener: (type, handler) => documentEvents.set(type, handler),
    };
    class EventSource {
        constructor(url) { this.url = url; this.listeners = new Map(); streams.push(this); }
        close() { this.closed = true; }
        addEventListener(type, handler) { this.listeners.set(type, handler); }
    }
    vm.runInNewContext(source, {
        document,
        window: { addEventListener: (type, handler) => windowEvents.set(type, handler) },
        EventSource,
    });
    return { document, documentEvents, windowEvents, streams, state, rows };
}

test('hidden tabs close polling and resume one stream when visible', () => {
    const ui = setup();
    assert.equal(ui.streams.length, 1);
    assert.equal(ui.streams[0].url, '/events/live?host=test');
    ui.document.hidden = true;
    ui.documentEvents.get('visibilitychange')();
    assert.equal(ui.streams[0].closed, true);
    ui.document.hidden = false;
    ui.documentEvents.get('visibilitychange')();
    ui.documentEvents.get('visibilitychange')();
    assert.equal(ui.streams.length, 2);
    ui.streams[1].onopen();
    assert.equal(ui.state.textContent, 'Live');
    ui.streams[1].onerror();
    assert.equal(ui.state.textContent, 'Retrying');
});

test('initially hidden and cached pages start only while active', () => {
    const ui = setup(true);
    assert.equal(ui.streams.length, 0);
    ui.document.hidden = false;
    ui.windowEvents.get('pageshow')();
    assert.equal(ui.streams.length, 1);
    ui.windowEvents.get('pagehide')();
    assert.equal(ui.streams[0].closed, true);
    ui.documentEvents.get('visibilitychange')();
    assert.equal(ui.streams.length, 1);
    ui.windowEvents.get('pageshow')();
    assert.equal(ui.streams.length, 2);
});

test('live rows remain escaped and bounded', () => {
    const ui = setup();
    const request = ui.streams[0].listeners.get('request');
    for (let index = 0; index < 110; index++) {
        request({ data: JSON.stringify({ occurredAt: '2026-11-18T10:00:00Z', host: '<script>', path: '/<img>', method: 'GET', status: 200 }) });
    }
    assert.equal(ui.rows.children.length, 100);
    assert.match(ui.rows.children[0].innerHTML, /&lt;script&gt;/);
    assert.doesNotMatch(ui.rows.children[0].innerHTML, /<script>/);
});
