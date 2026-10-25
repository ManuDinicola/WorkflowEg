"""Data types shared by the client and the worker."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Callable, Dict, List, Optional, Union

Variables = Dict[str, Any]


@dataclass(frozen=True)
class Job:
    """A job handed to a worker handler."""

    key: str
    type: str
    process_instance_key: str
    element_id: str
    retries: int
    deadline: str
    variables: Variables = field(default_factory=dict)
    custom_headers: Dict[str, str] = field(default_factory=dict)

    @staticmethod
    def from_json(data: Dict[str, Any]) -> "Job":
        return Job(
            key=str(data["key"]),
            type=data["type"],
            process_instance_key=str(data["processInstanceKey"]),
            element_id=data["elementId"],
            retries=int(data["retries"]),
            deadline=data["deadline"],
            variables=data.get("variables") or {},
            custom_headers=data.get("customHeaders") or {},
        )


@dataclass(frozen=True)
class Completed:
    """The handler finished the job; the variables are merged into the process."""

    variables: Optional[Variables] = None


@dataclass(frozen=True)
class Failed:
    """The handler failed. Without ``retries`` the remaining retries of the job minus one are used."""

    message: str
    retries: Optional[int] = None
    retry_backoff_ms: Optional[int] = None


@dataclass(frozen=True)
class BpmnError:
    """The handler reports a BPMN error that a boundary event can catch."""

    code: str
    message: Optional[str] = None
    variables: Optional[Variables] = None


JobResult = Union[Completed, Failed, BpmnError]
JobHandler = Callable[[Job], JobResult]


def complete(variables: Optional[Variables] = None) -> Completed:
    return Completed(variables)


def fail(message: str, retries: Optional[int] = None, retry_backoff_ms: Optional[int] = None) -> Failed:
    return Failed(message, retries, retry_backoff_ms)


def bpmn_error(code: str, message: Optional[str] = None, variables: Optional[Variables] = None) -> BpmnError:
    return BpmnError(code, message, variables)


@dataclass(frozen=True)
class WorkerOptions:
    """Tuning of a :class:`flowd_client.JobWorker` (all times in milliseconds)."""

    worker_name: str = "flowd-python-worker"
    job_timeout_ms: int = 30_000
    poll_timeout_ms: int = 10_000
    max_concurrent_jobs: int = 4
    fetch_variables: Optional[List[str]] = None
    min_backoff_ms: int = 200
    max_backoff_ms: int = 30_000
