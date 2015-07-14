﻿
[<AutoOpen>]
/// Contains helper functions which allow to interact with the F# Interactive.
module Fake.FSIHelper

open System
open System.IO
open System.Diagnostics
open System.Threading

let private FSIPath = @".\tools\FSharp\;.\lib\FSharp\;[ProgramFilesX86]\Microsoft SDKs\F#\4.0\Framework\v4.0;[ProgramFilesX86]\Microsoft SDKs\F#\3.1\Framework\v4.0;[ProgramFilesX86]\Microsoft SDKs\F#\3.0\Framework\v4.0;[ProgramFiles]\Microsoft F#\v4.0\;[ProgramFilesX86]\Microsoft F#\v4.0\;[ProgramFiles]\FSharp-2.0.0.0\bin\;[ProgramFilesX86]\FSharp-2.0.0.0\bin\;[ProgramFiles]\FSharp-1.9.9.9\bin\;[ProgramFilesX86]\FSharp-1.9.9.9\bin\"

let createDirectiveRegex id = 
    Text.RegularExpressions.Regex(
        "^\s*#" + id + "\s*(@\"|\"\"\"|\")(?<path>.+?)(\"\"\"|\")", 
        System.Text.RegularExpressions.RegexOptions.Compiled ||| 
        System.Text.RegularExpressions.RegexOptions.Multiline)
let loadRegex = createDirectiveRegex "load"
let rAssemblyRegex = createDirectiveRegex "r"
let searchPathRegex = createDirectiveRegex "I"

let private extractDirectives (regex : System.Text.RegularExpressions.Regex) scriptContents = 
    regex.Matches(scriptContents)
    |> Seq.cast<Text.RegularExpressions.Match>
    |> Seq.map(fun m ->
        (m.Groups.Item("path").Value)
    )
let rec getAllScripts scriptPath : seq<string * string> = 
    let scriptPath = 
        if Path.IsPathRooted scriptPath then
            scriptPath
        else
            Path.Combine(Directory.GetCurrentDirectory(), scriptPath)
    let scriptContents = File.ReadAllText(scriptPath)
    let loadedContents = 
        extractDirectives loadRegex scriptContents
        |> Seq.collect(fun path -> 
            let path =
                if Path.IsPathRooted path then
                    path
                else
                    Path.Combine(Path.GetDirectoryName(scriptPath), path)
            getAllScripts path
        )
    Seq.concat [List.toSeq [scriptPath, scriptContents]; loadedContents]

let getAllScriptContents (pathsAndContents : seq<string * string>) = 
    pathsAndContents |> Seq.map(snd)
let getIncludedAssembly scriptContents = extractDirectives rAssemblyRegex scriptContents
let getSearchPaths scriptContents = extractDirectives searchPathRegex scriptContents

let getScriptHash pathsAndContents =
    let fullContents = getAllScriptContents pathsAndContents |> String.concat("\n")
    let hasher = HashLib.HashFactory.Checksum.CreateCRC32a()
    hasher.ComputeString(fullContents).ToString()
/// The path to the F# Interactive tool.
let fsiPath =
    let ev = environVar "FSI"
    if not (isNullOrEmpty ev) then ev else
    if isUnix then
        let paths = appSettings "FSIPath" FSIPath
        // The standard name on *nix is "fsharpi"
        match tryFindFile paths "fsharpi" with
        | Some file -> file
        | None -> 
        // The early F# 2.0 name on *nix was "fsi"
        match tryFindFile paths "fsi" with
        | Some file -> file
        | None -> "fsharpi"
    else
        let dir = Path.GetDirectoryName fullAssemblyPath
        let fi = fileInfo (Path.Combine(dir, "fsi.exe"))
        if fi.Exists then fi.FullName else
        findPath "FSIPath" FSIPath "fsi.exe"

type FsiArgs =
    FsiArgs of string list * string * string list with
    static member parse (args:string array) =
        //Find first arg that does not start with - (as these are fsi options that precede the fsx).
        match args |> Array.tryFindIndex (fun arg -> arg.StartsWith("-") = false) with
        | Some(i) ->
            let fsxPath = args.[i]
            if fsxPath.EndsWith(".fsx", StringComparison.InvariantCultureIgnoreCase) then
                let fsiOpts = if i > 0 then args.[0..i-1] else [||]
                let scriptArgs = if args.Length > (i+1) then args.[i+1..] else [||]
                Choice1Of2(FsiArgs(fsiOpts |> List.ofArray, fsxPath, scriptArgs |> List.ofArray))
            else Choice2Of2(sprintf "Expected argument %s to be the build script path, but it does not have the .fsx extension." fsxPath) 
        | None -> Choice2Of2("Unable to locate the build script path.") 
    
