using Jint;
using Bunit;
using Microsoft.AspNetCore.Components;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using System.Text.Json;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

public sealed class V2ChildInteractionOwnerJsTests
{
    [Fact]
    public void V2SceneSelectionCallbacksCarryMountedOwnerGenerationAcrossReplacement()
    {
        ExecuteHarness("""
            const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 50");
            const marker = new Element("g", { sceneSelectionKind: "exit", sceneId: "item" }, { "data-scene-selection-kind": "exit", "data-scene-id": "item" }, svg);
            const calls = [], callback = { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } };
            window.initializeV2SceneViewport(svg, "room-owner", 4, callback);
            svg.dispatch("pointerup", pointerEvent(marker, 1, 2, { button: 0, pointerId: 1 }));
            assert(calls.length === 1 && calls[0][0] === "SelectSceneItemAsync" && calls[0][1] === "room-owner" && calls[0][2] === 4, "selection callback must identify its mounted room and generation");
            const typedNonrenderedB = "nonrendered-b"; window.selectV2SceneLayoutItem(svg, "item"); assert(marker.classList.contains("selected"), "rendered A begins as the browser selection"); window.clearV2SceneLayoutSelection(svg);
            assert(!marker.classList.contains("selected") && typedNonrenderedB === "nonrendered-b", "rendered A loses its DOM selected visual while typed nonrendered B remains pane-local state");
            window.initializeV2SceneViewport(svg, "room-owner", 5, callback);
            svg.dispatch("pointerup", pointerEvent(marker, 1, 2, { button: 0, pointerId: 2 })); svg.dispatch("pointerup", pointerEvent(svg, 1, 2, { button: 0, pointerId: 3 }));
            assert(calls.length === 3 && calls[2][0] === "ClearSceneSelectionAsync" && calls[2][1] === "room-owner" && calls[2][2] === 5, "replacement owner must send its new generation for clear admission");
            window.disposeV2SceneViewport(svg); svg.dispatch("pointerup", pointerEvent(marker, 1, 2, { button: 0, pointerId: 3 }));
            assert(calls.length === 3, "disposed owner must reject delayed selection callbacks locally");
        """);
    }

    [Fact]
    public void ActiveSubroomAndCheckGrips_RegisterDragStartAndDispatchOnlyOnDrop()
    {
        ExecuteHarness("""
            const root = new MockRoot();
            const subroom = row(root, "subroom", "subroom-id", 0), check = row(root, "check", "check-id", 0);
            const subroomTarget = row(root, "subroom", "subroom-target", 1, subroom.table), checkTarget = row(root, "check", "check-target", 1, check.table);
            const calls = [], owner = window.createV2ChildInteractionOwner(root);
            owner.register({ invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } }, "subroom");
            owner.register({ invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } }, "check");

            for (const [priorCalls, item] of [[0, { ...subroom, target: subroomTarget.node }], [1, { ...check, target: checkTarget.node }]]) {
                const transfer = transferOf();
                root.dispatch("dragstart", eventOf(item.grip, transfer));
                assert(transfer.values["text/plain"] === item.id, "drag start must place the row ID in DataTransfer");
                assert(transfer.effectAllowed === "move", "drag start must enable move");
                root.dispatch("dragover", eventOf(item.target, transfer));
                assert(item.target.classList.contains(item.kind === "subroom" ? "v2-drop-before" : "v2-check-drop-before"), "drag over must render an insertion line");
                assert(calls.length === priorCalls, "drag feedback must not dispatch a command");
                root.dispatch("drop", eventOf(item.target, transfer));
                assert(calls.length === (item.kind === "subroom" ? 1 : 2), "only drop dispatches exactly one command");
                assert(calls[calls.length - 1][0] === (item.kind === "subroom" ? "DropSubroomAsync" : "DropCheckAsync"), "drop must use the registered table callback");
                assert(calls[calls.length - 1][1] === item.id && calls[calls.length - 1][2] === "active" && calls[calls.length - 1][3] === 1, "drop must retain the active partition and insertion index");
            }
        """);
    }

    [Fact]
    public void ArchivedRowsHaveNoGripAndCancelledOrDisposedOwnersCannotDispatch()
    {
        ExecuteHarness("""
            const root = new MockRoot(), active = row(root, "subroom", "active-id", 0), archived = row(root, "subroom", "archived-id", 0, null, "archived");
            const calls = [], owner = window.createV2ChildInteractionOwner(root);
            owner.register({ invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } }, "subroom");
            assert(archived.grip === null, "archived rows must not render a draggable grip");
            root.dispatch("dragstart", eventOf(archived.node, transferOf()));
            assert(calls.length === 0, "an archived row cannot start a drag");
            const transfer = transferOf();
            root.dispatch("dragstart", eventOf(active.grip, transfer));
            root.dispatch("dragover", eventOf(active.node, transfer));
            assert(active.node.classList.contains("v2-drop-before"), "active drag must show feedback before cancellation");
            root.dispatch("dragend", eventOf(active.grip, transfer));
            assert(!active.node.classList.contains("v2-drop-before"), "drag cancellation must clear feedback");
            root.dispatch("dragstart", eventOf(active.grip, transferOf()));
            owner.dispose();
            root.dispatch("drop", eventOf(active.node, transferOf()));
            assert(calls.length === 0, "a disposed page root must reject stale drop callbacks");
            assert(!active.node.classList.contains("v2-drop-before"), "route disposal must clear feedback");
        """);
    }

    [Fact]
    public void OneRootOwnerRoutesAllFourTablesAndDispatchesOnlyDurableTableDrops()
    {
        ExecuteHarness("""
            const root = new MockRoot(), calls = [], owner = window.createV2ChildInteractionOwner(root);
            const kinds = ["subroom", "transition", "connection", "check"];
            const rows = kinds.map((kind, index) => row(root, kind, `${kind}-id`, index));
            for (const item of rows) owner.register({ invokeMethodAsync: (...args) => { calls.push([item.kind, ...args]); return Promise.resolve(); } }, item.kind);
            for (const item of rows) {
                const transfer = transferOf(); root.dispatch("dragstart", eventOf(item.grip, transfer));
                root.dispatch("dragover", eventOf(item.node, transfer));
                assert(item.node.classList.contains(configClass(item.kind)), "every active table must receive its own root-local feedback class");
                assert(calls.length === 0, "feedback must never invoke .NET");
                root.dispatch("drop", eventOf(item.node, transfer));
                assert(calls.length === 1 && calls[0][0] === item.kind, "durable-table drops use one typed table target"); calls.length = 0;
            }
            for (const kind of ["transition", "connection"]) {
                const item = rows.find(x => x.kind === kind), title = kind[0].toUpperCase() + kind.slice(1), prefix = "v2" + title;
                const tail = new Element("tr", { [prefix + "Tail"]: "true" }, { ["data-" + prefix.replace(/[A-Z]/g, x => "-" + x.toLowerCase()) + "-tail"]: "true" }, item.table);
                const beforeTail = transferOf(); root.dispatch("dragstart", eventOf(item.grip, beforeTail)); root.dispatch("dragover", eventOf(tail, beforeTail));
                assert(tail.classList.contains(configClass(kind)), "an active before-tail target must show its insertion line");
                root.dispatch("drop", eventOf(tail, beforeTail)); assert(calls.length === 1, "delivered table before-tail drops become durable"); calls.length = 0;
                const terminal = transferOf(); root.dispatch("dragstart", eventOf(item.grip, terminal)); root.dispatch("dragover", eventOf(item.table, terminal));
                assert(item.table.classList.contains(kind === "transition" ? "v2-transition-drop-tail" : "v2-connection-drop-tail"), "an active terminal target must show terminal feedback");
                root.dispatch("drop", eventOf(item.table, terminal)); assert(calls.length === 1, "delivered table terminal drops become durable"); calls.length = 0;
                const archived = row(root, kind, `${kind}-archived`, 0, null, "archived");
                assert(archived.grip === null, "archived rows have no fabricated grip");
            }
            owner.register({ invokeMethodAsync: (...args) => { calls.push(["replacement", ...args]); return Promise.resolve(); } }, "transition");
            owner.register({ invokeMethodAsync: (...args) => { calls.push(["replacement", ...args]); return Promise.resolve(); } }, "subroom");
            const replacement = transferOf(); root.dispatch("dragstart", eventOf(rows[0].grip, replacement)); root.dispatch("drop", eventOf(rows[0].node, replacement));
            assert(calls.length === 1 && calls[0][0] === "replacement", "registration replacement must discard the old callback"); calls.length = 0;
            root.dispatch("drop", eventOf(root, transferOf())); assert(calls.length === 0, "missing/no-drag drops are rejected");
            owner.unregister("connection"); const connectionTransfer = transferOf(); root.dispatch("dragstart", eventOf(rows[2].grip, connectionTransfer)); root.dispatch("drop", eventOf(rows[2].node, connectionTransfer));
            assert(calls.length === 0, "unregistered table callbacks are rejected");
            owner.dispose(); root.dispatch("keydown", eventOf(rows[0].node)); assert(Object.values(root.listeners).every(x => x.length === 0), "disposal removes every root listener");
            function configClass(kind) { return kind === "subroom" ? "v2-drop-before" : kind === "transition" ? "v2-transition-drop-before" : kind === "connection" ? "v2-connection-drop-before" : "v2-check-drop-before"; }
        """);
    }

    [Fact]
    public void OwnedKeydownWithoutKeyDoesNotDispatchWhileTabAndArrowInputBehaviorRemainsIntact()
    {
        ExecuteHarness("""
            const root = new MockRoot(), calls = [], owner = window.createV2ChildInteractionOwner(root);
            const field = new HTMLInputElement("input", { v2CheckField: "name", v2CheckClientRow: "client", v2CheckRight: "row|next" }, {}, root);
            field.value = "value"; field.selectionStart = field.selectionEnd = field.value.length;
            root.querySelector = selector => selector === "#editor-row-row-next" ? field : null;
            owner.register({ invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } }, "check");

            root.dispatch("keydown", eventOf(field));
            assert(calls.length === 0, "an owned native datalist keydown without key must not throw or dispatch .NET");

            root.dispatch("keydown", { ...eventOf(field), key: "Tab" });
            const tabTarget = new HTMLElement("input", {}, { id: "next-field" }, root); tabTarget.id = "next-field";
            root.dispatch("focusout", { ...eventOf(field), relatedTarget: tabTarget });
            assert(calls.length === 1 && calls[0][0] === "BlurCheckAsync" && calls[0][3] === "next-field", "a real Tab preserves the blur/create focus handoff");

            calls.length = 0;
            root.dispatch("keydown", { ...eventOf(field), key: "ArrowRight" });
            assert(calls.length === 1 && calls[0][0] === "NavigateCheckAsync" && calls[0][1] === "client" && calls[0][2] === "name" && calls[0][3] === "right", "a real Arrow input preserves typed navigation");
        """);
    }

    [Fact]
    public void UnusedEditableCellSpaceFocusesTextTextareaAndDatalistEditorsAcrossAllChildTablesWithoutDispatch()
    {
        ExecuteHarness("""
            const root = new MockRoot(), calls = [], owner = window.createV2ChildInteractionOwner(root);
            const cases = [
                ["subroom", "input", true], ["subroom", "textarea", false],
                ["transition", "input", false], ["transition", "textarea", false], ["transition", "input", true],
                ["connection", "input", false], ["connection", "textarea", false], ["connection", "input", true],
                ["check", "textarea", false], ["check", "input", true]
            ];
            for (const kind of ["subroom", "transition", "connection", "check"])
                owner.register({ invokeMethodAsync: (...args) => { calls.push([kind, ...args]); return Promise.resolve(); } }, kind);
            for (const [kind, tag, datalist] of cases) {
                const cell = new Element("td", {}, {}, root);
                const editor = tag === "textarea" ? new HTMLTextAreaElement("textarea", {}, {}, cell) : new HTMLInputElement("input", {}, {}, cell);
                if (datalist) editor.attrs.list = "suggestions";
                cell.nodes = { "input:not([type='checkbox']), textarea": editor };
                const event = eventOf(cell);
                root.dispatch("click", event);
                assert(editor.focusCount === 1, `${kind} ${tag}${datalist ? " datalist" : ""} unused cell space must focus its editor`);
                assert(!event.prevented && !event.stopped, "cell focus must not consume the click");
                assert(calls.length === 0, "cell focus must not dispatch a command or save callback");
            }
        """);
    }

