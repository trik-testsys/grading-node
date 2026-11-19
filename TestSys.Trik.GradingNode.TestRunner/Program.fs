open System.IO
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading
open Grpc.Net.Client
open Serilog
open TestSys.Trik.GradingNode.Grading
open TestSys.Trik.GradingNode.Grpc
open TestSys.Trik.GradingNode
open TestSys.Trik.GradingNode.Proto


type Expected =
    | Scores of (string * int option) seq
    | ExitCode of int

type TestCase = {
    task: Core.Task
    submission: Core.Submission
    expected: Expected
}

let log msg = Prelude.Logging.logInfo "Tests" msg
let logFail submissionId msg = log $"[{submissionId}:FAIL]: {msg}"
let logPass submissionId = log $"[{submissionId}:PASSED]"

let setupLogger () =
    Log.Logger <-
        LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger()

let createClient () =
    let handler = new HttpClientHandler()
    handler.ServerCertificateCustomValidationCallback <- HttpClientHandler.DangerousAcceptAnyServerCertificateValidator

    let address = "http://127.0.0.1:8080"
    let options =
        let o = GrpcChannelOptions()
        o.HttpHandler <- handler
        o

    let channel = GrpcChannel.ForAddress(address, options)
    let client = GradingNode.GradingNodeClient channel
    client

let getTestCases (path: string) =

    let ensureExists name p = 
        if not <| Directory.Exists p then
            failwith $"{name} directory not exists: {p}"
        p

    let parseExpected path =
        File.ReadAllLines path
        |> Seq.choose (fun s ->
            let parts = s.Split(" ")
            match parts with
            | [| fieldName; score |] ->
                if fieldName = "TOTAL:" then None
                else

                let fieldName = fieldName.Replace(".json:", "")
                let score =
                    if score = "ERROR" then
                        None
                    else
                        Some <| int score
                Some (fieldName, score)
            | _ -> failwith $"Unexpected line[{s}] in file[{path}]"
        )

    let parseTask path =

        ensureExists "task path" path |> ignore

        let submissionsPath =
            $"{path}/solutions"
            |> ensureExists "submissions"

        let fieldsPath =
            $"{path}/fields"
            |> ensureExists "fields"

        let expectedPath =
            $"{path}/expected"
            |> ensureExists "expected"

        let submissionNames =
            submissionsPath
            |> Directory.GetFiles
            |> Seq.map Path.GetFileName

        let submissionsWithExpected =
            submissionNames
            |> Seq.choose (fun name -> 
                let path = $"{submissionsPath}/{name}"
                let file = Core.InMemoryFile.FromFile path
                let submission = 
                    match Path.GetExtension path with
                    | ".qrs" -> Some <| Core.VisualLanguageSubmission file
                    | ".py" -> None // Core.PythonSubmission file
                    | ".js" -> None // Core.JavaScriptSubmission file
                    | x -> failwith $"Unexpected extension: {x}"
                let expected =
                    parseExpected $"{expectedPath}/{Path.GetFileNameWithoutExtension name}.txt"
                submission
                |> Option.map (fun s -> s, expected)
            )

        let task =
            fieldsPath
            |> Directory.GetFiles
            |> Seq.map Core.InMemoryFile.FromFile
            |> List.ofSeq
            |> fun x -> { fields = x }: Core.Task

        submissionsWithExpected
        |> Seq.map (fun (s, e) -> { task = task; submission = s; expected = Scores e })

    ensureExists "tasks" path
    |> Directory.GetDirectories
    |> Seq.map parseTask
    |> Seq.concat

let getFailureCases (path: string) =

    let defaultFieldsPath = Path.Combine(path, "..", "examples", "straight_line", "fields")

    let readFields fieldsPath =
        fieldsPath
        |> Directory.GetFiles
        |> Seq.map Core.InMemoryFile.FromFile
        |> List.ofSeq
        |> fun x -> { fields = x }: Core.Task

    let parseCase casePath =
        let caseFieldsPath = Path.Combine(casePath, "fields")
        let fieldsPath = if Directory.Exists caseFieldsPath then caseFieldsPath else defaultFieldsPath
        let exitCode = File.ReadAllText(Path.Combine(casePath, "expected_exit_code.txt")).Trim() |> int
        let submission =
            Path.Combine(casePath, "submission.qrs")
            |> Core.InMemoryFile.FromFile
            |> Core.VisualLanguageSubmission
        { task = readFields fieldsPath; submission = submission; expected = ExitCode exitCode }

    if not <| Directory.Exists path then
        failwith $"failures directory not exists: {path}"
    path
    |> Directory.GetDirectories
    |> Seq.sort
    |> Seq.map parseCase

