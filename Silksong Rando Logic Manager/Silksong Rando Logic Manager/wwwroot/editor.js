const roomZoneMapVisibilityKey = "silksong-rando-logic.room-map-context-visible";
const roomMapContextVisibilityKey = "silksong-rando-logic.room-context-panel-visible";
const roomMapContextHeightKey = "silksong-rando.v2.map-context-height";
const roomMapContextRatioMinimum = 5;
const roomMapContextRatioMaximum = 9;
const mapViewPreferencePrefix = "silksong-rando-logic.map-view.";
const roomContextPreferenceStyle = document.getElementById("room-context-preferences");
const roomContextPreferences = { contextVisible: true, zoneVisible: true, ratioTerm: null };
const mapLayerPreferences = new Map();
const cssAttributeString = value => `"${String(value).replace(/[\0-\x1f\x7f"\\]/g, character => character === "\0" ? "\\fffd " : `\\${character.codePointAt(0).toString(16)} `)}"`;
if (!window.areaMapViewportResolver) window.areaMapViewportResolver = (() => {
    const storagePrefix = mapViewPreferencePrefix;
    const parseViewBox = value => {
        const raw = typeof value === "string" ? value.trim() : "";
        if (!raw) return null;
        const view = raw.split(/\s+/).map(Number);
        return view.length === 4 && view.every(Number.isFinite) && view[2] > 0 && view[3] > 0 ? view : null;
    };
    const fittedView = base => { const pad = .03; return [base[0] - base[2] * pad, base[1] - base[3] * pad, base[2] * (1 + 2 * pad), base[3] * (1 + 2 * pad)]; };
    const clamp = (view, base) => {
        const result = view.slice();
        result[2] = Math.min(base[2], Math.max(base[2] / 8, result[2]));
        result[3] = result[2] * base[3] / base[2];
        result[0] = Math.max(base[0], Math.min(base[0] + base[2] - result[2], result[0]));
        result[1] = Math.max(base[1], Math.min(base[1] + base[3] - result[3], result[1]));
        return result;
    };
    const storageKey = svg => `${storagePrefix}${svg.dataset.mapKey}`;
    const resolve = (svg, loadedState) => {
        const base = parseViewBox(svg.getAttribute("data-base-viewbox"));
        if (!base) return null;
        const initial = parseViewBox(svg.getAttribute("data-initial-viewbox")) || base;
        const navigationBounds = fittedView(base);
        const initialFit = fittedView(initial);
        const rawViewVersion = svg.getAttribute("data-map-view-version") || "";
        const hasViewVersion = rawViewVersion.trim().length > 0;
        const state = loadedState && typeof loadedState === "object" && !Array.isArray(loadedState) ? loadedState : {};
        const saved = Array.isArray(state.view) && state.view.length === 4 && state.view.every(Number.isFinite)
            && (!hasViewVersion || state.viewVersion === rawViewVersion);
        return {
            navigationBounds,
            initialFit,
            view: clamp(saved ? state.view : initialFit, navigationBounds),
            viewVersion: rawViewVersion,
            hasViewVersion
        };
    };
    return Object.freeze({ storageKey, resolve, clamp });
})();

if (window.customElements && !customElements.get("area-map-prepaint")) {
    customElements.define("area-map-prepaint", class extends HTMLElement {
        connectedCallback() {
            const svg = this.previousElementSibling;
            if (!svg?.matches?.("svg.map-wireframe")) return;
            let state = {};
            try { state = JSON.parse(sessionStorage.getItem(window.areaMapViewportResolver.storageKey(svg)) || "{}"); }
            catch { state = {}; }
            const resolved = window.areaMapViewportResolver.resolve(svg, state);
            if (resolved) svg.setAttribute("viewBox", resolved.view.join(" "));
        }
    });
}
const mapLayerPreferenceCss = () => {
    const rules = [];
    for (const [viewportKey, layers] of mapLayerPreferences) {
        const map = `.map-wireframe[data-map-key=${cssAttributeString(viewportKey)}]`;
        if (typeof layers.image === "boolean") rules.push(`${map} .map-overlay-image { display: ${layers.image ? "inline" : "none"}; }`);
        if (typeof layers.unlinked === "boolean") rules.push(`${map} [data-map-owner="unlinked"] { display: ${layers.unlinked ? "inline" : "none"};${layers.unlinked ? "" : " pointer-events: none;"} }`);
        if (typeof layers.linked === "boolean") {
            rules.push(layers.linked
                ? `${map} [data-map-owner="linked"] { fill: rgb(0 0 0 / 0%); stroke: var(--map-room-border, #fff); pointer-events: all; cursor: default; }\n${map} [data-map-owner="linked"]:is(:hover, :focus) { stroke: var(--map-room-border, #fff); stroke-width: 1px; }\n${map} [data-map-owner="linked"]:focus { stroke-width: 2px; }`
                : `${map} [data-map-owner="linked"] { fill: transparent; stroke: transparent; pointer-events: all; cursor: pointer; }\n${map} [data-map-owner="linked"]:is(:hover, :focus) { stroke: rgb(255 255 255 / 35%); stroke-width: 1px; }`);
        }
    }
    return rules.join("\n");
};
const applyRoomContextPreferences = () => {
    if (!(roomContextPreferenceStyle instanceof HTMLStyleElement)) return;
    const ratio = Number.isFinite(roomContextPreferences.ratioTerm)
        ? Math.min(roomMapContextRatioMaximum, Math.max(roomMapContextRatioMinimum, roomContextPreferences.ratioTerm))
        : roomMapContextRatioMaximum;
    const contextDisplay = roomContextPreferences.contextVisible ? "block" : "none";
    const showToggleDisplay = roomContextPreferences.contextVisible ? "none" : "inline-block";
    const hideToggleDisplay = roomContextPreferences.contextVisible ? "inline-block" : "none";
    const zoneDisplay = roomContextPreferences.zoneVisible ? "flex" : "none";
    const dividerDisplay = roomContextPreferences.zoneVisible ? "block" : "none";
    const sceneBasis = roomContextPreferences.zoneVisible ? "0" : "100%";
    roomContextPreferenceStyle.textContent = `
:root { --room-map-context-ratio-term: ${ratio}; }
.room-editor-v2 .room-map-context-height-host { display: ${contextDisplay}; }
.room-editor-v2 .room-map-context-toggle-show { display: ${showToggleDisplay}; }
.room-editor-v2 .room-map-context-toggle-hide { display: ${hideToggleDisplay}; }
.room-editor-v2 .room-map-context > .room-context-zone-pane { display: ${zoneDisplay}; }
.room-editor-v2 .room-map-context > .room-context-divider { display: ${dividerDisplay}; }
.room-editor-v2 .room-map-context > .room-context-scene-pane { flex-basis: ${sceneBasis}; }
.map-wireframe .map-overlay-image, .map-wireframe [data-map-owner="unlinked"] { display: inline; }
.map-wireframe[data-has-overlay="true"] [data-map-owner="linked"] { fill: transparent; stroke: transparent; pointer-events: all; cursor: pointer; }
.map-wireframe[data-has-overlay="true"] [data-map-owner="linked"]:is(:hover, :focus) { stroke: rgb(255 255 255 / 35%); stroke-width: 1px; }
${mapLayerPreferenceCss()}
`;
};
const setRoomMapContextVisibility = visible => { roomContextPreferences.contextVisible = visible; applyRoomContextPreferences(); };
const setRoomZoneMapVisibility = visible => { roomContextPreferences.zoneVisible = visible; applyRoomContextPreferences(); };
const applyRoomMapContextRatio = value => { roomContextPreferences.ratioTerm = Number.isFinite(value) ? value : null; applyRoomContextPreferences(); };
window.updateMapLayerPreferences = (viewportKey, layers) => {
    if (typeof viewportKey !== "string" || !layers || typeof layers !== "object") return;
    const next = { ...(mapLayerPreferences.get(viewportKey) || {}) };
    for (const layer of ["image", "linked", "unlinked"]) if (typeof layers[layer] === "boolean") next[layer] = layers[layer];
    mapLayerPreferences.set(viewportKey, next);
    applyRoomContextPreferences();
};

// This parser-blocking script runs before blazor.web.js. Resolve browser-owned
// room-context composition before the prerendered document can first paint.
try { roomContextPreferences.contextVisible = sessionStorage.getItem(roomMapContextVisibilityKey) !== "false"; }
catch { roomContextPreferences.contextVisible = true; }
try { roomContextPreferences.zoneVisible = sessionStorage.getItem(roomZoneMapVisibilityKey) !== "false"; }
catch { roomContextPreferences.zoneVisible = true; }
try {
    for (let index = 0; index < sessionStorage.length; index++) {
        const storageKey = sessionStorage.key(index);
        if (!storageKey?.startsWith(mapViewPreferencePrefix)) continue;
        let stored;
        try { stored = JSON.parse(sessionStorage.getItem(storageKey) || "{}"); }
        catch { continue; }
        if (!stored || typeof stored !== "object" || Array.isArray(stored)) continue;
        const layers = {};
        for (const layer of ["image", "linked", "unlinked"]) if (typeof stored[layer] === "boolean") layers[layer] = stored[layer];
        if (Object.keys(layers).length) mapLayerPreferences.set(storageKey.slice(mapViewPreferencePrefix.length), layers);
    }
} catch { }
try {
    const stored = localStorage.getItem(roomMapContextHeightKey);
    const value = stored === null || !stored.trim() ? Number.NaN : Number(stored);
    if (!Number.isFinite(value) || value < roomMapContextRatioMinimum || value > roomMapContextRatioMaximum) {
        localStorage.removeItem(roomMapContextHeightKey);
    } else {
        roomContextPreferences.ratioTerm = value;
    }
} catch { roomContextPreferences.ratioTerm = null; }
applyRoomContextPreferences();

window.focusAndSelect = (element) => {
    if (!element) {
        return;
    }

    element.focus();
    element.select();
};

/* V2 scene navigation is isolated to its mounted pane. It has no .NET callback
   or durable persistence. Its zone-pane preference is tab-local browser state. */
