module TestSys.Trik.GradingNode.Grpc.Implementation

open System.Threading
open TestSys.Trik.GradingNode
open TestSys.Trik.GradingNode.Grading.Core
open TestSys.Trik.GradingNode.Grpc
open TestSys.Trik.GradingNode.Prelude

let tag = "Node"
let logError msg = Logging.logError tag msg
let logDebug msg = Logging.logDebug tag msg
let logInfo msg = Logging.logInfo tag msg
let logWarning msg = Logging.logInfo tag msg

type GradingNodeService() =

    inherit Proto.GradingNode.GradingNodeBase()

    let mutable connected = false
    let workerThreadsCount = System.Diagnostics.Process.GetCurrentProcess().Threads.Count - 1
    let submissionChannel = System.Threading.Channels.Channel.CreateUnbounded<SubmissionData>()
    let resultChannel = System.Threading.Channels.Channel.CreateUnbounded<GradingResult>()

    let options =
        let fsOptions =
            {
                mountedDirectory = Configuration.varFromEnv "MOUNTED_DIRECTORY"
                hostDirectory = Configuration.varFromEnv "HOST_DIRECTORY"
            }
        {
            fsOptions = fsOptions
            innerTimeout = int <| Configuration.varFromEnv "INNER_TIMEOUT_SECONDS"
        }


    let startWorkerThread name token =

        Streams.attachWorkerToChannel name token submissionChannel.Reader (fun x ->
            task {
                let grader = new Grader(options, x)
                try
                    let! result = grader.Grade(token)
                    do! resultChannel.Writer.WriteAsync result
                finally
                    (grader :> System.IDisposable).Dispose()
            }
        )

    override this.Grade(requestStream, responseStream, _) =

        if connected then
            logWarning "Connection denied"
            System.Threading.Tasks.Task.CompletedTask
        else

        connected <- true
        logInfo "Connection established"

        let tokenSource = new CancellationTokenSource()
        let token = tokenSource.Token

        let workerTasks =
            Array.init workerThreadsCount (fun i -> startWorkerThread $"Worker-{i}" token)

        let resultsRedirect =
            Streams.redirectChannelToStream
                "ResultsRedirect"
                token
                resultChannel.Reader
                responseStream
                Proto.Result.FromGradingResult

        let submissionsRedirect =
            Streams.redirectStreamToChannel
                "SubmissionsRedirect"
                token
                requestStream
                submissionChannel.Writer
                _.ToSubmissionData()

        let closeChannel: Tasks.Task<Result<unit,exn>> =
            task {
                do! submissionChannel.Reader.Completion
                let! _ = System.Threading.Tasks.Task.WhenAll(workerTasks)
                resultChannel.Writer.Complete()
                return Ok ()
            }

        let tasks = Array.append workerTasks [| resultsRedirect; submissionsRedirect; closeChannel |]

        task {
            let waitingFor = System.Collections.Generic.HashSet<_>(tasks)
            let mutable hasErrors = false
            while waitingFor.Count > 0 do
                let! completedTask = System.Threading.Tasks.Task.WhenAny(waitingFor)
                waitingFor.Remove(completedTask) |> ignore
                let! result = completedTask
                match result with
                | Ok () -> ()
                | Error _ ->
                    hasErrors <- true
                    logDebug "Start cancellation"
                    tokenSource.Cancel()
            connected <- false
            if hasErrors then
                logError "Connection finished with errors"
            else
                logInfo "Connection finished successfully"
        }
