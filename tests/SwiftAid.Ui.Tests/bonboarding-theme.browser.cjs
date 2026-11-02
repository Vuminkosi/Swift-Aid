const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || 'playwright');

(async () => {
    const browser = await chromium.launch({ headless: true,
        ...(process.env.SWIFTAID_CHROMIUM_PATH ? { executablePath: process.env.SWIFTAID_CHROMIUM_PATH } : {}) });
    try {
        const page = await browser.newPage();
        const library = path.join(process.env.USERPROFILE, '.nuget/packages/bit.bonboarding/10.7.0/staticwebassets/bit-bonboarding.css');
        await page.setContent('<html data-theme="agentic"><body><div class="bit-bonboarding-root" data-bonboarding-theme="system"><div class="bit-bonboarding-popover" data-bonboarding-placed="true"><span class="bit-bonboarding-title">Ellai guidance</span><div class="bit-bonboarding-arrow"></div><button class="bit-bonboarding-button bit-bonboarding-button-primary">Next</button></div></div></body></html>');
        await page.addStyleTag({ content: fs.readFileSync(library, 'utf8') });
        await page.addStyleTag({ content: fs.readFileSync('src/SwiftAid.UI/wwwroot/css/swiftaid-theme.css', 'utf8') });
        for (const width of [390, 1280]) {
            await page.setViewportSize({ width, height: 900 });
            for (const osTheme of ['light', 'dark']) {
                await page.emulateMedia({ colorScheme: osTheme });
                for (const theme of ['light', 'dark', 'light']) {
                    await page.evaluate(mode => document.documentElement.dataset.colorMode = mode, theme);
                    const colors = await page.evaluate(() => {
                        const popup = getComputedStyle(document.querySelector('.bit-bonboarding-popover'));
                        const arrow = getComputedStyle(document.querySelector('.bit-bonboarding-arrow'));
                        return { surface: popup.backgroundColor, text: popup.color, arrow: arrow.backgroundColor, scheme: popup.colorScheme };
                    });
                    assert.equal(colors.surface, theme === 'dark' ? 'rgb(21, 30, 24)' : 'rgb(255, 255, 255)');
                    assert.equal(colors.text, theme === 'dark' ? 'rgb(237, 242, 238)' : 'rgb(33, 33, 33)');
                    assert.equal(colors.arrow, colors.surface);
                    assert.equal(colors.scheme, theme);
                }
            }
        }
        console.log('PASS: Bonboarding palette and arrow follow live application theme changes at 390/1280, regardless of OS preference. CSS rendering fixture only.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
