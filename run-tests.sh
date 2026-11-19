#!/usr/bin/env bash

./node-env.sh --clear || exit 1
./node-env.sh --init || exit 1

docker build -t "testsystrik/grading-node:latest" . || exit 1
docker compose up -d || exit 1

sleep 10

export TRIK_STUDIO_IMAGE="testsystrik/trik-studio:2026.2-v2.4.0"
export EXAMPLES_DIRECTORY="./libs/trik-testsys/grading-scripts/examples"
export FAILURES_DIRECTORY="./libs/trik-testsys/grading-scripts/failures"
if ! dotnet run --project TestSys.Trik.GradingNode.TestRunner; then
    docker compose down
    exit 1
else
    docker compose down
    exit 0
fi
