#!/usr/bin/env -S dotnet fsi

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text.Json

let audioExtensions =
    HashSet(
        [| ".aac"; ".flac"; ".m4a"; ".m4b"; ".mp3"; ".ogg"; ".opus"; ".wav"; ".wma" |],
        StringComparer.OrdinalIgnoreCase
    )

type Options =
    { Root: string option
      Inbox: string option
      Ffprobe: string
      Apply: bool }

type Move = { Source: string; Destination: string }

let usage =
    """Usage: sort_audio.fsx [--root PATH] [--inbox PATH] [--apply]

Organize audio files into Artist/Album folders using their metadata.

Options:
  --root PATH   Music library root. Alternative: MUSIC_ROOT environment variable.
  --inbox PATH  Inbox directory. Default: ROOT/.inbox
  --ffprobe PATH
                ffprobe executable. Alternative: FFPROBE environment variable or PATH.
  --apply       Move files. Without this option, only print the plan.
  -h, --help    Show this help.
"""

let environmentVariable name =
    Environment.GetEnvironmentVariable name
    |> Option.ofObj
    |> Option.filter (String.IsNullOrWhiteSpace >> not)

let parseArgs (args: string array) =
    let rec loop options index =
        if index >= args.Length then
            options
        else
            match args[index] with
            | "--apply" -> loop { options with Apply = true } (index + 1)
            | "--root" when index + 1 < args.Length ->
                loop
                    { options with
                        Root = Some args[index + 1] }
                    (index + 2)
            | "--inbox" when index + 1 < args.Length ->
                loop
                    { options with
                        Inbox = Some args[index + 1] }
                    (index + 2)
            | "--ffprobe" when index + 1 < args.Length ->
                loop
                    { options with
                        Ffprobe = args[index + 1] }
                    (index + 2)
            | "-h"
            | "--help" ->
                Console.Write usage
                Environment.Exit 0
                options
            | option -> invalidArg "args" $"Unknown or incomplete option: {option}"

    loop
        { Root = environmentVariable "MUSIC_ROOT"
          Inbox = None
          Ffprobe = environmentVariable "FFPROBE" |> Option.defaultValue "ffprobe"
          Apply = false }
        0

let invalidNameChars = Path.GetInvalidFileNameChars() |> Set.ofArray

let safeDirectoryName (value: string) =
    let result =
        value.Trim()
        |> String.map (fun character ->
            if invalidNameChars.Contains character || character = '/' || character = '\\' then
                '_'
            else
                character)

    match result with
    | "."
    | ".." -> $"_{result}_"
    | value -> value

let readTags ffprobe file =
    let startInfo = ProcessStartInfo(ffprobe)
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false

    [ "-v"
      "error"
      "-show_entries"
      "format_tags=artist,album,album_artist"
      "-of"
      "json"
      file ]
    |> List.iter startInfo.ArgumentList.Add

    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()

    if child.ExitCode <> 0 then
        failwith $"ffprobe failed for {file}: {error.Trim()}"

    use document = JsonDocument.Parse output
    let mutable format = Unchecked.defaultof<JsonElement>
    let mutable tags = Unchecked.defaultof<JsonElement>

    if
        document.RootElement.TryGetProperty("format", &format)
        && format.TryGetProperty("tags", &tags)
    then
        tags.EnumerateObject()
        |> Seq.map (fun property -> property.Name.ToLowerInvariant(), property.Value.GetString())
        |> Map.ofSeq
    else
        Map.empty

let tryTag name tags =
    Map.tryFind name tags
    |> Option.bind (fun value -> if String.IsNullOrWhiteSpace value then None else Some value)

let planFile ffprobe root source =
    try
        let tags = readTags ffprobe source

        let artist =
            tryTag "album_artist" tags
            |> Option.orElseWith (fun () -> tryTag "artist" tags)
            |> Option.map safeDirectoryName

        let album = tryTag "album" tags |> Option.map safeDirectoryName

        match artist, album with
        | Some artist, Some album ->
            let destination = Path.Combine(root, artist, album, Path.GetFileName source)

            if File.Exists destination then
                Error $"{Path.GetFileName source}: destination already exists"
            else
                Ok
                    { Source = source
                      Destination = destination }
        | _ -> Error $"{Path.GetFileName source}: missing Album Artist/Artist or Album tag"
    with error ->
        Error $"{Path.GetFileName source}: {error.Message}"

let planMoves ffprobe root inbox =
    let initialResults =
        Directory.EnumerateFiles(inbox, "*", SearchOption.AllDirectories)
        |> Seq.filter (Path.GetExtension >> audioExtensions.Contains)
        |> Seq.sort
        |> Seq.toArray
        |> Array.Parallel.map (planFile ffprobe root)

    let destinations = HashSet<string>(StringComparer.OrdinalIgnoreCase)
    let duplicateDestinations = HashSet<string>(StringComparer.OrdinalIgnoreCase)

    for result in initialResults do
        match result with
        | Ok move when not (destinations.Add move.Destination) -> duplicateDestinations.Add move.Destination |> ignore
        | _ -> ()

    let moves = ResizeArray<Move>()
    let skipped = ResizeArray<string>()

    for result in initialResults do
        match result with
        | Ok move when duplicateDestinations.Contains move.Destination ->
            skipped.Add $"{Path.GetFileName move.Source}: duplicate destination in plan"
        | Ok move -> moves.Add move
        | Error message -> skipped.Add message

    moves.ToArray(), skipped.ToArray()

let run args =
    let options = parseArgs args

    let root =
        options.Root
        |> Option.defaultWith (fun () -> invalidArg "root" "Set --root or the MUSIC_ROOT environment variable")
        |> Path.GetFullPath

    let inbox =
        Path.GetFullPath(defaultArg options.Inbox (Path.Combine(root, ".inbox")))

    if not (Directory.Exists inbox) then
        raise (DirectoryNotFoundException $"Inbox directory not found: {inbox}")

    let relativeInbox = Path.GetRelativePath(root, inbox)

    if
        relativeInbox = "."
        || relativeInbox = ".."
        || relativeInbox.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
    then
        invalidArg "inbox" "The inbox directory must be inside the music library"

    let moves, skipped = planMoves options.Ffprobe root inbox
    let action = if options.Apply then "MOVE" else "WOULD MOVE"
    let createdDirectories = HashSet<string>(StringComparer.Ordinal)

    for move in moves do
        Console.WriteLine $"{action} {Path.GetFileName move.Source} -> {Path.GetRelativePath(root, move.Destination)}"

        if options.Apply then
            let directory = Path.GetDirectoryName move.Destination

            if createdDirectories.Add directory then
                Directory.CreateDirectory directory |> ignore

            File.Move(move.Source, move.Destination)

    for message in skipped do
        Console.Error.WriteLine $"SKIP {message}"

    let status = if options.Apply then "Moved" else "Ready to move"
    Console.WriteLine $"{status}: {moves.Length}. Skipped: {skipped.Length}."

try
    run (fsi.CommandLineArgs |> Array.skip 1)
with error ->
    Console.Error.WriteLine $"ERROR {error.Message}"
    Environment.ExitCode <- 1
