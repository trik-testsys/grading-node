module TestSys.Trik.GradingNode.Prelude.Logging

open Serilog.Events

let private log lvl (msg: string) = Serilog.Log.Logger.Write(lvl, msg)


let logError scope msg = log LogEventLevel.Error $"[{scope}] {msg}"
let logWarning scope msg = log LogEventLevel.Warning $"[{scope}] {msg}"
let logInfo scope msg = log LogEventLevel.Information $"[{scope}] {msg}"
let logDebug scope msg = log LogEventLevel.Debug $"[{scope}] {msg}"
