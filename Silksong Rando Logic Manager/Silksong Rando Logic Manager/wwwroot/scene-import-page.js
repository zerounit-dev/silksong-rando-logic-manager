// Yield one paint opportunity before deliberately blocking main-thread processing.
export function allowPreparationPaint() {
    return new Promise(resolve => requestAnimationFrame(() => setTimeout(resolve, 0)));
}

export function create(root, inputId) {
    const picker = root.querySelector(`#${inputId}`);
    if (!(picker instanceof HTMLInputElement)) throw new Error('Scene picker is not mounted.');
    let disposed = false;
    let initialPickerRequested = false;
    const drag = event => { if (event.dataTransfer?.types.includes('Files')) event.preventDefault(); };
    const drop = event => {
        event.preventDefault();
        if (picker.disabled || !event.dataTransfer?.files?.length) return;
        const transfer = new DataTransfer();
        transfer.items.add(event.dataTransfer.files[0]);
        picker.files = transfer.files;
        picker.dispatchEvent(new Event('change', { bubbles: true }));
    };
    root.addEventListener('dragover', drag);
    root.addEventListener('drop', drop);
    root.querySelector('h1')?.focus();
    return {
        requestInitialPicker() {
            if (initialPickerRequested || disposed || !root.isConnected || !picker.isConnected || picker.disabled) return;
            initialPickerRequested = true;
            try {
                if (typeof picker.showPicker === 'function') picker.showPicker();
                else picker.click();
            } catch (error) {
                // Automatic entry need not have transient user activation. Do not
                // retry with click() after a denied showPicker() request.
                if (!(error instanceof DOMException && error.name === 'NotAllowedError')) throw error;
            }
        },
        resetSelection() { picker.value = ''; },
        focusPicker(reset = true) { if (reset) picker.value = ''; requestAnimationFrame(() => picker.focus()); },
        dispose() { disposed = true; root.removeEventListener('dragover', drag); root.removeEventListener('drop', drop); }
    };
}
