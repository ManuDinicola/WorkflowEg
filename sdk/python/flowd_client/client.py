"""Thin typed client for the worker part of the Flowd API (standard library only)."""

from __future__ import annotations

import json
import urllib.error
import urllib.parse
import urllib.request
from typing import Any, Dict, List, Optional

from .types import BpmnError, Completed, Failed, Job, JobResult, Variables


class FlowdHttpError(Exception):
    """The server answered with an error status."""

    def __init__(self, status: int, body: str) -> None:
        super().__init__(f"Flowd answered {status}: {body}")
        self.status = status
        self.body = body


class FlowdClient:
    def __init__(self, base_url: str, api_key: Optional[str] = None, request_timeout_s: float = 70.0) -> None:
        self._base_url = base_url.rstrip("/")
        self._api_key = api_key
        self._timeout = request_timeout_s

    def activate_jobs(
        self,
        job_type: str,
        worker: str,
        timeout_ms: int,
        max_jobs: int,
        request_timeout_ms: int,
        fetch_variables: Optional[List[str]] = None,
    ) -> List[Job]:
        """Long-polls for up to ``max_jobs`` jobs of one type."""
        body = self._post(
            "/v1/jobs/activate",
            {
                "type": job_type,
                "worker": worker,
                "timeoutMs": timeout_ms,
                "maxJobs": max_jobs,
                "requestTimeoutMs": request_timeout_ms,
                "fetchVariables": fetch_variables,
            },
        )
        return [Job.from_json(j) for j in (body or {}).get("jobs", [])]

    def report(self, job: Job, result: JobResult, worker: str) -> None:
        """Reports the result of a job. A 409 (already completed or taken over) is not an error: delivery is at-least-once."""
        path = "/v1/jobs/" + urllib.parse.quote(job.key, safe="")
        if isinstance(result, Completed):
            self._post(path + "/complete", {"variables": result.variables, "worker": worker}, tolerate=(409,))
        elif isinstance(result, Failed):
            retries = result.retries if result.retries is not None else max(job.retries - 1, 0)
            self._post(
                path + "/fail",
                {"retries": retries, "errorMessage": result.message, "retryBackoffMs": result.retry_backoff_ms, "worker": worker},
                tolerate=(409,),
            )
        elif isinstance(result, BpmnError):
            self._post(
                path + "/error",
                {"errorCode": result.code, "errorMessage": result.message, "variables": result.variables, "worker": worker},
                tolerate=(409,),
            )
        else:
            raise TypeError(f"Unknown job result {result!r}")

    def start_instance(self, bpmn_process_id: str, variables: Optional[Variables] = None) -> str:
        """Starts a process instance and returns its key."""
        body = self._post("/v1/process-instances", {"bpmnProcessId": bpmn_process_id, "variables": variables})
        return str(body["processInstanceKey"])

    def _post(self, path: str, payload: Dict[str, Any], tolerate: tuple = ()) -> Any:
        headers = {"Content-Type": "application/json"}
        if self._api_key:
            headers["X-Api-Key"] = self._api_key
        request = urllib.request.Request(
            self._base_url + path,
            data=json.dumps(payload).encode("utf-8"),
            headers=headers,
            method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=self._timeout) as response:  # noqa: S310 - the URL comes from the caller
                text = response.read().decode("utf-8")
        except urllib.error.HTTPError as error:
            if error.code in tolerate:
                return None
            raise FlowdHttpError(error.code, error.read().decode("utf-8", "replace")) from error
        return json.loads(text) if text else None