window.initializeV2SceneViewport = (svg, viewKey, ownerGeneration, placementCallback) => {
    if (!(svg instanceof SVGElement)) return;
    // Test and legacy callers without an explicit owner generation remain a
    // browser-only owner; mounted V2 panes always provide the fourth argument.
    if (placementCallback === undefined && ownerGeneration && typeof ownerGeneration === "object") { placementCallback = ownerGeneration; ownerGeneration = 0; }
    const pane = svg.closest(".room-context-scene-pane");
    const initial = String(svg.dataset.sceneInitialViewbox).split(" ").map(Number);
    if (!(pane instanceof HTMLElement) || initial.length !== 4 || !initial.every(Number.isFinite)) return;
    let state = svg.__v2SceneViewport;
    // Blazor may replace the image/control subtree after Apply or recapture while
    // retaining this SVG and room key. Reconcile those current nodes without
    // resetting the pane-local shown state or viewport lifecycle.
    if (state?.key === viewKey) { state.reconcile?.(ownerGeneration); svg.closest(".room-editor-v2")?.[v2ChildInteractionOwnerKey]?.reconcileAttention?.(); return; }
    state?.dispose();
    const parse = value => String(value).split(" ").map(Number), apply = view => svg.setAttribute("viewBox", view.join(" "));
    const clamp = view => { const width = Math.min(initial[2], Math.max(initial[2] / 8, view[2])), height = width * initial[3] / initial[2]; return [Math.max(initial[0], Math.min(initial[0] + initial[2] - width, view[0])), Math.max(initial[1], Math.min(initial[1] + initial[3] - height, view[1])), width, height]; };
    const point = (x, y) => { const value = svg.createSVGPoint(); value.x = x; value.y = y; return value.matrixTransform(svg.getScreenCTM().inverse()); };
    // A reused SVG can receive a new room owner before an earlier marker command
    // settles. Marker-command continuations may update only their owner generation.
    const generation = (Number(svg.__v2SceneViewportGeneration) || 0) + 1;
    svg.__v2SceneViewportGeneration = generation;
    let down = null, imageShown = true, zoneShown = true, selected = null, armed = null, nudgeTail = Promise.resolve(), nudgeGeometry = null, nudgeEpoch = 0, active = true, draft = null, failedMarker = null, frameVisual = null, relationshipThreads = [], currentOwnerGeneration = ownerGeneration;
    const layers = { subroom: true, exit: true, check: true }, layerEpochs = { subroom: 0, exit: 0, check: 0 };
    const cancelledArms = { subroom: false, exit: false, check: false };
    let explicitIntent = null, capturedIntent = null;
    let latestArmRevision = -1, latestSelectionRevision = -1;
    const category = kind => kind === "connection" ? "exit" : kind;
    const visibleKind = kind => layers[category(kind)] === true;
    const visibleItem = item => !!item && visibleKind(item.dataset.sceneSelectionKind);
    const layerOperation = kind => { const key = category(kind), epoch = layerEpochs[key]; return currentOperation(() => layerEpochs[key] === epoch && layers[key]); };
    const ownsCurrentGeneration = () => active && svg.__v2SceneViewportGeneration === generation;
    // Every scene-owned async continuation captures this owner generation. This
    // is deliberately local to the named scene browser owner: a route/SVG-owner
    // replacement or disposal invalidates all prior completions without creating
    // a global async framework or changing command admission.
    const currentOperation = (operationIsCurrent = () => true) => {
        const operationGeneration = generation;
        return () => ownsCurrentGeneration() && operationGeneration === generation && operationIsCurrent();
    };
    // Every .NET scene callback carries the mounted page owner. The page repeats
    // this admission before any transient-state mutation or durable command.
    const callback = (method, ...args) => placementCallback?.invokeMethodAsync(method, viewKey, currentOwnerGeneration, ...args);
    // Passive synchronous presentation handoff only. The child owner owns
    // projection/cleanup; this owner remains the sole gesture/selection owner.
    const attentionRoot = svg.closest(".room-editor-v2");
    const syncAttention = () => attentionRoot?.[v2ChildInteractionOwnerKey]?.syncAttention?.();
    // Pointer-up clears `down`, but a committed marker drag is allowed to retain
    // its browser visual until its one command/refresh settles. Track that visual
    // separately so replacing or disposing this room owner cannot strand it.
    const markerVisuals = new Set();
    const markerVisual = id => { let found = null; markerVisuals.forEach(value => { if (value.id === id) found = value; }); return found; };
    const relationshipGeometry = thread => {
        const marker = markerVisual(thread.markerId), markerGeometry = marker ? { x: marker.x, y: marker.y } : thread.marker;
        const frameGeometry = frameVisual?.item?.dataset?.sceneId?.toLowerCase() === thread.frameId ? frameVisual.geometry : thread.frame;
        return { marker: markerGeometry, frame: frameGeometry };
    };
    const renderRelationship = (thread, markerOverride = null, frameOverride = null) => {
        const activeGeometry = relationshipGeometry(thread), marker = markerOverride || activeGeometry.marker, frame = frameOverride || activeGeometry.frame;
        const right = frame.x + frame.w, top = frame.y + frame.h;
        const inside = marker.x >= frame.x && marker.x <= right && marker.y >= frame.y && marker.y <= top;
        thread.node.style.display = inside || !layers.subroom || !visibleKind(thread.markerKind) ? "none" : "";
        thread.node.setAttribute("x1", marker.x); thread.node.setAttribute("y1", -marker.y);
        thread.node.setAttribute("x2", Math.max(frame.x, Math.min(right, marker.x))); thread.node.setAttribute("y2", -Math.max(frame.y, Math.min(top, marker.y)));
    };
    const rebuildRelationships = () => {
        relationshipThreads = [...(svg.querySelectorAll?.("[data-scene-relationship-thread=true]") || [])].map(node => ({
            node, markerId: node.dataset.sceneThreadMarkerId?.toLowerCase(), frameId: node.dataset.sceneThreadFrameId?.toLowerCase(),
            markerKind: svg.querySelector(`[data-scene-layout-marker][data-scene-id='${node.dataset.sceneThreadMarkerId}']`)?.dataset.sceneSelectionKind,
            marker: { x: Number(node.dataset.sceneThreadMarkerX), y: Number(node.dataset.sceneThreadMarkerY) },
            frame: { x: Number(node.dataset.sceneThreadFrameX), y: Number(node.dataset.sceneThreadFrameY), w: Number(node.dataset.sceneThreadFrameWidth), h: Number(node.dataset.sceneThreadFrameHeight) }
        })).filter(thread => thread.markerId && thread.frameId && [thread.marker.x, thread.marker.y, thread.frame.x, thread.frame.y, thread.frame.w, thread.frame.h].every(Number.isFinite) && thread.frame.w > 0 && thread.frame.h > 0);
        relationshipThreads.forEach(thread => renderRelationship(thread));
    };
    const updateMarkerRelationships = (id, geometry = null) => { relationshipThreads.forEach(thread => { if (thread.markerId === id.toLowerCase()) renderRelationship(thread, geometry); }); syncAttention(); };
    const updateFrameRelationships = (id, geometry = null) => { relationshipThreads.forEach(thread => { if (thread.frameId === id.toLowerCase()) renderRelationship(thread, null, geometry); }); syncAttention(); };
    const settleMarkerVisual = item => { const id = item.dataset.sceneId.toLowerCase(), visual = [...markerVisuals].find(value => value.id === id); item.style.transform=""; if (visual) { visual.item.style.transform=""; markerVisuals.delete(visual); updateMarkerRelationships(visual.id); } };
    const clearMarkerVisuals = () => { markerVisuals.forEach(value => value.item.style.transform=""); markerVisuals.clear(); syncAttention(); };
    const reset = pane.querySelector("[data-scene-reset-view=true]"), status = pane.querySelector("[data-scene-status-text=true]"), retryButton = pane.querySelector("[data-scene-marker-retry=true]");
    let layerControls = [];
    let image = svg.querySelector?.("[data-scene-layout-image=true]"), imageToggle = pane.querySelector("[data-scene-image-toggle=true]"), zoneToggle = pane.querySelector("[data-scene-zone-toggle=true]");
    const applyLayers = () => {
        svg.querySelectorAll("[data-scene-selection-kind]").forEach(item => item.style.display = visibleItem(item) ? "" : "none");
        for (const { node, key } of layerControls) {
            const shown = key ? layers[key] : Object.values(layers).every(Boolean);
            const label = `${shown ? "Hide" : "Show"} ${!key ? "annotations" : key === "subroom" ? "subrooms" : key === "exit" ? "exits and connections" : "check locations and enemies"}`;
            node.setAttribute("aria-pressed", String(shown)); node.setAttribute("aria-label", label); node.setAttribute("title", label);
        }
        relationshipThreads.forEach(thread => renderRelationship(thread));
        syncAttention();
    };
    const setLayer = (kind, shown) => {
        const key = category(kind);
        if (!active || !(key in layers) || layers[key] === shown) return;
        layers[key] = shown;
        if (!shown) {
            layerEpochs[key]++;
            cancelledArms[key] = true;
            if (category(explicitIntent?.kind) === key) explicitIntent = null;
            if (category(capturedIntent?.kind) === key) capturedIntent = null;
            if (category(armed?.kind) === key) { armed = null; clearDraft(); if (status) status.textContent = ""; svg.classList.remove("scene-layout-placement-armed"); svg.style.cursor = ""; }
            if (category(selected?.dataset.sceneSelectionKind) === key) { selected.classList.remove("selected"); selected = null; nudgeEpoch++; nudgeGeometry = null; }
            const gestureKind = down?.drawStart ? "subroom" : (down?.frame || down?.drag)?.dataset.sceneSelectionKind;
            if (category(gestureKind) === key) cancelGesture();
            if (key === "subroom") {
                restoreFrameVisual(); clearSnap();
                // A different category's gesture is preserved; remove only the
                // now-ineligible frame targets/locks from its snap candidates.
                if (down?.candidates) down.candidates = candidates(down.frame);
            }
            for (const visual of [...markerVisuals]) if (category(visual.item.dataset.sceneSelectionKind) === key) { visual.item.style.transform = ""; markerVisuals.delete(visual); }
            if (category(failedMarker?.kind) === key) clearFailure();
        }
        applyLayers();
        callback("SetSceneLayerVisibilityAsync", key, shown)?.catch(() => {});
    };
    const bindLayerControls = () => {
        for (const { node, click } of layerControls) node.removeEventListener("click", click);
        layerControls = [...pane.querySelectorAll("[data-scene-layer-toggle], [data-scene-annotation-toggle=true]")].map(node => {
            const key = node.dataset.sceneLayerToggle, click = () => {
                if (!active) return;
                if (key) setLayer(key, !layers[key]);
                else {
                    const shown = !Object.values(layers).every(Boolean);
                    for (const categoryKey of Object.keys(layers)) setLayer(categoryKey, shown);
                }
            };
            node.addEventListener("click", click); return { node, key, click };
        });
    };
    const clearDraft = () => { draft?.remove(); draft=null; };
    // A resize cannot use a group translation: the body, each exterior target,
    // and the upright label all have distinct prospective geometry.  Keep their
    // durable attributes so cancellation/rejection can restore the exact frame.
    const frameParts = item => ({
        body: item.querySelector?.("[data-scene-frame-target='move']"), n: item.querySelector?.(".edge-n"), s: item.querySelector?.(".edge-s"), e: item.querySelector?.(".edge-e"), w: item.querySelector?.(".edge-w"),
        nw: item.querySelector?.(".corner-nw"), ne: item.querySelector?.(".corner-ne"), sw: item.querySelector?.(".corner-sw"), se: item.querySelector?.(".corner-se"), label: item.querySelector?.("text")
    });
    const captureFrameValues = item => {
        const values = [];
        const remember = (node, names) => { if (node) for (const name of names) values.push([node, name, node.getAttribute(name)]); };
        const parts = frameParts(item);
        remember(parts.body, ["x", "y", "width", "height"]);
        [parts.n, parts.s, parts.e, parts.w, parts.nw, parts.ne, parts.sw, parts.se].forEach(node => remember(node, ["x", "y", "width", "height"]));
        remember(parts.label, ["x", "y"]);
        return values;
    };
    const rememberFrameVisual = item => {
        if (frameVisual?.item === item) return frameVisual;
        restoreFrameVisual();
        // Labels are upright presentation, never frame geometry.  Restore their
        // durable coordinates, but never retain a prior SVG transform that could
        // vertically flip a label after an interrupted client-first resize.
        return frameVisual = { item, values: captureFrameValues(item), geometry: null, mode: null, reconciled: false };
    };
    const restoreFrameVisual = () => {
        if (!frameVisual) return;
        const frameId = frameVisual.item.dataset.sceneId;
        frameVisual.item.style.transform = "";
        if (frameVisual.reconciled) {
            applyFrameGeometry(frameVisual.item, { x: Number(frameVisual.item.dataset.sceneFrameX), y: Number(frameVisual.item.dataset.sceneFrameY), w: Number(frameVisual.item.dataset.sceneFrameWidth), h: Number(frameVisual.item.dataset.sceneFrameHeight) });
        } else {
            for (const [node, name, value] of frameVisual.values) {
                if (value === null) node.removeAttribute?.(name); else node.setAttribute(name, value);
            }
        }
        frameParts(frameVisual.item).label?.removeAttribute("transform");
        frameVisual = null;
        updateFrameRelationships(frameId);
    };
    const applyFrameGeometry = (item, value) => {
        const p = frameParts(item), x = value.x, y = value.y, w = value.w, h = value.h, top = -y - h, bottom = -y, middle = -y - h / 2;
        const set = (node, attrs) => { if (node) for (const [name, attribute] of Object.entries(attrs)) node.setAttribute(name, attribute); };
        set(p.body, { x, y: top, width: w, height: h });
        set(p.n, { x, y: top - 1, width: w, height: 1 }); set(p.s, { x, y: bottom, width: w, height: 1 });
        set(p.e, { x: x + w, y: top, width: 1, height: h }); set(p.w, { x: x - 1, y: top, width: 1, height: h });
        set(p.nw, { x: x - 1, y: top - 1, width: 2, height: 2 }); set(p.ne, { x: x + w - 1, y: top - 1, width: 2, height: 2 });
        set(p.sw, { x: x - 1, y: bottom - 1, width: 2, height: 2 }); set(p.se, { x: x + w - 1, y: bottom - 1, width: 2, height: 2 });
        set(p.label, { x: x + w / 2, y: middle });
        p.label?.removeAttribute("transform");
    };
    const showFrameVisual = (item, value, mode) => {
        rememberFrameVisual(item);
        frameVisual.geometry = value;
        frameVisual.mode = mode;
        if (mode === "move") {
            item.style.transform = `translate(${value.x - Number(item.dataset.sceneFrameX)}px, ${-(value.y - Number(item.dataset.sceneFrameY))}px)`;
        } else {
            item.style.transform = "";
            applyFrameGeometry(item, value);
        }
        updateFrameRelationships(item.dataset.sceneId, value);
    };
    const settleFrameVisual = (item, committed) => {
        if (!frameVisual || frameVisual.item.dataset.sceneId !== item.dataset.sceneId) return;
        if (!committed) { restoreFrameVisual(); return; }
        const currentItem = frameVisual.item;
        currentItem.style.transform = "";
        applyFrameGeometry(currentItem, { x: Number(currentItem.dataset.sceneFrameX), y: Number(currentItem.dataset.sceneFrameY), w: Number(currentItem.dataset.sceneFrameWidth), h: Number(currentItem.dataset.sceneFrameHeight) });
        frameVisual = null;
        updateFrameRelationships(currentItem.dataset.sceneId);
    };
    const armedInstruction = value => value?.kind === "subroom"
        ? "draw subroom rectangle; drag to the opposite corner, or press Escape to cancel"
        : value ? `place ${value.kind} annotation; click the scene, or press Escape to cancel` : "";
    const clearFailure = () => { failedMarker=null; if(status && !armed) status.textContent=""; if(retryButton) retryButton.hidden=true; };
    // The typed .NET identity is authoritative and deliberately survives a
    // nonrenderable item. The browser mirrors only currently rendered geometry.
    const notifySelection = item => { const valid=currentOperation(), owner=currentOwnerGeneration; const call=item ? placementCallback?.invokeMethodAsync("SelectSceneItemAsync",viewKey,owner,item.dataset.sceneSelectionKind,item.dataset.sceneId) : placementCallback?.invokeMethodAsync("ClearSceneSelectionAsync",viewKey,owner); call?.then(()=>{if(!valid()||owner!==currentOwnerGeneration)return;}).catch(()=>{}); };
    const failure = value => { failedMarker=value; if(status) status.textContent="marker save failed"; if(retryButton) retryButton.hidden=false; };
    const retryMarker = () => { const value=failedMarker; if(!value || !visibleKind(value.kind)) return; const valid=layerOperation(value.kind); callback("CommitMarkerDragAsync",value.kind,value.id,value.x,value.y).then(ok=>{if(valid()&&ok)clearFailure();}).catch(()=>{}); };
    // Image visibility is deliberately owned entirely by this named mounted-pane
    // owner. It has no .NET callback, command, map load, or projection work.
    const showImage = shown => { if (!active) return; imageShown = shown; if (image) image.style.display = shown ? "" : "none"; if (imageToggle) { imageToggle.setAttribute("aria-pressed", String(shown)); imageToggle.setAttribute("aria-label", shown ? "Hide scene image" : "Show scene image"); imageToggle.setAttribute("title", shown ? "Hide scene image" : "Show scene image"); } };
    // This named scene owner, rather than the legacy map child, owns the V2
    // tab-local split-pane preference.  Hiding retains the compatibility child
    // and its own browser state, but removes its pane and divider from layout.
    const setZoneVisible = shown => { if (!active) return; zoneShown = shown; setRoomZoneMapVisibility(shown); const context = pane.closest(".room-map-context"), zonePane = context?.querySelector?.(".room-context-zone-pane"), divider = context?.querySelector?.(".room-context-divider"); context?.classList.toggle("scene-layout-only", !shown); context?.classList.toggle("with-zone-map", shown); if (zonePane) { zonePane.hidden = !shown; zonePane.setAttribute("aria-hidden", String(!shown)); } if (divider) { divider.hidden = !shown; divider.setAttribute("aria-hidden", "true"); } if (zoneToggle) { zoneToggle.setAttribute("aria-pressed", String(shown)); zoneToggle.setAttribute("aria-label", shown ? "Hide zone map" : "Show zone map"); zoneToggle.setAttribute("title", shown ? "Hide zone map" : "Show zone map"); const icon=zoneToggle.querySelector("i"); if(icon){icon.classList.toggle("fa-chevron-left",shown);icon.classList.toggle("fa-chevron-right",!shown);} } };
    const resetView = () => { clearFailure(); apply(initial.slice()); };
    const wheel = event => { clearFailure(); event.preventDefault(); const current = parse(svg.getAttribute("viewBox")), at = point(event.clientX, event.clientY), pixels = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? svg.clientHeight : 1), factor = Math.exp(Math.max(-.06, Math.min(.06, pixels * .003))), width = Math.min(initial[2], Math.max(initial[2] / 8, current[2] * factor)), effective = width / current[2]; apply(clamp([at.x - (at.x - current[0]) * effective, at.y - (at.y - current[1]) * effective, width, width * initial[3] / initial[2]])); };
    const snapClass = "scene-layout-snap-target-edge", guideClass = "scene-layout-snap-guide";
    const clearSnap = (resetLocks=true) => { [...(svg.querySelectorAll?.(`.${snapClass}`) || [])].forEach(x => x.classList.remove(snapClass)); [...(svg.querySelectorAll?.(`.${guideClass}`) || [])].forEach(x => x.remove()); if (resetLocks && down) down.locks = {}; };
    const valid = item => ["sceneFrameX", "sceneFrameY", "sceneFrameWidth", "sceneFrameHeight"].every(k => Number.isFinite(Number(item.dataset[k]))) && Number(item.dataset.sceneFrameWidth)>0 && Number(item.dataset.sceneFrameHeight)>0;
    const candidates = excluded => { const result={x:[],y:[]}; const add=(value,axis,node,edge)=>result[axis].push({value,node,edge}); const room=svg.querySelector?.(".scene-layout-room-bounds"); if(room){const x=Number(room.getAttribute("x")),top=-Number(room.getAttribute("y")),w=Number(room.getAttribute("width")),h=Number(room.getAttribute("height"));if(w>0&&h>0){add(x,"x",room,"l");add(x+w,"x",room,"r");add(top-h,"y",room,"b");add(top,"y",room,"t");}} svg.querySelectorAll?.("[data-scene-layout-frame=true]").forEach(frame=>{if(frame===excluded||!visibleItem(frame)||!valid(frame))return;const x=Number(frame.dataset.sceneFrameX),y=Number(frame.dataset.sceneFrameY),w=Number(frame.dataset.sceneFrameWidth),h=Number(frame.dataset.sceneFrameHeight);add(x,"x",frame,"l");add(x+w,"x",frame,"r");add(y,"y",frame,"b");add(y+h,"y",frame,"t");}); return result; };
    const scale = axis => { const m=svg.getScreenCTM?.(); const length=axis==="x"?Math.hypot(m?.a||0,m?.b||0):Math.hypot(m?.c||0,m?.d||0); return length>0?6/length:0; };
    const targetEdge = candidate => candidate.node.classList?.contains("scene-layout-room-bounds") ? svg.querySelector?.(`[data-scene-snap-room-edge='${candidate.edge}']`) : candidate.node.querySelector?.(candidate.edge==="l"?".edge-w":candidate.edge==="r"?".edge-e":candidate.edge==="b"?".edge-s":".edge-n");
    const renderSnap = (locked, axis) => { const target=targetEdge(locked); target?.classList.add(snapClass); if(!document.createElementNS)return;const line=document.createElementNS("http://www.w3.org/2000/svg","line");line.classList.add(guideClass);if(axis==="x"){line.setAttribute("x1",locked.value);line.setAttribute("x2",locked.value);line.setAttribute("y1",-Number(svg.dataset.sceneMinY));line.setAttribute("y2",-Number(svg.dataset.sceneMaxY));}else{const y=-locked.value;line.setAttribute("x1",Number(svg.dataset.sceneMinX));line.setAttribute("x2",Number(svg.dataset.sceneMaxX));line.setAttribute("y1",y);line.setAttribute("y2",y);}svg.appendChild(line); };
    const snap = (rectangle,event) => { clearSnap(false); if(!down||down.snapDisabled||event.ctrlKey){if(down)down.locks={};return rectangle;}const edge=(name,value,axis)=>{const threshold=scale(axis), list=down.candidates[axis], lock=down.locks[name];let choice=lock&&Math.abs(lock.value-value)<=threshold*2?lock:null;if(!choice)choice=list.map(c=>({c,d:Math.abs(c.value-value)})).filter(x=>x.d<=threshold).sort((a,b)=>a.d-b.d||a.c.value-b.c.value)[0]?.c;if(choice){down.locks[name]=choice;renderSnap(choice,axis);return choice.value;}delete down.locks[name];return value;};const l=edge("l",rectangle.x,"x"),r=edge("r",rectangle.x+rectangle.w,"x"),b=edge("b",rectangle.y,"y"),t=edge("t",rectangle.y+rectangle.h,"y");return{x:Math.min(l,r),y:Math.min(b,t),w:Math.max(.0001,Math.abs(r-l)),h:Math.max(.0001,Math.abs(t-b))}; };
    const geometry = event => { const at=point(event.clientX,event.clientY); if(down.drawStart){const start=down.drawStart, sy=-start.y, ay=-at.y;return{x:Math.min(start.x,at.x),y:Math.min(sy,ay),w:Math.abs(at.x-start.x),h:Math.abs(ay-sy)};}const item=down.frame,dx=at.x-down.origin.x,dy=-(at.y-down.origin.y),x=Number(item.dataset.sceneFrameX),y=Number(item.dataset.sceneFrameY),w=Number(item.dataset.sceneFrameWidth),h=Number(item.dataset.sceneFrameHeight),m=down.mode;if(m==="move")return{x:x+dx,y:y+dy,w,h};return{x:m.includes("l")?x+dx:x,y:m.includes("b")?y+dy:y,w:m.includes("l")?w-dx:m.includes("r")?w+dx:w,h:m.includes("b")?h-dy:m.includes("t")?h+dy:h}; };
    const start = event => { if (event.button !== 0) return; clearFailure(); const at=point(event.clientX,event.clientY), snapStart=(extra={})=>Object.assign({id:event.pointerId,x:event.clientX,y:event.clientY,locks:{},snapDisabled:!!event.ctrlKey},extra); if (armed?.kind === "subroom") { event.preventDefault(); down=snapStart({drawStart:at}); down.candidates=candidates(); return; } if (armed) { event.preventDefault(); return; } const target=event.target?.closest?.("[data-scene-frame-target]"), item=event.target?.closest?.("[data-scene-selection-kind]"); if(item && item===selected && item.dataset.sceneSelectionKind==="subroom" && target) {down=snapStart({frame:item,mode:target.dataset.sceneFrameTarget,origin:at});down.candidates=candidates(item);} else if(item && item===selected && (item.dataset.sceneSelectionKind==="exit"||item.dataset.sceneSelectionKind==="check"||item.dataset.sceneSelectionKind==="connection")) down={id:event.pointerId,x:event.clientX,y:event.clientY,lastX:event.clientX,lastY:event.clientY,drag:item}; else if(event.target===svg||event.target?.classList?.contains("scene-layout-room-bounds")) down={id:event.pointerId,x:event.clientX,y:event.clientY,lastX:event.clientX,lastY:event.clientY,panning:false}; };
    const move = event => { if (!down || event.pointerId !== down.id || Math.hypot(event.clientX-down.x,event.clientY-down.y)<5) return; if(down.drawStart||down.frame){down.moved=true;const value=snap(geometry(event),event); if(down.drawStart && document.createElementNS){clearDraft();draft=document.createElementNS("http://www.w3.org/2000/svg","rect");draft.classList.add("scene-layout-draft");draft.setAttribute("x",value.x);draft.setAttribute("y",-value.y-value.h);draft.setAttribute("width",value.w);draft.setAttribute("height",value.h);svg.appendChild(draft);} if(down.frame)showFrameVisual(down.frame,value,down.mode); return;} if(down.drag){down.moved=true;const current=point(event.clientX,event.clientY), origin=point(down.x,down.y), dx=current.x-origin.x, svgDy=current.y-origin.y;down.drag.style.transform=`translate(${dx}px, ${svgDy}px)`;updateMarkerRelationships(down.drag.dataset.sceneId,{x:Number(down.drag.dataset.sceneMarkerX)+dx,y:Number(down.drag.dataset.sceneMarkerY)-svgDy});return;} if (!down.panning) { down.panning = true; svg.setPointerCapture(down.id); } const previous = point(down.lastX, down.lastY), current = point(event.clientX, event.clientY), view = parse(svg.getAttribute("viewBox")); down.lastX = event.clientX; down.lastY = event.clientY; apply(clamp([view[0] - (current.x - previous.x), view[1] - (current.y - previous.y), view[2], view[3]])); };
    const select = event => {
        if (event.button !== 0) return;
        clearFailure();
        if (down?.drawStart) { const value=snap(geometry(event),event); clearDraft(); if(down.moved && value.w>0 && value.h>0) callback("CommitSubroomGeometryAsync", armed.entityId,value.x,value.y,value.w,value.h).catch(()=>{}); else callback("CancelPlacementAsync").catch(()=>{}); return; }
        if (armed) {
            const at = point(event.clientX, event.clientY), request = armed;
            // This is the sole durable scene gesture boundary. Arming, cursor,
            // hover, pan, zoom, and cancellation never invoke .NET.
            callback("CommitPlacementAsync", request.kind, request.entityId, at.x, -at.y).catch(() => {});
            return;
        }
        if(down && down.frame && down.moved){const item=down.frame,value=snap(geometry(event),event),valid=layerOperation("subroom");if(value.w>0&&value.h>0) callback("CommitSubroomGeometryAsync",item.dataset.sceneId,value.x,value.y,value.w,value.h).then(ok=>{if(!valid())return;settleFrameVisual(item,ok);}).catch(()=>{if(!valid())return;settleFrameVisual(item,false);}); return;} if(down && down.drag && down.moved){const item=down.drag, at=point(event.clientX,event.clientY),origin=point(down.x,down.y),dx=at.x-origin.x,svgDy=at.y-origin.y,valid=layerOperation(item.dataset.sceneSelectionKind),visual={item,id:item.dataset.sceneId.toLowerCase(),x:Number(item.dataset.sceneMarkerX)+dx,y:Number(item.dataset.sceneMarkerY)-svgDy};item.style.transform=`translate(${dx}px, ${svgDy}px)`;markerVisuals.add(visual);updateMarkerRelationships(visual.id); callback("CommitMarkerDragAsync",item.dataset.sceneSelectionKind,item.dataset.sceneId,at.x,-at.y).then(ok=>{if(!valid())return;settleMarkerVisual(item);if(ok){clearFailure();}else failure({kind:item.dataset.sceneSelectionKind,id:item.dataset.sceneId,x:at.x,y:-at.y});}).catch(()=>{if(!valid())return;settleMarkerVisual(item);failure({kind:item.dataset.sceneSelectionKind,id:item.dataset.sceneId,x:at.x,y:-at.y});}); return;} const item = event.target?.closest?.("[data-scene-selection-kind]");
        if (!item && down?.panning) return;
        if (item && !visibleItem(item)) return;
        if (selected === item) return;
        selected?.classList.remove("selected"); selected = item || null;
        explicitIntent = null;
        capturedIntent = null;
        if (selected) { selected.classList.add("selected"); cancelledArms[category(selected.dataset.sceneSelectionKind)] = false; }
        syncAttention();
        notifySelection(selected);
        clearFailure();
    };
    const end = (event, cancelled=false) => { if (cancelled) clearFailure(); if (down?.id === event.pointerId) { if (cancelled && down.drag) { down.drag.style.transform=""; updateMarkerRelationships(down.drag.dataset.sceneId); } if (cancelled && down.frame) restoreFrameVisual(); if (svg.hasPointerCapture(event.pointerId)) svg.releasePointerCapture(event.pointerId); clearDraft(); clearSnap(); down = null; } };
    const toggleImage = () => showImage(!imageShown), toggleZone = () => { clearFailure(); const shown = !zoneShown; setZoneVisible(shown); window.saveRoomZoneMapVisibility?.(shown); };
    const reconcile = ownerGeneration => {
        currentOwnerGeneration = ownerGeneration;
        bindLayerControls();
        // Detach replaced controls before observing the new DOM. A stale control
        // cannot mutate the current owner after a render replacement.
        imageToggle?.removeEventListener("click", toggleImage);
        zoneToggle?.removeEventListener("click", toggleZone);
        image = svg.querySelector?.("[data-scene-layout-image=true]");
        imageToggle = pane.querySelector("[data-scene-image-toggle=true]");
        zoneToggle = pane.querySelector("[data-scene-zone-toggle=true]");
        imageToggle?.addEventListener("click", toggleImage);
        zoneToggle?.addEventListener("click", toggleZone);
        // A same-room authoritative reconciliation may replace scene data while a
        // draw is active. Drafts and snap feedback are ephemeral, so remove them
        // before that refresh completes. Keep an in-flight frame/marker transform:
        // it is the separately permitted client-first visual until its command
        // resolves against the authoritative refresh.
        clearDraft();
        clearSnap();
        if (down?.drawStart) down = null;
        markerVisuals.forEach(visual => {
            const currentItem = svg.querySelector?.(`[data-scene-id='${visual.id}'][data-scene-layout-marker]`);
            if (currentItem) visual.item = currentItem;
            visual.item.style.transform = `translate(${visual.x - Number(visual.item.dataset.sceneMarkerX)}px, ${-(visual.y - Number(visual.item.dataset.sceneMarkerY))}px)`;
        });
        if (frameVisual) {
            const currentFrame = svg.querySelector?.(`[data-scene-id='${frameVisual.item.dataset.sceneId}'][data-scene-layout-frame=true]`);
            if (currentFrame) {
                frameVisual.item = currentFrame;
                frameVisual.item.style.transform = "";
                frameVisual.reconciled = true;
            } else frameVisual = null;
        }
        rebuildRelationships();
        if (frameVisual) showFrameVisual(frameVisual.item, frameVisual.geometry, frameVisual.mode);
        applyLayers();
        showImage(imageShown);
        setZoneVisible(zoneShown);
        syncAttention();
    };
    const cancelGesture = () => { clearFailure(); clearDraft(); clearSnap(); if (down?.drag) { down.drag.style.transform=""; updateMarkerRelationships(down.drag.dataset.sceneId); } if (frameVisual) restoreFrameVisual(); if (down && svg.hasPointerCapture(down.id)) svg.releasePointerCapture(down.id); down=null; };
    const cancelPlacement = event => { if (event.type === "keydown" && event.key !== "Escape") return; capturedIntent = null; cancelGesture(); if (!armed) return; event.preventDefault(); callback("CancelPlacementAsync").catch(() => {}); };
    const cancelDrag = event => end(event, true);
    // Key repeats arrive before the server refreshes DOM attributes.  Chain from
    // the accepted/pending geometry, never the stale rendered dataset.
    const nudge = event => { if(!visibleItem(selected) || !["ArrowLeft","ArrowRight","ArrowUp","ArrowDown"].includes(event.key)) return; clearFailure(); event.preventDefault(); const item=selected, kind=item.dataset.sceneSelectionKind, frame=kind==="subroom", amount=event.shiftKey?5:1, base=nudgeGeometry?.id===item.dataset.sceneId?nudgeGeometry:{id:item.dataset.sceneId,kind,x:Number(frame?item.dataset.sceneFrameX:item.dataset.sceneMarkerX),y:Number(frame?item.dataset.sceneFrameY:item.dataset.sceneMarkerY)}; const next={...base,x:base.x+(event.key==="ArrowLeft"?-amount:event.key==="ArrowRight"?amount:0),y:base.y+(event.key==="ArrowDown"?-amount:event.key==="ArrowUp"?amount:0)}, epoch=nudgeEpoch,layerCurrent=layerOperation(kind),valid=()=>layerCurrent()&&epoch===nudgeEpoch; nudgeGeometry=next; nudgeTail=nudgeTail.then(async()=>{if(!valid())return;try{const ok=await callback("CommitSceneNudgeAsync",kind,item.dataset.sceneId,next.x,next.y);if(valid()&&!ok){nudgeEpoch++;nudgeGeometry=null;}}catch{if(valid()){nudgeEpoch++;nudgeGeometry=null;}}}); };
    svg.addEventListener("wheel", wheel, { passive:false }); svg.addEventListener("pointerdown", start); svg.addEventListener("pointermove", move); svg.addEventListener("pointerup", select); svg.addEventListener("pointerup", end); svg.addEventListener("pointercancel", cancelDrag); svg.addEventListener("keydown", cancelPlacement); svg.addEventListener("keydown", nudge); svg.addEventListener("contextmenu", cancelPlacement); imageToggle?.addEventListener("click", toggleImage); zoneToggle?.addEventListener("click", toggleZone); reset?.addEventListener("click", resetView);
    retryButton?.addEventListener("click", retryMarker);
    // Capture precedes authoritative page admission. A candidate may reveal its
    // layer, but cannot retire the retained interaction or release a cancelled
    // arm. Only a matching newer page parameter delivery confirms that identity.
    const confirmCapturedIntent = (kind, id, revision, selection) => {
        if (!capturedIntent || capturedIntent.kind !== kind ||
            (selection ? capturedIntent.selectionId : capturedIntent.id) !== id.toLowerCase().replaceAll("-", "") ||
            revision <= (selection ? capturedIntent.selectionRevision : capturedIntent.armRevision)) return;
        cancelGesture();
        explicitIntent = capturedIntent; capturedIntent = null;
        cancelledArms[category(kind)] = false;
    };
    const arm = (kind, entityId, acquireFocus, ownerGeneration, revision) => {
        if (!ownsCurrentGeneration() || ownerGeneration !== currentOwnerGeneration || Number(svg.dataset.sceneOwnerGeneration) !== ownerGeneration || !svg.isConnected) return;
        if (!Number.isFinite(revision) || revision < latestArmRevision) return;
        if (!visibleKind(kind)) return;
        confirmCapturedIntent(kind, entityId, revision, false);
        const intentMatches = explicitIntent?.kind === kind && explicitIntent.id === entityId.toLowerCase().replaceAll("-", "");
        if (!visibleKind(kind) || cancelledArms[category(kind)] || (explicitIntent && !intentMatches && armed?.kind !== kind)) return;
        latestArmRevision = revision;
        // Owned canonical connection reconciliation may retarget the retained
        // request. It updates identity only, never visibility or focus intent.
        if (explicitIntent && !intentMatches && armed?.kind === "connection" && kind === "connection") { explicitIntent.id = entityId.toLowerCase().replaceAll("-", ""); explicitIntent.selectionId = explicitIntent.id; }
        clearFailure(); clearSnap(); armed={kind,entityId};
        if(status) status.textContent=armedInstruction(armed);
        svg.classList.add("scene-layout-placement-armed"); svg.style.cursor="crosshair";
        // Render reconciliation retains placement but never invents focus intent.
        // The current root's typed dialog fact also guards delayed interop delivery.
        if (acquireFocus === true && (!explicitIntent || !explicitIntent.focusConsumed) && attentionRoot?.isConnected && attentionRoot.dataset.v2DialogOpen === "false") { if (explicitIntent) explicitIntent.focusConsumed = true; svg.focus(); }
    };
    const ownsRevision = (owner, revision, latest) => ownsCurrentGeneration() && owner === currentOwnerGeneration && Number(svg.dataset.sceneOwnerGeneration) === owner && svg.isConnected && Number.isFinite(revision) && revision >= latest;
    const clearPlacement = (owner, revision) => { if (!ownsRevision(owner, revision, latestArmRevision)) return; latestArmRevision=revision; if (armed) clearSnap(); armed=null; svg.classList.remove("scene-layout-placement-armed"); svg.style.cursor=""; };
    const clearSelection = (owner, revision) => { if (!ownsRevision(owner, revision, latestSelectionRevision)) return; latestSelectionRevision=revision; if (!selected) return; cancelGesture(); clearFailure(); nudgeEpoch++; nudgeGeometry=null; selected.classList.remove("selected"); selected=null; syncAttention(); };
    const selectItem = (id, owner, revision) => {
        if (!ownsRevision(owner, revision, latestSelectionRevision)) return;
        const item=svg.querySelector(`[data-scene-id='${id}']`);
        if (!visibleItem(item)) return;
        confirmCapturedIntent(item.dataset.sceneSelectionKind, id, revision, true);
        if (cancelledArms[category(item.dataset.sceneSelectionKind)]) return;
        if (explicitIntent?.selectionPending && (explicitIntent.kind !== item.dataset.sceneSelectionKind || explicitIntent.selectionId !== id.toLowerCase().replaceAll("-", ""))) return;
        latestSelectionRevision=revision;
        if (explicitIntent) explicitIntent.selectionPending=false;
        if (selected === item) return;
        cancelGesture(); clearFailure(); nudgeEpoch++; nudgeGeometry=null; selected?.classList.remove("selected"); selected=item; selected.classList.add("selected"); syncAttention();
    };
    svg.__v2SceneViewport = { key:viewKey, isLive:()=>active, reconcile, arm, clearPlacement, clearSelection, select:selectItem, dispose:() => { active=false; clearFailure(); nudgeEpoch++; nudgeGeometry=null; cancelGesture(); clearMarkerVisuals(); selected?.classList.remove("selected"); attentionRoot?.[v2ChildInteractionOwnerKey]?.retireSceneAttention?.(svg); svg.removeEventListener("wheel", wheel); svg.removeEventListener("pointerdown", start); svg.removeEventListener("pointermove", move); svg.removeEventListener("pointerup", select); svg.removeEventListener("pointerup", end); svg.removeEventListener("pointercancel", cancelDrag); svg.removeEventListener("keydown", cancelPlacement); svg.removeEventListener("keydown", nudge); svg.removeEventListener("contextmenu", cancelPlacement); for (const { node, click } of layerControls) node.removeEventListener("click", click); layerControls=[]; imageToggle?.removeEventListener("click", toggleImage); zoneToggle?.removeEventListener("click", toggleZone); reset?.removeEventListener("click", resetView); retryButton?.removeEventListener("click", retryMarker); } };
    svg.__v2SceneViewport.ownsOwner = value => ownsCurrentGeneration() && currentOwnerGeneration === value;
    svg.__v2SceneViewport.isKindVisible = visibleKind;
    // The existing root child click owner forwards only actual explicit row/re-arm
    // activations. Reveal is synchronous with that browser intent, never a delayed
    // render callback that could undo a subsequent hide. Reconciliation cannot
    // release a cancelled arm; a newer matching admitted parameter can.
    svg.__v2SceneViewport.explicitLayerIntent = (kind, id, selectionId) => {
        if (!ownsCurrentGeneration() || Number(svg.dataset.sceneOwnerGeneration) !== currentOwnerGeneration || !svg.isConnected || !(category(kind) in layers) || !id) return;
        capturedIntent = { kind, id: id.toLowerCase().replaceAll("-", ""), selectionId: (selectionId || id).toLowerCase().replaceAll("-", ""), selectionPending: true, focusConsumed: false, armRevision: latestArmRevision, selectionRevision: latestSelectionRevision };
        setLayer(kind, true);
    };
    bindLayerControls(); rebuildRelationships(); applyLayers(); showImage(true);
    // Resolve before making the host visible so reload/navigation never paint
    // the opposite split state. A missing/invalid stored value defaults shown.
    try { zoneShown = window.loadRoomZoneMapVisibility?.() !== false; } catch { zoneShown = true; }
    setZoneVisible(zoneShown); resetView();
    svg.closest(".room-editor-v2")?.[v2ChildInteractionOwnerKey]?.reconcileAttention?.();
};
window.disposeV2SceneViewport = (svg, ownerGeneration) => {
    const state = svg?.__v2SceneViewport;
    if (ownerGeneration !== undefined && !state?.ownsOwner?.(ownerGeneration)) return;
    state?.dispose(); if (svg) delete svg.__v2SceneViewport;
};
window.armV2ScenePlacement = (svg, kind, entityId, acquireFocus, ownerGeneration, revision) => svg?.__v2SceneViewport?.arm(kind, entityId, acquireFocus, ownerGeneration, revision);
window.clearV2ScenePlacement = (svg, ownerGeneration, revision) => svg?.__v2SceneViewport?.clearPlacement(ownerGeneration, revision);
window.selectV2SceneLayoutItem = (svg, entityId, ownerGeneration, revision) => svg?.__v2SceneViewport?.select(entityId, ownerGeneration, revision);
window.clearV2SceneLayoutSelection = (svg, ownerGeneration, revision) => svg?.__v2SceneViewport?.clearSelection(ownerGeneration, revision);

