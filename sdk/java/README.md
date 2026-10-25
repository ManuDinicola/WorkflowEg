# Flowd worker SDK for Java

Zero dependencies: the JDK only (Java 17 or newer; developed with 21). It long-polls Flowd for jobs, runs your handler with bounded concurrency and reports the result.

```java
var client = new FlowdClient("http://localhost:8080", System.getenv("FLOWD_API_KEY"));
try (var worker = new JobWorker(client, "send-invoice", "invoice-worker", 8, Duration.ofSeconds(30), job -> {
    var amount = ((Number) job.variables().get("amount")).doubleValue();
    return new JobWorker.Completed(Map.of("invoiceSent", amount > 0));
    // or: new JobWorker.Failed("mail server down", null, 5000)   (retries default to one less)
    // or: new JobWorker.BpmnError("NO_STOCK", "out of stock", Map.of())
})) {
    worker.start();
    Thread.currentThread().join();
}
```

An exception in the handler fails the job with one retry less. A transport failure waits a second and polls again. A 409 when reporting (job already completed or taken over) is ignored, because delivery is at least once: make handlers idempotent.

`FlowdClient` also has `startInstance(processId, variables)`. `Json` is the small JSON reader and writer the SDK uses; variables are plain `Map`, `List`, `String`, `Long`, `Double`, `Boolean`, `null`.

## Tests
```
mkdir out && javac -encoding UTF-8 -d out $(find src -name '*.java') && java -cp out io.flowd.client.FlowdClientTest
```
