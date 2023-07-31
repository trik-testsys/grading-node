#!/bin/bash

test_submission(){
  TARGET=$1
  TEST_FIELD=$2
  OUTPUT_FILE=$3
  TMP_SUBMIT="/tmp-submit.qrs"
  cp "$TARGET" "$TMP_SUBMIT"
  ./TRIKStudio/bin/patcher -f "$TEST_FIELD" "$TMP_SUBMIT"
  ./TRIKStudio/bin/2D-model -platform offscreen --close -r "$OUTPUT_FILE" "$TMP_SUBMIT"
  rm -f "$TMP_SUBMIT"
}

for field in "${TASK_DIRECTORY}/"*;
do
  if ! [ -f "$field" ]; then
    exit 1
  fi
  FIELD_NAME=$(basename "$field" .xml)
  echo "$field"
  echo "$TASK_DIRECTORY"
  echo "$FIELD_NAME"
  echo "${RESULT_DIRECTORY}${FIELD_NAME}.json"
  test_submission "$SUBMIT_FILE" "$field" "${RESULT_DIRECTORY}${FIELD_NAME}.json"
done

