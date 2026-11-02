const assert = require('node:assert/strict');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || 'playwright');

(async () => {
    const host = spawn('dotnet', [path.resolve('tests/SwiftAid.Ui.TestHost/bin/Debug/net10.0/SwiftAid.Ui.TestHost.dll'), '--urls', 'http://127.0.0.1:0'], {
        cwd: path.resolve('tests/SwiftAid.Ui.TestHost'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' }
    });
    let output = '', browser;
    host.stdout.on('data', data => { output += data; });
    host.stderr.on('data', data => { output += data; });
    try {
        const deadline = Date.now() + 30000;
        while (!/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/.test(output)) {
            assert(host.exitCode === null && Date.now() < deadline, output);
            await new Promise(resolve => setTimeout(resolve, 100));
        }
        const base = output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/)[1];
        browser = await chromium.launch({ headless: true, ...(process.env.SWIFTAID_CHROMIUM_PATH ? { executablePath: process.env.SWIFTAID_CHROMIUM_PATH } : {}) });
        const page = await browser.newPage();
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        for (const width of [1280, 390, 320]) {
            await page.setViewportSize({ width, height: 844 });
            await page.goto(base + '/ellai-microphone-fixture');
            await page.locator('[data-fixture-ready="true"]').waitFor();
            const mic = page.locator('[data-ellai-microphone-toggle]');
            await page.getByRole('button', { name: 'Turn on microphone', exact: true }).click();
            await page.getByRole('button', { name: 'Turn off microphone', exact: true }).waitFor();
            assert.equal(await mic.getAttribute('aria-pressed'), 'true');
            assert.equal(await page.locator('#capture-starts').textContent(), '1');
            assert.equal(await page.getByRole('button', { name: 'Turn off Ellai voice', exact: true }).count(), 1);
            await mic.click();
            await page.getByRole('button', { name: 'Turn on microphone', exact: true }).waitFor();
            assert.equal(await page.locator('#capture-stops').textContent(), '1');
            assert.equal(await page.getByRole('button', { name: 'Turn off Ellai voice', exact: true }).count(), 1);
            const status = await page.locator('.ellai-composer__status-row').boundingBox();
            const controls = await page.locator('.ellai-composer__speech-action').boundingBox();
            assert(controls.x >= status.x && controls.x + controls.width <= status.x + status.width + 1);
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
            await page.getByRole('button', { name: 'Turn off Ellai voice', exact: true }).click();
            await page.getByRole('button', { name: 'Listen to Ellai', exact: true }).waitFor();
            assert.equal(await mic.count(), 0);
        }
        assert.deepEqual(errors, []);
        console.log('PASS: shared composer microphone callbacks independent of voice, 1280/390/320px; synthetic capture only.');
    } finally {
        await browser?.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
