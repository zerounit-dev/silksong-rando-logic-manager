window.requirementCatalogueDrag = (() => {
    let dotnet, active, handlers, definition, definitionBackdropPointer, modalInitiator;
    const cleanup = () => { if (active) active.row.classList.remove("catalogue-dragging"); document.querySelectorAll(".catalogue-drop-before").forEach(x => x.classList.remove("catalogue-drop-before")); active = null; };
    const dispose = () => { cleanup(); definition = null; definitionBackdropPointer = null; if (!handlers) return; Object.entries(handlers).forEach(([name, handler]) => document.removeEventListener(name, handler)); handlers = null; dotnet = null; };
    const initialize = reference => {
        dispose(); dotnet = reference;
        handlers = {
            dragstart: event => { const grip = event.target.closest("[data-requirement-catalogue-grip]"); if (!grip || grip.disabled) return; const row = grip.closest("[data-requirement-catalogue-row]"); active = { row, kind: row.dataset.catalogueKind }; event.dataTransfer.effectAllowed = "move"; event.dataTransfer.setData("text/plain", row.dataset.catalogueId); row.classList.add("catalogue-dragging"); },
            dragover: event => { if (!active) return; const row = event.target.closest("[data-requirement-catalogue-row], [data-requirement-catalogue-tail]"); if (!row || (row.dataset.catalogueKind && row.dataset.catalogueKind !== active.kind) || (row.dataset.requirementCatalogueTail && row.dataset.requirementCatalogueTail !== active.kind)) return; event.preventDefault(); document.querySelectorAll(".catalogue-drop-before").forEach(x => x.classList.remove("catalogue-drop-before")); row.classList.add("catalogue-drop-before"); },
            drop: async event => { if (!active) return; const row = event.target.closest("[data-requirement-catalogue-row], [data-requirement-catalogue-tail]"); if (!row || (row.dataset.catalogueKind && row.dataset.catalogueKind !== active.kind) || (row.dataset.requirementCatalogueTail && row.dataset.requirementCatalogueTail !== active.kind)) { cleanup(); return; } event.preventDefault(); const rows = [...row.closest("tbody").querySelectorAll("[data-requirement-catalogue-row]")]; const source = rows.indexOf(active.row), target = row.dataset.requirementCatalogueTail ? rows.length - 1 : rows.indexOf(row); const targetIndex = source < target ? target - 1 : target; const id = active.row.dataset.catalogueId, kind = active.kind; cleanup(); await dotnet.invokeMethodAsync("Drop", kind, id, targetIndex); },
            dragend: cleanup,
            keydown: event => { if (event.key === "Escape") cleanup(); }
        };
        Object.entries(handlers).forEach(([name, handler]) => document.addEventListener(name, handler));
    };
    const openImportPicker = id => { const input = document.getElementById(id); input.value = ""; input.click(); };
    const focusModal = () => { modalInitiator = document.activeElement instanceof HTMLElement ? document.activeElement : null; document.querySelector(".requirement-syntax-guide-modal, .requirement-catalogue-backdrop [role=dialog]")?.focus(); };
    const restoreModalFocus = () => { const initiator = modalInitiator; modalInitiator = null; requestAnimationFrame(() => { if (initiator?.isConnected) initiator.focus(); }); };
    const focusField = id => document.getElementById(id)?.focus();
    const field = (modal, name) => modal.querySelector(`[data-requirement-definition-field="${name}"]`);
    const value = (modal, name) => field(modal, name)?.value ?? "";
    const collapseWhitespace = text => [...text].reduce((result, character) => {
        if (/\s/u.test(character)) return result.length === 0 || result.endsWith(" ") ? result : `${result} `;
        return `${result}${character}`;
    }, "").trim();
    const normalizeIdentification = text => collapseWhitespace([...text].filter(character => /[\p{L}\p{Nd}\s]/u.test(character)).join("")).toLocaleLowerCase();
    const mirroredAlias = text => normalizeIdentification(text);
    const aliasValidation = aliases => {
        const parsed = aliases.split(",").map(collapseWhitespace).filter(Boolean);
        if (parsed.length === 0) return "At least one alias is required.";
        if (parsed.some(alias => [...alias].some(character => !/[\p{L}\p{Nd}\s]/u.test(character)))) return "Aliases may contain only Unicode letters, decimal digits, and whitespace.";
        const identities = new Set();
        if (parsed.some(alias => identities.has(normalizeIdentification(alias)) || !identities.add(normalizeIdentification(alias)))) return "An alias is repeated in this definition.";
        return null;
    };
    const outputValidation = (syntax, output) => {
        if (!output.trim()) return "Output syntax is required.";
        const required = {
            "{predicate}": [], "{difficulty?} {predicate}": ["difficulty"], "{difficulty} {predicate}": ["difficulty"],
            "{predicate} {direction}": ["direction"], "{predicate} {item}": ["item"], "{predicate} {check}": ["check"], "{predicate} {quantity}": ["quantity"]
        }[syntax];
        if (!required) return "Input syntax is not supported.";
        const tokens = [];
        for (let index = 0; index < output.length; index++) {
            if (output[index] === "}") return "Output syntax has an unmatched closing brace.";
            if (output[index] !== "{") continue;
            const close = output.indexOf("}", index + 1);
            if (close < 0) return "Output syntax has an unmatched opening brace.";
            const token = output.slice(index + 1, close);
            if (token.includes("{")) return "Output syntax has malformed braces.";
            if (!["difficulty", "direction", "item", "quantity", "check"].includes(token)) return `Output syntax placeholder '{${token}}' is not supported.`;
            tokens.push(token); index = close;
        }
        for (const token of tokens) {
            if (!required.includes(token)) return `Output placeholder '{${token}}' is unavailable for this input syntax.`;
            if (tokens.filter(candidate => candidate === token).length > 1) return `Output placeholder '{${token}}' appears more than once.`;
        }
        const missing = required.find(token => !tokens.includes(token));
        return missing ? `Output placeholder '{${missing}}' is required exactly once.` : null;
    };
    const localMessages = (modal, issues) => {
        modal.querySelectorAll("[data-requirement-definition-local-messages]").forEach(container => {
            const message = issues[container.dataset.requirementDefinitionLocalMessages];
            container.replaceChildren(...(message ? [Object.assign(document.createElement("p"), { className: "requirement-catalogue-message", role: "status", textContent: message })] : []));
        });
    };
    const validateDefinition = modal => {
        const issues = {};
        if (!value(modal, "Name").trim()) issues.Name = "Name is required.";
        const aliasError = aliasValidation(value(modal, "Aliases"));
        if (aliasError) issues.Aliases = aliasError;
        if (modal.dataset.definitionKind === "predicate") {
            const syntax = value(modal, "InputSyntax"), outputError = outputValidation(syntax, value(modal, "OutputSyntax"));
            if (outputError) {
                if (outputError === "Input syntax is not supported.") issues.InputSyntax = outputError;
                else issues.OutputSyntax = outputError;
            }
        } else if (!value(modal, "OutputValue").trim()) issues.OutputValue = "Output value is required.";
        localMessages(modal, issues);
        modal.querySelector('[data-requirement-definition-action="apply"]').disabled = Object.keys(issues).length !== 0 || definition?.inFlightToken !== null;
        return Object.keys(issues).length === 0;
    };
    const command = modal => {
        const common = { id: modal.dataset.definitionId || null, name: value(modal, "Name"), category: value(modal, "Category") || null, aliasDraft: value(modal, "Aliases"), notes: value(modal, "Notes") };
        return modal.dataset.definitionKind === "predicate"
            ? { ...common, inputSyntax: value(modal, "InputSyntax"), outputSyntax: value(modal, "OutputSyntax") }
            : { ...common, outputValue: value(modal, "OutputValue") };
    };
    const initializeDefinitionDraft = sequence => {
        const modal = document.querySelector("[data-requirement-definition]");
        if (!modal) return;
        if (!definition || definition.sequence !== String(sequence)) {
            definition = { sequence: String(sequence), isNew: !modal.dataset.definitionId, aliasTouched: modal.dataset.definitionAliasTouched === "True" || modal.dataset.definitionAliasTouched === "true", inFlightToken: null, nextApplyToken: 0 };
            ["Name", "Category", "Aliases", "Notes", "InputSyntax", "OutputSyntax", "OutputValue"].forEach(name => { const input = field(modal, name); if (input) input.value = modal.dataset[`definition${name}`] ?? ""; });
        }
        validateDefinition(modal);
    };
    document.addEventListener("input", event => {
        const modal = event.target.closest("[data-requirement-definition]");
        if (!modal || !event.target.matches("[data-requirement-definition-field]")) return;
        modal.querySelectorAll("[data-requirement-definition-apply-result]").forEach(result => result.remove());
        if (event.target.dataset.requirementDefinitionField === "Aliases") definition.aliasTouched = true;
        if (event.target.dataset.requirementDefinitionField === "Name" && definition.isNew && !definition.aliasTouched) field(modal, "Aliases").value = mirroredAlias(event.target.value);
        validateDefinition(modal);
    });
    document.addEventListener("change", event => { const modal = event.target.closest("[data-requirement-definition]"); if (modal && event.target.matches("[data-requirement-definition-field]")) validateDefinition(modal); });
    const applyDefinition = async modal => {
        if (!validateDefinition(modal) || definition?.inFlightToken !== null) return;
        const sequence = definition.sequence, token = ++definition.nextApplyToken;
        definition.inFlightToken = token;
        validateDefinition(modal);
        try {
            await dotnet.invokeMethodAsync(modal.dataset.definitionKind === "predicate" ? "ApplyPredicateDraft" : "ApplyItemDraft", Number(sequence), command(modal));
        } finally {
            const current = document.querySelector("[data-requirement-definition]");
            if (definition?.sequence === sequence && definition.inFlightToken === token && current?.dataset.definitionSequence === sequence) {
                definition.inFlightToken = null;
                validateDefinition(current);
            }
        }
    };
    const closeDefinition = async modal => {
        const sequence = Number(modal.dataset.definitionSequence);
        definition = null;
        definitionBackdropPointer = null;
        await dotnet?.invokeMethodAsync("CloseDefinitionDraft", sequence);
    };
    document.addEventListener("click", async event => {
        const action = event.target.closest("[data-requirement-definition-action]");
        const modal = event.target.closest("[data-requirement-definition]");
        if (!modal || !action) return;
        if (action.dataset.requirementDefinitionAction === "cancel") { await closeDefinition(modal); return; }
        if (action.dataset.requirementDefinitionAction === "apply") await applyDefinition(modal);
    });
    document.addEventListener("keydown", async event => {
        const modal = event.target.closest("[data-requirement-definition]");
        if (modal && event.key === "Escape") { event.preventDefault(); await closeDefinition(modal); return; }
        const graphFailureDialog = event.target.closest("[data-room-graph-failure-dialog]");
        if (graphFailureDialog && event.key === "Tab") {
            event.preventDefault();
            graphFailureDialog.querySelector("[data-room-graph-failure-close]")?.focus();
        }
    });
    document.addEventListener("pointerdown", event => { definitionBackdropPointer = event.target.matches("[data-requirement-definition-backdrop]") ? { element: event.target, pointerId: event.pointerId } : null; });
    document.addEventListener("pointerup", async event => { const gesture = definitionBackdropPointer; definitionBackdropPointer = null; if (!gesture || gesture.element !== event.target || gesture.pointerId !== event.pointerId) return; const modal = document.querySelector("[data-requirement-definition]"); if (modal) await closeDefinition(modal); });
    document.addEventListener("pointercancel", () => { definitionBackdropPointer = null; });
    return { initialize, cleanup, dispose, openImportPicker, focusModal, restoreModalFocus, focusField, initializeDefinitionDraft };
})();

window.downloadFileFromBytes = (fileName, bytes) => {
    const url = URL.createObjectURL(new Blob([bytes], { type: "application/json;charset=utf-8" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    link.click();
    URL.revokeObjectURL(url);
};
