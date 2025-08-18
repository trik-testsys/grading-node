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

let private (|ConnectionAbortedException|_|) (e: exn) =
    match e with
    | :? System.IO.IOException as e ->
        if e.InnerException :? Microsoft.AspNetCore.Connections.ConnectionAbortedException then
            Some ()
        else
            None
    | _ -> None

type Worker (workerId: int, graderOptions: GraderOptions, inputChannel: Channels.ChannelReader<SubmissionData * Channels.ChannelWriter<GradingResult>>, token) =

    let mutable isBusy = false

    let grade data (channel: Channels.ChannelWriter<GradingResult>) =
        task {
            let grader = new DockerGrader(graderOptions,data)
            try
                try
                    isBusy <- true
                    let! result = grader.Grade(token)
                    do! channel.WriteAsync result
                with e ->
                    do! channel.WriteAsync { 
                        id = data.id 
                        result = Error <| UnexpectedException e 
                    }
            finally
                isBusy <- false
                (grader :> System.IDisposable).Dispose()
        }

    let startGrading () = 
        task {
            do! Async.SwitchToNewThread()
            logInfo $"Start new worker[{workerId}]"
            while! inputChannel.WaitToReadAsync(token) do
                let! (data, resultChannel) = inputChannel.ReadAsync(token)
                do! grade data resultChannel
        }

    let mutable currentTask = startGrading ()

    member this.WorkerId = workerId
    member this.IsBusy = isBusy
    member this.IsAlive = not currentTask.IsCompleted
    member this.GradingTask = currentTask
    member this.RestartGrading () = currentTask <- startGrading ()


type WorkerPool(threadsCount: int, graderOptions) =
 
    let tokenSource = new CancellationTokenSource()
    let token = tokenSource.Token

    let inputChannel = Channels.Channel.CreateUnbounded<SubmissionData * Channels.ChannelWriter<GradingResult>>()


    let workers = Array.init threadsCount (fun i -> Worker(i, graderOptions, inputChannel.Reader, token))

    let watchWorkers = 
        task {
            do! Async.SwitchToNewThread()
            do! Async.Sleep(5000)
            for worker in workers do
                if not worker.IsAlive then
                    match worker.GradingTask.Status with
                    | Tasks.TaskStatus.Faulted -> logError $"Worker[{worker.WorkerId}] faulted:\n{worker.GradingTask.Exception}"
                    | Tasks.TaskStatus.Canceled -> logError $"Worker[{worker.WorkerId}] canceled"
                    | _ -> logError $"Worker[{worker.WorkerId}] in unexpected state:\n{worker.GradingTask.Status}"
                    worker.RestartGrading()
        }

    member this.Grade (data: SubmissionData) =
        task {
            let resultChannel = Channels.Channel.CreateUnbounded<GradingResult>()
            do! inputChannel.Writer.WriteAsync( (data, resultChannel.Writer) )
            return! resultChannel.Reader.ReadAsync()
        }

    member this.BusyCount = 
        workers
        |> Array.filter (fun w -> w.IsBusy)
        |> Array.length

    member this.QueuedCount =
        inputChannel.Reader.Count

type GradingNodeService() =

    inherit Proto.GradingNode.GradingNodeBase()

    static let rnd = new System.Random(42)
    static let workerThreadsCount = int <| Configuration.varFromEnv "WORKERS_COUNT"

    static let options =
        let fsOptions =
            {
                mountedDirectory = Configuration.varFromEnv "MOUNTED_DIRECTORY"
                hostDirectory = Configuration.varFromEnv "HOST_DIRECTORY"
            }
        {
            fsOptions = fsOptions
            innerTimeout = int <| Configuration.varFromEnv "INNER_TIMEOUT_SECONDS"
            nodeId = int <| Configuration.varFromEnv "NODE_ID"
        }


    static let workerPool = WorkerPool(workerThreadsCount, options)
    
    override this.Grade(request, _) =

        let requestId = rnd.NextInt64(1_000_000_000)
        task {
            try
                logInfo $"Start proceeding request[{requestId}]"
                let submissionData = request.ToSubmissionData()
                let! gradingResult = workerPool.Grade(submissionData)
                return Proto.Result.FromGradingResult(gradingResult)
            with
                | ConnectionAbortedException as e->
                    logError $"Grade request[{requestId}] aborted"
                    return Proto.Result.FromGradingResult({ id = request.Id;  result = Error <| UnexpectedException e})
                | e ->
                    logError $"Grade request[{requestId}] finished with errror:\n{e}"
                    return Proto.Result.FromGradingResult({ id = request.Id;  result = Error <| UnexpectedException e})
        }

        override this.GetStatus(_,_) =
            task {
                let response = TestSys.Trik.GradingNode.Proto.Status()
                response.Capacity <- workerThreadsCount
                response.Id <- options.nodeId
                response.Queued <- workerPool.QueuedCount
                return response
            }
