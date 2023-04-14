#r "System.Security.Cryptography"
#r "nuget: CliWrap, 3.6.0"
#r "nuget: FSharp.Data, 6.1.1-beta"

open System
open System.IO
open System.IO.Compression
open System.Runtime.InteropServices
open System.Threading.Tasks
open FSharp.Data
open CliWrap
open CliWrap.Buffered

// 0. Setup and helper functions
let isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
let sdkFolder = Path.Combine(__SOURCE_DIRECTORY__, ".sdks")
let fsharpFolder = Path.Combine(__SOURCE_DIRECTORY__, ".fsharp")
let repositoriesFolder = Path.Combine(__SOURCE_DIRECTORY__, ".repositories")

let limits =
    {|
        MaxSequentialRuns = 5
        MaxParallelRuns = 20
    |}

let getFileHash filename =
    use sha256 = System.Security.Cryptography.SHA256.Create()
    use stream = File.OpenRead(filename)
    let hash = sha256.ComputeHash(stream)
    BitConverter.ToString(hash).Replace("-", "")

// 1. Download dotnet 8 nightly
let dotnetSDKDownloadUrl =
    if isWindows then
        // execute code only on Windows
        "https://aka.ms/dotnet/8.0.1xx/daily/dotnet-sdk-win-x64.zip"
    else
        failwith "Unsupported OS, feel free to add your own OS"

let currentVersion =
    let response = Http.Request(dotnetSDKDownloadUrl, httpMethod = "HEAD")

    Uri(response.ResponseUrl).Segments
    |> Seq.last
    |> Path.GetFileNameWithoutExtension

let currentVersionPath = Path.Combine(sdkFolder, currentVersion)

if Directory.Exists currentVersionPath then
    printfn $"SDK version %s{currentVersion} already exists"
else
    let extractDir = Directory.CreateDirectory(currentVersionPath)
    printfn $"About to download SDK version %s{currentVersion}"
    let response = Http.Request dotnetSDKDownloadUrl

    match response.Body with
    | Text txt -> failwithf $"Unexpected text response: %s{txt}"
    | Binary bytes ->
        if isWindows then
            // create a memory stream from the zip bytes
            use zipStream = new MemoryStream(bytes)
            use zipArchive = new ZipArchive(zipStream)

            for entry in zipArchive.Entries do
                let outputPath = Path.Combine(extractDir.FullName, entry.FullName)

                if not (Directory.Exists(Path.GetDirectoryName(outputPath))) then
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)) |> ignore

                if entry.Length = 0L then
                    // Create an empty directory
                    Directory.CreateDirectory(outputPath) |> ignore
                else
                    use entryStream = entry.Open()
                    use outputStream = File.Create(outputPath)
                    entryStream.CopyTo(outputStream)

let dotnetExe =
    Path.Combine(currentVersionPath, (if isWindows then "dotnet.exe" else "dotnet"))

// 2. Compile the latest F# compiler, this is a temporary step as we hope it is in the SDK soon.
if Directory.Exists fsharpFolder then
    printfn "Update the F# compiler to the latest commit"

    Cli
        .Wrap("git")
        .WithWorkingDirectory(fsharpFolder)
        .WithArguments("checkout .")
        .WithStandardOutputPipe(PipeTarget.ToDelegate(printfn "%s"))
        .ExecuteAsync()
        .Task.Wait()

    Cli
        .Wrap("git")
        .WithWorkingDirectory(fsharpFolder)
        .WithArguments("pull")
        .WithStandardOutputPipe(PipeTarget.ToDelegate(printfn "%s"))
        .ExecuteAsync()
        .Task.Wait()
else
    printfn "Cloning the F# compiler"

    Cli
        .Wrap("git")
        .WithWorkingDirectory(__SOURCE_DIRECTORY__)
        .WithArguments($"clone https://github.com/dotnet/fsharp --single-branch {fsharpFolder}")
        .WithStandardOutputPipe(PipeTarget.ToDelegate(printfn "%s"))
        .ExecuteAsync()
        .Task.Wait()

// Compile the F# compiler
// This script assumes the dotnet SDK the compiler needs in present
// Build the FSC first
Cli
    .Wrap("dotnet")
    .WithWorkingDirectory(fsharpFolder)
    .WithArguments("build -c Release FSharp.Compiler.Service.sln")
    .WithStandardOutputPipe(PipeTarget.ToDelegate(printfn "%s"))
    .ExecuteAsync()
    .Task.Wait()

// Build the fsc.exe, note that this is current Windows only
Cli
    .Wrap("dotnet")
    .WithWorkingDirectory(fsharpFolder)
    .WithArguments(
        [|
            "build"
            "./src/fsc/fscProject/fsc.fsproj"
            "-c Release"
            "-f net7.0"
            "-r win-x64"
            "-p:PublishReadyToRun=true"
            "--no-self-contained"
            // No Proto build
            "/p:BUILDING_USING_DOTNET=true"
            "/p:ErrorOnDuplicatePublishOutputFiles=False"
            // Roll forward so we can call with using the dotnet 8 SDK.
            "/p:RollForward=Major"
        |]
        |> String.concat " "
    )
    .WithStandardOutputPipe(PipeTarget.ToDelegate(printfn "%s"))
    .ExecuteAsync()
    .Task.Wait()

