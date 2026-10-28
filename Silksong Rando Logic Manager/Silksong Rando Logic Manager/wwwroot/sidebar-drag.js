// One native-drag owner per mounted sidebar. Only a completed local drop crosses
// the circuit; neither source tracking nor insertion feedback is renderer state.
const ownerKey = Symbol("sidebar-drag-owner");
const mime = "application/x-silksong-sidebar";
// Match Char.IsWhiteSpace used by NavMenu, including NEL, excluding BOM.
const whitespace = /[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]/g;

export function mount(root, callback, ownerId) {
    root[ownerKey]?.dispose();
    let disposed = false, pending = false, drag = null, feedback = null, destinationKey = null;
    const element = value => value instanceof Element ? value : null;
    const owned = value => value && root.contains(value);
    const version = () => root.dataset.sidebarView;
    const searching = () => root.dataset.sidebarSearch === "true"
        || [...root.querySelectorAll(".nav-header .search-entry")].some(input => input.value.replace(whitespace, "").length > 0);
    const available = () => !disposed && root.isConnected && !pending && !searching();
    const syncSources = () => {
        if (disposed) return;
        const enabled = available();
        root.querySelectorAll("[data-sidebar-room], [data-sidebar-group]").forEach(source => {
            const value = enabled ? "true" : "false";
            if (source.getAttribute("draggable") !== value) source.setAttribute("draggable", value);
        });
    };
    const clearFeedback = () => {
        if (feedback) feedback.node.classList.remove(feedback.className);
        feedback = null;
        destinationKey = null;
    };
    const cancel = () => { drag = null; clearFeedback(); };
    const current = () => available() && drag && drag.version === version() && owned(drag.source) && drag.source.isConnected;
    const targetFor = value => {
        if (!current()) return null;
        const target = element(value)?.closest(drag.family === "room" ? "[data-sidebar-room-target]" : "[data-sidebar-group-index]");
        if (!owned(target)) return null;
        const index = Number(drag.family === "room" ? target.dataset.sidebarRoomIndex : target.dataset.sidebarGroupIndex);
        if (!Number.isInteger(index) || index < 0) return null;
        let node = target, className = "active";
        const groupId = drag.family === "room" ? target.dataset.sidebarRoomTarget || null : null;
        if (drag.family === "group" && target.hasAttribute("data-sidebar-group")) className = "group-drop-active";
        if (drag.family === "room") {
            if (target.hasAttribute("data-sidebar-room")) node = target.previousElementSibling;
            else if (target.classList.contains("group-heading")) {
                // A heading appends, including when its room list is collapsed.
                node = [...target.parentElement.querySelectorAll(".drop-line")].at(-1) ?? target;
                if (node === target) className = "sidebar-room-drop-tail";
            }
        }
        if (!node) return null;
        return { groupId, index, node, className, key: `${drag.family}:${groupId ?? ""}:${index}` };
    };
    const localTransfer = event => !event.dataTransfer?.types.includes("Files") && event.dataTransfer?.types.includes(mime);
    const dragstart = event => {
        const target = element(event.target);
        const source = target?.closest("[data-sidebar-room], [data-sidebar-group]");
        if (!owned(source)) {
            // Archived links have native anchor dragging by default, but are not
            // sidebar sources or anchors. Leave independent file controls alone.
            if (owned(target?.closest(".room-link"))) event.preventDefault();
            return;
        }
        cancel();
        if (!available() || source.draggable !== true || !event.dataTransfer
            || target.closest("button, input, textarea, select")) {
            event.preventDefault();
            return;
        }
        const family = source.hasAttribute("data-sidebar-room") ? "room" : "group";
        const token = `${ownerId}:${crypto.randomUUID()}`;
        try {
            event.dataTransfer.setData(mime, token);
            event.dataTransfer.effectAllowed = "move";
        } catch {
            event.preventDefault();
            return;
        }
        drag = { family, source, id: family === "room" ? source.dataset.sidebarRoom : source.dataset.sidebarGroup,
            version: version(), token };
    };
    const dragover = event => {
        const target = localTransfer(event) ? targetFor(event.target) : null;
        if (!target) { clearFeedback(); return; }
        event.preventDefault();
        event.dataTransfer.dropEffect = "move";
        if (destinationKey === target.key) return;
        clearFeedback();
        feedback = { node: target.node, className: target.className };
        destinationKey = target.key;
        feedback.node.classList.add(feedback.className);
    };
    const drop = event => {
        const target = localTransfer(event) ? targetFor(event.target) : null;
        const source = drag;
        let matches = false;
        try { matches = source && event.dataTransfer?.getData(mime) === source.token; } catch { /* reject */ }
        cancel();
        if (!target || !matches) return;
        event.preventDefault();
        pending = true;
        syncSources();
        // Rejections after disconnect/disposal are consumed. Service failures are
        // presented by NavMenu's existing diagnostic path, never retried here.
        void Promise.resolve().then(() => {
            if (disposed || !root.isConnected || version() !== source.version || searching()) return;
            return callback.invokeMethodAsync("SubmitSidebarMoveAsync", {
                owner: ownerId, viewVersion: Number(source.version), family: source.family,
                sourceId: source.id, groupId: target.groupId, index: target.index
            });
        }).catch(() => {}).finally(() => {
            pending = false;
            syncSources();
        });
    };
    const dragleave = event => { if (!targetFor(event.relatedTarget)) clearFeedback(); };
    const input = event => {
        if (!element(event.target)?.matches(".nav-header .search-entry")) return;
        // The native value gates immediately, before the oninput circuit round trip.
        if (searching()) cancel();
        syncSources();
    };
    const keydown = event => { if (drag && event.key === "Escape") cancel(); };
    const outsideDrop = event => { if (!owned(element(event.target))) cancel(); };
    const outsideStart = event => { if (!owned(element(event.target))) cancel(); };
    const events = { dragstart, dragover, drop, dragleave, input };
    for (const [name, handler] of Object.entries(events)) root.addEventListener(name, handler, true);
    document.addEventListener("dragend", cancel, true);
    document.addEventListener("drop", outsideDrop, true);
    document.addEventListener("dragstart", outsideStart, true);
    document.addEventListener("keydown", keydown, true);
    window.addEventListener("blur", cancel);
    const viewObserver = new MutationObserver(() => { cancel(); syncSources(); });
    viewObserver.observe(root, { childList: true, subtree: true, attributes: true,
        attributeFilter: ["data-sidebar-view", "data-sidebar-search"] });
    const removalObserver = new MutationObserver(() => { if (!root.isConnected) api.dispose(); });
    removalObserver.observe(document, { childList: true, subtree: true });
    const api = {
        dispose() {
            if (disposed) return;
            disposed = true;
            cancel();
            viewObserver.disconnect();
            removalObserver.disconnect();
            for (const [name, handler] of Object.entries(events)) root.removeEventListener(name, handler, true);
            document.removeEventListener("dragend", cancel, true);
            document.removeEventListener("drop", outsideDrop, true);
            document.removeEventListener("dragstart", outsideStart, true);
            document.removeEventListener("keydown", keydown, true);
            window.removeEventListener("blur", cancel);
            if (root[ownerKey] === api) delete root[ownerKey];
        }
    };
    root[ownerKey] = api;
    syncSources();
    return api;
}