    [Fact]
    public void V2SceneViewportInitializer_DrivesTransientViewportAndAnnotationContract()
    {
        ExecuteHarness("""
            let dotNetCalls = 0, storageCalls = 0;
            window.DotNet = { invokeMethodAsync: () => { dotNetCalls++; } };
            sessionStorage.getItem = () => { storageCalls++; return null; };
            sessionStorage.setItem = () => { storageCalls++; };
            const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 50");
            const annotations = new Element("g"), toggle = new Element("button"), reset = new Element("button");
            const bounds = new Element("rect", {}, {}, svg), marker = new Element("circle", {}, {}, svg), frame = new Element("rect", {}, {}, svg);
            bounds.classList.add("scene-layout-room-bounds");
            pane.nodes["[data-scene-annotations=true]"] = annotations;
            pane.nodes["[data-scene-annotation-toggle=true]"] = toggle;
            pane.nodes["[data-scene-reset-view=true]"] = reset;

            window.initializeV2SceneViewport(svg, "room-one");
            assert(svg.getAttribute("viewBox") === "0 0 100 50", "initialization must set the initial viewBox");
            assert(annotations.style.display === "", "annotations must start shown");
            toggle.dispatch("click", eventOf(toggle));
            assert(annotations.style.display === "none", "the toggle must hide annotations only");
            assert(svg.getAttribute("viewBox") === "0 0 100 50", "hiding annotations must retain bounds and canvas viewBox");

            svg.dispatch("wheel", pointerEvent(svg, 50, 25, { deltaY: -1000, deltaMode: 0 }));
            let zoomed = viewBox(svg);
            assert(zoomed[2] < 100 && zoomed[2] >= 12.5, "wheel zoom must remain within initial-fit and 8x limits");
            assert(close(zoomed[0] + zoomed[2] / 2, 50) && close(zoomed[1] + zoomed[3] / 2, 25), "wheel zoom must remain pointer anchored");
            for (let i = 0; i < 80; i++) svg.dispatch("wheel", pointerEvent(svg, 50, 25, { deltaY: -1000, deltaMode: 0 }));
            assert(close(viewBox(svg)[2], 12.5), "wheel zoom must clamp at 8x zoom-in");
            for (let i = 0; i < 80; i++) svg.dispatch("wheel", pointerEvent(svg, 50, 25, { deltaY: 1000, deltaMode: 0 }));
            assert(close(viewBox(svg)[2], 100), "wheel zoom must not zoom out beyond initial fit");

            svg.dispatch("wheel", pointerEvent(svg, 50, 25, { deltaY: -1000, deltaMode: 0 }));
            const beforeShortDrag = svg.getAttribute("viewBox");
            svg.dispatch("pointerdown", pointerEvent(bounds, 50, 25, { button: 0, pointerId: 1 }));
            svg.dispatch("pointermove", pointerEvent(bounds, 46, 25, { pointerId: 1 }));
            assert(svg.getAttribute("viewBox") === beforeShortDrag, "a sub-5px bounds drag must not pan");
            svg.dispatch("pointermove", pointerEvent(bounds, 45, 25, { pointerId: 1 }));
            assert(svg.getAttribute("viewBox") !== beforeShortDrag, "a 5px bounds drag must pan");
            svg.dispatch("pointerup", pointerEvent(bounds, 45, 25, { pointerId: 1 }));

            const afterBoundsDrag = svg.getAttribute("viewBox");
            svg.dispatch("pointerdown", pointerEvent(marker, 50, 25, { button: 0, pointerId: 2 }));
            svg.dispatch("pointermove", pointerEvent(marker, 40, 25, { pointerId: 2 }));
            assert(svg.getAttribute("viewBox") === afterBoundsDrag, "markers must remain excluded from viewport pan");
            svg.dispatch("pointerdown", pointerEvent(frame, 50, 25, { button: 0, pointerId: 3 }));
            svg.dispatch("pointermove", pointerEvent(frame, 40, 25, { pointerId: 3 }));
            assert(svg.getAttribute("viewBox") === afterBoundsDrag, "frames must remain excluded from viewport pan");

            reset.dispatch("click", eventOf(reset));
            assert(svg.getAttribute("viewBox") === "0 0 100 50", "reset must restore the initial viewBox");
            toggle.dispatch("click", eventOf(toggle));
            assert(annotations.style.display === "", "toggle must show annotations before navigation state changes");
            toggle.dispatch("click", eventOf(toggle));
            assert(annotations.style.display === "none", "annotation state must be changed before navigation reset");
            window.initializeV2SceneViewport(svg, "room-two");
            assert(annotations.style.display === "", "a changed room key must restore annotations shown");
            assert(svg.getAttribute("viewBox") === "0 0 100 50", "a changed room key must reset the viewport");
            assert(dotNetCalls === 0 && storageCalls === 2, "each mounted route loads only its named-owner preference");
        """);
    }

    [Fact]
    public void V2CapturePreviewOwner_DrivesEveryGestureAndCleansReplacementAndDisposalListeners()
    {
        ExecuteHarness("""
            var Event = function() {};
            const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 50"); svg.dataset.baseViewbox = "0 0 100 50"; svg.setAttribute("viewBox", "0 0 100 50");
            const x = new Element("input"), y = new Element("input"), px = new Element("input"), py = new Element("input"); for (const input of [x,y,px,py]) input.dispatchEvent = event => input.dispatch("input", event); x.value="100"; y.value="50"; px.value="0"; py.value="0";
            const source = new Element("image"), bounds = new Element("rect", {}, {width:"100",height:"50"}); svg.nodes["[data-v2-scene-capture-source=true]"] = source; svg.nodes["[data-v2-scene-capture-bounds=true]"] = bounds;
            let draftCallbacks=0; for (const input of [x,y,px,py]) input.addEventListener("input", () => draftCallbacks++);
            window.initializeV2SceneImageCapturePreview(svg,x,y,px,py);
            const sourceMatchesLocal = () => { const width=10000/Number(x.value), height=5000/Number(y.value); return close(Number(source.getAttribute("width")),width) && close(Number(source.getAttribute("height")),height) && close(Number(source.getAttribute("x")),(100-width)/2+Number(px.value)/100*width) && close(Number(source.getAttribute("y")),-50+(50-height)/2+Number(py.value)/100*height); };
            assert(sourceMatchesLocal(), "initial source image geometry reflects browser-local calibration");
            x.value="25"; y.value="75"; px.value="4"; py.value="-3"; window.reconcileV2SceneImageCapturePreview(svg); assert(sourceMatchesLocal(), "typed reset reconciliation updates the live source image without a gesture callback");
            svg.dispatch("wheel", { deltaY:-1, ctrlKey:false, clientX:20, clientY:10, preventDefault() {} }); assert(svg.getAttribute("viewBox") !== "0 0 100 50", "plain wheel zooms inspection viewport");
            const beforePlainDrag=svg.getAttribute("viewBox"); svg.dispatch("pointerdown", pointerEvent(svg, 10, 10, {button:0,pointerId:1})); svg.dispatch("pointermove", pointerEvent(svg, 20, 15, {pointerId:1})); svg.dispatch("pointerup", pointerEvent(svg, 20, 15, {pointerId:1})); assert(svg.getAttribute("viewBox") !== beforePlainDrag && !svg.hasPointerCapture(1), "plain drag mutates and releases the inspection viewport");
            const ratioBeforeWheel=Number(x.value)/Number(y.value); svg.dispatch("wheel", { deltaY:-1, ctrlKey:true, clientX:20, clientY:10, preventDefault() {} }); assert(Number(x.value)!==100 && Number(y.value)!==50 && close(Number(x.value)/Number(y.value), ratioBeforeWheel) && sourceMatchesLocal(), "Ctrl wheel applies uniform ratio-preserving scale and live source geometry");
            svg.dispatch("pointerdown", pointerEvent(svg, 10, 10, {button:0,ctrlKey:true,pointerId:2})); svg.dispatch("pointermove", pointerEvent(svg, 20, 15, {pointerId:2})); svg.dispatch("pointerup", pointerEvent(svg, 20, 15, {pointerId:2})); assert(Number(px.value)!==0 && Number(py.value)!==0 && sourceMatchesLocal(), "Ctrl drag changes browser-local displayed image pan and live source geometry");
            const beforeX=Number(x.value), beforeY=Number(y.value), ratioBeforeScale=beforeX/beforeY; svg.dispatch("pointerdown", pointerEvent(svg, 10, 10, {button:0,ctrlKey:true,shiftKey:true,pointerId:3})); svg.dispatch("pointermove", pointerEvent(svg, 30, 20, {pointerId:3})); svg.dispatch("pointerup", pointerEvent(svg, 30, 20, {pointerId:3})); assert(Number(x.value)!==beforeX && Number(y.value)!==beforeY && !close(Number(x.value)/Number(y.value), ratioBeforeScale) && sourceMatchesLocal(), "Ctrl Shift drag independently changes X and Y scales with live source geometry");
            assert(draftCallbacks===0, "all preview wheel and pointer gestures must issue zero bound-input/.NET draft callbacks");
            const transferred=window.readV2SceneImageCapturePreview(svg); assert(transferred.scaleXPercent===String(x.value) && transferred.scaleYPercent===String(y.value) && transferred.panXPercent===String(px.value) && transferred.panYPercent===String(py.value), "Apply reads the current browser-local displayed values exactly once");
            window.initializeV2SceneImageCapturePreview(svg,x,y,px,py); svg.dispatch("pointerdown", pointerEvent(svg, 1, 1, {button:0,ctrlKey:true,pointerId:4})); svg.dispatch("pointermove", pointerEvent(svg, 2, 2, {pointerId:4})); assert(draftCallbacks===0, "replacement owner remains live without gesture callbacks");
            window.disposeV2SceneImageCapturePreview(svg); svg.dispatch("pointermove", pointerEvent(svg, 3, 3, {pointerId:4})); svg.dispatch("wheel", {deltaY:-1,ctrlKey:true,clientX:1,clientY:1,preventDefault() {}}); assert(draftCallbacks===0 && window.readV2SceneImageCapturePreview(svg)===null, "disposal rejects stale gestures and removes browser-local transfer state");
        """);
    }

    [Fact]
    public void V2SceneViewportOwner_InitializesTogglesAndCleansImageVisibilityWithoutServerOrStaleCallbacks()
    {
        ExecuteHarness("""
            let dotNetCalls = 0;
            const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 50");
            const image = new Element("image"), imageToggle = new Element("button"), icon = new Element("i");
            imageToggle.nodes = { "i": icon }; icon.classList.add("fa-image");
            svg.nodes["[data-scene-layout-image=true]"] = image;
            pane.nodes["[data-scene-image-toggle=true]"] = imageToggle;
            window.initializeV2SceneViewport(svg, "room-one", { invokeMethodAsync: () => { dotNetCalls++; return Promise.resolve(); } });
            assert(image.style.display === "", "an available image must start shown on mount");
            assert(imageToggle.getAttribute("aria-pressed") === "true" && imageToggle.getAttribute("aria-label") === "Hide scene image", "shown state must expose matching accessibility feedback");
            imageToggle.dispatch("click", eventOf(imageToggle));
            assert(image.style.display === "none", "the named owner must hide only the image");
            assert(imageToggle.getAttribute("aria-pressed") === "false" && imageToggle.getAttribute("aria-label") === "Show scene image" && imageToggle.getAttribute("title") === "Show scene image", "hidden state must expose matching feedback");
            assert(icon.classList.contains("fa-image") && !icon.classList.contains("fa-image-slash"), "image icon must remain stable while accessibility reports hidden state");
            // Apply/recapture can replace controls under the same RoomId. Reconcile
            // current nodes without changing the user's hidden state.
            const staleToggle = imageToggle, nextToggle = new Element("button"), nextIcon = new Element("i");
            nextToggle.nodes = { "i": nextIcon }; pane.nodes["[data-scene-image-toggle=true]"] = nextToggle;
            window.initializeV2SceneViewport(svg, "room-one", { invokeMethodAsync: () => { dotNetCalls++; return Promise.resolve(); } });
            assert(image.style.display === "none" && nextToggle.getAttribute("aria-pressed") === "false", "same-room replacement retains hidden state and wires replacement control");
            staleToggle.dispatch("click", eventOf(staleToggle));
            assert(image.style.display === "none", "a replaced control must be stale and rejected");
            // Stale/absent removes both image and toggle; a later capture replacement
            // is wired again under the unchanged room lifecycle key.
            delete svg.nodes["[data-scene-layout-image=true]"]; delete pane.nodes["[data-scene-image-toggle=true]"];
            window.initializeV2SceneViewport(svg, "room-one");
            const capturedImage = new Element("image"), capturedToggle = new Element("button"), capturedIcon = new Element("i");
            capturedToggle.nodes = { "i": capturedIcon }; svg.nodes["[data-scene-layout-image=true]"] = capturedImage; pane.nodes["[data-scene-image-toggle=true]"] = capturedToggle;
            window.initializeV2SceneViewport(svg, "room-one");
            assert(capturedImage.style.display === "none" && capturedToggle.getAttribute("aria-pressed") === "false", "absent-to-captured reconciliation preserves same-room visibility state");
            capturedToggle.dispatch("click", eventOf(capturedToggle));
            assert(capturedImage.style.display === "", "replacement capture control is wired by the named owner");
            const routeToggle = new Element("button"), routeIcon = new Element("i"); routeToggle.nodes = { "i": routeIcon }; pane.nodes["[data-scene-image-toggle=true]"] = routeToggle;
            window.initializeV2SceneViewport(svg, "room-two", { invokeMethodAsync: () => { dotNetCalls++; return Promise.resolve(); } });
            assert(capturedImage.style.display === "", "route navigation must reset image visibility to shown");
            capturedToggle.dispatch("click", eventOf(capturedToggle));
            assert(capturedImage.style.display === "", "the disposed route listener must not mutate the new owner state");
            window.disposeV2SceneViewport(svg);
            routeToggle.dispatch("click", eventOf(routeToggle));
            assert(capturedImage.style.display === "", "disposal must remove the image listener");
            assert(dotNetCalls === 0, "image visibility must not invoke .NET");
        """);
    }

