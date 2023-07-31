import json
import os.path
import subprocess
from typing import List, Tuple

from flask import Flask, request
from http import HTTPStatus
from werkzeug.datastructures import FileStorage


class Status:
    ACCEPTED = "ACCEPTED"
    FAILED = "FAILED"
    ERROR = "ERROR"
    RUNNING = "RUNNING"


# region Paths
PROJECT_ROOT = "/trik-testsys-grading-node"


def get_submission_directory_path(submission_id: int) -> str:
    return f"{PROJECT_ROOT}/submissions/{submission_id}"


def get_fields_directory_path(submission_id: int) -> str:
    return f"{PROJECT_ROOT}/submissions/{submission_id}/fields"


def get_submission_file_path(submission_id: int) -> str:
    return f"{PROJECT_ROOT}/submissions/{submission_id}/submission.qrs"


def get_result_directory_path(submission_id: int) -> str:
    return f"{PROJECT_ROOT}/results/{submission_id}"


def get_result_file_path(submission_id: int) -> str:
    result_dir_path: str = get_result_directory_path(submission_id)
    return f"{result_dir_path}/result.txt"
# endregion


# region Files
def read_all(path: str) -> str | None:
    if not os.path.exists(path) or not os.path.isfile(path):
        return None
    with open(path, "r") as f:
        return f.read()


def save_submission_file(submission_id: int, file: FileStorage):
    file.save(get_submission_file_path(submission_id))


def save_field_file(submission_id: int, n: int, file: FileStorage):
    file.save(f"{get_fields_directory_path(submission_id)}/field_{n}.xml")


def save_result_to_file(submission_id: int, status: str):
    result_path = get_result_file_path(submission_id)
    with open(result_path, "w") as f:
        f.write(status)


def prepare_for_testing(submission_id: int):

    def delete_file(path):
        if os.path.exists(path):
            os.remove(path)

    def create_directories(directories):
        for directory in directories:
            if not os.path.exists(directory):
                os.mkdir(directory)

    def clean_directories(directories):
        for directory in directories:
            for f in os.listdir(directory):
                os.remove(f"{directory}/{f}")

    submission_dir = get_submission_directory_path(submission_id)
    result_dir = get_result_directory_path(submission_id)
    fields_dir = get_fields_directory_path(submission_id)
    submission_file = get_submission_file_path(submission_id)

    create_directories([
        submission_dir,
        result_dir,
        fields_dir
    ])
    clean_directories([
        result_dir,
        fields_dir
    ])
    delete_file(submission_file)


# endregion


# region Result
def get_result(submission_id: int) -> str:
    result = read_all(get_result_file_path(submission_id))
    if result is None:
        return Status.RUNNING
    else:
        return result


def build_result(submission_id: int, exit_code: int):
    if (get_result(submission_id)) != Status.RUNNING:
        return

    result_dir_path = get_result_directory_path(submission_id)

    if exit_code == 124:
        save_result_to_file(submission_id, Status.FAILED)
        return
    if exit_code != 0:
        save_result_to_file(submission_id, Status.ERROR)
        return

    for field_result in os.listdir(result_dir_path):
        field_result_path = f"{result_dir_path}/{field_result}"
        result_json_text = read_all(field_result_path)
        assert (result_json_text is not None)
        result_json = json.loads(result_json_text)

        for logs in result_json:
            if logs["level"] == "error":
                save_result_to_file(submission_id, Status.FAILED)
                return

    save_result_to_file(submission_id, Status.ACCEPTED)


# endregion


class Grader:

    def __init__(self):
        self._was_setup = False
        self._trik_studio_image = ""
        self._timeout = 0
        self._ran_processes: List[Tuple[subprocess.Popen, int]] = []
        self._handled_by_current_run = []

    def setup(self, trik_studio_image: str, timeout: int):
        self._was_setup = True
        self._trik_studio_image = trik_studio_image
        self._timeout = timeout

    def was_setup(self) -> bool:
        return self._was_setup

    def start_grade(self, submission_id: int):
        if not self._was_setup:
            return

        process = subprocess.Popen([
            "/bin/bash",
            f"{PROJECT_ROOT}/src/grade.sh",
            str(submission_id),
            self._trik_studio_image,
            str(self._timeout)
        ])

        self._ran_processes.append((process, submission_id))

    def _proceed_processes(self):
        finished = []
        for proc, submission_id in self._ran_processes:
            if proc.poll() is None:
                continue
            finished.append(submission_id)
            build_result(submission_id, proc.returncode)

        self._ran_processes = [
            (proc, submission_id)
            for (proc, submission_id) in self._ran_processes
            if submission_id not in finished
        ]

    def get_result(self, submission_id: int) -> str:
        self._proceed_processes()
        return get_result(submission_id)


app = Flask(__name__)
grader = Grader()


def empty_body(code):
    return '', code


@app.route("/setup", methods=["POST"])
def setup():
    trik_studio_image = request.form["trik_studio_image"]
    if trik_studio_image is None:
        return empty_body(HTTPStatus.BAD_REQUEST)

    timeout = request.form["timeout"]
    if timeout is None:
        return empty_body(HTTPStatus.BAD_REQUEST)

    grader.setup(trik_studio_image, int(timeout))
    return empty_body(HTTPStatus.CREATED)


@app.route("/submission/<submission_id>", methods=["POST", "GET"])
def submission(submission_id):
    if request.method == "POST":

        submission_file = request.files["submission_file"]
        if submission_file is None:
            return empty_body(HTTPStatus.BAD_REQUEST)

        fields_count = request.form["fields_count"]
        if fields_count is None or int(fields_count) <= 0:
            return empty_body(HTTPStatus.BAD_REQUEST)

        if not grader.was_setup():
            return empty_body(HTTPStatus.FAILED_DEPENDENCY)

        fields_files = []
        for i in range(0, int(fields_count)):
            field_file = request.files[f"field_{i}"]
            if field_file is None:
                return empty_body(HTTPStatus.BAD_REQUEST)
            fields_files.append(field_file)

        prepare_for_testing(submission_id)

        save_submission_file(submission_id, submission_file)
        for i, field_file in enumerate(fields_files):
            save_field_file(submission_id, i, field_file)

        grader.start_grade(submission_id)

        return empty_body(HTTPStatus.CREATED)

    elif request.method == "GET":
        return grader.get_result(submission_id), HTTPStatus.OK

    else:
        return empty_body(HTTPStatus.METHOD_NOT_ALLOWED)
