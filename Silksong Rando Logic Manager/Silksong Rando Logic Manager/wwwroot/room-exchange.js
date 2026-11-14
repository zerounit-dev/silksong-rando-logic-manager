// Native document lifetime/presentation only; attachment validation stays in room-export.
import { mount as mountExport } from "./room-export.js";
import { initialize, retire } from "./workspace-request.js";

let mounted = false;
export function mount(body, actions, message, callback, version) {
    if (mounted) throw new Error("Room Exchange is already registered in this document.");
    mounted = true;
    const downloadOwner = mountExport(body);
    let stopped = false;
    const stop = (guidance = "This page is no longer active. Reload or reopen it to continue.") => {
        if (stopped) return;
        stopped = true;
        retire();
        downloadOwner.dispose();
        actions.querySelectorAll("button").forEach(button => { button.disabled = true; });
        message.textContent = guidance;
    };
    try {
        initialize({ invokeMethodAsync(method, serverVersion) {
            stop("The application changed. Reload or reopen this page before exporting rooms.");
            return callback.invokeMethodAsync(method, serverVersion);
        } }, version);
    } catch (error) { stop(); throw error; }
    const pagehide = () => stop(); // Also permanently retires BFCache-restored documents.
    const depart = event => {
        const link = event.target.closest?.("a[href]");
        if (!event.defaultPrevented && event.button === 0 && !event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey
            && link && !link.hasAttribute("download") && (!link.target || link.target === "_self")
            && new URL(link.href, location.href).href.split("#")[0] !== location.href.split("#")[0]) stop();
    };
    const reconnect = document.getElementById("components-reconnect-modal");
    const disconnected = event => { if (event.detail.state !== "hide") stop(); };
    window.addEventListener("pagehide", pagehide);
    window.addEventListener("beforeunload", pagehide);
    document.addEventListener("click", depart);
    reconnect?.addEventListener("components-reconnect-state-changed", disconnected);
    // DOM removal/fatal termination must not wait for delayed server circuit disposal.
    const observer = new MutationObserver(() => {
        if (!body.isConnected || document.getElementById("blazor-error-ui")?.getAttribute("aria-hidden") === "false") stop();
    });
    observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["aria-hidden"] });
    const requireOwner = () => { if (stopped || !body.isConnected) throw new Error("Room Exchange is retired."); };
    return {
        openImportPicker() {
            // A server callback/render may finish after native departure or disconnect.
            if (stopped || !body.isConnected) return;
            window.openSceneUploadPicker("distributed-import-input");
        },
        async download(includeAreaMap, workspace) {
            requireOwner();
            try { return await downloadOwner.download(includeAreaMap, workspace); }
            catch (error) { if (stopped) return { kind: "disposed" }; throw error; }
        },
        dispose() {
            stop(); observer.disconnect();
            window.removeEventListener("pagehide", pagehide);
            window.removeEventListener("beforeunload", pagehide);
            document.removeEventListener("click", depart);
            reconnect?.removeEventListener("components-reconnect-state-changed", disconnected);
        }
    };
}
