#r "nuget: CliWrap, 3.6.0"
#r "nuget: FSharp.Data, 6.1.1-beta"

open System
open System.Threading.Tasks
open FSharp.Data
open CliWrap

let version =
    Http
        .Request("https://aka.ms/dotnet/7.0.4xx/daily/dotnet-sdk-win-x64.zip", httpMethod = "HEAD")
        .ResponseUrl.Split('/', StringSplitOptions.RemoveEmptyEntries)
    |> Array.tryFind (fun x -> Char.IsDigit x.[0])

match version with
| None ->
    failwith "Could not find version"
    Task.CompletedTask
| Some version ->
    printfn "Version: %s" version
    
    let run file (args: string) =
        Cli
            .Wrap(file)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync()
            .Task

    let dotnet = run "dotnet"
    let git = run "git"

    task {
        let! _ = dotnet $"new globaljson --sdk-version {version} --force"
        let! changes = git "diff --exit-code"

        if changes.ExitCode = 0 then
            printfn "No changes"
            return ()
        else
            let! _ = git "add global.json"
            let! _ = git $"commit -m \"Update global.json to {version}\""
            let! _ = git "push"
            return ()
    }
    :> Task
|> Task.WaitAll