/* Separate modal-local V2 owner. Pointer edits remain browser-local; all numeric
   bounds, mapping and mode reconcile together at field/reset/Apply boundaries. */
window.initializeV2SceneImageCapturePreview = (svg, minX, minY, maxX, maxY, scaleX, scaleY, pixelOffsetX, pixelOffsetY, useSceneImage, annotationToggle,
    interpretation, resolution, customTarget, preparationSummary, initial) => {
    if (!(svg instanceof SVGElement)) return;
    svg.__v2SceneImageCapturePreview?.dispose();
    const base = String(svg.dataset.baseViewbox).split(" ").map(Number);
    if (base.length !== 4 || !base.every(Number.isFinite)) return;
    let down = null, active = true, disabled = false, frozen = false, annotationsShown = true;
    // Canonical offsets remain oriented INPUT pixels. Only their displayed native
    // fields are retained pixels; reduction changes never move the source.
    let inputOffsetX = initial.pixelOffsetX, inputOffsetY = initial.pixelOffsetY, ratios = null;
    const pending = () => svg.dataset.pendingUpload === "true" && useSceneImage.checked;
    const native = () => pending() && interpretation.value === "native";
    const alignment = () => useSceneImage.checked && !native();
    const showAnnotations = () => {
        if (!active) return;
        for (const node of svg.querySelectorAll("[data-v2-scene-capture-frame=true],[data-v2-scene-capture-marker]")) node.style.display = annotationsShown ? "" : "none";
        const label = annotationsShown ? "Hide annotations" : "Show annotations";
        annotationToggle?.setAttribute("aria-pressed", String(annotationsShown));
        annotationToggle?.setAttribute("aria-label", label);
        annotationToggle?.setAttribute("title", label);
    };
    const toggleAnnotations = () => { if (!active || !svg.isConnected) return; annotationsShown = !annotationsShown; showAnnotations(); };
    const point = (x, y) => { const value = svg.createSVGPoint(); value.x = x; value.y = y; return value.matrixTransform(svg.getScreenCTM().inverse()); };
    const value = input => { const text = input?.value; return text == null || text.trim() === "" ? NaN : Number(text); };
    const number = text => String(text ?? "").trim() === "" ? NaN : Number(text);
    const roundEven = n => { const floor = Math.floor(n), fraction = n - floor; return fraction === .5 ? (floor % 2 === 0 ? floor : floor + 1) : Math.round(n); };
    const preparation = () => {
        if (!pending()) { ratios = { x:1, y:1 }; return; }
        const l=value(minX), b=value(minY), r=value(maxX), t=value(maxY), sx=value(scaleX), sy=value(scaleY);
        const validBounds=[l,b,r,t,r-l,t-b].every(Number.isFinite) && r>l && t>b;
        const longest=Math.max(r-l,t-b), recommended=validBounds ? (longest<=40?64:longest<=80?32:longest<=160?16:8) : null;
        const target=resolution.value==="recommended" ? recommended : resolution.value==="custom" ? value(customTarget) : Number(resolution.value);
        const w=Number(svg.dataset.sourceWidth), h=Number(svg.dataset.sourceHeight);
        ratios=null;
        if (resolution.value==="recommended" && recommended===null) { preparationSummary.textContent="Set scene bounds to calculate recommended resolution."; return; }
        if (!(sx>0 && sy>0 && [sx,sy,w,h].every(Number.isFinite) && w>0 && h>0) || resolution.value!=="keep" && !(target>0 && Number.isFinite(target))) {
            preparationSummary.textContent="Enter positive finite input calibration and target pixels/unit."; return;
        }
        const factor=resolution.value==="keep" ? 1 : Math.min(1,target/sx,target/sy);
        const width=Math.max(1,roundEven(w*factor)), height=Math.max(1,roundEven(h*factor));
        ratios={x:width/w,y:height/h};
        const format=n=>Number(n.toPrecision(8)).toString();
        preparationSummary.textContent=`${w} × ${h} input → ${width} × ${height} retained; ${format(sx*ratios.x)} / ${format(sy*ratios.y)} retained pixels/unit (X/Y).` +
            (resolution.value==="recommended" ? ` Recommended target: ${recommended} pixels/unit.` : "") +
            (width>16383||height>16383 ? " Exceeds WebP's 16383-pixel axis limit; choose a lower resolution." : " Lossless WebP.");
    };
    // Do not dispatch bound input events here: a wheel/pointer movement is not a
    // draft edit or server event. Apply reads these local displayed values once.
    // Keep the reference image itself aligned with browser-local calibration
    // values. This is direct SVG feedback, not a bound input/.NET callback.
    const updateSource = () => {
        if (!active) return;
        preparation();
        const source = svg.querySelector("[data-v2-scene-capture-source=true]"), reference = svg.querySelector("[data-v2-scene-capture-reference=true]");
        const l = value(minX), b = value(minY), r = value(maxX), t = value(maxY), sx = value(scaleX), sy = value(scaleY), px = number(inputOffsetX), py = number(inputOffsetY), imageEnabled = !!useSceneImage.checked;
        const rect = (node, x, y, w, h, shown) => { if (!node) return; node.style.display = shown ? "" : "none"; if (shown) { node.setAttribute("x", x); node.setAttribute("y", y); node.setAttribute("width", w); node.setAttribute("height", h); } };
        rect(reference, Math.min(0,r), -Math.max(0,t), Math.abs(r), Math.abs(t), Number.isFinite(r) && Number.isFinite(t));
        const sourceWidth = Number(svg.dataset.sourceWidth), sourceHeight = Number(svg.dataset.sourceHeight);
        const imageWidth = sourceWidth / sx, imageHeight = sourceHeight / sy;
        const imageX = -px / sx, imageY = (py - sourceHeight) / sy;
        rect(source, imageX, imageY, imageWidth, imageHeight, imageEnabled && sx > 0 && sy > 0 && [px,py,imageWidth,imageHeight,imageX,imageY].every(Number.isFinite));
        for (const input of [minX,minY,maxX,maxY]) input.disabled=disabled||!imageEnabled;
        for (const input of [scaleX,scaleY]) input.disabled=disabled||!alignment();
        pixelOffsetX.disabled = disabled || !alignment() || !ratios; pixelOffsetY.disabled = disabled || !alignment() || !ratios;
        pixelOffsetX.value = ratios && Number.isFinite(px) ? String(px*ratios.x) : "";
        pixelOffsetY.value = ratios && Number.isFinite(py) ? String(py*ratios.y) : "";
        interpretation.disabled=disabled||!pending(); resolution.disabled=disabled||!pending();
        customTarget.disabled=disabled||!pending()||resolution.value!=="custom";
        for (const axis of ["x", "y"]) for (const node of svg.querySelectorAll(`[data-v2-scene-minimum='${axis}'],[data-v2-scene-minimum-hit='${axis}']`)) {
            const at = axis === "x" ? l : -b;
            const maximum = axis === "x" ? t : r;
            node.style.display = imageEnabled && Number.isFinite(at) && Number.isFinite(maximum) ? "" : "none"; node.style.pointerEvents = disabled || !imageEnabled ? "none" : "stroke";
            if (!Number.isFinite(at) || !Number.isFinite(maximum)) continue;
            node.setAttribute("x1", axis === "x" ? at : 0); node.setAttribute("x2", axis === "x" ? at : maximum);
            node.setAttribute("y1", axis === "y" ? at : 0); node.setAttribute("y2", axis === "y" ? at : -maximum);
        }
    };
    const change = (input, next) => { if (!active || !input || !Number.isFinite(next)) return; input.value = String(next); updateSource(); };
    const wheel = event => {
        if (!active) return; event.preventDefault();
        if (frozen) return;
        if (event.ctrlKey) {
            if (disabled || !alignment()) return;
            const pixels = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? svg.clientHeight : 1);
            const x = value(scaleX), y = value(scaleY), factor = Math.exp(Math.max(-.06, Math.min(.06, -pixels * .003)));
            if (x > 0 && y > 0 && Number.isFinite(x*factor) && Number.isFinite(y*factor)) { change(scaleX, x * factor); change(scaleY, y * factor); }
            return;
        }
        const view = String(svg.getAttribute("viewBox")).split(" ").map(Number), at = point(event.clientX, event.clientY), factor = Math.exp(Math.max(-.06, Math.min(.06, event.deltaY * .003)));
        view[2] *= factor; view[3] *= factor; view[0] = at.x - (at.x - view[0]) * factor; view[1] = at.y - (at.y - view[1]) * factor; svg.setAttribute("viewBox", view.join(" ")); updateSource();
    };
    const start = event => {
        if (!active || frozen || event.button !== 0) return;
        const minimum = event.target?.getAttribute?.("data-v2-scene-minimum-hit") || event.target?.getAttribute?.("data-v2-scene-minimum");
        if (!minimum && event.ctrlKey && event.shiftKey) return;
        const mode = minimum ? `minimum-${minimum}` : event.ctrlKey ? "pan-image" : "inspect";
        if (disabled && mode !== "inspect" || mode === "pan-image" && !alignment() || minimum && !useSceneImage.checked) return;
        event.preventDefault(); down = { id:event.pointerId, x:event.clientX, y:event.clientY, mode }; svg.setPointerCapture(event.pointerId);
    };
    const move = event => {
        if (!active || frozen || !down || event.pointerId !== down.id) return;
        const previous = point(down.x, down.y), current = point(event.clientX, event.clientY);
        if (down.mode === "minimum-x") change(minX, Math.round(current.x));
        else if (down.mode === "minimum-y") change(minY, Math.round(-current.y));
        else if (down.mode === "pan-image") {
            if (!alignment()) { down=null; return; }
            const x=number(inputOffsetX)-(current.x-previous.x)*value(scaleX), y=number(inputOffsetY)+(current.y-previous.y)*value(scaleY);
            if (Number.isFinite(x)&&Number.isFinite(y)) { inputOffsetX=String(x); inputOffsetY=String(y); updateSource(); }
        }
        else { const view = String(svg.getAttribute("viewBox")).split(" ").map(Number); view[0] -= current.x - previous.x; view[1] -= current.y - previous.y; svg.setAttribute("viewBox", view.join(" ")); updateSource(); }
        down.x = event.clientX; down.y = event.clientY;
    };
    const end = event => { if (down?.id === event.pointerId) { if (svg.hasPointerCapture(event.pointerId)) svg.releasePointerCapture(event.pointerId); down = null; } };
    const numeric = event => {
        if (!active || disabled || frozen) return;
        const input=event.currentTarget;
        if (input===pixelOffsetX && ratios && alignment()) inputOffsetX=Number.isFinite(value(input)) ? String(value(input)/ratios.x) : input.value;
        if (input===pixelOffsetY && ratios && alignment()) inputOffsetY=Number.isFinite(value(input)) ? String(value(input)/ratios.y) : input.value;
        updateSource();
    };
    const prepareChoice = () => {
        if (!active || disabled || frozen || !pending()) return;
        if (native()) { scaleX.value="64"; scaleY.value="64"; inputOffsetX="0"; inputOffsetY="0"; }
        if (down) { if(svg.hasPointerCapture(down.id)) svg.releasePointerCapture(down.id); down=null; }
        updateSource();
    };
    const numericInputs=[minX,minY,maxX,maxY,scaleX,scaleY,pixelOffsetX,pixelOffsetY,customTarget];
    for (const input of numericInputs) input.addEventListener("input",numeric);
    interpretation.addEventListener("change",prepareChoice); resolution.addEventListener("change",prepareChoice);
    svg.addEventListener("wheel", wheel, { passive:false }); svg.addEventListener("pointerdown", start); svg.addEventListener("pointermove", move); svg.addEventListener("pointerup", end); svg.addEventListener("pointercancel", end);
    annotationToggle?.addEventListener("click", toggleAnnotations);
    updateSource(); showAnnotations();
    svg.__v2SceneImageCapturePreview = {
        reconcile: (nextDisabled, nextToggle, nextFrozen) => {
            if (!active) return;
            if (annotationToggle !== nextToggle) {
                annotationToggle?.removeEventListener("click", toggleAnnotations);
                annotationToggle = nextToggle;
                annotationToggle?.addEventListener("click", toggleAnnotations);
            }
            frozen = !!nextFrozen;
            disabled = !!nextDisabled;
            if (down && (frozen || disabled && down.mode !== "inspect")) { if (svg.hasPointerCapture(down.id)) svg.releasePointerCapture(down.id); down = null; }
            if (annotationToggle) annotationToggle.disabled = disabled;
            updateSource(); showAnnotations();
        },
        reset: next => {
            if (!active) return;
            for (const [input,key] of [[minX,"minX"],[minY,"minY"],[maxX,"maxX"],[maxY,"maxY"],[scaleX,"pixelsPerUnitX"],[scaleY,"pixelsPerUnitY"]]) input.value = String(next[key]);
            inputOffsetX=next.pixelOffsetX; inputOffsetY=next.pixelOffsetY;
            interpretation.value=next.nativeUpload ? "native" : "advanced"; resolution.value=next.uploadResolution; customTarget.value=next.uploadCustomTarget;
            useSceneImage.checked = next.useSceneImage;
            updateSource();
        },
        values: () => active ? { minX:minX.value, minY:minY.value, maxX:maxX.value, maxY:maxY.value, pixelsPerUnitX:scaleX.value, pixelsPerUnitY:scaleY.value, pixelOffsetX:String(inputOffsetX), pixelOffsetY:String(inputOffsetY), useSceneImage:useSceneImage.checked,
            nativeUpload:interpretation.value==="native", uploadResolution:resolution.value, uploadCustomTarget:customTarget.value } : null,
        dispose: () => { active = false; down = null; for(const input of numericInputs) input.removeEventListener("input",numeric); interpretation.removeEventListener("change",prepareChoice); resolution.removeEventListener("change",prepareChoice); annotationToggle?.removeEventListener("click", toggleAnnotations); svg.removeEventListener("wheel", wheel); svg.removeEventListener("pointerdown", start); svg.removeEventListener("pointermove", move); svg.removeEventListener("pointerup", end); svg.removeEventListener("pointercancel", end); }
    };
};
window.reconcileV2SceneImageCapturePreview = (svg, disabled, annotationToggle, frozen = false) => svg?.__v2SceneImageCapturePreview?.reconcile?.(disabled, annotationToggle, frozen);
window.resetV2SceneImageCapturePreview = (svg, values) => svg?.__v2SceneImageCapturePreview?.reset?.(values);
window.readV2SceneImageCapturePreview = svg => {
    const state = svg?.__v2SceneImageCapturePreview;
    return state?.values?.() ?? null;
};
window.disposeV2SceneImageCapturePreview = svg => { svg?.__v2SceneImageCapturePreview?.dispose(); if (svg) delete svg.__v2SceneImageCapturePreview; };

