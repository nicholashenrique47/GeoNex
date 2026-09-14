// Run with: node PerfTest/MapEngineContracts.js
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../GeoNex/wwwroot/js/mapa.js'), 'utf8');
const start = source.indexOf('window.mapEngine = {');
const end = source.indexOf('\n};', start) + 3;

function setup() {
    let now = 1000;
    const frames = [], images = [], calls = [], timers = new Map();
    let timerId = 0;
    function canvas(visible) {
        const item = { width: 800, height: 600, style: { visibility: visible ? 'visible' : 'hidden' } };
        item.getContext = () => ({
            clearRect() { assert.equal(item.style.visibility, 'hidden', 'visible canvas must never be cleared'); },
            drawImage() { assert.equal(item.style.visibility, 'hidden', 'draw completes before swap'); }
        });
        return item;
    }
    const a = canvas(true), b = canvas(false);
    const context = vm.createContext({
        window: {}, console: { warn() {} }, URL, Set, Map,
        performance: { now: () => now },
        setTimeout: (fn, delay) => { timers.set(++timerId, { fn, delay, due: now + delay }); return timerId; },
        clearTimeout: id => timers.delete(id),
        requestAnimationFrame: fn => { frames.push(fn); return frames.length; },
        cancelAnimationFrame() {},
        document: { baseURI: 'http://localhost/', getElementById: () => null },
        Image: class {
            constructor() { this.width = 1200; this.height = 1000; images.push(this); }
            decode() { return new Promise((resolve, reject) => { this.resolve = resolve; this.reject = reject; }); }
        }
    });
    vm.runInContext(source.slice(start, end), context);
    const engine = context.window.mapEngine;
    engine.skiaCanvas = a; engine.backCanvas = b;
    engine.renderSurface = { style: {} };
    engine.dotNetHelper = { invokeMethodAsync(...args) { calls.push(args); return Promise.resolve(); } };
    engine.solicitarAnimacao = () => {};
    async function drain() { for (let i = 0; i < 8; i++) await Promise.resolve(); }
    async function present(image) {
        image.resolve(); await drain();
        frames.splice(0).forEach(fn => fn(now)); await drain();
    }
    function tick(ms) {
        const until = now + ms;
        let guard = 0;
        for (;;) {
            const next = [...timers].filter(([, timer]) => timer.due <= until)
                .sort((a, b) => a[1].due - b[1].due)[0];
            if (!next) break;
            assert.ok(++guard < 10000, 'timer loop remains bounded');
            timers.delete(next[0]); now = next[1].due; next[1].fn();
        }
        now = until;
    }
    return { engine, a, b, images, calls, timers, present, drain, tick, advance: ms => now += ms };
}

