// Run against a disposable Debug app launched with WebView2 CDP enabled.
// Creates only a temporary empty layer; never saves edits or opens user datasets.
const { chromium } = require('playwright');
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
        page.setDefaultTimeout(15000);
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        page.on('dialog', async dialog => { errors.push(dialog.message()); await dialog.dismiss(); });
        await page.locator('#map-container').waitFor({ state: 'visible' });
        assert(await page.getByText('CAMADAS', { exact: true }).isVisible(), 'Layer panel must render.');
        assert(await page.getByText('ATRIBUTOS DA FEIÇÃO', { exact: true }).isVisible(), 'Attributes panel must render.');

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
        await modal.locator('select').nth(2).selectOption('EPSG:4326');
        await modal.getByPlaceholder('Ex: C:\\Projetos_SIG').fill(directory);
        await modal.getByRole('button', { name: 'PROCESSAR ALOCAÇÃO' }).click();
        await modal.waitFor({ state: 'detached' });
        await page.waitForFunction(() => document.querySelector('[aria-label="Camada de destino da vetorização"]')?.value === 'TopologySmoke');

        const box = await page.locator('#map-container').boundingBox();
        const previousFrame = await page.evaluate(() => window.mapEngine.latestFramePresented);
        // Four crossings well inside the map and away from panels/toolbar.
        for (const [x, y] of [[0.4, 0.4], [0.6, 0.7], [0.4, 0.7], [0.6, 0.4]]) {
            await page.mouse.click(box.x + box.width * x, box.y + box.height * y);
        }
        await page.waitForFunction(() => document.querySelector('.toolbar-status')?.textContent.includes('4 ponto(s)'));
        assert(await page.getByLabel('Camada de destino da vetorização').isDisabled(), 'Destination must stay fixed during a sketch.');
        await toolbar.getByRole('button', { name: 'CONCLUIR', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.toolbar-status')?.textContent.includes('cruza'));
        assert(await toolbar.getByRole('button', { name: 'DESFAZER', exact: true }).isEnabled(), 'Invalid sketch must retain undo history.');
        // Blazor text updates before the asynchronous map image. Verify the real
        // canvas, not just controls that could pass with a completely blank map.
        await page.waitForFunction(previous => window.mapEngine.latestFramePresented > previous &&
            window.mapEngine.latestFramePresented >= window.mapEngine.latestFrameRequested, previousFrame);
        // The map is a cross-origin local-server image; inspect browser output
        // instead of attempting getImageData on its deliberately tainted canvas.
        const pixels = PNG.sync.read(await page.screenshot({ scale: 'css', clip: {
            x: box.x + box.width * 0.35, y: box.y + box.height * 0.35,
            width: box.width * 0.3, height: box.height * 0.4
        } })).data;
        let greenPixels = 0;
        for (let i = 0; i < pixels.length; i += 4)
            if (pixels[i + 1] > 180 && pixels[i] < 100 && pixels[i + 2] < 200) greenPixels++;
        assert(greenPixels > 100, 'The map canvas must visibly render the preserved green sketch.');
        await page.screenshot({ path: path.join(output, 'invalid-polygon.png') });
        await toolbar.getByRole('button', { name: 'DESFAZER', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.toolbar-status')?.textContent.includes('3 ponto(s)'));
        await toolbar.getByRole('button', { name: 'REFAZER', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.toolbar-status')?.textContent.includes('4 ponto(s)'));
        await toolbar.getByRole('button', { name: 'CANCELAR', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.toolbar-status')?.textContent.includes('0 ponto(s)'));
        assert.deepEqual(errors, [], 'The actual app must report no page errors or failure dialogs.');
        console.log(JSON.stringify({ result: 'passed', directory, screenshot: path.join(output, 'invalid-polygon.png'),
            coverage: 'MAUI startup, layer creation, four map clicks, topology rejection, preserved undo/redo, cancel' }));
    } finally {
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