// One dialog-local numeric context, independent of preview availability and draft
// values. Direct element references avoid document scanning or global focus policy.
window.createV2SceneCaptureFocusOwner = (fields, ...inputs) => {
    const dialog = fields?.closest?.("[role=dialog]");
    if (!(fields instanceof HTMLElement) || !(dialog instanceof HTMLElement) || !fields.isConnected || !dialog.isConnected) return null;
    fields.__v2SceneCaptureFocusOwner?.dispose();
    let active = true, lastNumeric = null;
    const usable = input => input instanceof HTMLInputElement && input.type === "number" && input.isConnected && fields.contains(input) && !input.disabled && !input.closest("[hidden]");
    lastNumeric = inputs.find(input => input === document.activeElement && usable(input)) || null;
    const remember = event => { if (active && usable(event.currentTarget)) lastNumeric = event.currentTarget; };
    inputs.forEach(input => input?.addEventListener("focus", remember));
    const owner = {
        restore: () => {
            if (!active || fields.__v2SceneCaptureFocusOwner !== owner || !fields.isConnected || !dialog.isConnected || !dialog.contains(fields) || dialog.dataset.v2ModalStage !== "ProposalOpen") return;
            // Committing temporarily disables every numeric control; do not clear
            // the context then. Hidden Native/None fields remain ineligible at restoration.
            if (usable(lastNumeric)) lastNumeric.focus(); else dialog.focus();
        },
        dispose: () => {
            active = false; lastNumeric = null;
            inputs.forEach(input => input?.removeEventListener("focus", remember));
            if (fields.__v2SceneCaptureFocusOwner === owner) delete fields.__v2SceneCaptureFocusOwner;
        }
    };
    fields.__v2SceneCaptureFocusOwner = owner;
    return owner;
};

window.focusAndSelectById = (id) => {
    requestAnimationFrame(() => window.focusAndSelect(document.getElementById(id)));
};

window.getElementSize = (element) => {
    const rectangle = element.getBoundingClientRect();
    return { width: rectangle.width, height: rectangle.height };
};

const sidebarSessionStateKey = "silksong-rando-logic.sidebar-state";
let sidebarScrollTimer = null;

document.addEventListener("scroll", (event) => {
    const sidebar = event.target;
    if (!(sidebar instanceof HTMLElement) || !sidebar.classList.contains("sidebar")) {
        return;
    }

    sidebar.classList.add("sidebar-scrolling");
    clearTimeout(sidebarScrollTimer);
    sidebarScrollTimer = setTimeout(() => sidebar.classList.remove("sidebar-scrolling"), 700);
}, true);

window.loadSidebarSessionState = () => {
    const value = sessionStorage.getItem(sidebarSessionStateKey);
    if (!value) {
        return null;
    }

    try {
        return JSON.parse(value);
    } catch {
        sessionStorage.removeItem(sidebarSessionStateKey);
        return null;
    }
};

window.saveSidebarSessionState = (state) => {
    sessionStorage.setItem(sidebarSessionStateKey, JSON.stringify(state));
};

window.loadRoomMapContextVisibility = () => {
    const visible = sessionStorage.getItem(roomMapContextVisibilityKey) !== "false";
    setRoomMapContextVisibility(visible);
    return visible;
};

window.saveRoomMapContextVisibility = (visible) => {
    setRoomMapContextVisibility(visible);
    sessionStorage.setItem(roomMapContextVisibilityKey, String(visible));
};

/* One mounted V2 page owns its map-context height interaction. The owner keeps
   this browser-local presentation preference out of room state and commits only
   a completed pointer gesture. */
window.createV2MapContextHeightOwner = (root) => {
    const host = root?.querySelector?.("[data-v2-map-context-height-host=true]"), handle = host?.querySelector?.("[data-v2-map-context-height-handle=true]");
    if (!(host instanceof HTMLElement) || !(handle instanceof HTMLElement)) return { dispose() {} };
    let disposed = false, ratio = roomContextPreferences.ratioTerm, drag = null;
    const clamp = value => Math.min(roomMapContextRatioMaximum, Math.max(roomMapContextRatioMinimum, value));
    const apply = value => applyRoomMapContextRatio(value);
    const commit = value => { ratio = clamp(value); apply(ratio); localStorage.setItem(roomMapContextHeightKey, String(ratio)); };
    const ratioFor = (height, width) => clamp(16 * height / width);
    const move = event => {
        if (!drag || disposed || event.pointerId !== drag.pointerId) return;
        apply(ratioFor(drag.height + event.clientY - drag.y, drag.width));
    };
    const end = event => {
        if (!drag || disposed || event.pointerId !== drag.pointerId) return;
        const current = ratioFor(drag.height + event.clientY - drag.y, drag.width); drag = null; window.removeEventListener("pointermove", move); window.removeEventListener("pointerup", end); window.removeEventListener("pointercancel", cancel); commit(current);
    };
    const cancel = () => {
        if (!drag) return;
        drag = null; window.removeEventListener("pointermove", move); window.removeEventListener("pointerup", end); window.removeEventListener("pointercancel", cancel); apply(ratio);
    };
    const down = event => {
        if (disposed || event.button !== 0) return;
        const bounds = host.getBoundingClientRect();
        if (bounds.width <= 0) return;
        event.preventDefault(); drag = { pointerId: event.pointerId, y: event.clientY, height: bounds.height, width: bounds.width };
        window.addEventListener("pointermove", move); window.addEventListener("pointerup", end); window.addEventListener("pointercancel", cancel);
    };
    handle.addEventListener("pointerdown", down); apply(ratio);
    return {
        dispose: () => { if (disposed) return; disposed = true; cancel(); handle.removeEventListener("pointerdown", down); }
    };
};

