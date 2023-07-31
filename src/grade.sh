#!/bin/bash


SUBMIT_ID=$1
IMAGE_NAME=$2
GRADE_TIMEOUT=$3
PROJECT_ROOT="/trik-testsys-grading-node/"
SUBMIT_FILE="${PROJECT_ROOT}submissions/$SUBMIT_ID/submission.qrs"
RESULT_DIRECTORY="${PROJECT_ROOT}results/$SUBMIT_ID/"
TASK_DIRECTORY="${PROJECT_ROOT}submissions/$SUBMIT_ID/fields"
GRADING_SCRIPT="${PROJECT_ROOT}src/trik_grade.sh"


if ! [ -f "$SUBMIT_FILE" ]; then
  exit 1
fi

if ! [ -f "$GRADING_SCRIPT" ]; then
  exit 1
fi

if ! [[ $GRADE_TIMEOUT =~ ^[0-9]+$ ]] ; then
  exit 1
fi

if ! [ -d "$RESULT_DIRECTORY" ]; then
  exit 1
fi

timeout --signal=SIGKILL "$GRADE_TIMEOUT" \
  docker run \
  --rm \
  --mount type=bind,source="$GRADING_SCRIPT",target="$GRADING_SCRIPT",readonly \
  --mount type=bind,source="$SUBMIT_FILE",target="$SUBMIT_FILE",readonly \
  --mount type=bind,source="$RESULT_DIRECTORY",target="$RESULT_DIRECTORY" \
  --mount type=bind,source="$TASK_DIRECTORY",target="$TASK_DIRECTORY",readonly \
  --env SUBMIT_FILE="$SUBMIT_FILE" \
  --env RESULT_DIRECTORY="$RESULT_DIRECTORY" \
  --env TASK_DIRECTORY="$TASK_DIRECTORY" \
  "$IMAGE_NAME" \
  /bin/bash \
  "$GRADING_SCRIPT"