    [Fact]
    public void V2SceneViewportOwner_ZoneChevronLoadsSavesAndReconcilesIndependentTabLocalPreferenceWithoutOppositeStateFlash()
    {
        ExecuteHarness("""
            let dotNetCalls = 0, loads = 0, saves = 0, stored = false;
            const context = new Element("div"); context.classList.add("room-map-context"); context.matches = selector => selector === ".room-map-context";
            context.classList.add("v2-scene-zone-preference-pending");
            const pane = new ScenePane(); pane.parentElement = context;
            const svg = new SceneSvg(pane, "0 0 100 50");
            const zonePane = new Element("div"); const divider = new Element("div");
            context.nodes = { ".room-context-zone-pane": zonePane, ".room-context-divider": divider };
            const first = new Element("button"), firstIcon = new Element("i"); first.nodes = { "i": firstIcon }; pane.nodes["[data-scene-zone-toggle=true]"] = first;
            window.loadRoomZoneMapVisibility = () => { loads++; return stored; };
            window.saveRoomZoneMapVisibility = value => { saves++; stored = value; };
            window.initializeV2SceneViewport(svg, "room-one", { invokeMethodAsync: () => { dotNetCalls++; return Promise.resolve(); } });
            assert(loads === 1 && !context.classList.contains("v2-scene-zone-preference-pending"), "hidden initialization must resolve once before the host becomes visible");
            assert(context.classList.contains("scene-layout-only") && zonePane.hidden && divider.hidden && first.getAttribute("aria-pressed") === "false", "stored hidden preference must remove legacy pane and divider and give scene-only state");
            first.dispatch("click", eventOf(first));
            assert(context.classList.contains("with-zone-map") && !zonePane.hidden && !divider.hidden && saves === 1 && stored, "toggle must save once and restore the retained legacy pane");
            const replacement = new Element("button"), replacementIcon = new Element("i"); replacement.nodes = { "i": replacementIcon }; pane.nodes["[data-scene-zone-toggle=true]"] = replacement;
            window.initializeV2SceneViewport(svg, "room-one"); const reconciled = replacement.getAttribute("aria-pressed"); first.dispatch("click", eventOf(first));
            assert(loads === 1 && replacement.getAttribute("aria-pressed") === reconciled, "same-route reconciliation must retain preference and reject replaced stale controls");
            replacement.dispatch("click", eventOf(replacement));
            assert(context.classList.contains("scene-layout-only") && zonePane.hidden && divider.hidden && replacement.getAttribute("aria-pressed") === "false" && saves === 2 && !stored, "replacement control must save hidden scene-only presentation");
            const routeToggle = new Element("button"), routeIcon = new Element("i"); routeToggle.nodes = { "i": routeIcon }; pane.nodes["[data-scene-zone-toggle=true]"] = routeToggle;
            window.initializeV2SceneViewport(svg, "room-two");
            assert(loads === 2 && context.classList.contains("scene-layout-only") && routeToggle.getAttribute("aria-pressed") === "false", "navigation/reload must reload and reconcile the persisted hidden preference");
            window.disposeV2SceneViewport(svg); replacement.dispatch("click", eventOf(replacement));
            assert(context.classList.contains("scene-layout-only") && saves === 2 && dotNetCalls === 0, "route/disposal cleanup must leave no callback or durable work");
        """);
    }

    [Fact]
    public void V2SceneViewportInitializer_SelectsOneItemWithoutSupersededInspector()
    {
        ExecuteHarness("""
            let dotNetCalls = 0, storageCalls = 0;
            window.DotNet = { invokeMethodAsync: () => { dotNetCalls++; } };
            sessionStorage.getItem = () => { storageCalls++; return null; };
            sessionStorage.setItem = () => { storageCalls++; };
            const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 50");
            const annotations = new Element("g");
            const marker = new Element("g", { sceneSelectionKind: "exit", sceneSelectionName: "Exit", sceneSelectionGeometry: "X: 10 · Y: 12" }, { "data-scene-selection-kind": "exit" }, svg);
            const frame = new Element("g", { sceneSelectionKind: "subroom", sceneSelectionName: "Frame", sceneSelectionGeometry: "X: 1 · Y: 2 · width: 3 · height: 4" }, { "data-scene-selection-kind": "subroom" }, svg);
            pane.nodes["[data-scene-annotations=true]"] = annotations;

            window.initializeV2SceneViewport(svg, "room-one");
            svg.dispatch("pointerup", pointerEvent(marker, 10, 12, { button: 0, pointerId: 1 }));
            assert(marker.classList.contains("selected"), "marker click must select one item");
            svg.dispatch("pointerup", pointerEvent(frame, 1, 2, { button: 0, pointerId: 2 }));
            assert(!marker.classList.contains("selected") && frame.classList.contains("selected"), "changing selection must leave exactly one selected item");
            svg.dispatch("pointerdown", pointerEvent(svg, 50, 25, { button: 0, pointerId: 3 }));
            svg.dispatch("pointerup", pointerEvent(svg, 50, 25, { button: 0, pointerId: 3 }));
            assert(!frame.classList.contains("selected"), "empty click must clear selection");
            svg.dispatch("pointerup", pointerEvent(marker, 10, 12, { button: 0, pointerId: 4 }));
            window.initializeV2SceneViewport(svg, "room-two");
            assert(!marker.classList.contains("selected"), "room navigation must clear selection");
            assert(dotNetCalls === 0 && storageCalls === 2, "selection does not access storage beyond each route's named-owner preference initialization");
        """);
    }

    [Fact]
    public void V2ScenePlacementOwner_ArmsFocusedCanvasForEscapeAndRetainsSecondaryCancellation()
    {
        ExecuteHarness("""
            const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 50");
            svg.dataset.sceneHeight = "50";
            const annotations = new Element("g"), callbackCalls = [];
            const callback = { invokeMethodAsync: (...args) => { callbackCalls.push(args); return Promise.resolve(true); } };
            pane.nodes["[data-scene-annotations=true]"] = annotations;
            window.initializeV2SceneViewport(svg, "room-one", callback);
            window.armV2ScenePlacement(svg, "check", "check-id");
            assert(svg.classList.contains("scene-layout-placement-armed") && svg.style.cursor === "crosshair", "arming must be local crosshair state");
            assert(svg.focusCount === 1, "arming must move keyboard focus from the row action to the scene canvas");
            svg.dispatch("pointermove", pointerEvent(svg, 20, 30, { pointerId: 1 }));
            svg.dispatch("wheel", pointerEvent(svg, 20, 30, { deltaY: 1, deltaMode: 0 }));
            assert(callbackCalls.length === 0, "arm/hover/viewport feedback must not invoke .NET");
            svg.dispatch("keydown", { key: "Escape", preventDefault() {} });
            assert(callbackCalls.length === 1 && callbackCalls[0][0] === "CancelPlacementAsync", "the focused scene canvas must receive Escape and cancel without a placement command");
            window.clearV2ScenePlacement(svg); callbackCalls.length = 0;
            window.armV2ScenePlacement(svg, "exit", "exit-id");
            svg.dispatch("contextmenu", { preventDefault() {} });
            assert(callbackCalls.length === 1 && callbackCalls[0][0] === "CancelPlacementAsync", "secondary scene input must retain its separate cancellation route");
            window.clearV2ScenePlacement(svg); callbackCalls.length = 0;
            window.armV2ScenePlacement(svg, "exit", "exit-id");
            svg.dispatch("pointerup", pointerEvent(svg, 20, 30, { button: 0, pointerId: 2 }));
            assert(callbackCalls.length === 1 && callbackCalls[0][0] === "CommitPlacementAsync", "only primary scene click commits placement");
            assert(callbackCalls[0][1] === "room-one" && callbackCalls[0][2] === 0 && callbackCalls[0][3] === "exit" && callbackCalls[0][4] === "exit-id" && callbackCalls[0][5] === 20 && callbackCalls[0][6] === -30, "click must carry its owner and use the direct negative-Y inverse");
            window.clearV2ScenePlacement(svg); callbackCalls.length = 0;
            window.armV2ScenePlacement(svg, "subroom", "subroom-id");
            svg.dispatch("pointerdown", pointerEvent(svg, 20, 30, { button: 0, pointerId: 3 }));
            svg.dispatch("pointerup", pointerEvent(svg, 20, 30, { button: 0, pointerId: 3 }));
            assert(callbackCalls.length === 1 && callbackCalls[0][0] === "CancelPlacementAsync", "an armed short draw must cancel without a geometry write");
        """);
    }

    [Fact]
    public void V2SceneMarkerDrag_RequiresSelectedMarkerThresholdAndOneCompletion_AndCancelsOrDisposesLocally()
    {
        ExecuteHarness("""
            const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 50");
            svg.dataset.sceneHeight = "50";
            const marker = new Element("g", { sceneSelectionKind: "exit", sceneId: "exit-id", sceneSelectionName: "Exit", sceneSelectionGeometry: "X: 10 · Y: 12" }, { "data-scene-selection-kind": "exit", "data-scene-id": "exit-id" }, svg);
            const calls = [], callback = { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(true); } };
            window.initializeV2SceneViewport(svg, "room-one", callback);

            // An unselected marker is an ordinary selection click, never a drag.
            svg.dispatch("pointerdown", pointerEvent(marker, 10, 12, { button: 0, pointerId: 1 }));
            svg.dispatch("pointermove", pointerEvent(marker, 30, 12, { pointerId: 1 }));
            svg.dispatch("pointerup", pointerEvent(marker, 30, 12, { button: 0, pointerId: 1 }));
            assert(calls.length === 1 && calls[0][0] === "SelectSceneItemAsync" && marker.classList.contains("selected"), "a selection click establishes the typed pane-local identity without a drag command"); calls.length = 0;
            svg.querySelector = selector => selector.includes("exit-id") ? marker : null;
            window.selectV2SceneLayoutItem(svg, "exit-id");

            svg.dispatch("pointerdown", pointerEvent(marker, 10, 12, { button: 0, pointerId: 2 }));
            svg.dispatch("pointermove", pointerEvent(marker, 14, 12, { pointerId: 2 }));
            assert(calls.length === 0 && !marker.style.transform, "sub-threshold feedback must not invoke .NET or move the marker");
            svg.dispatch("pointermove", pointerEvent(marker, 16, 12, { pointerId: 2 }));
            assert(calls.length === 0 && marker.style.transform, "thresholded drag feedback remains browser-only before completion");
            svg.dispatch("pointerup", pointerEvent(marker, 16, 12, { button: 0, pointerId: 2 }));
            assert(calls.length === 1 && calls[0][0] === "CommitMarkerDragAsync", "one completed drag invokes exactly one typed completion callback");
            assert(calls[0][1] === "room-one" && calls[0][2] === 0 && calls[0][3] === "exit" && calls[0][4] === "exit-id" && calls[0][5] === 16 && calls[0][6] === -12, "completion carries its owner and uses the direct negative-Y inverse");
            assert(marker.style.transform, "client-first marker feedback remains visible while its one command completes");

            svg.dispatch("pointerdown", pointerEvent(marker, 10, 12, { button: 0, pointerId: 3 }));
            svg.dispatch("pointermove", pointerEvent(marker, 20, 12, { pointerId: 3 }));
            svg.dispatch("pointercancel", pointerEvent(marker, 20, 12, { pointerId: 3 }));
            assert(calls.length === 1 && !marker.style.transform, "cancel clears feedback without a completion callback");
            window.disposeV2SceneViewport(svg);
            svg.dispatch("pointerup", pointerEvent(marker, 30, 12, { button: 0, pointerId: 4 }));
            assert(calls.length === 1 && !marker.style.transform, "route disposal removes stale drag behavior and feedback");
        """);
    }