// One modal-local owner: resize/hover stay local; native search belongs to Blazor.
const sceneReviewResizeOwnerKey = Symbol("scene-review-preview-resize-owner");
window.createSceneReviewPreviewResizeOwner = (host, modal) => {
    const handle = host?.querySelector?.("[data-scene-preview-resize-edge]");
    if (!(host instanceof HTMLElement) || !(handle instanceof HTMLElement) || !(modal instanceof HTMLElement) ||
        !modal.isConnected || !modal.contains(host)) throw new Error("Scene Review preview is not mounted.");
    modal[sceneReviewResizeOwnerKey]?.dispose();
    let disposed = false, ratio = 5, drag = null, pointer = null;
    const live = () => !disposed && modal.isConnected && modal[sceneReviewResizeOwnerKey] === owner;
    const overlay = () => host.querySelector("[data-scene-preview-hover]");
    const clearHover = () => { const circle = overlay(); if (circle) circle.style.display = "none"; };
    const rowAt = target => target instanceof Element ? target.closest("[data-scene-hover-id]") : null;
    const reconcile = () => {
        if (!live()) return;
        const circle = overlay();
        const controls = modal.querySelector(".scene-workbench-controls");
        if (!pointer || drag || host.hidden || controls?.disabled) { clearHover(); return; }
        // Hit-test the current rendered row, not a retained/recycled node identity.
        // The document supplies a point lookup only; all event/query ownership is local.
        const row = rowAt(modal.ownerDocument.elementFromPoint(pointer.x, pointer.y));
        if (!circle || !row || !modal.contains(row) || row.closest("[hidden]") || !row.getClientRects().length) { clearHover(); return; }
        const xText = row.dataset.sceneHoverX, yText = row.dataset.sceneHoverY;
        const x = Number(xText), y = Number(yText), radius = Number(circle.dataset.sceneHoverRadius);
        if (!row.dataset.sceneHoverId || !xText || !yText || !Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(radius) || radius <= 0) { clearHover(); return; }
        for (const [name, value] of [["cx", x], ["cy", y], ["r", radius]]) {
            const text = String(value);
            if (circle.getAttribute(name) !== text) circle.setAttribute(name, text);
        }
        circle.style.display = "";
    };
    const hoverMove = event => {
        if (!live() || !event.isPrimary) return;
        pointer = { x: event.clientX, y: event.clientY };
        reconcile();
    };
    const hoverOut = event => {
        if (!live() || !event.isPrimary) return;
        // Descendant-to-descendant transitions within one row keep its decoration.
        if (rowAt(event.target) && rowAt(event.target) === rowAt(event.relatedTarget)) return;
        pointer = event.relatedTarget instanceof Node && modal.contains(event.relatedTarget) ? { x: event.clientX, y: event.clientY } : null;
        reconcile();
    };
    const hoverLeave = () => { pointer = null; clearHover(); };
    // Virtualize renders independently of the parent OnAfterRender. Observe only
    // this modal's structural/fact changes, never the attributes we decorate.
    const hoverObserver = new MutationObserver(reconcile);
    const apply = value => host.style.setProperty("--scene-review-ratio-term", String(value));
    const clamp = value => Math.min(9, Math.max(5, value));
    const release = pointerId => {
        if (handle.hasPointerCapture(pointerId)) handle.releasePointerCapture(pointerId);
        window.removeEventListener("keydown", escape);
    };
    const cancel = event => {
        if (!drag || (event?.pointerId !== undefined && event.pointerId !== drag.pointerId)) return;
        const previous = drag; drag = null;
        apply(ratio); release(previous.pointerId);
    };
    const escape = event => { if (event.key === "Escape" && drag) { event.preventDefault(); cancel(); } };
    const ratioFor = event => clamp(16 * (drag.height + event.clientY - drag.y) / drag.width);
    const move = event => {
        if (disposed || !drag || event.pointerId !== drag.pointerId) return;
        apply(ratioFor(event));
    };
    const end = event => {
        if (disposed || !drag || event.pointerId !== drag.pointerId) return;
        ratio = ratioFor(event);
        const previous = drag; drag = null;
        apply(ratio); release(previous.pointerId);
    };
    const down = event => {
        if (!live() || drag || event.button !== 0 || !event.isPrimary || modal.querySelector(".scene-workbench-controls")?.disabled || host.hidden) return;
        const bounds = host.getBoundingClientRect();
        if (bounds.width <= 0) return;
        event.preventDefault();
        handle.setPointerCapture(event.pointerId);
        drag = { pointerId: event.pointerId, y: event.clientY, height: bounds.height, width: bounds.width };
        hoverLeave();
        window.addEventListener("keydown", escape);
    };
    handle.addEventListener("pointerdown", down);
    handle.addEventListener("pointermove", move);
    handle.addEventListener("pointerup", end);
    handle.addEventListener("pointercancel", cancel);
    handle.addEventListener("lostpointercapture", cancel);
    apply(5);
    const owner = {
        reconcile,
        dispose() {
            if (disposed) return;
            try { cancel(); }
            finally {
                disposed = true;
                window.removeEventListener("keydown", escape);
                hoverLeave();
                hoverObserver.disconnect();
                modal.removeEventListener("pointerover", hoverMove);
                modal.removeEventListener("pointermove", hoverMove);
                modal.removeEventListener("pointerout", hoverOut);
                modal.removeEventListener("pointerleave", hoverLeave);
                modal.removeEventListener("pointercancel", hoverLeave);
                modal.removeEventListener("scroll", reconcile, true);
                handle.removeEventListener("pointerdown", down);
                handle.removeEventListener("pointermove", move);
                handle.removeEventListener("pointerup", end);
                handle.removeEventListener("pointercancel", cancel);
                handle.removeEventListener("lostpointercapture", cancel);
                if (modal[sceneReviewResizeOwnerKey] === owner) delete modal[sceneReviewResizeOwnerKey];
            }
        }
    };
    modal[sceneReviewResizeOwnerKey] = owner;
    modal.addEventListener("pointerover", hoverMove);
    modal.addEventListener("pointermove", hoverMove);
    modal.addEventListener("pointerout", hoverOut);
    modal.addEventListener("pointerleave", hoverLeave);
    modal.addEventListener("pointercancel", hoverLeave);
    modal.addEventListener("scroll", reconcile, true);
    hoverObserver.observe(modal, { childList: true, subtree: true, attributes: true,
        attributeFilter: ["hidden", "disabled", "data-scene-hover-id", "data-scene-hover-x", "data-scene-hover-y", "data-scene-hover-radius"] });
    return owner;
};

window.loadRoomZoneMapVisibility = () => {
    const visible = sessionStorage.getItem(roomZoneMapVisibilityKey) !== "false";
    setRoomZoneMapVisibility(visible);
    return visible;
};

window.saveRoomZoneMapVisibility = (visible) => {
    setRoomZoneMapVisibility(visible);
    sessionStorage.setItem(roomZoneMapVisibilityKey, String(visible));
};

let sceneDropInputId = null;

window.openSceneUploadPicker = (inputId) => {
    const input = document.getElementById(inputId);
    if (input instanceof HTMLInputElement && !input.disabled) {
        input.click();
    }
};

window.registerSceneDropTarget = (inputId) => {
    sceneDropInputId = inputId;
};

document.addEventListener("dragover", (event) => {
    if (event.dataTransfer?.types.includes("Files")) {
        event.preventDefault();
    }
});

document.addEventListener("drop", (event) => {
    if (!event.dataTransfer?.files?.length) {
        return;
    }

    event.preventDefault();
    const input = sceneDropInputId ? document.getElementById(sceneDropInputId) : null;
    if (!(input instanceof HTMLInputElement) || input.disabled) {
        return;
    }

    const transfer = new DataTransfer();
    transfer.items.add(event.dataTransfer.files[0]);
    input.files = transfer.files;
    input.dispatchEvent(new Event("change", { bubbles: true }));
});

// Editor navigation supplies direct stable field IDs. It never infers a row
// from DOM order, entity IDs, headings, anchors, or generated replacement IDs.
window.focusEditorField = (id) => {
    const field = document.getElementById(id);
    if (!(field instanceof HTMLElement)) {
        return;
    }

    field.closest(".room-editor-v2")?.[v2ChildInteractionOwnerKey]?.beforeFocus?.();
    field.focus();
    if (field instanceof HTMLInputElement || field instanceof HTMLTextAreaElement) {
        field.select();
    }
};
window.focusV2ModalDialog = (id) => {
    const dialog = document.getElementById(id);
    if (dialog instanceof HTMLElement) dialog.focus();
};
const caretAtBoundary = (editor, key) => {
    if (editor instanceof HTMLSelectElement || editor.type === "checkbox") {
        return true;
    }

    const start = editor.selectionStart ?? 0;
    const end = editor.selectionEnd ?? 0;
    if (key === "ArrowLeft") return start === 0 && end === 0;
    if (key === "ArrowRight") return start === editor.value.length && end === editor.value.length;
    if (!(editor instanceof HTMLTextAreaElement)) return true;

    // Textarea up/down remains native until the caret reaches its first/last
    // visual line. Measure its own text/wrapping; navigation never discovers
    // another control or row from the DOM.
    const style = getComputedStyle(editor);
    const horizontalInsets = (Number.parseFloat(style.paddingLeft) || 0) + (Number.parseFloat(style.paddingRight) || 0) + (Number.parseFloat(style.borderLeftWidth) || 0) + (Number.parseFloat(style.borderRightWidth) || 0);
    const availableWidth = Math.max(1, editor.clientWidth - horizontalInsets);
    const canvas = document.createElement("canvas");
    const context = canvas.getContext("2d");
    if (!context) return false;
    context.font = style.font;
    const visualLines = (text) => {
        let lines = 1;
        let width = 0;
        for (const character of text) {
            if (character === "\n") { lines++; width = 0; continue; }
            const characterWidth = context.measureText(character).width;
            if (width && width + characterWidth > availableWidth) { lines++; width = characterWidth; }
            else width += characterWidth;
        }
        return lines;
    };
    const beforeLines = visualLines(editor.value.slice(0, start));
    const afterLines = visualLines(editor.value.slice(start));
    return key === "ArrowUp" ? beforeLines === 1 : afterLines === 1;
};

/* A mounted V2 page creates one root-local shared child owner for table
   dispatch, focus, drag feedback, and transient Requirements presentation. */
