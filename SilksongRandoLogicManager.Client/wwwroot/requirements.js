// Catalogue-local native mechanics. No row DOM reordering, persistence or timer.
import { accessDenial, applicationHeaders, checkApplicationResponse, requireActive } from "./workspace-request.js";
export function mount(callback, dialog) {
    let disposed = false, pending = false, initiator = null, backdrop = false;
    const controller = new AbortController();
    let root = null;
    const dragStart = event => {
        const grip = event.target.closest?.("[data-rc-grip]");
        if (!grip || !root?.contains(grip)) return;
        if (disposed || pending || errorOpen() || grip.disabled || grip.getAttribute("draggable") !== "true") { event.preventDefault(); return; }
        // Native transfer setup must run synchronously in dragstart, before any
        // .NET continuation. C# alone owns placement and the durable command.
        event.dataTransfer.effectAllowed = "move";
        event.dataTransfer.setData("text/plain", grip.dataset.rcGrip);
    };
    const dragOver = event => { if (event.target.closest?.("[data-rc-row], .catalogue-drop-tail")) event.dataTransfer.dropEffect = "move"; };
    const disconnect = () => {
        root?.removeEventListener("dragstart", dragStart, true);
        root?.removeEventListener("dragover", dragOver);
        root = null;
    };
    const errorOpen = () => !!document.querySelector(".v3-error[open]");
    const outside = event => {
        const box = dialog.getBoundingClientRect();
        return event.target === dialog && (event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom);
    };
    const down = event => { backdrop = event.button === 0 && outside(event); };
    const up = event => {
        const dismiss = backdrop && outside(event); backdrop = false;
        if (dismiss && !pending && !errorOpen()) void callback.invokeMethodAsync("CloseRequirementsDialogAsync").catch(console.error);
    };
    const cancel = event => { if (pending || errorOpen()) event.preventDefault(); };
    const pointerCancel = () => { backdrop = false; };
    dialog.addEventListener("pointerdown", down); dialog.addEventListener("pointerup", up);
    dialog.addEventListener("pointercancel", pointerCancel); dialog.addEventListener("cancel", cancel);
    return {
        connect(element) {
            if (element === root) return;
            disconnect();
            if (disposed || !(element instanceof HTMLElement)) return;
            root = element;
            root.addEventListener("dragstart", dragStart, true);
            root.addEventListener("dragover", dragOver);
        },
        remember(id) { initiator = id ? document.getElementById(id) : document.activeElement; },
        showGuide(id) {
            // Shared guide is a SECTION, never a native definition dialog.
            // Initial focus only; the App's existing owner handles error return.
            if (disposed || errorOpen()) return false;
            const guide = document.getElementById(id);
            if (!guide?.isConnected) return false;
            guide.focus();
            return true;
        },
        show() {
            // This check and show/focus are synchronous, closing the interop-await
            // race with the App's native general-error top-layer owner.
            if (disposed || errorOpen() || !dialog.isConnected) return false;
            if (!dialog.open) {
                dialog.showModal();
                const first = dialog.querySelector("[data-rc-first]");
                if (first && !errorOpen()) { first.focus(); first.select(); }
            }
            return true;
        },
        freeze(value) { pending = value; dialog.dataset.pending = String(value); const fields = dialog.querySelector("fieldset"); if (fields) fields.disabled = value; },
        close() { dialog.close(); backdrop = false; },
        restore() {
            if (disposed || errorOpen()) return;
            const top = [...document.querySelectorAll("dialog:modal")].at(-1);
            const eligible = element => element?.isConnected && !element.matches(":disabled") && element.getClientRects().length > 0 && (!top || top.contains(element));
            const target = eligible(initiator) ? initiator : top
                ? [...top.querySelectorAll("input, button, textarea, select, a[href]")].find(eligible)
                : [...document.querySelectorAll(".rc-page button:not(:disabled), .nav-title")].find(eligible);
            target?.focus(); initiator = null;
        },
        async download(workspace) {
            if (disposed || !workspace) throw new Error("Export owner unavailable.");
            const response = await fetch("/api/v3/requirements/export", {
                signal: controller.signal, cache: "no-store", credentials: "same-origin", redirect: "error",
                headers: applicationHeaders(workspace)
            });
            await checkApplicationResponse(response);
            const denial = await accessDenial(response);
            if (denial) return denial;
            if (!response.ok || !response.headers.get("content-type")?.startsWith("application/json")) throw new Error("Catalogue export failed.");
            const name = /filename="(silksong-requirement-catalogue-[0-9-]+\.json)"/.exec(response.headers.get("content-disposition") ?? "")?.[1];
            if (!name) throw new Error("Missing catalogue download metadata.");
            const bytes = await response.blob();
            requireActive();
            if (disposed) return;
            const url = URL.createObjectURL(bytes), anchor = document.createElement("a");
            try { anchor.href = url; anchor.download = name; document.body.append(anchor); anchor.click(); }
            finally { anchor.remove(); URL.revokeObjectURL(url); }
            return null;
        },
        dispose() {
            if (disposed) return; disposed = true; controller.abort(); dialog.close();
            disconnect();
            dialog.removeEventListener("pointerdown", down); dialog.removeEventListener("pointerup", up);
            dialog.removeEventListener("pointercancel", pointerCancel); dialog.removeEventListener("cancel", cancel);
            initiator = null;
        }
    };
}
