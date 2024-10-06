module TestSys.Trik.GradingNode.Grpc.Streams

open System.Threading
open TestSys.Trik.GradingNode.Prelude

let tag = "Stream"
let logWarning msg = Logging.logWarning tag msg
let logError msg = Logging.logError tag msg
let logDebug msg = Logging.logDebug tag msg

let private (|ConnectionAbortedException|_|) (e: exn) =
    match e with
    | :? System.IO.IOException as e ->
        if e.InnerException :? Microsoft.AspNetCore.Connections.ConnectionAbortedException then
            Some ()
        else
            None
    | _ -> None


let redirectStreamToChannel
    (name: string)
    (token: CancellationToken)
    (stream: Grpc.Core.IAsyncStreamReader<'a>)
    (toCh: System.Threading.Channels.ChannelWriter<'b>)
    (f: 'a -> 'b) =
    task {
        try
            logDebug $"Start stream to channel redirection[{name}]"
            do! Async.SwitchToThreadPool()
            while! stream.MoveNext(token) do
                let v = f stream.Current
                do! toCh.WriteAsync v
            toCh.Complete()
            logDebug $"Stream to channel redirection[{name}] finished successfully"
            return Ok ()
        with
            | :? System.OperationCanceledException ->
                logDebug $"Stream to channel redirection[{name}] cancelled"
                toCh.Complete()
                return Ok ()
            | ConnectionAbortedException ->
                logWarning $"Stream to channel redirection[{name}] aborted"
                toCh.Complete()
                return Ok ()
            | e ->
                logError $"Stream to channel redirection[{name}] finished with error: {e}"
                toCh.Complete(e)
                return Error e
    }

let redirectChannelToStream
    (name: string)
    (token: CancellationToken)
    (fromCh: System.Threading.Channels.ChannelReader<'a>)
    (stream: Grpc.Core.IAsyncStreamWriter<'b>)
    (f: 'a -> 'b) =

    let closeStream () =
        task {
            match stream with
            | :? Grpc.Core.IClientStreamWriter<'b> as s ->
                do! s.CompleteAsync()
            | _ -> ()
        }

    task {
        try
            logDebug $"Start channel to stream redirection[{name}]"
            do! Async.SwitchToThreadPool()
            while! fromCh.WaitToReadAsync(token) do
                let! v = fromCh.ReadAsync(token)
                do! stream.WriteAsync(f v, token)
            do! closeStream ()
            logDebug $"Channel to stream redirection[{name}] finished successfully"
            return Ok ()
        with
            | :? System.OperationCanceledException ->
                do! closeStream ()
                logDebug $"Channel to stream redirection[{name}] cancelled"
                return Ok ()
            | ConnectionAbortedException ->
                do! closeStream ()
                logWarning $"Channel to stream redirection[{name}] aborted"
                return Ok ()
            | e ->
                do! closeStream ()
                logError $"Channel to stream redirection[{name}] finished with error: {e}"
                return Error e
    }

let attachWorkerToChannel
    (name: string)
    (token: CancellationToken)
    (fromCh: System.Threading.Channels.ChannelReader<'a>)
    (f: 'a -> System.Threading.Tasks.Task<unit>) =
    task {
        try
            do! Async.SwitchToNewThread()
            logDebug $"Start worker[{name}] at thread: {System.Environment.CurrentManagedThreadId}"
            while! fromCh.WaitToReadAsync(token) do
                match fromCh.TryRead() with
                | true, v -> do! f v
                | false, _ -> ()
            logDebug $"Worker[{name}] finished successfully"
            return Ok ()
        with
            | :? System.OperationCanceledException ->
                logDebug $"Worker[{name}] cancelled"
                return Ok ()
            | ConnectionAbortedException->
                logWarning $"Worker[{name}] aborted"
                return Ok ()
            | e ->
                logError $"Worker[{name}] finished with error: {e}"
                return Error e
    }

let waitToComplete (name: string) (fromCh: System.Threading.Channels.ChannelReader<'a>) =
    task {
        try
            logDebug $"Start wait for complete [{name}]"
            do! fromCh.Completion
            return Ok ()
        with
            | ConnectionAbortedException->
                logWarning $"Wait for complete [{name}] aborted"
                return Ok ()
            | e ->
                logDebug $"Wait for complete [{name}] finished with error: {e}"
                return Error e
    }
