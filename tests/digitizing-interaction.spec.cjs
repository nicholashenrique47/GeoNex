const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const home = fs.readFileSync(path.resolve(__dirname, '../GeoNex/Components/Pages/Home.razor'), 'utf8');
const script = fs.readFileSync(path.resolve(__dirname, '../GeoNex/wwwroot/js/mapa.js'), 'utf8');
assert.doesNotMatch(home, /@onpointermove=/, 'Home must not duplicate the JavaScript pointer channel');
assert.equal((script.match(/document\.addEventListener\('keydown'/g) || []).length, 1,
    'Production script must register exactly one shortcut handler');
assert.match(home, /HistoricoAquisicao\.Add\(ptFinalAq\)/, 'Committed clicks participate in undo/redo');
assert.match(home, /@@media \(max-width: 820px\)/, 'Digitizing toolbar has a narrow viewport layout');

(async () => {
    const browser = await chromium.launch({ channel: 'msedge', headless: true });
    try {
        const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        await page.setContent('<div id="map-container" tabindex="0" style="position:fixed;inset:0">Map</div><input id="field"><button id="button">Apply</button><section role="dialog"><div id="dialog" tabindex="0">Dialog</div></section>');
        await page.addScriptTag({ path: path.resolve(__dirname, '../GeoNex/wwwroot/js/mapa.js') });
        await page.evaluate(() => {
            window.calls = [];
            window.mapEngine.container = document.getElementById('map-container');
            window.mapEngine.dotNetHelper = { invokeMethodAsync: async (...args) => window.calls.push(args) };
            window.mapEngine.setFerramenta('AquisicaoPoligono');
        });

        await page.locator('#map-container').focus();
        for (const key of ['Control+z', 'Control+y', 'Control+Shift+z', 'Enter', 'Escape']) await page.keyboard.press(key);
        assert.deepEqual(await page.evaluate(() => window.calls.map(call => call[1])),
            ['ctrl+z', 'ctrl+y', 'ctrl+shift+z', 'enter', 'escape'], 'Drawing shortcuts dispatch once');

        await page.evaluate(() => window.calls.length = 0);
        for (const selector of ['#field', '#button', '#dialog']) {
            await page.locator(selector).focus();
            for (const key of ['Enter', 'Control+z', 'Escape']) await page.locator(selector).press(key);
        }
        assert.deepEqual(await page.evaluate(() => window.calls), [], 'Interactive controls retain keyboard input');

        for (const tool of ['Medicao', 'AquisicaoPoligono', 'AquisicaoLinha', 'AquisicaoPonto']) {
            await page.evaluate(tool => {
                window.calls.length = 0;
                window.mapEngine.setFerramenta(tool);
                for (let i = 0; i < 200; i++)
                    window.mapEngine.onPointerMove({ pointerType: 'mouse', clientX: 10 + i, clientY: 30 });
            }, tool);
            await page.waitForFunction(() => window.calls.length === 1);
            const call = await page.evaluate(() => window.calls[0]);
            assert.equal(call[0], 'ReceberMovimentoFerramentas');
            assert.equal(call[1], 209, 'Burst sends its latest position');
        }

        await page.waitForFunction(() => !window.mapEngine.toolPointerInFlight);
        const slowQueue = await page.evaluate(async () => {
            window.calls.length = 0;
            let release;
            window.mapEngine.dotNetHelper = {
                invokeMethodAsync: (...args) => {
                    window.calls.push(args);
                    return new Promise(resolve => { release = resolve; });
                }
            };
            window.mapEngine.setFerramenta('AquisicaoLinha');
            window.mapEngine.onPointerMove({ pointerType: 'mouse', clientX: 50, clientY: 50 });
            await new Promise(requestAnimationFrame);
            for (let x = 100; x < 200; x++)
                window.mapEngine.onPointerMove({ pointerType: 'mouse', clientX: x, clientY: 60 });
            const whileBusy = { calls: window.calls.length, pending: window.mapEngine.toolPointerPending.clientX };
            release();
            await new Promise(resolve => setTimeout(resolve, 30));
            const afterRelease = { calls: window.calls.length, lastX: window.calls.at(-1)[1] };
            release();
            await new Promise(resolve => setTimeout(resolve, 0));
            return { whileBusy, afterRelease };
        });
        assert.deepEqual(slowQueue.whileBusy, { calls: 1, pending: 199 },
            'A slow backend keeps only one pending latest sample');
        assert.deepEqual(slowQueue.afterRelease, { calls: 2, lastX: 199 },
            'The latest sample is delivered after the backend becomes available');

        for (const scale of [0.5, 1, 2.5]) {
            await page.evaluate(scale => {
                window.calls.length = 0;
                window.mapEngine.dotNetHelper = { invokeMethodAsync: async (...args) => window.calls.push(args) };
                Object.assign(window.mapEngine, { currentX: 37, currentY: -19, currentScale: scale });
                window.mapEngine.setFerramenta('AquisicaoLinha');
                window.mapEngine.onPointerMove({ pointerType: 'mouse', clientX: 200, clientY: 100 });
            }, scale);
            await page.waitForFunction(() => window.calls.length === 1);
            await page.evaluate(() => window.mapEngine.dispararRaycast(200, 100));
            const calls = await page.evaluate(() => window.calls);
            assert.deepEqual(calls[0].slice(1, 3), calls[1].slice(1, 3),
                'Preview and click undo the same CSS pan/zoom transform');
        }

        const zoomed = await page.evaluate(() => {
            const result = [];
            const original = window.mapEngine.aplicarZoomCentralizado;
            window.mapEngine.aplicarZoomCentralizado = () => result.push(window.mapEngine.ferramentaAtual);
            for (const tool of ['Medicao', 'AquisicaoPoligono', 'AquisicaoLinha', 'AquisicaoPonto', 'Navegacao']) {
                window.mapEngine.setFerramenta(tool);
                window.mapEngine.onDoubleClick({ preventDefault() {}, clientX: 10, clientY: 10 });
            }
            window.mapEngine.aplicarZoomCentralizado = original;
            return result;
        });
        assert.deepEqual(zoomed, ['Navegacao'], 'Double-click while drawing cannot zoom the map');
        assert.deepEqual(errors, [], 'Production map script loads without browser errors');
        console.log('Digitizing interaction contracts passed.');
    } finally {
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
