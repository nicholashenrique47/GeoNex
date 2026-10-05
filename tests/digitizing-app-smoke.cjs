// Run against a disposable Debug app launched with WebView2 CDP enabled.
// Creates only a temporary empty layer; never saves edits or opens user datasets.
const { chromium } = require('playwright');
const { readyMap } = require('./app-map-helpers.cjs');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { PNG } = require('pngjs');

(async () => {
    const endpoint = process.env.GEONEX_TEST_CDP;
    assert(endpoint, 'Set GEONEX_TEST_CDP to the endpoint of a disposable app instance.');
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'GeoNex-topology-test-'));
    const output = path.resolve(__dirname, '../test-results/digitizing-app');
    fs.mkdirSync(output, { recursive: true });
    const browser = await chromium.connectOverCDP(endpoint);
    try {
        const page = browser.contexts().flatMap(c => c.pages()).find(p => p.url() === 'https://0.0.0.1/');
        assert(page, 'The actual MAUI Blazor WebView must be present.');
        await page.setViewportSize({ width: 1440, height: 1000 });
        page.setDefaultTimeout(15000);
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        page.on('dialog', async dialog => { errors.push(dialog.message()); await dialog.dismiss(); });
        await page.locator('#map-container').waitFor({ state: 'visible' });
        const layerDock = page.locator('.left-panel .panel-dock-toggle');
        const identifyDock = page.locator('.right-panel .panel-dock-toggle');
        await layerDock.click();
        assert(await page.getByText('CAMADAS', { exact: true }).isVisible(), 'Layer panel must expand from its dock rail.');
        await layerDock.click();
        await identifyDock.click();
        assert(await page.getByText('ATRIBUTOS DA FEIÇÃO', { exact: true }).isVisible(), 'Identify panel must expand from its dock rail.');
        await identifyDock.click();
        const initialLayerCount = await page.locator('.left-panel .layer-item').count();

        const toolbar = page.locator('.digitizing-toolbar');
        if (!await toolbar.isVisible()) {
            await page.evaluate(() => document.activeElement?.blur());
            await page.keyboard.press('d');
        }
        await toolbar.waitFor({ state: 'visible' });
        const modal = page.locator('.import-modal').filter({ hasText: 'ALOCAÇÃO DE DATASET VETORIAL' });
        if (!await modal.isVisible()) await page.getByLabel('Camada de destino da vetorização').selectOption('NOVA');
        await modal.waitFor({ state: 'visible' });
        await modal.getByPlaceholder('Ex: Levantamento_Topografico').fill('TopologySmoke');
        await modal.locator('select').nth(2).selectOption('EPSG:31982');
        await modal.getByPlaceholder('Ex: C:\\Projetos_SIG').fill(directory);
        await modal.getByRole('button', { name: 'PROCESSAR ALOCAÇÃO' }).click();
        await modal.waitFor({ state: 'detached' });
        await page.waitForFunction(() => document.querySelector('[aria-label="Camada de destino da vetorização"]')?.value === 'TopologySmoke');
        if (initialLayerCount === 0) {
            await page.waitForFunction(() => document.querySelector('.geonex-ui')?.classList.contains('layers-panel-open'));
            assert(await page.getByText('CAMADAS', { exact: true }).isVisible(), 'The first created layer must automatically open the Layers panel.');
            await layerDock.click();
            await page.waitForFunction(() => !document.querySelector('.geonex-ui')?.classList.contains('layers-panel-open'));
        }

        let box = await readyMap(page);
        const previousFrame = await page.evaluate(() => window.mapEngine.latestFramePresented);
        // Four crossings well inside the map and away from panels/toolbar.
        for (const [x, y] of [[0.4, 0.4], [0.6, 0.7], [0.4, 0.7], [0.6, 0.4]]) {
            await page.mouse.click(box.x + box.width * x, box.y + box.height * y);
        }
        await page.waitForFunction(() => document.querySelector('.toolbar-status')?.textContent.includes('4 ponto(s)'));
        await page.waitForFunction(() => document.querySelector('.sketch-metrics')?.textContent.includes('Comprimento'));
        assert(await page.getByLabel('Camada de destino da vetorização').isDisabled(), 'Destination must stay fixed during a sketch.');
        await toolbar.getByRole('button', { name: /^CONCLUIR$/i }).click();
        await page.waitForFunction(() => document.querySelector('.toolbar-status')?.textContent.includes('cruza'));
        assert(await toolbar.getByRole('button', { name: /^DESFAZER$/i }).isEnabled(), 'Invalid sketch must retain undo history.');
        // Blazor text updates before the asynchronous map image. Verify the real
        // canvas, not just controls that could pass with a completely blank map.
        await page.waitForFunction(previous => window.mapEngine.latestFramePresented > previous &&
            window.mapEngine.latestFramePresented >= window.mapEngine.latestFrameRequested, previousFrame);
        // The map is a cross-origin local-server image; inspect browser output
        // instead of attempting getImageData on its deliberately tainted canvas.
        async function countSketchPixels(cyan = false) {
            const pixels = PNG.sync.read(await page.screenshot({ scale: 'css', clip: {
                x: box.x + box.width * 0.35, y: box.y + box.height * 0.35,
                width: box.width * 0.3, height: box.height * 0.4
            } })).data;
            let green = 0;
            for (let i = 0; i < pixels.length; i += 4)
                if (pixels[i + 1] > 180 && pixels[i] < 100 &&
                    (cyan ? pixels[i + 2] > 180 : pixels[i + 2] < 200)) green++;
            return green;
        }
        const greenPixels = await countSketchPixels();
        assert(greenPixels > 100, 'The map canvas must visibly render the preserved green sketch.');
        await page.screenshot({ path: path.join(output, 'invalid-polygon.png') });
        await toolbar.getByRole('button', { name: /^DESFAZER$/i }).click();
        await page.waitForFunction(() => document.querySelector('.toolbar-status')?.textContent.includes('3 ponto(s)'));
        await toolbar.getByRole('button', { name: /^REFAZER$/i }).click();
        await page.waitForFunction(() => document.querySelector('.toolbar-status')?.textContent.includes('4 ponto(s)'));
        const beforeCancel = await page.evaluate(() => window.mapEngine.latestFramePresented);
        await toolbar.getByRole('button', { name: /^CANCELAR$/i }).click();
        await page.waitForFunction(() => document.querySelector('.toolbar-status')?.textContent.includes('0 ponto(s)'));
        await page.waitForFunction(previous => window.mapEngine.latestFramePresented > previous &&
            window.mapEngine.latestFramePresented >= window.mapEngine.latestFrameRequested, beforeCancel);
        assert.equal(await countSketchPixels(), 0, 'Cancel must remove sketch pixels, including on a base-cache hit.');
        const snapMenu = toolbar.locator('.snap-options');
        await snapMenu.locator('summary').click();
        await snapMenu.getByRole('button', { name: /Atrair Vértices/ }).click();
        await snapMenu.getByLabel('Meio das arestas', { exact: true }).check();
        await snapMenu.locator('summary').click();
        box = await readyMap(page);
        for (const x of [0.4, 0.6]) await page.mouse.click(box.x + box.width * x, box.y + box.height * 0.4);
        await page.waitForFunction(() => document.querySelector('.toolbar-status')?.textContent.includes('2 ponto(s)'));
        await readyMap(page);
        const beforeSnap = await page.evaluate(() => window.mapEngine.latestFramePresented);
        const midpoint = { x: box.x + box.width * 0.5, y: box.y + box.height * 0.4 };
        await page.mouse.move(midpoint.x, midpoint.y + 8);
        await page.waitForFunction(previous => window.mapEngine.latestFramePresented > previous &&
            window.mapEngine.latestFramePresented >= window.mapEngine.latestFrameRequested, beforeSnap);
        const marker = PNG.sync.read(await page.screenshot({ scale: 'css', clip: {
            x: midpoint.x - 20, y: midpoint.y - 20, width: 40, height: 40
        } }));
        let cyanCount = 0, cyanY = 0;
        for (let i = 0; i < marker.data.length; i += 4) {
            if (marker.data[i] < 100 && marker.data[i + 1] > 180 && marker.data[i + 2] > 180) {
                cyanCount++; cyanY += Math.floor(i / 4 / marker.width);
            }
        }
        assert(cyanCount > 20 && Math.abs(cyanY / cyanCount - 20) < 2.5,
            `Midpoint-only mode must snap to the segment center: ${JSON.stringify({cyanCount, meanY:cyanY/cyanCount, midpoint, box, current:await page.locator('#map-container').boundingBox()})}`);
        await page.screenshot({ path: path.join(output, 'midpoint-snap.png') });
        await toolbar.getByRole('button', { name: /^CANCELAR$/i }).click();
        // Real controls, rendered previews, history and finalization for each construction.
        for (const [mode, controls] of [
            ['Rectangle', [[.4,.4],[.6,.7]]],
            ['OrientedRectangle', [[.4,.45],[.55,.4],[.6,.65]]],
            ['Circle', [[.5,.55],[.6,.55]]],
            ['Ellipse', [[.45,.55],[.58,.55],[.45,.68]]],
            ['RegularPolygon', [[.5,.55],[.6,.55]]]
        ]) {
            await page.getByLabel('Modo de construção', { exact: true }).selectOption(mode);
            if (mode === 'RegularPolygon')
                await page.getByLabel('Lados do polígono regular', { exact: true }).selectOption('8');
            box = await readyMap(page);
            await page.mouse.click(box.x + box.width * controls[0][0], box.y + box.height * controls[0][1]);
            await page.waitForFunction(() => document.querySelector('.toolbar-status')?.textContent.includes('1 ponto(s)'));
            assert(await page.getByLabel('Modo de construção', { exact: true }).isDisabled(), 'Mode must be locked while controls exist.');
            for (const [x,y] of controls.slice(1)) await page.mouse.click(box.x + box.width*x, box.y + box.height*y);
            await page.waitForFunction(n => document.querySelector('.toolbar-status')?.textContent.includes(`${n} ponto(s)`), controls.length);
            await page.waitForFunction(() => window.mapEngine.latestFramePresented >= window.mapEngine.latestFrameRequested);
            assert(await countSketchPixels() > 100, `${mode} must have a visible polygon preview.`);
            await page.screenshot({ path: path.join(output, `${mode}.png`) });
            await toolbar.getByRole('button', { name: /^DESFAZER$/i }).click();
            await page.waitForFunction(n => document.querySelector('.toolbar-status')?.textContent.includes(`${n} ponto(s)`), controls.length - 1);
            assert(await toolbar.getByRole('button', { name: /^CONCLUIR$/i }).isDisabled(), 'Undo must return to incomplete controls.');
            await toolbar.getByRole('button', { name: /^REFAZER$/i }).click();
            await page.waitForFunction(n => document.querySelector('.toolbar-status')?.textContent.includes(`${n} ponto(s)`), controls.length);
            assert(await toolbar.getByRole('button', { name: /^CONCLUIR$/i }).isEnabled(), 'Redo must restore the complete construction.');
            await toolbar.getByRole('button', { name: /^CONCLUIR$/i }).click();
            await page.waitForFunction(() => document.querySelector('.toolbar-status')?.textContent.includes('0 ponto(s)'));
        }
        await page.getByLabel('Modo de construção', { exact: true }).selectOption('Vertices');
        await snapMenu.locator('summary').click();
        await snapMenu.getByLabel('Meio das arestas', { exact: true }).uncheck();
        await snapMenu.getByRole('button', { name: /Atrair Vértices/ }).click();
        await snapMenu.locator('summary').click();
        const beforeCommittedSnap = await page.evaluate(() => window.mapEngine.latestFramePresented);
        box = await readyMap(page);
        await page.mouse.move(box.x + box.width * .6, box.y + box.height * .55 + 8);
        await page.waitForFunction(previous => window.mapEngine.latestFramePresented > previous &&
            window.mapEngine.latestFramePresented >= window.mapEngine.latestFrameRequested, beforeCommittedSnap);
        const committedMarker = PNG.sync.read(await page.screenshot({ scale: 'css', clip: {
            x: box.x + box.width * .6 - 20, y: box.y + box.height * .55 - 20, width: 40, height: 40
        } })).data;
        let committedYellow = 0;
        for (let i = 0; i < committedMarker.length; i += 4)
            if (committedMarker[i] > 180 && committedMarker[i+1] > 180 && committedMarker[i+2] < 160) committedYellow++;
        assert(committedYellow > 20, 'A finished circle must participate in spatial-index vertex snap.');
        await page.setViewportSize({ width: 760, height: 900 });
        await readyMap(page);
        for (const control of await toolbar.locator('button, select, summary').all()) {
            if (!await control.isVisible()) continue;
            const bounds = await control.boundingBox();
            assert(bounds.x >= 0 && bounds.x + bounds.width <= 760, 'Toolbar controls must remain inside a narrow viewport.');
        }
        await page.screenshot({ path: path.join(output, 'construction-narrow.png') });
        await layerDock.click();
        await page.getByText('CAMADAS', { exact: true }).waitFor({ state: 'visible' });
        await layerDock.click();
        await page.getByText('CAMADAS', { exact: true }).waitFor({ state: 'hidden' });
        assert.equal(await page.locator('.left-panel .panel-rail-label').innerText(), 'Camadas');
        await page.setViewportSize({ width: 1440, height: 1000 });
        await page.evaluate(() => document.activeElement?.blur());
        await page.keyboard.press('m');
        await page.waitForFunction(() => window.mapEngine.ferramentaAtual === 'Medicao');
        await page.locator('.measurement-settings summary').click();
        await page.getByLabel('Encaixar no meio das arestas', { exact: true }).check();
        await page.locator('.measurement-settings summary').click();
        box = await readyMap(page);
        const beforeMeasurement = await page.evaluate(() => window.mapEngine.latestFramePresented);
        for (const [x, y] of [[0.4, 0.4], [0.6, 0.7]])
            await page.mouse.click(box.x + box.width * x, box.y + box.height * y);
        await page.mouse.move(box.x + box.width * 0.65, box.y + box.height * 0.65);
        await page.waitForFunction(() => [...document.querySelectorAll('.form-group-vertical span')]
            .some(el => /\d/.test(el.textContent) && !el.textContent.trim().startsWith('0,00')));
        await page.getByLabel('Unidade da distância', { exact: true }).selectOption('km');
        await page.waitForFunction(() => [...document.querySelectorAll('.form-group-vertical span')]
            .some(el => el.textContent.includes('km')));
        await page.getByLabel('Unidade da distância', { exact: true }).selectOption('m');
        await page.getByRole('button', { name: /^COPIAR RESULTADO$/i }).click();
        await page.waitForFunction(() => document.querySelector('.measurement-feedback')?.textContent.includes('copiado'));
        await page.waitForFunction(previous => window.mapEngine.latestFramePresented > previous &&
            window.mapEngine.latestFramePresented >= window.mapEngine.latestFrameRequested, beforeMeasurement);
        assert(await countSketchPixels(true) > 100, 'Measurement must also render over the reused base map.');
        await page.screenshot({ path: path.join(output, 'measurement.png') });

        await page.evaluate(() => document.activeElement?.blur());
        await page.keyboard.press('d');
        await toolbar.waitFor({ state: 'visible' });
        await page.getByLabel('Modo de construção', { exact: true }).selectOption('Vertices');
        box = await readyMap(page);
        const triangle = [[.72,.42],[.82,.42],[.77,.56]];
        for (const [x,y] of triangle) await page.mouse.click(box.x + box.width*x, box.y + box.height*y);
        await page.waitForFunction(() => document.querySelector('.toolbar-status')?.textContent.includes('3 ponto(s)'));
        assert((await page.locator('.construction-hint').innerText()).includes('primeiro vértice'), 'Polygon closure affordance must appear after three vertices.');
        await page.mouse.click(box.x + box.width*triangle[0][0], box.y + box.height*triangle[0][1]);
        await page.waitForFunction(() => document.querySelector('.toolbar-status')?.textContent.includes('0 ponto(s)'));
        await page.keyboard.press('i');
        await page.waitForFunction(() => document.querySelector('.geonex-ui')?.classList.contains('identify-panel-open'));
        await identifyDock.click();
        await page.waitForFunction(() => !document.querySelector('.geonex-ui')?.classList.contains('identify-panel-open'));
        box = await readyMap(page);
        await page.mouse.click(box.x + box.width*.77, box.y + box.height*.46);
        await page.waitForFunction(() => document.querySelector('.geonex-ui')?.classList.contains('identify-panel-open'));
        assert(await page.getByText('ATRIBUTOS DA FEIÇÃO', { exact: true }).isVisible(), 'A feature hit must reopen the Identify panel.');
        assert.deepEqual(errors, [], 'The actual app must report no page errors or failure dialogs.');
        console.log(JSON.stringify({ result: 'passed', directory, screenshot: path.join(output, 'invalid-polygon.png'),
            coverage: 'MAUI startup, first-layer and identify auto-open, polygon close-on-start, topology rejection, undo/redo, cancel, midpoint snap, live metrics, five construction previews, responsive toolbar, measurement and unit switching' }));
    } finally {
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
