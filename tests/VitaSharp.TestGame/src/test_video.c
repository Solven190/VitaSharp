/*
 * Video: a movie played to its end with sceAvPlayer, the way games play their intros. The player is created and the
 * source added, then nothing is read for a while (a game that queues its next movie during a loading screen: the
 * decoder fills its queue and waits), then every picture and audio frame is taken until sceAvPlayerIsActive says the
 * player reached its end. Checks: active from init (before the source), every picture seen once in order up to the
 * last one, the end reached in about the movie's length, audio delivered. A second run skips the movie halfway
 * (sceAvPlayerStop, then sceAvPlayerClose): both return and the player is inactive right after the stop.
 *
 * The movie (app0:movie/vstest.mp4, 4 s, 480x272, 30 fps, H.264 Main with B frames, AAC 48 kHz stereo) is made by
 * make_movie.sh (make_movie.ps1 on Windows) with FFmpeg.
 *
 * No check depends on the host clock: the end is "sceAvPlayerIsActive answered 0", the pictures are counted by their
 * timestamps; the one time limit (60 s, 15 times the movie) only ends a run whose player never ends, as a FAIL.
 */
#include <malloc.h>
#include <stdio.h>
#include <string.h>

#include <psp2/avplayer.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/sysmodule.h>

#include "app.h"

#define AREA "video"
#define MOVIE_PATH "app0:movie/vstest.mp4"
#define MOVIE_FRAMES 120
#define MOVIE_MS 4000
#define IDLE_BEFORE_START_US (2 * 1000 * 1000)
#define END_LIMIT_US (60 * 1000 * 1000ull)
#define POLL_US 8000
#define SKIP_AT_MS 1500

static void *video_allocate(void *argument, uint32_t alignment, uint32_t size)
{
    (void)argument;
    return memalign(alignment < 64 ? 64 : alignment, size);
}

static void video_free(void *argument, void *pointer)
{
    (void)argument;
    free(pointer);
}

/*
 * One movie: created, source added, idle, started, then read until its end, or stopped by the game at stopAtMs (a
 * skipped cutscene: sceAvPlayerStop and sceAvPlayerClose must return at once and the player must be inactive).
 */
