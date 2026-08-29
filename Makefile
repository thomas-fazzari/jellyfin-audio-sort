-include .env

FSI = dotnet fsi
SCRIPT = sort_audio.fsx
FFPROBE ?= ffprobe
FFMPEG ?= ffmpeg
export MUSIC_ROOT FFPROBE FFMPEG

run:
	$(FSI) $(SCRIPT) -- --apply

preview:
	$(FSI) $(SCRIPT) --

check:
	$(FSI) tests/sort_audio_tests.fsx