    [Fact]
    public async Task V2SceneMarkerFailureRetryAndFrameFailure_CleanUpThroughEveryInteractionLifecycle()
    {
        await ExecuteHarnessAsync("""
            async function runFailureLifecycle() {
            const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 50"); svg.dataset.sceneHeight = "50";
            const status = new Element("span"), retry = new Element("button"), marker = new Element("g", { sceneSelectionKind: "exit", sceneId: "marker", sceneMarkerX: "10", sceneMarkerY: "12" }, { "data-scene-selection-kind": "exit", "data-scene-id": "marker" }, svg);
            const frame = new Element("g", { sceneSelectionKind: "subroom", sceneId: "frame", sceneFrameX: "2", sceneFrameY: "3", sceneFrameWidth: "4", sceneFrameHeight: "5" }, { "data-scene-selection-kind": "subroom", "data-scene-id": "frame" }, svg);
            const body = new Element("rect", {}, { "data-scene-frame-target": "move" }, frame);
            pane.nodes["[data-scene-status-text=true]"] = status; pane.nodes["[data-scene-marker-retry=true]"] = retry;
            svg.querySelector = selector => selector.includes("marker") ? marker : selector.includes("frame") ? frame : null;
            let markerOutcome = false, frameOutcome = false; const calls = [];
            const callback = { invokeMethodAsync: (...args) => { calls.push(args); if(args[0] === "CommitMarkerDragAsync") return markerOutcome === "throw" ? Promise.reject(new Error("marker")) : Promise.resolve(markerOutcome); if(args[0] === "CommitSubroomGeometryAsync") return frameOutcome === "throw" ? Promise.reject(new Error("frame")) : Promise.resolve(frameOutcome); return Promise.resolve(true); } };
            window.initializeV2SceneViewport(svg, "room", callback); window.selectV2SceneLayoutItem(svg, "marker");
            async function failMarker(pointerId) { svg.dispatch("pointerdown", pointerEvent(marker, 10, 12, { button: 0, pointerId })); svg.dispatch("pointermove", pointerEvent(marker, 16, 12, { pointerId })); svg.dispatch("pointerup", pointerEvent(marker, 16, 12, { button: 0, pointerId })); await Promise.resolve(); await Promise.resolve(); }
            await failMarker(1);
            assert(status.textContent === "marker save failed" && retry.hidden === false && !marker.style.transform, "a rejected marker command must restore durable visual and expose concise retry status");
            svg.dispatch("keydown", { key: "ArrowRight", preventDefault() {} });
            assert(status.textContent === "" && retry.hidden === true, "an accepted Arrow nudge clears stale marker retry state before its command admission");
            await Promise.resolve(); await Promise.resolve();
            await failMarker(10);
            retry.dispatch("click", eventOf(retry)); await Promise.resolve(); await Promise.resolve();
            const markerCalls = calls.filter(x => x[0] === "CommitMarkerDragAsync");
            assert(markerCalls.length === 3 && markerCalls[2][5] === 16 && markerCalls[2][6] === -12, "retry must dispatch the exact captured failed coordinates");
            markerOutcome = true; retry.dispatch("click", eventOf(retry)); await Promise.resolve(); await Promise.resolve();
            assert(status.textContent === "" && retry.hidden === true, "a successful retry must clear marker correction state");
            markerOutcome = "throw"; await failMarker(2);
            assert(retry.hidden === false, "a thrown marker command must retain the visible retry state");
            retry.dispatch("click", eventOf(retry)); await Promise.resolve(); await Promise.resolve();
            assert(retry.hidden === false && status.textContent === "marker save failed", "a failed retry must retain the exact-position retry state");
            svg.dispatch("pointerdown", pointerEvent(marker, 10, 12, { button: 0, pointerId: 3 }));
            assert(retry.hidden === true && status.textContent === "", "an already-selected marker gesture start clears stale retry state");
            markerOutcome = false; await failMarker(4); window.selectV2SceneLayoutItem(svg, "frame");
            assert(retry.hidden === true, "selection change clears stale retry state");
            markerOutcome = false; window.selectV2SceneLayoutItem(svg, "marker"); await failMarker(5); svg.dispatch("pointercancel", pointerEvent(marker, 16, 12, { pointerId: 5 }));
            assert(retry.hidden === true, "pointer cancellation clears stale retry state");
            markerOutcome = false; await failMarker(50); svg.dispatch("keydown", { key: "Escape", preventDefault() {} });
            assert(retry.hidden === true, "Escape cancellation clears stale retry state");
            window.selectV2SceneLayoutItem(svg, "frame"); frameOutcome = false;
            svg.dispatch("pointerdown", pointerEvent(body, 2, -3, { button: 0, pointerId: 6 })); svg.dispatch("pointermove", pointerEvent(body, 9, -3, { pointerId: 6 })); svg.dispatch("pointerup", pointerEvent(body, 9, -3, { button: 0, pointerId: 6 })); await Promise.resolve(); await Promise.resolve();
            assert(!frame.style.transform, "a rejected frame command must clear its client-first visual");
            frameOutcome = "throw"; svg.dispatch("pointerdown", pointerEvent(body, 2, -3, { button: 0, pointerId: 7 })); svg.dispatch("pointermove", pointerEvent(body, 9, -3, { pointerId: 7 })); svg.dispatch("pointerup", pointerEvent(body, 9, -3, { button: 0, pointerId: 7 })); await Promise.resolve(); await Promise.resolve();
            assert(!frame.style.transform, "a thrown frame command must clear its client-first visual");
            markerOutcome = false; window.selectV2SceneLayoutItem(svg, "marker"); await failMarker(8); window.initializeV2SceneViewport(svg, "other", callback);
            assert(retry.hidden === true && status.textContent === "", "route replacement clears stale marker retry state");
            markerOutcome = false; window.selectV2SceneLayoutItem(svg, "marker"); await failMarker(9); window.disposeV2SceneViewport(svg);
            assert(retry.hidden === true && status.textContent === "", "disposal clears stale marker retry state");
            } runFailureLifecycle().then(() => completeScenario(), error => completeScenario(String(error)));
        """);
    }

    [Fact]
    public async Task V2SceneMarkerFailure_NewSceneInteractionsAndArmingClearRetryWithoutReplacingArmedStatus()
    {
        await ExecuteHarnessAsync("""
            async function runStatusLifecycle() {
            const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 50"); svg.dataset.sceneHeight = "50";
            const status = new Element("span"), retry = new Element("button"), reset = new Element("button"), annotations = new Element("g"), annotationToggle = new Element("button"), zoneToggle = new Element("button");
            const marker = new Element("g", { sceneSelectionKind: "exit", sceneId: "marker", sceneMarkerX: "10", sceneMarkerY: "12" }, { "data-scene-selection-kind": "exit", "data-scene-id": "marker" }, svg);
            pane.nodes["[data-scene-status-text=true]"] = status; pane.nodes["[data-scene-marker-retry=true]"] = retry; pane.nodes["[data-scene-reset-view=true]"] = reset; pane.nodes["[data-scene-annotations=true]"] = annotations; pane.nodes["[data-scene-annotation-toggle=true]"] = annotationToggle; pane.nodes["[data-scene-zone-toggle=true]"] = zoneToggle;
            svg.querySelector = selector => selector.includes("marker") ? marker : null;
            const callback = { invokeMethodAsync: method => method === "CommitMarkerDragAsync" ? Promise.resolve(false) : Promise.resolve(true) };
            window.initializeV2SceneViewport(svg, "room", callback); window.selectV2SceneLayoutItem(svg, "marker");
            async function fail() { svg.dispatch("pointerdown", pointerEvent(marker, 10, 12, { button: 0, pointerId: 1 })); svg.dispatch("pointermove", pointerEvent(marker, 16, 12, { pointerId: 1 })); svg.dispatch("pointerup", pointerEvent(marker, 16, 12, { button: 0, pointerId: 1 })); await Promise.resolve(); await Promise.resolve(); assert(!retry.hidden, "failed marker save must expose retry before each new interaction"); }
            await fail(); window.armV2ScenePlacement(svg, "exit", "exit-id");
            assert(retry.hidden && status.textContent === "place exit annotation; click the scene, or press Escape to cancel", "marker arming clears retry and restores the exact rendered armed instruction");
            window.clearV2ScenePlacement(svg); window.selectV2SceneLayoutItem(svg, "marker"); await fail(); window.armV2ScenePlacement(svg, "subroom", "subroom-id");
            assert(retry.hidden && status.textContent === "draw subroom rectangle; drag to the opposite corner, or press Escape to cancel", "subroom arming clears retry and restores the exact rendered armed instruction");
            window.clearV2ScenePlacement(svg); window.selectV2SceneLayoutItem(svg, "marker"); await fail(); svg.dispatch("wheel", pointerEvent(svg, 50, 25, { deltaY: -1, deltaMode: 0 })); assert(retry.hidden && status.textContent === "", "wheel zoom is a new scene interaction that clears retry");
            await fail(); reset.dispatch("click", eventOf(reset)); assert(retry.hidden && status.textContent === "", "reset view is a new scene interaction that clears retry");
            await fail(); annotationToggle.dispatch("click", eventOf(annotationToggle)); assert(retry.hidden && status.textContent === "", "annotation visibility toggle is a new scene interaction that clears retry");
            await fail(); zoneToggle.dispatch("click", eventOf(zoneToggle)); assert(retry.hidden && status.textContent === "", "zone-map toggle is a new scene interaction that clears retry");
            } runStatusLifecycle().then(() => completeScenario(), error => completeScenario(String(error)));
        """);
    }

    [Fact]
    public async Task V2SceneNudgeOwner_EnforcesCanvasSelectionQueueingAndRoutingWithoutUndoState()
    {
        await ExecuteHarnessAsync("""
            async function runNudgeContract() {
            const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 50");
            svg.dataset.sceneHeight = "50";
            const exit = new Element("g", { sceneSelectionKind: "exit", sceneId: "exit", sceneMarkerX: "10", sceneMarkerY: "12", sceneSelectionName: "Exit", sceneSelectionGeometry: "X: 10 · Y: 12" }, { "data-scene-selection-kind": "exit", "data-scene-id": "exit" }, svg);
            const connection = new Element("g", { sceneSelectionKind: "connection", sceneId: "connection", sceneMarkerX: "20", sceneMarkerY: "21", sceneSelectionName: "Path", sceneSelectionGeometry: "X: 20 · Y: 21" }, { "data-scene-selection-kind": "connection", "data-scene-id": "connection" }, svg);
            const frame = new Element("g", { sceneSelectionKind: "subroom", sceneId: "frame", sceneFrameX: "2", sceneFrameY: "3", sceneFrameWidth: "4", sceneFrameHeight: "5", sceneSelectionName: "Frame", sceneSelectionGeometry: "X: 2 · Y: 3 · width: 4 · height: 5" }, { "data-scene-selection-kind": "subroom", "data-scene-id": "frame" }, svg);
            svg.querySelector = selector => selector.includes("exit") ? exit : selector.includes("connection") ? connection : selector.includes("frame") ? frame : null;
            const calls = [], callback = { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(true); } };
            window.initializeV2SceneViewport(svg, "room-one", callback);
            svg.dispatch("keydown", { key: "ArrowRight", preventDefault() { this.prevented = true; } });
            assert(calls.length === 0, "unselected canvas keyboard input must not persist a nudge");
            svg.dispatch("pointerup", pointerEvent(exit, 10, 12, { button: 0, pointerId: 1 }));
            svg.dispatch("keydown", { key: "ArrowRight", preventDefault() {} });
            svg.dispatch("keydown", { key: "ArrowUp", shiftKey: true, preventDefault() {} });
            svg.dispatch("keydown", { key: "ArrowLeft", preventDefault() {} });
            await Promise.resolve(); await Promise.resolve();
            const exitNudges=calls.filter(x => x[0] === "CommitSceneNudgeAsync" && x[3] === "exit"); assert(exitNudges.length === 3, "selected canvas Arrow input must route only marker nudges");
            assert(exitNudges[0][5] === 11 && exitNudges[0][6] === 12 && exitNudges[1][5] === 11 && exitNudges[1][6] === 17 && exitNudges[2][5] === 10 && exitNudges[2][6] === 17, "Arrow and Shift+Arrow must serialize from accepted pending geometry"); calls.splice(0,calls.length,...calls.filter(x=>x[0]!=="SelectSceneItemAsync"));
            svg.dispatch("pointerup", pointerEvent(connection, 20, 21, { button: 0, pointerId: 2 }));
            svg.dispatch("pointerdown", pointerEvent(connection, 20, 29, { button: 0, pointerId: 20 }));
            svg.dispatch("pointermove", pointerEvent(connection, 26, 29, { pointerId: 20 }));
            svg.dispatch("pointerup", pointerEvent(connection, 26, 29, { button: 0, pointerId: 20 }));
            await Promise.resolve();
            const connectionDrag=calls.filter(x=>x[0]==="CommitMarkerDragAsync"); assert(connectionDrag.length === 1 && connectionDrag[0][3] === "connection", "selected connection drag must use the alias-group marker route");
            svg.dispatch("keydown", { key: "ArrowDown", preventDefault() {} });
            svg.dispatch("pointerup", pointerEvent(frame, 2, 47, { button: 0, pointerId: 3 }));
            svg.dispatch("keydown", { key: "ArrowUp", shiftKey: true, preventDefault() {} });
            await Promise.resolve(); await Promise.resolve();
            const laterNudges=calls.filter(x=>x[0]==="CommitSceneNudgeAsync"); assert(laterNudges[3][3] === "connection" && laterNudges[3][5] === 20 && laterNudges[3][6] === 20, "connection nudge must use the connection route");
            assert(laterNudges[4][3] === "subroom" && laterNudges[4][5] === 2 && laterNudges[4][6] === 8, "selected frame nudge must use frame geometry coordinates");
            assert(!("undo" in svg.__v2SceneViewport) && !Object.keys(window).includes("undoV2SceneLayout") && !calls.some(x => x[0] === "CommitSceneUndoAsync"), "V2 retains no undo state, export, or callback route");
            window.disposeV2SceneViewport(svg);
            }
            runNudgeContract().then(() => completeScenario(), error => completeScenario(String(error)));
        """);
    }

