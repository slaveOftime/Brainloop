#r "nuget: Fun.Build"

open System
open System.IO
open System.IO.Compression
open Fun.Result
open Fun.Build

let (</>) x y = Path.Combine(x, y)

let publishDir = __SOURCE_DIRECTORY__ </> "publish"

let options = {|
    platform = CmdArg.Create(longName = "--platform", values = [ "win-x64"; "linux-x64"; "linux-arm64"; "osx-x64"; "osx-arm64" ])
    run = CmdArg.Create(longName = "--run", shortName = "-r")
    docker = CmdArg.Create(longName = "--docker")
    zip = CmdArg.Create(longName = "--zip")
|}


let stage_checkEnvs = stage "check-env" { run "dotnet --version" }


pipeline "dev" {
    description "Setup local env for dev"
    whenCmdArg options.platform
    stage_checkEnvs
    stage "build" {
        run "dotnet build"
        run (fun ctx -> asyncResult {
            let platform = ctx.GetCmdArg(options.platform)
            let nativesDir = __SOURCE_DIRECTORY__ </> "Brainloop" </> "runtimes" </> platform </> "native"
            let publishDir = __SOURCE_DIRECTORY__ </> "Brainloop" </> "bin" </> "Debug" </> "net9.0"
            for file in Directory.GetFiles(nativesDir) do
                File.Copy(file, Path.Combine(publishDir </> Path.GetFileName(file)), true)
        })
    }
    stage "run" {
        whenCmdArg options.run
        workingDir "Brainloop"
        run "dotnet run --no-restore"
    }
    runIfOnlySpecified
}


pipeline "publish" {
    description "Publish single executable"
    whenAny {
        cmdArg options.platform
        cmdArg options.docker
    }
    whenAny {
        when' true
        cmdArg options.zip
    }
    stage_checkEnvs
    stage "bundle" {
        workingDir "Brainloop"
        run (fun ctx -> asyncResult {
            let platform =
                match ctx.TryGetCmdArg(options.platform) with
                | Some x -> x
                | None ->
                    match Environment.OSVersion.Platform with
                    | PlatformID.Win32NT -> "win-x64"
                    | PlatformID.Unix when Environment.Is64BitOperatingSystem -> "linux-x64"
                    | PlatformID.Unix when Environment.Is64BitProcess -> "linux-arm64"
                    | PlatformID.MacOSX when Environment.Is64BitOperatingSystem -> "osx-x64"
                    | PlatformID.MacOSX when Environment.Is64BitProcess -> "osx-arm64"
                    | _ -> failwith $"Unsupported platform: {Environment.OSVersion.Platform}"

            let isDocker = ctx.TryGetCmdArg(options.docker) |> Option.isSome
            let nativesDir = __SOURCE_DIRECTORY__ </> "Brainloop" </> "runtimes" </> platform </> "native"
            let publishDir = __SOURCE_DIRECTORY__ </> publishDir </> (if isDocker then "docker" else platform)

            if Directory.Exists publishDir then Directory.Delete(publishDir, true)

            if isDocker then
                do!
                    ctx.RunCommand
                        $"""dotnet publish -c Release /p:SelfContained=false /p:PublishReadyToRun=true /p:PublishTrimmed=false -o {publishDir}"""
            else
                do!
                    ctx.RunCommand
                        $"""dotnet publish -c Release -r {platform} /p:PublishSingleFile=true /p:PublishReadyToRun=false /p:PublishTrimmed=true -o {publishDir}"""

            for file in Directory.GetFiles(nativesDir) do
                File.Copy(file, publishDir </> Path.GetFileName(file), true)

            Directory.Delete(publishDir </> "wwwroot" </> "_content" </> "BlazorMonaco" </> "lib" </> "monaco-editor" </> "min-maps", true)

            if ctx.TryGetCmdArg options.zip |> Option.isSome then
                let changelog = Changelog.GetLastVersion(__SOURCE_DIRECTORY__ </> "Brainloop")
                ZipFile.CreateFromDirectory(
                    publishDir,
                    publishDir </> ".." </> "brainloop-" + Path.GetFileName publishDir + "-" + changelog.Value.Version + ".zip"
                )
        })
    }
    runIfOnlySpecified
}

pipeline "publish-all" {
    description "Publish all platforms"
    stage "bundles" {
        run "dotnet fsi ./build.fsx -- -p publish --platform win-x64 --zip"
        run "dotnet fsi ./build.fsx -- -p publish --platform linux-x64 --zip"
        run "dotnet fsi ./build.fsx -- -p publish --platform linux-arm64 --zip"
        run "dotnet fsi ./build.fsx -- -p publish --platform osx-x64 --zip"
        run "dotnet fsi ./build.fsx -- -p publish --platform osx-arm64 --zip"
    }
    runIfOnlySpecified
}


tryPrintPipelineCommandHelp ()
