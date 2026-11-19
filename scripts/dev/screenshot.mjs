// Screenshot helper for checking UI steps by hand (plan 1a D16): Node.js and a Chromium browser
// (Edge or Chrome), driven through the DevTools Protocol. No npm package.
//
// Usage:
//   node scripts/dev/screenshot.mjs --url http://localhost:5180/ --out shot.png
//        [--width 360] [--height 800] [--theme light|dark] [--lang pl-PL] [--timezone Europe/Warsaw]
//        [--wait "text on the page"] [--click "button text"] [--then-wait "text"] [--eval "js expression"]
//        [--timeout 60]
//
// --width below 600 emulates a phone (DevTools device metrics, so widths under the browser's
// minimum window size work). --theme sets prefers-color-scheme; the app follows it when no theme
// was chosen (each run uses a fresh browser profile). --lang sets the browser's language, --timezone
// its time zone. --click presses the innermost visible element whose text or aria-label is exactly that text (inside the open
// dialog or popover when a dialog is open), after
// --wait; several steps are separated by ">>" ("Reporting currency >> Euro (EUR) >> Save"), and a
// step "type:Label=text" types into the field with that label. --eval prints the value of a JavaScript expression after all waits.
// The browser comes from BROWSER_PATH, or the usual Edge or Chrome install location.