    [Fact]
    public async Task V2SceneViewportOwner_SameKeyReconciliationRetainsInFlightFrameAndMarkerTransformsUntilRejectedOrThrownCompletion()
    {
        await ExecuteHarnessAsync("""
            async function runSameKeyClientFirstReconciliation() {
            const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 50"); svg.dataset.sceneHeight = "50";
            const frame = new Element("g", { sceneSelectionKind: "subroom", sceneId: "frame", sceneFrameX: "2", sceneFrameY: "3", sceneFrameWidth: "4", sceneFrameHeight: "5" }, { "data-scene-selection-kind": "subroom", "data-scene-id": "frame" }, svg);
            const body = new Element("rect", {}, { "data-scene-frame-target": "move" }, frame);
            const marker = new Element("g", { sceneSelectionKind: "exit", sceneId: "marker", sceneMarkerX: "10", sceneMarkerY: "12" }, { "data-scene-selection-kind": "exit", "data-scene-id": "marker" }, svg);
            svg.querySelector = selector => selector.includes("frame") ? frame : selector.includes("marker") ? marker : null;
            const pending = [], callback = { invokeMethodAsync: (...args) => { const completion = {}; pending.push({ method: args[0], completion }); return new Promise((resolve, reject) => { completion.resolve = resolve; completion.reject = reject; }); } };
            window.initializeV2SceneViewport(svg, "room", callback);
            function startFrame(framePointer) {
                window.selectV2SceneLayoutItem(svg, "frame");
                svg.dispatch("pointerdown", pointerEvent(body, 2, -3, { button: 0, pointerId: framePointer }));
                svg.dispatch("pointermove", pointerEvent(body, 9, -3, { pointerId: framePointer }));
                svg.dispatch("pointerup", pointerEvent(body, 9, -3, { button: 0, pointerId: framePointer }));
            }
            function startMarker(markerPointer) {
                window.selectV2SceneLayoutItem(svg, "marker");
                svg.dispatch("pointerdown", pointerEvent(marker, 10, 12, { button: 0, pointerId: markerPointer }));
                svg.dispatch("pointermove", pointerEvent(marker, 16, 12, { pointerId: markerPointer }));
                svg.dispatch("pointerup", pointerEvent(marker, 16, 12, { button: 0, pointerId: markerPointer }));
            }
            startFrame(1);
            assert(pending.length === 1 && frame.style.transform, "a completed frame gesture retains its client-first transform while its one durable command is in flight");
            window.initializeV2SceneViewport(svg, "room", callback);
            assert(frame.style.transform, "same-key browser-owner reconciliation clears only drawing/snap ephemera and retains the active frame client-first transform");
            pending[0].completion.resolve(false);
            await Promise.resolve(); await Promise.resolve();
            assert(!frame.style.transform, "a rejected frame completion clears its retained client-first transform after same-key reconciliation");
            startMarker(2);
            window.initializeV2SceneViewport(svg, "room", callback);
            assert(marker.style.transform, "same-key reconciliation retains the active marker client-first transform");
            pending[1].completion.resolve(false);
            await Promise.resolve(); await Promise.resolve();
            assert(!marker.style.transform, "a rejected marker completion clears its retained client-first transform after same-key reconciliation");
            startFrame(3);
            window.initializeV2SceneViewport(svg, "room", callback);
            assert(frame.style.transform, "a later same-key reconciliation retains the frame transform pending a thrown completion");
            pending[2].completion.reject(new Error("frame"));
            await Promise.resolve(); await Promise.resolve();
            assert(!frame.style.transform, "a thrown frame completion clears its retained client-first transform after same-key reconciliation");
            startMarker(4);
            window.initializeV2SceneViewport(svg, "room", callback);
            pending[3].completion.reject(new Error("marker"));
            await Promise.resolve(); await Promise.resolve();
            assert(!marker.style.transform, "a thrown marker completion clears its retained client-first transform after same-key reconciliation");
            startFrame(5);
            window.initializeV2SceneViewport(svg, "room", callback);
            pending[4].completion.resolve(true);
            await Promise.resolve(); await Promise.resolve();
            assert(!frame.style.transform, "an accepted frame completion reconciles its client-first visual after same-key reconciliation");
            startMarker(6);
            window.initializeV2SceneViewport(svg, "room", callback);
            pending[5].completion.resolve(true);
            await Promise.resolve(); await Promise.resolve();
            assert(!marker.style.transform, "an accepted marker completion reconciles its client-first visual after same-key reconciliation");
            } runSameKeyClientFirstReconciliation().then(() => completeScenario(), error => completeScenario(String(error)));
        """);
    }

    [Fact]
    public void V2SceneViewportOwner_PendingMarkerTransformClearsOnRouteReplacementAndDisposal()
    {
        ExecuteHarness("""
            const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 50"); svg.dataset.sceneHeight = "50";
            const marker = new Element("g", { sceneSelectionKind: "exit", sceneId: "marker", sceneMarkerX: "10", sceneMarkerY: "12" }, { "data-scene-selection-kind": "exit", "data-scene-id": "marker" }, svg);
            svg.querySelector = selector => selector.includes("marker") ? marker : null;
            const pending = [], callback = { invokeMethodAsync: (...args) => { const completion = {}; pending.push(completion); return new Promise((resolve, reject) => { completion.resolve = resolve; completion.reject = reject; }); } };
            function start(pointerId) { window.selectV2SceneLayoutItem(svg, "marker"); svg.dispatch("pointerdown", pointerEvent(marker, 10, 12, { button: 0, pointerId })); svg.dispatch("pointermove", pointerEvent(marker, 16, 12, { pointerId })); svg.dispatch("pointerup", pointerEvent(marker, 16, 12, { button: 0, pointerId })); }
            window.initializeV2SceneViewport(svg, "room", callback);
            start(1);
            assert(pending.length === 1 && marker.style.transform, "a pending marker command must retain its client-first transform after pointer-up");
            window.initializeV2SceneViewport(svg, "other-room", callback);
            assert(!marker.style.transform, "different-room initialization clears the pending old-room marker transform before command completion");
            pending[0].resolve(true);
            start(2);
            assert(pending.length === 2 && marker.style.transform, "a later pending marker command must again retain its transform before disposal");
            window.disposeV2SceneViewport(svg);
            assert(!marker.style.transform, "disposal clears the pending marker transform before command completion");
        """);
    }

    [Fact]
    public async Task V2SceneViewportOwner_StaleMarkerCompletionsCannotMutateReplacedOrDisposedOwnerGeneration()
    {
        await ExecuteHarnessAsync("""
            async function staleMarkerCompletionLifecycle() {
                async function verify(lifecycle, outcome) {
                    const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 50"); svg.dataset.sceneHeight = "50";
                    const status = new Element("span"), retry = new Element("button");
                    const marker = new Element("g", { sceneSelectionKind: "exit", sceneId: "marker", sceneMarkerX: "10", sceneMarkerY: "12" }, { "data-scene-selection-kind": "exit", "data-scene-id": "marker" }, svg);
                    pane.nodes["[data-scene-status-text=true]"] = status; pane.nodes["[data-scene-marker-retry=true]"] = retry;
                    svg.querySelector = selector => selector.includes("marker") ? marker : null;
                    let completion; const callback = { invokeMethodAsync: method => method === "CommitMarkerDragAsync" ? new Promise((resolve, reject) => completion = { resolve, reject }) : Promise.resolve(true) };
                    window.initializeV2SceneViewport(svg, "source", callback); window.selectV2SceneLayoutItem(svg, "marker");
                    svg.dispatch("pointerdown", pointerEvent(marker, 10, 12, { button: 0, pointerId: 1 }));
                    svg.dispatch("pointermove", pointerEvent(marker, 16, 12, { pointerId: 1 }));
                    svg.dispatch("pointerup", pointerEvent(marker, 16, 12, { button: 0, pointerId: 1 }));
                    assert(marker.style.transform && completion, "the source marker command must remain pending before owner replacement");
                    if (lifecycle === "route") window.initializeV2SceneViewport(svg, "destination", callback); else window.disposeV2SceneViewport(svg);
                    // Model current/reused DOM state after route initialization or disposal.
                    marker.style.transform = "current-transform"; status.textContent = "current-status"; retry.hidden = false;
                    if (outcome === "throw") completion.reject(new Error("marker")); else completion.resolve(outcome === "success");
                    await Promise.resolve(); await Promise.resolve();
                    assert(marker.style.transform === "current-transform" && status.textContent === "current-status" && retry.hidden === false,
                        `${lifecycle} ${outcome} completion must not mutate reused/current marker DOM, retry, or status state`);
                }
                for (const lifecycle of ["route", "dispose"])
                    for (const outcome of ["success", "rejected", "throw"])
                        await verify(lifecycle, outcome);
            } staleMarkerCompletionLifecycle().then(() => completeScenario(), error => completeScenario(String(error)));
        """);
    }

    [Fact]
    public async Task V2SceneViewportOwner_StaleFrameGeometryCompletionsCannotMutateReplacedOrDisposedOwnerGeneration()
    {
        await ExecuteHarnessAsync("""
            async function staleFrameCompletionLifecycle() {
                async function verify(lifecycle, outcome) {
                    const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 50"); svg.dataset.sceneHeight = "50";
                    const status = new Element("span"), retry = new Element("button");
                    const frame = new Element("g", { sceneSelectionKind: "subroom", sceneId: "frame", sceneFrameX: "10", sceneFrameY: "10", sceneFrameWidth: "5", sceneFrameHeight: "5" }, { "data-scene-selection-kind": "subroom", "data-scene-id": "frame" }, svg);
                    const body = new Element("rect", {}, { "data-scene-frame-target": "move", x: "10", y: "-15", width: "5", height: "5" }, frame);
                    pane.nodes["[data-scene-status-text=true]"] = status; pane.nodes["[data-scene-marker-retry=true]"] = retry;
                    svg.querySelector = selector => selector.includes("frame") ? frame : null;
                    let completion; const callback = { invokeMethodAsync: method => method === "CommitSubroomGeometryAsync" ? new Promise((resolve, reject) => completion = { resolve, reject }) : Promise.resolve(true) };
                    window.initializeV2SceneViewport(svg, "source", callback); window.selectV2SceneLayoutItem(svg, "frame");
                    svg.dispatch("pointerdown", pointerEvent(body, 10, -10, { button: 0, pointerId: 1 }));
                    svg.dispatch("pointermove", pointerEvent(body, 16, -10, { pointerId: 1 }));
                    svg.dispatch("pointerup", pointerEvent(body, 16, -10, { button: 0, pointerId: 1 }));
                    assert(frame.style.transform && completion, "the source frame move command must remain pending before owner replacement");
                    if (lifecycle === "route") window.initializeV2SceneViewport(svg, "destination", callback); else window.disposeV2SceneViewport(svg);
                    // Model current/reused DOM state after route initialization or disposal.
                    frame.style.transform = "current-transform"; status.textContent = "current-status"; retry.hidden = false;
                    if (outcome === "throw") completion.reject(new Error("frame")); else completion.resolve(outcome === "success");
                    await Promise.resolve(); await Promise.resolve();
                    assert(frame.style.transform === "current-transform" && status.textContent === "current-status" && retry.hidden === false,
                        `${lifecycle} ${outcome} completion must not mutate reused/current frame DOM, retry, or status state`);
                }
                for (const lifecycle of ["route", "dispose"])
                    for (const outcome of ["success", "rejected", "throw"])
                        await verify(lifecycle, outcome);
            } staleFrameCompletionLifecycle().then(() => completeScenario(), error => completeScenario(String(error)));
        """);
    }

