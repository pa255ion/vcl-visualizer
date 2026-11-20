# Render one VCL `fig` (Python), in its work directory: runs code.py with numpy as np,
# matplotlib.pyplot as plt (and scipy, sympy, networkx if wanted), styled to
# match the board, and writes out.svg and out.png (transparent),
# meta.json (each axes' place in the figure and its data limits, for
# pointing into the figure) or, on failure, err.txt and a figure that
# shows the error. (Changing the output? Bump vcl.fig_version, so caches renew.)
# Runs inside the sandbox `vcl serve` sets up (core/src/fig.zig):
# no network, nothing writable but the work directory, limited time and memory.
import json
import os
import resource
import sys
import traceback

import signal

resource.setrlimit(resource.RLIMIT_CPU, (30, 35))


def _too_long(*_):
    raise TimeoutError("the code ran for more than 30 s of CPU time and was stopped")


signal.signal(signal.SIGXCPU, _too_long)
resource.setrlimit(resource.RLIMIT_AS, (3 << 30, 3 << 30))
resource.setrlimit(resource.RLIMIT_FSIZE, (64 << 20, 64 << 20))

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

FG, MUTED, GRID = "#e8edf3", "#8796ab", "#2a3647"
PALETTE = ["#42a5f5", "#ffa726", "#66bb6a", "#f06292", "#ab47bc", "#26a69a", "#ffee58"]
plt.rcParams.update({
    "figure.figsize": (8, 5),
    "figure.dpi": 100,
    "figure.facecolor": "none",
    "savefig.facecolor": "none",
    "savefig.transparent": True,
    "axes.facecolor": "none",
    "axes.edgecolor": MUTED,
    "axes.labelcolor": FG,
    "axes.titlecolor": FG,
    "axes.titlesize": 20,
    "axes.labelsize": 17,
    "axes.grid": True,
    "axes.prop_cycle": matplotlib.cycler(color=PALETTE),
    "axes.spines.top": False,
    "axes.spines.right": False,
    "grid.color": GRID,
    "grid.linewidth": 0.8,
    "xtick.color": MUTED,
    "ytick.color": MUTED,
    "xtick.labelcolor": FG,
    "ytick.labelcolor": FG,
    "xtick.labelsize": 15,
    "ytick.labelsize": 15,
    "text.color": FG,
    "lines.linewidth": 2.6,
    "lines.markersize": 7,
    "patch.edgecolor": "none",
    "legend.frameon": False,
    "legend.fontsize": 15,
    "legend.labelcolor": FG,
    "font.family": "DejaVu Sans",
    "font.size": 16,
    "image.cmap": "viridis",
    "svg.fonttype": "path",
})
plt.show = lambda *a, **k: None  # nothing to show: the figure is saved

code = open("code.py").read()
env = {"__name__": "__fig__", "np": np, "plt": plt, "matplotlib": matplotlib}
error = None
try:
    exec(compile(code, "<fig>", "exec"), env)
    if not plt.get_fignums():
        raise RuntimeError("the code drew nothing (use plt.plot, plt.imshow, …)")
except BaseException as e:  # noqa: BLE001 (SystemExit too: it's the lesson's code)
    line = next((f.lineno for f in reversed(traceback.extract_tb(e.__traceback__)) if f.filename == "<fig>"), None)
    error = f"{type(e).__name__}: {e}" + (f" (code line {line})" if line else "")

fig = plt.gcf()
if error:
    plt.close("all")
    fig = plt.figure(figsize=(8, 2.2))
    fig.text(0.03, 0.75, "figure failed", color="#ef5350", fontsize=20, weight="bold")
    fig.text(0.03, 0.45, error[:300], color=FG, fontsize=15, family="DejaVu Sans Mono", wrap=True, va="top")
    open("err.txt", "w").write(error)
else:
    try:
        fig.set_layout_engine("constrained")
    except Exception:  # noqa: BLE001
        pass

fig.savefig("out.svg", format="svg")
fig.savefig("out.png", format="png", dpi=200)

if not error:
    fig.canvas.draw()
    w, h = fig.get_size_inches()
    meta = {"size": [w, h], "axes": []}
    for ax in fig.axes:
        b = ax.get_position()
        meta["axes"].append({
            "box": [b.x0, b.y0, b.x1, b.y1],  # fractions of the figure, from the bottom left
            "x": list(ax.get_xlim()), "y": list(ax.get_ylim()),
            "xlog": ax.get_xscale() == "log", "ylog": ax.get_yscale() == "log",
            "name": ax.get_label() if not ax.get_label().startswith("<") else "",
        })
    json.dump(meta, open("meta.json", "w"))
sys.exit(0)
