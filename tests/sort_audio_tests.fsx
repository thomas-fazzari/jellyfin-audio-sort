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
    createTaggedFlac root "one/track.flac" "Test Artist" "Test Album" |> ignore
    createTaggedFlac root "two/track.flac" "Test Artist" "Test Album" |> ignore
    let output = runSorter root

    if
        not (File.Exists(Path.Combine(root, ".inbox", "one", "track.flac")))
        || not (File.Exists(Path.Combine(root, ".inbox", "two", "track.flac")))
    then
        failwith "Duplicate plan moved a source file"

    if
        not (output.Contains("Moved: 0. Skipped: 2."))
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

Console.WriteLine "PASS move, duplicate-plan, path-safety, and broken-file tests"
