// Shared map-only prepaint support. Room/scene composition stays in editor.js.
(() => {
    const mapViewPreferencePrefix = "silksong-rando-logic.map-view.";
    const mapLayerPreferences = new Map();
    const cssAttributeString = value => `"${String(value).replace(/[\0-\x1f\x7f"\\]/g, character => character === "\0" ? "\\fffd " : `\\${character.codePointAt(0).toString(16)} `)}"`;
    window.areaMapViewportResolver = (() => {
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
        const storageKey = svg => `${mapViewPreferencePrefix}${svg.dataset.mapKey}`;
        const resolve = (svg, loadedState) => {
            const base = parseViewBox(svg.getAttribute("data-base-viewbox"));
            if (!base) return null;
            const initial = parseViewBox(svg.getAttribute("data-initial-viewbox")) || base;
            const navigationBounds = fittedView(base), initialFit = fittedView(initial);
            const rawViewVersion = svg.getAttribute("data-map-view-version") || "";
            const hasViewVersion = rawViewVersion.trim().length > 0;
            const state = loadedState && typeof loadedState === "object" && !Array.isArray(loadedState) ? loadedState : {};
            const saved = Array.isArray(state.view) && state.view.length === 4 && state.view.every(Number.isFinite) && (!hasViewVersion || state.viewVersion === rawViewVersion);
            return { navigationBounds, initialFit, view: clamp(saved ? state.view : initialFit, navigationBounds), viewVersion: rawViewVersion, hasViewVersion };
        };
        return Object.freeze({ storageKey, resolve, clamp });
    })();
    if (window.customElements && !customElements.get("area-map-prepaint")) {
        customElements.define("area-map-prepaint", class extends HTMLElement {
            connectedCallback() {
                const svg = this.previousElementSibling;
                if (!svg?.matches?.("svg.map-wireframe")) return;
                let state = {};
                try { state = JSON.parse(sessionStorage.getItem(window.areaMapViewportResolver.storageKey(svg)) || "{}"); } catch { }
                const resolved = window.areaMapViewportResolver.resolve(svg, state);
                if (resolved) svg.setAttribute("viewBox", resolved.view.join(" "));
            }
        });
    }
    const style = document.createElement("style");
    style.id = "area-map-preferences";
    document.head.append(style);
    const apply = () => {
        const rules = [`.map-wireframe .map-overlay-image, .map-wireframe [data-map-owner="unlinked"] { display: inline; }
.map-wireframe[data-has-overlay="true"] [data-map-owner="linked"] { fill: transparent; stroke: transparent; pointer-events: all; cursor: pointer; }
.map-wireframe[data-has-overlay="true"] [data-map-owner="linked"]:is(:hover, :focus) { stroke: rgb(255 255 255 / 35%); stroke-width: 1px; }`];
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
        style.textContent = rules.join("\n");
    };
    window.updateMapLayerPreferences = (viewportKey, layers) => {
        if (typeof viewportKey !== "string" || !layers || typeof layers !== "object") return;
        const next = { ...(mapLayerPreferences.get(viewportKey) || {}) };
        for (const layer of ["image", "linked", "unlinked"]) if (typeof layers[layer] === "boolean") next[layer] = layers[layer];
        mapLayerPreferences.set(viewportKey, next); apply();
    };
    try {
        for (let index = 0; index < sessionStorage.length; index++) {
            const storageKey = sessionStorage.key(index);
            if (!storageKey?.startsWith(mapViewPreferencePrefix)) continue;
            let stored;
            try { stored = JSON.parse(sessionStorage.getItem(storageKey) || "{}"); } catch { continue; }
            if (!stored || typeof stored !== "object" || Array.isArray(stored)) continue;
            const layers = {};
            for (const layer of ["image", "linked", "unlinked"]) if (typeof stored[layer] === "boolean") layers[layer] = stored[layer];
            if (Object.keys(layers).length) mapLayerPreferences.set(storageKey.slice(mapViewPreferencePrefix.length), layers);
        }
    } catch { }
    apply();
})();
