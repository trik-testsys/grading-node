module TestSys.Trik.GradingNode.Grading.Core

open System
open System.Diagnostics
open System.IO
open System.Threading
open Docker
open TestSys.Trik.GradingNode.Prelude

type InMemoryFile = {
    name: string
    content: byte[]
} with
    static member FromFile(path: string) =
        {
            name = Path.GetFileName path
            content = File.ReadAllBytes path
        }

[<RequireQualifiedAccess>]
type Task = {
    fields: InMemoryFile list
}

type GradingOptions = {
    dockerImage: string
    recordVideo: bool
}

type Submission =
    | VisualLanguageSubmission of InMemoryFile
    | PythonSubmission of InMemoryFile
    | JavaScriptSubmission of InMemoryFile

type SubmissionData = {
    id: int
    task: Task
    options: GradingOptions
    submission: Submission
}

type FieldResult = {
    name: string
    verdictFile: InMemoryFile
    videoFile: InMemoryFile option
}

type GradingError =
    | UnexpectedException of exn
    | NonZeroExitCode of int
    | MismatchedFiles
    | InnerTimeoutExceed
    | UnsupportedImageVersion of string

type GradingResult = {
    id: int
    result: Result<FieldResult list, GradingError>
}

type FileSystemOptions = {
    mountedDirectory: string
    hostDirectory: string
}

type GraderOptions = {
    fsOptions: FileSystemOptions
    innerTimeout: int
    nodeId: int
}

let tag = "Grading"
let logError submissionId msg = Logging.logError tag $"Submission[{submissionId}]: {msg}"
let logDebug submissionId msg = Logging.logDebug tag $"Submission[{submissionId}]: {msg}"
let logInfo submissionId msg = Logging.logInfo tag $"Submission[{submissionId}]: {msg}"
let logWarning submissionId msg = Logging.logWarning tag $"Submission[{submissionId}]: {msg}"

let rec private writeTmpFile dir (f: InMemoryFile) =
    let path = $"{dir}/{f.name}"
    Logging.logDebug tag $"Writing tmp file: path={path}, size={f.content.Length}"
    assert Directory.Exists(dir)
    let stream = File.Create(path)
    stream.Write(f.content)

