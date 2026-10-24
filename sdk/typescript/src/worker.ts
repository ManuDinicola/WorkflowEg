import { FlowdClient, FlowdHttpError } from "./client.js";
import { fail, type Job, type JobHandler, type JobResult, type WorkerOptions } from "./types.js";

/**
 * Long-polling worker: fetches jobs, runs the handler with bounded concurrency, reports the result, retries transport errors with
 * jittered exponential back-off and stops gracefully when the signal aborts. Delivery is at-least-once: handlers must be idempotent.
 */
export class JobWorker {
  private readonly name: string;
  private readonly jobTimeoutMs: number;
  private readonly pollTimeoutMs: number;
  private readonly maxConcurrent: number;
  private readonly minBackoffMs: number;
  private readonly maxBackoffMs: number;
  private handledCount = 0;

  constructor(
    private readonly client: FlowdClient,
    private readonly type: string,
    private readonly handler: JobHandler,
    private readonly options: WorkerOptions = {},
  ) {
    this.name = options.workerName ?? "flowd-ts-worker";
    this.jobTimeoutMs = options.jobTimeoutMs ?? 30_000;
    this.pollTimeoutMs = options.pollTimeoutMs ?? 10_000;
    this.maxConcurrent = options.maxConcurrentJobs ?? 4;
    this.minBackoffMs = options.minBackoffMs ?? 200;
    this.maxBackoffMs = options.maxBackoffMs ?? 30_000;
  }

  /** Number of jobs whose result was reported. */
  get handled(): number {
    return this.handledCount;
  }

  /** Runs until `signal` aborts, then waits for the handlers that are still running. */
  async run(signal: AbortSignal): Promise<void> {
    const running = new Set<Promise<void>>();
    let failures = 0;
    while (!signal.aborted) {
      try {
        while (running.size >= this.maxConcurrent) {
          await Promise.race(running);
        }

        const jobs = await this.client.activateJobs(
          this.type,
          { worker: this.name, timeoutMs: this.jobTimeoutMs, maxJobs: this.maxConcurrent - running.size, requestTimeoutMs: this.pollTimeoutMs, fetchVariables: this.options.fetchVariables },
          signal,
        );
        failures = 0;
        for (const job of jobs) {
          const task: Promise<void> = this.runJob(job, signal).finally(() => running.delete(task));
          running.add(task);
        }
      } catch (error) {
        if (signal.aborted) {
          break;
        }

        failures++;
        if (error instanceof FlowdHttpError && error.status >= 400 && error.status < 500 && error.status !== 429) {
          throw error; // a bad key or request will not fix itself by retrying
        }

        await this.sleep(this.backoff(failures), signal);
      }
    }

    await Promise.allSettled(running);
  }

  private async runJob(job: Job, signal: AbortSignal): Promise<void> {
    let result: JobResult;
    try {
      result = await this.handler(job, signal);
    } catch (error) {
      if (signal.aborted) {
        return; // shutting down mid-job: the deadline hands it to another worker
      }

      result = fail(error instanceof Error ? error.message : String(error));
    }

    for (let attempt = 1; ; attempt++) {
      try {
        await this.client.report(job, result, this.name);
        this.handledCount++;
        return;
      } catch (error) {
        if (error instanceof FlowdHttpError && error.status < 500 && error.status !== 429) {
          return; // the engine refused it for good
        }

        if (attempt >= 5) {
          return; // give up: the job deadline hands it to another delivery (at-least-once)
        }

        await this.sleep(this.backoff(attempt), new AbortController().signal);
      }
    }
  }

  private backoff(failures: number): number {
    const exponential = this.minBackoffMs * 2 ** Math.min(failures - 1, 10);
    return Math.min(exponential, this.maxBackoffMs) * (0.5 + Math.random() / 2); // jitter between 50 % and 100 %
  }

  private sleep(ms: number, signal: AbortSignal): Promise<void> {
    return new Promise((resolve) => {
      const timer = setTimeout(done, ms);
      signal.addEventListener("abort", done, { once: true });
      function done(): void {
        clearTimeout(timer);
        resolve();
      }
    });
  }
}

/** Creates a worker for one job type. */
export function createWorker(client: FlowdClient, type: string, handler: JobHandler, options?: WorkerOptions): JobWorker {
  return new JobWorker(client, type, handler, options);
}
