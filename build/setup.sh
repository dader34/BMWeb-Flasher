#!/usr/bin/env bash
# One-time setup: pull the EdiabasLib submodule and add its net8.0 target so it
# builds on macOS/Linux. Idempotent — safe to run more than once.
set -euo pipefail
cd "$(dirname "$0")/.."

git submodule update --init --recursive

CSPROJ="external/ediabaslib/EdiabasLib/EdiabasLib/EdiabasLib.csproj"
PROPS="external/ediabaslib/EdiabasLib/Directory.Build.props"

# Add a plain net8.0 TFM alongside the Windows/Android ones.
if ! grep -q ";net8.0;" "$CSPROJ"; then
  python3 - "$CSPROJ" <<'PY'
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8-sig').read()
s = s.replace(
    "<TargetFrameworks>net10.0-windows10.0.26100.0;net8.0-windows10.0.26100.0;net481;$(_AndroidTargets)</TargetFrameworks>",
    "<TargetFrameworks>net10.0-windows10.0.26100.0;net8.0-windows10.0.26100.0;net481;net8.0;$(_AndroidTargets)</TargetFrameworks>")
s = s.replace(
    "<SignAssembly>true</SignAssembly>",
    "<SignAssembly Condition=\"'$(TargetFramework)' != 'net8.0'\">true</SignAssembly>")
s = s.replace(
    "<ItemGroup Condition=\"$(TargetFramework.Contains('windows'))\">",
    "<ItemGroup Condition=\"$(TargetFramework.Contains('windows')) or '$(TargetFramework)' == 'net8.0'\">")
open(p, 'w', encoding='utf-8').write(s)
print("patched", p)
PY
fi

# The submodule's Directory.Build.props needs EnableWindowsTargeting so the SDK
# will evaluate the Windows TFMs during restore on a non-Windows host.
if ! grep -q "EnableWindowsTargeting" "$PROPS"; then
  python3 - "$PROPS" <<'PY'
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8-sig').read()
s = s.replace(
    "<EnableAndroidTargets>false</EnableAndroidTargets>",
    "<EnableAndroidTargets>false</EnableAndroidTargets>\n    <EnableWindowsTargeting>true</EnableWindowsTargeting>")
open(p, 'w', encoding='utf-8').write(s)
print("patched", p)
PY
fi

echo "Setup complete. Build with:  dotnet build -c Release src/BmwebFlasher"
