(() => {
    "use strict";
    // One document owner; neither a circuit nor a room owns this session.
    if (window.archipelagoConnection) {
        window.archipelagoConnection.reconcile();
        return;
    }
    const host = document.getElementById("archipelago-modal");
    const dialog = host.querySelector("[role=dialog]");
    const statusStyle = document.getElementById("archipelago-status");
    const warning = document.getElementById("archipelago-warning");
    const fields = Object.fromEntries(["server", "port", "slot", "password"].map(name =>
        [name, document.getElementById(`archipelago-${name}`)]));
    const auto = document.getElementById("archipelago-auto");
    const follow = document.getElementById("archipelago-follow");
    const connect = document.getElementById("archipelago-connect");
    const defaults = { server: "archipelago.gg", port: "", slot: "Player", password: "", auto: false, follow: true };
    const draft = { ...defaults };
    const storageWarnings = new Set();
    let state = "disconnected", failure = "", generation = 0, socket = null;
    let deadline = null, retry = null, retryIndex = 0, retryGeneration = 0, retryBlocked = false;
    let launcher = null, backdropPointer = null;
    let roomOwner = null, valueRevision = 0, hasRoomValue = false, latestRoomValue = null;
    function followCurrent(token, root) {
        return token === valueRevision && draft.follow && state === "connected" &&
            roomOwner?.root === root && root.isConnected && location.href === roomOwner.url && !followBlocked();
    }
    function followBlocked() {
        if (!host.hidden) return true;
        // Expanded Requirements is its inline editor, not a blocking proposal.
        return Array.from(document.querySelectorAll(".confirmation-backdrop, .requirement-syntax-guide-backdrop, .requirement-catalogue-backdrop, .scene-review-backdrop, .scene-parsing-backdrop, .diagnostic-backdrop, .fatal-circuit-fallback, dialog[open]"))
            .some(element => element.getClientRects().length > 0 && getComputedStyle(element).visibility !== "hidden");
    }
    function receivedRoom(value, live) {
        // Advance synchronously on EVERY packet, even off, blocked, outside a room
        // or unavailable. Exact normalized equality belongs solely to C#.
        const previous = latestRoomValue, hadValue = hasRoomValue;
        latestRoomValue = typeof value === "string" ? value : null;
        hasRoomValue = true;
        const token = ++valueRevision, owner = roomOwner;
        if (!live || !hadValue || !owner || !followCurrent(token, owner.root) || followBlocked()) return;
        owner.callback.invokeMethodAsync("FollowArchipelagoRoomAsync", token, previous, latestRoomValue, owner.page)
            .catch(() => {}); // Circuit retirement consumes this value; never retry.
    }
    // Independent in-memory identity, stable through this tab's reconnect attempts.
    const identityBytes = new Uint32Array(4);
    crypto.getRandomValues(identityBytes);
    const uuid = Array.from(identityBytes, value => value.toString(16).padStart(8, "0")).join("");
    const own = (object, key) => Object.prototype.hasOwnProperty.call(object, key);
    const record = value => value !== null && typeof value === "object" && !Array.isArray(value);

    function storageFault(error, area) {
        if (!(error instanceof DOMException) || !["SecurityError", "QuotaExceededError", "InvalidStateError"].includes(error.name)) throw error;
        storageWarnings.add(`${area} storage is unavailable; these preferences remain in memory only.`);
    }
    function restore(area, names) {
        let raw;
        try { raw = window[area].getItem("logic-manager.archipelago"); }
        catch (error) { storageFault(error, area); return; }
        if (raw === null) return;
        let stored;
        try { stored = JSON.parse(raw); }
        catch (error) { if (!(error instanceof SyntaxError)) throw error; return; }
        if (!record(stored)) return;
        for (const name of names) {
            if (typeof stored[name] === typeof defaults[name]) draft[name] = stored[name];
        }
    }
    function persist(area, names) {
        try { window[area].setItem("logic-manager.archipelago", JSON.stringify(Object.fromEntries(names.map(name => [name, draft[name]])))); }
        catch (error) { storageFault(error, area); }
    }
    restore("localStorage", ["server", "port", "slot"]);
    restore("sessionStorage", ["password", "auto", "follow"]);
    for (const [name, field] of Object.entries(fields)) field.value = draft[name];
    auto.checked = draft.auto;
    follow.checked = draft.follow;

    function details() {
        const server = draft.server.trim(), port = draft.port.trim(), slot = draft.slot.trim();
        if (!server) return { error: "Enter a server hostname or IP address." };
        // URL parsing is used only after excluding URL, path, userinfo and port syntax.
        const ipv6 = /^\[[0-9a-fA-F:.]+\]$/.test(server);
        if ((!ipv6 && !/^[a-zA-Z0-9.-]+$/.test(server)) || (!ipv6 && server.includes(":")))
            return { error: "Enter a hostname or IP address without a URL, path or port; bracket IPv6 addresses." };
        if (!/^[0-9]+$/.test(port) || Number(port) < 1 || Number(port) > 65535)
            return { error: "Enter a port from 1 through 65535. A blank port never connects." };
        if (!slot) return { error: "Enter a slot name." };
        try { new URL(`wss://${server}:${Number(port)}/`); }
        catch (error) { if (!(error instanceof TypeError)) throw error; return { error: "Enter a valid server hostname or IP address." }; }
        return { server, port: Number(port), slot, password: draft.password };
    }
    function reconcile() {
        const label = `Archipelago: ${state === "connected" ? "connected" : state === "connecting" ? "connecting" : state === "failed" ? "connection failed or lost" : "disconnected"}`;
        for (const button of document.querySelectorAll("[data-archipelago-launcher]")) {
            button.title = label;
            button.setAttribute("aria-label", label);
            button.setAttribute("aria-expanded", String(!host.hidden));
        }
    }
    function render() {
        const focused = document.activeElement;
        const locked = state === "connecting" || state === "connected";
        for (const field of Object.values(fields)) field.disabled = locked;
        connect.textContent = state === "connected" ? "disconnect" : state === "connecting" ? "connecting..." : "connect";
        const validation = details().error;
        connect.disabled = state === "connecting" || (state !== "connected" && Boolean(validation));
        const messages = [...storageWarnings, failure, !locked ? validation : ""].filter(Boolean);
        warning.textContent = messages.join("\n");
        warning.hidden = messages.length === 0;
        // Permanent CSS fact applies even to fresh sidebar markup before reconciliation.
        statusStyle.textContent = `.sidebar-export-footer .sidebar-archipelago-control { color: ${state === "connected" ? "#146c43" : state === "failed" ? "#842029" : "#171717"}; }`;
        reconcile();
        if (!host.hidden && host.contains(focused) && focused.disabled) auto.focus({ preventScroll: true });
    }
    function cancelRetry() {
        ++retryGeneration;
        clearTimeout(retry);
        retry = null;
    }
    function retire() {
        ++generation;
        ++valueRevision; hasRoomValue = false; latestRoomValue = null;
        clearTimeout(deadline);
        deadline = null;
        const old = socket;
        socket = null;
        if (old) {
            old.onopen = old.onmessage = old.onerror = old.onclose = null;
            old.close();
        }
    }
    function scheduleRetry() {
        if (!draft.auto || retryBlocked || details().error) return;
        const delay = [2000, 4000, 8000, 16000, 30000][Math.min(retryIndex++, 4)];
        // Scheduled wait is editable; only an admitted attempt locks the form.
        state = "disconnected";
        const token = retryGeneration;
        retry = setTimeout(() => {
            if (token !== retryGeneration) return;
            retry = null;
            if (draft.auto && !retryBlocked) start();
        }, delay);
    }
    function failed(message, blockRetry = false) {
        retire();
        failure = message;
        retryBlocked = blockRetry;
        state = "failed";
        if (!blockRetry) scheduleRetry();
        render();
    }
    const protocolWarning = "Connection failed. See the browser console for details.";
    class ProtocolError extends Error { }
    function requireShape(condition, criterion) { if (!condition) throw new ProtocolError(criterion); }

    function openTransport(connection, plaintext) {
        // Every entry, including fallback and scheduled retry, checks required port.
        if (details().error) { state = "disconnected"; render(); return; }
        const token = ++generation;
        ++valueRevision; hasRoomValue = false; latestRoomValue = null;
        // Reconstruct from the admitted explicit port. Changing URL.protocol on
        // a normalized wss URL would lose port 443 and silently fall back to 80.
        const url = new URL(`${plaintext ? "ws" : "wss"}://${connection.server}:${connection.port}/`);
        let opened = false, phase = "room-info", key = null;
        let current;
        const active = () => generation === token && socket === current;
        function diagnostic(kind, command, type, reason) {
            if (!active()) return;
            // Select primitive strings only, redact every literal password occurrence,
            // and never pass a live packet, event, exception or connection to console.
            const safe = value => typeof value !== "string" ? "unavailable" :
                connection.password ? value.split(connection.password).join("[redacted]") : value;
            console.error("[Archipelago]", kind, "phase:", phase,
                "command:", safe(command), "type:", safe(type), "reason:", safe(reason));
        }
        function refused(packet) {
            const codes = Array.isArray(packet.errors) && packet.errors.length ? packet.errors : [null];
            const messages = new Set();
            let technical = false;
            for (const code of codes) {
                diagnostic("ConnectionRefused", "ConnectionRefused", code, "Authentication refused.");
                switch (code) {
                    case "InvalidPassword": messages.add("Archipelago refused authentication. Check the password."); break;
                    case "InvalidSlot": messages.add("Archipelago refused authentication. Check the slot name."); break;
                    case "InvalidGame": messages.add("Archipelago refused authentication: client/game mismatch."); technical = true; break;
                    case "IncompatibleVersion": messages.add("Archipelago refused authentication: incompatible client protocol version."); technical = true; break;
                    case "InvalidItemsHandling": messages.add("Archipelago refused authentication: unsupported client items handling."); technical = true; break;
                    default: technical = true; break;
                }
            }
            if (technical) messages.add(protocolWarning);
            failed([...messages].join("\n"), true);
        }
        function transportFailure() {
            if (!active()) return;
            const fallback = !opened && !plaintext && location.protocol === "http:";
            retire();
            if (fallback) openTransport(connection, true);
            else failed(opened ? "The Archipelago connection was lost or authentication timed out. Browser transport details cannot identify the cause." : "Couldn’t connect to the server. Check the server address and port, then try again.");
        }
        try { current = new WebSocket(url.href); }
        catch (error) {
            if (!(error instanceof DOMException) || !["SecurityError", "SyntaxError"].includes(error.name)) throw error;
            // No socket exists yet, but this is still the same pre-open attempt.
            if (!plaintext && location.protocol === "http:") openTransport(connection, true);
            else failed("The browser could not open this WebSocket endpoint. Check browser connection restrictions and the supplied endpoint.");
            return;
        }
        socket = current;
        const send = packet => {
            if (!active()) return false;
            if (current.readyState !== WebSocket.OPEN) { transportFailure(); return false; }
            try { current.send(JSON.stringify([packet])); }
            catch (error) {
                if (!(error instanceof DOMException) || error.name !== "InvalidStateError") throw error;
                transportFailure();
                return false;
            }
            return true;
        };
        deadline = setTimeout(transportFailure, 10000);
        current.onopen = () => {
            if (!active()) return;
            opened = true;
            clearTimeout(deadline);
            deadline = setTimeout(transportFailure, 15000);
        };
        current.onerror = transportFailure;
        current.onclose = transportFailure;
        current.onmessage = event => {
            if (!active()) return;
            let command = null;
            try {
                requireShape(typeof event.data === "string", "WebSocket message data must be a string.");
                let packets;
                try { packets = JSON.parse(event.data); }
                catch (error) { if (!(error instanceof SyntaxError)) throw error; throw new ProtocolError("WebSocket message must contain valid JSON."); }
                requireShape(Array.isArray(packets), "JSON envelope must be an array of commands.");
                for (const packet of packets) {
                    if (!active()) return;
                    command = record(packet) && typeof packet.cmd === "string" ? packet.cmd : null;
                    requireShape(record(packet) && typeof packet.cmd === "string", "Each command must be an object with a string cmd.");
                    switch (packet.cmd) {
                        case "RoomInfo":
                            requireShape(phase === "room-info", "RoomInfo requires the room-info phase.");
                            phase = "authentication";
                            if (!send({ cmd: "Connect", name: connection.slot, password: connection.password,
                                game: null, tags: ["Tracker", "NoText"], items_handling: 0, slot_data: true, uuid,
                                version: { major: 0, minor: 6, build: 0, class: "Version" } })) return;
                            break;
                        case "ConnectionRefused":
                            requireShape(phase === "authentication", "ConnectionRefused requires the authentication phase.");
                            refused(packet);
                            return;
                        case "Connected":
                            requireShape(phase === "authentication", "Connected requires the authentication phase.");
                            requireShape(Number.isSafeInteger(packet.team) && packet.team >= 0, "Connected team must be a nonnegative safe integer.");
                            requireShape(Number.isSafeInteger(packet.slot) && packet.slot > 0, "Connected slot must be a positive safe integer.");
                            requireShape(record(packet.slot_data), "Connected slot_data must be an object.");
                            phase = "connected";
                            key = `Silksong:CurrentRoom:${packet.team}:${packet.slot}`;
                            clearTimeout(deadline);
                            deadline = null;
                            state = "connected";
                            failure = "";
                            retryIndex = 0;
                            console.log("[Archipelago] Connected slot_data", packet.slot_data);
                            if (!send({ cmd: "SetNotify", keys: [key] }) || !send({ cmd: "Get", keys: [key] })) return;
                            render();
                            break;
                        case "RoomUpdate":
                            requireShape(phase === "connected", "RoomUpdate requires the connected phase.");
                            if (own(packet, "slot_data")) {
                                requireShape(record(packet.slot_data), "RoomUpdate slot_data must be an object when present.");
                                console.log("[Archipelago] RoomUpdate slot_data", packet.slot_data);
                            }
                            break;
                        case "Retrieved":
                            requireShape(phase === "connected", "Retrieved requires the connected phase.");
                            requireShape(record(packet.keys), "Retrieved keys must be an object.");
                            requireShape(own(packet.keys, key), "Retrieved keys must contain the subscribed current-room key (null is allowed).");
                            console.log("[Archipelago] current room initial", key, packet.keys[key]);
                            receivedRoom(packet.keys[key], false);
                            break;
                        case "SetReply":
                            requireShape(phase === "connected", "SetReply requires the connected phase.");
                            requireShape(typeof packet.key === "string", "SetReply key must be a string.");
                            if (packet.key === key) {
                                requireShape(own(packet, "value"), "Matching SetReply must contain value (null is allowed).");
                                console.log("[Archipelago] current room update", key, packet.value);
                                receivedRoom(packet.value, true);
                            }
                            break;
                        case "InvalidPacket":
                            diagnostic("InvalidPacket", packet.original_cmd, packet.type, packet.text);
                            failed(protocolWarning, true);
                            return;
                        default: break; // No game state, chat, item or unrelated storage consumers.
                    }
                }
            } catch (error) {
                if (!(error instanceof ProtocolError)) throw error;
                if (!active()) return;
                diagnostic("Local protocol error", command, "ProtocolError", error.message);
                failed(protocolWarning, true);
            }
        };
    }
    function start() {
        if (socket || state === "connecting" || state === "connected") return;
        cancelRetry();
        const connection = details();
        if (connection.error) { render(); return; }
        retryBlocked = false;
        state = "connecting";
        failure = "";
        render();
        openTransport(connection, false);
    }
    function disconnect() {
        cancelRetry();
        retire();
        draft.auto = auto.checked = false;
        persist("sessionStorage", ["password", "auto", "follow"]);
        retryBlocked = false;
        retryIndex = 0;
        failure = "";
        state = "disconnected";
        render();
    }
    for (const [name, field] of Object.entries(fields)) {
        field.addEventListener("input", () => {
            if (state === "connecting" || state === "connected") return;
            draft[name] = field.value;
            cancelRetry();
            retryBlocked = false;
            failure = "";
            persist(name === "password" ? "sessionStorage" : "localStorage", name === "password" ? ["password", "auto", "follow"] : ["server", "port", "slot"]);
            render();
        });
    }
    auto.addEventListener("change", () => {
        draft.auto = auto.checked;
        persist("sessionStorage", ["password", "auto", "follow"]);
        if (!draft.auto) {
            cancelRetry();
            // A cancelled transport retry is terminal failure, not intentional disconnect.
            if (failure && state === "disconnected" && !socket && deadline === null && retry === null) state = "failed";
        }
        else { retryBlocked = false; start(); }
        render();
    });
    host.querySelector("form").addEventListener("submit", event => {
        event.preventDefault();
        if (state === "connected") disconnect();
        else start();
    });
    follow.addEventListener("change", () => {
        draft.follow = follow.checked;
        ++valueRevision; // Enabling retains latest baseline; neither edge replays it.
        persist("sessionStorage", ["password", "auto", "follow"]);
        render();
    });
    function closeModal() {
        host.hidden = true;
        backdropPointer = null;
        reconcile();
        const target = launcher?.isConnected ? launcher : document.querySelector("[data-archipelago-launcher]");
        target?.focus({ preventScroll: true });
    }
    host.querySelector(".archipelago-close").addEventListener("click", closeModal);
    document.addEventListener("click", event => {
        const button = event.target.closest?.("[data-archipelago-launcher]");
        if (!button) return;
        launcher = button;
        host.hidden = false;
        render();
        (fields.server.disabled ? auto : fields.server).focus({ preventScroll: true });
    });
    host.addEventListener("pointerdown", event => {
        backdropPointer = event.target === host && event.button === 0 ? event.pointerId : null;
    });
    host.addEventListener("pointercancel", () => { backdropPointer = null; });
    host.addEventListener("click", event => {
        if (event.target === host && backdropPointer !== null && event.button === 0) closeModal();
        backdropPointer = null;
    });
    host.addEventListener("keydown", event => {
        if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); closeModal(); }
        else if (event.key === "Tab") {
            const focusable = Array.from(dialog.querySelectorAll("button, input")).filter(element => !element.disabled);
            const first = focusable[0], last = focusable.at(-1);
            if (event.shiftKey && (document.activeElement === first || document.activeElement === dialog)) { event.preventDefault(); last.focus(); }
            else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
        }
    });
    document.addEventListener("focusin", event => {
        if (host.hidden || host.contains(event.target)) return;
        // This compact generic-layer modal must never steal diagnostic, V2,
        // expanded-editor or native reconnect-dialog focus above it.
        const higher = document.querySelectorAll(".v2-page-modal-backdrop, .requirement-syntax-guide-backdrop, .requirement-catalogue-backdrop, .scene-review-backdrop, .scene-parsing-backdrop, .requirements-expanded-backdrop, .diagnostic-backdrop, .fatal-circuit-fallback, dialog[open]");
        if (Array.from(higher).some(element => element.getClientRects().length > 0 && getComputedStyle(element).visibility !== "hidden")) return;
        (fields.server.disabled ? auto : fields.server).focus({ preventScroll: true });
    });
    window.archipelagoConnection = Object.freeze({
        reconcile, followCurrent, followBlocked,
        registerRoom: (root, callback, page) => {
            ++valueRevision;
            roomOwner = { root, callback, page, url: location.href };
        },
        unregisterRoom: root => { if (roomOwner?.root === root) { ++valueRevision; roomOwner = null; } }
    });
    // A replacement InteractiveServer island can insert a launcher independently
    // of enhancedload. This mount hook reconciles only that owner's status facts;
    // it owns no session, observer, circuit callback or connection trigger.
    customElements.define("archipelago-launcher", class extends HTMLElement {
        connectedCallback() { queueMicrotask(reconcile); }
    });
    Blazor.addEventListener("enhancedload", () => { ++valueRevision; reconcile(); });
    render();
    if (draft.auto && !details().error) start();
})();