import { spawn } from "node:child_process";
import { existsSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const args = parseArgs(process.argv.slice(2));
if (!args.url || !args.out) {
    console.error("Usage: node scripts/dev/screenshot.mjs --url <url> --out <file.png> [options]; see the comment at the top.");
    process.exit(1);
}

const width = Number(args.width ?? 1280);
const height = Number(args.height ?? 800);
const timeoutMs = Number(args.timeout ?? 60) * 1000;
const profile = mkdtempSync(join(tmpdir(), "fintrack-shot-"));
const browser = startBrowser(profile, args.lang);

try {
    await run();
} catch (error) {
    console.error(error.message);
    process.exitCode = 1;
} finally {
    browser.process.kill();
    await sleep(500);
    try {
        rmSync(profile, { recursive: true, force: true });
    } catch {
        // The browser may still hold files for a moment; the folder is in the temp directory.
    }
}

async function run() {
    const ws = new WebSocket(await browser.endpoint);
    await new Promise((resolve, reject) => {
        ws.onopen = resolve;
        ws.onerror = reject;
    });
    const cdp = connect(ws);

    const { targetId } = await cdp.send("Target.createTarget", { url: "about:blank" });
    const { sessionId } = await cdp.send("Target.attachToTarget", { targetId, flatten: true });
    const page = (method, params = {}) => cdp.send(method, params, sessionId);

    await page("Page.enable");
    await page("Runtime.enable");
    await page("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: width < 600 });
    if (args.theme) {
        await page("Emulation.setEmulatedMedia", { features: [{ name: "prefers-color-scheme", value: args.theme }] });
    }
    if (args.timezone) {
        await page("Emulation.setTimezoneOverride", { timezoneId: args.timezone });
    }

    await page("Page.navigate", { url: args.url });

    const evaluate = async (expression) =>
        (await page("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true })).result?.value;

    const waitForText = async (text) => {
        const deadline = Date.now() + timeoutMs;
        while (Date.now() < deadline) {
            if (await evaluate(`document.body && document.body.innerText.includes(${JSON.stringify(text)})`)) {
                return;
            }
            await sleep(250);
        }
        fail(`Timed out waiting for "${text}".`);
    };

    if (args.wait) {
        await waitForText(args.wait);
    }

    // Each step clicks the innermost visible element whose text is exactly the step's text
    // (a button, a link, a list option, a field label), waiting until one appears. While a dialog
    // is open only its elements and an open popover's count, as only they can be reached.
    for (const step of (args.click ?? "").split(">>").map((text) => text.trim()).filter(Boolean)) {
        if (step.startsWith("type:")) {
            await typeInto(step.slice("type:".length));
            continue;
        }

        const deadline = Date.now() + timeoutMs;
        let clicked = false;
        while (!clicked && Date.now() < deadline) {
            clicked = await evaluate(`(() => {
                const wanted = ${JSON.stringify(step)};
                const matches = [...document.querySelectorAll("body *")]
                    .filter(element => element.offsetParent !== null
                        && (element.innerText?.trim() === wanted || element.getAttribute("aria-label") === wanted));
                // An open dialog covers the page, so only its elements, and an open popover's (a select's
                // list is rendered outside the dialog), can be pressed then, as for a person.
                const dialog = [...document.querySelectorAll('[role="dialog"]')].filter(element => element.offsetParent !== null).pop();
                const reachable = dialog
                    ? matches.filter(element => dialog.contains(element) || element.closest(".mud-popover-open"))
                    : matches;
                const target = reachable.find(element => ![...element.children].some(child => reachable.includes(child)));
                if (!target) return false;
                for (const type of ["mousedown", "mouseup", "click"]) {
                    target.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, view: window }));
                }
                return true;
            })()`);
            if (!clicked) {
                await sleep(250);
            }
        }
        if (!clicked) {
            fail(`Nothing to click with the text "${step}".`);
        }
        await sleep(500);
    }

    // "type:Label=text": focus the input of the field labelled Label, clear it and type the text.
    async function typeInto(spec) {
        const separator = spec.indexOf("=");
        const [label, text] = [spec.slice(0, separator).trim(), spec.slice(separator + 1)];
        const focused = await evaluate(`(() => {
            const label = [...document.querySelectorAll("label")].find(element => element.innerText.trim().replace(/[*]$/, "") === ${JSON.stringify(label)});
            const input = label && (document.getElementById(label.htmlFor) ?? label.closest(".mud-input-control")?.querySelector("input"));
            if (!input) return false;
            input.focus();
            input.select();
            return true;
        })()`);
        if (!focused) {
            fail(`No field labelled "${label}".`);
        }
        await page("Input.insertText", { text });
        await sleep(800);
    }

    if (args["then-wait"]) {
        await waitForText(args["then-wait"]);
    }

    // Let fonts, transitions and the last render settle.
    await sleep(800);

    if (args.eval) {
        console.log(JSON.stringify(await evaluate(args.eval)));
    }

    const { data } = await page("Page.captureScreenshot", { format: "png" });
    writeFileSync(args.out, Buffer.from(data, "base64"));
    console.log(`Saved ${args.out} (${width}x${height}${args.theme ? ", " + args.theme : ""}).`);

    ws.close();
}

function startBrowser(userDataDir, lang) {
    const path = process.env.BROWSER_PATH ?? [
        "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
        "C:/Program Files/Microsoft/Edge/Application/msedge.exe",
        "C:/Program Files/Google/Chrome/Application/chrome.exe",
        "/usr/bin/google-chrome",
        "/usr/bin/chromium",
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    ].find(existsSync);
    if (!path) {
        fail("No Edge or Chrome found; set BROWSER_PATH.");
    }

    const flags = [
        "--headless=new",
        "--disable-gpu",
        "--no-first-run",
        "--no-default-browser-check",
        "--remote-debugging-port=0",
        `--user-data-dir=${userDataDir}`,
    ];
    if (lang) {
        flags.push(`--lang=${lang}`, `--accept-lang=${lang}`);
    }

    const child = spawn(path, flags, { stdio: ["ignore", "ignore", "pipe"] });
    const endpoint = new Promise((resolve, reject) => {
        let output = "";
        child.stderr.on("data", (chunk) => {
            output += chunk;
            const match = output.match(/DevTools listening on (ws:\/\/\S+)/);
            if (match) {
                resolve(match[1]);
            }
        });
        child.on("exit", (code) => reject(new Error(`The browser exited (${code}).`)));
    });

    return { process: child, endpoint };
}

function connect(ws) {
    let nextId = 1;
    const pending = new Map();
    ws.onmessage = (event) => {
        const message = JSON.parse(event.data);
        if (message.id && pending.has(message.id)) {
            const { resolve, reject } = pending.get(message.id);
            pending.delete(message.id);
            message.error ? reject(new Error(`${message.error.message} (${message.error.code})`)) : resolve(message.result);
        }
    };

    return {
        send(method, params = {}, sessionId = undefined) {
            const id = nextId++;
            ws.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
            return new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
        },
    };
}

function parseArgs(list) {
    const parsed = {};
    for (let index = 0; index < list.length; index += 2) {
        if (!list[index].startsWith("--") || index + 1 >= list.length) {
            fail(`Unexpected argument "${list[index]}".`);
        }
        parsed[list[index].slice(2)] = list[index + 1];
    }
    return parsed;
}

function sleep(milliseconds) {
    return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

function fail(message) {
    throw new Error(message);
}
