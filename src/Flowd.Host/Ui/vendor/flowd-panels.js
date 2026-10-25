/* Properties panel sections of the Flowd modeler (FR-UI-009): timers, messages, signals, errors, escalations, multi-instance,
   input and output mappings, listeners. Each section reads the BPMN model through bpmn-js and writes it back through the command stack,
   so every edit can be undone. Loaded after bpmn-js; used by the Modeler tab of the UI. */
"use strict";
(function () {
  function sections(ctx, element) {
    const { el, field, factory, modeling, canvas } = ctx;
    const bo = element.businessObject;
    const out = [];
    const definition = (type) => (bo.eventDefinitions || []).find((d) => d.$instanceOf(type));
    const rootOwner = () => canvas().getRootElement();
    const definitions = () => rootOwner().businessObject.$parent;
    const update = (target, properties) => modeling().updateModdleProperties(element, target, properties);

    /* A message, signal, error or escalation is a root element of the file that events point to; create it on first use. */
    const rootFor = (owner, refName, type, props) => {
      let root = owner[refName];
      if (!root) {
        root = factory().create(type, { id: ctx.newId(type.split(":")[1]), ...props });
        modeling().updateModdleProperties(rootOwner(), definitions(), { rootElements: [...definitions().rootElements, root] });
        update(owner, { [refName]: root });
      }
      return root;
    };

    const section = (title, ...kids) => { if (kids.length) out.push(el("h4", {}, title), ...kids); };
    const select = (label, value, options, apply) => {
      const s = el("select", {}, ...options.map(([v, l]) => el("option", { value: v, ...(v === value ? { selected: "selected" } : {}) }, l)));
      s.addEventListener("change", () => apply(s.value));
      return el("div", {}, el("label", {}, label), s);
    };
    const check = (label, value, apply) => {
      const c = el("input", { type: "checkbox", ...(value ? { checked: "checked" } : {}) });
      c.addEventListener("change", () => apply(c.checked));
      return el("label", { class: "inline" }, c, " " + label);
    };

    /* timers */
    const timer = definition("bpmn:TimerEventDefinition");
    if (timer) {
      const kind = timer.timeCycle ? "timeCycle" : timer.timeDate ? "timeDate" : "timeDuration";
      const value = timer[kind]?.body ?? "";
      const apply = (k, v) => update(timer, { timeDate: undefined, timeDuration: undefined, timeCycle: undefined, ...(v ? { [k]: factory().create("bpmn:FormalExpression", { body: v }) } : {}) });
      const input = field("Value (ISO 8601 or a FEEL expression starting with =)", value, (v) => apply(kindBox.value, v));
      const kindBox = el("select", {}, ...[["timeDuration", "Duration (PT15M)"], ["timeDate", "Date (2026-12-31T10:00:00Z)"], ["timeCycle", "Cycle (R5/PT10M)"]].map(([v, l]) => el("option", { value: v, ...(v === kind ? { selected: "selected" } : {}) }, l)));
      kindBox.addEventListener("change", () => apply(kindBox.value, input.querySelector("input").value.trim()));
      section("Timer", el("div", {}, el("label", {}, "Kind"), kindBox), input);
    }

    /* messages (message events and receive tasks) */
    const messageOwner = definition("bpmn:MessageEventDefinition") || (bo.$instanceOf("bpmn:ReceiveTask") ? bo : null);
    if (messageOwner) {
      const message = messageOwner.messageRef;
      const key = (message?.extensionElements?.values || []).find((v) => v.$type === "flowd:Subscription")?.correlationKey ?? "";
      const message$ = () => rootFor(messageOwner, "messageRef", "bpmn:Message", { name: "message" });
      section("Message",
        field("Message name", message?.name, (v) => update(message$(), { name: v })),
        field("Correlation key (FEEL, starts with =)", key, (v) => {
          const target = message$();
          update(target, { extensionElements: v ? factory().create("bpmn:ExtensionElements", { values: [factory().create("flowd:Subscription", { correlationKey: v })] }) : undefined });
        }));
    }

    /* signals */
    const signal = definition("bpmn:SignalEventDefinition");
    if (signal) {
      section("Signal", field("Signal name", signal.signalRef?.name, (v) => update(rootFor(signal, "signalRef", "bpmn:Signal", { name: v }), { name: v })));
    }

    /* errors and escalations */
    const error = definition("bpmn:ErrorEventDefinition");
    if (error) {
      section("Error", field("Error code (empty on a boundary event catches every error)", error.errorRef?.errorCode, (v) => update(rootFor(error, "errorRef", "bpmn:Error", { name: v || "error", errorCode: v }), { errorCode: v || undefined })));
    }
    const escalation = definition("bpmn:EscalationEventDefinition");
    if (escalation) {
      section("Escalation", field("Escalation code", escalation.escalationRef?.escalationCode, (v) => update(rootFor(escalation, "escalationRef", "bpmn:Escalation", { name: v || "escalation", escalationCode: v }), { escalationCode: v || undefined })));
    }

    /* conditional events */
    const conditional = definition("bpmn:ConditionalEventDefinition");
    if (conditional) {
      section("Condition", field("Condition (FEEL, starts with =)", conditional.condition?.body, (v) => update(conditional, { condition: v ? factory().create("bpmn:FormalExpression", { body: v }) : undefined })));
    }

    /* multi-instance */
    if (bo.$instanceOf("bpmn:Activity") && !bo.triggeredByEvent) {
      const loop = bo.loopCharacteristics;
      const settings = (loop?.extensionElements?.values || []).find((v) => v.$type === "flowd:LoopCharacteristics");
      const setSettings = (attrs) => update(loop, { extensionElements: factory().create("bpmn:ExtensionElements", { values: [factory().create("flowd:LoopCharacteristics", { ...(settings ? { inputCollection: settings.inputCollection, inputElement: settings.inputElement, outputCollection: settings.outputCollection, outputElement: settings.outputElement } : {}), ...attrs })] }) });
      const kids = [check("Run once for every item of a list (multi-instance)", !!loop, (on) => {
        modeling().updateProperties(element, { loopCharacteristics: on ? factory().create("bpmn:MultiInstanceLoopCharacteristics", { isSequential: false, extensionElements: factory().create("bpmn:ExtensionElements", { values: [factory().create("flowd:LoopCharacteristics", { inputCollection: "= items", inputElement: "item" })] }) }) : undefined });
      })];
      if (loop) {
        kids.push(check("One after the other (sequential)", loop.isSequential, (v) => update(loop, { isSequential: v })),
          field("Input collection (FEEL)", settings?.inputCollection, (v) => setSettings({ inputCollection: v })),
          field("Input element (variable name)", settings?.inputElement, (v) => setSettings({ inputElement: v })),
          field("Output collection (variable name)", settings?.outputCollection, (v) => setSettings({ outputCollection: v || undefined })),
          field("Output element (FEEL)", settings?.outputElement, (v) => setSettings({ outputElement: v || undefined })),
          field("Completion condition (FEEL)", loop.completionCondition?.body, (v) => update(loop, { completionCondition: v ? factory().create("bpmn:FormalExpression", { body: v }) : undefined })));
      }
      section("Multi-instance", ...kids);
    }

    /* input and output mappings, and listeners: rows with two fields each */
    const extension = (type) => (bo.extensionElements?.values || []).find((v) => v.$type === type);
    const writeExtension = (type, node) => {
      const kept = (bo.extensionElements?.values || []).filter((v) => v.$type !== type);
      const values = node ? [...kept, node] : kept;
      modeling().updateProperties(element, { extensionElements: values.length ? factory().create("bpmn:ExtensionElements", { values }) : undefined });
    };
    const rows = (title, items, firstLabel, secondLabel, apply) => {
      const list = el("div", {});
      const current = items.map((i) => ({ ...i }));
      const draw = () => {
        list.replaceChildren(...current.map((row, index) => {
          const a = el("input", { value: row.a ?? "", placeholder: firstLabel, size: "10" }), b = el("input", { value: row.b ?? "", placeholder: secondLabel, size: "10" });
          a.addEventListener("change", () => { row.a = a.value.trim(); apply(current); });
          b.addEventListener("change", () => { row.b = b.value.trim(); apply(current); });
          return el("div", { class: "row" }, a, b, el("button", { onclick: () => { current.splice(index, 1); apply(current); draw(); } }, "✕"));
        }));
      };
      draw();
      return el("div", {}, el("label", {}, title), list, el("button", { onclick: () => { current.push({ a: "", b: "" }); draw(); } }, "Add"));
    };
    const mapping = extension("flowd:IoMapping");
    const writeMapping = (inputs, outputs) => {
      const usable = (rowsList) => rowsList.filter((r) => r.a && r.b);
      const node = usable(inputs).length + usable(outputs).length === 0 && mapping?.propagateAll === undefined
        ? undefined
        : factory().create("flowd:IoMapping", { ...(mapping?.propagateAll === undefined ? {} : { propagateAll: mapping.propagateAll }), inputs: usable(inputs).map((r) => factory().create("flowd:Input", { source: r.a, target: r.b })), outputs: usable(outputs).map((r) => factory().create("flowd:Output", { source: r.a, target: r.b })) });
      writeExtension("flowd:IoMapping", node);
    };
    const inputs = (mapping?.inputs || []).map((i) => ({ a: i.source, b: i.target }));
    const outputs = (mapping?.outputs || []).map((o) => ({ a: o.source, b: o.target }));
    if (bo.$instanceOf("bpmn:Activity") || bo.$instanceOf("bpmn:IntermediateCatchEvent") || bo.$instanceOf("bpmn:IntermediateThrowEvent")) {
      let live = { inputs, outputs };
      section("Variable mappings",
        rows("Inputs: FEEL source → variable", inputs, "= source", "target", (r) => { live = { ...live, inputs: r }; writeMapping(live.inputs, live.outputs); }),
        rows("Outputs: FEEL source → variable", outputs, "= source", "target", (r) => { live = { ...live, outputs: r }; writeMapping(live.inputs, live.outputs); }));
    }

    if (bo.$instanceOf("bpmn:FlowNode") && !bo.$instanceOf("bpmn:BoundaryEvent")) {
      const isUserTask = bo.$instanceOf("bpmn:UserTask");
      const type = isUserTask ? "flowd:TaskListeners" : "flowd:ExecutionListeners";
      const events = isUserTask ? "created, assigned, completed, start, end" : "start, end";
      const container = extension(type);
      section("Listeners (jobs that run on " + events + ")",
        rows("Event → job type", (container?.listeners || []).map((l) => ({ a: l.event, b: l.type })), "event", "job type", (r) => {
          const usable = r.filter((x) => x.a && x.b);
          writeExtension(type, usable.length ? factory().create(type, { listeners: usable.map((x) => factory().create("flowd:Listener", { event: x.a, type: x.b })) }) : undefined);
        }));
    }

    return out;
  }

  window.FlowdPanels = { sections };
})();
