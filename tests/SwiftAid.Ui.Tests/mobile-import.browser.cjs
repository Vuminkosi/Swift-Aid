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
        await page.setViewportSize({ width: 390, height: 844 });
        await page.goto(`${base}/case-imports-fixture`);
        await page.getByRole('heading', { name: 'Import cases' }).waitFor();
        await page.getByText('No imports for this workspace and country.').waitFor();
        for (const width of [320, 390, 768]) {
            await page.setViewportSize({ width, height: 844 });
            assert.equal(await page.locator('table:visible').count(), 0);
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `import overflow at ${width}px`);
        }
        await page.setViewportSize({ width: 390, height: 844 });
        const columns = ['schema_version', 'command_id', 'case_id', 'external_record_id', 'subject_scope', 'narrative',
            'observations_json', 'captured_at', 'occurred_at', 'country_code', 'region', 'area', 'locality',
            'location_id', 'latitude', 'longitude', 'location_precision', 'accountable_owner_id', 'workspace_id',
            'source_language', 'source_reference'];
        await page.locator('#mobile-case-import-file').setInputFiles({ name: 'synthetic.csv', mimeType: 'text/csv',
            buffer: Buffer.from(`${columns.join(',')}\n${columns.map(() => 'synthetic').join(',')}\n`) });
        await page.getByRole('checkbox', { name: /reviewed these reports/i }).check();
        await page.getByRole('checkbox', { name: /confirm these reports/i }).check();
        await page.getByRole('button', { name: 'Import cases', exact: true }).click();
        await page.getByText('Import complete. Your cases are saved.', { exact: true }).waitFor();
        await page.getByText(/1 saved/).waitFor();
        await page.getByRole('button', { name: 'View result', exact: true }).click();
        await page.getByText('Import complete. Your cases are saved.', { exact: true }).waitFor();
        await page.screenshot({ path: path.resolve('.codex/case-list-browser/mobile-import.png'), fullPage: true });
        await page.goto(`${base}/case-imports-fixture?paused=true`);
        await page.getByRole('button', { name: 'Resume', exact: true }).click();
        await page.getByText('Select the original file and confirm its details to resume.', { exact: true }).waitFor();
        const row = columns.map(() => 'synthetic').join(',');
        await page.locator('#mobile-case-import-file').setInputFiles({ name: 'synthetic.csv', mimeType: 'text/csv',
            buffer: Buffer.from(`${columns.join(',')}\n${row}\n${row}\n`) });
        await page.getByRole('checkbox', { name: /reviewed these reports/i }).check();
        await page.getByRole('checkbox', { name: /confirm these reports/i }).check();
        await page.getByRole('button', { name: 'Resume import', exact: true }).click();
        await page.getByText('This older import did not record a file fingerprint, so the original file cannot be verified automatically.', { exact: true }).waitFor();
        await page.screenshot({ path: path.resolve('.codex/case-list-browser/mobile-import-legacy-warning.png'), fullPage: true });
        await page.getByRole('checkbox', { name: /unchanged original file/i }).check();
        await page.getByRole('button', { name: 'Resume import', exact: true }).click();
        await page.getByText('Import complete. Your cases are saved.', { exact: true }).waitFor();
        await page.getByText(/2 saved/).waitFor();
        await page.goto(`${base}/case-imports-fixture?bound=true`);
        await page.getByRole('button', { name: 'Resume', exact: true }).click();
        await page.getByText('Select the original file and confirm its details to resume.', { exact: true }).waitFor();
        await page.locator('#mobile-case-import-file').setInputFiles({ name: 'changed.csv', mimeType: 'text/csv',
            buffer: Buffer.from(`${columns.join(',')}\n${row}\n${columns.map(() => 'changed').join(',')}\n`) });
        await page.getByRole('checkbox', { name: /reviewed these reports/i }).check();
        await page.getByRole('checkbox', { name: /confirm these reports/i }).check();
        await page.getByRole('button', { name: 'Resume import', exact: true }).click();
        await page.getByText('The selected file or import settings differ from the original import. Select the original file and settings to resume.', { exact: true }).waitFor();
        await page.getByText(/1 saved/).waitFor();
        await page.locator('#mobile-case-import-file').setInputFiles({ name: 'synthetic.csv', mimeType: 'text/csv',
            buffer: Buffer.from(`${columns.join(',')}\n${row}\n${row}\n`) });
        await page.getByRole('button', { name: 'Resume import', exact: true }).click();
        await page.getByText('Import complete. Your cases are saved.', { exact: true }).waitFor();
        await page.getByText(/2 saved/).waitFor();
        await page.goto(`${base}/case-imports-fixture?bound=true`);
        await page.getByRole('button', { name: 'Resume', exact: true }).click();
        await page.getByText('Select the original file and confirm its details to resume.', { exact: true }).waitFor();
        await page.locator('#mobile-case-import-file').setInputFiles({ name: 'synthetic.csv', mimeType: 'text/csv',
            buffer: Buffer.from(`${columns.join(',')}\n${row}\n${row}\n`) });
        await page.getByRole('checkbox', { name: /reviewed these reports/i }).check();
        await page.getByRole('checkbox', { name: /confirm these reports/i }).check();
        await page.getByRole('button', { name: 'Revoke import access', exact: true }).click();
        await page.getByRole('button', { name: 'Resume import', exact: true }).click();
        await page.getByText('Your current access does not permit this import. Saved cases remain available to authorized members.', { exact: true }).waitFor();
        assert.equal(await page.getByText(/1 saved/).count(), 0);
        assert.deepEqual(errors, []);
        assert(!output.includes('Unhandled exception'), output);
        console.log('PASS: mobile import submits, resumes legacy after warning, rejects changed files, resumes originals, and clears data on access revocation at 320/390/768px.');
    } finally {
        if (browser) await browser.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
