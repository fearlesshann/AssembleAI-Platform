"""One bounded parse per child process; input/output use pipes, never temporary files."""
import ctypes
import os
import sys

_job_handle = None


def limit_resources():
    global _job_handle
    memory = 512 * 1024 * 1024
    if os.name == "nt":
        from ctypes import wintypes as w
        class Basic(ctypes.Structure):
            _fields_ = [("process_time", ctypes.c_longlong), ("job_time", ctypes.c_longlong), ("flags", w.DWORD),
                        ("minimum", ctypes.c_size_t), ("maximum", ctypes.c_size_t), ("active", w.DWORD),
                        ("affinity", ctypes.c_size_t), ("priority", w.DWORD), ("scheduling", w.DWORD)]
        class Io(ctypes.Structure):
            _fields_ = [(name, ctypes.c_ulonglong) for name in ("reads", "writes", "other", "read_bytes", "write_bytes", "other_bytes")]
        class Extended(ctypes.Structure):
            _fields_ = [("basic", Basic), ("io", Io), ("process_memory", ctypes.c_size_t), ("job_memory", ctypes.c_size_t),
                        ("peak_process_memory", ctypes.c_size_t), ("peak_job_memory", ctypes.c_size_t)]
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, w.LPCWSTR]
        kernel.CreateJobObjectW.restype = w.HANDLE
        kernel.SetInformationJobObject.argtypes = [w.HANDLE, ctypes.c_int, ctypes.c_void_p, w.DWORD]
        kernel.SetInformationJobObject.restype = w.BOOL
        kernel.AssignProcessToJobObject.argtypes = [w.HANDLE, w.HANDLE]
        kernel.AssignProcessToJobObject.restype = w.BOOL
        kernel.GetCurrentProcess.restype = w.HANDLE
        _job_handle = kernel.CreateJobObjectW(None, None)
        limits = Extended()
        limits.basic.flags = 0x100 | 0x2000 | 0x8  # process memory, kill on close, one active process
        limits.basic.active = 1
        limits.process_memory = memory
        if not _job_handle or not kernel.SetInformationJobObject(_job_handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
            raise RuntimeError("resource_limit_unavailable")
        if not kernel.AssignProcessToJobObject(_job_handle, kernel.GetCurrentProcess()):
            raise RuntimeError("resource_limit_unavailable")
    else:
        import resource
        resource.setrlimit(resource.RLIMIT_AS, (memory, memory))
        resource.setrlimit(resource.RLIMIT_CPU, (12, 12))


def main():
    import json
    try:
        limit_resources()
    except Exception:
        sys.stdout.write('{"code":"parser_unavailable"}')
        return
    from dataclasses import asdict
    from .documents import MAX_BYTES, DocumentError, parse_document
    try:
        result = parse_document(sys.stdin.buffer.read(MAX_BYTES + 1), sys.argv[1])
        output = json.dumps(asdict(result), ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        if len(output) > 8 * 1024 * 1024:
            raise DocumentError("document_too_complex")
        sys.stdout.buffer.write(output)
    except DocumentError as failure:
        sys.stdout.write(json.dumps({"code": failure.code}))
    except Exception:
        sys.stdout.write('{"code":"invalid_document"}')


if __name__ == "__main__":
    main()
