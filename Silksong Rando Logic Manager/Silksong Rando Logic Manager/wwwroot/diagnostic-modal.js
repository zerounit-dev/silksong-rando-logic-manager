let callbackReference;
const pendingDetails = [];

window.addEventListener("error", (event) => {
    report(formatBrowserError(event));
});

window.addEventListener("unhandledrejection", (event) => {
    report(`Unhandled promise rejection\n${formatReason(event.reason)}`);
});

export function register(reference) {
    callbackReference = reference;
    while (pendingDetails.length > 0) {
        report(pendingDetails.shift());
    }
}

export function unregister() {
    callbackReference = undefined;
}

export async function copyDetails(details) {
    try {
        await navigator.clipboard.writeText(details);
        return true;
    } catch {
        return false;
    }
}

function report(details) {
    if (!callbackReference) {
        pendingDetails.push(details);
        return;
    }

    callbackReference.invokeMethodAsync("ReportBrowserError", details).catch(() => {
        pendingDetails.push(details);
        callbackReference = undefined;
    });
}

function formatBrowserError(event) {
    const location = event.filename
        ? `${event.filename}:${event.lineno ?? 0}:${event.colno ?? 0}`
        : undefined;
    const stack = event.error?.stack;
    const details = ["Browser error", event.message, location, stack].filter(Boolean);

    return details.join("\n");
}

function formatReason(reason) {
    if (reason instanceof Error) {
        return reason.stack ?? `${reason.name}: ${reason.message}`;
    }

    if (typeof reason === "string") {
        return reason;
    }

    try {
        return JSON.stringify(reason, null, 2);
    } catch {
        return String(reason);
    }
}
