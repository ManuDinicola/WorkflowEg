package io.flowd.client;

import java.time.Duration;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Semaphore;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * A long-polling worker with bounded concurrency. It fetches only as many jobs as it has free slots, reports the result of the
 * handler, and turns an exception into a failed job with one retry less. A transport failure waits a moment and polls again.
 */
public final class JobWorker implements AutoCloseable {
    /** What a handler wants done with the job. */
    public sealed interface Result permits Completed, Failed, BpmnError {
    }

    public record Completed(Map<String, Object> variables) implements Result {
    }

    public record Failed(String message, Integer retries, long retryBackoffMs) implements Result {
    }

    public record BpmnError(String code, String message, Map<String, Object> variables) implements Result {
    }

    /** Handles one job. */
    @FunctionalInterface
    public interface Handler {
        Result handle(FlowdClient.Job job) throws Exception;
    }

    private final FlowdClient client;
    private final String type;
    private final String name;
    private final int concurrency;
    private final Duration jobTimeout;
    private final Handler handler;
    private final AtomicBoolean running = new AtomicBoolean();
    private final ExecutorService pool;
    private final Semaphore slots;
    private Thread poller;

    public JobWorker(FlowdClient client, String type, String name, int concurrency, Duration jobTimeout, Handler handler) {
        this.client = client;
        this.type = type;
        this.name = name;
        this.concurrency = concurrency;
        this.jobTimeout = jobTimeout;
        this.handler = handler;
        this.pool = Executors.newFixedThreadPool(concurrency);
        this.slots = new Semaphore(concurrency);
    }

    /** Starts polling on a background thread. */
    public void start() {
        if (!running.compareAndSet(false, true)) {
            return;
        }
        poller = new Thread(this::pollLoop, "flowd-worker-" + name);
        poller.setDaemon(true);
        poller.start();
    }

    @Override
    public void close() {
        running.set(false);
        pool.shutdown();
        if (poller != null) {
            poller.interrupt();
        }
    }

    private void pollLoop() {
        while (running.get()) {
            int free = 0;
            int taken = 0;
            try {
                slots.acquire();
                free = 1 + slots.drainPermits();
                for (var job : client.activateJobs(type, name, jobTimeout.toMillis(), free, 10_000)) {
                    taken++;
                    pool.execute(() -> run(job));
                }
            } catch (InterruptedException ex) {
                Thread.currentThread().interrupt();
                return;
            } catch (RuntimeException ex) {
                pause();
            } finally {
                slots.release(free - taken); // slots of jobs that were not taken (or never fetched) are free again
            }
        }
    }

    private void run(FlowdClient.Job job) {
        try {
            Result result = handler.handle(job);
            report(job, result);
        } catch (Exception ex) {
            report(job, new Failed(String.valueOf(ex.getMessage()), null, 0));
        } finally {
            slots.release();
        }
    }

    private void report(FlowdClient.Job job, Result result) {
        try {
            switch (result) {
                case Completed c -> client.complete(job, c.variables(), name);
                case Failed f -> client.fail(job, f.message(), f.retries(), f.retryBackoffMs(), name);
                case BpmnError e -> client.throwError(job, e.code(), e.message(), e.variables(), name);
            }
        } catch (RuntimeException ex) {
            // the job times out and is activated again: at-least-once delivery
        }
    }

    private static void pause() {
        try {
            Thread.sleep(1000);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
        }
    }
}
