import json
import os.path
import subprocess
import sys
import logging
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
project_root = "/trik-testsys-grading-node"
root_overriden = False


def get_log_file_path():
    return f"{project_root}/log.txt"


def get_submission_directory_path(submission_id: int) -> str:
    return f"{project_root}/submissions/{submission_id}"


def get_fields_directory_path(submission_id: int) -> str:
    return f"{project_root}/submissions/{submission_id}/fields"


def get_submission_file_path(submission_id: int) -> str:
    return f"{project_root}/submissions/{submission_id}/submission.qrs"


def get_result_directory_path(submission_id: int) -> str:
    return f"{project_root}/results/{submission_id}"


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


def save_result_to_file(submission_id: int, status: str, trik_result_json_array: List[dict] | None):
    result_path = get_result_file_path(submission_id)
    with open(result_path, "w") as f:
        f.write(json.dumps({
            "status": status,
            "trikResultJsonArray": trik_result_json_array
        }))


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
        return json.dumps({
            "status": Status.RUNNING,
            "trikResultJsonArray": None,
        })
    else:
        return json.loads(result)


def build_result(submission_id: int, exit_code: int):
    if (json.loads(get_result(submission_id))["status"]) != Status.RUNNING:
        return

    result_dir_path = get_result_directory_path(submission_id)

    if exit_code == 124:
        save_result_to_file(submission_id, Status.FAILED, None)
        return
    if exit_code != 0:
        save_result_to_file(submission_id, Status.ERROR, None)
        return

    trik_result_json_array = []
    for field_result in os.listdir(result_dir_path):
        field_result_path = f"{result_dir_path}/{field_result}"
        result_json_text = read_all(field_result_path)
        assert (result_json_text is not None)
        result_json = json.loads(result_json_text)
        trik_result_json_array.append(result_json)

    for trik_result_json in trik_result_json_array:
        for logs in trik_result_json:
            if logs["level"] == "error":
                save_result_to_file(submission_id, Status.FAILED, trik_result_json_array)
                return

    save_result_to_file(submission_id, Status.ACCEPTED, trik_result_json_array)
# endregion


class Grader:

    def __init__(self):
        self._ran_processes: List[Tuple[subprocess.Popen, int]] = []
        self._handled_by_current_run = []

    def start_grade(self, submission_id: int, trik_studio_image: str, timeout: int):

        args = [
            "/bin/bash",
            f"{project_root}/src/grade.sh",
            str(submission_id),
            trik_studio_image,
            str(timeout)
        ]

        if root_overriden:
            args.append(project_root)

        process = subprocess.Popen(args)
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


@app.route("/submission/<submission_id>", methods=["POST", "GET"])
def submission(submission_id):
    if request.method == "POST":

        submission_file = request.files["submission_file"]
        if submission_file is None:
            return empty_body(HTTPStatus.BAD_REQUEST)

        fields_count = request.form["fields_count"]
        if fields_count is None or int(fields_count) <= 0:
            return empty_body(HTTPStatus.BAD_REQUEST)

        timeout = request.form["timeout"]
        if timeout is None or int(timeout) <= 0:
            return empty_body(HTTPStatus.BAD_REQUEST)

        trik_studio_image = request.form["trik_studio_image"]
        if fields_count is None:
            return empty_body(HTTPStatus.BAD_REQUEST)

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

        grader.start_grade(
            submission_id,
            trik_studio_image,
            int(timeout)
        )

        return empty_body(HTTPStatus.CREATED)

    elif request.method == "GET":
        return grader.get_result(submission_id), HTTPStatus.OK

    else:
        return empty_body(HTTPStatus.METHOD_NOT_ALLOWED)


@app.route("/ping", methods=["GET"])
def ping():
    return empty_body(HTTPStatus.OK)


if __name__ == "__main__":
    # Overriding options only for simplifying development
    port = 8080
    try:
        override_port_index = sys.argv.index("--override-port")
        port = sys.argv[override_port_index + 1]
    except ValueError:
        pass

    root = project_root
    try:
        override_root_index = sys.argv.index("--override-root")
        root = sys.argv[override_root_index + 1]
        root_overriden = True
    except ValueError:
        pass

    project_root = root
    logging.basicConfig(
        filename=get_log_file_path(),
        encoding="utf-8",
        level=logging.DEBUG
    )
    app.run(host="0.0.0.0", port=port)