// Capture information about the state of the compiler
let gitInfo (args: string) =
    Cli
        .Wrap("git")
        .WithWorkingDirectory(fsharpFolder)
        .WithArguments(args)
        .ExecuteBufferedAsync()
        .Task.Result.StandardOutput.Trim()

let fscCommit = gitInfo "rev-parse HEAD"
let fscCommitDate = gitInfo "log -1 --format=%ai" |> DateTime.Parse
let fscMessage = gitInfo "log -1 --format=%s"

let DotnetFscCompilerPath =
    Path.Combine(fsharpFolder, "artifacts", "bin", "fsc", "Release", "net7.0", "win-x64", "fsc.dll")

// 3. Clone various projects to test
if not (Directory.Exists repositoriesFolder) then
    Directory.CreateDirectory repositoriesFolder |> ignore

type ProjectInRepository =
    {
        /// Relative path to the project file
        Path: string
        /// Names of the binaries we want to include in our determinism test
        OutputBinaries: string list
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

type CompilationResult =
    {
        Attempt: int
        Duration: TimeSpan
        TypeCheckMode: TypeCheckMode
        BinaryHashes: BinaryHash list
    }

type ProjectResult = ProjectResult of projectName: string * compilationResults: CompilationResult array
type RepositoryResult = RepositoryResult of repository: RepositoryConfiguration * projectResults: ProjectResult array

let repositories =
    [
        {
            GitUrl = "https://github.com/fsprojects/fantomas"
            CommitSha = "1655281cea0d5e4680d953b9f160735b0d2a6f7f"
            RemoveGlobalJson = true
            Init =
                [
                    // This will download the FCS files as well.
                    Cli.Wrap(dotnetExe).WithArguments("restore")
                    // Trigger the fslex/fsyacc build.
                    Cli
                        .Wrap(dotnetExe)
                        .WithArguments("build ./src/Fantomas.FCS/Fantomas.FCS.fsproj")
                ]
            Projects =
                [
                    {
                        Path = "src/Fantomas/Fantomas.fsproj"
                        OutputBinaries =
                            [
                                "Fantomas.Client.dll"
                                "Fantomas.FCS.dll"
                                "Fantomas.Core.dll"
                                "Fantomas.dll"
                            ]
                    }
                ]
        }
    ]

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

        let! result =
            Cli
                .Wrap(dotnetExe)
                .WithWorkingDirectory(repositoryFolder.FullName)
                .WithArguments(
                    [|
                        "build"
                        project.Path
                        "-c Release"
                        $"-o %s{outputFolder}"
                        "--no-incremental"
                        $"/p:DotnetFscCompilerPath=\"%s{DotnetFscCompilerPath}\""
                        $"/p:OtherFlags=\"%s{typeCheckMode.Flags}\""
                    |]
                    |> String.concat " "
                )
                .WithStandardOutputPipe(PipeTarget.ToDelegate(printfn "%s"))
                .ExecuteAsync()

        let binaries =
            project.OutputBinaries
            |> List.map (fun binaryFileName ->
                let binaryPath = Path.Combine(outputFolder, binaryFileName)

                if not (File.Exists binaryPath) then
                    failwithf $"Binary %s{binaryPath} not found after compiling %s{project.Path}"

                BinaryHash(binaryFileName, getFileHash binaryPath)
            )

        return
            {
                Attempt = idx
                Duration = result.RunTime
                TypeCheckMode = typeCheckMode
                BinaryHashes = binaries
            }
    }

let testProject (repositoryFolder: DirectoryInfo) (project: ProjectInRepository) : Task<ProjectResult> =
    task {
        printfn $"Start testing project %s{project.Path}"

        let results =
            Array.zeroCreate<CompilationResult> (limits.MaxSequentialRuns + limits.MaxParallelRuns)

        let runs =
            [|
                yield!
                    [ 1 .. limits.MaxSequentialRuns ]
                    |> List.map (fun idx -> fun () -> build repositoryFolder idx Sequential project)
                yield!
                    [ 1 .. limits.MaxParallelRuns ]
                    |> List.map (fun idx -> fun () -> build repositoryFolder idx Parallel project)
            |]

        for idx in 0 .. results.Length - 1 do
            let! result = runs.[idx] ()
            results.[idx] <- result

        printfn $"End testing project %s{project.Path}"

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
        do! git $"clean -xdf"

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
    |> List.forall (fun (RepositoryResult(projectResults = results)) ->
        results
        |> Array.forall (fun (ProjectResult(compilationResults = results)) ->
            results
            |> Array.forall (fun result ->
                let grouped =
                    result.BinaryHashes
                    |> List.groupBy (fun (BinaryHash(file = file)) -> file)
                    |> List.map (fun (file, hashes) ->
                        file, (hashes |> List.map (fun (BinaryHash(hash = hash)) -> hash) |> List.distinct)
                    )

                grouped
                |> List.forall (fun (file, hashes) ->
                    if hashes.Length <> 1 then
                        let hashes = String.concat ", " hashes
                        printfn $"File %s{file} has different hashes: %s{hashes}"
                        false
                    else
                        true
                )
            )
        )
    )

if not allBinariesHaveTheSameHash then
    exit 1
