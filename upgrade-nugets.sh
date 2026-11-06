#!/bin/sh
# https://github.com/dotnet-outdated/dotnet-outdated
set -e # exit on error

if ! git diff --cached --quiet; then
  echo "Aborted: staged changes detected in git"
  exit 1
fi

if ! command -v dotnet-outdated
then
	# https://github.com/dotnet-outdated/dotnet-outdated
	dotnet tool install --global dotnet-outdated-tool
fi

dotnet outdated -u

if git diff --quiet mise.toml \
  WasteRegulatorCaseManagement/WasteRegulatorCaseManagement.csproj \
  WasteRegulatorCaseManagement.Test/WasteRegulatorCaseManagement.Test.csproj; then
  echo "No package updates available, nothing to do"
  exit 0
fi

echo "Running regression tests..."
dotnet test

echo "Committing upgrades..."
git commit --include mise.toml \
--include WasteRegulatorCaseManagement/WasteRegulatorCaseManagement.csproj \
--include WasteRegulatorCaseManagement.Test/WasteRegulatorCaseManagement.Test.csproj \
--message "chore: Dependency upgrades (nuget)"
