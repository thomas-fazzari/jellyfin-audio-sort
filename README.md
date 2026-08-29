# Jellyfin Audio Sort

Automatically sorts audio files into Jellyfin's `Artist/Album` folder structure using embedded metadata.

## Requirements

- .NET 10 SDK
- `ffprobe`, from Jellyfin or FFmpeg
- Audio files with `Album Artist` or `Artist` + `Album` tags

## Usage

Set your local configuration:

```bash
cp .env.example .env
```

Set `MUSIC_ROOT` in `.env` to your Jellyfin target music library folder. Set `FFPROBE` only if `ffprobe` is not available on `PATH`.

`MUSIC_ROOT/.inbox` is the staging folder. Drop new audio files there, then run the script:

```bash
make
```

Preview without changing files:

```bash
make preview
```

The script never overwrites existing files. Files without enough metadata stay in `.inbox` and are reported with `SKIP`.
