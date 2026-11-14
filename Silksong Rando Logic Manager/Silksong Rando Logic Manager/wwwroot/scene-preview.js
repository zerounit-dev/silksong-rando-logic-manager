const key = Symbol('scene-preview-owner');
export function create(host, root) {
    const handle = host?.querySelector('[data-scene-preview-resize-edge]');
    if (!(host instanceof HTMLElement) || !(handle instanceof HTMLElement) || !(root instanceof HTMLElement) ||
        !root.isConnected || !root.contains(host)) throw new Error('Scene preview is not mounted.');
    root[key]?.dispose();
    let disposed = false, ratio = 5, drag = null, pointer = null;
    const live = () => !disposed && root.isConnected && root[key] === owner;
    const overlay = () => host.querySelector('[data-scene-preview-hover]');
    const clearHover = () => { const circle = overlay(); if (circle) circle.style.display = 'none'; };
    const rowAt = target => target instanceof Element ? target.closest('[data-scene-hover-id]') : null;
    const reconcile = () => {
        if (!live()) return;
        const circle = overlay(), controls = root.querySelector('.scene-workbench-controls');
        if (!pointer || drag || host.hidden || controls?.disabled) { clearHover(); return; }
        const row = rowAt(root.ownerDocument.elementFromPoint(pointer.x, pointer.y));
        if (!circle || !row || !root.contains(row) || row.closest('[hidden]') || !row.getClientRects().length) { clearHover(); return; }
        const xText = row.dataset.sceneHoverX, yText = row.dataset.sceneHoverY;
        const x = Number(xText), y = Number(yText), radius = Number(circle.dataset.sceneHoverRadius);
        if (!row.dataset.sceneHoverId || !xText || !yText || !Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(radius) || radius <= 0) { clearHover(); return; }
        for (const [name, value] of [['cx', x], ['cy', y], ['r', radius]]) {
            if (circle.getAttribute(name) !== String(value)) circle.setAttribute(name, String(value));
        }
        circle.style.display = '';
    };
    const hoverMove = event => { if (live() && event.isPrimary) { pointer = { x: event.clientX, y: event.clientY }; reconcile(); } };
    const hoverOut = event => {
        if (!live() || !event.isPrimary || rowAt(event.target) && rowAt(event.target) === rowAt(event.relatedTarget)) return;
        pointer = event.relatedTarget instanceof Node && root.contains(event.relatedTarget) ? { x: event.clientX, y: event.clientY } : null;
        reconcile();
    };
    const hoverLeave = () => { pointer = null; clearHover(); };
    const observer = new MutationObserver(reconcile);
    const apply = value => host.style.setProperty('--scene-review-ratio-term', String(value));
    const clamp = value => Math.min(9, Math.max(5, value));
    const release = pointerId => {
        if (handle.hasPointerCapture(pointerId)) handle.releasePointerCapture(pointerId);
        window.removeEventListener('keydown', escape);
    };
    const cancel = event => {
        if (!drag || event?.pointerId !== undefined && event.pointerId !== drag.pointerId) return;
        const previous = drag; drag = null; apply(ratio); release(previous.pointerId);
    };
    const escape = event => { if (event.key === 'Escape' && drag) { event.preventDefault(); cancel(); } };
    const ratioFor = event => clamp(16 * (drag.height + event.clientY - drag.y) / drag.width);
    const move = event => { if (!disposed && drag && event.pointerId === drag.pointerId) apply(ratioFor(event)); };
    const end = event => {
        if (disposed || !drag || event.pointerId !== drag.pointerId) return;
        ratio = ratioFor(event); const previous = drag; drag = null; apply(ratio); release(previous.pointerId);
    };
    const down = event => {
        if (!live() || drag || event.button !== 0 || !event.isPrimary || root.querySelector('.scene-workbench-controls')?.disabled || host.hidden) return;
        const bounds = host.getBoundingClientRect(); if (bounds.width <= 0) return;
        event.preventDefault(); handle.setPointerCapture(event.pointerId);
        drag = { pointerId: event.pointerId, y: event.clientY, height: bounds.height, width: bounds.width };
        hoverLeave(); window.addEventListener('keydown', escape);
    };
    handle.addEventListener('pointerdown', down); handle.addEventListener('pointermove', move);
    handle.addEventListener('pointerup', end); handle.addEventListener('pointercancel', cancel); handle.addEventListener('lostpointercapture', cancel);
    apply(5);
    const owner = {
        reconcile,
        dispose() {
            if (disposed) return;
            try { cancel(); }
            finally {
                disposed = true; window.removeEventListener('keydown', escape); hoverLeave(); observer.disconnect();
                root.removeEventListener('pointerover', hoverMove); root.removeEventListener('pointermove', hoverMove);
                root.removeEventListener('pointerout', hoverOut); root.removeEventListener('pointerleave', hoverLeave);
                root.removeEventListener('pointercancel', hoverLeave); root.removeEventListener('scroll', reconcile, true);
                handle.removeEventListener('pointerdown', down); handle.removeEventListener('pointermove', move);
                handle.removeEventListener('pointerup', end); handle.removeEventListener('pointercancel', cancel); handle.removeEventListener('lostpointercapture', cancel);
                if (root[key] === owner) delete root[key];
            }
        }
    };
    root[key] = owner;
    root.addEventListener('pointerover', hoverMove); root.addEventListener('pointermove', hoverMove);
    root.addEventListener('pointerout', hoverOut); root.addEventListener('pointerleave', hoverLeave);
    root.addEventListener('pointercancel', hoverLeave); root.addEventListener('scroll', reconcile, true);
    observer.observe(root, { childList: true, subtree: true, attributes: true,
        attributeFilter: ['hidden', 'disabled', 'data-scene-hover-id', 'data-scene-hover-x', 'data-scene-hover-y', 'data-scene-hover-radius'] });
    return owner;
}
