#!/bin/sh
# Atlas Local owns the database processes and remains PID 1. Bootstrap also runs
# on existing volumes, unlike scripts intended only for an empty database.
(
    attempt=0
    until mongosh --quiet mongodb://127.0.0.1:27017/admin?directConnection=true \
        --file /observability/bootstrap.js >/dev/null 2>&1; do
        attempt=$((attempt + 1))
        if [ "$attempt" -ge 60 ]; then
            echo 'MongoDB observability bootstrap failed; database remains available.' >&2
            exit 1
        fi
        sleep 2
    done
    echo 'MongoDB observability bootstrap complete.'
) &
exec "$@"
