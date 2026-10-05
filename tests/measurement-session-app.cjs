// Run after digitizing-app-smoke.cjs against the same disposable MAUI instance.
const { chromium } = require('playwright');
const { readyMap } = require('./app-map-helpers.cjs');
const assert = require('node:assert/strict');
const path = require('node:path');

(async () => {
    assert(process.env.GEONEX_TEST_CDP, 'Provide a disposable WebView CDP endpoint.');
    const browser = await chromium.connectOverCDP(process.env.GEONEX_TEST_CDP);
    try {
        const page = browser.contexts().flatMap(c => c.pages()).find(p => p.url() === 'https://0.0.0.1/');
        assert(page, 'MAUI app required.');
        await page.setViewportSize({ width: 1440, height: 1000 });
        await page.evaluate(() => document.activeElement?.blur());
        await page.keyboard.press('m');
        const panel = page.locator('.measurement-workbench');
        await panel.waitFor({ state: 'visible' });
        await readyMap(page);
        const clear = panel.getByRole('button', { name: 'Limpar rastro', exact: true });
        if (await clear.isEnabled()) await clear.click();
        await panel.getByRole('button', { name: 'Distância', exact: true }).click();
        const length = page.getByTestId('measurement-length');
        const state = page.getByTestId('measurement-state');
        async function count(n) {
            await page.waitForFunction(n => document.querySelector('[data-testid="measurement-state"]').textContent.includes(`${n} ponto(s)`), n);
        }
        async function clickPoint(x, y, n) { await page.mouse.click(x, y); await count(n); }
        async function finish() {
            await page.evaluate(() => document.activeElement?.blur());
            await page.keyboard.press('Enter');
            await page.waitForFunction(() => document.querySelector('[data-testid="measurement-state"]').textContent.includes('Resultado confirmado'));
        }
        // Capture the app's copy payload without modifying the user's real clipboard.
        await page.evaluate(() => {
            window.originalMeasurementCopy = navigator.clipboard?.writeText;
            window.originalMeasurementExec = document.execCommand;
            window.measurementCopies = [];
            if (navigator.clipboard) navigator.clipboard.writeText = async text => window.measurementCopies.push(text);
            document.execCommand = function(command, ...args) {
                if (command === 'copy') { window.measurementCopies.push(document.activeElement.value); return true; }
                return window.originalMeasurementExec.call(document, command, ...args);
            };
        });
        await clickPoint(450, 450, 1);
        assert(await panel.getByRole('button', { name: 'Concluir medição', exact: true }).isDisabled());
        await clickPoint(650, 450, 2);
        await page.mouse.move(850, 650);
        await panel.getByRole('button', { name: 'Copiar resultado', exact: true }).click();
        await page.waitForFunction(() => window.measurementCopies.length === 1);
        await page.mouse.move(900, 800);
        await panel.getByRole('button', { name: 'Copiar resultado', exact: true }).click();
        await page.waitForFunction(() => window.measurementCopies.length === 2);
        const copies = await page.evaluate(() => window.measurementCopies);
        assert.equal(copies[0], copies[1], 'Copied confirmed geometry must not depend on the cursor.');
        assert(copies[0].includes('Pontos confirmados: 2 (cursor excluído)'));
        await finish();
        const confirmed = (await length.innerText()).trim();
        assert(copies[0].includes(`Comprimento: ${confirmed}`), 'Frozen value must match copied confirmed length.');
        await page.mouse.move(800, 700);
        await page.mouse.click(900, 750);
        await page.evaluate(() => window.mapEngine.dotNetHelper.invokeMethodAsync('ReceberMovimentoFerramentas', 900, 750, true));
        assert.equal((await length.innerText()).trim(), confirmed);
        assert((await state.innerText()).includes('2 ponto(s)'), 'Clicks must not alter a completed measurement.');
        await page.screenshot({ path: path.resolve(__dirname, '../test-results/digitizing-app/measurement-confirmed.png') });
        await panel.getByRole('button', { name: 'Continuar medição', exact: true }).click();
        await clickPoint(650, 650, 3);
        await finish();
        await panel.getByRole('button', { name: 'Desfazer', exact: true }).click();
        await count(2);
        assert(!(await state.innerText()).includes('Resultado confirmado'), 'Undo reopens the measurement.');
        await panel.getByRole('button', { name: 'Refazer', exact: true }).click();
        await count(3);
        await panel.getByRole('button', { name: 'Área', exact: true }).click();
        await finish();
        assert(!(await page.getByTestId('measurement-area').innerText()).includes('—'));
        await panel.getByRole('button', { name: 'Limpar rastro', exact: true }).click();
        await count(0);
        assert((await state.innerText()).includes('Aguardando pontos'));
        // Crossing ring must remain editable, never become a confirmed area.
        for (const [x, y, n] of [[450,450,1], [750,650,2], [450,650,3], [750,450,4]]) await clickPoint(x,y,n);
        await panel.getByRole('button', { name: 'Concluir medição', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.measurement-error')?.textContent.includes('Área indisponível'));
        assert(!(await state.innerText()).includes('Resultado confirmado'));
        await panel.getByRole('button', { name: 'Limpar rastro', exact: true }).click();
        await count(0);
        console.log('Measurement session passed: committed copy, Enter finish, immutable result, resume, undo/redo, valid/invalid area, reset.');
    } finally {
        for (const page of browser.contexts().flatMap(c => c.pages())) {
            if (page.url() === 'https://0.0.0.1/') await page.evaluate(() => {
                if (window.originalMeasurementCopy) navigator.clipboard.writeText = window.originalMeasurementCopy;
                if (window.originalMeasurementExec) document.execCommand = window.originalMeasurementExec;
            }).catch(() => {});
        }
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
