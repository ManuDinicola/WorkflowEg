package io.flowd.client;

import java.io.IOException;
import java.net.URI;
import java.net.URLEncoder;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Thin client for the worker part of the Flowd REST API (JDK only, no dependencies). */
public final class FlowdClient {
    /** An activated job. Variables are the parsed JSON object. */
    public record Job(String key, String type, String processInstanceKey, String elementId, int retries,
                      Map<String, Object> variables, Map<String, String> customHeaders) {
    }

    /** The server answered with an error status. */
    public static final class FlowdHttpException extends RuntimeException {
        private static final long serialVersionUID = 1L;

        private final int status;

        public FlowdHttpException(int status, String body) {
            super("Flowd answered " + status + ": " + body);
            this.status = status;
        }

        public int status() {
            return status;
        }
    }

    private final String baseUrl;
    private final String apiKey;
    private final HttpClient http = HttpClient.newHttpClient();
    private final Duration requestTimeout;

    public FlowdClient(String baseUrl, String apiKey, Duration requestTimeout) {
        this.baseUrl = baseUrl.endsWith("/") ? baseUrl.substring(0, baseUrl.length() - 1) : baseUrl;
        this.apiKey = apiKey;
        this.requestTimeout = requestTimeout;
    }

    public FlowdClient(String baseUrl, String apiKey) {
        this(baseUrl, apiKey, Duration.ofSeconds(70));
    }

    /** Long-polls for up to {@code maxJobs} jobs of one type. */
    @SuppressWarnings("unchecked")
    public List<Job> activateJobs(String type, String worker, long timeoutMs, int maxJobs, long requestTimeoutMs) {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("type", type);
        body.put("worker", worker);
        body.put("timeoutMs", timeoutMs);
        body.put("maxJobs", maxJobs);
        body.put("requestTimeoutMs", requestTimeoutMs);
        Map<String, Object> reply = (Map<String, Object>) post("/v1/jobs/activate", body, false);
        List<Job> jobs = new ArrayList<>();
        for (Object item : (List<Object>) reply.getOrDefault("jobs", List.of())) {
            Map<String, Object> j = (Map<String, Object>) item;
            Map<String, String> headers = new LinkedHashMap<>();
            Map<String, Object> raw = (Map<String, Object>) j.getOrDefault("customHeaders", Map.of());
            raw.forEach((k, v) -> headers.put(k, String.valueOf(v)));
            Object vars = j.get("variables");
            jobs.add(new Job(
                    String.valueOf(j.get("key")),
                    String.valueOf(j.get("type")),
                    String.valueOf(j.get("processInstanceKey")),
                    String.valueOf(j.get("elementId")),
                    ((Number) j.getOrDefault("retries", 0L)).intValue(),
                    vars instanceof Map<?, ?> m ? (Map<String, Object>) m : Map.of(),
                    headers));
        }
        return jobs;
    }

    /** Completes a job. A 409 (already completed or taken over) is not an error: delivery is at-least-once. */
    public void complete(Job job, Map<String, Object> variables, String worker) {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("variables", variables);
        body.put("worker", worker);
        post("/v1/jobs/" + encode(job.key()) + "/complete", body, true);
    }

    /** Fails a job; the retries left default to one less than now. */
    public void fail(Job job, String message, Integer retries, long retryBackoffMs, String worker) {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("retries", retries != null ? retries : Math.max(job.retries() - 1, 0));
        body.put("errorMessage", message);
        body.put("retryBackoffMs", retryBackoffMs);
        body.put("worker", worker);
        post("/v1/jobs/" + encode(job.key()) + "/fail", body, true);
    }

    /** Throws a BPMN error from a job. */
    public void throwError(Job job, String code, String message, Map<String, Object> variables, String worker) {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("errorCode", code);
        body.put("errorMessage", message);
        body.put("variables", variables);
        body.put("worker", worker);
        post("/v1/jobs/" + encode(job.key()) + "/error", body, true);
    }

    /** Starts a process instance and returns its key. */
    @SuppressWarnings("unchecked")
    public String startInstance(String bpmnProcessId, Map<String, Object> variables) {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("bpmnProcessId", bpmnProcessId);
        body.put("variables", variables);
        Map<String, Object> reply = (Map<String, Object>) post("/v1/process-instances", body, false);
        return String.valueOf(reply.get("processInstanceKey"));
    }

    private static String encode(String value) {
        return URLEncoder.encode(value, StandardCharsets.UTF_8);
    }

    private Object post(String path, Map<String, Object> payload, boolean tolerateConflict) {
        HttpRequest.Builder request = HttpRequest.newBuilder(URI.create(baseUrl + path))
                .timeout(requestTimeout)
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(Json.write(payload), StandardCharsets.UTF_8));
        if (apiKey != null && !apiKey.isEmpty()) {
            request.header("X-Api-Key", apiKey);
        }
        try {
            HttpResponse<String> response = http.send(request.build(), HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8));
            int status = response.statusCode();
            if (status == 409 && tolerateConflict) {
                return null;
            }
            if (status >= 300) {
                throw new FlowdHttpException(status, response.body());
            }
            String text = response.body();
            return text == null || text.isBlank() ? null : Json.parse(text);
        } catch (IOException ex) {
            throw new IllegalStateException("The Flowd request failed: " + ex.getMessage(), ex);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
            throw new IllegalStateException("Interrupted", ex);
        }
    }
}
