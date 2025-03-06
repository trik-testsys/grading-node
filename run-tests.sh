#!/usr/bin/env bash

./node-env.sh --clear || exit 1
./node-env.sh --init || exit 1

docker build -t "testsystrik/grading-node:latest" . || exit 1
docker compose up -d || exit 1

sleep 10

export TRIK_STUDIO_IMAGE="testsystrik/trik-studio:release-2023.1-2024-10-10-2.0.0"
export EXAMPLES_DIRECTORY="./libs/trik-testsys/grading-scripts/examples"
if ! dotnet run --project TestSys.Trik.GradingNode.TestRunner; then
    docker compose down
    exit 1
else
    docker compose down
    exit 0
fi
