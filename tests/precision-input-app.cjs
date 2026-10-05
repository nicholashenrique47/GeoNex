// Run after digitizing-app-smoke.cjs against the same disposable MAUI instance.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const path = require('node:path');

(async () => {
    assert(process.env.GEONEX_TEST_CDP);
    const browser = await chromium.connectOverCDP(process.env.GEONEX_TEST_CDP);
    try {
        const page = browser.contexts().flatMap(c => c.pages()).find(p => p.url() === 'https://0.0.0.1/');
        assert(page);
        await page.setViewportSize({ width: 1440, height: 1000 });
        await page.evaluate(() => document.activeElement?.blur());
        await page.keyboard.press('d');
        const toolbar = page.locator('.tool-workbench');
        await toolbar.waitFor({ state: 'visible' });
        assert(await toolbar.evaluate(el => el.getBoundingClientRect().top >=
            document.querySelector('.top-bar').getBoundingClientRect().bottom + 4),
            'The digitizing bar must sit below the top menu.');
        await toolbar.getByRole('button', { name: 'Simples', exact: true }).click();
        const precisionShortcut = toolbar.getByRole('button', { name: 'Inserir coordenadas (F6)', exact: true });
        await precisionShortcut.waitFor({ state: 'visible' });
        await precisionShortcut.click();
        const coordinates = page.locator('#hud-f6');
        await coordinates.waitFor({ state: 'visible' });
        assert(await page.locator('.geonex-ui').evaluate(el => el.classList.contains('digitizing-simple')),
            'Opening F6 must not silently switch the vectorization level.');
        await page.getByRole('button', { name: 'Fechar coordenadas F6', exact: true }).click();
        await toolbar.getByRole('button', { name: 'Simples', exact: true }).click();
        await toolbar.getByRole('button', { name: 'Simples', exact: true }).focus();
        await page.keyboard.press('F6');
        await page.waitForFunction(() => document.activeElement?.getAttribute('aria-label') === 'Coordenada X');
        assert(await page.locator('.geonex-ui').evaluate(el => el.classList.contains('digitizing-simple')),
            'F6 opens only coordinate entry; it must not change the mode.');
        const precision = page.locator('#hud-f6');
        const x = page.getByLabel('Coordenada X', { exact: true });
        const y = page.getByLabel('Coordenada Y', { exact: true });
        async function count(n) {
            await page.waitForFunction(n => document.querySelector('.toolbar-status').textContent.includes(`${n} ponto(s)`), n);
        }
        assert(await precision.getByRole('button', { name: 'Adicionar coordenada', exact: true }).isDisabled());
        await x.fill('500000'); await y.fill('7400000');
        await y.press('Enter');
        await count(1);
        assert(await precision.isVisible(), 'Repeated coordinate input stays available.');
        await precision.getByRole('button', { name: 'Adicionar coordenada', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.toolbar-status').textContent.includes('coincide'));
        await page.evaluate(() => document.activeElement?.blur());
        await page.keyboard.press('Tab');
        await page.waitForFunction(() => document.activeElement?.getAttribute('aria-label') === 'Distância polar em metros');
        const cogo = page.locator('#hud-cogo');
        await cogo.waitFor({ state: 'visible' });
        assert(await precision.isVisible(), 'COGO is a separate floating submenu and does not replace F6 coordinates.');
        assert(await page.locator('.geonex-ui').evaluate(el => el.classList.contains('digitizing-simple')),
            'Opening COGO must also preserve the selected vectorization level.');
        const distance = page.getByLabel('Distância polar em metros', { exact: true });
        const azimuth = page.getByLabel('Azimute polar em graus', { exact: true });
        await distance.fill('-1');
        assert(await cogo.getByRole('button', { name: 'Projetar ponto', exact: true }).isDisabled());
        await distance.fill('100'); await azimuth.fill('90'); await azimuth.press('Enter');
        await count(2);
        await azimuth.fill('0'); await azimuth.press('Enter');
        await count(3);
        await page.waitForFunction(() => document.querySelector('.sketch-metrics').textContent.includes('200,00 m'));
        assert((await toolbar.locator('.sketch-metrics').innerText()).includes('5.000,00 m²'));
        await page.getByLabel('Distância da restrição em metros', { exact: true }).fill('25');
        const constraintMode = page.getByLabel('Modo da restrição', { exact: true });
        assert(['false', 'true'].includes(await constraintMode.inputValue()), 'Constraint mode must show an actual selected option.');
        await constraintMode.selectOption('true');
        assert.equal(await constraintMode.inputValue(), 'true');
        await cogo.getByRole('button', { name: 'Ativar restrição', exact: true }).click();
        await cogo.getByRole('button', { name: 'Desativar restrição', exact: true }).waitFor();
        await cogo.getByRole('button', { name: 'Desativar restrição', exact: true }).click();
        for (const [width, height] of [[1440,900], [760,600], [390,600]]) {
            await page.setViewportSize({ width, height });
            await page.waitForFunction(() => ['hud-f6', 'hud-cogo'].every(id => {
                const el = document.getElementById(id);
                if (!el) return true;
                const r = el.getBoundingClientRect();
                return r.left >= 0 && r.right <= innerWidth && r.top >= 0 && r.bottom <= innerHeight;
            }));
            for (const control of await page.locator('#hud-f6 input, #hud-f6 button, #hud-cogo input, #hud-cogo select, #hud-cogo button').all()) {
                await control.scrollIntoViewIfNeeded();
                assert(await control.evaluate(el => {
                    const r = el.getBoundingClientRect();
                    const hit = document.elementFromPoint(r.x + r.width/2, r.y + r.height/2);
                    return r.left >= 0 && r.right <= innerWidth && r.top >= 0 && r.bottom <= innerHeight && (hit === el || el.contains(hit));
                }), 'Precision controls must remain reachable without overlapping panels.');
            }
            await page.screenshot({ path: path.resolve(__dirname, `../test-results/digitizing-app/precision-${width}.png`) });
        }
        await page.getByRole('button', { name: 'Fechar COGO', exact: true }).click();
        await page.getByRole('button', { name: 'Fechar coordenadas F6', exact: true }).click();
        await precision.waitFor({ state: 'hidden' });
        await cogo.waitFor({ state: 'hidden' });
        await count(3);
        await toolbar.getByRole('button', { name: 'Cancelar', exact: true }).click();
        await count(0);
        console.log('Precision input passed: F6/Tab focus, Enter submission, duplicate/negative rejection, 100m polar vectors, restriction, 1440/760/390px controls.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
