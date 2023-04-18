#r "System.Security.Cryptography"
#r "nuget: CliWrap, 3.6.0"
#r "nuget: FSharp.Data, 6.1.1-beta"
#r "nuget: MSBuild.StructuredLogger, 2.1.790"

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading.Tasks
open CliWrap

// 0. Setup and helper functions
let isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
let sdkFolder = Path.Combine(__SOURCE_DIRECTORY__, ".sdks")
let fsharpFolder = Path.Combine(__SOURCE_DIRECTORY__, ".fsharp")
let repositoriesFolder = Path.Combine(__SOURCE_DIRECTORY__, ".repositories")

let limits =
    {|
        MaxSequentialRuns = 5
        MaxParallelRuns = 15
    |}

// Roll forward so we can call with using the dotnet 7.0.4xx or 8.0.1xx SDK.
Environment.SetEnvironmentVariable("RollForward", "Major")

let getFileHash filename =
    use sha256 = System.Security.Cryptography.SHA256.Create()
    use stream = File.OpenRead(filename)
    let hash = sha256.ComputeHash(stream)
    BitConverter.ToString(hash).Replace("-", "")
// TODO: get SDK path

Environment.SetEnvironmentVariable("SuppressNETCoreSdkPreviewMessage", "true")

let DotnetFscCompilerPath = Path.Combine(AppContext.BaseDirectory, "fsc.dll")

// TODO: Capture information about the state of the compiler

// 3. Clone various projects to test
if not (Directory.Exists repositoriesFolder) then
    Directory.CreateDirectory repositoriesFolder |> ignore

type ProjectInRepository =
    {
        /// Relative path to the project file.
        Path: string
        // Additional argument to pass to the initial build.
        AdditionalInitialBuildArguments: string list
    }

type RepositoryConfiguration =
    {
        GitUrl: string
        CommitSha: string
        RemoveGlobalJson: bool
        Init: Command list
        Projects: ProjectInRepository list
    }

    member this.RepositoryName = Uri(this.GitUrl).Segments |> Seq.last

type TypeCheckMode =
    | Sequential
    | Parallel

    member x.Flags =
        match x with
        | Sequential -> "--deterministic+"
        | Parallel -> "--deterministic+ --test:GraphBasedChecking"

    override x.ToString() =
        match x with
        | Sequential -> "sequential"
        | Parallel -> "parallel"

type BinaryHash = BinaryHash of file: string * hash: string

[<CustomEquality; NoComparison>]
type CompilationResult =
    {
        Attempt: int
        Duration: TimeSpan
        TypeCheckMode: TypeCheckMode
        BinaryHash: BinaryHash
    }

    override x.Equals other =
        match other with
        | :? CompilationResult as other ->
            let (BinaryHash(hash = xHash)) = x.BinaryHash
            let (BinaryHash(hash = otherHash)) = other.BinaryHash
            xHash = otherHash
        | _ -> false

    override x.GetHashCode() =
        let hash = HashCode()
        hash.Add(x.BinaryHash)
        hash.ToHashCode()

/// Aim to shortcut the compilations of a project when the result has been unstable for a single instance.
[<RequireQualifiedAccess>]
type HistoricCompilationResult<'TResult when 'TResult: equality> =
    | NeverRan
    | Stable of result: 'TResult * times: int
    | Unstable of initial: 'TResult * times: int * variant: 'TResult

type ProjectResult = ProjectResult of projectName: string * compilationResults: CompilationResult array
type RepositoryResult = RepositoryResult of repository: RepositoryConfiguration * projectResults: ProjectResult array

let repositories =
    [
        {
            GitUrl = "https://github.com/fsprojects/fantomas"
            CommitSha = "d3f2daa02eccf6fb0fbcd1bfeeaccfd252a67700"
            RemoveGlobalJson = true
            Init =
                [
                    // This will download the FCS files.
                    Cli.Wrap("dotnet").WithArguments("fsi ./build.fsx -p Init")
                    // Restore the nuget packages.
                    Cli.Wrap("dotnet").WithArguments("restore")
                    // Trigger the fslex/fsyacc build.
                    Cli.Wrap("dotnet").WithArguments("build ./src/Fantomas.FCS/Fantomas.FCS.fsproj")
                ]
            Projects =
                [
                    {
                        Path = "src/Fantomas.Core/Fantomas.Core.fsproj"
                        AdditionalInitialBuildArguments = []
                    }
                    {
                        Path = "src/Fantomas.Core.Tests/Fantomas.Core.Tests.fsproj"
                        AdditionalInitialBuildArguments = []
                    }
                ]
        }
        {
            GitUrl = "https://github.com/dotnet/fsharp"
            CommitSha = "fe4fda6e2a775c9e664af8949d1ecff608e4691b"
            RemoveGlobalJson = true
            Init = [ Cli.Wrap("dotnet").WithArguments("build FSharp.Compiler.Service.sln") ]
            Projects =
                [
                    {
                        Path = "src/FSharp.Core/FSharp.Core.fsproj"
                        AdditionalInitialBuildArguments = [ "/p:BUILDING_USING_DOTNET=true" ]
                    }
                    {
                        Path = "src/Compiler/FSharp.Compiler.Service.fsproj"
                        AdditionalInitialBuildArguments = [ "/p:BUILDING_USING_DOTNET=true" ]
                    }
                ]
        }
    ]

