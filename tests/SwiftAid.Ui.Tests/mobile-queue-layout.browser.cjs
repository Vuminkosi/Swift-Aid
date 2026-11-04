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
        for (const width of [320, 390, 768, 1024, 1280]) {
            await page.setViewportSize({ width, height: 900 });
            await page.goto(base + '/mobile-queue-fixture');
            await page.getByRole('heading', { name: 'Mobile queue fixture' }).waitFor();
            const mobile = width < 1280;
            const cases = page.locator('.case-queue');
            const workspace = page.locator('.operations-case-queue');
            const monitoring = page.locator('.monitoring-queue');
            const capability = page.locator('.operations-capability');
            const coordination = page.locator('.intake-coordination');
            const conflict = page.locator('[aria-labelledby="session-conflict-heading"]');
            const map = page.locator('.operations-map');
            await (mobile ? cases.getByRole('list', { name: 'Assigned cases' }) : cases.locator('table')).waitFor();
            await (mobile ? workspace.locator('.operations-case-queue__rows') : workspace.locator('table')).waitFor();
            await (mobile ? monitoring.getByRole('list', { name: 'Open monitoring work' }) : monitoring.locator('table')).waitFor();
            await (mobile ? capability.getByRole('list', { name: 'Fixture results results' }) : capability.locator('table')).waitFor();
            await (mobile ? coordination.getByRole('list', { name: 'Unrouted intake reports' }) : coordination.locator('table')).waitFor();
            await (mobile ? conflict.getByRole('list', { name: 'Proposed and current session details' }) : conflict.locator('table')).waitFor();
            await (mobile ? map.getByRole('list', { name: 'Area totals by location' }) : map.locator('table')).waitFor();
            assert.equal(await page.locator('table:visible').count(), mobile ? 0 : 7);
            await workspace.getByText('Synthetic observation').waitFor();
            await workspace.getByText('Eswatini', { exact: true }).waitFor();
            await workspace.getByText('Under review', { exact: true }).waitFor();
            await monitoring.getByText('Synthetic follow-up', { exact: true }).waitFor();
            assert.equal(await monitoring.getByText('private-actor-id', { exact: true }).count(), 0);
            await monitoring.getByRole('button', { name: 'Next', exact: true }).click();
            await monitoring.getByText('Synthetic follow-up 25', { exact: true }).waitFor();
            assert.equal(await monitoring.getByText('Synthetic follow-up', { exact: true }).count(), 0);
            await monitoring.getByRole('button', { name: 'Previous', exact: true }).click();
            await capability.getByText('Fixture summary').waitFor();
            await capability.getByText(/42/).first().waitFor();
            if (mobile) await capability.getByText(/Updated:/).waitFor();
            await conflict.getByText(/Proposed title/).waitFor();
            await conflict.getByText(/Current title/).waitFor();
            await map.getByText('Eswatini', { exact: true }).first().waitFor();
            await coordination.getByRole('button', { name: /review/i }).first().click();
            await coordination.getByLabel('Coordination detail', { exact: true }).waitFor();
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `overflow at ${width}px`);
        }
        assert.deepEqual(errors, []);
        assert(!output.includes('Unhandled exception'), output);
        console.log('PASS: synthetic queues, session comparison, intake review, surveillance totals, responsive lists and horizontal fit at 320/390/768/1024/1280px.');
    } finally {
        if (browser) await browser.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
