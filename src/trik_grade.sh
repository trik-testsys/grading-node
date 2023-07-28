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

echo "$SUBMIT_FILE"
echo "$RESULT_DIRECTORY"
XDG_RUNTIME_DIR=/tmp/runtime-root ./TRIKStudio/trik-studio --version --platform offscreen | grep TRIK | cut -d ' ' -f 4-


for field in "${TASK_DIRECTORY}"*;
do
  FIELD_NAME=$(basename "$field" .xml)
  test_submission "$SUBMIT_FILE" "$field" "${RESULT_DIRECTORY}${FIELD_NAME}.json"
done