(async () => {
    {
        const s = setup(), e = s.engine;
        e.carregarNovoFrame('/vector-first?deferOnline=1', 1, 0, false, 0, 800, 600, 90, 40, 2, true);
        assert.equal(s.calls.length, 0, 'online refinement waits for vector presentation');
        await s.present(s.images[0]); s.tick(0);
        const cameras = s.calls.filter(call => call[0] === 'AtualizarCameraJS');
        assert.equal(cameras.length, 1, 'one automatic online refinement');
        assert.equal(cameras[0][4], false, 'refinement requests full quality');
        assert.deepEqual(cameras[0].slice(-3), [90, 40, 2], 'online refinement preserves fitted layer camera');
    }
    {
        const s = setup(), e = s.engine;
        e.carregarNovoFrame('/old-vector-preview', 1, 0, false, 0, 800, 600, 0, 0, 1, true);
        e.carregarNovoFrame('/new-scene', 2, 0, false, 0, 800, 600, 0, 0, 2, false);
        await s.present(s.images[1]);
        await s.present(s.images[0]); s.tick(0);
        assert.equal(s.calls.filter(call => call[0] === 'AtualizarCameraJS').length, 0,
            'discarded online preview cannot trigger obsolete refinement');
    }
    {
        const s = setup(), e = s.engine, requested = [];
        e.solicitarFrame = interactive => requested.push(interactive);
        for (let i = 0; i < 10; i++) { e.scheduleRender(); s.tick(100); }
        assert.ok(requested.length > 0 && requested.every(Boolean), 'wheel burst requests previews, not premature final I/O');
        s.tick(500);
        assert.equal(requested.filter(value => !value).length, 1, 'exactly one full-quality refinement after settling');
        for (const latency of [0, 16, 300, 5000, NaN, Infinity]) {
            e.currentLatency = latency;
            assert.ok(e.obterAtrasoAssentamento() >= 250 && e.obterAtrasoAssentamento() <= 400, 'settling delay bounded across hardware/network latency');
        }
    }
    {
        const s = setup(), e = s.engine;
        e.targetX = 90; e.targetScale = 2;
        e.executarRequisicao(true);
        s.advance(300);
        e.solicitarFrame(false);
        assert.equal(e.activeRequestId, 1, 'same-camera final does not cancel slow preview');
        assert.equal(s.calls.length, 1);
        assert.equal(e.hasPendingRequest, true);
        assert.equal(e.pendingInteractive, false);
        e.carregarNovoFrame('/slow-preview', 1, 1, false, 0, 800, 600, 90, 0, 2);
        await s.present(s.images[0]); s.tick(0);
        const cameraCalls = s.calls.filter(call => call[0] === 'AtualizarCameraJS');
        assert.equal(cameraCalls.length, 2);
        assert.equal(cameraCalls[1][4], false, 'preview automatically followed by final quality');
        assert.deepEqual(cameraCalls[1].slice(-3), [90, 0, 2], 'refinement uses committed camera without double zoom');
    }
    {
        const s = setup(), e = s.engine;
        e.executarRequisicao(true);
        s.advance(300);
        e.solicitarFrame(true);
        assert.equal(s.calls.length, 1, 'identical in-flight preview is not duplicated');
        assert.equal(e.hasPendingRequest, false);
        e.solicitarFrame(false);
        assert.equal(e.pendingInteractive, false);
        e.targetX = 150;
        e.scheduleRender();
        assert.equal(e.pendingInteractive, true, 'new gesture demotes queued old final until next settle');
        e.carregarNovoFrame('/previous-pause', 1, 1, false);
        await s.present(s.images[0]); s.tick(0);
        const cameraCalls = s.calls.filter(call => call[0] === 'AtualizarCameraJS');
        assert.equal(cameraCalls.at(-1)[4], true, 'continued navigation does not trigger premature high-quality I/O');
    }
    {
        const s = setup(), e = s.engine;
        e.committedCamera = { panX: 40, panY: -30, zoom: 2 };
        e.targetX = 100; e.targetY = 20; e.targetScale = 1.5;
        e.executarRequisicao(false);
        e.carregarNovoFrame('/first', 1, 1, false, 200, 800, 600, 160, -25, 3);
        s.advance(200);
        e.targetX = 180; e.targetScale = 2;
        e.solicitarFrame(false);
        assert.equal(e.activeRequestId, 2, 'slow request is superseded');
        e.carregarNovoFrame('/late-old-request', 99, 1, false);
        assert.equal(s.images.length, 1, 'late callbacks never fetch an obsolete HTTP request');
        assert.deepEqual(s.calls[1].slice(-3), [40, -30, 2], 'new request retains presented camera basis');
        await s.present(s.images[0]);
        assert.equal(e.latestFramePresented, 0, 'superseded decode cannot move camera');
        e.carregarNovoFrame('/second', 2, 2, false, 200, 800, 600, 260, -40, 4);
        await s.present(s.images[1]);
        assert.equal(e.skiaCanvas, s.b);
        assert.equal(s.b.style.left, '-200px');
        assert.equal(s.b.style.width, '1200px', 'physical bitmap keeps CSS footprint at every DPI');
        assert.equal(s.a.style.visibility, 'hidden');
        assert.equal(e.targetScale, 1);
        assert.equal(e.targetX, 0);
        assert.equal(e.committedCamera.panX, 260);
        // Next swap returns to A without drawing on the currently visible B.
        e.carregarNovoFrame('/third', 3, 0, false, 0, 800, 600, 260, -40, 4);
        await s.present(s.images[2]);
        assert.equal(e.skiaCanvas, s.a);
        e.carregarNovoFrame('/third', 3, 0, false);
        assert.equal(s.images.length, 3, 'duplicate frame cannot redraw a now-visible staging canvas');
    }
    {
        const s = setup(), e = s.engine;
        e.executarRequisicao(true);
        e.carregarNovoFrame('/uncorrelated', 9, 0, false);
        assert.equal(s.images.length, 0, 'external frame cannot commit an unacknowledged camera');
        e.carregarNovoFrame('/old', 1, 1, false);
        e.resetarCamera();
        await s.present(s.images[0]);
        assert.equal(e.latestFramePresented, 0, 'reset invalidates decodes already in flight');
    }
    {
        const s = setup(), e = s.engine;
        e.targetX = 70; e.targetScale = 1.2;
        e.executarRequisicao(false);
        e.falharRequisicao(1); e.falharRequisicao(1); e.falharRequisicao(1);
        assert.equal(s.calls.length, 3, 'retry count is bounded');
        for (const call of s.calls) assert.equal(call[5], 1, 'retry keeps transaction identity');
        assert.equal(e.targetX, 70, 'failure preserves last visual delta');
        s.advance(200); e.solicitarFrame(false);
        assert.equal(e.activeRequestId, 2, 'navigation can recover after terminal failure');
    }
    {
        const s = setup(), e = s.engine;
        e.executarRequisicao(true);
        e.carregarNovoFrame('/old-basemap', 1, 1, false);
        e.renderTimeout = 21; e.previewTimeout = 22; e.velocityX = 50;
        const barrier = e.sincronizarCameraComBlazor(1234, -5678, 2500);
        assert.equal(barrier, 1, 'fit returns the last old request identity');
        assert.equal(e.renderTimeout, null);
        assert.equal(e.previewTimeout, null);
        assert.equal(e.velocityX, 0);
        assert.equal(e.committedCamera.zoom, 2500, 'fit retains non-default camera basis');
        await s.present(s.images[0]);
        assert.equal(e.latestFramePresented, 0, 'old basemap decode cannot undo layer fit');
        e.carregarNovoFrame('/fitted-vector', 2, 0, false, 0, 800, 600, 1234, -5678, 2500);
        await s.present(s.images[1]);
        e.executarRequisicao(false);
        assert.deepEqual(s.calls.at(-1).slice(-3), [1234, -5678, 2500], 'next gesture uses fitted camera');
    }
    {
        const s = setup(), e = s.engine;
        e.sincronizarCameraComBlazor(1234, -5678, 2500);
        e.executarRequisicao(true, true);
        assert.equal(s.calls[0][4], true, 'fit requests preview first');
        e.carregarNovoFrame('/fit-preview', 1, 1, false, 0, 800, 600, 1234, -5678, 2500);
        assert.equal(s.calls.length, 1, 'final must not cancel the unpresented preview');
        await s.present(s.images[0]);
        assert.equal(e.latestFramePresented, 1);
        for (const [id, timer] of [...s.timers]) if (timer.delay === 0) { s.timers.delete(id); timer.fn(); }
        const cameraCalls = s.calls.filter(call => call[0] === 'AtualizarCameraJS');
        assert.equal(cameraCalls.length, 2, 'fit automatically refines after presentation');
        assert.equal(cameraCalls[1][4], false, 'refinement uses final quality');
        assert.deepEqual(cameraCalls[1].slice(-3), [1234, -5678, 2500]);
    }
    console.log('Map engine contracts: PASS (A/B swap, camera/DPI, stale decode, wheel burst debounce, preview-before-final, deduplication, gesture resumption, reset/retries)');
})().catch(error => { console.error(error); process.exitCode = 1; });
