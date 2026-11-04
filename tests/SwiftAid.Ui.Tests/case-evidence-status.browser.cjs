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
            await page.setViewportSize({ width, height: 1000 });
            await page.emulateMedia({ colorScheme: width === 390 ? 'dark' : 'light', reducedMotion: 'no-preference' });
            await page.goto(base + '/case-evidence-fixture');
            await page.locator('[data-fixture-ready="true"]').waitFor();
            assert.equal(await page.locator('.case-report-evidence__observations li').count(), 3);
            const panels = page.locator('.case-report-evidence__panel');
            const first = await panels.nth(0).boundingBox(), second = await panels.nth(1).boundingBox();
            if (width > 760) assert(second.x > first.x && Math.abs(first.y - second.y) < 2);
            else assert(second.y >= first.y + first.height);
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
            const links = page.locator('.case-report-evidence__facility a');
            assert.equal(await links.count(), 2);
            assert.equal(await links.first().getAttribute('target'), '_blank');
            assert((await links.first().getAttribute('rel')).includes('noopener'));
            assert.equal(await page.locator('.ellai-status__motion').count(), 10);
            assert(await page.locator('.ellai-status__motion i').evaluateAll(items => items.every(item => getComputedStyle(item).animationName !== 'none')));
            assert.equal(await page.locator('.ellai-status__dot').first().evaluate(item => getComputedStyle(item).fontWeight), '900');
            await page.screenshot({ path: `.codex/case-evidence-status-${width}.png`, fullPage: true });
            await page.emulateMedia({ reducedMotion: 'reduce' });
            assert(await page.locator('.ellai-status__motion i, .ellai-status__dot').evaluateAll(items => items.every(item => getComputedStyle(item).animationName === 'none')));
        }
        assert.deepEqual(errors, []);
        console.log('PASS: evidence panels, observation tags, map links, ten animated statuses and reduced motion at 1280/390px. Synthetic fixture; no facility-service or device validation.');
    } finally {
        await browser?.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
