window.mapNavigation = (() => {
    const viewport = window.areaMapViewportResolver;
    const key = svg => viewport.storageKey(svg);
    const parse = value => value.split(" ").map(Number);
    const save = (svg, state) => sessionStorage.setItem(key(svg), JSON.stringify(state));
    const load = svg => { try { const state = JSON.parse(sessionStorage.getItem(key(svg)) || "{}"); return state && typeof state === "object" && !Array.isArray(state) ? state : {}; } catch { return {}; } };
    const defaultVisible = (svg, owner) => owner !== "linked" || svg.dataset.hasOverlay !== "true";
    const visible = (svg, state, owner) => state[owner] ?? defaultVisible(svg, owner);
    const apply = (svg, state) => {
        if (state.view) svg.setAttribute("viewBox", state.view.join(" "));
        for (const owner of ["linked", "unlinked"]) svg.querySelectorAll(`[data-map-owner=${owner}]`).forEach(path => { const isVisible = visible(svg, state, owner); path.classList.toggle("hidden-linked-owner", owner === "linked" && !isVisible); path.style.display = isVisible || owner === "linked" ? "" : "none"; });
        svg.querySelectorAll(".map-overlay-image").forEach(image => image.style.display = visible(svg, state, "image") ? "" : "none");
    };
    const mapPoint = (svg, clientX, clientY) => {
        const point = svg.createSVGPoint();
        point.x = clientX;
        point.y = clientY;
        return point.matrixTransform(svg.getScreenCTM().inverse());
    };
    const createOwner = svg => {
        if (!svg) throw new Error("The requested map SVG is not mounted.");
        const abort = new AbortController();
        let disposed = false, down = null, suppressClick = false, navigationBounds, initialFit, state = {}, activeKey;
        const controlListeners = new Map();
        const current = () => !disposed && svg.isConnected && svg.__mapNavigationOwner === owner;
        const snapshot = () => ({ image: visible(svg, state, "image"), linked: visible(svg, state, "linked"), unlinked: visible(svg, state, "unlinked") });
        const updatePrepaint = () => window.updateMapLayerPreferences?.(svg.dataset.mapKey, snapshot());
        const applyControls = () => controlListeners.forEach((_, button) => {
            const command = button.dataset.mapCommand;
            if (command === "reset") return;
            const shown = visible(svg, state, command);
            button.setAttribute("aria-pressed", String(shown));
            if (command === "image" && button.classList.contains("map-image-toggle-compact")) {
                const label = shown ? "hide map image" : "show map image";
                button.setAttribute("title", label); button.setAttribute("aria-label", label);
            }
        });
        const reconcileControls = () => {
            const controls = new Set(svg.closest(".area-map-surface")?.querySelectorAll("[data-map-command]") || []);
            controlListeners.forEach((listener, button) => {
                if (controls.has(button)) return;
                button.removeEventListener("click", listener);
                controlListeners.delete(button);
            });
            controls.forEach(button => {
                if (controlListeners.has(button)) return;
                const listener = event => { if (!current()) return; event.preventDefault(); const command = button.dataset.mapCommand; if (command === "reset") reset(); else toggle(command); };
                controlListeners.set(button, listener);
                button.addEventListener("click", listener);
            });
        };
        const reconcile = () => {
            if (!current()) return snapshot();
            const nextKey = key(svg);
            if (activeKey !== nextKey) { activeKey = nextKey; state = load(svg); }
            if (!state.visibilityDefaultsV4) {
                if (state.image === undefined) state.image = true;
                if (state.unlinked === undefined) state.unlinked = true;
                if (state.linked === undefined) state.linked = svg.dataset.hasOverlay !== "true";
                state.visibilityDefaultsV4 = true;
            }
            let resolved = viewport.resolve(svg, state);
            if (!resolved) return snapshot();
            if (resolved.hasViewVersion && state.viewVersion !== resolved.viewVersion) {
                delete state.view;
                state.viewVersion = resolved.viewVersion;
                resolved = viewport.resolve(svg, state);
            }
            navigationBounds = resolved.navigationBounds;
            initialFit = resolved.initialFit;
            state.view = resolved.view;
            reconcileControls();
            apply(svg, state); applyControls(); save(svg, state); updatePrepaint(); return snapshot();
        };
        const toggle = layer => { if (!current()) return snapshot(); state[layer] = !visible(svg, state, layer); apply(svg, state); applyControls(); save(svg, state); updatePrepaint(); return snapshot(); };
        const reset = () => { if (!current()) return snapshot(); state.view = viewport.clamp(initialFit, navigationBounds); apply(svg, state); applyControls(); save(svg, state); return snapshot(); };
        const dispose = () => { if (disposed) return; disposed = true; abort.abort(); controlListeners.forEach((listener, button) => button.removeEventListener("click", listener)); controlListeners.clear(); down = null; if (svg.__mapNavigationOwner === owner) delete svg.__mapNavigationOwner; };
        const owner = { snapshot, reconcile, toggle, reset, dispose };
        svg.__mapNavigationOwner = owner;
        reconcile();
        const options = { signal: abort.signal };
        svg.addEventListener("wheel", event => { if (!current()) return; event.preventDefault(); const view = parse(svg.getAttribute("viewBox")), pointer = mapPoint(svg, event.clientX, event.clientY), pixels = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? svg.clientHeight : 1), factor = Math.exp(Math.max(-.06, Math.min(.06, pixels * .003))), zoomed = viewport.clamp([navigationBounds[0], navigationBounds[1], view[2] * factor, view[3] * factor], navigationBounds), effectiveFactor = zoomed[2] / view[2]; view[2] = zoomed[2]; view[3] = zoomed[3]; view[0] = pointer.x - (pointer.x - view[0]) * effectiveFactor; view[1] = pointer.y - (pointer.y - view[1]) * effectiveFactor; state.view = viewport.clamp(view, navigationBounds); apply(svg, state); save(svg, state); }, { passive: false, capture: true, signal: abort.signal });
        svg.addEventListener("pointerdown", event => { if (current() && event.button === 0) down = { x: event.clientX, y: event.clientY, lastX: event.clientX, lastY: event.clientY, pointerId: event.pointerId, panning: false }; }, options);
        svg.addEventListener("pointermove", event => { if (!current() || !down || event.pointerId !== down.pointerId) return; if (Math.hypot(event.clientX - down.x, event.clientY - down.y) < 5) return; if (!down.panning) { down.panning = true; svg.setPointerCapture(down.pointerId); } const previous = mapPoint(svg, down.lastX, down.lastY), at = mapPoint(svg, event.clientX, event.clientY), view = parse(svg.getAttribute("viewBox")); down.lastX = event.clientX; down.lastY = event.clientY; state.view = viewport.clamp([view[0] - (at.x - previous.x), view[1] - (at.y - previous.y), view[2], view[3]], navigationBounds); apply(svg, state); save(svg, state); }, options);
        const end = event => { if (!current() || !down || event.pointerId !== down.pointerId) return; if (down.panning) { suppressClick = true; if (svg.hasPointerCapture(event.pointerId)) svg.releasePointerCapture(event.pointerId); } down = null; };
        svg.addEventListener("pointerup", end, options); svg.addEventListener("pointercancel", end, options);
        svg.addEventListener("click", event => { if (!current() || !suppressClick) return; suppressClick = false; event.preventDefault(); event.stopImmediatePropagation(); }, { capture: true, signal: abort.signal });
    };
    return {
        initialize(svg) {
            if (!svg) throw new Error("The requested map SVG is not mounted.");
            if (svg.__mapNavigationOwner) svg.__mapNavigationOwner.reconcile();
            else createOwner(svg);
        },
        dispose(svg) { svg?.__mapNavigationOwner?.dispose(); }
    };
})();

