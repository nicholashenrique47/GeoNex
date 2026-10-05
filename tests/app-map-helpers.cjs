async function readyMap(page) {
    // Allow layout and ResizeObserver to see panel/toolbar changes before asserting a frame.
    await page.evaluate(async () => {
        await new Promise(requestAnimationFrame);
        await new Promise(requestAnimationFrame);
    });
    await page.waitForFunction(() => {
        const map = document.getElementById('map-container'), engine = window.mapEngine;
        return map && engine.presentedViewport && !engine.viewportResizePending &&
            Math.abs(engine.presentedViewport.width - map.clientWidth) <= 1 &&
            Math.abs(engine.presentedViewport.height - map.clientHeight) <= 1 &&
            engine.latestFramePresented >= engine.latestFrameRequested;
    });
    return page.locator('#map-container').boundingBox();
}
module.exports = { readyMap };
