<#
.SYNOPSIS
    Makes assets\vstest.mp4, the movie of the "video" area (src\test_video.c).

.DESCRIPTION
    Usage: .\make_movie.ps1   (make_movie.sh on Linux and macOS)
    4 s, 480x272, 30 fps, H.264 Main with B frames (a reordering stream, like the games' movies) and AAC 48 kHz
    stereo. The file is committed; run this only to change it.

    Requirements: ffmpeg with libx264 and the native AAC encoder on PATH.
#>
param(
    [switch]$Help
)

$ErrorActionPreference = "Stop"
if ($Help)
{
    Get-Help $MyInvocation.MyCommand.Path -Detailed
    return
}

if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue))
{
    throw "make_movie.ps1: ffmpeg not found on PATH"
}

$out = Join-Path $PSScriptRoot "assets\vstest.mp4"
& ffmpeg -hide_banner -loglevel error -y `
    -f lavfi -i "testsrc2=size=480x272:rate=30:duration=4" `
    -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=4" `
    -c:v libx264 -profile:v main -bf 2 -g 30 -pix_fmt yuv420p `
    -c:a aac -ac 2 -b:a 96k -movflags +faststart -map_metadata -1 -fflags +bitexact `
    $out
if ($LASTEXITCODE -ne 0)
{
    throw "make_movie.ps1: ffmpeg failed ($LASTEXITCODE)"
}

Write-Output "wrote $out"