/// Create a text file with the F# compiler arguments scrapped from an binary log file.
let mkCompilerArgsFromBinLog file =
    let build = Microsoft.Build.Logging.StructuredLogger.BinaryLog.ReadBuild file

    let projectName =
        build.Children
        |> Seq.choose (
            function
            | :? Microsoft.Build.Logging.StructuredLogger.Project as p -> Some p.Name
            | _ -> None
        )
        |> Seq.distinct
        |> Seq.exactlyOne

    let message (fscTask: Microsoft.Build.Logging.StructuredLogger.FscTask) =
        fscTask.Children
        |> Seq.tryPick (
            function
            | :? Microsoft.Build.Logging.StructuredLogger.Message as m when m.Text.Contains "fsc" -> Some m.Text
            | _ -> None
        )

    let mutable args = None

    build.VisitAllChildren<Microsoft.Build.Logging.StructuredLogger.Task>(fun task ->
        match task with
        | :? Microsoft.Build.Logging.StructuredLogger.FscTask as fscTask ->
            match fscTask.Parent.Parent with
            | :? Microsoft.Build.Logging.StructuredLogger.Project as p when p.Name = projectName ->
                args <- message fscTask
            | _ -> ()
        | _ -> ()
    )

    match args with
    | None -> failwithf $"Could not read the fsc arguments from %s{file}"
    | Some args -> args

/// Build the project first the first time to extract the fsc argument list.
let initialBuild (repositoryFolder: DirectoryInfo) (project: ProjectInRepository) : Task =
    task {
        printfn $"Building %s{project.Path} to extract the fsc arguments."
        let projectFile = Path.Combine(repositoryFolder.FullName, project.Path)
        let binlogFile = Path.ChangeExtension(projectFile, ".binlog")
        let argsPath = Path.ChangeExtension(binlogFile, ".rsp")

        if not (File.Exists argsPath) then
            let! result =
                Cli
                    .Wrap("dotnet")
                    .WithWorkingDirectory(repositoryFolder.FullName)
                    .WithArguments(
                        [|
                            "build"
                            project.Path
                            "-c Release"
                            "--no-incremental"
                            $"-bl:\"%s{binlogFile}\""
                            yield! project.AdditionalInitialBuildArguments
                        |]
                        |> String.concat " "
                    )
                    .WithStandardOutputPipe(PipeTarget.ToDelegate(printfn "%s"))
                    .ExecuteAsync()
                    .Task

            if result.ExitCode <> 0 then
                printfn $"Could build {project.Path} to extract the fsc arguments."

            let fscArgs = mkCompilerArgsFromBinLog binlogFile

            let fscArgs =
                fscArgs.Split([| ' '; '\n' |])
                // Skip the dotnet.exe fsc.dll arguments.
                |> Array.skip 2
                |> String.concat "\n"

            File.WriteAllText(argsPath, fscArgs)

            return ()
    }
    :> Task

let build
    (repositoryFolder: DirectoryInfo)
    (idx: int)
    (typeCheckMode: TypeCheckMode)
    (project: ProjectInRepository)
    : Task<CompilationResult> =
    task {
        printfn $"Start building {project.Path} (%i{idx}) in %O{typeCheckMode} mode"

        let outputFolder =
            Path.Combine(
                repositoryFolder.FullName,
                ".results",
                Path.GetFileNameWithoutExtension(project.Path),
                $"%O{typeCheckMode}-%03i{idx}"
            )
            |> DirectoryInfo

        if not outputFolder.Exists then
            outputFolder.Create()

        let rspFile =
            Path.ChangeExtension(Path.Combine(repositoryFolder.FullName, project.Path), ".rsp")
            |> Path.GetFullPath
            |> FileInfo

        if not rspFile.Exists then
            failwithf $"Expected args file %s{rspFile.Name} to exist."

        let outputFilePath =
            let fscArgs = File.ReadAllLines rspFile.FullName
            let outputArg = fscArgs |> Array.find (fun arg -> arg.StartsWith "-o:")
            // Most likely a relative path
            let file = outputArg.Replace("-o:", "")
            Path.Combine(rspFile.Directory.FullName, file)

        let! result =
            Cli
                .Wrap("dotnet")
                .WithWorkingDirectory(rspFile.Directory.FullName)
                .WithArguments(
                    [|
                        $"\"%s{DotnetFscCompilerPath}\""
                        $"\"@%s{rspFile.Name}\""
                        typeCheckMode.Flags
                    |]
                    |> String.concat " "
                )
                .WithStandardOutputPipe(PipeTarget.ToDelegate(printfn "%s"))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(printfn "%s"))
                .ExecuteAsync()

        let binaryHash =
            let copiedBinaryFileName =
                Path.Combine(outputFolder.FullName, Path.GetFileName(outputFilePath))

            File.Copy(outputFilePath, copiedBinaryFileName, true)
            BinaryHash(copiedBinaryFileName, getFileHash outputFilePath)

        return
            {
                Attempt = idx
                Duration = result.RunTime
                TypeCheckMode = typeCheckMode
                BinaryHash = binaryHash
            }
    }