    [Fact]
    public async Task V2SceneViewportOwner_StaleRetryAndNudgeCompletionsCannotMutateCurrentOwnerState()
    {
        await ExecuteHarnessAsync("""
            async function staleContinuationLifecycle() {
                async function verify(operation, lifecycle, outcome) {
                    const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 50"); svg.dataset.sceneHeight = "50";
                    const status = new Element("span"), retry = new Element("button");
                    const marker = new Element("g", { sceneSelectionKind: "exit", sceneId: "marker", sceneMarkerX: "10", sceneMarkerY: "12" }, { "data-scene-selection-kind": "exit", "data-scene-id": "marker" }, svg);
                    pane.nodes["[data-scene-status-text=true]"] = status; pane.nodes["[data-scene-marker-retry=true]"] = retry;
                    svg.querySelector = selector => selector.includes("marker") ? marker : null;
                    let completion, markerSetup = operation === "retry" ? false : null;
                    const callback = { invokeMethodAsync: method => {
                        if (method === "CommitMarkerDragAsync" && markerSetup !== null) { const result = markerSetup; markerSetup = null; return Promise.resolve(result); }
                        return new Promise((resolve, reject) => completion = { resolve, reject, method });
                    } };
                    window.initializeV2SceneViewport(svg, "source", callback); window.selectV2SceneLayoutItem(svg, "marker");
                    if (operation === "retry") {
                        svg.dispatch("pointerdown", pointerEvent(marker, 10, 12, { button: 0, pointerId: 1 })); svg.dispatch("pointermove", pointerEvent(marker, 16, 12, { pointerId: 1 })); svg.dispatch("pointerup", pointerEvent(marker, 16, 12, { button: 0, pointerId: 1 }));
                        await Promise.resolve(); await Promise.resolve(); retry.dispatch("click", eventOf(retry));
                    } else {
                        svg.dispatch("keydown", { key: "ArrowRight", preventDefault() {} });
                    }
                    await Promise.resolve();
                    assert(completion, `${operation} must have one pending continuation before ${lifecycle}`);
                    if (lifecycle === "route") window.initializeV2SceneViewport(svg, "destination", callback); else window.disposeV2SceneViewport(svg);
                    marker.style.transform = "current-transform"; marker.classList.add("selected"); status.textContent = "current-status"; retry.hidden = false;
                    if (outcome === "throw") completion.reject(new Error(operation)); else completion.resolve(outcome === "success");
                    await Promise.resolve(); await Promise.resolve();
                    assert(marker.style.transform === "current-transform" && marker.classList.contains("selected") && status.textContent === "current-status" && retry.hidden === false,
                        `${operation} ${lifecycle} ${outcome} completion must not revive or mutate current transforms, selection, or status/retry DOM`);
                }
                for (const operation of ["retry", "nudge"])
                    for (const lifecycle of ["route", "dispose"])
                        for (const outcome of ["success", "rejected", "throw"])
                            await verify(operation, lifecycle, outcome);
            } staleContinuationLifecycle().then(() => completeScenario(), error => completeScenario(String(error)));
        """);
    }

    [Fact]
    public async Task V2SceneViewportOwner_CurrentRetryAndNudgeCompletionsSurviveSameOwnerReconciliation()
    {
        await ExecuteHarnessAsync("""
            async function currentContinuationLifecycle() {
                async function verify(operation, outcome) {
                    const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 50"); svg.dataset.sceneHeight = "50";
                    const status = new Element("span"), retry = new Element("button");
                    const marker = new Element("g", { sceneSelectionKind: "exit", sceneId: "marker", sceneMarkerX: "10", sceneMarkerY: "12" }, { "data-scene-selection-kind": "exit", "data-scene-id": "marker" }, svg);
                    pane.nodes["[data-scene-status-text=true]"] = status; pane.nodes["[data-scene-marker-retry=true]"] = retry;
                    svg.querySelector = selector => selector.includes("marker") ? marker : null;
                    let completion, markerSetup = operation === "retry" ? false : null;
                    const callback = { invokeMethodAsync: method => {
                        if (method === "CommitMarkerDragAsync" && markerSetup !== null) { const result = markerSetup; markerSetup = null; return Promise.resolve(result); }
                        return new Promise((resolve, reject) => completion = { resolve, reject });
                    } };
                    window.initializeV2SceneViewport(svg, "room", callback); window.selectV2SceneLayoutItem(svg, "marker");
                    if (operation === "retry") {
                        svg.dispatch("pointerdown", pointerEvent(marker, 10, 12, { button: 0, pointerId: 1 })); svg.dispatch("pointermove", pointerEvent(marker, 16, 12, { pointerId: 1 })); svg.dispatch("pointerup", pointerEvent(marker, 16, 12, { button: 0, pointerId: 1 }));
                        await Promise.resolve(); await Promise.resolve(); retry.dispatch("click", eventOf(retry));
                    } else svg.dispatch("keydown", { key: "ArrowRight", preventDefault() {} });
                    await Promise.resolve(); window.initializeV2SceneViewport(svg, "room", callback);
                    if (outcome === "throw") completion.reject(new Error(operation)); else completion.resolve(outcome === "success");
                    await Promise.resolve(); await Promise.resolve();
                    if (operation === "retry") assert(outcome === "success" ? status.textContent === "" && retry.hidden : status.textContent === "marker save failed" && !retry.hidden, `same-owner ${outcome} retry reconciliation retains its settled correction lifecycle`);
                    else assert(marker.classList.contains("selected") && status.textContent === "", `same-owner ${outcome} queued nudge completion retains current selection and clean status`);
                }
                for (const operation of ["retry", "nudge"])
                    for (const outcome of ["success", "rejected", "throw"])
                        await verify(operation, outcome);
            } currentContinuationLifecycle().then(() => completeScenario(), error => completeScenario(String(error)));
        """);
    }

    [Fact]
    public async Task V2SceneNudgeOwner_CancelsRejectedSuccessorsAndRebases()
    {
        await ExecuteHarnessAsync("""
            async function runFailureContract() {
            const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 50"); svg.dataset.sceneHeight = "50";
            const marker = new Element("g", { sceneSelectionKind: "check", sceneId: "check", sceneMarkerX: "10", sceneMarkerY: "12", sceneSelectionName: "Check", sceneSelectionGeometry: "X: 10 · Y: 12" }, { "data-scene-selection-kind": "check", "data-scene-id": "check" }, svg);
            svg.querySelector = selector => selector.includes("check") ? marker : null;
            const calls = []; let rejectNudge = true;
            const callback = { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(args[0] === "CommitSceneNudgeAsync" ? !rejectNudge : true); } };
            window.initializeV2SceneViewport(svg, "room", callback); window.selectV2SceneLayoutItem(svg, "check");
            svg.dispatch("keydown", { key: "ArrowRight", preventDefault() {} }); svg.dispatch("keydown", { key: "ArrowRight", preventDefault() {} });
            await Promise.resolve(); await Promise.resolve(); await Promise.resolve();
            assert(calls.length === 1 && calls[0][5] === 11, "a rejected nudge cancels every queued successor before it can commit");
            rejectNudge = false; svg.dispatch("keydown", { key: "ArrowLeft", preventDefault() {} }); await Promise.resolve(); await Promise.resolve();
            assert(calls.length === 2 && calls[1][5] === 9 && calls[1][6] === 12, "the next nudge rebases from authoritative rendered geometry after rejection");
            assert(!("undo" in svg.__v2SceneViewport) && !Object.keys(window).includes("undoV2SceneLayout") && !calls.some(x => x[0] === "CommitSceneUndoAsync"), "nudge failure recovery does not create an undo route or state");
            } runFailureContract().then(() => completeScenario(), error => completeScenario(String(error)));
        """);
    }

    [Fact]
    public void V2SceneViewport_SubroomSnappingUsesSnapshotCandidatesScaleLocksHighlightsCtrlAndCompletionOnly()
    {
        ExecuteHarness("""
            const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 100");
            svg.dataset.sceneHeight = "100"; svg.dataset.sceneWidth = "100";
            svg.getScreenCTM = () => ({ a: 2, b: 0, c: 0, d: 2, inverse: () => ({}) });
            const guides = []; document.createElementNS = () => { const line = new Element("line"); line.remove = () => guides.splice(guides.indexOf(line), 1); return line; }; svg.appendChild = line => guides.push(line);
            const calls = [], callback = { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(true); } };
            const room = new Element("rect", {}, { "class": "scene-layout-room-bounds", width: "100", height: "100" }, svg); room.classList.add("scene-layout-room-bounds");
            const roomEdges = {}; for (const edge of ["l", "r", "b", "t"]) roomEdges[edge] = new Element("rect", {}, { "data-scene-snap-room-edge": edge }, svg);
            function frame(id, x, y, w, h) {
                const item = new Element("g", { sceneSelectionKind: "subroom", sceneId: id, sceneFrameX: String(x), sceneFrameY: String(y), sceneFrameWidth: String(w), sceneFrameHeight: String(h) }, { "data-scene-layout-frame": "true", "data-scene-selection-kind": "subroom", "data-scene-id": id }, svg);
                const body = new Element("rect", {}, { "data-scene-frame-target": "move" }, item), edges = { l: new Element("rect", {}, {}, item), r: new Element("rect", {}, {}, item), b: new Element("rect", {}, {}, item), t: new Element("rect", {}, {}, item) };
                edges.l.classList.add("edge-w"); edges.r.classList.add("edge-e"); edges.b.classList.add("edge-s"); edges.t.classList.add("edge-n");
                item.querySelector = selector => selector === ".edge-w" ? edges.l : selector === ".edge-e" ? edges.r : selector === ".edge-s" ? edges.b : selector === ".edge-n" ? edges.t : selector.includes("move") ? body : null;
                return { item, body, edges };
            }
            const manipulated = frame("moving", 10, 10, 5, 5), target = frame("target", 20, 20, 10, 10);
            const all = [manipulated.item, target.item];
            svg.querySelectorAll = selector => selector === "[data-scene-layout-frame=true]" ? all : selector === ".scene-layout-snap-guide" ? guides : selector === ".scene-layout-snap-target-edge" ? [...Object.values(target.edges), ...Object.values(roomEdges)].filter(x => x.classList.contains("scene-layout-snap-target-edge")) : [];
            svg.querySelector = selector => selector === ".scene-layout-room-bounds" ? room : selector.includes("moving") ? manipulated.item : selector.includes("target") ? target.item : selector.startsWith("[data-scene-snap-room-edge") ? roomEdges[selector.split("'")[1]] : null;
            pane.nodes["[data-scene-annotations=true]"] = new Element("g");
            window.initializeV2SceneViewport(svg, "room", callback);
            window.armV2ScenePlacement(svg, "subroom", "draw");
            svg.dispatch("pointerdown", pointerEvent(svg, 19, -20, { button: 0, pointerId: 1 }));
            target.item.dataset.sceneFrameX = "50"; target.item.dataset.sceneFrameY = "50";
            svg.dispatch("pointermove", pointerEvent(svg, 31, -30, { pointerId: 1 }));
            assert(calls.length === 0 && guides.length > 0 && target.edges.l.classList.contains("scene-layout-snap-target-edge"), "draw feedback must snap at six CSS pixels/scale with guides/highlights and without .NET completion");
            svg.dispatch("pointerup", pointerEvent(svg, 31, -30, { button: 0, pointerId: 1 }));
            assert(calls.length === 1 && calls[0][0] === "CommitSubroomGeometryAsync" && calls[0][4] === 20 && calls[0][5] === 20 && calls[0][6] === 10 && calls[0][7] === 10, "draw commits independently snapped lower/upper edges once");
            assert(guides.length === 0 && !target.edges.l.classList.contains("scene-layout-snap-target-edge"), "pointer completion must clear guides, highlights, and locks");
            calls.length = 0; target.item.dataset.sceneFrameX = "20"; target.item.dataset.sceneFrameY = "20"; window.clearV2ScenePlacement(svg); window.armV2ScenePlacement(svg, "subroom", "same-room-refresh");
            svg.dispatch("pointerdown", pointerEvent(svg, 19, -20, { button: 0, pointerId: 11 }));
            svg.dispatch("pointermove", pointerEvent(svg, 31, -30, { pointerId: 11 }));
            assert(guides.length > 0 && target.edges.l.classList.contains("scene-layout-snap-target-edge"), "active drawing owns a draft and snap feedback before reconciliation");
            window.initializeV2SceneViewport(svg, "room", callback);
            assert(guides.length === 0 && !target.edges.l.classList.contains("scene-layout-snap-target-edge"), "same-room reconciliation clears drawing draft guides and highlights before refresh completion");
            target.item.dataset.sceneFrameX = "20"; target.item.dataset.sceneFrameY = "20";
            calls.length = 0; window.clearV2ScenePlacement(svg); window.selectV2SceneLayoutItem(svg, "moving");
            svg.dispatch("pointerdown", pointerEvent(manipulated.body, 10, -10, { button: 0, pointerId: 2 }));
            svg.dispatch("pointermove", pointerEvent(manipulated.body, 19, -10, { pointerId: 2 }));
            assert(calls.length === 0 && target.edges.l.classList.contains("scene-layout-snap-target-edge"), "move excludes its own frame and highlights the target edge before commit");
            svg.dispatch("pointerup", pointerEvent(manipulated.body, 19, -10, { button: 0, pointerId: 2 }));
            assert(calls.length === 1 && calls[0][4] === 20, "move commits the snapped edge geometry");
            calls.length = 0;
            const corner = new Element("rect", {}, { "data-scene-frame-target": "tr" }, manipulated.item);
            svg.dispatch("pointerdown", pointerEvent(corner, 15, -15, { button: 0, pointerId: 4 }));
            svg.dispatch("pointermove", pointerEvent(corner, 29, -29, { pointerId: 4 }));
            assert(calls.length === 0 && target.edges.r.classList.contains("scene-layout-snap-target-edge"), "corner resize independently locks the upper X/Y target edges before completion");
            svg.dispatch("pointerup", pointerEvent(corner, 29, -29, { button: 0, pointerId: 4 }));
            assert(calls.length === 1 && calls[0][6] === 20 && calls[0][7] === 20, "corner resize commits snapped positive width and height");
            calls.length = 0;
            svg.dispatch("pointerdown", pointerEvent(manipulated.body, 10, -10, { button: 0, pointerId: 5 }));
            svg.dispatch("pointermove", pointerEvent(manipulated.body, 19, -10, { pointerId: 5 }));
            svg.dispatch("pointermove", pointerEvent(manipulated.body, 25, -10, { pointerId: 5 }));
            assert(target.edges.l.classList.contains("scene-layout-snap-target-edge"), "an edge lock remains while its raw edge stays within twice the projected threshold");
            svg.dispatch("pointermove", pointerEvent(manipulated.body, 27, -10, { pointerId: 5 }));
            assert(target.edges.r.classList.contains("scene-layout-snap-target-edge"), "an edge lock releases and deterministically reselects after twice the threshold");
            svg.dispatch("pointercancel", pointerEvent(manipulated.body, 27, -10, { pointerId: 5 }));
            assert(calls.length === 0 && guides.length === 0, "cancellation clears a released lock without a geometry callback");
            calls.length = 0;
            svg.dispatch("pointerdown", pointerEvent(manipulated.body, 10, -10, { button: 0, pointerId: 3, ctrlKey: true }));
            svg.dispatch("pointermove", pointerEvent(manipulated.body, 19, -10, { pointerId: 3, ctrlKey: true }));
            assert(!target.edges.l.classList.contains("scene-layout-snap-target-edge"), "Ctrl bypasses guides, highlights, locks, and snap calculation");
            svg.dispatch("pointerup", pointerEvent(manipulated.body, 19, -10, { button: 0, pointerId: 3, ctrlKey: true }));
            assert(calls.length === 1 && calls[0][4] === 19, "Ctrl commits unsnapped move geometry through the existing route");
            calls.length = 0; window.clearV2ScenePlacement(svg);
            window.armV2ScenePlacement(svg, "subroom", "collapsed");
            svg.dispatch("pointerdown", pointerEvent(svg, 18, -18, { button: 0, pointerId: 6 }));
            svg.dispatch("pointermove", pointerEvent(svg, 22, -22, { pointerId: 6 }));
            svg.dispatch("pointerup", pointerEvent(svg, 22, -22, { button: 0, pointerId: 6 }));
            assert(calls.length === 1, "opposing edges snapped to one immutable candidate commit exactly one geometry callback");
            assert(calls[0][0] === "CommitSubroomGeometryAsync" && calls[0][4] === 20 && calls[0][5] === 20 && calls[0][6] > 0 && calls[0][7] > 0, "opposing edges snapped to one immutable candidate commit strictly positive complete geometry");
            calls.length = 0; window.clearV2ScenePlacement(svg); target.item.dataset.sceneFrameX = "20"; target.item.dataset.sceneFrameY = "20";
            function activateSnap(pointerId) { window.selectV2SceneLayoutItem(svg, "moving"); svg.dispatch("pointerdown", pointerEvent(manipulated.body, 10, -10, { button: 0, pointerId })); svg.dispatch("pointermove", pointerEvent(manipulated.body, 19, -10, { pointerId })); assert(guides.length > 0 && target.edges.l.classList.contains("scene-layout-snap-target-edge"), "active gesture must visibly retain a guide, highlight, and lock before cancellation"); }
            function assertCleared(message) { assert(calls.length === 0 && guides.length === 0 && !target.edges.l.classList.contains("scene-layout-snap-target-edge"), message); }
            activateSnap(7); svg.dispatch("keydown", { key: "Escape", preventDefault() {} }); assertCleared("Escape clears active snap feedback without a geometry callback");
            activateSnap(8); svg.dispatch("contextmenu", { preventDefault() {} }); assertCleared("secondary-click cancellation clears active snap feedback without a geometry callback");
            activateSnap(9); window.selectV2SceneLayoutItem(svg, "target"); assertCleared("selection change clears active snap feedback without a geometry callback");
            activateSnap(10); window.disposeV2SceneViewport(svg); assertCleared("disposal clears active snap feedback without a geometry callback");
        """);
    }

