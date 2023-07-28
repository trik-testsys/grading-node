#!/bin/bash

PROJECT_ROOT="/trik-testsys-grading-node/"
RED="\e[31m"
GREEN="\e[32m"
YELLOW="\e[33m"
END_COLOR="\e[0m"

error() {
    echo -e "${RED}$1${END_COLOR}"
}

warning() {
    echo -e "${YELLOW}$1${END_COLOR}"
}

successfully() {
    echo -e "${GREEN}$1${END_COLOR}"
}

ensure_user_want_continue() {
  read -r -p "Are you want to continue? [y/N]" continue
  if ! [ "$continue" = "y" ]; then
    exit 1
  fi
}

assert_file_exist() {
  if ! [ -f "$1" ]; then
    error "Required file doesn't exist: $1"
    exit 1
  fi
}

config_path() {
  echo "${PROJECT_ROOT}config/$1"
}

script_path() {
  echo "${PROJECT_ROOT}src/$1"
}

setup_environment() {
  if ! [ -d "$PROJECT_ROOT" ]; then
    error "Unexpected project location, required: $PROJECT_ROOT"
    exit 1
  fi

  WORKING_DIRECTORY=$(pwd)
  if ! [ "${WORKING_DIRECTORY}/" = "$PROJECT_ROOT" ]; then
    error "Unexpected working directory, required: $PROJECT_ROOT"
    exit 1
  fi

  # Validate project structure
  ADMIN_SSH_KEY_PATH="$(config_path "ADMIN_SSH_KEY")"
  GRADING_SYSTEM_SSH_KEY_PATH="$(config_path "GRADING_SYSTEM_SSH_KEY")"
  GRADE_SCRIPT_PATH="$(script_path "grade.sh")"
  assert_file_exist "$ADMIN_SSH_KEY_PATH"
  assert_file_exist "$GRADING_SYSTEM_SSH_KEY_PATH"
  assert_file_exist "$GRADE_SCRIPT_PATH"
}

setup_system(){
  # Update system and install dependencies
  sudo apt-get update
  sudo apt-get install ufw docker.io
  # Make other scripts executable
  chmod +x "$GRADE_SCRIPT_PATH"
  # Create required directories
  mkdir "${PROJECT_ROOT}submissions"
  mkdir "${PROJECT_ROOT}results"
  mkdir "${PROJECT_ROOT}tasks"
}

setup_ufw() {
  # Allow only ssh
  sudo ufw enable
  sudo ufw default deny incoming
  sudo ufw default allow outgoing
  sudo ufw allow ssh
}

setup_ssh(){
  # Add grading-server and admin ssh key
  cat "$ADMIN_SSH_KEY_PATH" >> ~/.ssh/authorized_keys
  cat "$GRADING_SYSTEM_SSH_KEY_PATH" >> ~/.ssh/authorized_keys
  # Disable password authentication
  sed -i "s/#PasswordAuthentication no/PasswordAuthentication no/" /etc/ssh/sshd_config
  sudo service ssh restart
}

setup_environment
SETUP_TARGET=$1

case $SETUP_TARGET in
  "system")
    setup_system
  ;;
  "ufw")
    setup_ufw
  ;;
  "ssh")
    setup_ssh
  ;;
  "all")
    setup_system
    setup_ufw
    setup_ssh
  ;;
  *)
    error "Incorrect setup target, available: system, ufw, ssh, all"
    exit 1
  ;;
esac
