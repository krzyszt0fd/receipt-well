import sys
import json
import subprocess
from pathlib import Path

NPX = "npx.cmd" if sys.platform == "win32" else "npx"

data = json.load(sys.stdin)
file_path = data.get("tool_input", {}).get("file_path", "")

if not (file_path.endswith(".ts") or file_path.endswith(".html")):
    sys.exit(0)

# Only care about frontend source files
p = Path(file_path)
if "frontend" not in p.parts:
    sys.exit(0)

# Locate the frontend root (directory containing angular.json)
frontend_root = None
for parent in p.parents:
    if (parent / "angular.json").exists():
        frontend_root = parent
        break

if frontend_root is None:
    sys.exit(0)

failed = False

# ESLint on the changed file
eslint = subprocess.run(
    [NPX, "eslint", str(p)],
    capture_output=True,
    text=True,
    encoding="utf-8",
    errors="replace",
    cwd=str(frontend_root),
)
if eslint.returncode != 0:
    print("ESLint:\n" + eslint.stdout + eslint.stderr)
    failed = True

# TypeScript type check (whole project, incremental)
tsc = subprocess.run(
    [NPX, "tsc", "--noEmit", "-p", "tsconfig.app.json"],
    capture_output=True,
    text=True,
    encoding="utf-8",
    errors="replace",
    cwd=str(frontend_root),
)
if tsc.returncode != 0:
    print("TypeScript:\n" + tsc.stdout + tsc.stderr)
    failed = True

if failed:
    sys.exit(2)
