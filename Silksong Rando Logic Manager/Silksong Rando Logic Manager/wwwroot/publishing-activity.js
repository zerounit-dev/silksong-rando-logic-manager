// Page-local native dialog lifetime. The browser owns focus containment and Escape.
export function create(dialog, launcher) {
    let disposed = false;
    let backdropPointer = null;
    const outside = event => {
        const box = dialog.getBoundingClientRect();
        return event.target === dialog && (event.clientX < box.left || event.clientX > box.right ||
            event.clientY < box.top || event.clientY > box.bottom);
    };
    const down = event => {
        backdropPointer = event.isPrimary && event.button === 0 && outside(event) ? event.pointerId : null;
    };
    const click = event => {
        const dismiss = backdropPointer !== null && event.button === 0 && outside(event);
        backdropPointer = null;
        if (dismiss) {
            event.preventDefault();
            event.stopPropagation();
            dialog.close();
        }
    };
    const cancelPointer = () => { backdropPointer = null; };
    const closed = () => {
        backdropPointer = null;
        if (!disposed && launcher.isConnected) launcher.focus({ preventScroll: true });
    };
    dialog.addEventListener("pointerdown", down);
    dialog.addEventListener("click", click);
    dialog.addEventListener("pointercancel", cancelPointer);
    dialog.addEventListener("close", closed);
    return {
        open() {
            if (disposed || !dialog.isConnected || dialog.open) return;
            launcher.focus({ preventScroll: true });
            dialog.showModal();
        },
        dispose() {
            disposed = true;
            dialog.removeEventListener("pointerdown", down);
            dialog.removeEventListener("click", click);
            dialog.removeEventListener("pointercancel", cancelPointer);
            dialog.removeEventListener("close", closed);
            if (dialog.open) dialog.close();
        }
    };
}
