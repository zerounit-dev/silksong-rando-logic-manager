(() => {
    "use strict";

    const capturedErrors = [];
    const maximumCapturedErrors = 8;

    window.addEventListener("error", event => capture(formatBrowserError(event)));
    window.addEventListener("unhandledrejection", event => capture(formatUnhandledRejection(event)));

    document.addEventListener("DOMContentLoaded", initialize);

    function initialize() {
        const fallback = document.getElementById("blazor-error-ui");
        if (!fallback) {
            return;
        }

        const copyButton = document.getElementById("fatal-circuit-copy");
        const reloadButton = document.getElementById("fatal-circuit-reload");
        copyButton?.addEventListener("click", () => copyDetails(fallback));
        reloadButton?.addEventListener("click", () => window.location.reload());

        const observer = new MutationObserver(() => populateWhenVisible(fallback));
        observer.observe(fallback, { attributes: true, attributeFilter: ["style", "class"] });
        populateWhenVisible(fallback);
    }

    function capture(details) {
        capturedErrors.push({ timestamp: new Date().toISOString(), details });
        if (capturedErrors.length > maximumCapturedErrors) {
            capturedErrors.shift();
        }
    }

    function populateWhenVisible(fallback) {
        if (!isVisible(fallback)) {
            return;
        }

        fallback.style.display = "grid";
        fallback.setAttribute("aria-hidden", "false");
        const details = document.getElementById("fatal-circuit-details");
        if (details) {
            details.value = formatFatalDetails();
        }
    }

    function isVisible(element) {
        return window.getComputedStyle(element).display !== "none";
    }

    function formatFatalDetails() {
        const terminationTime = new Date().toISOString();
        const browserDetails = capturedErrors.length === 0
            ? "No browser error details were captured before the connection ended."
            : capturedErrors.map(entry => `[${entry.timestamp}]\n${entry.details}`).join("\n\n");

        return [
            "Circuit terminated",
            `Timestamp (UTC): ${terminationTime}`,
            "",
            "Browser-available details:",
            browserDetails,
            "",
            "Recovery guidance:",
            "Reload this page to start a new connection. If the problem continues, keep these browser details and check the application server logs."
        ].join("\n");
    }

    async function copyDetails(fallback) {
        const details = document.getElementById("fatal-circuit-details");
        const status = document.getElementById("fatal-circuit-copy-status");
        if (!details) {
            return;
        }

        const copied = await tryClipboardCopy(details.value) || fallbackCopy(details);
        if (status) {
            status.textContent = copied ? "Details copied." : "Details selected. Copy them with your browser.";
        }
    }

    async function tryClipboardCopy(value) {
        if (!navigator.clipboard?.writeText) {
            return false;
        }

        try {
            await navigator.clipboard.writeText(value);
            return true;
        } catch {
            return false;
        }
    }

    function fallbackCopy(details) {
        details.focus();
        details.select();
        details.setSelectionRange(0, details.value.length);

        try {
            return document.execCommand("copy");
        } catch {
            return false;
        }
    }

    function formatBrowserError(event) {
        const location = event.filename
            ? `Location: ${event.filename}:${event.lineno ?? 0}:${event.colno ?? 0}`
            : undefined;
        const stack = event.error?.stack ? `Stack:\n${event.error.stack}` : undefined;
        return ["Client error", `Message: ${event.message || "Unavailable"}`, location, stack].filter(Boolean).join("\n");
    }

    function formatUnhandledRejection(event) {
        const reason = formatReason(event.reason);
        return ["Unhandled promise rejection", `Message: ${reason.message}`, reason.stack ? `Stack:\n${reason.stack}` : undefined].filter(Boolean).join("\n");
    }

    function formatReason(reason) {
        if (reason instanceof Error) {
            return { message: `${reason.name}: ${reason.message}`, stack: reason.stack };
        }

        if (typeof reason === "string") {
            return { message: reason };
        }

        try {
            return { message: JSON.stringify(reason, null, 2) };
        } catch {
            return { message: String(reason) };
        }
    }
})();
