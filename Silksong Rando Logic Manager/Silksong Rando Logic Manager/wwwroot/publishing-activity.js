// Resolve each instant in the browser's local zone, keeping the compact date on both surfaces.
export function formatTimes(instants, hasLatest) {
    const formatter = new Intl.DateTimeFormat("en-GB", {
        year: "numeric", month: "2-digit", day: "2-digit",
        hour: "2-digit", minute: "2-digit", hourCycle: "h23"
    });
    return instants.map((instant, index) => {
        const parts = Object.fromEntries(formatter.formatToParts(new Date(instant))
            .map(({ type, value }) => [type, value]));
        const { year, month, day, hour, minute } = parts;
        if (!year || !month || !day || !hour || !minute) return null;
        const date = `${year.padStart(4, "0")}-${month}-${day}`;
        if (hasLatest && index === 0) {
            const hour24 = Number(hour);
            return `${date} ${hour24 % 12 || 12}:${minute} ${hour24 < 12 ? "AM" : "PM"}`;
        }
        return `${date} ${hour}:${minute}`;
    });
}

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
