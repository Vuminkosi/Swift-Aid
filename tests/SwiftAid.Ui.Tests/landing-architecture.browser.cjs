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
        for (const width of [1280, 390]) {
            await page.setViewportSize({ width, height: 900 });
            await page.emulateMedia({ colorScheme: width === 390 ? 'dark' : 'light' });
            await page.goto(base + '/landing-fixture');
            await page.locator('[data-fixture-ready="true"]').waitFor({ state: 'attached' });
            const diagram = page.locator('.platform-flow');
            await diagram.getByRole('heading', { name: 'How SwiftAid comes together' }).waitFor();
            assert.equal(await diagram.locator('.platform-flow__name', { hasText: /^Kokoro$/ }).count(), 1);
            await diagram.getByRole('button', { name: 'Pause flow', exact: true }).click();
            await page.waitForFunction(() => document.querySelector('.platform-flow--paused'));
            assert.equal(await diagram.getByRole('button', { name: 'Play flow', exact: true }).getAttribute('aria-pressed'), 'true');
            await diagram.getByText('Read all connections', { exact: true }).click();
            assert((await diagram.locator('.platform-flow__connections').innerText()).includes('ASP.NET Core → Kokoro'));
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), 'Homepage must not overflow horizontally');
            const overflowingLabels = await diagram.locator('.platform-flow__name, .platform-flow__detail').evaluateAll(labels =>
                labels.filter(label => label.getBBox().width > 180).map(label => label.textContent));
            assert.deepEqual(overflowingLabels, []);
            await page.screenshot({ path: path.resolve(`.codex/landing-kokoro-${width}.png`), fullPage: true });
        }
        assert.deepEqual(errors, []);
        assert(!output.includes('Unhandled exception'), output);
        console.log('PASS: shared landing page at 1280/390px; Kokoro connection, pause interaction, label fit and horizontal containment. Chromium fixture, not deployed-page verification.');
    } finally {
        if (browser) await browser.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
