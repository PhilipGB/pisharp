"""Keep parity test processes and their children within a small thread budget."""
import os


TEST_THREAD_LIMIT = 2
_THREAD_ENVIRONMENT = {
    "BLAS_NUM_THREADS": "2",
    "DOTNET_PROCESSOR_COUNT": "2",
    "MKL_NUM_THREADS": "2",
    "NUMEXPR_MAX_THREADS": "2",
    "NUMEXPR_NUM_THREADS": "2",
    "OMP_NUM_THREADS": "2",
    "OMP_THREAD_LIMIT": "2",
    "OPENBLAS_NUM_THREADS": "2",
    "RAYON_NUM_THREADS": "2",
    "TBB_NUM_THREADS": "2",
    "UV_THREADPOOL_SIZE": "2",
    "VECLIB_MAXIMUM_THREADS": "2",
}


def thread_limit_environment():
    """Return the environment settings needed by child processes in the harness."""
    return dict(_THREAD_ENVIRONMENT)


def apply_test_thread_limit():
    """Cap runnable CPU work and common native/runtime worker pools at two."""
    os.environ.update(_THREAD_ENVIRONMENT)

    get_affinity = getattr(os, "sched_getaffinity", None)
    set_affinity = getattr(os, "sched_setaffinity", None)
    if get_affinity is None or set_affinity is None:
        return

    allowed_cpus = sorted(get_affinity(0))
    if len(allowed_cpus) > TEST_THREAD_LIMIT:
        set_affinity(0, set(allowed_cpus[:TEST_THREAD_LIMIT]))