let private FsiStartInfo workingDirectory (FsiArgs(fsiOptions, scriptPath, scriptArgs)) environmentVars =
    (fun (info: ProcessStartInfo) ->
        info.FileName <- fsiPath
        info.Arguments <- String.concat " " (fsiOptions @ [scriptPath] @ scriptArgs)
        info.WorkingDirectory <- workingDirectory
        let setVar k v =
            info.EnvironmentVariables.[k] <- v
        for (k, v) in environmentVars do
            setVar k v
        setVar "MSBuild"  msBuildExe
        setVar "GIT" Git.CommandHelper.gitPath
        setVar "FSI" fsiPath)

/// Creates a ProcessStartInfo which is configured to the F# Interactive.
let fsiStartInfo script workingDirectory args info =
    FsiStartInfo workingDirectory (FsiArgs([], script, [])) args info

/// Run the given buildscript with fsi.exe
let executeFSI workingDirectory script args =
    let (result, messages) =
        ExecProcessRedirected
            (fsiStartInfo script workingDirectory args)
            TimeSpan.MaxValue
    Thread.Sleep 1000
    (result, messages)

/// Run the given build script with fsi.exe and allows for extra arguments to FSI.
let executeFSIWithArgs workingDirectory script extraFsiArgs args =
    let result = ExecProcess (FsiStartInfo workingDirectory (FsiArgs(extraFsiArgs, script, [])) args) TimeSpan.MaxValue
    Thread.Sleep 1000
    result = 0

/// Run the given build script with fsi.exe and allows for extra arguments to the script. Returns output.
let executeFSIWithScriptArgsAndReturnMessages script (scriptArgs: string[]) =
    let (result, messages) =
        ExecProcessRedirected (fun si ->
            FsiStartInfo "" (FsiArgs([], script, scriptArgs |> List.ofArray)) [] si)
            TimeSpan.MaxValue
    Thread.Sleep 1000
    (result, messages)

open Microsoft.FSharp.Compiler.Interactive.Shell

type private AssemblySource = 
| GAC
| Disk

