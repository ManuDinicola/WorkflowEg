// Package flowd is a worker SDK for Flowd: a small client of the REST API and a long-polling job worker. It uses only the Go standard library.
package flowd

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// Job is an activated job. Variables holds the JSON object the job was activated with.
type Job struct {
	Key                string            `json:"key"`
	Type               string            `json:"type"`
	ProcessInstanceKey string            `json:"processInstanceKey"`
	ElementID          string            `json:"elementId"`
	Retries            int               `json:"retries"`
	Variables          map[string]any    `json:"variables"`
	CustomHeaders      map[string]string `json:"customHeaders"`
}

// HTTPError is returned when the server answers with an error status.
type HTTPError struct {
	Status int
	Body   string
}

func (e *HTTPError) Error() string { return fmt.Sprintf("flowd answered %d: %s", e.Status, e.Body) }

// Client talks to the REST API of a running Flowd.
type Client struct {
	baseURL string
	apiKey  string
	http    *http.Client
}

// NewClient returns a client for baseURL such as "http://localhost:8080". apiKey may be empty when Flowd runs without authentication;
// a value that starts with "flowd_" is sent as an API key, anything else as a bearer token.
func NewClient(baseURL, apiKey string) *Client {
	return &Client{baseURL: strings.TrimRight(baseURL, "/"), apiKey: apiKey, http: &http.Client{Timeout: 70 * time.Second}}
}

// ActivateRequest describes which jobs to fetch.
type ActivateRequest struct {
	Type           string
	Worker         string
	Timeout        time.Duration // how long the worker may keep a job before it is offered again
	MaxJobs        int
	RequestTimeout time.Duration // how long the server may wait for jobs to appear (long polling)
	FetchVariables []string
}

// ActivateJobs long-polls for up to MaxJobs jobs of one type.
func (c *Client) ActivateJobs(ctx context.Context, r ActivateRequest) ([]Job, error) {
	body := map[string]any{
		"type": r.Type, "worker": r.Worker, "timeoutMs": r.Timeout.Milliseconds(), "maxJobs": r.MaxJobs,
		"requestTimeoutMs": r.RequestTimeout.Milliseconds(), "fetchVariables": r.FetchVariables,
	}
	var reply struct {
		Jobs []Job `json:"jobs"`
	}
	if err := c.post(ctx, "/v1/jobs/activate", body, &reply, false); err != nil {
		return nil, err
	}
	return reply.Jobs, nil
}

// Complete completes a job. A 409 (already completed or taken over) is not an error: delivery is at least once.
func (c *Client) Complete(ctx context.Context, job Job, variables map[string]any, worker string) error {
	return c.post(ctx, jobPath(job, "complete"), map[string]any{"variables": variables, "worker": worker}, nil, true)
}

// Fail reports a failed job. With retries == nil the retries left are one less than now.
func (c *Client) Fail(ctx context.Context, job Job, message string, retries *int, backoff time.Duration, worker string) error {
	left := job.Retries - 1
	if retries != nil {
		left = *retries
	}
	if left < 0 {
		left = 0
	}
	return c.post(ctx, jobPath(job, "fail"), map[string]any{"retries": left, "errorMessage": message, "retryBackoffMs": backoff.Milliseconds(), "worker": worker}, nil, true)
}

// ThrowError throws a BPMN error from a job.
func (c *Client) ThrowError(ctx context.Context, job Job, code, message string, variables map[string]any, worker string) error {
	return c.post(ctx, jobPath(job, "error"), map[string]any{"errorCode": code, "errorMessage": message, "variables": variables, "worker": worker}, nil, true)
}

// StartInstance starts a process instance and returns its key.
func (c *Client) StartInstance(ctx context.Context, bpmnProcessID string, variables map[string]any) (string, error) {
	var reply struct {
		Key string `json:"processInstanceKey"`
	}
	if err := c.post(ctx, "/v1/process-instances", map[string]any{"bpmnProcessId": bpmnProcessID, "variables": variables}, &reply, false); err != nil {
		return "", err
	}
	return reply.Key, nil
}

func jobPath(job Job, action string) string {
	return "/v1/jobs/" + url.PathEscape(job.Key) + "/" + action
}

func (c *Client) post(ctx context.Context, path string, payload any, out any, tolerateConflict bool) error {
	data, err := json.Marshal(payload)
	if err != nil {
		return err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.baseURL+path, bytes.NewReader(data))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	switch {
	case c.apiKey == "":
	case strings.HasPrefix(c.apiKey, "flowd_"):
		req.Header.Set("X-Api-Key", c.apiKey)
	default:
		req.Header.Set("Authorization", "Bearer "+c.apiKey)
	}
	resp, err := c.http.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, 16<<20))
	if err != nil {
		return err
	}
	if resp.StatusCode == http.StatusConflict && tolerateConflict {
		return nil
	}
	if resp.StatusCode >= 300 {
		return &HTTPError{Status: resp.StatusCode, Body: string(body)}
	}
	if out != nil && len(bytes.TrimSpace(body)) > 0 {
		return json.Unmarshal(body, out)
	}
	return nil
}
