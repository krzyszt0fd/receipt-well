import sys
import json
import subprocess

data = json.load(sys.stdin)
file_path = data.get("tool_input", {}).get("file_path", "")

if not file_path.endswith(".csproj"):
    sys.exit(0)

result = subprocess.run(
    ["dotnet", "restore", "src/backend/ReceiptWell.sln"],
    capture_output=True,
    text=True,
    encoding="utf-8",
    errors="replace",
)

if result.returncode != 0:
    print(result.stdout + result.stderr, file=sys.stderr)
    sys.exit(2)