static void play_movie(bool skip)
{
    const char *prefix = skip ? "skip_" : "";
    char check[48];
    SceAvPlayerInitData init;
    memset(&init, 0, sizeof(init));
    init.memoryReplacement.allocate = video_allocate;
    init.memoryReplacement.deallocate = video_free;
    init.memoryReplacement.allocateTexture = video_allocate;
    init.memoryReplacement.deallocateTexture = video_free;
    init.basePriority = 0xA0;
    init.numOutputVideoFrameBuffers = 2;
    init.autoStart = SCE_FALSE;
    SceAvPlayerHandle player = sceAvPlayerInit(&init);
    snprintf(check, sizeof(check), "%sinit", prefix);
    result_check(AREA, check, player > 0, "handle 0x%X", (unsigned int)player);
    if (player <= 0)
    {
        return;
    }

    /* the firmware's player is active from init until its end (a game may check before it adds the source) */
    snprintf(check, sizeof(check), "%sactive_before_source", prefix);
    result_check(AREA, check, sceAvPlayerIsActive(player) == SCE_TRUE, "%d", (int)sceAvPlayerIsActive(player));
    int addResult = sceAvPlayerAddSource(player, MOVIE_PATH);
    snprintf(check, sizeof(check), "%sadd_source", prefix);
    result_check(AREA, check, addResult >= 0, "0x%08X", (unsigned int)addResult);
    if (addResult < 0)
    {
        sceAvPlayerClose(player);
        return;
    }

    /* queued but not read yet: the decoder runs ahead and waits with a full queue */
    sceKernelDelayThread(IDLE_BEFORE_START_US);
    sceAvPlayerStart(player);

    int pictures = 0;
    int outOfOrder = 0;
    int audioFrames = 0;
    int polls = 0;
    uint64_t lastPictureMs = 0;
    bool havePicture = false;
    uint64_t start = time_now_us();
    uint64_t lastProgress = start;
    bool ended = false;
    while (time_now_us() - start < END_LIMIT_US)
    {
        if (skip && havePicture && lastPictureMs >= SKIP_AT_MS)
        {
            break;
        }

        if (!sceAvPlayerIsActive(player))
        {
            ended = true;
            break;
        }

        SceAvPlayerFrameInfo frame;
        memset(&frame, 0, sizeof(frame));
        if (sceAvPlayerGetVideoData(player, &frame) && frame.pData != NULL)
        {
            if (!havePicture || frame.timeStamp > lastPictureMs)
            {
                pictures++;
            }
            else if (frame.timeStamp < lastPictureMs)
            {
                outOfOrder++;
            }

            havePicture = true;
            if (frame.timeStamp > lastPictureMs)
            {
                lastPictureMs = frame.timeStamp;
            }
        }

        SceAvPlayerFrameInfo audio;
        memset(&audio, 0, sizeof(audio));
        while (sceAvPlayerGetAudioData(player, &audio) && audio.pData != NULL)
        {
            audioFrames++;
            memset(&audio, 0, sizeof(audio));
        }

        polls++;
        if (time_now_us() - lastProgress > 500000)
        {
            char message[64];
            snprintf(message, sizeof(message), "picture %d at %u ms", pictures, (unsigned int)lastPictureMs);
            app_show_progress("Video", message);
            lastProgress = time_now_us();
        }
        else
        {
            sceKernelDelayThread(POLL_US);
        }
    }

    uint64_t elapsedMs = (time_now_us() - start) / 1000;
    if (skip)
    {
        /* the game skips the movie: Stop returns (it must not wait for the decoder), the player is at its end right
         * after it, Close returns; the times are only reported */
        uint64_t stopStart = time_now_us();
        int stopResult = sceAvPlayerStop(player);
        uint64_t stopMs = (time_now_us() - stopStart) / 1000;
        result_check(AREA, "skip_stop", stopResult >= 0, "0x%08X in %u ms at %u ms", (unsigned int)stopResult, (unsigned int)stopMs, (unsigned int)lastPictureMs);
        result_check(AREA, "skip_inactive", sceAvPlayerIsActive(player) == SCE_FALSE, "%d", (int)sceAvPlayerIsActive(player));
        uint64_t closeStart = time_now_us();
        int closeResult = sceAvPlayerClose(player);
        uint64_t closeMs = (time_now_us() - closeStart) / 1000;
        result_check(AREA, "skip_close", closeResult >= 0, "0x%08X in %u ms", (unsigned int)closeResult, (unsigned int)closeMs);
        return;
    }

    result_check(AREA, "reaches_end", ended, "%u ms (movie %d ms), %d polls", (unsigned int)elapsedMs, MOVIE_MS, polls);
    /* a late poll may skip a picture whose successor is already due (reported); a frozen stream never reaches the
     * last one */
    result_check(AREA, "pictures", pictures > 0, "%d of %d", pictures, MOVIE_FRAMES);
    result_check(AREA, "pictures_in_order", outOfOrder == 0, "%d out of order", outOfOrder);
    result_check(AREA, "last_picture", lastPictureMs + 100 >= (uint64_t)(MOVIE_MS - 1000 / 30), "%u ms", (unsigned int)lastPictureMs);
    result_check(AREA, "audio", audioFrames > 0, "%d frames", audioFrames);
    result_check(AREA, "close", sceAvPlayerClose(player) >= 0, "closed");
}

void test_video_run(void)
{
    results_clear_area(AREA);
    app_show_progress("Video", "sceAvPlayer: movie to its end, then skipped");
    int moduleResult = sceSysmoduleLoadModule(SCE_SYSMODULE_AVPLAYER);
    result_check(AREA, "load_module", moduleResult >= 0, "0x%08X", (unsigned int)moduleResult);
    if (moduleResult < 0)
    {
        return;
    }

    play_movie(false);
    play_movie(true);
    sceSysmoduleUnloadModule(SCE_SYSMODULE_AVPLAYER);
}