type DockerGrader(options: GraderOptions, submissionData: SubmissionData) =

    static let rnd = Random()

    let submissionId = submissionData.id
    let fsOptions = options.fsOptions
    let recordVideo = submissionData.options.recordVideo

    let uid = Guid.NewGuid()
    let containerName = $"run-{uid}"
    let rootDirectory, rootHostDirectory =
        $"{fsOptions.mountedDirectory}/tmp-{uid}", $"{fsOptions.hostDirectory}/tmp-{uid}"

    let stopContainer () =
        logDebug submissionId "start stopping container"
        let p = createStopContainerProcess containerName
        p.Start() |> ignore
        p.WaitForExit()
        logDebug submissionId "finished stopping container"

    let mode =
        match submissionData.submission with
        | VisualLanguageSubmission _ -> "qrs"
        | PythonSubmission _ -> "py"
        | JavaScriptSubmission _ -> "js"

    let fromRoots f =
        f rootDirectory, f rootHostDirectory
    let fieldsDirectory, hostFieldsDirectory =
        fromRoots <| fun root -> $"{root}/fields"
    let resultsDirectory, hostResultsDirectory =
        fromRoots <| fun root -> $"{root}/results"
    let videoDirectory, hostVideoDirectory =
        fromRoots <| fun root -> $"{root}/video"
    let submissionFile, hostSubmissionFile =
        fromRoots <| fun root -> $"{root}/submission.{mode}"

    let initFileSystem () =
        logDebug submissionId "start initializing file system"
        let createDir = Directory.CreateDirectory >> ignore
        createDir rootDirectory
        createDir fieldsDirectory
        createDir resultsDirectory
        createDir videoDirectory

        submissionData.task.fields
        |> Seq.iter (writeTmpFile fieldsDirectory)

        match submissionData.submission with
        | VisualLanguageSubmission inMemoryFile
        | PythonSubmission inMemoryFile
        | JavaScriptSubmission inMemoryFile ->
            File.WriteAllBytes(submissionFile, inMemoryFile.content)

        logDebug submissionId "finished initializing file system"

    let clearFileSystem () =
        logDebug submissionId "start clearing file system"
        if Directory.Exists(rootDirectory) then
            Directory.Delete(rootDirectory, true)
        else
            logWarning submissionId "file system already cleared"
        logDebug submissionId "finished clearing file system"

    let getFiles dir =
        Directory.EnumerateFiles(dir)
        |> Seq.map InMemoryFile.FromFile


    let getFile dir (name: string) =
        let path = $"{dir}/{name}"
        if File.Exists path then
            Some <| {
                name = name
                content = File.ReadAllBytes path
            }
        else None

    let getVideo name = getFile videoDirectory $"{name}.mp4"
    let getVerdict name = getFile resultsDirectory $"{name}.json"

    let dockerOptions =
        let commonOptions =
            [
                EnvVar ("MODE", mode)
                Mount (hostSubmissionFile, $"/submission.{mode}", Readonly)
                Mount (hostResultsDirectory, "/results", ReadWrite)
                Mount (hostFieldsDirectory, "/fields", Readonly)
            ]
        match recordVideo with
        | false -> commonOptions
        | true -> Mount (hostVideoDirectory, "/video", ReadWrite) :: commonOptions

    let mutable disposed = false

    let getResults () =
        logDebug submissionId "start getting results"
        let getResult (name: string) =

            let nameWithoutExtension = Path.GetFileNameWithoutExtension name

            let verdict = getVerdict nameWithoutExtension
            let video = getVideo nameWithoutExtension

            logDebug submissionId $"got results for field[{nameWithoutExtension}]: : verdict[{verdict.IsSome}], video[{video.IsSome}]"

            match verdict, video with
            | Some verdict, Some video when recordVideo ->
                {
                    name = nameWithoutExtension
                    verdictFile = verdict
                    videoFile = Some video
                }
            | Some verdict, None when not recordVideo->
                {
                    name = nameWithoutExtension
                    verdictFile = verdict
                    videoFile = None
                }
            | _ ->
                let msg = $"unexpected files for field[{nameWithoutExtension}]: verdict[{verdict.IsSome}], video[{video.IsSome}]"
                logError submissionId $"getting results failed, {msg}"
                failwith msg

        let results =
            submissionData.task.fields
            |> Seq.map (fun x -> getResult x.name)
        logDebug submissionId "successfully finished getting results"
        results |> List.ofSeq

    let drainProcessOutputToLog prefix (proc: Process) =
        proc.OutputDataReceived.AddHandler(fun sender args ->
            if args.Data <> null then
                logDebug submissionId $"{prefix} stdout: {args.Data}"
        )
        proc.ErrorDataReceived.AddHandler(fun sender args ->
            if args.Data <> null then
                logDebug submissionId $"{prefix} stderr: {args.Data}"
        )
    
    member this.Grade(token: CancellationToken) =
        task {
            logInfo submissionId "start grading"

            use innerTimeoutTokenSource = new CancellationTokenSource()
            innerTimeoutTokenSource.CancelAfter(TimeSpan.FromSeconds(options.innerTimeout))
            let innerTimeoutToken = innerTimeoutTokenSource.Token

            use tokenSource = CancellationTokenSource.CreateLinkedTokenSource([| token; innerTimeoutToken |])
            let token = tokenSource.Token
            let dispose () = (this :> IDisposable).Dispose()


            innerTimeoutToken.Register(Action dispose) |> ignore

            let err e =
                {
                    id = submissionData.id
                    result = Error e
                }
            try
                initFileSystem ()
                let proc = createRunContainerProcess containerName submissionData.options.dockerImage dockerOptions
                drainProcessOutputToLog "TRIKStudio" proc
                proc.Start() |> ignore
                proc.BeginOutputReadLine()
                proc.BeginErrorReadLine()
                do! proc.WaitForExitAsync(token)
                if proc.ExitCode = 0 then
                    let result =
                        {
                            id = submissionData.id
                            result = Ok <| getResults ()
                        }
                    logInfo submissionId "finished grading, OK"
                    return result
                else
                    logInfo submissionId $"finished grading, non-zero exit code: {proc.ExitCode}"
                    return err <| NonZeroExitCode proc.ExitCode
            with
                | :? OperationCanceledException ->
                    logInfo submissionId "finished grading, inner timeout exceed"
                    return err <| InnerTimeoutExceed
                | e ->
                    logError submissionId $"finished grading, unexpected exception: {e}"
                    return err <| UnexpectedException e
        }

    interface IDisposable with
        member this.Dispose () =
            if not disposed then
                disposed <- true
                stopContainer ()
                clearFileSystem ()
