open System
open System.Diagnostics
open System.IO

let run executable arguments =
    let startInfo = ProcessStartInfo(executable)
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    arguments |> List.iter startInfo.ArgumentList.Add

    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()

    if child.ExitCode <> 0 then
        failwith $"{executable} failed:\n{output}{error}"

    output + error

let commandFromEnvironment name fallback =
    Environment.GetEnvironmentVariable name
    |> Option.ofObj
    |> Option.defaultValue fallback

let ffmpeg = commandFromEnvironment "FFMPEG" "ffmpeg"
let ffprobe = commandFromEnvironment "FFPROBE" "ffprobe"

let script =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "sort_audio.fsx"))

let createTaggedFlac root relativePath artist album =
    let destination = Path.Combine(root, ".inbox", relativePath)
    Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore

    run
        ffmpeg
        [ "-v"
          "error"
          "-f"
          "lavfi"
          "-i"
          "anullsrc=r=8000:cl=mono"
          "-t"
          "0.1"
          "-metadata"
          $"artist={artist}"
          "-metadata"
          $"album={album}"
          "-c:a"
          "flac"
          destination ]
    |> ignore

    destination

let runSorter root =
    run "dotnet" [ "fsi"; script; "--"; "--root"; root; "--ffprobe"; ffprobe; "--apply" ]

let withTemporaryRoot test =
    let root =
        Path.Combine(Path.GetTempPath(), $"jellyfin-audio-sort-{Guid.NewGuid():N}")

    try
        Directory.CreateDirectory(Path.Combine(root, ".inbox")) |> ignore
        test root
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

withTemporaryRoot (fun root ->
    let source = createTaggedFlac root "track.flac" "Test Artist" "Test Album"
    let output = runSorter root
    let destination = Path.Combine(root, "Test Artist", "Test Album", "track.flac")

    if File.Exists source || not (File.Exists destination) then
        failwith "Tagged audio was not moved to Artist/Album"

    if not (output.Contains("Moved: 1. Skipped: 0.")) then
        failwith $"Unexpected output: {output}")

withTemporaryRoot (fun root ->
    let sources =
        [| createTaggedFlac root "one/track.flac" "Test Artist" "Test Album"
           createTaggedFlac root "two/TRACK.flac" "test artist" "test album"
           createTaggedFlac root "three/track.flac" "Test Artist" "Test Album" |]

    let output = runSorter root

    if sources |> Array.exists (File.Exists >> not) then
        failwith "Duplicate plan moved a source file"

    if
        not (output.Contains("Moved: 0. Skipped: 3."))
        || not (output.Contains("duplicate destination in plan"))
    then
        failwith $"Duplicate destinations were not rejected: {output}")

withTemporaryRoot (fun root ->
    createTaggedFlac root "track.flac" ".." "Test\\Album" |> ignore
    let output = runSorter root
    let destination = Path.Combine(root, "_.._", "Test_Album", "track.flac")

    if not (File.Exists destination) then
        failwith $"Unsafe path segments were not sanitized: {output}")

withTemporaryRoot (fun root ->
    let goodSource = createTaggedFlac root "good.flac" "Test Artist" "Test Album"
    let brokenSource = Path.Combine(root, ".inbox", "broken.mp3")
    File.WriteAllText(brokenSource, "not audio")
    let output = runSorter root
    let goodDestination = Path.Combine(root, "Test Artist", "Test Album", "good.flac")

    if File.Exists goodSource || not (File.Exists goodDestination) then
        failwith "Valid audio was not moved when another file was broken"

    if not (File.Exists brokenSource) then
        failwith "Broken audio should remain in the inbox"

    if
        not (output.Contains("Moved: 1. Skipped: 1."))
        || not (output.Contains("SKIP broken.mp3: ffprobe failed"))
    then
        failwith $"Broken audio did not become a SKIP: {output}")

withTemporaryRoot (fun root ->
    let first = createTaggedFlac root "00.flac" "Test Artist" "Test Album"

    for index in 31..-1..1 do
        File.Copy(first, Path.Combine(root, ".inbox", $"{index:D2}.flac"))

    let sources = Directory.GetFiles(Path.Combine(root, ".inbox")) |> Array.sort
    let existing = Path.Combine(root, "Test Artist", "Test Album", "00.flac")
    Directory.CreateDirectory(Path.GetDirectoryName existing) |> ignore
    File.WriteAllText(existing, "existing destination")

    let expectedMoves action =
        sources[1..]
        |> Array.map (fun source ->
            let name = Path.GetFileName source
            let destination = Path.Combine("Test Artist", "Test Album", name)
            $"{action} {name} -> {destination}")

    let preview =
        run "dotnet" [ "fsi"; script; "--"; "--root"; root; "--ffprobe"; ffprobe ]

    let previewMoves =
        preview.Split(Environment.NewLine)
        |> Array.filter (fun line -> line.StartsWith("WOULD MOVE "))

    if previewMoves <> expectedMoves "WOULD MOVE" then
        failwith $"Preview did not preserve source order: {preview}"

    if sources |> Array.exists (File.Exists >> not) then
        failwith "Preview moved a source file"

    let output = runSorter root

    let actualMoves =
        output.Split(Environment.NewLine)
        |> Array.filter (fun line -> line.StartsWith("MOVE "))

    if actualMoves <> expectedMoves "MOVE" then
        failwith $"Apply did not preserve preview order: {output}"

    for source in sources[1..] do
        let destination =
            Path.Combine(root, "Test Artist", "Test Album", Path.GetFileName source)

        if File.Exists source || not (File.Exists destination) then
            failwith $"Batch move failed for {source}"

    if not (File.Exists first) || File.ReadAllText(existing) <> "existing destination" then
        failwith "Existing destination was overwritten or its source was moved"

    if
        not (preview.Contains("Ready to move: 31. Skipped: 1."))
        || not (output.Contains("Moved: 31. Skipped: 1."))
    then
        failwith $"Unexpected batch counts: {preview}{output}")

Console.WriteLine "PASS move, duplicate-plan, path-safety, broken-file, and ordered-batch tests"