    [Fact]
    public async Task V2SceneViewport_ResizeRendersProspectiveFrameGeometryAndRestoresItForEveryNonCommitLifecycle()
    {
        await ExecuteHarnessAsync("""
            async function resizeVisualLifecycle() {
                const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 100"); svg.dataset.sceneHeight = "100"; svg.dataset.sceneWidth = "100"; svg.getScreenCTM = () => ({ a: 2, b: 0, c: 0, d: 2, inverse: () => ({}) });
                function frame() {
                    const item = new Element("g", { sceneSelectionKind: "subroom", sceneId: "frame", sceneFrameX: "10", sceneFrameY: "10", sceneFrameWidth: "5", sceneFrameHeight: "5" }, { "data-scene-selection-kind": "subroom", "data-scene-id": "frame" }, svg);
                    const body = new Element("rect", {}, { "data-scene-frame-target": "move", x: "10", y: "-15", width: "5", height: "5" }, item), n = new Element("rect", {}, { x: "10", y: "-16", width: "5", height: "1" }, item), s = new Element("rect", {}, { x: "10", y: "-10", width: "5", height: "1" }, item), e = new Element("rect", {}, { x: "15", y: "-15", width: "1", height: "5" }, item), w = new Element("rect", {}, { x: "9", y: "-15", width: "1", height: "5" }, item), nw = new Element("rect", {}, { x: "9", y: "-16", width: "2", height: "2" }, item), ne = new Element("rect", {}, { "data-scene-frame-target": "tr", x: "14", y: "-16", width: "2", height: "2" }, item), sw = new Element("rect", {}, { x: "9", y: "-11", width: "2", height: "2" }, item), se = new Element("rect", {}, { x: "14", y: "-11", width: "2", height: "2" }, item), label = new Element("text", {}, { x: "12.5", y: "-12.5", transform: "scale(1 -1) translate(0 25)" }, item);
                    item.querySelector = selector => selector === "[data-scene-frame-target='move']" ? body : selector === ".edge-n" ? n : selector === ".edge-s" ? s : selector === ".edge-e" ? e : selector === ".edge-w" ? w : selector === ".corner-nw" ? nw : selector === ".corner-ne" ? ne : selector === ".corner-sw" ? sw : selector === ".corner-se" ? se : selector === "text" ? label : null;
                    return { item, body, n, s, e, w, nw, ne, sw, se, label };
                }
                let outcome = false, pending = null; const parts = frame();
                svg.querySelector = selector => selector.includes("frame") ? parts.item : null; svg.querySelectorAll = () => [];
                const callback = { invokeMethodAsync: method => method === "CommitSubroomGeometryAsync" ? (outcome === "pending" ? new Promise(resolve => pending = resolve) : outcome === "throw" ? Promise.reject(new Error("resize")) : Promise.resolve(outcome)) : Promise.resolve(true) };
                window.initializeV2SceneViewport(svg, "room", callback); window.selectV2SceneLayoutItem(svg, "frame");
                function start(pointerId) { svg.dispatch("pointerdown", pointerEvent(parts.ne, 15, -15, { button: 0, pointerId })); svg.dispatch("pointermove", pointerEvent(parts.ne, 25, -25, { pointerId })); }
                function durable(message) { assert(parts.body.getAttribute("x") === "10" && parts.body.getAttribute("y") === "-15" && parts.body.getAttribute("width") === "5" && parts.body.getAttribute("height") === "5" && parts.n.getAttribute("y") === "-16" && parts.s.getAttribute("y") === "-10" && parts.e.getAttribute("x") === "15" && parts.w.getAttribute("x") === "9" && parts.ne.getAttribute("x") === "14" && parts.se.getAttribute("y") === "-11" && parts.label.getAttribute("x") === "12.5" && parts.label.getAttribute("y") === "-12.5" && !parts.label.getAttribute("transform"), message); }
                start(1);
                assert(parts.body.getAttribute("x") === "10" && parts.body.getAttribute("y") === "-25" && parts.body.getAttribute("width") === "15" && parts.body.getAttribute("height") === "15" && parts.n.getAttribute("y") === "-26" && parts.s.getAttribute("y") === "-10" && parts.e.getAttribute("x") === "25" && parts.w.getAttribute("x") === "9" && parts.nw.getAttribute("x") === "9" && parts.nw.getAttribute("y") === "-26" && parts.ne.getAttribute("x") === "24" && parts.ne.getAttribute("y") === "-26" && parts.sw.getAttribute("x") === "9" && parts.sw.getAttribute("y") === "-11" && parts.se.getAttribute("x") === "24" && parts.se.getAttribute("y") === "-11" && parts.label.getAttribute("x") === "17.5" && parts.label.getAttribute("y") === "-17.5" && !parts.label.getAttribute("transform"), "corner resize must immediately update the body, all exterior target geometry, and upright centered label to the snapped prospective frame");
                svg.dispatch("pointerup", pointerEvent(parts.ne, 25, -25, { button: 0, pointerId: 1 })); await Promise.resolve(); await Promise.resolve(); durable("a rejected resize restores every durable frame primitive");
                outcome = "throw"; start(2); svg.dispatch("pointerup", pointerEvent(parts.ne, 25, -25, { button: 0, pointerId: 2 })); await Promise.resolve(); await Promise.resolve(); durable("a thrown resize restores every durable frame primitive");
                outcome = true; start(3); svg.dispatch("pointercancel", pointerEvent(parts.ne, 25, -25, { pointerId: 3 })); durable("pointer cancellation restores prospective resize geometry without a command");
                outcome = true; start(4); svg.dispatch("pointerup", pointerEvent(parts.ne, 25, -25, { button: 0, pointerId: 4 })); await Promise.resolve(); await Promise.resolve(); window.initializeV2SceneViewport(svg, "room", callback); durable("a successful resize reconciles its durable frame with upright label coordinates");
                outcome = "pending"; start(5); svg.dispatch("pointerup", pointerEvent(parts.ne, 25, -25, { button: 0, pointerId: 5 })); assert(parts.body.getAttribute("width") === "15" && parts.label.getAttribute("x") === "17.5" && parts.label.getAttribute("y") === "-17.5" && !parts.label.getAttribute("transform"), "the prospective upright label remains centered through its one geometry command"); window.initializeV2SceneViewport(svg, "room", callback); assert(parts.label.getAttribute("x") === "17.5" && parts.label.getAttribute("y") === "-17.5" && !parts.label.getAttribute("transform"), "same-room authoritative reconciliation retains the current prospective upright label"); window.initializeV2SceneViewport(svg, "other", callback); durable("route replacement restores the durable frame while a geometry command remains pending");
                window.initializeV2SceneViewport(svg, "room", callback); window.selectV2SceneLayoutItem(svg, "frame"); outcome = "pending"; start(6); svg.dispatch("pointerup", pointerEvent(parts.ne, 25, -25, { button: 0, pointerId: 6 })); window.disposeV2SceneViewport(svg); durable("disposal restores durable geometry and removes the pending resize visual"); pending?.(true); await Promise.resolve();
            } resizeVisualLifecycle().then(() => completeScenario(), error => completeScenario(String(error)));
        """);
    }

