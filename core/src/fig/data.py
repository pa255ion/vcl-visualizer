# Run one VCL `data` command (Python), in its work directory: runs code.py
# with numpy as np (scipy, sympy, networkx can be imported) and writes its
# variables to out.json: numbers, strings, lists and arrays (1D, or 2D as rows),
# for the lesson to use as {name.var}. On failure: err.txt. Runs inside the
# same sandbox as figures (core/src/fig.zig). (Changing the output? Bump
# vcl.fig_version.)
import json
import math
import resource
import signal
import traceback
import types

resource.setrlimit(resource.RLIMIT_CPU, (30, 35))
resource.setrlimit(resource.RLIMIT_AS, (3 << 30, 3 << 30))
resource.setrlimit(resource.RLIMIT_FSIZE, (64 << 20, 64 << 20))


def _too_long(*_):
    raise TimeoutError("the code ran for more than 30 s of CPU time and was stopped")


signal.signal(signal.SIGXCPU, _too_long)

import numpy as np

MAX_ITEMS = 20000


def plain(v):
    """A JSON-ready value, or None if it isn't data."""
    if isinstance(v, (bool, np.bool_)):
        return bool(v)
    if isinstance(v, (int, np.integer)):
        return int(v)
    if isinstance(v, (float, np.floating)):
        f = float(v)
        return f if math.isfinite(f) else None
    if isinstance(v, str):
        return v
    if isinstance(v, (list, tuple, np.ndarray)):
        a = np.asarray(v)
        if a.dtype.kind not in "biuf" or a.ndim not in (1, 2) or a.size > MAX_ITEMS:
            return None
        return np.where(np.isfinite(a), a, 0).astype(float).tolist() if a.dtype.kind == "f" else a.tolist()
    return None


env = {"__name__": "__data__", "np": np}
try:
    exec(compile(open("code.py").read(), "<data>", "exec"), env)
    out = {}
    for k, v in env.items():
        if k.startswith("_") or k == "np" or isinstance(v, (types.ModuleType, types.FunctionType, type)):
            continue
        p = plain(v)
        if p is not None:
            out[k] = p
    json.dump(out, open("out.json", "w"))
except BaseException as e:  # noqa: BLE001
    line = next((f.lineno for f in reversed(traceback.extract_tb(e.__traceback__)) if f.filename == "<data>"), None)
    open("err.txt", "w").write(f"{type(e).__name__}: {e}" + (f" (code line {line})" if line else ""))