let parseResultFile (file: Core.InMemoryFile) =
    let text = System.Text.Encoding.UTF8.GetString file.content
    if text.Contains("error") then
        None
    else
        let integerPattern = Regex(@"\b(\d+)\b")
        let score =
            text.Split("\n")
            |> Seq.choose (fun s ->
                let regexMatch = integerPattern.Match s
                if regexMatch.Success then
                    let intValue = regexMatch.Groups[1].Value |> int
                    Some intValue
                else
                    None
            )
            |> Seq.last
        Some score

exception TestException of string

let runTests (testCases: TestCase seq) (client: GradingNode.GradingNodeClient) =

    let mutable failed = false
    
    let fail () =
        failed <- true

    let resultChannel = System.Threading.Channels.Channel.CreateUnbounded<Core.GradingResult>()
    let tokenSource = new CancellationTokenSource()
    let token = tokenSource.Token

    let tests = System.Collections.Generic.Dictionary<int64, TestCase>()

    let assertScores (result: Core.GradingResult) expected =
        match result.result with
        | Ok fieldResults ->
            let expected = Set expected
            let actual =
                fieldResults
                |> Seq.map (fun x -> x.name, parseResultFile x.verdictFile)
                |> Set
            let diff = Set.difference actual expected
            if not <| Seq.isEmpty diff then
                logFail result.id "Results not matched:"
                log "Expected:"
                for name, r in Seq.sortBy fst expected do
                    log $"  {name} -> {r}"
                log "Actual:"
                for name, r in Seq.sortBy fst actual do
                    log $"  {name} -> {r}"
                fail ()
            else
                logPass result.id
        | Error errorValue ->
            logFail result.id $"Unexpected error: {errorValue}"
            fail ()

    // Compared as sent over gRPC: the kind and the description keep the exit code that the transformed error loses.
    let assertExitCodeError (result: Proto.Result) exitCode =
        let expected = Proto.ErrorResult.FromGradingError(Core.GradingError.FromExitCode exitCode)
        match result.ResultCase with
        | Proto.Result.ResultOneofCase.Error
            when result.Error.Kind = expected.Kind && result.Error.Description = expected.Description ->
            logPass result.Id
        | Proto.Result.ResultOneofCase.Error ->
            logFail result.Id $"Expected error[{expected.Kind}: {expected.Description}], got error[{result.Error.Kind}: {result.Error.Description}]"
            fail ()
        | resultCase ->
            logFail result.Id $"Expected error[{expected.Kind}: {expected.Description}], got {resultCase}"
            fail ()

    let handleResult (result: Proto.Result) =
        match tests.TryGetValue(result.Id) with
        | true, testCase ->
            tests.Remove result.Id |> ignore
            match testCase.expected with
            | Scores expected -> assertScores (result.ToGradingResult()) expected
            | ExitCode exitCode -> assertExitCodeError result exitCode
        | false, _ ->
            fail ()
            logFail result.Id "Unexpected submission id"


    let options =
        {
            recordVideo = false
            dockerImage = Prelude.Configuration.varFromEnv "TRIK_STUDIO_IMAGE"
        }: Core.GradingOptions

    let sendSubmission (submissionId: int64) (testCase: TestCase) =
        task {
            let data: Core.SubmissionData = 
                {
                    id = submissionId
                    task = testCase.task
                    options = options
                    submission = testCase.submission 
                }
            let! result = client.GradeAsync (Proto.Submission.FromSubmissionData(data))
            handleResult result
        }

    let tasks = 
        testCases
        |> Seq.mapi ( fun i testCase -> 
            tests.Add(int64 i, testCase)
            log $"Sent submission[{i}]"
            sendSubmission (int64 i) testCase
        )

    task {
        let! _ = System.Threading.Tasks.Task.WhenAll tasks
        if tests.Count <> 0 then
            for test in tests do
                log $"No response for submission[{test.Key}]"
            fail ()
        return failed
    }

[<EntryPoint>]
let main _ =
    setupLogger ()
    let examples = getTestCases <| Prelude.Configuration.varFromEnv "EXAMPLES_DIRECTORY"
    let failures =
        match System.Environment.GetEnvironmentVariable "FAILURES_DIRECTORY" with
        | null | "" -> Seq.empty
        | path -> getFailureCases path
    let testCases = Seq.append examples failures |> List.ofSeq
    let client = createClient ()
    let failedTask = runTests testCases client
    let failed = failedTask.Result
    if failed then
        1
    else
        0