window.mapOverlayCalibration = (() => {
    const parse = value => value.split(" ").map(Number);
    const point = (svg, x, y) => { const value = svg.createSVGPoint(); value.x = x; value.y = y; return value.matrixTransform(svg.getScreenCTM().inverse()); };
    return {
        initialize(id) {
            const svg = document.getElementById(id);
            if (!svg || svg.dataset.ready) return;
            svg.dataset.ready = "true";
            const base = parse(svg.dataset.baseViewbox);
            let down = null;
            svg.addEventListener("wheel", event => {
                event.preventDefault();
                if (event.ctrlKey) {
                    const scaleX = document.getElementById("overlay-scale-x-percent"), scaleY = document.getElementById("overlay-scale-y-percent");
                    const zoom = parse(svg.getAttribute("viewBox"))[2] / base[2];
                    const step = zoom < .5 ? .1 : zoom < 1 ? .25 : .5;
                    const change = event.deltaY < 0 ? step : -step;
                    scaleX.value = String(Math.max(.1, Number(scaleX.value || 100) + change));
                    scaleY.value = String(Math.max(.1, Number(scaleY.value || 100) + change));
                    scaleX.dispatchEvent(new Event("input", { bubbles: true })); scaleY.dispatchEvent(new Event("input", { bubbles: true }));
                    return;
                }
                const view = parse(svg.getAttribute("viewBox")), at = point(svg, event.clientX, event.clientY), factor = Math.exp(Math.max(-.06, Math.min(.06, event.deltaY * .003)));
                view[2] *= factor; view[3] *= factor; view[0] = at.x - (at.x - view[0]) * factor; view[1] = at.y - (at.y - view[1]) * factor;
                svg.setAttribute("viewBox", view.join(" "));
            }, { passive: false });
            svg.addEventListener("pointerdown", event => { if (event.button === 0) { down = { id: event.pointerId, x: event.clientX, y: event.clientY, mode: event.ctrlKey ? event.shiftKey ? "scale" : "align" : "pan" }; svg.setPointerCapture(event.pointerId); } });
            svg.addEventListener("pointermove", event => {
                if (!down || event.pointerId !== down.id) return;
                const previous = point(svg, down.x, down.y), current = point(svg, event.clientX, event.clientY);
                if (down.mode === "align") {
                    const base = parse(svg.dataset.baseViewbox), left = document.getElementById("overlay-left-offset-percent"), bottom = document.getElementById("overlay-bottom-offset-percent");
                    left.value = String(Number(left.value || 0) + (current.x - previous.x) / base[2] * 100);
                    bottom.value = String(Number(bottom.value || 0) - (current.y - previous.y) / base[3] * 100);
                    left.dispatchEvent(new Event("input", { bubbles: true })); bottom.dispatchEvent(new Event("input", { bubbles: true }));
                } else if (down.mode === "scale") {
                    const base = parse(svg.dataset.baseViewbox), scaleX = document.getElementById("overlay-scale-x-percent"), scaleY = document.getElementById("overlay-scale-y-percent");
                    scaleX.value = String(Math.max(.1, Number(scaleX.value || 100) + (current.x - previous.x) / base[2] * 100));
                    scaleY.value = String(Math.max(.1, Number(scaleY.value || 100) - (current.y - previous.y) / base[3] * 100));
                    scaleX.dispatchEvent(new Event("input", { bubbles: true })); scaleY.dispatchEvent(new Event("input", { bubbles: true }));
                } else {
                    const view = parse(svg.getAttribute("viewBox"));
                    view[0] -= current.x - previous.x; view[1] -= current.y - previous.y;
                    svg.setAttribute("viewBox", view.join(" "));
                }
                down.x = event.clientX; down.y = event.clientY;
            });
            const end = event => { if (down?.id === event.pointerId) { if (svg.hasPointerCapture(event.pointerId)) svg.releasePointerCapture(event.pointerId); down = null; } };
            svg.addEventListener("pointerup", end); svg.addEventListener("pointercancel", end);
        }
    };
})();

