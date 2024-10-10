module [<AutoOpen>] TestSys.Trik.GradingNode.Grpc.Transformers

open TestSys.Trik.GradingNode
open TestSys.Trik.GradingNode.Grading.Core

type Proto.File with

    member this.ToInMemoryFile () =
        let bytes = this.Content.ToByteArray()
        {
            name = this.Name
            content = bytes
        }

    static member FromInMemoryFile (f: InMemoryFile) =
        let result = Proto.File()
        result.Name <- f.name
        result.Content <- Google.Protobuf.ByteString.CopyFrom(f.content)
        result

type Proto.Task with

    member this.ToGradingTask (): Task =
        let fields =
            this.Fields
            |> Seq.map _.ToInMemoryFile()
            |> List.ofSeq
        {
            fields = fields
        }

    static member FromGradingTask (t: Task) =
        let result = Proto.Task()
        result.Fields.Add (t.fields |> Seq.map Proto.File.FromInMemoryFile)
        result

type Proto.Options with

    member this.ToGradingOptions () =
        {
            dockerImage = this.DockerImage
            recordVideo = this.RecordVideo
        }

    static member FromGradingOptions (o: GradingOptions) =
        let result = Proto.Options()
        result.DockerImage <- o.dockerImage
        result.RecordVideo <- o.recordVideo
        result

type Proto.VisualLanguageSubmission with

    member this.ToSubmission () =
        VisualLanguageSubmission (this.File.ToInMemoryFile())

type Proto.PythonSubmission with

    member this.ToSubmission () =
        PythonSubmission (this.File.ToInMemoryFile())

type Proto.JavaScriptSubmission with

    member this.ToSubmission () =
        JavaScriptSubmission (this.File.ToInMemoryFile())

type Proto.Submission with

    member this.ToSubmissionData() =
        let submission =
            match this.SubmissionCase with
            | Proto.Submission.SubmissionOneofCase.VisualLanguageSubmission -> this.VisualLanguageSubmission.ToSubmission()
            | Proto.Submission.SubmissionOneofCase.PythonSubmission -> this.PythonSubmission.ToSubmission()
            | Proto.Submission.SubmissionOneofCase.JavascriptSubmission -> this.JavascriptSubmission.ToSubmission()
            | _ -> failwith "Unexpected value"
        {
            id = this.Id
            task = this.Task.ToGradingTask()
            options = this.Options.ToGradingOptions()
            submission = submission
        }

    static member FromSubmissionData (s: SubmissionData) =
        let result = Proto.Submission()

        result.Id <- s.id
        result.Task <- Proto.Task.FromGradingTask s.task
        result.Options <- Proto.Options.FromGradingOptions s.options

        match s.submission with
        | VisualLanguageSubmission file  ->
            let submission = Proto.VisualLanguageSubmission ()
            submission.File <- Proto.File.FromInMemoryFile file
            result.VisualLanguageSubmission <- submission
        | PythonSubmission file ->
            let submission = Proto.PythonSubmission ()
            submission.File <- Proto.File.FromInMemoryFile file
            result.PythonSubmission <- submission
        | JavaScriptSubmission file ->
            let submission = Proto.JavaScriptSubmission ()
            submission.File <- Proto.File.FromInMemoryFile file
            result.JavascriptSubmission <- submission

        result

type Proto.FieldResult with

    member this.ToGradingFieldResult () =
        let videoFile =
            Option.ofObj this.Video
            |> Option.map _.ToInMemoryFile()
        {
            name = this.Name
            verdictFile = this.Verdict.ToInMemoryFile()
            videoFile = videoFile
        }

    static member FromGradingFieldResult (r: FieldResult) =
        let result = Proto.FieldResult()
        result.Name <- r.name
        result.Verdict <- Proto.File.FromInMemoryFile r.verdictFile
        match r.videoFile with
        | None -> ()
        | Some value ->
            result.Video <- Proto.File.FromInMemoryFile value
        result

type Proto.ErrorResult with

    member this.ToGradingError () =
        match this.Kind with
        | 1 -> UnexpectedException <| Unchecked.defaultof<_>
        | 2 -> NonZeroExitCode -1
        | 3 -> MismatchedFiles
        | 4 -> InnerTimeoutExceed
        | 5 -> UnsupportedImageVersion ""
        | _ -> failwith "TODO"

    static member FromGradingError (e: GradingError) =
        let result = Proto.ErrorResult()
        let kind, description = 
            match e with
            | UnexpectedException exn ->
                1, $"Unexpected exception: {exn.Message}"
            | NonZeroExitCode i ->
                2, $"Unexpected non-zero exit code: {i}"
            | MismatchedFiles ->
                3, "Mismatched fields/results files"
            | InnerTimeoutExceed ->
                4, "Inner timeout exceed"
            | UnsupportedImageVersion _ ->
                5, "Unsupported image version"
        result.Kind <- kind
        result.Description <- description
        result

type Proto.OkResult with

    member this.ToFieldResults () =
        this.Results
        |> Seq.map _.ToGradingFieldResult()
        |> List.ofSeq

    static member FromFieldResults (results: FieldResult seq) =
        let result = Proto.OkResult()
        let results =
            results
            |> Seq.map Proto.FieldResult.FromGradingFieldResult
        result.Results.Add(results)
        result

type Proto.Result with

    member this.ToGradingResult () =
        let result = 
            match this.ResultCase with
            | Proto.Result.ResultOneofCase.Ok ->
                Ok <| this.Ok.ToFieldResults()
            | Proto.Result.ResultOneofCase.Error ->
                Error <| this.Error.ToGradingError()
            | _ -> failwith "U"
        {
            id = this.Id
            result = result
        }

    static member FromGradingResult (r: GradingResult) =
        let result = Proto.Result()
        result.Id <- r.id
        match r.result with
        | Ok res ->
            result.Ok <- Proto.OkResult.FromFieldResults res
        | Error err ->
            result.Error <- Proto.ErrorResult.FromGradingError err
        result