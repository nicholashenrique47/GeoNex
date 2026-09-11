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
        setTimeout: (fn, delay) => { timers.set(++timerId, { fn, delay }); return timerId; },
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
    return { engine, a, b, images, calls, timers, present, drain, advance: ms => now += ms };
}

(async () => {
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
    console.log('Map engine contracts: PASS (A/B swap, overscan, stale decode, camera preemption/rebase, reset, bounded retries)');
})().catch(error => { console.error(error); process.exitCode = 1; });