    [Fact]
    public async Task V2SceneViewport_RejectedAndThrownSubroomDrawsClearVisibleDraftGuidesAndHighlightsImmediately()
    {
        await ExecuteHarnessAsync("""
            async function rejectedDrawLifecycle() {
                const pane = new ScenePane(), svg = new SceneSvg(pane, "0 0 100 100"); svg.dataset.sceneHeight = "100"; svg.dataset.sceneWidth = "100";
                const target = new Element("g", { sceneFrameX: "20", sceneFrameY: "20", sceneFrameWidth: "10", sceneFrameHeight: "10" }, { "data-scene-layout-frame": "true" }, svg), targetEdge = new Element("rect", {}, {}, target); target.querySelector = selector => selector === ".edge-w" ? targetEdge : null;
                const guides = [], drafts = []; document.createElementNS = () => { const node = new Element("line"); node.remove = () => { for (const list of [drafts, guides]) { const index = list.indexOf(node); if (index >= 0) list.splice(index, 1); } }; return node; }; svg.appendChild = function(node) { if (node.classList.contains("scene-layout-draft")) drafts.push(node); else guides.push(node); };
                svg.querySelector = selector => selector.includes("target") ? target : null; svg.querySelectorAll = selector => selector === "[data-scene-layout-frame=true]" ? [target] : selector === ".scene-layout-snap-guide" ? guides : selector === ".scene-layout-snap-target-edge" ? (targetEdge.classList.contains("scene-layout-snap-target-edge") ? [targetEdge] : []) : [];
                let outcome = false; const callback = { invokeMethodAsync: method => method === "CommitSubroomGeometryAsync" ? (outcome === "throw" ? Promise.reject(new Error("draw")) : Promise.resolve(false)) : Promise.resolve(true) };
                window.initializeV2SceneViewport(svg, "room", callback);
                async function draw(pointerId, message) { window.armV2ScenePlacement(svg, "subroom", "draw"); svg.dispatch("pointerdown", pointerEvent(svg, 19, -20, { button: 0, pointerId })); svg.dispatch("pointermove", pointerEvent(svg, 31, -30, { pointerId })); assert(drafts.length === 1, "armed drawing must visibly own a draft before its command"); assert(guides.length > 0, "armed drawing must visibly own a snap guide before its command"); svg.dispatch("pointerup", pointerEvent(svg, 31, -30, { button: 0, pointerId })); assert(drafts.length === 0 && guides.length === 0, message); await Promise.resolve(); await Promise.resolve(); assert(drafts.length === 0 && guides.length === 0, "a rejected draw result cannot restore stale browser feedback"); }
                await draw(1, "a false draw command clears draft/guides/highlight before its result returns"); outcome = "throw"; await draw(2, "a thrown draw command clears draft/guides/highlight before its result returns");
            } rejectedDrawLifecycle().then(() => completeScenario(), error => completeScenario(String(error)));
        """);
    }

    [Fact]
    public void RenderedActiveTransitionAndConnectionMarkup_DrivesSharedOwnerBeforeTailAndTerminalFeedback()
    {
        using var context = new TestContext();
        var transitionId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var transition = context.RenderComponent<TransitionTablePresentation>(p => p.Add(x => x.View,
            new TransitionTableView([Transition(transitionId)], [], ["room"], new Dictionary<Guid, IReadOnlyList<string>>())));
        var connection = context.RenderComponent<ConnectionTablePresentation>(p => p.Add(x => x.View,
            new ConnectionTableView([Connection(connectionId)], [], ["subroom"])));

        var rendered = JsonSerializer.Serialize(new[]
        {
            RenderedTable(transition, "transition"),
            RenderedTable(connection, "connection")
        });

        ExecuteHarness("""
            const root = new MockRoot(), calls = [], owner = window.createV2ChildInteractionOwner(root);
            const rendered = 
            """ + rendered + ";" + """
            for (const item of rendered) {
                calls.length = 0;
                const actual = mountRenderedTable(root, item);
                owner.register({ invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } }, item.kind);
                const transfer = transferOf();
                root.dispatch("dragstart", eventOf(actual.grip, transfer));
                assert(transfer.values["text/plain"] === item.row.attributes[`data-v2-${item.kind}-row`], "rendered active grip must start a drag using its rendered row contract");
                root.dispatch("dragover", eventOf(actual.row, transfer));
                assert(actual.row.classList.contains(item.before), "rendered active row must receive shared-owner feedback");
                assert(calls.length === 0, "feedback from rendered markup must not dispatch .NET");
                root.dispatch("dragover", eventOf(actual.tail, transfer));
                assert(actual.tail.classList.contains(item.before), "rendered before-tail target must receive shared-owner feedback");
                root.dispatch("dragover", eventOf(actual.table, transfer));
                assert(actual.table.classList.contains(item.tail), "rendered terminal table target must receive shared-owner feedback");
                root.dispatch("drop", eventOf(actual.table, transfer));
                assert(calls.length === 1, "rendered delivered table feedback commits its one drop command");
            }
        """);
    }

    private static void ExecuteHarness(string scenario)
    {
        var source = File.ReadAllText(Path.Combine(FindSolutionRoot(), "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "wwwroot", "editor.js"));
        var engine = new Engine();
        engine.Execute(Harness);
        engine.Execute(source);
        engine.Execute(scenario);
        engine.Advanced.ProcessTasks();
    }

    private static async Task ExecuteHarnessAsync(string scenario)
    {
        var source = File.ReadAllText(Path.Combine(FindSolutionRoot(), "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "wwwroot", "editor.js"));
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Engine();
        engine.SetValue("completeScenario", (Action<string?>)(error => completion.TrySetResult(error)));
        engine.Execute(Harness);
        engine.Execute(source);
        engine.Execute(scenario);
        for (var attempt = 0; attempt < 100 && !completion.Task.IsCompleted; attempt++)
            engine.Advanced.ProcessTasks();
        var error = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(error is null, error);
    }

    private static object RenderedTable<TComponent>(IRenderedComponent<TComponent> component, string kind) where TComponent : IComponent
    {
        var table = component.Find($"table[data-v2-{kind}-table='active']");
        var row = table.QuerySelector($"tr[data-v2-{kind}-row]")
            ?? throw new InvalidOperationException($"Rendered active {kind} row is missing.");
        var grip = row.QuerySelector($"button[data-v2-{kind}-grip]")
            ?? throw new InvalidOperationException($"Rendered active {kind} grip is missing.");
        var tail = table.QuerySelector($"tr[data-v2-{kind}-tail]")
            ?? throw new InvalidOperationException($"Rendered active {kind} tail is missing.");
        return new
        {
            kind,
            before = $"v2-{kind}-drop-before",
            tail = $"v2-{kind}-drop-tail",
            table = MarkupElement(table),
            row = MarkupElement(row),
            grip = MarkupElement(grip),
            tailTarget = MarkupElement(tail)
        };
    }

    private static object MarkupElement(AngleSharp.Dom.IElement element) => new
    {
        tag = element.TagName.ToLowerInvariant(),
        attributes = element.Attributes.ToDictionary(attribute => attribute.Name, attribute => attribute.Value)
    };

    private static TransitionRowView Transition(Guid id) => new(id, 0, false, DateTime.UtcNow, "alias", "transition", "source", "room", "target", "", "", false, null, null, null, null, null, null, null, null, null, null, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral, V2ReferenceState.Resolved, V2ReferenceState.Resolved, V2ReferenceState.Resolved, V2InverseState.Zero);
    private static ConnectionRowView Connection(Guid id) => new(id, 0, false, DateTime.UtcNow, "alias", "connection", "source", "destination", "", "", false, null, false, null, null, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral, V2ReferenceState.Resolved, V2ReferenceState.Resolved, "one-way", false);

    private static string FindSolutionRoot([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Silksong Rando Logic Manager.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the solution root.");
    }

    private const string Harness = """
        var window = {};
        class Node {}
        class Element extends Node {
            constructor(tag, dataset, attrs, parent) { super(); this.tagName = tag; this.attrs = attrs || {}; this.dataset = dataset || {}; for (const [name, value] of Object.entries(this.attrs)) if (name.startsWith("data-")) this.dataset[name.slice(5).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())] = value; this.draggable = this.attrs.draggable === "true"; this.parentElement = parent || null; this.style = {}; this.listeners = {}; this.classList = { values: [], add: function(...x) { this.values.push(...x); }, remove: function(...x) { this.values = this.values.filter(v => !x.includes(v)); }, toggle: function(x, force) { if (force === undefined ? !this.contains(x) : force) this.add(x); else this.remove(x); }, contains: function(x) { return this.values.includes(x); } }; }
            getAttribute(name) { return this.attrs[name] || null; }
            setAttribute(name, value) { this.attrs[name] = String(value); }
            removeAttribute(name) { delete this.attrs[name]; }
            addEventListener(type, listener) { (this.listeners[type] ||= []).push(listener); }
            focus() { this.focusCount = (this.focusCount || 0) + 1; }
            removeEventListener(type, listener) { this.listeners[type] = (this.listeners[type] || []).filter(x => x !== listener); }
            dispatch(type, event) { event.target ||= this; event.preventDefault ||= (() => { event.prevented = true; }); for (const listener of this.listeners[type] || []) listener(event); }
            matches(selector) { return selector.split(',').some(part => { part = part.trim(); if (/^[a-z]+$/.test(part)) return this.tagName === part; const match = part.match(/^([a-z]+)?\[([^\]]+)\]$/); if (!match) return false; return (!match[1] || this.tagName === match[1]) && this.attrs[match[2]] !== undefined; }); }
            closest(selector) { for (let node = this; node; node = node.parentElement) if (node.matches(selector)) return node; return null; }
            querySelector(selector) { return this.nodes?.[selector] || null; }
        }
        class HTMLElement extends Element {}
        class HTMLInputElement extends HTMLElement {}
        class HTMLTextAreaElement extends HTMLElement {}
        class HTMLSelectElement extends HTMLElement {}
        class SVGElement extends HTMLElement {}
        class ScenePane extends HTMLElement { constructor() { super("div"); this.nodes = {}; } querySelector(selector) { return this.nodes[selector] || null; } }
        class SceneSvg extends SVGElement {
            constructor(pane, initial) { super("svg", { sceneInitialViewbox: initial }, { viewBox: "99 99 1 1" }, pane); this.nodes = {}; this.clientHeight = 100; this.captured = new Set(); this.focusCount = 0; }
            closest(selector) { return selector === ".room-context-scene-pane" ? this.parentElement : super.closest(selector); }
            createSVGPoint() { return { x: 0, y: 0, matrixTransform(matrix) { return { x: this.x, y: this.y }; } }; }
            getScreenCTM() { return { inverse: () => ({}) }; }
            setPointerCapture(id) { this.captured.add(id); }
            hasPointerCapture(id) { return this.captured.has(id); }
            releasePointerCapture(id) { this.captured.delete(id); }
            focus() { this.focusCount++; }
        }
        class MockRoot extends HTMLElement {
            constructor() { super("div", {}, {}, null); this.listeners = {}; }
            addEventListener(type, listener) { (this.listeners[type] ||= []).push(listener); }
            removeEventListener(type, listener) { this.listeners[type] = (this.listeners[type] || []).filter(x => x !== listener); }
            contains(node) { for (; node; node = node.parentElement) if (node === this) return true; return false; }
            dispatch(type, event) { event.target ||= this; event.preventDefault ||= (() => { event.prevented = true; }); event.stopImmediatePropagation ||= (() => { event.stopped = true; }); for (const listener of this.listeners[type] || []) listener(event); }
        }
        var document = { addEventListener: () => {}, getElementById: () => null, createElement: () => ({ getContext: () => null }) };
        var sessionStorage = { getItem: () => null, setItem: () => {}, removeItem: () => {} };
        var requestAnimationFrame = callback => callback(); var clearTimeout = () => {}; var setTimeout = () => 0;
        class DataTransfer { constructor() { this.items = { add: () => {} }; } }
        function assert(value, message) { if (!value) throw new Error(message); }
        function eventOf(target, dataTransfer) { return { target, dataTransfer, preventDefault() { this.prevented = true; }, stopImmediatePropagation() { this.stopped = true; } }; }
        function pointerEvent(target, x, y, values) { return { target, clientX: x, clientY: y, preventDefault() { this.prevented = true; }, ...values }; }
        function viewBox(svg) { return svg.getAttribute("viewBox").split(" ").map(Number); }
        function close(left, right) { return Math.abs(left - right) < .000001; }
        function transferOf() { return { values: {}, setData(type, value) { this.values[type] = value; }, effectAllowed: null }; }
        function mountRenderedTable(root, item) { const table = new Element(item.table.tag, {}, item.table.attributes, root), row = new Element(item.row.tag, {}, item.row.attributes, table), grip = new Element(item.grip.tag, {}, item.grip.attributes, row), tail = new Element(item.tailTarget.tag, {}, item.tailTarget.attributes, table); return { table, row, grip, tail }; }
        function row(root, kind, id, index, table, partition) { const title = kind[0].toUpperCase() + kind.slice(1), prefix = "v2" + title; table ||= new Element("table", { [prefix + "Table"]: partition || "active" }, { ["data-" + prefix.replace(/[A-Z]/g, x => "-" + x.toLowerCase()) + "-table"]: partition || "active" }, root); const node = new Element("tr", { [prefix + "Row"]: id, v2ChildIndex: String(index) }, { ["data-" + prefix.replace(/[A-Z]/g, x => "-" + x.toLowerCase()) + "-row"]: id }, table); const grip = partition === "archived" ? null : new Element("button", { [prefix + "Grip"]: "true" }, { ["data-" + prefix.replace(/[A-Z]/g, x => "-" + x.toLowerCase()) + "-grip"]: "true", draggable: "true" }, node); return { kind, id, table, node, grip, target: node }; }
        """;
}
