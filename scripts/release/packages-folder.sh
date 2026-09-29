#!/usr/bin/env bash
# Prints the NuGet global packages folder, where a restore put the packages the
# release reads license texts from, or refuses with the path it tried.
#
#   scripts/release/packages-folder.sh
#
# NUGET_PACKAGES wins when it is set, as it does for the restore itself.
# Otherwise the folder is the one `dotnet nuget locals global-packages --list`
# names, read from the one line that begins with "global-packages: ": the first
# dotnet command on a fresh machine prints a welcome text before it. Run it after
# a restore; before one, the folder may not exist yet.
set -euo pipefail

listed=
if [ -n "${NUGET_PACKAGES:-}" ]; then
  folder=$NUGET_PACKAGES
else
  listed=$(DOTNET_NOLOGO=1 dotnet nuget locals global-packages --list 2>&1 | tr -d '\r') || true
  folder=$(sed -n 's/^global-packages: //p' <<< "$listed" | head -n 1)
fi
folder=${folder%/}
folder=${folder%\\}
if [ -z "$folder" ] || [ ! -d "$folder" ]; then
  if [ -n "${NUGET_PACKAGES:-}" ]; then from="from NUGET_PACKAGES"; else from="from dotnet nuget locals; NUGET_PACKAGES is not set"; fi
  printf "refused: could not find the NuGet packages folder: tried '%s' (%s)" "${folder:-nothing}" "$from" >&2
  [ -n "$listed" ] && printf '; dotnet nuget locals printed: %s' "$(tr '\n' ' ' <<< "$listed")" >&2
  printf '\n' >&2
  exit 1
fi
printf '%s\n' "$folder"
