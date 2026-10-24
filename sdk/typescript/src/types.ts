/** JSON object of process variables. */
export type Variables = Record<string, unknown>;

/** A job handed to a worker handler. */
export interface Job {
  key: string;
  type: string;
  processInstanceKey: string;
  elementId: string;
  retries: number;
  deadline: string;
  variables: Variables;
  customHeaders: Record<string, string>;
}

/** The handler finished the job; the variables are merged into the process. */
export interface Completed {
  kind: "completed";
  variables?: Variables;
}

/** The handler failed. Without `retries` the remaining retries of the job minus one are used. */
export interface Failed {
  kind: "failed";
  message: string;
  retries?: number;
  retryBackoffMs?: number;
}

/** The handler reports a BPMN error that a boundary event can catch. */
export interface BpmnError {
  kind: "bpmnError";
  code: string;
  message?: string;
  variables?: Variables;
}

/** What a worker handler returns. */
export type JobResult = Completed | Failed | BpmnError;

export const complete = (variables?: Variables): Completed => ({ kind: "completed", variables });
export const fail = (message: string, options: { retries?: number; retryBackoffMs?: number } = {}): Failed => ({ kind: "failed", message, ...options });
export const bpmnError = (code: string, message?: string, variables?: Variables): BpmnError => ({ kind: "bpmnError", code, message, variables });

export type JobHandler = (job: Job, signal: AbortSignal) => Promise<JobResult> | JobResult;

export interface WorkerOptions {
  /** Name reported to the engine; default "flowd-ts-worker". */
  workerName?: string;
  /** How long a job stays locked to this worker, in ms. Default 30 000. */
  jobTimeoutMs?: number;
  /** Server-side long-poll duration of one activation request, in ms. Default 10 000. */
  pollTimeoutMs?: number;
  /** Jobs handled at the same time. Default 4. */
  maxConcurrentJobs?: number;
  /** Only these variables are sent with the jobs. Default: all. */
  fetchVariables?: string[];
  /** First back-off after a transport error, in ms. Default 200. */
  minBackoffMs?: number;
  /** Largest back-off, in ms. Default 30 000. */
  maxBackoffMs?: number;
}