const v2ChildInteractionOwnerKey = Symbol("v2-child-interaction-owner");
window.createV2ChildInteractionOwner = (root, initialRequirementsContext) => {
      const existing = root[v2ChildInteractionOwnerKey];
      if(existing?.isLive?.())throw new Error("A V2 child interaction owner is already installed for this page root.");
      existing?.dispose?.();
        const owners = {}, configs = {}, tabSources = new WeakSet(), feedback = new Map(); let drag = null, disposed = false, requirementsContext = initialRequirementsContext ?? { rooms: [], checks: [], predicates: [], items: [], countables: [], souls: [], crests: [], methods: [] }, activeRequirement = null, suppressedRequirementBlur = null, closingRequirement = false, pointerRequirementCaret = null;
     const config = kind => configs[kind];
     const control = x => x instanceof HTMLInputElement || x instanceof HTMLTextAreaElement || x instanceof HTMLSelectElement;
      const requirementEditor = x => x instanceof HTMLTextAreaElement && x.matches("[data-v2-requirements-editor]");
      const expandedRequirementEditor = x => x instanceof HTMLTextAreaElement && x.matches("[data-v2-requirements-expanded-editor]");
       const identify = text => String(text).toLowerCase().replace(/[^\p{L}\p{Nd}\s]/gu, "").replace(/\s+/g, " ").trim();
       const addRequirementLookup = (lookup, key, value) => { const matches=lookup.get(key);if(matches)matches.push(value);else lookup.set(key,[value]); };
        // Keep every alias hit for Requirements recognition. Only Name coloring
        // consumes this distinct-definition summary on the same compiled bucket.
        const addFriendlyNameAliasLookup = (lookup, key, definition) => {
            addRequirementLookup(lookup,key,definition);
            const matches=lookup.get(key);
            matches.uniqueDefinition=matches.length===1?definition:matches.uniqueDefinition===definition?definition:null;
        };
         const compileRequirementsLookups = context => {
               const roomsByReference=new Map(),checksByName=new Map(),checksByRoom=new Map(),itemAliases=new Map(),countableAliases=new Map(),soulAliases=new Map(),crestAliases=new Map(),methodAliases=new Map(),predicateAliasesByFirstToken=new Map(),predicateAliases=[];
             for(const room of context.rooms||[])addRequirementLookup(roomsByReference,identify(room.referenceId),room);
             for(const check of context.checks||[]){const name=identify(check.friendlyName);addRequirementLookup(checksByName,name,check);let roomChecks=checksByRoom.get(check.roomId);if(!roomChecks){roomChecks=new Map();checksByRoom.set(check.roomId,roomChecks);}addRequirementLookup(roomChecks,name,check);}
               for(const [rows,map] of [[context.items,itemAliases],[context.countables,countableAliases],[context.souls,soulAliases],[context.crests,crestAliases],[context.methods,methodAliases]])
                 for(const row of rows||[])for(const alias of row.aliases||[])addFriendlyNameAliasLookup(map,identify(alias),row);
             for(const predicate of context.predicates||[])for(const alias of predicate.aliases||[]){const normalized=identify(alias),tokens=normalized?normalized.split(" "):[];if(tokens.length){const candidate={predicate,tokens,normalizedAlias:normalized};addRequirementLookup(predicateAliasesByFirstToken,tokens[0],candidate);predicateAliases.push(candidate);}}
               return {roomsByReference,checksByName,checksByRoom,itemAliases,countableAliases,soulAliases,crestAliases,methodAliases,predicateAliasesByFirstToken,predicateAliases};
         };
       let requirementsLookups=compileRequirementsLookups(requirementsContext);
        const friendlyNameEditor = field => field instanceof HTMLTextAreaElement && field.matches("[data-v2-friendly-name-space]");
        const renderFriendlyName = field => {
            if(disposed||!friendlyNameEditor(field))return;
            const identity=identify(field.value), space=field.dataset.v2FriendlyNameSpace;
            let role=null;
              const namespaces=space==="enemies"?[[requirementsLookups.soulAliases,"soul"]]:space==="checks"?[[requirementsLookups.itemAliases,"item"],[requirementsLookups.countableAliases,"countable"]]:[];
             if(identity&&namespaces.length){
                 const probe=phrase=>namespaces.map(([lookup])=>lookup.get(phrase));
                 const exact=probe(identity), maximal=[];
                 // Any exact bucket is final, even when its definitions are ambiguous.
                 if(exact.some(Boolean))maximal.push(exact);
                 else{
                     const words=identity.split(" ");
                     let furthestEnd=0;
                     // Earlier starts and descending ends let a single frontier reject
                     // strict containment, without suppressing overlaps or separate hits.
                     for(let start=0;start<words.length;start++){
                         for(let end=words.length;end>start;end--){
                             const buckets=probe(words.slice(start,end).join(" "));
                             if(!buckets.some(Boolean))continue;
                             if(end>furthestEnd){maximal.push(buckets);furthestEnd=end;}
                             // Every shorter hit at this start is strictly contained.
                             break;
                         }
                     }
                 }
                 let definition=null, ambiguous=false;
                 for(const buckets of maximal)for(let index=0;index<buckets.length;index++){
                     const bucket=buckets[index];if(!bucket)continue;
                     const candidate=bucket.uniqueDefinition, candidateRole=namespaces[index][1];
                     if(!candidate||(definition&&(definition!==candidate||role!==candidateRole)))ambiguous=true;
                     else{definition=candidate;role=candidateRole;}
                 }
                 if(ambiguous)role=null;
             }
            if(role)field.dataset.v2FriendlyNameRole=role;
            else field.removeAttribute("data-v2-friendly-name-role");
        };
        const reconcileFriendlyNames = () => { if(!disposed)root.querySelectorAll("textarea[data-v2-friendly-name-space]").forEach(renderFriendlyName); };
      const setText = (node, text, cls = "") => { const span = document.createElement("span"); span.textContent = text; if (cls) span.className = cls; node.append(span); };
       const parseRequirementsV2 = (source, roomId) => {
         const spans=[], add=(text,cls="")=>spans.push([text,cls]); let danger=false, at=0;
         const mark=(text,cls="requirements-malformed")=>{danger=true;add(text,cls);};
          const codePointBefore=(text,index)=>{if(index<=0)return "";const trailing=text.charCodeAt(index-1),leading=index>1?text.charCodeAt(index-2):0,start=trailing>=0xDC00&&trailing<=0xDFFF&&leading>=0xD800&&leading<=0xDBFF?index-2:index-1;return text.slice(start,index);};
          const codePointAt=(text,index)=>index>=text.length?"":String.fromCodePoint(text.codePointAt(index));
          const identifierCodePoint=value=>/[\p{L}\p{Nd}]/u.test(value);
          const wordAt=(word,index)=>source.slice(index,index+word.length)===word&&!identifierCodePoint(codePointBefore(source,index))&&!identifierCodePoint(codePointAt(source,index+word.length));
                const syntax = { only:"{predicate}", optional:"{difficulty?} {predicate}", required:"{difficulty} {predicate}", direction:"{predicate} [{direction}]", optionalDirection:"{predicate} [{direction?}]", item:"{predicate} {item}", check:"{predicate} {check}", quantity:"{predicate} {quantity}", difficultySoul:"{difficulty} {predicate} [{soul}]", soul:"{predicate} [{soul}]", countable:"{predicate} {quantity} {countable}", countableFirst:"{predicate} {countable} {quantity}", crestDirection:"[{crest}] {predicate} {direction}", difficultyCrest:"{difficulty} [{crest}] {predicate}", optionalCrestDirection:"[{crest?}] {predicate} {direction}", difficultyOptionalCrest:"{difficulty} [{crest?}] {predicate}", difficultyMethod:"{difficulty} [{method}] {predicate}" };
            const quantityShapes = ["{predicate} {quantity?}", "{predicate} [{direction}] {quantity?}", "{difficulty} {predicate} {quantity?}", "{difficulty} {predicate} {quantity}", "{difficulty} [{method}] {predicate} {quantity?}", "{predicate} [{direction?}] {quantity?}"];
           const operandShapes=new Map([...Object.values(syntax).filter(pattern=>pattern.includes("[")),...quantityShapes].map(pattern=>[pattern,pattern.split(" ").map(marker=>({role:marker.replace(/[\[\]{}?]/gu,""),optional:marker.includes("?"),list:marker.startsWith("[")}))]));
            // Composed final Quantity is raw syntax: optional omission, N, or separate plain x/X N.
           // Keep the old normalized/unbounded quantity and Countable paths untouched.
           const composedQuantity = (text, part, optional) => {
               const raw=text.slice(part.start,part.end), tokens=[...raw.matchAll(/\S+/gu)];
               if(!tokens.length)return optional?{identity:"",roles:[]}:null;
               if(tokens.length!==1&&(tokens.length!==2||!/^[xX]$/u.test(tokens[0][0])))return null;
               const number=tokens.at(-1),token=number[0];
               if(!/^[1-9][0-9]*$/u.test(token)||Number(token)>2147483647)return null;
               const start=part.start+number.index;
               return {identity:raw.trim(),roles:[{start,end:start+token.length,className:"requirements-quantity"}]};
           };
           const difficulty = new Set(["easy","medium","hard","cbt","intended"]), direction = new Set(["left","right","up","down","both","either","any","all"]);
           const tokenizeAtom = text => { const chunks=[...text.matchAll(/\S+/gu)].map(match=>({start:match.index,end:match.index+match[0].length,identity:identify(match[0])})),meaningful=chunks.filter(chunk=>chunk.identity);return {text,chunks,meaningful}; };
              const matchingCandidates = tokens => { if(!tokens.length)return [];const matches=(requirementsLookups.predicateAliasesByFirstToken.get(tokens[0].identity)||[]).map(candidate=>({candidate,difficultyEnabled:false,invalidDifficulty:false}));if(tokens.length>1)for(const candidate of requirementsLookups.predicateAliasesByFirstToken.get(tokens[1].identity)||[]){const input=candidate.predicate.inputSyntax;if(difficulty.has(tokens[0].identity)&&(input===syntax.optional||input===syntax.required||input===syntax.difficultySoul))matches.push({candidate,difficultyEnabled:true,invalidDifficulty:false});else if(!difficulty.has(tokens[0].identity)&&(input===syntax.required||input===syntax.difficultySoul))matches.push({candidate,difficultyEnabled:false,invalidDifficulty:true});}return matches; };
           const matchedPrefix = (tokens,candidate,start) => { if(start+candidate.tokens.length>tokens.length)return null;for(let index=0;index<candidate.tokens.length;index++)if(tokens[start+index].identity!==candidate.tokens[index])return null;return {next:start+candidate.tokens.length,prefixEnd:tokens[start+candidate.tokens.length-1].end}; };
            const argumentStart = (text,prefixEnd) => prefixEnd+(text.slice(prefixEnd).length-text.slice(prefixEnd).trimStart().length);
          const scoped = (raw, prefixEnd) => { const rest=raw.slice(prefixEnd).trimStart(), words=[]; for(let index=0;index<rest.length;){const word=wordAtIn(rest,"IN",index)?"IN":wordAtIn(rest,"THE",index)?"THE":wordAtIn(rest,"GLOBAL",index)?"GLOBAL":null;if(word){words.push({word,index});index+=word.length;}else index++;} if(words.some(x=>x.word==="GLOBAL"))return { state:"unknown" }; if(words.length===0)return { state:"local", name:rest }; if(words.length!==1)return { state:"malformed" }; const qualifier=words[0]; if(qualifier.word==="THE"&&qualifier.index===0)return rest.slice(3).trim()?{state:"the",name:rest.slice(3).trimStart(),qualifier}:{state:"malformed"}; if(qualifier.word==="IN"&&qualifier.index>0&&rest.slice(qualifier.index+2).trim())return {state:"in",name:rest.slice(0,qualifier.index).trimEnd(),room:rest.slice(qualifier.index+2).trimStart(),qualifier}; return {state:"malformed"}; };
          const wordAtIn=(text,word,index)=>text.slice(index,index+word.length)===word&&!identifierCodePoint(codePointBefore(text,index))&&!identifierCodePoint(codePointAt(text,index+word.length));
          const validListStructure = text => {
              let opening=-1;
              for(let index=0;index<text.length;index++){
                  const character=text[index];
                  if(character==="["){if(opening>=0)return false;opening=index;}
                  else if(character==="]"){
                      if(opening<0)return false;
                      const entries=text.slice(opening+1,index).split(",");
                      if(entries.some(entry=>!entry.trim()))return false;
                      opening=-1;
                  }else if(character===","&&opening<0)return false;
                  else if(opening>=0&&(character==="("||character===")"||wordAtIn(text,"AND",index)||wordAtIn(text,"OR",index)))return false;
              }
              return opening<0;
          };
          const operandPartitions = (text,slots) => {
              const cuts=[];let bracket=false;
              for(let index=0;index<text.length;index++){
                  if(text[index]==="[")bracket=true;else if(text[index]==="]")bracket=false;
                  if(!bracket&&/\s/u.test(text[index])&&index>0&&!/\s/u.test(text[index-1]))cuts.push(index);
              }
              const result=[];
              const split=(start,slot,prefix)=>{
                  while(start<text.length&&/\s/u.test(text[start]))start++;
                  if(slot===slots.length-1){if(start<text.length||slots[slot].optional)result.push([...prefix,{start,end:text.length}]);return;}
                  for(const end of cuts)if(end>start)split(end,slot+1,[...prefix,{start,end}]);
                   // Punctuation-only raw suffix is not optional Direction/Quantity omission.
                  if(slot===slots.length-2&&slots.at(-1).optional&&start<text.length&&identify(text.match(/\S+$/u)?.[0]||""))result.push([...prefix,{start,end:text.length},{start:text.length,end:text.length}]);
              };
               split(0,0,[]);
               // Only the approved leading optional Crest may be absent; all present boundaries still compete.
                if(slots.length===3&&slots[0].role==="crest"&&slots[0].optional&&slots[0].list&&slots[1].role==="predicate"&&!slots[1].optional&&!slots[1].list&&slots[2].role==="direction"&&!slots[2].optional&&!slots[2].list)
                    split(0,1,[{start:0,end:0}]);
                // The fifteenth descriptor alone admits an empty middle Crest. Keep every
                // full difficulty/predicate boundary and exact raw ranges, without skipping text.
                if(slots.length===3&&slots[0].role==="difficulty"&&!slots[0].optional&&!slots[0].list&&slots[1].role==="crest"&&slots[1].optional&&slots[1].list&&slots[2].role==="predicate"&&!slots[2].optional&&!slots[2].list)
                    for(const end of cuts){
                        let start=end;while(start<text.length&&/\s/u.test(text[start]))start++;
                        if(start<text.length)result.push([{start:0,end},{start:end,end},{start,end:text.length}]);
                    }
                 // QTY2 alone admits both supporting slots absent and empty-middle Direction
                 // with present Quantity. Keep every complete boundary and actual source range.
                 if(slots.length===3&&slots[0].role==="predicate"&&!slots[0].optional&&!slots[0].list&&slots[1].role==="direction"&&slots[1].optional&&slots[1].list&&slots[2].role==="quantity"&&slots[2].optional&&!slots[2].list){
                     if(text.length&&identify(text.match(/\S+$/u)?.[0]||""))result.push([{start:0,end:text.length},{start:text.length,end:text.length},{start:text.length,end:text.length}]);
                     for(const end of cuts){
                         let start=end;while(start<text.length&&/\s/u.test(text[start]))start++;
                         if(start<text.length)result.push([{start:0,end},{start:end,end},{start,end:text.length}]);
                     }
                 }
                 return result;
          };
          const operandValues = (text,part,slot) => {
              const raw=text.slice(part.start,part.end);
              if(!raw)return slot.optional?[]:null;
              if(!/[\[\],]/u.test(raw))return [part];
              if(!slot.list||!raw.startsWith("[")||!raw.endsWith("]")||/[\[\]]/u.test(raw.slice(1,-1)))return null;
              const entries=[];let start=part.start+1;
              for(const entry of raw.slice(1,-1).split(",")){
                  const leading=entry.length-entry.trimStart().length,trailing=entry.length-entry.trimEnd().length;
                  entries.push({start:start+leading,end:start+entry.length-trailing});start+=entry.length+1;
              }
              return entries;
          };
          const operandCandidates = text => {
              const complete=[];let incomplete=false,malformed=false;
              for(const candidate of requirementsLookups.predicateAliases){
                  const slots=operandShapes.get(candidate.predicate.inputSyntax);if(!slots)continue;
                  // Per managed alias, not across definitions/aliases or formatted outputs.
                  const interpretations=new Map();
                  // Recognition guidance only; complete interpretation always uses all raw boundaries below.
                  const tokens=tokenizeAtom(text).meaningful;
                  if(tokens.some((_,start)=>matchedPrefix(tokens,candidate,start)))incomplete=true;
                  for(const parts of operandPartitions(text,slots)){
                      const predicateIndex=slots.findIndex(slot=>slot.role==="predicate"),predicateRaw=text.slice(parts[predicateIndex].start,parts[predicateIndex].end);
                      if(/[\[\],]/u.test(predicateRaw)||identify(predicateRaw)!==candidate.normalizedAlias)continue;
                      incomplete=true;let valid=true;const roles=[],identities=[];
                      for(let index=0;index<slots.length&&valid;index++){
                           const slot=slots[index];if(slot.role==="predicate")continue;
                           if(slot.role==="quantity"){
                               const quantity=composedQuantity(text,parts[index],slot.optional);
                               if(!quantity){valid=false;if(/[\[\],]/u.test(text.slice(parts[index].start,parts[index].end)))malformed=true;break;}
                               identities.push(quantity.identity);roles.push(...quantity.roles);continue;
                           }
                          const values=operandValues(text,parts[index],slot);
                          if(values===null){valid=false;if(/[\[\],]/u.test(text.slice(parts[index].start,parts[index].end)))malformed=true;break;}
                          identities.push(values.map(value=>identify(text.slice(value.start,value.end))));
                          for(const value of values){
                              const identity=identify(text.slice(value.start,value.end));
                              if(slot.role==="difficulty")valid=difficulty.has(identity);
                              else if(slot.role==="direction")valid=direction.has(identity);
                                else {const map={crest:requirementsLookups.crestAliases,method:requirementsLookups.methodAliases,soul:requirementsLookups.soulAliases}[slot.role];if(!map)throw new Error("Unexpected approved typed operand role.");valid=(map.get(identity)?.length??0)===1;}
                              if(!valid)break;
                              roles.push({...value,className:`requirements-${slot.role}`});
                          }
                      }
                      if(valid){
                          // Arrays retain slot/entry boundaries, order and repeated authored alternatives.
                          const key=JSON.stringify(identities),existing=interpretations.get(key);
                          if(!existing)interpretations.set(key,{type:"non-check",roles});
                          else for(let index=0;index<roles.length;index++){
                              // Equivalent allocations differ only at ignored boundary punctuation. Color their
                              // common exact operand span; leave the boundary in raw ordinary foreground, no winner.
                              existing.roles[index].start=Math.max(existing.roles[index].start,roles[index].start);
                              existing.roles[index].end=Math.min(existing.roles[index].end,roles[index].end);
                          }
                      }
                  }
                  complete.push(...interpretations.values());
              }
              return {complete,incomplete,malformed};
          };
           const renderDependency = (raw, candidate) => { const prefix=raw.slice(0,candidate.prefixEnd), rest=raw.slice(candidate.prefixEnd), leading=rest.slice(0,rest.length-rest.trimStart().length), operand=rest.trimStart(); add(prefix);add(leading); if(candidate.scope.state==="the"){add("THE","requirements-syntax");const name=operand.slice(3),space=name.slice(0,name.length-name.trimStart().length),value=name.trim(),trailing=name.slice(name.trimEnd().length);add(space);add(value,candidate.className);add(trailing);return;} if(candidate.scope.state==="in"){const q=candidate.scope.qualifier.index,name=operand.slice(0,q),room=operand.slice(q+2),nameSpace=name.slice(0,name.length-name.trimStart().length),roomSpace=room.slice(0,room.length-room.trimStart().length),roomTrailing=room.slice(room.trimEnd().length);add(nameSpace);add(name.trim(),candidate.className);add(name.slice(name.length-(name.length-name.trimEnd().length)));add("IN","requirements-syntax");add(roomSpace);add(room.trim(),candidate.className);add(roomTrailing);return;} add(operand,candidate.className); };
           const renderNonCheck = (raw, candidate) => {
               if(!candidate.roles){if(!candidate.className){add(raw);return;}add(raw.slice(0,candidate.argumentStart));add(raw.slice(candidate.argumentStart,candidate.argumentEnd),candidate.className);add(raw.slice(candidate.argumentEnd));return;}
               const offset=raw.length-raw.trimStart().length;
               const roles=candidate.roles;
               let cursor=0;
               for(const role of roles){const start=offset+role.start,end=offset+role.end;add(raw.slice(cursor,start));add(raw.slice(start,end),role.className);cursor=end;}
               add(raw.slice(cursor));
           };
           // Quantity spelling is raw, unlike alias identity. Never skip a punctuation-only chunk here.
           const countableArgument = (text,prefixEnd,end=text.length) => {
               const start=argumentStart(text,prefixEnd),tail=text.slice(start,end),match=/^(\S+)(\s+)([\s\S]+)$/u.exec(tail);
               if(!match||!/^[1-9][0-9]*$/u.test(match[1])||Number(match[1])>2147483647)return null;
               const countableStart=start+match[1].length+match[2].length;
               return {identity:identify(match[3]),roles:[{start,end:start+match[1].length,className:"requirements-quantity"},{start:countableStart,end,className:"requirements-countable"}]};
           };
           const completeNonCheck = (tokenized,chunkStart=0,chunkEnd=tokenized.chunks.length) => {
               const end=tokenized.chunks[chunkEnd-1]?.end,tokens=tokenized.meaningful.filter(token=>token.start>=tokenized.chunks[chunkStart]?.start&&token.end<=end);
               const raw=tokenized.text.slice(tokenized.chunks[chunkStart]?.start,end).trim();
               if(!validListStructure(raw))return false;
               if(operandCandidates(raw).complete.length>0)return true;
               if(/[\[\],]/u.test(raw))return false;
               for(const entry of matchingCandidates(tokens)){
                   const input=entry.candidate.predicate.inputSyntax;if(input===syntax.check||operandShapes.has(input))continue;
                   const shifted=entry.difficultyEnabled||entry.invalidDifficulty,match=matchedPrefix(tokens,entry.candidate,shifted?1:0);if(!match)continue;
                   const exact=match.next===tokens.length,rest=tokens.slice(match.next).map(token=>token.identity).join(" ");
                   if(input===syntax.countable&&!shifted){const argument=countableArgument(tokenized.text,match.prefixEnd,end);if(argument&&(requirementsLookups.countableAliases.get(argument.identity)?.length??0)>0)return true;continue;}
                   if(input===syntax.countableFirst&&!shifted){const argument=countableFirstArgument(tokenized.text,match.prefixEnd,end);if(argument&&(requirementsLookups.countableAliases.get(argument.identity)?.length??0)>0)return true;continue;}
                    if(input===syntax.only&&!shifted&&exact)return true;if(input===syntax.optional&&exact)return true;if(input===syntax.required&&entry.difficultyEnabled&&exact)return true;if(shifted)continue;
                   if(input===syntax.quantity&&/^[1-9][0-9]*$/u.test(rest))return true;if(input===syntax.item&&(requirementsLookups.itemAliases.get(rest)?.length??0)>0)return true;
               }return false;
           };
           const adjacentCompleteOperands = tokenized => { for(let split=1;split<tokenized.chunks.length;split++)if(completeNonCheck(tokenized,0,split)&&completeNonCheck(tokenized,split,tokenized.chunks.length))return true;return false; };
           const countableFirstArgument = (text,prefixEnd,end=text.length) => {
               // Quantity is the final raw token; never skip trailing punctuation or words.
               const start=argumentStart(text,prefixEnd),tail=text.slice(start,end).trimEnd(),match=/^([\s\S]*\S)(\s+)(\S+)$/u.exec(tail);
               if(!match||!/^[1-9][0-9]*$/u.test(match[3])||Number(match[3])>2147483647)return null;
               const countableEnd=start+match[1].length,quantityStart=countableEnd+match[2].length;
               return {identity:identify(match[1]),roles:[{start,end:countableEnd,className:"requirements-countable"},{start:quantityStart,end:start+tail.length,className:"requirements-quantity"}]};
           };
           const atom=(raw)=>{const text=raw.trim(); if(!text||!validListStructure(text)){mark(raw);return false;} const tokenized=tokenizeAtom(text),tokens=tokenized.meaningful,operands=operandCandidates(text),complete=[...operands.complete],incomplete=operands.incomplete?[true]:[]; let malformedScope=false, forbiddenGlobal=false;
              for(const entry of matchingCandidates(tokens)){const input=entry.candidate.predicate.inputSyntax;if(operandShapes.has(input)||/[\[\],]/u.test(text))continue;const shifted=entry.difficultyEnabled||entry.invalidDifficulty,start=shifted?1:0,match=matchedPrefix(tokens,entry.candidate,start);if(!match)continue;const exact=match.next===tokens.length,restNorm=tokens.slice(match.next).map(token=>token.identity).join(" ");
                if(input===syntax.only&&!shifted&&exact)complete.push({type:"plain"});
                 if(input===syntax.optional||input===syntax.required){if(exact&&((input===syntax.optional&&!entry.difficultyEnabled)||entry.difficultyEnabled)){const sourceDifficulty=tokenized.chunks[0];complete.push(entry.difficultyEnabled&&difficulty.has(sourceDifficulty.identity)?{type:"non-check",argumentStart:sourceDifficulty.start,argumentEnd:sourceDifficulty.end,className:"requirements-difficulty"}:{type:"plain"});}else if(exact&&!entry.difficultyEnabled)incomplete.push(true);}
                 if(input===syntax.countable&&!shifted){const argument=countableArgument(text,match.prefixEnd);if(argument)for(const countable of requirementsLookups.countableAliases.get(argument.identity)||[])complete.push({type:"non-check",roles:argument.roles});incomplete.push(true);continue;}
                 if(input===syntax.countableFirst&&!shifted){const argument=countableFirstArgument(text,match.prefixEnd);if(argument)for(const countable of requirementsLookups.countableAliases.get(argument.identity)||[])complete.push({type:"non-check",roles:argument.roles});incomplete.push(true);continue;}
                if(shifted)continue;
               if(input===syntax.quantity){if(/^[1-9][0-9]*$/u.test(restNorm)){const start=argumentStart(text,match.prefixEnd);complete.push({type:"non-check",argumentStart:start,argumentEnd:text.length,className:"requirements-quantity"});}else incomplete.push(true);}
               if(input===syntax.item){for(const item of requirementsLookups.itemAliases.get(restNorm)||[]){const start=argumentStart(text,match.prefixEnd);complete.push({type:"non-check",argumentStart:start,argumentEnd:text.length,className:"requirements-item"});}incomplete.push(true);}
               if(input===syntax.check){const scope=scoped(text,match.prefixEnd);if(scope.state==="malformed"){malformedScope=true;continue;}if(scope.state==="unknown"){forbiddenGlobal=true;continue;}if(!scope.name?.trim()){incomplete.push(true);continue;}let targetRoom=roomId, className="requirements-local";if(scope.state==="the"){targetRoom=null;className="requirements-global";}if(scope.state==="in"){const rooms=requirementsLookups.roomsByReference.get(identify(scope.room))||[];if(rooms.length!==1){complete.push({type:"dependency",prefixEnd:match.prefixEnd,scope,className:"requirements-unresolved"});continue;}targetRoom=rooms[0].roomId;className="requirements-in";}const checkName=identify(scope.name),matches=targetRoom===null?(requirementsLookups.checksByName.get(checkName)||[]):(requirementsLookups.checksByRoom.get(targetRoom)?.get(checkName)||[]);complete.push({type:"dependency",prefixEnd:match.prefixEnd,scope,className:matches.length===1?className:"requirements-unresolved"});}
             }
             if(complete.length===1){const candidate=complete[0];if(candidate.type==="dependency")renderDependency(raw,candidate);else if(candidate.type==="non-check")renderNonCheck(raw,candidate);else add(raw);return true;}if(complete.length>1){add(raw,"requirements-unresolved");return true;}if(malformedScope||operands.malformed){mark(raw);return false;}if(forbiddenGlobal){mark(raw,"requirements-unknown");return false;}if(incomplete.length){mark(raw,"requirements-incomplete");return false;}if(adjacentCompleteOperands(tokenized)){mark(raw);return false;}mark(raw,"requirements-unknown");return false;};
        const expression=(end=false,depth=0)=>{let expect=true, operators=new Set(), lastKnown=false, saw=false, lastOperator=null;
          while(at<source.length){if(end&&source[at]===")"){if(expect){if(lastOperator!==null)spans[lastOperator][1]="requirements-malformed";mark(")");}else add(")",`requirements-paren-depth-${(depth - 1) % 4}`);at++;return !expect;} if(!end&&source[at]===")"){mark(")");at++;expect=false;continue;} if(/\s/u.test(source[at])){const start=at;while(at<source.length&&/\s/u.test(source[at]))at++;add(source.slice(start,at));continue;}
            if(source[at]==="("){if(!expect&&lastKnown){const start=at++;let escaped=false,nested=false;while(at<source.length&&source[at]!==")"){if(source[at]==="\\")escaped=true;if(source[at]==="(")nested=true;at++;}if(at>=source.length||escaped||nested){mark(source.slice(start,at));return false;}add(source.slice(start,++at),"requirements-annotation");lastKnown=false;continue;}if(!expect){mark("(");at++;continue;}const opening=spans.length;add("(",`requirements-paren-depth-${depth % 4}`);at++;if(!expression(true,depth+1))spans[opening][1]="requirements-malformed";lastKnown=false;expect=false;saw=true;continue;}
              const operator=wordAt("AND",at)?"AND":wordAt("OR",at)?"OR":null;if(operator){if(expect)mark(operator);else{operators.add(operator);lastOperator=spans.length;add(operator,operators.size>1?"requirements-malformed":"requirements-syntax");if(operators.size>1)danger=true;expect=true;}at+=operator.length;lastKnown=false;continue;}
            const start=at;let bracketDepth=0;while(at<source.length){const character=source[at];if(bracketDepth===0&&(character==="("||character===")"||wordAt("AND",at)||wordAt("OR",at)))break;if(character==="[")bracketDepth++;if(character==="]")bracketDepth--;at++;}const raw=source.slice(start,at);if(!raw){at++;continue;}if(!expect){mark(raw);continue;}lastKnown=atom(raw);expect=false;saw=true;}
           if(end){if(lastOperator!==null)spans[lastOperator][1]="requirements-malformed";mark("", "requirements-malformed");return false;}if(expect&&saw){danger=true;if(lastOperator!==null)spans[lastOperator][1]="requirements-malformed";}return !expect;};
         try { if(!source.trim()){add(source);return {spans,danger:false};} expression(); return {spans,danger}; } catch { throw new Error("Requirements highlighter failed."); }
      };
        const rawRequirements = (layer,value) => { const target=layer?.querySelector?.("[data-v2-requirements-highlight]");if(target){target.replaceChildren();target.textContent=value;}layer?.removeAttribute?.("data-v2-requirements-highlight-ready");layer?.classList?.remove("requirements-parser-danger");if(layer?.matches?.(".requirements-inline-layer"))layer.closest("td")?.classList.remove("requirements-parser-danger"); };
           const renderArchivedRequirements = element => { const target=element.matches("[data-v2-requirements-highlight]")?element:element.querySelector("[data-v2-requirements-highlight]"); if(!target)return; const source=target.textContent||"",savedContext=requirementsContext,savedLookups=requirementsLookups;try{requirementsContext={...savedContext,rooms:[],checks:[]};requirementsLookups={...savedLookups,roomsByReference:new Map(),checksByName:new Map(),checksByRoom:new Map()};target.replaceChildren();const result=parseRequirementsV2(source,null);for(const [text,cls] of result.spans)setText(target,text,cls);}catch{target.replaceChildren();target.textContent=source;target.removeAttribute("data-v2-requirements-highlight-ready");target.classList.remove("requirements-parser-danger");target.closest("td")?.classList.remove("requirements-parser-danger");}finally{requirementsContext=savedContext;requirementsLookups=savedLookups;} };
        const renderRequirements = (layer, value, roomId) => { try { const target=layer?.querySelector?.("[data-v2-requirements-highlight]");if(!target)return;target.replaceChildren();const result=parseRequirementsV2(value,roomId);for(const [text,cls] of result.spans)setText(target,text,cls);layer.setAttribute("data-v2-requirements-highlight-ready","");layer.classList.toggle("requirements-parser-danger",result.danger);if(layer.matches(".requirements-inline-layer"))layer.closest("td")?.classList.toggle("requirements-parser-danger",result.danger);}catch{rawRequirements(layer,value);} };
        const growInlineRequirement = editor => { if(!requirementEditor(editor))return;editor.style.height="0px";editor.style.height=`${editor.scrollHeight}px`; };
        const renderInlineRequirement = editor => { if(!requirementEditor(editor))return;const layer=editor.closest(".requirements-editor-layer");renderRequirements(layer,editor.value,editor.dataset.v2RequirementsRoom);growInlineRequirement(editor); };
        const alignExpandedRequirement = state => { const editor=state?.proxy,layer=state?.layer,highlight=layer?.querySelector("[data-v2-requirements-highlight]");if(!expandedRequirementEditor(editor)||!layer||!highlight)return;editor.style.height="0px";const style=getComputedStyle(editor),lineHeight=Number.parseFloat(style.lineHeight)||14,padding=(Number.parseFloat(style.paddingTop)||0)+(Number.parseFloat(style.paddingBottom)||0),minimum=Math.ceil(lineHeight*3+padding),maximum=Math.max(minimum,window.innerHeight-32),desired=Math.max(minimum,editor.scrollHeight);layer.style.height=`${Math.min(desired,maximum)}px`;editor.style.height="100%";editor.style.overflowY=desired>maximum?"auto":"hidden";highlight.style.transform=`translate(${-editor.scrollLeft}px, ${-editor.scrollTop}px)`; };
        const renderExpandedRequirement = state => { if(!state)return;renderRequirements(state.layer,state.value,state.roomId);alignExpandedRequirement(state); };
        const synchronizeRequirement = state => { const proxy=state?.proxy;if(!expandedRequirementEditor(proxy))return;state.value=proxy.value;renderExpandedRequirement(state);const inline=root.querySelector(`#${CSS.escape(state.id)}`);if(requirementEditor(inline)){state.inline=inline;inline.value=state.value;renderInlineRequirement(inline);} };
        const createExpandedRequirement = state => { const host=root.querySelector("[data-v2-requirements-expanded-host]");if(!host)return false;const backdrop=document.createElement("div"),layer=document.createElement("div"),highlight=document.createElement("div"),proxy=document.createElement("textarea");backdrop.className="requirements-expanded-backdrop";backdrop.dataset.v2RequirementsExpandedBackdrop="";layer.className="requirements-editor-layer requirements-expanded-layer";layer.setAttribute("role","dialog");layer.setAttribute("aria-modal","true");layer.setAttribute("aria-label",`${state.label} expanded editor`);highlight.dataset.v2RequirementsHighlight="";highlight.setAttribute("aria-hidden","true");proxy.id=`${state.id}-expanded-proxy`;proxy.className="requirements-expanded-editor";proxy.dataset.v2RequirementsExpandedEditor="";proxy.setAttribute("aria-label",`${state.label} expanded editor`);proxy.value=state.value;layer.append(highlight,proxy);backdrop.append(layer);host.replaceChildren(backdrop);const consume=event=>{event.preventDefault();event.stopImmediatePropagation();};let armedPointer=null,readyPointer=null;const clearArm=()=>{window.removeEventListener("pointerdown",interveningPointerDown,true);window.removeEventListener("pointerup",armedPointerUp,true);window.removeEventListener("pointercancel",armedPointerCancel,true);armedPointer=null;};const clearReady=()=>{window.removeEventListener("pointerdown",readyPointerDown,true);window.removeEventListener("pointercancel",readyPointerCancel,true);window.removeEventListener("click",readyClick,true);readyPointer=null;};const clearGesture=()=>{clearArm();clearReady();};const interveningPointerDown=()=>clearArm();const armedPointerCancel=()=>{clearArm();clearReady();};const armedPointerUp=event=>{const pointerId=armedPointer,matching=event.pointerId===pointerId,dismiss=matching&&event.isPrimary===true&&event.button===0&&event.target===backdrop;clearArm();clearReady();if(!dismiss)return;consume(event);readyPointer=pointerId;window.addEventListener("pointerdown",readyPointerDown,true);window.addEventListener("pointercancel",readyPointerCancel,true);window.addEventListener("click",readyClick,true);};const readyPointerDown=()=>clearReady();const readyPointerCancel=()=>clearReady();const readyClick=event=>{const dismiss=event.target===backdrop;clearReady();if(!dismiss)return;consume(event);closeRequirements(true);};backdrop.addEventListener("pointerdown",event=>{if(event.target!==backdrop)return;consume(event);clearGesture();if(event.isPrimary!==true||event.button!==0)return;armedPointer=event.pointerId;window.addEventListener("pointerdown",interveningPointerDown,true);window.addEventListener("pointerup",armedPointerUp,true);window.addEventListener("pointercancel",armedPointerCancel,true);});backdrop.addEventListener("click",event=>{if(event.target===backdrop)consume(event);});layer.addEventListener("pointerdown",event=>event.stopPropagation());layer.addEventListener("pointerup",event=>event.stopPropagation());layer.addEventListener("click",event=>event.stopPropagation());state.backdrop=backdrop;state.layer=layer;state.proxy=proxy;state.clearBackdropGesture=clearGesture;renderExpandedRequirement(state);return true; };
        const closeRequirements = (restoreFocus=false) => { if(!activeRequirement||closingRequirement)return;closingRequirement=true;pointerRequirementCaret=null;const state=activeRequirement,proxy=state.proxy;if(expandedRequirementEditor(proxy)){state.value=proxy.value;state.start=proxy.selectionStart??state.start;state.end=proxy.selectionEnd??state.end;state.direction=proxy.selectionDirection||state.direction;}const inline=root.querySelector(`#${CSS.escape(state.id)}`);if(requirementEditor(inline)){state.inline=inline;if(inline.value!==state.value)inline.value=state.value;renderInlineRequirement(inline);}state.clearBackdropGesture?.();state.backdrop?.remove();activeRequirement=null;if(restoreFocus&&requirementEditor(inline)&&inline.isConnected){inline.focus({preventScroll:true});try{inline.setSelectionRange(Math.min(state.start,state.value.length),Math.min(state.end,state.value.length),state.direction);}catch{}}closingRequirement=false;reconcileAttention(); };
        const reconcileRequirement = () => { pointerRequirementCaret=null;if(!activeRequirement)return;const state=activeRequirement,hadProxy=expandedRequirementEditor(state.proxy)&&state.proxy.isConnected;if(hadProxy)state.value=state.proxy.value;const inline=root.querySelector(`#${CSS.escape(state.id)}`);if(!requirementEditor(inline)){closeRequirements(false);return;}state.inline=inline;if(inline.value!==state.value)inline.value=state.value;renderInlineRequirement(inline);if(!hadProxy){state.clearBackdropGesture?.();if(!createExpandedRequirement(state)){closeRequirements(false);return;}}if(state.proxy.value!==state.value)state.proxy.value=state.value;renderExpandedRequirement(state);if(!hadProxy){state.proxy.focus({preventScroll:true});try{state.proxy.setSelectionRange(Math.min(state.start,state.value.length),Math.min(state.end,state.value.length),state.direction);}catch{}} };
        const sourceOffset = (container,node,offset) => { if(!(node instanceof Node)||!container.contains(node))return null;try{const range=document.createRange();range.selectNodeContents(container);range.setEnd(node,Math.max(0,Math.min(Number(offset)||0,node.nodeType===Node.TEXT_NODE?(node.nodeValue||"").length:node.childNodes.length)));return range.toString().length;}catch{return null;} };
        const nearestSourceOffset = (container,x,y) => { const walker=document.createTreeWalker(container,NodeFilter.SHOW_TEXT),nodes=[];let node,base=0,best={offset:0,distance:Infinity};while(node=walker.nextNode()){const text=node.nodeValue||"";nodes.push({node,text,base});base+=text.length;}const consider=(offset,rect,horizontal)=>{if(!rect||(!rect.width&&!rect.height))return;const px=horizontal,top=rect.top,bottom=rect.bottom||rect.top,dx=Math.abs(x-px),dy=y<top?top-y:y>bottom?y-bottom:0,distance=dx*dx+dy*dy;if(distance<best.distance)best={offset,distance};};for(const item of nodes){const boundaries=[0];for(let at=0;at<item.text.length;){at+=String.fromCodePoint(item.text.codePointAt(at)).length;boundaries.push(at);}for(const local of boundaries){try{const range=document.createRange();range.setStart(item.node,local);range.collapse(true);const rect=range.getBoundingClientRect();consider(item.base+local,rect,rect.left);}catch{}}for(let index=0;index<boundaries.length-1;index++){const start=boundaries[index],end=boundaries[index+1];try{const range=document.createRange();range.setStart(item.node,start);range.setEnd(item.node,end);const rects=[...range.getClientRects()];for(const rect of rects){consider(item.base+start,rect,rect.left);consider(item.base+end,rect,rect.right);}}catch{}}}return Math.max(0,Math.min(best.offset,base)); };
        const normalizeSourceOffset = (text,offset) => { const value=Math.max(0,Math.min(offset,text.length));return value>0&&value<text.length&&text.charCodeAt(value)>=0xDC00&&text.charCodeAt(value)<=0xDFFF&&text.charCodeAt(value-1)>=0xD800&&text.charCodeAt(value-1)<=0xDBFF?value-1:value; };
        const pointerSourceOffset = (editor,event) => { const container=editor.closest(".requirements-editor-layer")?.querySelector("[data-v2-requirements-highlight]");if(!container)return normalizeSourceOffset(editor.value,editor.selectionStart??0);const previousEditorPointerEvents=editor.style.pointerEvents,previousHighlightPointerEvents=container.style.pointerEvents;editor.style.pointerEvents="none";container.style.pointerEvents="auto";let offset=null;try{const at=(node,index)=>sourceOffset(container,node,index);if(typeof document.caretPositionFromPoint==="function"){const position=document.caretPositionFromPoint(event.clientX,event.clientY);offset=position&&at(position.offsetNode,position.offset);}if(offset===null&&typeof document.caretRangeFromPoint==="function"){const range=document.caretRangeFromPoint(event.clientX,event.clientY);offset=range&&at(range.startContainer,range.startOffset);}}finally{editor.style.pointerEvents=previousEditorPointerEvents;container.style.pointerEvents=previousHighlightPointerEvents;}return normalizeSourceOffset(editor.value,offset===null?nearestSourceOffset(container,event.clientX,event.clientY):offset); };
        const openRequirements = (inline,caret=null) => { pointerRequirementCaret=null;if(!requirementEditor(inline)||activeRequirement?.id===inline.id)return;if(activeRequirement)closeRequirements(false);const nativeStart=inline.selectionStart??0,nativeEnd=inline.selectionEnd??nativeStart,start=caret===null?nativeStart:Math.max(0,Math.min(caret,inline.value.length)),end=caret===null?nativeEnd:start;const state={id:inline.id,inline,label:inline.getAttribute("aria-label")||"Requirements",roomId:inline.dataset.v2RequirementsRoom,value:inline.value,start,end,direction:caret===null?(inline.selectionDirection||"none"):"none",backdrop:null,layer:null,proxy:null,clearBackdropGesture:null};activeRequirement=state;if(!createExpandedRequirement(state)){activeRequirement=null;return;}suppressedRequirementBlur=inline;state.proxy.focus({preventScroll:true});try{state.proxy.setSelectionRange(state.start,state.end,state.direction);}catch{}if(suppressedRequirementBlur===inline)suppressedRequirementBlur=null; };
      const attribute = name => name.replace(/[A-Z]/g, m => `-${m.toLowerCase()}`);
      // RH1 keeps two independent DOM associations, not scene selection or row drafts.
      // Fresh reconciliation reads actual hover/focus and C# facts; a removed row
      // never leaves its former marker identity as a sticky attention target.
      const attentionSelector = "tr[data-v2-row-annotation-kind][data-v2-row-annotation-id]";
      let hoveredAttentionRow = null, focusedAttentionRow = null;
      let attentionSurface = null;
      const suppressedAttentionParts = new Set();
      const attentionRow = node => {
          const row = node instanceof Element ? node.closest(attentionSelector) : null;
          if (!row || !root.contains(row)) return null;
          return Object.keys(configs).some(kind => {
              const c = config(kind);
              return row.hasAttribute(`data-${attribute(c.row)}`) && row.closest(`table[data-${attribute(c.table)}='active']`);
          }) ? row : null;
      };
      const attentionFocusRow = node => {
          if (activeRequirement?.proxy === node && expandedRequirementEditor(node))
              return attentionRow(root.querySelector(`#${CSS.escape(activeRequirement.id)}`));
          return node instanceof Element && node.matches("input,textarea,select,button") ? attentionRow(node) : null;
      };
      const paintAttention = () => {
          // Restore only browser-owned visible presentation. Opacity does not
          // change SVG visiblePainted hit testing; original geometry/strokes,
          // handles, metadata and DOM order stay with the scene owner/Blazor.
          clearAttentionSurface();
          const svg = root.querySelector(".room-context-scene-pane [data-scene-layout-canvas]"),
              layer = svg?.querySelector("[data-scene-annotations]"),
              surface = layer?.querySelector("[data-v2-row-annotation-foreground]");
          if (disposed || !svg?.__v2SceneViewport?.isLive() || !surface || layer.style.display === "none") return;
          attentionSurface = surface;
          const current = new Map();
          // Insertion order is visual only: hover first, focus last. A shared
          // canonical target is projected once at focus strength.
          for (const [row, strength] of [[hoveredAttentionRow, "hover"], [focusedAttentionRow, "focus"]]) {
              if (!attentionRow(row)) continue;
              const kind = row.dataset.v2RowAnnotationKind, id = row.dataset.v2RowAnnotationId;
              const item = layer.querySelector(`[data-scene-selection-kind='${CSS.escape(kind)}'][data-scene-id='${CSS.escape(id)}']`);
              if (item && svg.__v2SceneViewport.isKindVisible(kind)) { current.delete(item); current.set(item, strength); }
          }
          for (const [item, strength] of current) {
              const frame = item.dataset.sceneSelectionKind === "subroom",
                  body = frame ? item.querySelector(".scene-layout-subroom-body") : item.querySelector(":scope > rect, :scope > circle"),
                  label = item.querySelector(":scope > text");
              if (!body) continue;
              const projection = document.createElementNS("http://www.w3.org/2000/svg", "g");
              // Closed presentation classes/attributes only. Never clone a scene
              // group: that would also copy command identity and resize targets.
              projection.classList.add(frame ? "scene-layout-subroom" : `scene-layout-${item.dataset.sceneSelectionKind}`, `row-annotation-${strength}`);
              if (item.classList.contains("scene-layout-enemy")) projection.classList.add("scene-layout-enemy");
              if (item.classList.contains("selected")) projection.classList.add("row-annotation-selected");
              const transform = item.getAttribute("transform");
              if (transform !== null) projection.setAttribute("transform", transform);
              projection.style.transform = item.style.transform;
              const edge = projectAttentionPart(body), visible = projectAttentionPart(body);
              edge.classList.add("row-annotation-dark-edge");
              visible.classList.add("row-annotation-border");
              projection.append(edge, visible);
              if (label) projection.append(projectAttentionPart(label));
              surface.append(projection);
              for (const part of label ? [body, label] : [body]) {
                  part.classList.add("row-annotation-source-suppressed");
                  suppressedAttentionParts.add(part);
              }
          }
      };
      const projectAttentionPart = source => {
          const part = document.createElementNS("http://www.w3.org/2000/svg", source.localName);
          for (const name of ["x", "y", "width", "height", "rx", "ry", "cx", "cy", "r", "text-anchor", "dominant-baseline", "transform"])
              if (source.hasAttribute(name)) part.setAttribute(name, source.getAttribute(name));
          if (source.classList.contains("scene-layout-subroom-body")) part.classList.add("scene-layout-subroom-body");
          if (source.localName === "text") part.textContent = source.textContent;
          return part;
      };
      const clearAttentionSurface = () => {
          for (const part of suppressedAttentionParts) part.classList.remove("row-annotation-source-suppressed");
          suppressedAttentionParts.clear();
          attentionSurface?.replaceChildren();
          attentionSurface = null;
      };
      const reconcileAttention = () => {
          hoveredAttentionRow = disposed ? null : attentionRow(root.querySelector(`${attentionSelector}:hover`));
          focusedAttentionRow = disposed ? null : attentionFocusRow(document.activeElement);
          paintAttention();
      };
      const attentionPointer = event => {
          if (disposed) return;
          const next = attentionRow(event.type === "pointerout" ? event.relatedTarget : event.target);
          if (next === hoveredAttentionRow) return;
          hoveredAttentionRow = next;
          paintAttention();
      };
      const attentionFocus = event => {
          if (disposed) return;
          const next = attentionFocusRow(event.type === "focusout" ? event.relatedTarget : event.target);
          if (next === focusedAttentionRow) return;
          focusedAttentionRow = next;
          paintAttention();
      };
     const kindOf = x => Object.keys(configs).find(kind => x?.dataset?.[configs[kind].field]) ?? null;
     const data = (x,k,n) => x.dataset[`${config(k).directionPrefix}${n}`];
     const clear = kind => { for(const [item, saved] of feedback){if(kind && saved.kind!==kind)continue;item.classList.remove(saved.before,saved.tail);feedback.delete(item);} };
      const owned = event => !disposed && event.target instanceof Node && root.contains(event.target);
      const followNative = () => {
          const focused = document.activeElement;
          const field = activeRequirement?.proxy === focused ? activeRequirement.inline : focused;
          if (!control(field) || !root.contains(field) || field.disabled || field.readOnly) return { field: null, native: null };
          const kind = kindOf(field), c = kind && config(kind);
          const value = activeRequirement?.proxy === focused ? focused.value : field instanceof HTMLInputElement && field.type === "checkbox" ? String(field.checked) : field.value;
          if (c) return { field, native: { kind, client: field.dataset[c.client], field: field.dataset[c.field], value } };
          if (field.dataset.v2HeaderField) return { field, native: { kind: "header", client: "", field: field.dataset.v2HeaderField, value } };
          return { field, native: { kind: "unowned", client: "", field: "", value } };
      };
      const followBoundary = async (token, callback, page) => {
          const ap = window.archipelagoConnection;
          const current = () => !disposed && ap?.followCurrent(token, root) && !ap.followBlocked();
          if (!current() || drag) return false;
          const captured = followNative();
          if (!await callback.invokeMethodAsync("PreflightArchipelagoBoundary", token, captured.native, page)) return false;
          if (!current() || drag) return false;
          const final = followNative();
          // A user changed editor/final value during preflight. Consume, not queue.
          if (final.field !== captured.field || JSON.stringify(final.native) !== JSON.stringify(captured.native)) return false;
          if (captured.field) {
              if (activeRequirement) closeRequirements(true);
              // Normal blur delivers the exact native value to the established
              // editor handler. Its save is independent of AP cancellation.
              // Expanded Requirements first returns its proxy text to the inline
              // field via normal dismissal; that field then receives normal blur.
              captured.field.blur();
          }
          if (!current()) return false;
          return true; // No AP save invocation, completion observation or wait.
      };
     // Consume only C#-supplied proposal reconciliation facts. This is native
     // value cleanup on the existing boundary completion/render, not a browser
     // row draft, save queue, revision protocol or another circuit callback.
      const reconciledFacts = new WeakMap();
      const reconcileBoundaryFacts = (kind=null,client=null) => {
         for(const registered of kind?[kind]:Object.keys(configs)){
             const c=config(registered);if(!c)return;
             for(const field of root.querySelectorAll(`[data-${attribute(c.field)}][data-v2-child-reconcile-value]`)){
                 if(client&&field.dataset[c.client]!==client)continue;
                  if(!control(field)||field.disabled||field.readOnly)continue;
                  const value=field.dataset.v2ChildReconcileValue;
                  const exclusive=field.dataset.v2ChildReconcileExclusive==="true", previous=reconciledFacts.get(field);
                  // Consume a released instruction once, even if Blazor's value
                  // frame was unchanged. Ordinary later typing is not swept/reset.
                  if(!exclusive&&previous?.exclusive===false&&previous.value===value)continue;
                  reconciledFacts.set(field,{exclusive,value});
                 if(field instanceof HTMLInputElement&&field.type==="checkbox")field.checked=value==="true";
                 else field.value=value;
                 if(activeRequirement?.id===field.id){activeRequirement.value=value;if(activeRequirement.proxy)activeRequirement.proxy.value=value;renderExpandedRequirement(activeRequirement);}
                 if(requirementEditor(field))renderInlineRequirement(field);
                  if(friendlyNameEditor(field))renderFriendlyName(field);
             }
         }
     };
      const dispatchBoundary = (kind,owner,args) => {
          const c=config(kind),field=root.querySelector(`#${CSS.escape(`editor-row-${args[1]}-${args[2]}`)}`),value=args[args.length-1];
          if(field?.hasAttribute("data-v2-child-reconcile-value")&&value!==null&&value!==undefined&&String(value)!==field.dataset.v2ChildReconcileValue)
              for(const target of root.querySelectorAll(`[data-${attribute(c.client)}]`))if(target.dataset[c.client]===args[1])reconciledFacts.delete(target);
          return owner.invokeMethodAsync(...args).then(result=>{
              if(!disposed&&owners[kind]===owner)reconcileBoundaryFacts(kind,args[1]);
              return result;
          });
      };
           const keydown = event => { if(!owned(event)||typeof event.key!=="string")return; const field=event.target;if(expandedRequirementEditor(field)){if(event.key==="Tab"||event.key==="Escape"){event.preventDefault();event.stopImmediatePropagation();closeRequirements(true);}return;} const kind=kindOf(field); if(!kind||!control(field))return; if(event.key==="Tab"){if(field.value?.length>0)tabSources.add(field);return;} if(!event.key.startsWith("Arrow")||field instanceof HTMLSelectElement||(field instanceof HTMLInputElement&&field.type==="checkbox"))return; const isRequirement=requirementEditor(field);if(isRequirement&&(event.isComposing===true||event.keyCode===229))return; const c=config(kind), owner=owners[kind], direction=event.key.slice(5).toLowerCase(), suffix=`${direction[0].toUpperCase()}${direction.slice(1)}`, encoded=data(field,kind,suffix); if(!encoded||!caretAtBoundary(field,event.key)||!owner?.invokeMethodAsync)return; const [row,target]=encoded.split("|"), latent=data(field,kind,`${suffix}Latent`)==="true"; if(!row||!target||(!latent&&!root.querySelector(`#editor-row-${row}-${target}`)))return; event.preventDefault();event.stopImmediatePropagation(); const args=[c.method,field.dataset[c.client],field.dataset[c.field],direction,field.value];dispatchBoundary(kind,owner,args).catch(()=>{}); };
         const pointerdown = event => { pointerRequirementCaret=null;if(!owned(event)||event.isPrimary!==true||event.button!==0)return;const inline=event.target;if(requirementEditor(inline))pointerRequirementCaret={id:inline.id,pointerId:event.pointerId,offset:pointerSourceOffset(inline,event)}; };
         const pointercancel = () => { pointerRequirementCaret=null; };
         const dblclick = event => { if(!owned(event))return;const inline=event.target;if(!requirementEditor(inline))return;event.stopImmediatePropagation();const caret=pointerRequirementCaret?.id===inline.id?pointerRequirementCaret.offset:null;pointerRequirementCaret=null;openRequirements(inline,caret); };
          const focusout = event => { if(!owned(event))return; attentionFocus(event); const field=event.target;if(field===suppressedRequirementBlur){suppressedRequirementBlur=null;event.stopImmediatePropagation();return;}const kind=kindOf(field); if(!kind||!control(field))return; const c=config(kind),owner=owners[kind]; if(!field.dataset[c.client]||!owner?.invokeMethodAsync)return; event.stopImmediatePropagation(); const target=tabSources.has(field)&&event.relatedTarget instanceof HTMLElement&&event.relatedTarget.id?event.relatedTarget.id:null; tabSources.delete(field); const textField=field instanceof HTMLTextAreaElement||(field instanceof HTMLInputElement&&field.type!=="checkbox");const args=[c.blur,field.dataset[c.client],field.dataset[c.field],target,textField?field.value:null];dispatchBoundary(kind,owner,args).catch(()=>{}); };
        const dragstart = event => { if(!owned(event)||!(event.target instanceof Element))return;const kind=Object.keys(configs).find(k=>event.target.closest(`[data-${attribute(configs[k].grip)}]`));if(!kind)return;const c=config(kind),grip=event.target.closest(`[data-${attribute(c.grip)}]`);if(grip.disabled||grip.draggable!==true||!event.dataTransfer)return;const row=grip.closest(`tr[data-${attribute(c.row)}]`),id=row?.dataset[c.row],owner=owners[kind],table=row?.closest("table");if(!id||!owner?.invokeMethodAsync||table?.dataset[c.table]!=="active")return;closeRequirements(false);try{event.dataTransfer.setData("text/plain",id);}catch{return;}drag={kind,id,partition:table.dataset[c.table],section:c.section?table.dataset[c.section]:null};event.dataTransfer.effectAllowed="move"; };
      // Resolve the receiving table independently of drag state so no-drag drops
      // remain harmless browser-only events.
      const dropTarget=event=>{const kind=event.target instanceof Element?Object.keys(configs).find(k=>event.target.closest(`table[data-${attribute(configs[k].table)}]`)):null;return {kind,table:kind?event.target.closest(`table[data-${attribute(configs[kind].table)}]`):null};};
     const tableFor=(event,c)=>event.target instanceof Element?event.target.closest(`table[data-${c.table.replace(/[A-Z]/g,m=>`-${m.toLowerCase()}`)}]`):null;
       const acceptsDrag=(table,saved)=>{const c=config(saved.kind);return c&&table&&saved.partition==="active"&&table.dataset[c.table]===saved.partition&&(!c.section||saved.section&&saved.section===table.dataset[c.section]);};
       const dragover=event=>{if(!owned(event)||!drag)return;const c=config(drag.kind),table=tableFor(event,c);if(!acceptsDrag(table,drag)){clear(drag.kind);return;}event.preventDefault();clear(drag.kind);const target=event.target instanceof Element?event.target.closest(`tr[data-${attribute(c.row)}],tr[data-${attribute(c.tailRow)}='true']`):null;const item=target??table;item.classList.add(target?c.before:c.tail);feedback.set(item,{kind:drag.kind,before:c.before,tail:c.tail});};
        const drop=event=>{
            if(!owned(event))return;
            const receiving=dropTarget(event),table=receiving.table,saved=drag;
            drag=null;if(!saved)return;
            const c=config(saved.kind);clear(saved.kind);
            if(receiving.kind!==saved.kind||!acceptsDrag(table,saved))return;
            event.preventDefault();event.stopImmediatePropagation();
            if(!c.drop||!owners[saved.kind]?.invokeMethodAsync)return;
            const target=event.target instanceof Element?event.target.closest(`tr[data-${attribute(c.row)}]`):null;
            const index=target?Number.parseInt(target.dataset.v2ChildIndex,10):Number.parseInt(table.dataset.v2ChildCount,10);
            const args=[c.drop,saved.id,saved.partition,Number.isSafeInteger(index)?index:0];
            if(c.section)args.push(saved.section);
            owners[saved.kind].invokeMethodAsync(...args).catch(()=>{});
        };
        const change=event=>{if(!owned(event))return;const field=event.target,kind=kindOf(field);if(!kind||field.disabled||!(field instanceof HTMLSelectElement||(field instanceof HTMLInputElement&&field.type==="checkbox")))return;const c=config(kind),owner=owners[kind];if(!c.control||!field.dataset[c.client]||!owner?.invokeMethodAsync)return;event.stopImmediatePropagation();dispatchBoundary(kind,owner,[c.control,field.dataset[c.client],field.dataset[c.field],field instanceof HTMLInputElement?String(field.checked):field.value]).catch(()=>{});};
        const click=event=>{if(!owned(event))return;sceneLayerClick(event);if(event.target instanceof Element&&!control(event.target)){const cell=event.target.closest("td");let editor=cell?.querySelector("input:not([type='checkbox']), textarea");if(!editor&&(cell?.classList.contains("verification-cell")||cell?.classList.contains("check-type-cell")))editor=cell.querySelector("select");if(editor&&!editor.disabled&&!editor.readOnly)editor.focus();}const button=event.target instanceof Element?event.target.closest("button[data-v2-child-action]"):null;if(!button||button.disabled)return;const action=button.dataset.v2ChildAction,kind=Object.keys(configs).find(k=>configs[k].action&&button.closest(`table[data-${attribute(configs[k].table)}]`));if(!kind)return;const c=config(kind),id=button.closest("tr")?.dataset[c.row],owner=owners[kind];if(!c.actions?.includes(action)||!id||!owner?.invokeMethodAsync)return;event.preventDefault();event.stopImmediatePropagation();owner.invokeMethodAsync(c.action,action,id).catch(()=>{});};
        const sceneLayerClick = event => {
            const button = event.target instanceof Element ? event.target.closest("button") : null;
            if (!button || button.disabled) return;
            const pane = root.querySelector(".room-context-scene-pane"), scene = pane?.querySelector("[data-scene-layout-canvas]")?.__v2SceneViewport;
            if (!scene?.isLive()) return;
            if (button.matches("[data-scene-action='rearm']") && pane.contains(button)) {
                scene.explicitLayerIntent(pane.dataset.sceneSelectedKind, pane.dataset.sceneSelectedId); return;
            }
            const match = button.id.match(/^v2-(subroom|transition|connection|check)-(select|show|place)-([0-9a-f-]+)$/i);
            if (!match) return;
            const tableKind = match[1], c = config(tableKind), row = button.closest("tr"), kind = tableKind === "transition" ? "exit" : tableKind;
            if (!c || !owners[tableKind] || !row?.closest(`table[data-${attribute(c.table)}='active']`) || row.dataset[c.row]?.toLowerCase() !== match[3].toLowerCase()) return;
            scene.explicitLayerIntent(kind, match[3], row.dataset.v2RowAnnotationId);
        };
    const dragend=()=>{if(drag)clear(drag.kind);drag=null;};
         const input=event=>{
             const field=event.target;
              if(owned(event)&&friendlyNameEditor(field))renderFriendlyName(field);
             if(owned(event)&&!field.disabled&&!field.readOnly&&(field instanceof HTMLInputElement||field instanceof HTMLTextAreaElement)&&field.dataset.v2ChildMirrorTarget){
                 const target=root.querySelector(`#${CSS.escape(field.dataset.v2ChildMirrorTarget)}`);
                 if(target instanceof HTMLInputElement&&!target.disabled&&!target.readOnly&&field.closest("tr")?.contains(target))target.value=field.value;
             }
             if(expandedRequirementEditor(field)){if(activeRequirement?.proxy===field)synchronizeRequirement(activeRequirement);return;}if(requirementEditor(field))renderInlineRequirement(field);
         };
        const scroll=event=>{if(activeRequirement?.proxy===event.target)alignExpandedRequirement(activeRequirement);};
        const resize=()=>{root.querySelectorAll("textarea[data-v2-requirements-editor]").forEach(growInlineRequirement);alignExpandedRequirement(activeRequirement);};
        ["keydown","pointerdown","pointercancel","dblclick","focusout","input","scroll","change","click","dragstart","dragover","drop","dragend"].forEach(type=>root.addEventListener(type,{keydown,pointerdown,pointercancel,dblclick,focusout,input,scroll,change,click,dragstart,dragover,drop,dragend}[type],true));window.addEventListener("resize",resize);
         root.addEventListener("pointerover", attentionPointer, true);
         root.addEventListener("pointerout", attentionPointer, true);
         root.addEventListener("focusin", attentionFocus, true);
         // focusout updates attention inside its existing capture handler before
         // stopImmediatePropagation, without altering the independent save callback.
          const api={
              followBoundary,
              reconcileAttention,
              syncAttention:paintAttention,
              retireSceneAttention:svg=>{if(attentionSurface && svg.contains(attentionSurface))clearAttentionSurface();},
            isLive:()=>!disposed,
            beforeFocus:()=>{if(!disposed)reconcileBoundaryFacts();},
            register:(reference,kind,descriptor)=>{if(!disposed&&typeof kind==="string"&&descriptor&&["method","blur","drop","row","table","grip","field","client","directionPrefix","before","tail","tailRow"].every(key=>typeof descriptor[key]==="string"&&descriptor[key])){pointerRequirementCaret=null;if(activeRequirement&&kindOf(activeRequirement.inline)===kind)closeRequirements(false);clear(kind);if(drag?.kind===kind)drag=null;configs[kind]=Object.freeze({...descriptor});owners[kind]=reference;reconcileAttention();}},
            unregister:kind=>{pointerRequirementCaret=null;if(activeRequirement&&kindOf(activeRequirement.inline)===kind)closeRequirements(false);clear(kind);if(drag?.kind===kind)drag=null;delete owners[kind];delete configs[kind];reconcileAttention();},
                setRequirementsContext:context=>{requirementsContext=context??{rooms:[],checks:[],predicates:[],items:[],countables:[],souls:[],crests:[],methods:[]};requirementsLookups=compileRequirementsLookups(requirementsContext);reconcileBoundaryFacts();reconcileRequirement();root.querySelectorAll("textarea[data-v2-requirements-editor]").forEach(renderInlineRequirement);root.querySelectorAll(".requirements-preview").forEach(renderArchivedRequirements);reconcileFriendlyNames();reconcileAttention();},
            commitActiveRequirement:async()=>{let field;if(activeRequirement){const state=activeRequirement;closeRequirements(true);field=root.querySelector(`#${CSS.escape(state.id)}`);}else{const focused=document.activeElement;if(!requirementEditor(focused)||!root.contains(focused))return true;field=focused;}const kind=kindOf(field),c=kind&&config(kind),owner=kind&&owners[kind];if(!requirementEditor(field)||!c||!owner?.invokeMethodAsync||!field.dataset[c.client])return false;suppressedRequirementBlur=field;field.blur();if(suppressedRequirementBlur===field)suppressedRequirementBlur=null;try{return await owner.invokeMethodAsync(c.blur,field.dataset[c.client],field.dataset[c.field],null,field.value)!==false;}catch{return false;}},
            focus:id=>{if(!disposed)window.focusEditorField(id);},
            clear,
            dispose:()=>{if(disposed)return;disposed=true;root.removeEventListener("pointerover",attentionPointer,true);root.removeEventListener("pointerout",attentionPointer,true);root.removeEventListener("focusin",attentionFocus,true);reconcileAttention();["keydown","pointerdown","pointercancel","dblclick","focusout","input","scroll","change","click","dragstart","dragover","drop","dragend"].forEach(type=>root.removeEventListener(type,{keydown,pointerdown,pointercancel,dblclick,focusout,input,scroll,change,click,dragstart,dragover,drop,dragend}[type],true));window.removeEventListener("resize",resize);closeRequirements(false);clear();drag=null;pointerRequirementCaret=null;Object.keys(owners).forEach(k=>delete owners[k]);Object.keys(configs).forEach(k=>delete configs[k]);if(root[v2ChildInteractionOwnerKey]===api)delete root[v2ChildInteractionOwnerKey];}
        };
        root[v2ChildInteractionOwnerKey]=api;
        reconcileFriendlyNames();
        return api;
};
