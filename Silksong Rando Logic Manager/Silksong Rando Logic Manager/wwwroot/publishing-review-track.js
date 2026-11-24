// Scroll geometry only. Text stays on the circuit until its viewport is admitted.
export function observe(main, owner) {
    const host = main.closest(".tools-content");
    const lifetime = new AbortController();
    const signal = lifetime.signal;
    const addedId = !host.id;
    const originalOverflowAnchor = host.style.overflowAnchor;
    if (addedId) host.id = "publishing-scroll-surface";
    let root, band, bar, revision = -1, total = 0, base = 0, lastAnchor, pendingMap;
    let frame = 0, inFlight = false, lastRead, drag, focusKey;
    let absoluteIntent = 0;
    let widthDirty = true;
    let canvasX = host.scrollLeft, canvasInset;
    const horizontal = new Map();
    const mountedPans = new Map();
    const horizontalCap = 262144;
    const measured = new Map();
    const observed = new Set();
    let glass;
    const clamp = (value, a, b) => Math.max(a, Math.min(b, value));
    const hostOrigin = () => host.getBoundingClientRect().top + host.clientTop;
    const hostLeft = () => host.getBoundingClientRect().left + host.clientLeft;
    const topOf = element => host.scrollTop + element.getBoundingClientRect().top - hostOrigin();
    const origin = () => topOf(band);
    const logicalTop = () => host.scrollTop - origin() + base;
    const travel = () => band ? Math.max(0, total - band.offsetHeight) : 0;
    const nativeMax = () => Math.max(0, host.scrollHeight - host.clientHeight);
    const absoluteY = () => host.scrollTop + base;
    const absoluteMax = () => nativeMax() + travel();
    function syncPans(measure, inset) {
        const active = new Set((root.dataset.reviewWidthOwners ?? "").split(" "));
        for (const [key, state] of horizontal) if (!active.has(state.owner)) horizontal.delete(key);
        mountedPans.clear();
        canvasX = host.scrollLeft;
        canvasInset = inset;
        const left = hostLeft(), right = left + host.clientWidth - (parseFloat(inset) || 0);
        for (const box of root.querySelectorAll("[data-review-width-owner]")) {
            const owner = box.dataset.reviewWidthOwner, key = box.dataset.reviewKey;
            const id = `${owner}/${key}`;
            const pan = box.querySelector("[data-review-pan]");
            const track = pan.firstElementChild;
            const rail = box.querySelector("[data-review-rail]");
            const thumb = box.querySelector("[data-review-x-thumb]");
            const rect = box.getBoundingClientRect();
            // Only trim the right edge where the file is clipped. Its left edge
            // remains in normal flow, including when it is offscreen to the left.
            const usable = rect.width > 0 && rect.right > left && rect.left < right;
            if (usable && rect.right > right) box.style.setProperty("--review-visible-width", `${right - rect.left}px`);
            else box.style.removeProperty("--review-visible-width");
            const state = horizontal.get(id) ?? { owner, key, offset: 0, base: 0, measured: 0, cell: 0, travel: 0 };
            const columns = Number(box.dataset.reviewOutlierColumns);
            if (measure) {
                const probe = track.querySelector("[data-review-cell-probe]");
                const cell = probe ? probe.getBoundingClientRect().width / 100 : 0;
                const logical = columns ? (columns + 4) * cell : 0;
                if (state.cell !== cell || !track.hasAttribute("data-review-windowed")) state.measured = 0;
                if (track.hasAttribute("data-review-windowed")) {
                    // Keep range from observed rows that can leave the Y window,
                    // and reserve the capped width for a sliced exceptional row.
                    const origin = track.getBoundingClientRect().left;
                    for (const line of track.querySelectorAll("[data-review-inline]"))
                        state.measured = Math.max(state.measured, line.getBoundingClientRect().right - origin);
                    track.style.setProperty("--review-retained-width", `${Math.max(state.measured, Math.min(logical, horizontalCap))}px`);
                } else track.style.removeProperty("--review-retained-width");
                state.cell = cell;
                state.travel = Math.max(0, logical - horizontalCap);
            }
            const { travel } = state;
            const physical = pan.scrollWidth, client = pan.clientWidth;
            rail.firstElementChild.style.width = `${physical}px`;
            rail.hidden = !(usable && client && !travel && physical > client);
            if (thumb) thumb.hidden = !usable || !client || !travel;
            rail.tabIndex = rail.hidden ? -1 : 0;
            Object.assign(state, { pan, rail, track, thumb, columns, client, usable: usable && client > 0 });
            horizontal.set(id, state);
            mountedPans.set(pan, state);
            if (!rail.hidden) mountedPans.set(rail, state);
            if (state.usable) {
                // A wider viewport can clamp native X before its scroll event.
                // Restore the stored local X within the new geometry, never
                // adding the canvas motion or feeding it back into Tools.
                const local = state.offset - state.base;
                if (state.base > travel || local < 0 || local > physical - client) seekX(state, state.offset);
                else setLocalX(state, local);
            }
        }
        for (const state of horizontal.values()) {
            if (!mountedPans.has(state.pan)) state.pan = state.rail = state.track = state.thumb = null;
        }
    }

    const maxX = state => Math.max(0, state.pan.scrollWidth - state.pan.clientWidth + state.travel);
    function seekX(state, x) {
        x = clamp(x, 0, maxX(state));
        state.base = clamp(x - horizontalCap / 2, 0, state.travel);
        setLocalX(state, x - state.base);
    }

    function setLocalX(state, x) {
        if (state.pan.scrollLeft !== x) state.pan.scrollLeft = x;
        state.offset = state.pan.scrollLeft + state.base;
        if (!state.rail.hidden && state.rail.scrollLeft !== state.pan.scrollLeft) state.rail.scrollLeft = state.pan.scrollLeft;
    }

    function positionX(state) {
        const { pan, thumb, travel } = state;
        if (travel) {
            const edge = horizontalCap / 4;
            if ((state.base > 0 && pan.scrollLeft < edge) ||
                (state.base < travel && pan.scrollLeft + pan.clientWidth > horizontalCap - edge)) seekX(state, state.offset);
            const max = maxX(state);
            const size = Math.min(pan.clientWidth, Math.max(28, pan.clientWidth ** 2 / Math.max(1, max + pan.clientWidth)));
            thumb.firstElementChild.style.width = `${size}px`;
            thumb.firstElementChild.style.left = `${max ? state.offset / max * (pan.clientWidth - size) : 0}px`;
            thumb.setAttribute("aria-valuemax", Math.round(max));
            thumb.setAttribute("aria-valuenow", Math.round(state.offset));
        }
        positionStrips(state);
    }

    function positionStrips({ pan, cell: width, track, base: xbase }) {
        if (!width) return;
        const right = track.getBoundingClientRect().right;
        const surface = pan.getBoundingClientRect().left + pan.clientLeft;
        const positions = [];
        for (const strip of pan.querySelectorAll("[data-review-strip]")) {
            const start = strip.parentElement.getBoundingClientRect().left;
            const room = Math.max(0, right - start);
            const pixels = surface - start;
            const column = (pixels + xbase) / width;
            let left = 0, covered = false;
            const textEnd = Number(strip.dataset.reviewTextEnd);
            const end = Number(strip.dataset.reviewEnd);
            const cue = strip.querySelector("[data-review-ending]");
            const stripBounds = strip.getBoundingClientRect();
            if (cue && column >= textEnd && column < end) {
                const bounds = cue.getBoundingClientRect();
                left = pixels - (bounds.left - stripBounds.left) - bounds.width * (column - textEnd) / (end - textEnd);
                covered = true;
            } else if (column < textEnd) {
                for (const piece of strip.querySelectorAll("[data-review-from]")) {
                    const from = Number(piece.dataset.reviewFrom), to = Number(piece.dataset.reviewTo);
                    if (column < from && from === 0) { left = -xbase; covered = true; break; }
                    if (column < from || column >= to) continue;
                    // Measured glyph advance, not a cell-count assumption, places
                    // the active source interval. Adjacent pieces stay inline.
                    const bounds = piece.getBoundingClientRect();
                    left = pixels - (bounds.left - stripBounds.left) - bounds.width * (column - from) / (to - from);
                    covered = true;
                    break;
                }
                // Fit the terminal glyph/cue when this row's logical end enters
                // the viewport. This is per row, not just the longest open row.
                const terminal = cue || (Number(strip.lastElementChild?.dataset.reviewTo) === textEnd && end === textEnd);
                const edge = Math.min(pixels + pan.clientWidth, room);
                if (covered && terminal && (edge + xbase) / width >= end)
                    left = Math.min(left, edge - stripBounds.width - 1);
            }
            positions.push({ strip, left, show: covered && left > -40000 && left < room });
        }
        for (const { strip, left, show } of positions) {
            strip.style.left = `${show ? left : 0}px`;
            strip.toggleAttribute("data-review-painted", show);
        }
    }

    function rememberAnchor(anchor) {
        lastAnchor = { from: revision, anchor, intent: absoluteIntent, physical: host.scrollTop, motion: 0 };
    }

    function sampleMotion(position = pendingMap ?? lastAnchor) {
        if (!position || position.intent !== absoluteIntent) return;
        const physical = host.scrollTop;
        // Clamp the old sample to the current native range before measuring
        // motion. A forced move to the new boundary is not reading intent.
        position.motion += physical - clamp(position.physical, 0, nativeMax());
        position.physical = physical;
    }

    function seekY(value, relativeInput = false) {
        const position = pendingMap ?? lastAnchor;
        sampleMotion(position);
        const before = absoluteY();
        const max = nativeMax(), extra = travel();
        const target = clamp(value, 0, max + extra);
        // Center when possible, but choose a base that leaves a reachable
        // native offset. Home/End force base 0/extra even before first paint.
        base = extra ? clamp(Math.round(target - origin() - band.offsetHeight / 2),
            Math.max(0, target - max), Math.min(target, extra)) : 0;
        host.scrollTop = target - base;
        if (position && position.intent === absoluteIntent) {
            // A relative thumb key can move only the base. Count its logical
            // delta once, and absorb our own scrollTop write in the baseline.
            if (relativeInput) position.motion += absoluteY() - before;
            position.physical = host.scrollTop;
        }
        positionWindow();
    }

    function normalizeY() {
        const physical = host.scrollTop;
        const native = clamp(physical, 0, nativeMax());
        if (base >= 0 && base <= travel() && physical === native) return;
        // Geometry can shrink while the same band node and its old base
        // survive. Re-express the position in the current range, preserving
        // logical Y where possible. seekY samples prior motion before the
        // write and absorbs the write without charging it as relative input.
        seekY(native + base);
    }

    function seekRelativeY(delta) {
        normalizeY();
        seekY(absoluteY() + delta, true);
    }

    const seek = y => seekY(origin() + y);

    function seekAbsolute(value) {
        absoluteIntent++;
        pendingMap = lastAnchor = null;
        seekY(value);
    }

    function positionWindow() {
        glass = band?.querySelector("[data-review-origin]");
        if (glass) {
            const offset = Number(glass.dataset.reviewOrigin) - base;
            // Never write a logical-distance CSS position to an off-band window.
            const top = logicalTop();
            const overlaps = top < Number(glass.dataset.reviewLastY) && top + host.clientHeight > Number(glass.dataset.reviewFirstY);
            glass.toggleAttribute("data-review-active", overlaps && Math.abs(offset) <= band.offsetHeight + 40000);
            glass.style.top = `${clamp(offset, -40000, band.offsetHeight + 40000)}px`;
        }
    }

    function positionBar() {
        const rect = host.getBoundingClientRect();
        const nativeGutter = host.offsetWidth - host.clientWidth - host.clientLeft;
        const gutter = Math.max(14, nativeGutter);
        bar.hidden = travel() <= 0;
        if (bar.hidden) main.style.removeProperty("--publishing-scroll-inset");
        else {
            main.style.setProperty("--publishing-scroll-inset", `${gutter - nativeGutter}px`);
            bar.style.cssText = `left:${rect.right - gutter}px;top:${hostOrigin()}px;width:${gutter}px;height:${host.clientHeight}px`;
            bar.setAttribute("aria-controls", host.id);
            const max = absoluteMax();
            const value = clamp(host.scrollTop + base, 0, max);
            const height = Math.min(host.clientHeight, Math.max(28, host.clientHeight ** 2 / Math.max(1, max + host.clientHeight)));
            const thumb = bar.firstElementChild;
            thumb.style.height = `${height}px`;
            thumb.style.top = `${max ? value / max * (host.clientHeight - height) : 0}px`;
            bar.setAttribute("aria-valuemax", Math.round(max));
            bar.setAttribute("aria-valuenow", Math.round(value));
            bar.setAttribute("aria-valuetext", `${max ? (value / max * 100).toFixed(1) : 0}% through publication review`);
        }

    }

    function captureNative() {
        const start = topOf(root), y = host.scrollTop;
        if (y < start) return { key: null, row: 0, offset: 0, position: y - start };
        const end = start + root.offsetHeight;
        if (y >= end) return { key: null, row: 0, offset: 0, position: total + y - end };
        let marker, before = -Infinity;
        const z = hostOrigin();
        for (const node of root.querySelectorAll("[data-review-key]")) {
            const rect = node.getBoundingClientRect();
            if (rect.top <= z && rect.top >= before) { before = rect.top; marker = node; }
        }
        if (!marker) return { key: null, row: 0, offset: 0, position: 0 };
        const offset = Math.max(0, z - before);
        const rows = Number(marker.dataset.reviewRowCount);
        const row = rows ? Math.min(rows - 1, Math.floor(offset / 22)) : 0;
        return { key: marker.dataset.reviewKey, row, offset: offset - row * 22, position: y - start };
    }

    function restoreNative(anchor, motion) {
        if (anchor.key) {
            const marker = root.querySelector(`[data-review-key="${CSS.escape(anchor.key)}"]`);
            if (marker) { seekY(topOf(marker) + anchor.row * 22 + anchor.offset + motion); return; }
        }
        seekY(topOf(root) + (anchor.position >= total ? root.offsetHeight + anchor.position - total : anchor.position) + motion);
    }

    const resize = new ResizeObserver(entries => {
        for (const { target } of entries) {
            if (target === host) widthDirty = true;
            const key = target.dataset.reviewMeasure;
            if (key && target.isConnected && target.closest("[data-review-root]") === root && Number(root.dataset.reviewRoot) === revision)
                measured.set(key, Math.ceil(target.getBoundingClientRect().height));
        }
        schedule();
    });

    function observeWindow() {
        const current = new Set(band?.querySelectorAll("[data-review-measure]") ?? []);
        for (const node of observed) if (!current.has(node)) { resize.unobserve(node); observed.delete(node); }
        for (const node of current) if (!observed.has(node)) { resize.observe(node); observed.add(node); }
    }

    function captureBand() {
        const top = logicalTop();
        for (const node of glass?.children ?? []) {
            const start = Number(node.dataset.reviewStart);
            if (top < start || top >= start + node.offsetHeight) continue;
            const within = top - start;
            const rows = !node.dataset.reviewMeasure;
            const count = Number(node.firstElementChild?.dataset.reviewRowCount);
            const increment = rows && count ? Math.min(count - 1, Math.floor(within / 22)) : 0;
            return { key: node.dataset.reviewSpan, row: Number(node.dataset.reviewFirst) + increment,
                offset: within - increment * 22, position: top };
        }
        return { key: null, row: 0, offset: 0, position: top };
    }

    function syncRoot() {
        const next = main.querySelector("[data-review-root]");
        const nextRevision = next ? Number(next.dataset.reviewRoot) : -1;
        if (next === root && nextRevision === revision) return;
        const sameReview = root === next;
        const position = sameReview ? pendingMap ?? lastAnchor : null;
        // Sample before changing representations, even if the DOM already has
        // the new revision. Keep the original map source and accumulated input.
        if (position) sampleMotion(position);
        const oldBand = band;
        root = next;
        revision = nextRevision;
        band = root?.querySelector(".publishing-review-band");
        if (!band || oldBand !== band) base = 0;
        bar = root?.querySelector(".publishing-review-scrollbar-y");
        total = Number(root?.dataset.reviewTotal ?? 0);
        host.style.overflowAnchor = band ? "none" : originalOverflowAnchor;
        if (!band) main.style.removeProperty("--publishing-scroll-inset");
        widthDirty = true;
        if (!sameReview) { base = 0; focusKey = null; horizontal.clear(); mountedPans.clear(); }
        pendingMap = position?.intent === absoluteIntent ? position : null;
        lastAnchor = lastRead = null;
        measured.clear();
        observeWindow();
    }

    function restoreFocus() {
        if (!focusKey || document.activeElement !== document.body) return;
        const marker = root.querySelector(`[data-review-key="${CSS.escape(focusKey)}"]`);
        const item = marker?.querySelector("[data-review-pan]") ?? marker;
        if (item) item.focus({ preventScroll: true });
        else if (bar && !bar.hidden) bar.focus({ preventScroll: true });
    }

    function remapGeometry(position, changes) {
        let delta = 0;
        for (const { start, before, after } of changes ?? []) {
            if (position < start) break;
            if (position < start + before) return start + delta + Math.min(position - start, after - 1);
            delta += after - before;
        }
        return position + delta;
    }

    async function pump() {
        frame = 0;
        if (signal.aborted) return;
        syncRoot();
        if (!root) return;
        normalizeY();
        sampleMotion();
        if (band) {
            observeWindow();
            if (!pendingMap) {
                const local = host.scrollTop - origin();
                const edge = band.offsetHeight / 4;
                if ((base > 0 && local < edge) || (base < travel() && local + host.clientHeight > band.offsetHeight - edge))
                    seek(local + base);
            }
            positionWindow();
            positionBar();
            if (!pendingMap) rememberAnchor(captureBand());
        } else if (!pendingMap) {
            rememberAnchor(captureNative());
            restoreFocus();
        }
        const inset = main.style.getPropertyValue("--publishing-scroll-inset");
        if (widthDirty || canvasX !== host.scrollLeft || canvasInset !== inset) {
            const measure = widthDirty;
            widthDirty = false;
            syncPans(measure, inset);
        }
        for (const [element, state] of mountedPans) if (element === state.pan && state.columns) positionX(state);
        if (inFlight) return;
        const stamp = revision;
        if (pendingMap) {
            const position = pendingMap;
            const { from, anchor, intent } = position;
            {
                inFlight = true;
                try {
                    const mapped = await owner.invokeMethodAsync("MapReviewAnchor", stamp, from, anchor);
                    if (signal.aborted || !mapped || pendingMap !== position || intent !== absoluteIntent ||
                        !root?.isConnected || stamp !== Number(root.dataset.reviewRoot)) return;
                    sampleMotion(position);
                    pendingMap = null;
                    total = mapped.total;
                    const { motion } = position;
                    if (band) { seek(mapped.anchor.position + motion); rememberAnchor(captureBand()); }
                    else { restoreNative(mapped.anchor, motion); rememberAnchor(captureNative()); restoreFocus(); }
                } catch (error) { if (!signal.aborted) console.error("Unable to position publishing review", error); }
                finally {
                    inFlight = false;
                    if (pendingMap === position && stamp === Number(root?.dataset.reviewRoot)) pendingMap = null;
                    schedule();
                }
                return;
            }
        }
        if (!band) return;
        const top = logicalTop(), height = host.clientHeight;
        const viewportTop = hostOrigin(), bottom = viewportTop + height;
        const columns = [];
        for (const [element, state] of mountedPans) {
            if (element !== state.pan || !state.columns || !state.cell) continue;
            if (![...state.pan.querySelectorAll("[data-review-outlier-row]")].some(row => {
                const rect = row.getBoundingClientRect();
                return rect.bottom > viewportTop && rect.top < bottom;
            })) continue;
            columns.push({ key: state.key, left: Math.max(0, Math.floor(state.offset / state.cell) - 16),
                count: Math.ceil(state.pan.clientWidth / state.cell) });
        }
        const horizontalChanged = columns.some(({ key, left, count }) => {
            const prior = lastRead?.columns.find(x => x.key === key);
            return !prior || Math.abs(prior.left - left) >= 96 || prior.count !== count;
        });
        const geometry = [...measured].filter(([key, size]) => size !== lastRead?.sizes.get(key));
        measured.clear();
        if (!geometry.length && lastRead && Math.abs(lastRead.top - top) < 440 && height === lastRead.height &&
            !horizontalChanged) { restoreFocus(); return; }
        const sizes = lastRead?.sizes ?? new Map();
        const intent = absoluteIntent;
        inFlight = true;
        let follow = false;
        try {
            const result = await owner.invokeMethodAsync("ReadReviewViewport", stamp, top, height, columns, geometry.map(([key, height]) => ({ key, height })));
            if (signal.aborted || !result?.mounted || !root?.isConnected || stamp !== Number(root.dataset.reviewRoot)) return;
            follow = true;
            const now = logicalTop();
            const newerSeek = intent !== absoluteIntent;
            const seekAtEnd = absoluteY() >= absoluteMax() - 1 && (newerSeek || result.geometry?.length);
            const target = newerSeek ? now : remapGeometry(now, result.geometry);
            for (const [key, size] of geometry) sizes.set(key, size);
            total = result.total;
            if (result.geometry?.length) band.style.height = `${Math.min(total, Number(band.dataset.reviewBandLimit))}px`;
            if (seekAtEnd) seek(absoluteMax() - origin());
            else if (target !== logicalTop() || base > travel()) seek(target);
            lastRead = { top: result.anchor.position, height, columns, sizes };
            positionWindow();
            rememberAnchor(captureBand());
            restoreFocus();
        } catch (error) { if (!signal.aborted) console.error("Unable to load publishing review viewport", error); }
        finally { inFlight = false; if (follow || stamp !== Number(root?.dataset.reviewRoot)) schedule(); }
    }

    function schedule() { if (!frame && !signal.aborted) frame = requestAnimationFrame(pump); }

    const mutation = new MutationObserver(() => { widthDirty = true; schedule(); });
    mutation.observe(main, { childList: true, subtree: true, attributes: true,
        attributeFilter: ["data-review-root", "data-review-origin", "data-review-first-y", "data-review-last-y"] });
    resize.observe(host);
    host.addEventListener("scroll", () => {
        syncRoot();
        const position = pendingMap ?? lastAnchor;
        if (!widthDirty && canvasX === host.scrollLeft && position?.physical === host.scrollTop) return;
        sampleMotion(position);
        schedule();
    }, { passive: true, signal });
    main.addEventListener("scroll", event => {
        const state = mountedPans.get(event.target);
        if (!state?.usable) return;
        // Resize can clamp one surface before the next layout sync. Don't
        // copy that clamp to the other surface as though it were local input.
        if (state.pan.clientWidth !== state.client) { widthDirty = true; schedule(); return; }
        const from = event.target;
        const to = from === state.pan ? state.rail : state.pan;
        if (!state.rail.hidden && to.scrollLeft !== from.scrollLeft) to.scrollLeft = from.scrollLeft;
        state.offset = state.pan.scrollLeft + state.base;
        if (state.columns) schedule();
    }, { passive: true, capture: true, signal });
    host.addEventListener("focusin", event => { focusKey = event.target.closest("[data-review-key]")?.dataset.reviewKey; }, { signal });
    // Wheel, trackpad, and touch panning remain native. The replacement control
    // handles only explicit absolute seeks and keyboard operations on its thumb.
    host.addEventListener("keydown", event => {
        if (event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey ||
            event.target.closest("input,textarea,select,[contenteditable],dialog")) return;
        syncRoot();
        const surface = event.target.closest("[data-review-pan],[data-review-rail]");
        if (surface && (event.key === "ArrowLeft" || event.key === "ArrowRight")) {
            // Do not hand an exhausted local horizontal axis to the Tools canvas.
            event.preventDefault();
            const delta = event.key === "ArrowLeft" ? -40 : 40;
            const state = mountedPans.get(surface);
            if (state?.travel) { seekX(state, state.offset + delta); schedule(); }
            else surface.scrollLeft += delta;
            return;
        }
        const local = [...mountedPans.values()].find(state => state.thumb === event.target && state.travel);
        if (local) {
            const at = local.offset, max = maxX(local);
            const action = { Home: 0, End: max, ArrowLeft: at - 40, ArrowRight: at + 40,
                PageUp: at - local.pan.clientWidth, PageDown: at + local.pan.clientWidth }[event.key];
            if (typeof action === "number") { event.preventDefault(); seekX(local, action); schedule(); }
            return;
        }
        if (!root) return;
        if (event.key === "Home" || event.key === "End") {
            event.preventDefault();
            seekAbsolute(event.key === "Home" ? 0 : absoluteMax());
            schedule();
            return;
        }
        if (!bar || event.target !== bar || !travel()) return;
        const delta = { ArrowUp: -22, ArrowDown: 22,
            PageUp: -host.clientHeight, PageDown: host.clientHeight, " ": host.clientHeight }[event.key];
        if (typeof delta !== "number") return;
        event.preventDefault();
        seekRelativeY(delta);
        schedule();
    }, { signal });
    main.addEventListener("pointerdown", event => {
        const hovered = event.target.closest(".publishing-review-scrollbar,[data-review-x-thumb]");
        if (!hovered || event.button !== 0) return;
        const horizontal = [...mountedPans.values()].find(state => state.thumb === hovered);
        event.preventDefault();
        hovered.focus({ preventScroll: true });
        const thumb = hovered.firstElementChild.getBoundingClientRect();
        const offset = event.target === hovered.firstElementChild ? (horizontal ? event.clientX - thumb.left : event.clientY - thumb.top)
            : (horizontal ? thumb.width : thumb.height) / 2;
        drag = { id: event.pointerId, offset, horizontal };
        hovered.setPointerCapture(event.pointerId);
        dragTo(event);
    }, { signal });
    main.addEventListener("pointermove", dragTo, { signal });
    for (const name of ["lostpointercapture", "pointerup", "pointercancel"])
        main.addEventListener(name, event => { if (drag?.id === event.pointerId) drag = null; }, { signal });

    function dragTo(event) {
        if (!bar || drag?.id !== event.pointerId) return;
        if (drag.horizontal) {
            const state = drag.horizontal;
            if (!state.travel || !state.pan?.isConnected) return;
            const thumb = state.thumb;
            const ratio = clamp((event.clientX - thumb.getBoundingClientRect().left - drag.offset) /
                Math.max(1, thumb.clientWidth - thumb.firstElementChild.offsetWidth), 0, 1);
            seekX(state, ratio * maxX(state));
            schedule();
            return;
        }
        const ratio = clamp((event.clientY - bar.getBoundingClientRect().top - drag.offset) /
            Math.max(1, bar.clientHeight - bar.firstElementChild.offsetHeight), 0, 1);
        seekAbsolute(ratio * absoluteMax());
        positionBar();
        schedule();
    }

    schedule();
    return {
        dispose() {
            lifetime.abort();
            mutation.disconnect();
            resize.disconnect();
            if (frame) cancelAnimationFrame(frame);
            host.style.overflowAnchor = originalOverflowAnchor;
            main.style.removeProperty("--publishing-scroll-inset");
            if (addedId) host.removeAttribute("id");
        }
    };
}
