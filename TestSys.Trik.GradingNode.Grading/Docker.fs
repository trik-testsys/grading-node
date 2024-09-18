module internal TestSys.Trik.GradingNode.Grading.Docker

open System.Diagnostics
open System.IO
open System.Text
open TestSys.Trik.GradingNode.Prelude

let tag = "Docker"
let logDebug msg = Logging.logDebug tag msg

type MountMode =
    | Readonly
    | ReadWrite

type DockerOptions =
    | Mount of string * string * MountMode
    | EnvVar of string * string
    | User of uint * uint

let private buildOptions (options: DockerOptions seq) =
    let args = StringBuilder()
    for option in options do
        match option with
        | Mount (source, target, mountMode) ->
            match mountMode with
            | Readonly -> args.Append $" --mount type=bind,source={source},target={target},readonly" |> ignore
            | ReadWrite -> args.Append $" --mount type=bind,source={source},target={target}" |> ignore
        | EnvVar (name, value) -> args.Append $""" -e {name}="{value}" """ |> ignore
        | User (uid, gid) -> args.Append $" --user {uid}:{gid}" |> ignore
    args.ToString ()

let private executeDockerCommand args =
    logDebug $"Execute: docker{args}"
    let info = ProcessStartInfo()
    info.WorkingDirectory <- Directory.GetCurrentDirectory()
    info.FileName <- "docker"
    info.Arguments <- args
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true

    Process.Start info

let runContainer (containerName: string) (imageName: string) (options: DockerOptions seq) =
    let options = buildOptions options
    let command = $" run --name {containerName} --rm {options} {imageName} /bin/bash /grade.sh"
    executeDockerCommand command

let stopContainer (containerName: string) =
    let command = $" stop {containerName}"
    executeDockerCommand command