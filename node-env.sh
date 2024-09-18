#!/usr/bin/env bash

init() {
  mkdir "grading-node-workspace"
}

clear() {
  rm -rf "grading-node-workspace"
}

case $1 in
  --init)
    init
    ;;
  --clear)
    clear
    ;;
  *)
    echo "Unknown option $1"
    exit 1
    ;;
esac

