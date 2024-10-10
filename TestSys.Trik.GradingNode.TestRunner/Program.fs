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


type TestCase = {
    task: Core.Task
    submission: Core.Submission
    expected: (string * int option) seq
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
        |> Seq.map (fun (s, e) -> { task = task; submission = s; expected = e})

    ensureExists "tasks" path
    |> Directory.GetDirectories
    |> Seq.map parseTask
    |> Seq.concat

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

    let call = client.Grade()

    let submissionChannel = System.Threading.Channels.Channel.CreateUnbounded<Core.SubmissionData>()
    let resultsChannel = System.Threading.Channels.Channel.CreateUnbounded<Core.GradingResult>()

    let tokenSource = new CancellationTokenSource()
    let token = tokenSource.Token

    let tests = System.Collections.Generic.Dictionary<int, TestCase>()

    let handleResults (result: Core.GradingResult) =
        match tests.TryGetValue(result.id) with
        | true, { task = _; submission = _; expected = expected } ->
            tests.Remove result.id |> ignore
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
        | false, _ ->
            fail ()
            logFail result.id "Unexpected submission id"

    let resultsRedirect =
        Streams.redirectStreamToChannel
            "ResultsRedirect"
            token
            call.ResponseStream
            resultsChannel.Writer
            _.ToGradingResult()

    let submissionsRedirect =
        Streams.redirectChannelToStream
            "SubmissionRedirect"
            token
            submissionChannel.Reader
            call.RequestStream
            Submission.FromSubmissionData

    let worker = Streams.attachWorkerToChannel "Worker" token resultsChannel.Reader (fun x ->
        task { handleResults x }
    )

    let options =
        {
            recordVideo = false
            dockerImage = Prelude.Configuration.varFromEnv "TRIK_STUDIO_IMAGE"
        }: Core.GradingOptions

    task {
        let mutable submissionId = 0
        for testCase in testCases do
            submissionId <- submissionId + 1
            let submission =
                {
                    id = submissionId
                    task = testCase.task
                    options = options
                    submission = testCase.submission 
                }: Core.SubmissionData
            tests.Add(submissionId, testCase)
            do! submissionChannel.Writer.WriteAsync(submission)
            log $"Sent submission[{submissionId}]"
        submissionChannel.Writer.Complete()
        let! _ = System.Threading.Tasks.Task.WhenAll [| worker; resultsRedirect; submissionsRedirect |]
        if tests.Count <> 0 then
            for test in tests do
                log $"No response for submission[{test.Key}]"
            fail ()
        return failed
    }

[<EntryPoint>]
let main _ =
    setupLogger ()
    let testCases = getTestCases <| Prelude.Configuration.varFromEnv "EXAMPLES_DIRECTORY"
    let client = createClient ()
    let failedTask = runTests testCases client
    let failed = failedTask.Result
    if failed then
        1
    else
        0
