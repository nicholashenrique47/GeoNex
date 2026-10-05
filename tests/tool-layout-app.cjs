// Run after digitizing-app-smoke.cjs on the same disposable MAUI instance.
const { chromium } = require('playwright');
const { readyMap } = require('./app-map-helpers.cjs');
const assert = require('node:assert/strict');
const path = require('node:path');

(async () => {
    assert(process.env.GEONEX_TEST_CDP, 'Provide the disposable WebView CDP endpoint.');
    const browser = await chromium.connectOverCDP(process.env.GEONEX_TEST_CDP);
    try {
        const page = browser.contexts().flatMap(c => c.pages()).find(p => p.url() === 'https://0.0.0.1/');
        assert(page, 'MAUI app required.');
        const errors = [];
        page.on('pageerror', e => errors.push(e.message));

        async function mapRect() {
            const r = await readyMap(page);
            return Object.fromEntries(['x', 'y', 'width', 'height'].map(k => [k, Math.round(r[k])]));
        }
        async function assertMapStable(expected, label) {
            assert.deepEqual(await mapRect(), expected, `Opening ${label} must not resize or offset the map viewport.`);
        }
        async function assertFrameFits() {
            await readyMap(page);
            const layout = await page.evaluate(() => {
                const map = document.getElementById('map-container').getBoundingClientRect();
                const header = document.querySelector('.top-bar').getBoundingClientRect();
                const footer = document.querySelector('.bottom-status-bar').getBoundingClientRect();
                return { map: { x: map.x, y: map.y, width: map.width, height: map.height },
                    headerBottom: header.bottom, footerTop: footer.top, viewport: { width: innerWidth, height: innerHeight } };
            });
            assert(layout.map.width >= Math.min(250, layout.viewport.width - 100), 'Map must retain a usable width on narrow screens.');
            assert(layout.map.height >= 100 && layout.map.x >= 0 && layout.map.y >= layout.headerBottom - 1 &&
                layout.map.y + layout.map.height <= layout.footerTop + 1 && layout.map.x + layout.map.width <= layout.viewport.width + 1,
                `Map must remain inside the fixed app frame: ${JSON.stringify(layout)}`);
            return layout;
        }
        async function reachable(locator) {
            await locator.scrollIntoViewIfNeeded();
            const state = await locator.evaluate(el => {
                const r = el.getBoundingClientRect();
                const hit = document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2);
                return { inside: r.left >= 0 && r.right <= innerWidth + 1 && r.top >= 0 && r.bottom <= innerHeight + 1,
                    hit: hit === el || el.contains(hit), width: r.width, height: r.height };
            });
            assert(state.inside && state.hit && state.width > 0 && state.height >= 30,
                `Control must be visible, usable and not intercepted: ${await locator.getAttribute('aria-label') || await locator.textContent()} ${JSON.stringify(state)}`);
        }

        for (const width of [1440, 760, 390]) {
            await page.setViewportSize({ width, height: 900 });
            await page.evaluate(() => document.activeElement?.blur());
            const initial = await mapRect();
            await assertFrameFits();

            await page.keyboard.press('m');
            const measuring = page.locator('.measurement-workbench');
            await measuring.waitFor({ state: 'visible' });
            await assertMapStable(initial, 'measurement mode');
            await reachable(measuring.getByRole('button', { name: 'Área', exact: true }));
            await measuring.getByRole('button', { name: 'Área', exact: true }).click();
            await reachable(page.getByLabel('Unidade da área', { exact: true }));
            await page.getByLabel('Unidade da área', { exact: true }).selectOption('ha');
            await reachable(measuring.getByRole('button', { name: 'Copiar resultado', exact: true }));

            const identifyDock = page.locator('.right-panel .panel-dock-toggle');
            await identifyDock.click();
            await measuring.waitFor({ state: 'hidden' });
            assert.equal(await page.locator('.right-panel .panel-rail-label').innerText(), 'Medição', 'The collapsed tool panel must remain discoverable.');
            await assertMapStable(initial, 'collapsed identification panel');
            await identifyDock.click();
            await measuring.waitFor({ state: 'visible' });
            await assertMapStable(initial, 'expanded identification panel');

            const settings = page.locator('.measurement-settings');
            if (!await settings.getAttribute('open').then(v => v !== null)) await settings.locator('summary').click();
            await reachable(page.getByLabel('Tolerância do snap de medição', { exact: true }));
            await page.getByLabel('Tolerância do snap de medição', { exact: true }).selectOption('10');
            await settings.locator('summary').click();
            await page.screenshot({ path: path.resolve(__dirname, `../test-results/digitizing-app/measurement-layout-${width}.png`) });

            await page.evaluate(() => document.activeElement?.blur());
            await page.keyboard.press('d');
            const toolbar = page.locator('.tool-workbench');
            await toolbar.waitFor({ state: 'visible' });
            await assertMapStable(initial, 'simple digitizing mode');
            const simpleBar = await toolbar.boundingBox();
            assert(simpleBar.height <= 64, `Simple digitizing should remain a single compact bar: ${JSON.stringify(simpleBar)}`);

            const layerDock = page.locator('.left-panel .panel-dock-toggle');
            await layerDock.click();
            await page.getByText('CAMADAS', { exact: true }).waitFor({ state: 'visible' });
            await assertMapStable(initial, 'expanded layer panel');
            await layerDock.click();
            await page.getByText('CAMADAS', { exact: true }).waitFor({ state: 'hidden' });
            assert.equal(await page.locator('.left-panel .panel-rail-label').innerText(), 'Camadas', 'The collapsed layer panel must remain discoverable.');
            await assertMapStable(initial, 'collapsed layer panel');

            await toolbar.getByRole('button', { name: 'Avançada', exact: true }).click();
            await page.getByLabel('Modo de construção', { exact: true }).selectOption('RegularPolygon');
            await page.getByLabel('Lados do polígono regular').waitFor({ state: 'visible' });
            assert.equal(await page.getByLabel('Lados do polígono regular').locator('option').count(), 30);
            await page.getByLabel('Lados do polígono regular').selectOption('7');
            for (const control of await toolbar.locator('button, select, summary').all()) {
                if (await control.isVisible()) await reachable(control);
            }
            const before = await toolbar.locator('.toolbar-status').innerText();
            await toolbar.locator('.snap-options summary').click();
            await assertMapStable(initial, 'expanded snapping options');
            await reachable(toolbar.getByLabel('Tolerância do snap', { exact: true }));
            await toolbar.getByLabel('Tolerância do snap', { exact: true }).selectOption('10');
            assert.equal(await toolbar.locator('.toolbar-status').innerText(), before, 'Settings must not add map points.');
            await toolbar.locator('.snap-options summary').click();
            const bounds = await toolbar.boundingBox();
            assert(bounds.y + bounds.height <= 650, 'Advanced tools must leave at least 250px for the map.');
            await page.screenshot({ path: path.resolve(__dirname, `../test-results/digitizing-app/digitizing-layout-${width}.png`) });
            await toolbar.getByRole('button', { name: 'Simples', exact: true }).click();
            await assertMapStable(initial, 'return to simple digitizing');

            if (width < 1000) {
                const menu = page.getByRole('button', { name: 'Menu', exact: true });
                await reachable(menu);
                await menu.click();
                await assertMapStable(initial, 'application menu');
                await reachable(page.locator('#geonex-main-menu').getByRole('button', { name: 'Ferramentas ▾', exact: true }));
                await menu.click();
            }
        }
        assert.deepEqual(errors, []);

        for (const width of [760, 390]) {
            await page.setViewportSize({ width, height: 600 });
            for (const key of ['m', 'd']) {
                await page.evaluate(() => document.activeElement?.blur());
                const before = await mapRect();
                await page.keyboard.press(key);
                await page.waitForFunction(key => key === 'm' ? window.mapEngine.ferramentaAtual === 'Medicao' : window.mapEngine.ferramentaAtual.startsWith('Aquisicao'), key);
                await assertFrameFits();
                await assertMapStable(before, `${key} tool at short height`);
            }
        }
        console.log('Map-first layout: stable viewport, compact digitizing bar, usable dock rails and reachable controls at 1440/760/390px.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
