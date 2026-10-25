# Flowd worker SDK for Go

Standard library only (Go 1.22 or newer). A small client of the REST API and a long-polling worker with bounded concurrency.

```go
client := flowd.NewClient("http://localhost:8080", os.Getenv("FLOWD_API_KEY"))

worker := flowd.NewWorker(client, flowd.WorkerConfig{Type: "send-invoice", Name: "invoice-worker", Concurrency: 8, JobTimeout: 30 * time.Second},
	func(ctx context.Context, job flowd.Job) (flowd.Result, error) {
		amount, _ := job.Variables["amount"].(float64)
		return flowd.Completed{Variables: map[string]any{"invoiceSent": amount > 0}}, nil
		// or: flowd.Failed{Message: "mail server down", Backoff: 5 * time.Second}   (retries default to one less)
		// or: flowd.BpmnError{Code: "NO_STOCK", Message: "out of stock"}
	})

ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
defer stop()
_ = worker.Run(ctx) // returns when ctx is cancelled, after the running jobs have reported
```

An error returned by the handler, or a panic, fails the job with one retry less. A transport failure waits a second and polls again. A 409 when reporting (job already completed or taken over) is ignored, because delivery is at least once: make handlers idempotent. `client.StartInstance(ctx, "process-id", vars)` starts an instance.

## Tests
```
go vet ./... && go test ./...
```
