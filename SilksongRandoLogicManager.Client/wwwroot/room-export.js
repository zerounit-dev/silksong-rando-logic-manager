// Owns only complete-room attachment delivery. Bytes never cross .NET interop.
import { accessDenial, applicationHeaders, checkApplicationResponse, requireActive } from "./workspace-request.js";
function completeFilename(disposition) {
    if (!disposition || !/^attachment\s*(?:;|$)/i.test(disposition)) throw new Error("Missing attachment.");
    const parameters = new Map();
    const rest = disposition.replace(/^attachment\s*/i, "");
    const pattern = /;\s*([!#$%&'*+.^_`|~\w-]+)\s*=\s*("(?:[^"\\]|\\.)*"|[^;\s]+)\s*/gy;
    let offset = 0, match;
    while (offset < rest.length) {
        pattern.lastIndex = offset;
        match = pattern.exec(rest);
        if (!match) throw new Error("Malformed attachment.");
        const key = match[1].toLowerCase();
        if (parameters.has(key)) throw new Error("Duplicate attachment parameter.");
        parameters.set(key, match[2]); offset = pattern.lastIndex;
    }
    let filename;
    if (parameters.has("filename*")) {
        const value = parameters.get("filename*");
        const encoded = /^UTF-8'[^']*'(.+)$/i.exec(value);
        if (!encoded) throw new Error("Unsupported attachment encoding.");
        filename = decodeURIComponent(encoded[1]);
    } else {
        const value = parameters.get("filename");
        filename = value?.startsWith('"') ? value.slice(1, -1).replace(/\\(.)/g, "$1") : value;
    }
    if (!/^silksong-logic-\d{8}-\d{6}-\d{3}\.json$/.test(filename ?? "")) throw new Error("Unexpected complete filename.");
    return filename;
}

export function mount(host) {
    let disposed = false, flight = false, controller = null;
    const resources = new Set();
    const freeze = value => {
        host.dataset.pending = value ? "true" : "false";
        host.querySelector("fieldset").disabled = value;
    };
    const retire = resource => {
        clearTimeout(resource.timer);
        resource.anchor?.remove(); resource.anchor = null;
        if (resource.url) URL.revokeObjectURL(resource.url);
        resource.url = null; resources.delete(resource);
    };
    return {
        async download(includeAreaMap, workspace) {
            if (disposed || flight || !workspace) throw new Error("Export owner unavailable.");
            flight = true; controller = new AbortController();
            let response = null, blob = null, resource = null;
            try {
                freeze(true);
                response = await fetch(`/exports/complete?includeAreaMap=${includeAreaMap ? "true" : "false"}`, {
                    method: "GET", credentials: "same-origin", cache: "no-store", signal: controller.signal,
                    redirect: "error", headers: applicationHeaders(workspace)
                });
                await checkApplicationResponse(response);
                const denial = await accessDenial(response);
                if (denial) return { kind: "access-denied", denial };
                const media = response.headers.get("Content-Type")?.split(";", 1)[0].trim().toLowerCase();
                if (response.status === 422 && media === "application/problem+json") {
                    const problem = await response.json();
                    if (disposed) return { kind: "disposed" };
                    if (problem?.status === 422 && problem.title === "Check location Type correction required"
                        && typeof problem.detail === "string" && problem.detail.trim())
                        return { kind: "type-correction", detail: problem.detail };
                    throw new Error("Unclassified export rejection.");
                }
                if (response.status !== 200 || media !== "application/json") throw new Error("Unexpected export response.");
                const filename = completeFilename(response.headers.get("Content-Disposition"));
                blob = await response.blob();
                if (!blob.size) throw new Error("Empty export body.");
                // Reject even mislabeled HTML/problem bodies, without rewriting the attachment.
                const document = JSON.parse(await blob.text());
                if (document?.exportVersion !== 6 || document.isPartialRoomDump !== false
                    || !Array.isArray(document.rooms) || !Array.isArray(document.roomGroupings?.groups))
                    throw new Error("Unexpected complete export body.");
                requireActive();
                if (disposed) return { kind: "disposed" };
                resource = { url: URL.createObjectURL(blob), anchor: null, timer: null };
                resources.add(resource);
                resource.anchor = window.document.createElement("a");
                resource.anchor.href = resource.url; resource.anchor.download = filename;
                resource.anchor.hidden = true; window.document.body.append(resource.anchor);
                resource.anchor.click();
                resource.anchor.remove(); resource.anchor = null;
                // Browser handoff only, not a disk-save acknowledgement. Keep URL alive
                // beyond this stack for native download consumption (including Firefox).
                const handedOff = resource;
                resource.timer = setTimeout(() => retire(handedOff), 1000);
                resource = null;
                return { kind: "handed-off" };
            } finally {
                if (resource) retire(resource);
                // Abort also retires unread failure bodies. Completed bytes are browser-owned.
                controller?.abort(); controller = null; response = null; blob = null;
                flight = false;
                if (!disposed && host.isConnected) freeze(false);
            }
        },
        dispose() {
            if (disposed) return;
            disposed = true; controller?.abort();
            host.querySelector("fieldset").disabled = true;
            for (const resource of [...resources]) retire(resource);
        }
    };
}