window.sceneLayoutNavigation = (() => {
    const parse = value => value.split(" ").map(Number);
    const fittedView = (base, padding = .1) => [base[0] - base[2] * padding, base[1] - base[3] * padding, base[2] * (1 + 2 * padding), base[3] * (1 + 2 * padding)];
    const mapPoint = (svg, clientX, clientY) => { const point = svg.createSVGPoint(); point.x = clientX; point.y = clientY; return point.matrixTransform(svg.getScreenCTM().inverse()); };
    const clamp = (view, base) => {
        view[2] = Math.min(base[2], Math.max(base[2] / 8, view[2])); view[3] = view[2] * base[3] / base[2];
        view[0] = Math.max(base[0], Math.min(base[0] + base[2] - view[2], view[0])); view[1] = Math.max(base[1], Math.min(base[1] + base[3] - view[3], view[1])); return view;
    };
    const threshold = (svg, state) => 6 * state.view[2] / Math.max(1, svg.clientWidth);
    const invokeLayout = (state, method, ...args) => {
        state.saveQueue = state.saveQueue.catch(() => {}).then(async () => {
        try {
            const result = await state.dotNetReference.invokeMethodAsync(method, ...args);
            if (result === false) await state.dotNetReference.invokeMethodAsync("LayoutSaveFailed", method);
            return result;
        }
        catch (error) {
            state.armed = null;
            try { await state.dotNetReference.invokeMethodAsync("ClearArmed"); } catch { }
            try { await state.dotNetReference.invokeMethodAsync("LayoutSaveFailed", method, error?.message || String(error)); } catch { }
            return false;
        }
        });
        return state.saveQueue;
    };
    const snapRectangle = (svg, state, selected, rect, disabled) => {
        svg.querySelectorAll(".scene-layout-snap-guide").forEach(guide => guide.remove());
        if (disabled) { state.snaps = {}; return rect; }
        const edges = { x: [], y: [] }, room = svg.querySelector(".scene-layout-room-bounds");
        const add = element => { const x = Number(element.getAttribute("x")), y = Number(element.getAttribute("y")), w = Number(element.getAttribute("width")), h = Number(element.getAttribute("height")); edges.x.push(x, x + w); edges.y.push(y, y + h); };
        if (room) add(room); svg.querySelectorAll("[data-scene-layout-subroom]").forEach(item => { if (item !== selected) add(item.querySelector(".scene-layout-subroom-body")); });
        const limit = threshold(svg, state), nearest = (name, value, candidates) => {
            const locked = state.snaps[name];
            if (locked) {
                locked.drift += Math.abs(value - locked.edge);
                if (locked.drift <= limit * 2) return locked.edge;
                delete state.snaps[name];
            }
            const candidate = candidates.map(edge => [edge, Math.abs(edge - value)]).filter(item => item[1] <= limit).sort((a, b) => a[1] - b[1])[0];
            if (!candidate) return value;
            state.snaps[name] = { edge: candidate[0], drift: 0 };
            return candidate[0];
        };
        const left = nearest("left", rect[0], edges.x), right = nearest("right", rect[0] + rect[2], edges.x), top = nearest("top", rect[1], edges.y), bottom = nearest("bottom", rect[1] + rect[3], edges.y);
        [[left, "x"], [right, "x"], [top, "y"], [bottom, "y"]].forEach(([value, axis]) => { if ((axis === "x" ? value !== rect[0] && value !== rect[0] + rect[2] : value !== rect[1] && value !== rect[1] + rect[3])) { const guide = document.createElementNS("http://www.w3.org/2000/svg", "line"); guide.classList.add("scene-layout-snap-guide"); if (axis === "x") { guide.setAttribute("x1", value); guide.setAttribute("x2", value); guide.setAttribute("y1", state.view[1]); guide.setAttribute("y2", state.view[1] + state.view[3]); } else { guide.setAttribute("x1", state.view[0]); guide.setAttribute("x2", state.view[0] + state.view[2]); guide.setAttribute("y1", value); guide.setAttribute("y2", value); } svg.append(guide); } });
        return [left, top, Math.max(.1, right - left), Math.max(.1, bottom - top)];
    };
    const updateSubroomGeometry = (subroom, x, y, width, height) => {
        subroom.dataset.subroomX = String(x); subroom.dataset.subroomY = String(y); subroom.dataset.subroomWidth = String(width); subroom.dataset.subroomHeight = String(height);
        const body = subroom.querySelector(".scene-layout-subroom-body"), label = subroom.querySelector("text");
        body.setAttribute("x", x); body.setAttribute("y", y); body.setAttribute("width", width); body.setAttribute("height", height);
        if (label) { label.setAttribute("x", x + width / 2); label.setAttribute("y", y + height / 2); }
    };
    const updateBounds = svg => {
        const state = svg?.__sceneLayoutNavigation;
        if (!state) return;
        state.base = fittedView(parse(svg.dataset.baseViewbox), Number(svg.dataset.scenePadding || .1));
        state.view = clamp(state.view, state.base);
        svg.setAttribute("viewBox", state.view.join(" "));
    };
    const initialize = (svg, dotNetReference) => {
        if (!svg) return;
        if (svg.__sceneLayoutNavigation) { svg.__sceneLayoutNavigation.dotNetReference = dotNetReference; updateBounds(svg); return; }
        svg.dataset.ready = "true";
        const base = fittedView(parse(svg.dataset.baseViewbox), Number(svg.dataset.scenePadding || .1));
        const state = { base, view: base.slice(), dotNetReference, armed: null, selected: null, undo: null, snaps: {}, saveQueue: Promise.resolve() };
        svg.__sceneLayoutNavigation = state;
        svg.setAttribute("viewBox", state.view.join(" "));
        let down = null;
        svg.addEventListener("wheel", event => {
            event.preventDefault();
            const view = parse(svg.getAttribute("viewBox")), pointer = mapPoint(svg, event.clientX, event.clientY), pixels = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? svg.clientHeight : 1), factor = Math.exp(Math.max(-.06, Math.min(.06, pixels * .003)));
            view[2] *= factor; view[3] *= factor; view[0] = pointer.x - (pointer.x - view[0]) * factor; view[1] = pointer.y - (pointer.y - view[1]) * factor;
            state.view = clamp(view, state.base);
            svg.setAttribute("viewBox", state.view.join(" "));
        }, { passive: false });
        svg.addEventListener("pointerdown", event => {
            if (event.button !== 0) return;
            state.snaps = {};
            const marker = event.target.closest("[data-scene-layout-marker]"), subroom = event.target.closest("[data-scene-layout-subroom]"), resize = event.target.closest("[data-scene-layout-resize]")?.dataset.sceneLayoutResize;
            const point = mapPoint(svg, event.clientX, event.clientY);
            if (state.armed?.kind === "subroom") { svg.setPointerCapture(event.pointerId); down = { x: event.clientX, y: event.clientY, pointerId: event.pointerId, drawing: true, start: point, subroomId: state.armed.id }; return; }
            if (state.armed) { down = { x: event.clientX, y: event.clientY, pointerId: event.pointerId, placing: true, point }; return; }
            const item = marker || subroom;
            if (item && state.selected !== item) { svg.querySelector(".scene-layout-draft")?.remove(); state.armed = null; state.selected?.classList.remove("selected"); state.selected = item; item.classList.add("selected"); down = { x: event.clientX, y: event.clientY, pointerId: event.pointerId, selecting: true }; return; }
            down = { x: event.clientX, y: event.clientY, lastX: event.clientX, lastY: event.clientY, pointerId: event.pointerId, panning: false, marker, subroom, resize, original: subroom ? [Number(subroom.dataset.subroomX), Number(subroom.dataset.subroomY), Number(subroom.dataset.subroomWidth), Number(subroom.dataset.subroomHeight)] : null };
        });
        svg.addEventListener("pointermove", event => {
            if (!down) return;
            if (down.drawing) {
                const current = mapPoint(svg, event.clientX, event.clientY), x = Math.min(down.start.x, current.x), y = Math.min(down.start.y, current.y), width = Math.abs(current.x - down.start.x), height = Math.abs(current.y - down.start.y);
                svg.querySelector(".scene-layout-draft")?.remove(); const draft = document.createElementNS("http://www.w3.org/2000/svg", "rect"); draft.classList.add("scene-layout-draft"); draft.setAttribute("x", x); draft.setAttribute("y", y); draft.setAttribute("width", width); draft.setAttribute("height", height); svg.append(draft); down.draft = [x, y, width, height]; return;
            }
            if (Math.hypot(event.clientX - down.x, event.clientY - down.y) < 5) return;
            if (down.selecting || down.placing) return;
            if (!down.panning) { down.panning = true; svg.setPointerCapture(down.pointerId); }
            const previous = mapPoint(svg, down.lastX, down.lastY), current = mapPoint(svg, event.clientX, event.clientY), view = parse(svg.getAttribute("viewBox"));
            down.lastX = event.clientX; down.lastY = event.clientY;
            if (down.marker) down.marker.setAttribute("transform", `translate(${current.x} ${current.y})`);
            else if (down.subroom) {
                let [x, y, width, height] = [Number(down.subroom.dataset.subroomX), Number(down.subroom.dataset.subroomY), Number(down.subroom.dataset.subroomWidth), Number(down.subroom.dataset.subroomHeight)], dx = current.x - previous.x, dy = current.y - previous.y;
                if (!down.resize) { x += dx; y += dy; } else { if (down.resize.includes("w")) { x += dx; width -= dx; } if (down.resize.includes("e")) width += dx; if (down.resize.includes("n")) { y += dy; height -= dy; } if (down.resize.includes("s")) height += dy; }
                [x, y, width, height] = snapRectangle(svg, state, down.subroom, [x, y, Math.max(.1, width), Math.max(.1, height)], event.ctrlKey);
                updateSubroomGeometry(down.subroom, x, y, width, height);
            }
            else { state.view = clamp([view[0] - (current.x - previous.x), view[1] - (current.y - previous.y), view[2], view[3]], state.base); svg.setAttribute("viewBox", state.view.join(" ")); }
        });
        svg.addEventListener("pointerup", async event => {
            if (!down) return;
            if (svg.hasPointerCapture(event.pointerId)) svg.releasePointerCapture(event.pointerId);
            if (down.placing) {
                const point = mapPoint(svg, event.clientX, event.clientY); await invokeLayout(state, "SaveMarkerPosition", state.armed.kind, state.armed.id, point.x, point.y, state.armed.kind !== "exit"); state.armed = null;
            }
            if (down.drawing) {
                svg.querySelector(".scene-layout-draft")?.remove(); if (down.draft && down.draft[2] * svg.clientWidth / state.view[2] >= 5 && down.draft[3] * svg.clientHeight / state.view[3] >= 5) await invokeLayout(state, "SaveSubroomGeometry", down.subroomId, down.draft[0], down.draft[1], down.draft[2], down.draft[3]); state.armed = null;
            }
            if (down.panning && down.marker) {
                const point = mapPoint(svg, event.clientX, event.clientY);
                state.undo = { kind: down.marker.dataset.markerKind, id: down.marker.dataset.markerId, point: mapPoint(svg, down.x, down.y) };
                await invokeLayout(state, "SaveMarkerPosition", down.marker.dataset.markerKind, down.marker.dataset.markerId, point.x, point.y, false);
            }
            if (down.panning && down.subroom) { state.undo = { kind: "subroom", id: down.subroom.dataset.subroomId, geometry: down.original }; await invokeLayout(state, "SaveSubroomGeometry", down.subroom.dataset.subroomId, Number(down.subroom.dataset.subroomX), Number(down.subroom.dataset.subroomY), Number(down.subroom.dataset.subroomWidth), Number(down.subroom.dataset.subroomHeight)); }
            svg.querySelectorAll(".scene-layout-snap-guide").forEach(guide => guide.remove()); down = null;
        });
        svg.addEventListener("pointercancel", () => { svg.querySelector(".scene-layout-draft")?.remove(); down = null; });
        svg.addEventListener("contextmenu", event => { event.preventDefault(); state.armed = null; dotNetReference.invokeMethodAsync("ClearArmed"); });
        svg.addEventListener("keydown", event => { if (event.key === "Escape") { svg.querySelector(".scene-layout-draft")?.remove(); state.armed = null; down = null; dotNetReference.invokeMethodAsync("ClearArmed"); } });
        svg.addEventListener("keydown", async event => { if (!state.selected || !["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(event.key)) return; event.preventDefault(); const amount = event.shiftKey ? 5 : 1, dx = event.key === "ArrowLeft" ? -amount : event.key === "ArrowRight" ? amount : 0, dy = event.key === "ArrowUp" ? -amount : event.key === "ArrowDown" ? amount : 0; if (state.selected.dataset.sceneLayoutMarker !== undefined) { const match = state.selected.getAttribute("transform").match(/[-.\d]+/g).map(Number), x = match[0] + dx, y = match[1] + dy; state.selected.setAttribute("transform", `translate(${x} ${y})`); await invokeLayout(state, "SaveMarkerPosition", state.selected.dataset.markerKind, state.selected.dataset.markerId, x, y, false); } else { const x = Number(state.selected.dataset.subroomX) + dx, y = Number(state.selected.dataset.subroomY) + dy, width = Number(state.selected.dataset.subroomWidth), height = Number(state.selected.dataset.subroomHeight); updateSubroomGeometry(state.selected, x, y, width, height); await invokeLayout(state, "SaveSubroomGeometry", state.selected.dataset.subroomId, x, y, width, height); } });
    };
    return {
        initialize(id, dotNetReference) { initialize(document.getElementById(id), dotNetReference); },
        updateBounds(id) { updateBounds(document.getElementById(id)); },
        arm(id, kind, markerId) { const svg = document.getElementById(id), state = svg?.__sceneLayoutNavigation; if (state) { state.armed = { kind, id: markerId }; svg.focus(); } },
        undo(id) { const state = document.getElementById(id)?.__sceneLayoutNavigation, undo = state?.undo; if (!undo) return; state.undo = null; return undo.kind === "subroom" ? invokeLayout(state, "SaveSubroomGeometry", undo.id, ...undo.geometry) : invokeLayout(state, "SaveMarkerPosition", undo.kind, undo.id, undo.point.x, undo.point.y, false); },
        reset(id) { const svg = document.getElementById(id), state = svg?.__sceneLayoutNavigation; if (state) { state.view = state.base.slice(); svg.setAttribute("viewBox", state.view.join(" ")); } }
    };
})();

window.sceneImageCapture = (() => {
    const parse = value => value.split(" ").map(Number);
    const point = (svg, x, y) => { const value = svg.createSVGPoint(); value.x = x; value.y = y; return value.matrixTransform(svg.getScreenCTM().inverse()); };
    const input = id => document.getElementById(id);
    const change = (element, value) => { element.value = String(value); element.dispatchEvent(new Event("input", { bubbles: true })); };
    const minimumScale = .1;
    const sharedScaleFactor = (scaleX, scaleY, delta) => {
        if (!Number.isFinite(scaleX) || !Number.isFinite(scaleY) || scaleX <= 0 || scaleY <= 0) return null;
        // The geometric mean treats the independent axes symmetrically while
        // making the absolute calibration step relative to the current draft.
        const coverage = Math.exp((Math.log(scaleX) + Math.log(scaleY)) / 2);
        if (!Number.isFinite(coverage) || coverage <= 0) return null;
        const requested = (coverage + delta) / coverage;
        const lowerBound = minimumScale / Math.min(scaleX, scaleY);
        const upperBound = Number.MAX_VALUE / Math.max(scaleX, scaleY);
        return Math.min(upperBound, Math.max(lowerBound, requested));
    };
    return {
        initialize(id, scaleXId, scaleYId, panXId, panYId) {
            const svg = document.getElementById(id);
            if (!svg || svg.dataset.ready) return;
            svg.dataset.ready = "true";
            const base = parse(svg.dataset.baseViewbox);
            let down = null;
            svg.addEventListener("wheel", event => {
                event.preventDefault();
                const scaleX = input(scaleXId), scaleY = input(scaleYId);
                if (event.ctrlKey) {
                    const zoom = parse(svg.getAttribute("viewBox"))[2] / base[2];
                    const step = zoom < .5 ? .05 : zoom < 1.5 ? .25 : .5;
                    const delta = event.deltaY < 0 ? -step : step;
                    const currentX = Number(scaleX.value), currentY = Number(scaleY.value);
                    const factor = sharedScaleFactor(currentX, currentY, delta);
                    if (factor !== null) {
                        change(scaleX, currentX * factor);
                        change(scaleY, currentY * factor);
                    }
                    return;
                }
                const view = parse(svg.getAttribute("viewBox")), at = point(svg, event.clientX, event.clientY), factor = Math.exp(Math.max(-.06, Math.min(.06, event.deltaY * .003)));
                view[2] *= factor; view[3] *= factor; view[0] = at.x - (at.x - view[0]) * factor; view[1] = at.y - (at.y - view[1]) * factor;
                svg.setAttribute("viewBox", view.join(" "));
            }, { passive: false });
            svg.addEventListener("pointerdown", event => { if (event.button === 0) { down = { id: event.pointerId, x: event.clientX, y: event.clientY, mode: event.ctrlKey ? event.shiftKey ? "scale" : "pan-image" : "inspect" }; svg.setPointerCapture(event.pointerId); } });
            svg.addEventListener("pointermove", event => {
                if (!down || event.pointerId !== down.id) return;
                const previous = point(svg, down.x, down.y), current = point(svg, event.clientX, event.clientY), scaleX = input(scaleXId), scaleY = input(scaleYId), panX = input(panXId), panY = input(panYId);
                if (down.mode === "pan-image") {
                    change(panX, Number(panX.value || 0) + (current.x - previous.x) / base[2] * Number(scaleX.value || 100));
                    change(panY, Number(panY.value || 0) + (current.y - previous.y) / base[3] * Number(scaleY.value || 100));
                } else if (down.mode === "scale") {
                    change(scaleX, Math.max(.1, Number(scaleX.value || 100) - (current.x - previous.x) / base[2] * 100));
                    change(scaleY, Math.max(.1, Number(scaleY.value || 100) - (current.y - previous.y) / base[3] * 100));
                } else {
                    const view = parse(svg.getAttribute("viewBox"));
                    view[0] -= current.x - previous.x; view[1] -= current.y - previous.y;
                    svg.setAttribute("viewBox", view.join(" "));
                }
                down.x = event.clientX; down.y = event.clientY;
            });
            const end = event => { if (down?.id === event.pointerId) { if (svg.hasPointerCapture(event.pointerId)) svg.releasePointerCapture(event.pointerId); down = null; } };
            svg.addEventListener("pointerup", end); svg.addEventListener("pointercancel", end);
        }
    };
})();
