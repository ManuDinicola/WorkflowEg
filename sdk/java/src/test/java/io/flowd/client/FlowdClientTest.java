package io.flowd.client;

import com.sun.net.httpserver.HttpServer;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/** Plain-Java tests (no framework): run with {@code javac} then {@code java io.flowd.client.FlowdClientTest}. Exits 1 on failure. */
public final class FlowdClientTest {
    private static final List<String> requests = new CopyOnWriteArrayList<>();
    private static int failures;

    public static void main(String[] args) throws Exception {
        jsonRoundTrip();
        jsonRejectsMalformedText();
        workerCompletesAndFailsJobs();
        if (failures > 0) {
            System.exit(1);
        }
        System.out.println("all tests passed");
    }

    private static void check(boolean condition, String what) {
        if (!condition) {
            failures++;
            System.out.println("FAILED: " + what);
        }
    }

    private static void jsonRoundTrip() {
        Object parsed = Json.parse("{\"a\":[1,2.5,\"x\\n\",true,null],\"b\":{\"c\":\"\\u00e9\"}}");
        check(Json.write(parsed).equals("{\"a\":[1,2.5,\"x\\n\",true,null],\"b\":{\"c\":\"\u00e9\"}}"), "json round trip");
    }

    private static void jsonRejectsMalformedText() {
        try {
            Json.parse("{\"a\":");
            check(false, "malformed json is rejected");
        } catch (IllegalArgumentException expected) {
            check(true, "malformed json is rejected");
        }
    }

    private static void workerCompletesAndFailsJobs() throws Exception {
        HttpServer server = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        CountDownLatch reported = new CountDownLatch(2);
        boolean[] served = {false};
        server.createContext("/", exchange -> {
            String body = new String(exchange.getRequestBody().readAllBytes(), StandardCharsets.UTF_8);
            requests.add(exchange.getRequestURI().getPath() + " " + body + " key=" + exchange.getRequestHeaders().getFirst("X-Api-Key"));
            String reply = "";
            if (exchange.getRequestURI().getPath().equals("/v1/jobs/activate")) {
                synchronized (served) {
                    reply = served[0] ? "{\"jobs\":[]}"
                            : "{\"jobs\":[{\"key\":\"1\",\"type\":\"t\",\"processInstanceKey\":\"9\",\"elementId\":\"E\",\"retries\":3,\"variables\":{\"n\":2},\"customHeaders\":{}},"
                            + "{\"key\":\"2\",\"type\":\"t\",\"processInstanceKey\":\"9\",\"elementId\":\"E\",\"retries\":3,\"variables\":{\"n\":0},\"customHeaders\":{}}]}";
                    served[0] = true;
                }
            } else {
                reported.countDown();
            }
            byte[] bytes = reply.getBytes(StandardCharsets.UTF_8);
            exchange.sendResponseHeaders(reply.isEmpty() ? 204 : 200, reply.isEmpty() ? -1 : bytes.length);
            if (!reply.isEmpty()) {
                exchange.getResponseBody().write(bytes);
            }
            exchange.close();
        });
        server.start();
        try (JobWorker worker = new JobWorker(
                new FlowdClient("http://127.0.0.1:" + server.getAddress().getPort(), "secret", Duration.ofSeconds(5)),
                "t", "w", 2, Duration.ofSeconds(30),
                job -> {
                    if (((Number) job.variables().get("n")).intValue() == 0) {
                        throw new IllegalStateException("boom");
                    }
                    return new JobWorker.Completed(Map.of("doubled", 4));
                })) {
            worker.start();
            check(reported.await(10, TimeUnit.SECONDS), "both jobs are reported");
        } finally {
            server.stop(0);
        }
        check(requests.stream().anyMatch(r -> r.startsWith("/v1/jobs/1/complete") && r.contains("\"doubled\":4") && r.endsWith("key=secret")), "job 1 completed with variables and api key");
        check(requests.stream().anyMatch(r -> r.startsWith("/v1/jobs/2/fail") && r.contains("\"retries\":2") && r.contains("boom")), "job 2 failed with one retry less");
    }
}
