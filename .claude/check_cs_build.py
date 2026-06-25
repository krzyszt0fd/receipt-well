import sys
import json
import subprocess
from pathlib import Path

data = json.load(sys.stdin)
file_path = data.get("tool_input", {}).get("file_path", "")

if not file_path.endswith(".cs"):
    sys.exit(0)

# Find the nearest .csproj by walking up from the edited file
search = Path(file_path).parent
csproj = None
while search != search.parent:
    found = list(search.glob("*.csproj"))
    if found:
        csproj = found[0]
        break
    search = search.parent

if csproj is None:
    sys.exit(0)

result = subprocess.run(
    ["dotnet", "build", str(csproj), "--no-restore"],
    capture_output=True,
    text=True,
    encoding="utf-8",
    errors="replace",
)

if result.returncode != 0:
    print(result.stdout + result.stderr, file=sys.stderr)
    sys.exit(2)
