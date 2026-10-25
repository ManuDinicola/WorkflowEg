package flowd

import (
	"context"
	"fmt"
	"sync"
	"time"
)

// Result is what a handler wants done with its job: Completed, Failed or BpmnError.
type Result interface{ isResult() }

// Completed completes the job with the given variables.
type Completed struct{ Variables map[string]any }

// Failed fails the job. A nil Retries means one less than now; Backoff delays the next attempt.
type Failed struct {
	Message string
	Retries *int
	Backoff time.Duration
}

// BpmnError throws a BPMN error that a boundary or event sub-process can catch.
type BpmnError struct {
	Code      string
	Message   string
	Variables map[string]any
}

func (Completed) isResult() {}
func (Failed) isResult()    {}
func (BpmnError) isResult() {}

// Handler runs one job. Returning an error (or panicking) fails the job with one retry less.
type Handler func(ctx context.Context, job Job) (Result, error)

// WorkerConfig configures a Worker.
type WorkerConfig struct {
	Type        string        // the job type to work on
	Name        string        // identifies this worker in logs and job ownership
	Concurrency int           // at most this many jobs at the same time (default 4)
	JobTimeout  time.Duration // how long a job may run before it is offered again (default 30 s)
}

// Worker long-polls for jobs of one type and runs a handler with bounded concurrency. It fetches only as many jobs as it has free slots.
type Worker struct {
	client  *Client
	cfg     WorkerConfig
	handler Handler
}

// NewWorker creates a worker; call Run to start it.
func NewWorker(client *Client, cfg WorkerConfig, handler Handler) *Worker {
	if cfg.Concurrency < 1 {
		cfg.Concurrency = 4
	}
	if cfg.JobTimeout <= 0 {
		cfg.JobTimeout = 30 * time.Second
	}
	return &Worker{client: client, cfg: cfg, handler: handler}
}

// Run polls until ctx is cancelled, then waits for the running jobs to finish. A transport failure waits a second and polls again.
func (w *Worker) Run(ctx context.Context) error {
	slots := make(chan struct{}, w.cfg.Concurrency)
	var running sync.WaitGroup
	defer running.Wait()
	for ctx.Err() == nil {
		slots <- struct{}{} // wait for one free slot
		free := 1
		for len(slots) < cap(slots) && free < cap(slots) { // take every other free slot too
			slots <- struct{}{}
			free++
		}
		jobs, err := w.client.ActivateJobs(ctx, ActivateRequest{Type: w.cfg.Type, Worker: w.cfg.Name, Timeout: w.cfg.JobTimeout, MaxJobs: free, RequestTimeout: 10 * time.Second})
		for i := len(jobs); i < free; i++ {
			<-slots // slots of jobs that were not fetched are free again
		}
		if err != nil {
			if ctx.Err() == nil {
				select {
				case <-time.After(time.Second):
				case <-ctx.Done():
				}
			}
			continue
		}
		for _, job := range jobs {
			running.Add(1)
			go func(job Job) {
				defer running.Done()
				defer func() { <-slots }()
				w.run(ctx, job)
			}(job)
		}
	}
	return ctx.Err()
}

func (w *Worker) run(ctx context.Context, job Job) {
	result, err := w.safely(ctx, job)
	// report even when the worker is stopping: the job would otherwise run again after its timeout
	report, cancel := context.WithTimeout(context.WithoutCancel(ctx), 10*time.Second)
	defer cancel()
	switch {
	case err != nil:
		_ = w.client.Fail(report, job, err.Error(), nil, 0, w.cfg.Name)
	case result == nil:
		_ = w.client.Complete(report, job, nil, w.cfg.Name)
	default:
		switch r := result.(type) {
		case Completed:
			_ = w.client.Complete(report, job, r.Variables, w.cfg.Name)
		case Failed:
			_ = w.client.Fail(report, job, r.Message, r.Retries, r.Backoff, w.cfg.Name)
		case BpmnError:
			_ = w.client.ThrowError(report, job, r.Code, r.Message, r.Variables, w.cfg.Name)
		}
	}
}

func (w *Worker) safely(ctx context.Context, job Job) (result Result, err error) {
	defer func() {
		if r := recover(); r != nil {
			err = fmt.Errorf("handler panicked: %v", r)
		}
	}()
	return w.handler(ctx, job)
}
