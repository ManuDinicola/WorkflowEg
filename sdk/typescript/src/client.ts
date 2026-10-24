import type { Job, JobResult, Variables } from "./types.js";

export interface ClientOptions {
  /** Sent as `X-Api-Key` with every request. */
  apiKey?: string;
  /** Replace `fetch`, for tests or proxies. */
  fetch?: typeof fetch;
}

/** The server answered with an error status. */
export class FlowdHttpError extends Error {
  constructor(readonly status: number, readonly body: string) {
    super(`Flowd answered ${status}: ${body}`);
    this.name = "FlowdHttpError";
  }
}

/** Thin typed client for the worker part of the Flowd API. */
export class FlowdClient {
  private readonly baseUrl: string;
  private readonly apiKey: string | undefined;
  private readonly doFetch: typeof fetch;

  constructor(baseUrl: string, options: ClientOptions = {}) {
    this.baseUrl = baseUrl.replace(/\/+$/, "");
    this.apiKey = options.apiKey;
    this.doFetch = options.fetch ?? fetch;
  }

  /** Long-polls for up to `maxJobs` jobs of one type. */
  async activateJobs(
    type: string,
    request: { worker: string; timeoutMs: number; maxJobs: number; requestTimeoutMs: number; fetchVariables?: string[] },
    signal?: AbortSignal,
  ): Promise<Job[]> {
    const body = await this.post("/v1/jobs/activate", { type, ...request }, signal);
    return (body as { jobs: Job[] }).jobs;
  }

  /** Reports the result of a job. A 409 (already completed or taken over) is not an error: delivery is at-least-once. */
  async report(job: Job, result: JobResult, worker: string, signal?: AbortSignal): Promise<void> {
    const path = `/v1/jobs/${encodeURIComponent(job.key)}`;
    switch (result.kind) {
      case "completed":
        await this.post(`${path}/complete`, { variables: result.variables, worker }, signal, [409]);
        return;
      case "failed":
        await this.post(
          `${path}/fail`,
          { retries: result.retries ?? Math.max(job.retries - 1, 0), errorMessage: result.message, retryBackoffMs: result.retryBackoffMs, worker },
          signal,
          [409],
        );
        return;
      case "bpmnError":
        await this.post(`${path}/error`, { errorCode: result.code, errorMessage: result.message, variables: result.variables, worker }, signal, [409]);
        return;
    }
  }

  /** Starts a process instance and returns its key. */
  async startInstance(bpmnProcessId: string, variables?: Variables): Promise<string> {
    const body = await this.post("/v1/process-instances", { bpmnProcessId, variables });
    return (body as { processInstanceKey: string }).processInstanceKey;
  }

  private async post(path: string, payload: unknown, signal?: AbortSignal, tolerate: number[] = []): Promise<unknown> {
    const headers: Record<string, string> = { "Content-Type": "application/json" };
    if (this.apiKey) {
      headers["X-Api-Key"] = this.apiKey;
    }

    const response = await this.doFetch(`${this.baseUrl}${path}`, { method: "POST", headers, body: JSON.stringify(payload), signal });
    if (tolerate.includes(response.status)) {
      return undefined;
    }

    const text = await response.text();
    if (!response.ok) {
      throw new FlowdHttpError(response.status, text);
    }

    return text.length > 0 ? JSON.parse(text) : undefined;
  }
}
