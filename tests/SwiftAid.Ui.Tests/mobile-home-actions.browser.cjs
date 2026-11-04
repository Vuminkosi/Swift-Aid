const assert = require("node:assert/strict");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");

const hostDll = path.resolve("tests/SwiftAid.Ui.TestHost/bin/Debug/net10.0/SwiftAid.Ui.TestHost.dll");
const hostContentRoot = path.resolve("tests/SwiftAid.Ui.TestHost");

async function startHost(child) {
    let output = "";
    child.stdout.on("data", chunk => { output += chunk.toString(); });
    child.stderr.on("data", chunk => { output += chunk.toString(); });
    const deadline = Date.now() + 30000;

    while (Date.now() < deadline) {
        if (child.exitCode !== null) {
            throw new Error(`UI test host exited before readiness (${child.exitCode}).\n${output}`);
        }

        const listener = output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/);
        if (listener) {
            const baseUrl = listener[1];
            try {
                const response = await fetch(baseUrl);
                if (response.ok && (await response.text()).includes("SwiftAid shared component test fixture")) {
                    return baseUrl;
                }
            } catch {
                // Kestrel may announce its ephemeral port before the first request is ready.
            }
        }

        await new Promise(resolve => setTimeout(resolve, 200));
    }

    throw new Error(`Timed out waiting for the UI test host.\n${output}`);
}

async function main() {
    const host = spawn("dotnet", [hostDll, "--urls", "http://127.0.0.1:0"], {
        cwd: hostContentRoot,
        stdio: ["ignore", "pipe", "pipe"],
        windowsHide: true,
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development" }
    });

    let browser;
    try {
        const baseUrl = await startHost(host);
        browser = await chromium.launch({ channel: "chrome", headless: true });
        const context = await browser.newContext({ viewport: { width: 390, height: 844 } });
        const page = await context.newPage();
        const pageErrors = [];
        page.on("pageerror", error => pageErrors.push(error.message));

        const actionLinks = [
            ["Report a case", "/report"],
            ["Open my cases", "/my-cases"],
            ["Open follow-ups", "/follow-ups"],
            ["Open community sessions", "/sessions"]
        ];
        const states = {
            Loading: async () => await page.getByRole("progressbar", { name: "Loading your work summary" }).waitFor(),
            Unavailable: async () => await page.getByText("Your work summary is unavailable right now.").waitFor(),
            Denied: async () => await page.getByText("Your account cannot view this work summary.").waitFor(),
            Empty: async () => {
                await page.getByText("No recent activity.").waitFor();
                await page.getByText("You have no assigned work right now.").waitFor();
            },
            Stale: async () => await page.getByText("This information may be out of date.").waitFor(),
            Partial: async () => await page.getByText("Some work information is unavailable.").waitFor(),
            FreshnessUnknown: async () => await page.getByText("We can’t confirm how recent this information is.").waitFor(),
            Populated: async () => await page.getByText("Case reported").waitFor()
        };

        const expectedCounts = {
            Loading: { cases: "Unavailable", followUps: "Unavailable" },
            Unavailable: { cases: "Unavailable", followUps: "Unavailable" },
            Denied: { cases: "Withheld", followUps: "Withheld" },
            Empty: { cases: "0", followUps: "0" },
            Stale: { cases: "3", followUps: "1" },
            Partial: { cases: "4", followUps: "Unavailable" },
            FreshnessUnknown: { cases: "5", followUps: "2" },
            Populated: { cases: "4", followUps: "2" }
        };
        assert.deepEqual(
            Object.keys(expectedCounts).sort(),
            Object.keys(states).sort(),
            "Every overview state must have an explicit count-state expectation"
        );

        for (const [state, assertState] of Object.entries(states)) {
            await page.goto(`${baseUrl}/home-surface/${state}`, { waitUntil: "networkidle" });
            await page.locator(".signed-home-mobile").waitFor({ state: "visible" });
            await page.getByRole("region", { name: "Quick actions" }).waitFor({ state: "visible" });
            await assertState();
            // Ellai is intentionally unavailable until its access contract and runnable entry path exist.
            // Replace these fail-closed assertions with a real journey check when that contract is approved.
            assert.equal(await page.getByRole("button", { name: "Open Ellai actions" }).count(), 0,
                `${state}: unavailable Ellai launcher must not be exposed`);
            assert.equal(await page.getByText("Ask Ellai", { exact: true }).count(), 0,
                `${state}: unavailable Ellai call to action must not be exposed`);

            for (const [name, href] of actionLinks) {
                const link = page.getByRole("link", { name, exact: true });
                await link.waitFor({ state: "visible" });
                assert.equal(await link.getAttribute("href"), href, `${state}: ${name} destination changed`);
            }

            assert.doesNotMatch(
                await page.locator("body").innerText(),
                /\b(sample data|demo mode|fake service)\b/i,
                `${state}: development-service language leaked into the product surface`
            );

            const caseCount = await page.locator(".signed-home-action--green .mud-typography-caption").innerText();
            const followUpCount = await page.locator(".signed-home-action--teal .mud-typography-caption").innerText();
            assert.equal(caseCount, expectedCounts[state].cases, `${state}: case count did not match its explicit count state`);
            assert.equal(followUpCount, expectedCounts[state].followUps, `${state}: follow-up count did not match its explicit count state`);
        }

        assert.deepEqual(pageErrors, [], `Unexpected browser errors: ${JSON.stringify(pageErrors)}`);
        console.log(JSON.stringify({
            result: "PASS",
            viewport: { width: 390, height: 844 },
            states: Object.keys(states),
            quickActionDestinationsVerified: actionLinks,
            explicitCountStatesVerified: expectedCounts,
            pageErrors
        }, null, 2));
        await context.close();
    } finally {
        if (browser) await browser.close();
        if (host.exitCode === null) {
            host.kill();
            await new Promise(resolve => host.once("exit", resolve));
        }
    }
}

main().catch(error => {
    console.error(error.stack || error);
    process.exitCode = 1;
});
