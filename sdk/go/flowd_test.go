package flowd

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"
)

type recorder struct {
	mu       sync.Mutex
	requests []string
	served   bool
}

func (r *recorder) add(path, body, key string) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.requests = append(r.requests, path+" "+body+" key="+key)
}

func (r *recorder) has(prefix, contains string) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	for _, q := range r.requests {
		if strings.HasPrefix(q, prefix) && strings.Contains(q, contains) {
			return true
		}
	}
	return false
}

func server(t *testing.T, rec *recorder) *httptest.Server {
	t.Helper()
	return httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
		body, _ := io.ReadAll(req.Body)
		rec.add(req.URL.Path, string(body), req.Header.Get("X-Api-Key"))
		w.Header().Set("Content-Type", "application/json")
		switch {
		case req.URL.Path == "/v1/jobs/activate":
			rec.mu.Lock()
			first := !rec.served
			rec.served = true
			rec.mu.Unlock()
			if first {
				_, _ = w.Write([]byte(`{"jobs":[{"key":"1","type":"t","processInstanceKey":"9","elementId":"E","retries":3,"variables":{"n":2},"customHeaders":{}},` +
					`{"key":"2","type":"t","processInstanceKey":"9","elementId":"E","retries":3,"variables":{"n":0},"customHeaders":{}}]}`))
				return
			}
			_, _ = w.Write([]byte(`{"jobs":[]}`))
		case strings.HasSuffix(req.URL.Path, "/conflict"):
			w.WriteHeader(http.StatusConflict)
		case req.URL.Path == "/v1/process-instances":
			_, _ = w.Write([]byte(`{"processInstanceKey":"77"}`))
		default:
			w.WriteHeader(http.StatusNoContent)
		}
	}))
}

func TestClientStartsInstancesAndSendsTheAPIKey(t *testing.T) {
	rec := &recorder{}
	srv := server(t, rec)
	defer srv.Close()

	key, err := NewClient(srv.URL, "flowd_secret").StartInstance(context.Background(), "order", map[string]any{"id": 1})

	if err != nil || key != "77" {
		t.Fatalf("StartInstance = %q, %v", key, err)
	}
	if !rec.has("/v1/process-instances", "key=flowd_secret") {
		t.Fatalf("the API key was not sent: %v", rec.requests)
	}
}

func TestClientToleratesConflictAndReportsOtherErrors(t *testing.T) {
	rec := &recorder{}
	srv := server(t, rec)
	defer srv.Close()
	client := NewClient(srv.URL, "")

	if err := client.Complete(context.Background(), Job{Key: "conflict"}, nil, "w"); err != nil {
		t.Fatalf("a 409 must not be an error: %v", err)
	}
	bad := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(http.StatusForbidden) }))
	defer bad.Close()
	var httpErr *HTTPError
	if err := NewClient(bad.URL, "").Complete(context.Background(), Job{Key: "1"}, nil, "w"); !errors.As(err, &httpErr) || httpErr.Status != 403 {
		t.Fatalf("expected a 403 HTTPError, got %v", err)
	}
}

func TestWorkerCompletesGoodJobsAndFailsBadOnesWithOneRetryLess(t *testing.T) {
	rec := &recorder{}
	srv := server(t, rec)
	defer srv.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	worker := NewWorker(NewClient(srv.URL, "flowd_k"), WorkerConfig{Type: "t", Name: "w", Concurrency: 2}, func(_ context.Context, job Job) (Result, error) {
		if job.Variables["n"].(float64) == 0 {
			return nil, errors.New("boom")
		}
		return Completed{Variables: map[string]any{"doubled": 4}}, nil
	})
	done := make(chan error, 1)
	go func() { done <- worker.Run(ctx) }()

	deadline := time.Now().Add(8 * time.Second)
	for time.Now().Before(deadline) && !(rec.has("/v1/jobs/1/complete", `"doubled":4`) && rec.has("/v1/jobs/2/fail", `"retries":2`)) {
		time.Sleep(50 * time.Millisecond)
	}
	cancel()
	<-done

	if !rec.has("/v1/jobs/1/complete", `"doubled":4`) {
		t.Fatalf("job 1 was not completed: %v", rec.requests)
	}
	if !rec.has("/v1/jobs/2/fail", `"retries":2`) || !rec.has("/v1/jobs/2/fail", "boom") {
		t.Fatalf("job 2 was not failed with one retry less: %v", rec.requests)
	}
}

func TestWorkerTurnsAPanicIntoAFailedJobAndKeepsRunning(t *testing.T) {
	rec := &recorder{}
	srv := server(t, rec)
	defer srv.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	worker := NewWorker(NewClient(srv.URL, ""), WorkerConfig{Type: "t", Name: "w"}, func(_ context.Context, job Job) (Result, error) {
		if job.Key == "1" {
			panic("oops")
		}
		return BpmnError{Code: "NO_STOCK", Message: "none left"}, nil
	})
	go func() { _ = worker.Run(ctx) }()

	deadline := time.Now().Add(8 * time.Second)
	for time.Now().Before(deadline) && !(rec.has("/v1/jobs/1/fail", "panicked") && rec.has("/v1/jobs/2/error", "NO_STOCK")) {
		time.Sleep(50 * time.Millisecond)
	}
	cancel()

	if !rec.has("/v1/jobs/1/fail", "panicked") || !rec.has("/v1/jobs/2/error", "NO_STOCK") {
		t.Fatalf("unexpected requests: %v", rec.requests)
	}
	var sample map[string]any
	if err := json.Unmarshal([]byte(`{"a":1}`), &sample); err != nil {
		t.Fatal(err)
	}
}
