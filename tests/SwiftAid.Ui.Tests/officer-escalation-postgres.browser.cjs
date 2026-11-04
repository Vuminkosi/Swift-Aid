const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || 'playwright');

(async () => {
    const browser = await chromium.launch({ headless: true });
    try {
        const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        await page.goto(process.env.SWIFTAID_OFFICER_BROWSER_URL + '/officer-review');
        await page.getByRole('button', { name: 'Load available escalations and officers' }).click();
        await page.getByRole('combobox', { name: 'Raised escalation' }).click();
        await page.getByRole('option', { name: /^Raised / }).click();
        await page.getByRole('combobox', { name: 'Eligible officer' }).click();
        await page.getByRole('option').first().click();
        const utc = date => date.toISOString().slice(0, 16).replace('T', ' ');
        await page.getByLabel('Acknowledgment deadline (UTC)').fill(utc(new Date(Date.now() + 7200000)));
        await page.getByLabel('Follow-up deadline (UTC)').fill(utc(new Date(Date.now() + 86400000)));
        await page.getByRole('button', { name: 'Review assignment' }).click();
        await page.getByRole('group', { name: 'Review officer escalation assignment' }).waitFor();
        await page.getByRole('button', { name: 'Approve reviewed assignment' }).click();
        await page.getByText('Approval recorded. It expires after 15 minutes', { exact: false }).waitFor();
        await page.getByRole('button', { name: 'Assign approved officer' }).click();
        await page.getByText('Officer assignment and follow-up recorded.', { exact: false }).waitFor();
        assert.deepEqual(errors, []);
        process.stdout.write('PASS: browser review and assignment reached PostgreSQL.\n');
    } finally {
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
