const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");

const hostDll = path.resolve("tests/SwiftAid.Ui.TestHost/bin/Debug/net10.0/SwiftAid.Ui.TestHost.dll");
const hostContentRoot = path.resolve("tests/SwiftAid.Ui.TestHost");
const artifactDirectory = path.join(__dirname, "artifacts");
fs.mkdirSync(artifactDirectory, { recursive: true });

async function startHost(child) {
    let output = "";
    child.stdout.on("data", chunk => { output += chunk.toString(); });
    child.stderr.on("data", chunk => { output += chunk.toString(); });
    const deadline = Date.now() + 30000;
    let baseUrl;
    let identityVerified = false;

    while (Date.now() < deadline) {
        if (child.exitCode !== null) {
            throw new Error(`Test host child PID ${child.pid} exited before readiness (${child.exitCode}).\n${output}`);
        }

        const listener = output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/);
        if (listener) {
            baseUrl = listener[1];
            try {
                const response = await fetch(baseUrl);
                if (response.ok) {
                    const markup = await response.text();
                    if (!markup.includes("Shared component verification fixture")) {
                        throw new Error(`Child PID ${child.pid} listener did not serve the test fixture identity at ${baseUrl}`);
                    }
                    if (child.exitCode !== null) throw new Error(`Test host child PID ${child.pid} exited during identity verification`);
                    identityVerified = true;
                    return { baseUrl, output, identityVerified, childPid: child.pid };
                }
            } catch (error) {
                if (error.message.includes("did not serve the test fixture identity")) throw error;
                // Kestrel has announced the port but may not have completed its first request yet.
            }
        }

        await new Promise(resolve => setTimeout(resolve, 200));
    }

    throw new Error(`Timed out waiting for child PID ${child.pid} to announce and serve an ephemeral loopback listener.\n${output}`);
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
        const hostIdentity = await startHost(host);
        const { baseUrl, output: hostOutput } = hostIdentity;
        let liveHostOutput = hostOutput;
        host.stdout.on("data", chunk => { liveHostOutput += chunk.toString(); });
        host.stderr.on("data", chunk => { liveHostOutput += chunk.toString(); });
        const directAssetChecks = {};
        for (const path of [
            "/_content/Blazor.Lottie.Player/lottie.min.js",
            "/_content/Blazor.Lottie.Player/lottiePlayerModule.js",
            "/_content/SwiftAid.UI/assets/loading/swiftaid-loader.lottie.json",
            "/_content/SwiftAid.UI/assets/loading/swiftaid-loader.gif",
            "/_content/SwiftAid.UI/assets/loading/swiftaid-loader-still.png"
        ]) {
            const response = await fetch(`${baseUrl}${path}`);
            directAssetChecks[path] = response.status;
        }
        assert(Object.values(directAssetChecks).every(status => status === 200),
            `A required local UI asset did not load: ${JSON.stringify(directAssetChecks)}`);
        browser = await chromium.launch({ channel: "chrome", headless: true });
        const context = await browser.newContext({ viewport: { width: 390, height: 844 } });
        const page = await context.newPage();
        const errors = [];
        const expectedSyntheticRuntimeErrors = [];
        const candidateLateSyntheticRuntimeErrors = [];
        let browserEventSequence = 0;
        let lateMalformedRequestSeen = false;
        let lateMalformedResponseCompletedSequence;
        let browserPhase = "page-startup";
        const consoleErrors = [];
        const consoleErrorLocations = [];
        const requests = [];
        const failedResponses = [];
        const failedRequests = [];
        page.on("pageerror", error => {
            const record = { phase: browserPhase, sequence: ++browserEventSequence, message: error.message, stack: error.stack };
            if (browserPhase === "lottie-second-load-synthetic-error"
                && lottieJsonRequests === 2
                && lateMalformedRequestSeen
                && error.message === "Cannot read properties of null (reading '__complete')"
                && error.stack.includes("lottie.min.js")
                && error.stack.includes("completeData")) {
                candidateLateSyntheticRuntimeErrors.push(record);
            } else {
                errors.push(record);
            }
        });
        page.on("console", message => {
            if (message.type() === "error") {
                consoleErrors.push(message.text());
                consoleErrorLocations.push({ text: message.text(), location: message.location() });
            }
        });
        page.on("request", request => requests.push(request.url()));
        page.on("response", response => { if (response.status() >= 400) failedResponses.push(`${response.status()} ${response.url()}`); });
        page.on("requestfailed", request => failedRequests.push(`${request.failure()?.errorText ?? "failed"} ${request.url()}`));
        let lottieJsonRequests = 0;
        let resolveLateMalformedResponse;
        const lateMalformedResponse = new Promise(resolve => { resolveLateMalformedResponse = resolve; });
        await page.route("**/_content/SwiftAid.UI/assets/loading/swiftaid-loader.lottie.json", async route => {
            lottieJsonRequests++;
            if (lottieJsonRequests === 2) {
                lateMalformedRequestSeen = true;
                await route.fulfill({ status: 200, contentType: "application/json", body: "{ synthetic invalid Lottie JSON" });
                lateMalformedResponseCompletedSequence = ++browserEventSequence;
                resolveLateMalformedResponse();
                return;
            }

            const response = await route.fetch();
            await route.fulfill({
                response,
                headers: { ...response.headers(), "cache-control": "no-store" }
            });
        });

        await page.goto(baseUrl, { waitUntil: "networkidle" });
        assert.equal(await page.getByRole("heading", { name: "Shared component verification fixture" }).count(), 1);
        assert.equal(await page.locator(".swiftaid-loader").count(), 0);

        const stillImage = "./_content/SwiftAid.UI/assets/loading/swiftaid-loader-still.png";
        await page.emulateMedia({ reducedMotion: "reduce" });
        await page.getByTestId("toggle-loading").click();
        try {
            await page.getByRole("status").getByText("Synthetic fixture loading status").waitFor({ state: "visible", timeout: 10000 });
        } catch {
            throw new Error(`Loader did not appear. Body: ${await page.locator("body").innerText()}\nConsole errors: ${consoleErrors.join(" | ")}\nFailed responses: ${failedResponses.join(" | ")}\nPage errors: ${JSON.stringify(errors)}\nHost output: ${hostOutput}`);
        }
        assert.equal(await page.locator(".swiftaid-loader__visual").getAttribute("aria-hidden"), "true");
        assert.equal(await page.locator(".swiftaid-loader__image").getAttribute("src"), stillImage);

        const screenshots = [];
        const saveScreenshot = async name => {
            const outputPath = path.join(artifactDirectory, name);
            await page.screenshot({ path: outputPath, fullPage: true });
            screenshots.push(path.relative(process.cwd(), outputPath));
        };
        await saveScreenshot("fixture-390-light-reduced-motion.png");
        await page.emulateMedia({ colorScheme: "dark" });
        await page.waitForFunction(() => document.documentElement.dataset.theme === "dark");
        await saveScreenshot("fixture-390-dark-reduced-motion.png");
        await page.setViewportSize({ width: 1280, height: 900 });
        await saveScreenshot("fixture-1280-dark-reduced-motion.png");
        await page.emulateMedia({ colorScheme: "light" });
        await page.waitForFunction(() => document.documentElement.dataset.theme === "light");
        await saveScreenshot("fixture-1280-light-reduced-motion.png");

        const stillResponse = await page.request.get(new URL(stillImage.replace(/^\.\//, "/"), baseUrl).toString());
        assert.equal(stillResponse.status(), 200);

        await page.emulateMedia({ reducedMotion: "no-preference" });
        await page.locator(".swiftaid-loader__fallback").waitFor({ state: "visible" });
        await page.setViewportSize({ width: 390, height: 844 });
        await saveScreenshot("fixture-390-light-gif-fallback.png");
        await page.getByTestId("toggle-lottie").click();
        browserPhase = "lottie-first-load-ready";
        await page.waitForFunction(() => performance.getEntriesByType("resource")
            .some(entry => entry.name.includes("_content/Blazor.Lottie.Player/lottie.min.js")));
        try {
            await page.locator(".swiftaid-loader__lottie.is-ready").waitFor({ state: "visible", timeout: 10000 });
        } catch {
            await saveScreenshot("fixture-390-light-lottie-error-boundary.png");
            const resources = await page.evaluate(() => performance.getEntriesByType("resource")
                .map(item => item.name)
                .filter(name => name.includes("Lottie") || name.includes("lottie")));
            const runtimeState = await page.evaluate(() => ({
                hasLottie: Boolean(window.lottie),
                loaderMarkup: document.querySelector(".swiftaid-loader__visual")?.innerHTML,
                script: document.querySelector("script[data-swiftaid-lottie-runtime]")?.outerHTML
            }));
            throw new Error(`Lottie failed to become ready. Asset status: ${JSON.stringify(directAssetChecks)}\nRuntime: ${JSON.stringify(runtimeState)}\nFailed responses: ${failedResponses.join(" | ")}\nFailed requests: ${failedRequests.join(" | ")}\nBrowser console: ${JSON.stringify(consoleErrorLocations)}\nPage errors: ${JSON.stringify(errors)}\nResources: ${JSON.stringify(resources)}\nScreenshots: ${screenshots.join(", ")}\nHost logs: ${liveHostOutput}`);
        }
        assert(await page.evaluate(() => Boolean(window.lottie && typeof window.lottie === "object")));
        assert(requests.some(url => url.includes("swiftaid-loader.lottie.json")), "Lottie data asset was not requested");
        assert(await page.locator(".swiftaid-loader__lottie svg, .swiftaid-loader__lottie canvas").count() > 0,
            "Lottie did not render an SVG or canvas");
        const lottieSvgOrCanvas = await page.locator(".swiftaid-loader__lottie svg, .swiftaid-loader__lottie canvas").count();
        await saveScreenshot("fixture-390-light-lottie-ready.png");

        const statusText = await page.getByRole("status").innerText();
        browserPhase = "lottie-second-load-synthetic-error";
        await page.getByTestId("toggle-lottie").click();
        await page.locator(".swiftaid-loader__lottie").waitFor({ state: "detached" });
        await page.getByTestId("toggle-lottie").click();
        await Promise.race([
            lateMalformedResponse,
            new Promise((_, reject) => setTimeout(() => reject(new Error("Second malformed Lottie response was not completed within 10 seconds")), 10000))
        ]);
        assert.equal(lateMalformedRequestSeen, true, "Second Lottie JSON request did not reach its route handler");
        assert.equal(lateMalformedResponseCompletedSequence !== undefined, true, "Second malformed Lottie response was not completed");
        await page.locator(".swiftaid-loader__fallback").waitFor({ state: "visible", timeout: 10000 });
        assert.equal(lottieJsonRequests, 2, "Expected exactly two Lottie JSON requests before the late-failure assertion");
        const latePlayerCount = await page.locator(".swiftaid-loader__lottie").count();
        let latePlayerState = null;
        if (latePlayerCount === 1) {
            latePlayerState = await page.locator(".swiftaid-loader__lottie")
                .evaluate(element => ({ className: element.className, opacity: getComputedStyle(element).opacity }));
        }
        assert(latePlayerCount === 0 || (latePlayerCount === 1
            && latePlayerState.className.includes("is-pending")
            && latePlayerState.opacity === "0"),
        `Late failure must remove the player or leave it hidden and pending: ${JSON.stringify({ latePlayerCount, latePlayerState })}`);
        assert.equal(await page.getByRole("status").innerText(), statusText,
            "Late Lottie data failure changed or removed the loading status");
        await saveScreenshot("fixture-390-light-late-lottie-fallback.png");
        for (const error of candidateLateSyntheticRuntimeErrors) {
            if (error.sequence > lateMalformedResponseCompletedSequence) {
                expectedSyntheticRuntimeErrors.push(error);
            } else {
                errors.push(error);
            }
        }

        const widths = [320, 390, 768, 1280];
        const layout = [];
        const overflowByWidth = [];
        for (const width of widths) {
            await page.setViewportSize({ width, height: 844 });
            const current = await page.evaluate(() => ({
                width: document.documentElement.clientWidth,
                scrollWidth: document.documentElement.scrollWidth,
                statusVisible: Boolean(document.querySelector('[role="status"]')),
                overflow: [...document.querySelectorAll("body *")]
                    .map(element => ({
                        tag: element.tagName,
                        className: typeof element.className === "string" ? element.className : "",
                        testId: element.getAttribute("data-testid"),
                        right: Math.round(element.getBoundingClientRect().right),
                        width: Math.round(element.getBoundingClientRect().width)
                    }))
                    .filter(element => element.right > document.documentElement.clientWidth + 1)
                    .slice(0, 20)
            }));
            layout.push({ width: current.width, scrollWidth: current.scrollWidth, statusVisible: current.statusVisible });
            if (current.width !== current.scrollWidth) overflowByWidth.push({ testedWidth: width, elements: current.overflow });
        }
        if (!layout.every(item => item.width === item.scrollWidth)) {
            throw new Error(`Horizontal overflow: ${JSON.stringify({ layout, overflowByWidth })}`);
        }

        await page.emulateMedia({ colorScheme: "dark" });
        await page.waitForFunction(() => document.documentElement.dataset.theme === "dark");
        await page.emulateMedia({ colorScheme: "light" });
        await page.waitForFunction(() => document.documentElement.dataset.theme === "light");

        const status = await page.getByRole("status").getAttribute("aria-live");
        assert.equal(status, "polite");
        await page.getByTestId("toggle-loading").click();
        await page.locator(".swiftaid-loader").waitFor({ state: "detached" });
        assert.deepEqual(errors, [], `Unexpected browser errors: ${JSON.stringify(errors)}; console: ${JSON.stringify(consoleErrorLocations)}; requests: ${JSON.stringify(failedRequests)}; host: ${liveHostOutput}`);

        for (const url of requests) {
            assert(!/^https?:\/\/(?!127\.0\.0\.1)/i.test(url), `Unexpected external request: ${url}`);
        }
        assert.deepEqual(errors, [], `Browser errors: ${JSON.stringify(errors)}; console: ${JSON.stringify(consoleErrorLocations)}; requests: ${JSON.stringify(failedRequests)}; host: ${liveHostOutput}`);

        console.log(JSON.stringify({
            result: "PASS",
            viewportWidths: widths,
            layout,
            localLottieRequested: requests.some(url => url.includes("_content/Blazor.Lottie.Player/lottie.min.js")),
            sharedAssetsRequested: requests.filter(url => url.includes("_content/SwiftAid.UI/assets/loading/")),
            failedResponses,
            failedRequests,
            consoleErrorLocations,
            page404Requests: requests.filter(url => url.includes("favicon") || failedResponses.some(failure => failure.endsWith(url))),
            lottieJsonRequests,
            expectedSyntheticRuntimeErrors,
            latePlayerState,
            latePlayerCount,
            hostIdentity,
            lottieSvgOrCanvas,
            pageErrors: errors,
            browserConsoleErrors: consoleErrors,
            hostReadyOutput: hostOutput.split(/\r?\n/).filter(line => line.includes("Now listening"))
        }, null, 2));

        const fallbackPage = await context.newPage();
        await fallbackPage.route("**/_content/Blazor.Lottie.Player/lottie.min.js", route => route.abort());
        await fallbackPage.goto(baseUrl, { waitUntil: "networkidle" });
        await fallbackPage.getByTestId("toggle-loading").click();
        await fallbackPage.getByRole("status").waitFor({ state: "visible" });
        await fallbackPage.getByTestId("toggle-lottie").click();
        await fallbackPage.locator(".swiftaid-loader__fallback").waitFor({ state: "visible" });
        assert.equal(await fallbackPage.locator(".swiftaid-loader__lottie").count(), 0,
            "Lottie component rendered after local runtime load failure");
        await fallbackPage.close();

        const earlyFailurePage = await context.newPage();
        const earlyFailureErrors = [];
        const expectedEarlySyntheticRuntimeErrors = [];
        const candidateEarlySyntheticRuntimeErrors = [];
        let earlyEventSequence = 0;
        let earlyMalformedRequestSeen = false;
        let earlyMalformedResponseCompletedSequence;
        let resolveEarlyMalformedResponse;
        const earlyMalformedResponse = new Promise(resolve => { resolveEarlyMalformedResponse = resolve; });
        earlyFailurePage.on("pageerror", error => {
            const record = { sequence: ++earlyEventSequence, message: error.message, stack: error.stack };
            if (earlyMalformedRequestSeen
                && error.message === "Cannot read properties of null (reading '__complete')"
                && error.stack.includes("lottie.min.js")
                && error.stack.includes("completeData")) {
                candidateEarlySyntheticRuntimeErrors.push(record);
            } else {
                earlyFailureErrors.push(record);
            }
        });
        await earlyFailurePage.route("**/_content/SwiftAid.UI/assets/loading/swiftaid-loader.lottie.json", async route => {
            earlyMalformedRequestSeen = true;
            await route.fulfill({ status: 200, contentType: "application/json", body: "{ synthetic invalid Lottie JSON" });
            earlyMalformedResponseCompletedSequence = ++earlyEventSequence;
            resolveEarlyMalformedResponse();
        });
        await earlyFailurePage.emulateMedia({ reducedMotion: "no-preference" });
        await earlyFailurePage.goto(baseUrl, { waitUntil: "networkidle" });
        await earlyFailurePage.getByTestId("toggle-loading").click();
        await earlyFailurePage.getByRole("status").waitFor({ state: "visible" });
        await earlyFailurePage.waitForFunction(() => performance.getEntriesByType("resource")
            .some(entry => entry.name.includes("SwiftAidLoader.razor.js")), { timeout: 10000 });
        assert.equal(await earlyFailurePage.evaluate(() => window.matchMedia("(prefers-reduced-motion: reduce)").matches), false,
            "Pre-ready fault scenario requires the no-preference animation path");
        await earlyFailurePage.getByTestId("toggle-lottie").click();
        await Promise.race([
            earlyMalformedResponse,
            new Promise((_, reject) => setTimeout(() => reject(new Error("Pre-ready synthetic animation request was not observed within 10 seconds")), 10000))
        ]);
        assert.equal(earlyMalformedRequestSeen, true, "Pre-ready malformed animation response did not reach its route handler");
        assert.equal(earlyMalformedResponseCompletedSequence !== undefined, true, "Pre-ready malformed animation response was not completed");
        for (const error of candidateEarlySyntheticRuntimeErrors) {
            if (error.sequence > earlyMalformedResponseCompletedSequence) {
                expectedEarlySyntheticRuntimeErrors.push(error);
            } else {
                earlyFailureErrors.push(error);
            }
        }
        await earlyFailurePage.locator(".swiftaid-loader__fallback").waitFor({ state: "visible", timeout: 10000 });
        const earlyFailureState = await earlyFailurePage.locator(".swiftaid-loader__lottie")
            .evaluate(element => ({ className: element.className, opacity: getComputedStyle(element).opacity }));
        assert(earlyFailureState.className.includes("is-pending"),
            `Pre-ready failure unexpectedly marked Lottie ready: ${JSON.stringify(earlyFailureState)}`);
        assert.equal(earlyFailureState.opacity, "0",
            `Pre-ready failure did not hide the pending Lottie player behind the GIF: ${JSON.stringify(earlyFailureState)}`);
        assert.equal(await earlyFailurePage.getByRole("status").innerText(), "Synthetic fixture loading status");
        assert.equal(earlyMalformedRequestSeen, true, "Pre-ready malformed animation response was not injected");
        assert.deepEqual(earlyFailureErrors, [], `Pre-ready failure produced an unexpected browser error: ${JSON.stringify(earlyFailureErrors)}`);
        await earlyFailurePage.screenshot({
            path: path.join(artifactDirectory, "fixture-390-light-early-lottie-fallback.png"),
            fullPage: true
        });
        await earlyFailurePage.close();
        await context.close();

        const externalRequests = requests.filter(url => /^https?:\/\/(?!127\.0\.0\.1)/i.test(url));
        assert.deepEqual(externalRequests, [], `Unexpected external requests: ${JSON.stringify(externalRequests)}`);
        console.log(JSON.stringify({
            scenarioResults: {
                initialGifFallback: true,
                lottieReady: lottieSvgOrCanvas > 0,
                postReadySyntheticErrorRestoredFallbackAndStatus: true,
                preReadySyntheticErrorRestoredFallbackAndStatus: earlyMalformedRequestSeen
            },
            expectedSyntheticRuntimeErrors,
            expectedEarlySyntheticRuntimeErrors,
            earlyFailureUnhandledErrors: earlyFailureErrors,
            externalRequests,
            retainedScreenshots: screenshots.concat(path.relative(process.cwd(), path.join(artifactDirectory, "fixture-390-light-early-lottie-fallback.png")))
        }, null, 2));
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
