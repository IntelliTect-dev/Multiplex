#!/bin/sh
# Starts Chrome the way the TripIt sign-in browser starts it -- with a window on the virtual display,
# its sandbox on, and a DevTools port -- and reports whether it came up. Deploy-MultiplexLinux.ps1
# runs it in the server's container, as the server's own user, at the end of every deploy.
display="${DISPLAY#:}"
if [ ! -S "/tmp/.X11-unix/X${display}" ]; then
    echo "No virtual display at ${DISPLAY}: Xvfb is not running in the container."
    exit 1
fi

profile=$(mktemp -d)
log="${profile}.log"
# Port 0: Chrome picks a free port and writes it to DevToolsActivePort once DevTools listens.
google-chrome-stable --user-data-dir="${profile}" --remote-debugging-port=0 --no-first-run \
    --no-default-browser-check --window-size=1280,900 about:blank >"${log}" 2>&1 &
pid=$!

result=1
waited=0
while [ "${waited}" -lt 60 ]; do
    if [ -s "${profile}/DevToolsActivePort" ]; then result=0; break; fi
    kill -0 "${pid}" 2>/dev/null || break
    sleep 1
    waited=$((waited + 1))
done
kill "${pid}" 2>/dev/null
wait "${pid}" 2>/dev/null

if [ "${result}" -eq 0 ]; then
    echo "Chrome started with a window on ${DISPLAY}, sandboxed."
elif [ "${waited}" -ge 60 ]; then
    echo "Chrome did not start within 60 s."
    tail -n 15 "${log}"
else
    echo "Chrome exited while starting."
    tail -n 15 "${log}"
fi
rm -rf "${profile}" "${log}"
exit "${result}"
