import assert from "node:assert/strict";
import { createServer, type IncomingMessage, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import { test } from "node:test";
import { FlowdClient, FlowdHttpError, bpmnError, complete, createWorker, fail, type Job } from "../src/index.js";

interface Call {
  path: string;
  body: Record<string, unknown>;
  apiKey: string | undefined;
}

const job = (key: string, retries = 3): Job => ({
  key,
  type: "work",
  processInstanceKey: "1",
  elementId: "A",
  retries,
  deadline: new Date().toISOString(),
  variables: { amount: 5 },
  customHeaders: {},
});

async function read(request: IncomingMessage): Promise<Record<string, unknown>> {
  const chunks: Buffer[] = [];
  for await (const chunk of request) {
    chunks.push(chunk as Buffer);
  }
  const text = Buffer.concat(chunks).toString("utf8");
  return text ? (JSON.parse(text) as Record<string, unknown>) : {};
}

/** A fake Flowd: `activate` decides what each poll answers; everything else is recorded. */
async function fakeServer(activate: (poll: number) => { status: number; jobs?: Job[] }, reportStatus = 204): Promise<{ server: Server; url: string; calls: Call[] }> {
  const calls: Call[] = [];
  let polls = 0;
  const server = createServer((request, response) => {
    void read(request).then((body) => {
      calls.push({ path: request.url ?? "", body, apiKey: request.headers["x-api-key"] as string | undefined });
      if (request.url === "/v1/jobs/activate") {
        const answer = activate(polls++);
        response.writeHead(answer.status, { "Content-Type": "application/json" });
        response.end(answer.status === 200 ? JSON.stringify({ jobs: answer.jobs ?? [] }) : "{}");
        return;
      }
      response.writeHead(reportStatus);
      response.end();
    });
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  return { server, url: `http://127.0.0.1:${(server.address() as AddressInfo).port}`, calls };
}

async function until(condition: () => boolean, timeoutMs = 5000): Promise<void> {
  const start = Date.now();
  while (!condition()) {
    assert.ok(Date.now() - start < timeoutMs, "timed out waiting for the condition");
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}

const fast = { pollTimeoutMs: 20, minBackoffMs: 1, maxBackoffMs: 5 };

test("completes jobs with variables, sends the api key and stops gracefully", async () => {
  const { server, url, calls } = await fakeServer((poll) => ({ status: 200, jobs: poll === 0 ? [job("1"), job("2")] : [] }));
  const abort = new AbortController();
  const worker = createWorker(new FlowdClient(url, { apiKey: "secret" }), "work", (j) => complete({ doubled: Number(j.variables["amount"]) * 2 }), fast);
  const run = worker.run(abort.signal);

  await until(() => worker.handled === 2);
  abort.abort();
  await run;
  server.close();

  const completes = calls.filter((c) => c.path.endsWith("/complete"));
  assert.equal(completes.length, 2);
  assert.deepEqual(completes[0]?.body["variables"], { doubled: 10 });
  assert.ok(calls.every((c) => c.apiKey === "secret"));
});

test("a throwing handler becomes a failed job with one retry fewer", async () => {
  const { server, url, calls } = await fakeServer((poll) => ({ status: 200, jobs: poll === 0 ? [job("7", 3)] : [] }));
  const abort = new AbortController();
  const worker = createWorker(new FlowdClient(url), "work", () => { throw new Error("boom"); }, fast);
  const run = worker.run(abort.signal);

  await until(() => worker.handled === 1);
  abort.abort();
  await run;
  server.close();

  const failed = calls.find((c) => c.path.endsWith("/fail"));
  assert.equal(failed?.body["retries"], 2);
  assert.equal(failed?.body["errorMessage"], "boom");
});

test("fail with explicit retries and a BPMN error are reported", async () => {
  const { server, url, calls } = await fakeServer((poll) => ({ status: 200, jobs: poll === 0 ? [job("1"), job("2")] : [] }));
  const abort = new AbortController();
  const worker = createWorker(new FlowdClient(url), "work", (j) => (j.key === "1" ? fail("later", { retries: 0, retryBackoffMs: 500 }) : bpmnError("LIMIT", "too much", { why: 1 })), fast);
  const run = worker.run(abort.signal);

  await until(() => worker.handled === 2);
  abort.abort();
  await run;
  server.close();

  assert.deepEqual(calls.find((c) => c.path === "/v1/jobs/1/fail")?.body["retries"], 0);
  assert.equal(calls.find((c) => c.path === "/v1/jobs/1/fail")?.body["retryBackoffMs"], 500);
  const error = calls.find((c) => c.path === "/v1/jobs/2/error");
  assert.equal(error?.body["errorCode"], "LIMIT");
  assert.deepEqual(error?.body["variables"], { why: 1 });
});

test("server errors and dropped connections are retried with back-off, more often than there are slots", async () => {
  const { server, url } = await fakeServer((poll) => (poll < 10 ? { status: 503 } : { status: 200, jobs: poll === 10 ? [job("1")] : [] }));
  const abort = new AbortController();
  const worker = createWorker(new FlowdClient(url), "work", () => complete(), { ...fast, maxConcurrentJobs: 2 });
  const run = worker.run(abort.signal);

  await until(() => worker.handled === 1);
  abort.abort();
  await run;
  server.close();
});

test("a rejected api key ends the worker instead of retrying forever", async () => {
  const { server, url } = await fakeServer(() => ({ status: 401 }));
  const worker = createWorker(new FlowdClient(url), "work", () => complete(), fast);

  await assert.rejects(worker.run(new AbortController().signal), (error: unknown) => error instanceof FlowdHttpError && error.status === 401);
  server.close();
});

test("a 409 when reporting (job already taken over) is tolerated", async () => {
  const { server, url } = await fakeServer((poll) => ({ status: 200, jobs: poll === 0 ? [job("1")] : [] }), 409);
  const abort = new AbortController();
  const worker = createWorker(new FlowdClient(url), "work", () => complete(), fast);
  const run = worker.run(abort.signal);

  await until(() => worker.handled === 1);
  abort.abort();
  await run;
  server.close();
});

test("start instance returns the key", async () => {
  const server = createServer((request, response) => {
    void read(request).then(() => {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify({ processInstanceKey: "42" }));
    });
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));

  const key = await new FlowdClient(`http://127.0.0.1:${(server.address() as AddressInfo).port}/`).startInstance("order", { a: 1 });

  assert.equal(key, "42");
  server.close();
});
