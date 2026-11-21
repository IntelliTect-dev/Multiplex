#!/bin/sh
# Starts the virtual display the headed sign-in browser draws on, then runs the server in this
# shell's place, so Docker's stop signal reaches it.
set -e
display="${DISPLAY#:}"
# A restarted container keeps /tmp: drop the lock its earlier display left behind.
rm -f "/tmp/.X${display}-lock" "/tmp/.X11-unix/X${display}"
Xvfb ":${display}" -screen 0 1280x1024x24 -nolisten tcp >/dev/null 2>&1 &
exec dotnet /app/Multiplex.Web.dll