let hashRegex = Text.RegularExpressions.Regex("(?<script>.+)_(?<hash>[a-zA-Z0-9]+\.dll$)", System.Text.RegularExpressions.RegexOptions.Compiled)
/// Run the given FAKE script with fsi.exe at the given working directory. Provides full access to Fsi options and args. Redirect output and error messages.
let internal runFAKEScriptWithFsiArgsAndRedirectMessages printDetails (FsiArgs(fsiOptions, scriptPath, scriptArgs)) args onErrMsg onOutMsg useCache cleanCache =
    if printDetails then traceFAKE "Running Buildscript: %s" scriptPath

    // Add arguments to the Environment
    for (k,v) in args do
      Environment.SetEnvironmentVariable(k, v, EnvironmentVariableTarget.Process)

    // Create an env var that only contains the build script args part from the --fsiargs (or "").
    Environment.SetEnvironmentVariable("fsiargs-buildscriptargs", String.Join(" ", scriptArgs))

    let fsiConfig = FsiEvaluationSession.GetDefaultConfiguration()

    let commonOptions =
        [ "fsi.exe"; "--noninteractive" ] @ fsiOptions
        |> List.toArray

    let sbOut = Text.StringBuilder()
    let sbErr = Text.StringBuilder()
    let handleMessages() =
        let handleMessagesFrom (sb:Text.StringBuilder) onMsg =
            let s = sb.ToString()
            if not <| String.IsNullOrEmpty s
                then onMsg s
        handleMessagesFrom sbOut onOutMsg
        handleMessagesFrom sbErr onErrMsg
    let handleException (ex : Exception) = 
        onErrMsg (ex.ToString())

    use outStream = new StringWriter(sbOut)
    use errStream = new StringWriter(sbErr)
    use stdin = new StreamReader(Stream.Null)

    let allScriptContents = getAllScripts scriptPath
    let scriptHash = lazy (getScriptHash allScriptContents)
    //TODO this is only calculating the hash for the input file, not anything #load-ed
    
    let scriptFileName = lazy(Path.GetFileName(scriptPath))
    let hashPath = lazy("./.fake/" + scriptFileName.Value + "_" + scriptHash.Value)
    let assemblyPath = lazy(hashPath.Value + ".dll")
    let assemblyRefPath = lazy(hashPath.Value + "_references.txt")
    let cacheValid = lazy (
        System.IO.File.Exists(assemblyPath.Value) &&
        System.IO.File.Exists(assemblyRefPath.Value))

    let getScriptAndHash fileName =
        let matched = hashRegex.Match(fileName)
        matched.Groups.Item("script").Value, matched.Groups.Item("hash").Value

    if useCache && cacheValid.Value then
        
        trace ("Using cache")
        let noExtension = Path.GetFileNameWithoutExtension(scriptFileName.Value)
        let fullName = 
            sprintf "<StartupCode$FSI_0001>.$FSI_0001_%s%s$%s" 
                (noExtension.Substring(0, 1).ToUpper())
                (noExtension.Substring(1))
                (Path.GetExtension(scriptFileName.Value).Substring(1))

        for loc in File.ReadAllLines(assemblyRefPath.Value) do
            Reflection.Assembly.LoadFrom(loc) |> ignore

        let assembly = Reflection.Assembly.LoadFrom(assemblyPath.Value)

        let mainModule = assembly.GetType(fullName)
        
        try
            let _result = 
                mainModule.InvokeMember(
                    "main@",  
                    System.Reflection.BindingFlags.InvokeMethod ||| 
                    System.Reflection.BindingFlags.Public ||| 
                    System.Reflection.BindingFlags.Static, null, null, [||])
            true
        with
        | ex ->
            handleException ex
            false
    else
        let cacheDir = DirectoryInfo(Path.Combine(".",".fake"))
        if useCache then            
            if cacheDir.Exists then
                let oldFiles = 
                    cacheDir.GetFiles()
                    |> Seq.filter(fun file -> 
                        let oldScriptName, _ = getScriptAndHash(file.Name)
                        oldScriptName = scriptFileName.Value)

                if (oldFiles |> Seq.length) > 0 then
                    if cleanCache then
                        for file in oldFiles do
                            file.Delete()
                    trace "Cache is invalid, recompiling"
                else 
                    trace "Cache doesn't exist"
            else
                trace "Cache doesn't exist"
        try
            let session = FsiEvaluationSession.Create(fsiConfig, commonOptions, stdin, outStream, errStream)
            try
                session.EvalScript scriptPath

                try
                    if useCache && not cacheValid.Value then
                        let assemBuilder = session.DynamicAssembly :?> System.Reflection.Emit.AssemblyBuilder
                        assemBuilder.Save("FSI-ASSEMBLY.dll")
                        if not (Directory.Exists cacheDir.FullName) then
                            Directory.CreateDirectory cacheDir.FullName |> ignore
                        File.Move("FSI-ASSEMBLY.dll", assemblyPath.Value)
                    
                        if File.Exists("FSI-ASSEMBLY.pdb") then
                            File.Delete("FSI-ASSEMBLY.pdb")

                        let refedAssemblies = 
                            System.AppDomain.CurrentDomain.GetAssemblies()
                            |> Seq.filter(fun assem -> not assem.IsDynamic)
                            |> Seq.map(fun assem -> assem.Location)
                            
                        File.WriteAllLines(assemblyRefPath.Value, refedAssemblies) |> ignore
                        trace (System.Environment.NewLine + "Saved cache")
                with 
                | ex ->
                    handleException ex
                    reraise()
                // TODO: Reactivate when FCS don't show output any more 
                // handleMessages()
                true
            with
            | _ex ->
                handleMessages()
                false
        with
        | exn ->
            traceError "FsiEvaluationSession could not be created."
            traceError <| sbErr.ToString()
            raise exn

/// Run the given buildscript with fsi.exe and allows for extra arguments to the script. Returns output.
let executeBuildScriptWithArgsAndReturnMessages script (scriptArgs: string[]) useCache cleanCache =
    let messages = ref []
    let appendMessage isError msg =
        messages := { IsError = isError
                      Message = msg
                      Timestamp = DateTimeOffset.UtcNow } :: !messages
    let result =
        runFAKEScriptWithFsiArgsAndRedirectMessages
            true (FsiArgs([], script, scriptArgs |> List.ofArray)) []
            (appendMessage true) (appendMessage false) useCache cleanCache
    (result, !messages)

/// Run the given buildscript with fsi.exe at the given working directory.  Provides full access to Fsi options and args.
let runBuildScriptWithFsiArgsAt printDetails (FsiArgs(fsiOptions, script, scriptArgs)) args useCache cleanCache =
    runFAKEScriptWithFsiArgsAndRedirectMessages
        printDetails (FsiArgs(fsiOptions, script, scriptArgs)) args
        traceError (fun s-> traceFAKE "%s" s)
        useCache
        cleanCache

/// Run the given buildscript with fsi.exe at the given working directory.
let runBuildScriptAt printDetails script extraFsiArgs args useCache cleanCache =
    runBuildScriptWithFsiArgsAt printDetails (FsiArgs(extraFsiArgs, script, [])) args useCache cleanCache

/// Run the given buildscript with fsi.exe
let runBuildScript printDetails script extraFsiArgs args useCache cleanCache =
    runBuildScriptAt printDetails script extraFsiArgs args useCache cleanCache