module TestSys.Trik.GradingNode.Prelude.Configuration

open System

let varFromEnv name =
    match Option.ofObj <| Environment.GetEnvironmentVariable(name) with
    | Some value -> value
    | None -> failwith $"Required env var[{name}] not specified"