let testProject (repositoryFolder: DirectoryInfo) (project: ProjectInRepository) : Task<ProjectResult> =
    task {
        printfn $"Start testing project %s{project.Path}"

        let results =
            Array.init<CompilationResult option> (limits.MaxSequentialRuns + limits.MaxParallelRuns) (fun _ -> None)

        // Run the initial build to extract the fsc arguments.
        do! initialBuild repositoryFolder project

        let runs =
            [|
                yield!
                    [ 1 .. limits.MaxSequentialRuns ]
                    |> List.map (fun idx -> fun () -> build repositoryFolder idx Sequential project)
                yield!
                    [ 1 .. limits.MaxParallelRuns ]
                    |> List.map (fun idx -> fun () -> build repositoryFolder idx Parallel project)
            |]

        let mutable currentResult = HistoricCompilationResult.NeverRan

        for idx in 0 .. results.Length - 1 do
            try
                match currentResult with
                | HistoricCompilationResult.Unstable _ -> ()
                | HistoricCompilationResult.NeverRan ->
                    let! result = runs.[idx] ()
                    results.[idx] <- Some result
                    currentResult <- HistoricCompilationResult.Stable(result, 1)
                | HistoricCompilationResult.Stable(stableResult, times) ->
                    let! result = runs.[idx] ()
                    results.[idx] <- Some result

                    if result = stableResult then
                        currentResult <- HistoricCompilationResult.Stable(result, times + 1)
                    else
                        currentResult <- HistoricCompilationResult.Unstable(stableResult, times, result)
            with ex ->
                printfn $"Failed to compile %s{project.Path} (%i{idx}): %A{ex}"

        printfn $"End testing project %s{project.Path}"
        let results = Array.choose id results
        return ProjectResult(project.Path, results)
    }

let testRepository (repository: RepositoryConfiguration) : Task<RepositoryResult> =
    task {
        let repositoryFolder =
            DirectoryInfo(Path.Combine(repositoriesFolder, repository.RepositoryName))

        let git (arguments: string) =
            Cli
                .Wrap("git")
                .WithWorkingDirectory(repositoryFolder.FullName)
                .WithArguments(arguments)
                .WithStandardOutputPipe(PipeTarget.ToDelegate(printfn "%s"))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(printfn "%s"))
                .ExecuteAsync()
                .Task
            :> Task

        // Clone the project if missing
        if not repositoryFolder.Exists then
            repositoryFolder.Create()
            do! git "init"
            do! git $"remote add origin %s{repository.GitUrl}"
            do! git "fetch origin"
            do! git $"reset --hard %s{repository.CommitSha}"

        // do! git $"clean -xdf"
        printfn $"Preparing {repository.RepositoryName}"

        if repository.RemoveGlobalJson then
            let globalJson = FileInfo(Path.Combine(repositoryFolder.FullName, "global.json"))

            if globalJson.Exists then
                globalJson.Delete()

        // Run any initial commands the repository might need before we can compile the code.
        for cmd in repository.Init do
            do!
                cmd
                    .WithWorkingDirectory(repositoryFolder.FullName)
                    .WithStandardOutputPipe(PipeTarget.ToDelegate(printfn "%s"))
                    .ExecuteAsync()
                    .Task
                :> Task

        let resultsFolder =
            DirectoryInfo(Path.Combine(repositoryFolder.FullName, ".results"))
        // Always clean the results folder
        if resultsFolder.Exists then
            resultsFolder.Delete(true)

        resultsFolder.Create()

        let projectResults = Array.zeroCreate<ProjectResult> repository.Projects.Length

        for idx = 0 to repository.Projects.Length - 1 do
            let project = repository.Projects.[idx]
            let! result = testProject repositoryFolder project
            projectResults.[idx] <- result

        return RepositoryResult(repository, projectResults)
    }

let results =
    repositories
    |> List.map (fun repository ->
        // I'm ok with this running synchronously, as it's just a test.
        (testRepository repository).Result
    )

let allBinariesHaveTheSameHash =
    results
    |> List.forall (fun (RepositoryResult(projectResults = projectResults)) ->
        projectResults
        |> Array.forall (fun (ProjectResult(projectName, compilationResults)) ->
            let groups =
                compilationResults
                |> Array.groupBy (fun projectCompilationResult ->
                    let (BinaryHash(hash = hashValue)) = projectCompilationResult.BinaryHash
                    hashValue
                )

            if groups.Length <> 1 then
                let hashes = groups |> Seq.map fst |> String.concat ", "

                printfn $"File %s{projectName} has different hashes: %s{hashes}"
                false
            else
                true
        )
    )

if not allBinariesHaveTheSameHash then
    exit 1
