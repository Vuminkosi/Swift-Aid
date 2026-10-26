const assert = require("node:assert/strict");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");

const gatewayDll = path.resolve("src/SwiftAid.Gateway/bin/Release/net10.0/SwiftAid.Gateway.dll");
const gatewayContentRoot = path.resolve("src/SwiftAid.Gateway");

async function startHost(host) {
    let output = "";
    host.stdout.on("data", chunk => { output += chunk.toString(); });
    host.stderr.on("data", chunk => { output += chunk.toString(); });
    const deadline = Date.now() + 30000;
    let baseUrl;

    while (Date.now() < deadline) {
        if (host.exitCode !== null) throw new Error(`Gateway child PID ${host.pid} exited before readiness (${host.exitCode}). ${output}`);
        const listener = output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/);
        if (listener) {
            baseUrl = listener[1];
            try {
                const response = await fetch(baseUrl);
                if (response.ok) {
                    const markup = await response.text();
                    if (!markup.includes("A concern deserves a clear next step.")) {
                        throw new Error(`Child PID ${host.pid} listener did not serve the Gateway home identity at ${baseUrl}`);
                    }
                    if (host.exitCode !== null) throw new Error(`Gateway child PID ${host.pid} exited during identity verification`);
                    return { baseUrl, output, childPid: host.pid, identityVerified: true };
                }
            } catch (error) {
                if (error.message.includes("did not serve the Gateway home identity")) throw error;
                // Kestrel may need a moment after announcing its listener.
            }
        }
        await new Promise(resolve => setTimeout(resolve, 200));
    }

    throw new Error(`Timed out waiting for Gateway child PID ${host.pid} to announce and serve an ephemeral loopback listener. ${output}`);
}

async function main() {
    const host = spawn("dotnet", [gatewayDll, "--urls", "http://127.0.0.1:0"], {
        cwd: gatewayContentRoot,
        stdio: ["ignore", "pipe", "pipe"],
        windowsHide: true,
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development" }
    });

    let browser;
    try {
        const hostIdentity = await startHost(host);
        const { baseUrl, output: startupOutput } = hostIdentity;
        browser = await chromium.launch({ channel: "chrome", headless: true });
        const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
        const page = await context.newPage();
        const pageErrors = [];
        const failedResponses = [];
        const externalRequests = [];
        page.on("pageerror", error => pageErrors.push({ message: error.message, stack: error.stack }));
        page.on("response", response => {
            if (response.status() >= 400) failedResponses.push(`${response.status()} ${response.url()}`);
        });
        page.on("request", request => {
            if (/^https?:\/\/(?!127\.0\.0\.1)/i.test(request.url())) externalRequests.push(request.url());
        });

        await page.goto(baseUrl, { waitUntil: "networkidle" });
        const toggle = page.getByRole("button", { name: "Toggle navigation" });
        await toggle.waitFor({ state: "visible" });
        const controlledId = await toggle.getAttribute("aria-controls");
        assert.equal(controlledId, "primary-navigation");
        assert.equal(await page.locator(`#${controlledId}`).count(), 1, "aria-controls must resolve to the drawer element");
        const initialMarkup = await page.evaluate(id => ({
            toggle: document.querySelector('button[aria-label="Toggle navigation"]')?.outerHTML,
            drawer: document.getElementById(id)?.outerHTML.slice(0, 400)
        }), controlledId);

        const states = [];
        const ariaMismatches = [];
        for (let index = 0; index < 3; index++) {
            const expanded = await toggle.getAttribute("aria-expanded");
            const drawerOpen = await page.locator(`#${controlledId}`).evaluate(element => element.classList.contains("mud-drawer--open"));
            states.push({ phase: "before", toggle: index + 1, expanded, drawerOpen,
                buttonMarkup: await toggle.evaluate(element => element.outerHTML) });
            if (expanded !== (drawerOpen ? "true" : "false")) {
                ariaMismatches.push({ phase: "before", toggle: index + 1, expanded, drawerOpen });
            }
            await toggle.click();
            await page.waitForFunction(({ id, expectedOpen }) => {
                const drawer = document.getElementById(id);
                return drawer?.classList.contains("mud-drawer--open") === expectedOpen;
            }, { id: controlledId, expectedOpen: !drawerOpen }, { timeout: 5000 });
            const nextExpanded = await toggle.getAttribute("aria-expanded");
            const nextDrawerOpen = await page.locator(`#${controlledId}`).evaluate(element => element.classList.contains("mud-drawer--open"));
            states.push({ phase: "after", toggle: index + 1, expanded: nextExpanded, drawerOpen: nextDrawerOpen,
                buttonMarkup: await toggle.evaluate(element => element.outerHTML) });
            if (nextExpanded !== (nextDrawerOpen ? "true" : "false")) {
                ariaMismatches.push({ phase: "after", toggle: index + 1, expanded: nextExpanded, drawerOpen: nextDrawerOpen });
            }
        }

        const keyboardStates = [];
        await toggle.focus();
        for (const key of ["Space", "Enter"]) {
            const wasOpen = await page.locator(`#${controlledId}`).evaluate(element => element.classList.contains("mud-drawer--open"));
            await toggle.press(key);
            await page.waitForFunction(({ id, expectedOpen }) => {
                const drawer = document.getElementById(id);
                return drawer?.classList.contains("mud-drawer--open") === expectedOpen;
            }, { id: controlledId, expectedOpen: !wasOpen }, { timeout: 5000 });
            const expanded = await toggle.getAttribute("aria-expanded");
            const drawerOpen = await page.locator(`#${controlledId}`).evaluate(element => element.classList.contains("mud-drawer--open"));
            keyboardStates.push({ key, expanded, drawerOpen });
            if (expanded !== (drawerOpen ? "true" : "false")) {
                ariaMismatches.push({ phase: "keyboard", key, expanded, drawerOpen });
            }
        }

        assert.deepEqual(pageErrors, [], `Gateway page errors: ${JSON.stringify(pageErrors)}`);
        assert.deepEqual(failedResponses, [], `Gateway HTTP errors: ${JSON.stringify(failedResponses)}`);
        assert.deepEqual(externalRequests, [], `Gateway external requests: ${JSON.stringify(externalRequests)}`);
        console.log(JSON.stringify({ initialMarkup, states, keyboardStates, ariaMismatches }, null, 2));
        assert.deepEqual(ariaMismatches, [], `Rendered aria-expanded does not match drawer state: ${JSON.stringify(ariaMismatches)}`);
        console.log(JSON.stringify({
            result: "PASS",
            app: "Gateway Release output, task-owned Development process",
            hostIdentity,
            controlledId,
            states,
            keyboardStates,
            finalState: {
                expanded: await toggle.getAttribute("aria-expanded"),
                drawerOpen: await page.locator(`#${controlledId}`).evaluate(element => element.classList.contains("mud-drawer--open"))
            },
            pageErrors,
            failedResponses,
            externalRequests,
            startupOutput: startupOutput.split(/\r?\n/).filter(line => line.includes("Now listening"))
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
