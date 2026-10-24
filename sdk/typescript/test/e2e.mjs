// Manual end-to-end check against a running Flowd (not part of `npm test`):
//   FLOWD_URL=http://127.0.0.1:5099 node test/e2e.mjs   (after `npm run build`)
import { FlowdClient, createWorker, complete } from "../dist/src/index.js";

const url = process.env.FLOWD_URL ?? "http://127.0.0.1:8080";
const xml = `<?xml version="1.0" encoding="UTF-8"?>
<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowd="http://flowd.io/schema/1.0">
  <bpmn:process id="ts-e2e" isExecutable="true">
    <bpmn:startEvent id="S"/>
    <bpmn:serviceTask id="A"><bpmn:extensionElements><flowd:taskDefinition type="ts-work"/></bpmn:extensionElements></bpmn:serviceTask>
    <bpmn:endEvent id="E"/>
    <bpmn:sequenceFlow id="f1" sourceRef="S" targetRef="A"/><bpmn:sequenceFlow id="f2" sourceRef="A" targetRef="E"/>
  </bpmn:process>
</bpmn:definitions>`;

const form = new FormData();
form.append("resources", new Blob([xml]), "ts-e2e.bpmn");
const deployed = await fetch(`${url}/v1/deployments`, { method: "POST", body: form });
if (!deployed.ok) throw new Error(`deploy failed: ${deployed.status}`);

const client = new FlowdClient(url);
const key = await client.startInstance("ts-e2e", { amount: 21 });
const abort = new AbortController();
const worker = createWorker(client, "ts-work", (job) => complete({ doubled: Number(job.variables.amount) * 2 }), { pollTimeoutMs: 500 });
const run = worker.run(abort.signal);

const deadline = Date.now() + 15000;
let state = "";
while (Date.now() < deadline) {
  state = (await (await fetch(`${url}/v1/process-instances/${key}`)).json()).state;
  if (state === "COMPLETED") break;
  await new Promise((r) => setTimeout(r, 100));
}
abort.abort();
await run;

const variables = await (await fetch(`${url}/v1/process-instances/${key}/variables`)).json();
console.log(JSON.stringify({ state, handled: worker.handled, doubled: variables.doubled }));
if (state !== "COMPLETED" || variables.doubled !== 42) process.exit(1);
