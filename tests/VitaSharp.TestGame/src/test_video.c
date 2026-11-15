/*
 * Video: a movie played to its end with sceAvPlayer, the way games play their intros. The player is created and the
 * source added, then nothing is read for a while (a game that queues its next movie during a loading screen: the
 * decoder fills its queue and waits), then every picture and audio frame is taken until sceAvPlayerIsActive says the
 * player reached its end. Checks: active from init (before the source), every picture seen once in order up to the
 * last one, the end reached in about the movie's length, audio delivered. A second run skips the movie halfway
 * (sceAvPlayerStop, then sceAvPlayerClose): both return and the player is inactive right after the stop.
 * Then the firmware's pacing and jumps: one read per vblank, a late caller, a hold loop that jumps back and a single
 * jump (see play_paced, play_late, play_jumps, play_single_jump).
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
#include <psp2/display.h>
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

/* ------------------------------------------------------------------ pacing and jumps (libsceavplayer 3.74)
 *
 * How the firmware's player hands pictures out and jumps (Killzone: Mercenary's intro and title movie, 14/11/2026):
 * GetVideoData gives the next picture in order, one per call, once the player's clock reaches it (never the newest
 * due one, so a late caller catches up picture by picture); JumpToTime goes back to the sync sample before the time
 * and hands out nothing from before it, CurrentTime answers the jump's time right after the call, and another jump
 * is refused with 0x806A0002 while the player is still in its jump state. Every loop is paced by the vblank or by the
 * calls themselves; the time limits only end a player that never gets there, as a FAIL. */

#define FRAME_MS 34                      /* one 30 fps picture: 33 or 34 ms between timestamps */
#define JUMP_TO_MS 1500                  /* in the middle of a GOP (sync samples every second) */
#define JUMP_WHEN_PAST_MS 3000
#define JUMP_LOOPS 3
#define JUMP_LIMIT_US (30 * 1000 * 1000ull)
#define LATE_DELAY_US (500 * 1000)
#define LATE_CALLS 8
#define AVPLAYER_JUMP_IN_PROGRESS ((int)0x806A0002)
#define PACED_HITCH_EVERY 40
#define PACED_HITCH_US (150 * 1000)
#define RAPID_JUMP_FRAMES 10             /* a second jump this soon after one is a jump on every frame */

/* A player for the test movie, started (0 when it cannot be made: recorded as FAIL under check). */
static SceAvPlayerHandle start_player(const char *check)
{
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
    if (player <= 0)
    {
        result_add(AREA, check, RESULT_FAIL, "init 0x%X", (unsigned int)player);
        return 0;
    }

    int addResult = sceAvPlayerAddSource(player, MOVIE_PATH);
    if (addResult < 0)
    {
        result_add(AREA, check, RESULT_FAIL, "add source 0x%08X", (unsigned int)addResult);
        sceAvPlayerClose(player);
        return 0;
    }

    sceAvPlayerStart(player);
    return player;
}

static void drain_audio(SceAvPlayerHandle player)
{
    SceAvPlayerFrameInfo audio;
    memset(&audio, 0, sizeof(audio));
    while (sceAvPlayerGetAudioData(player, &audio) && audio.pData != NULL)
    {
        memset(&audio, 0, sizeof(audio));
    }
}

/* The timestamp (ms) of the picture GetVideoData hands out now, or -1 when it hands out none. */
static int64_t take_picture(SceAvPlayerHandle player)
{
    SceAvPlayerFrameInfo frame;
    memset(&frame, 0, sizeof(frame));
    if (sceAvPlayerGetVideoData(player, &frame) && frame.pData != NULL)
    {
        return (int64_t)frame.timeStamp;
    }

    return -1;
}

/*
 * The movie (H.264 with B frames, so the decoder reorders) read with one GetVideoData call per vblank, as a game's
 * render loop does, with a slow frame (PACED_HITCH_US) every PACED_HITCH_EVERY vblanks: the timestamps handed out
 * only go forward, and none is left out (each new one is the next picture, 33-34 ms after the one before; after a
 * slow frame the next calls catch up picture by picture instead of jumping to the newest).
 */
static void play_paced(void)
{
    SceAvPlayerHandle player = start_player("paced_pts_forward");
    if (player == 0)
    {
        return;
    }

    int pictures = 0;
    int backwards = 0;
    int64_t largestGap = 0;
    int64_t last = -1;
    int frames = 0;
    uint64_t start = time_now_us();
    while (sceAvPlayerIsActive(player) && time_now_us() - start < END_LIMIT_US)
    {
        int64_t timeStamp = take_picture(player);
        if (timeStamp >= 0 && last < 0)
        {
            pictures++;
            last = timeStamp;
        }
        else if (timeStamp > last)
        {
            pictures++;
            if (timeStamp - last > largestGap)
            {
                largestGap = timeStamp - last;
            }

            last = timeStamp;
        }
        else if (timeStamp >= 0 && timeStamp < last)
        {
            backwards++;
        }

        drain_audio(player);
        frames++;
        if (frames % PACED_HITCH_EVERY == 0)
        {
            sceKernelDelayThread(PACED_HITCH_US);
        }

        sceDisplayWaitVblankStart();
    }

    result_check(AREA, "paced_pts_forward", pictures > 0 && backwards == 0, "%d pictures, %d went back", pictures,
                 backwards);
    result_check(AREA, "paced_no_picture_skipped", pictures >= MOVIE_FRAMES - 1 && largestGap <= FRAME_MS,
                 "%d of %d pictures, largest step %lld ms", pictures, MOVIE_FRAMES, (long long)largestGap);
    sceAvPlayerClose(player);
}

/*
 * A late caller (a slow frame): the player's clock runs on while nothing is read, then GetVideoData is called again
 * and again at once. Each call hands out the next picture in order, one per call, none skipped to the newest.
 */
static void play_late(void)
{
    SceAvPlayerHandle player = start_player("late_one_picture_per_call");
    if (player == 0)
    {
        return;
    }

    int64_t first = -1;
    uint64_t start = time_now_us();
    while (first < 0 && sceAvPlayerIsActive(player) && time_now_us() - start < END_LIMIT_US)
    {
        first = take_picture(player);
        drain_audio(player);
        sceDisplayWaitVblankStart();
    }

    sceKernelDelayThread(LATE_DELAY_US);
    int64_t previous = first;
    int inStep = 0;
    char steps[96];
    int used = 0;
    steps[0] = '\0';
    for (int i = 0; i < LATE_CALLS; i++)
    {
        int64_t timeStamp = take_picture(player);
        int64_t step = timeStamp - previous;
        if (first >= 0 && timeStamp > previous && step <= FRAME_MS)
        {
            inStep++;
        }

        if (used < (int)sizeof(steps) - 8)
        {
            used += snprintf(steps + used, sizeof(steps) - (size_t)used, " %lld", (long long)step);
        }

        if (timeStamp >= 0)
        {
            previous = timeStamp;
        }
    }

    result_check(AREA, "late_one_picture_per_call", inStep == LATE_CALLS, "first %lld ms, steps%s", (long long)first,
                 steps);
    sceAvPlayerClose(player);
}

/*
 * A scanner-style hold loop (Killzone: Mercenary's title movie): whenever CurrentTime passes JUMP_WHEN_PAST_MS the
 * program jumps back to JUMP_TO_MS. Checks: CurrentTime is the jump's time right after the call; a second jump at
 * once is refused (0x806A0002); each loop has one jump and its pictures animate (a new one on most frames, not the
 * same one held).
 */
static void play_jumps(void)
{
    SceAvPlayerHandle player = start_player("jump_current_time");
    if (player == 0)
    {
        return;
    }

    int jumps = 0;
    int currentTimeWrong = 0;
    uint64_t wrongCurrentTime = 0;
    int secondJumpAccepted = 0;
    int secondJumpResult = 0;
    int fewestPictures = 1 << 30;
    int picturesThisLoop = 0;
    int64_t last = -1;
    uint64_t start = time_now_us();
    int framesSinceJump = 0;
    int rapidJumps = 0;
    while (jumps < JUMP_LOOPS + 1 && sceAvPlayerIsActive(player) && time_now_us() - start < JUMP_LIMIT_US)
    {
        /* the game reads CurrentTime at the start of its frame, before it asks for the frame's picture */
        framesSinceJump++;
        if (sceAvPlayerCurrentTime(player) > JUMP_WHEN_PAST_MS)
        {
            /* the loops between two jumps (not the first playback up to the first jump) */
            if (jumps > 0 && picturesThisLoop < fewestPictures)
            {
                fewestPictures = picturesThisLoop;
            }

            if (jumps > 0 && framesSinceJump < RAPID_JUMP_FRAMES)
            {
                rapidJumps++;
            }

            sceAvPlayerJumpToTime(player, JUMP_TO_MS);
            jumps++;
            picturesThisLoop = 0;
            framesSinceJump = 0;
            uint64_t afterJump = sceAvPlayerCurrentTime(player);
            if (afterJump < JUMP_TO_MS || afterJump >= JUMP_TO_MS + 100)
            {
                currentTimeWrong++;
                wrongCurrentTime = afterJump;
            }

            int again = sceAvPlayerJumpToTime(player, JUMP_TO_MS);
            if (again != AVPLAYER_JUMP_IN_PROGRESS)
            {
                secondJumpAccepted++;
                secondJumpResult = again;
            }
        }

        int64_t timeStamp = take_picture(player);
        if (timeStamp >= 0 && timeStamp != last)
        {
            picturesThisLoop++;
            last = timeStamp;
        }

        drain_audio(player);
        sceDisplayWaitVblankStart();
    }

    result_check(AREA, "jump_current_time", jumps > 0 && currentTimeWrong == 0, "%d jumps, %d wrong (last %llu ms)",
                 jumps, currentTimeWrong, (unsigned long long)wrongCurrentTime);
    result_check(AREA, "jump_during_jump_refused", jumps > 0 && secondJumpAccepted == 0, "%d of %d answered 0x%08X",
                 secondJumpAccepted, jumps, (unsigned int)secondJumpResult);
    /* a loop of 1.5 s at 30 fps has about 45 pictures; a held picture or a jump on every frame gives one or none */
    bool loopsDone = jumps == JUMP_LOOPS + 1;
    result_check(AREA, "jump_once_per_loop_animates", loopsDone && rapidJumps == 0 && fewestPictures >= 20,
                 "%d jumps, %d on the frames right after one, fewest pictures in a loop %d", jumps, rapidJumps,
                 loopsDone ? fewestPictures : 0);
    sceAvPlayerClose(player);
}

/*
 * One jump into the middle of a GOP (the decoder starts again at the sync sample a second earlier): of the pictures
 * handed out after it, none is from before the jump's time.
 */
static void play_single_jump(void)
{
    SceAvPlayerHandle player = start_player("jump_nothing_before_target");
    if (player == 0)
    {
        return;
    }

    int64_t last = -1;
    uint64_t start = time_now_us();
    while (last < JUMP_WHEN_PAST_MS && sceAvPlayerIsActive(player) && time_now_us() - start < JUMP_LIMIT_US)
    {
        int64_t timeStamp = take_picture(player);
        if (timeStamp > last)
        {
            last = timeStamp;
        }

        drain_audio(player);
        sceDisplayWaitVblankStart();
    }

    int jumpResult = sceAvPlayerJumpToTime(player, JUMP_TO_MS);
    int pictures = 0;
    int beforeTarget = 0;
    int64_t earliest = -1;
    int64_t previous = -1;
    for (int frame = 0; frame < 60 && sceAvPlayerIsActive(player); frame++)
    {
        int64_t timeStamp = take_picture(player);
        if (timeStamp >= 0 && timeStamp != previous)
        {
            pictures++;
            previous = timeStamp;
            if (timeStamp < JUMP_TO_MS)
            {
                beforeTarget++;
            }

            if (earliest < 0 || timeStamp < earliest)
            {
                earliest = timeStamp;
            }
        }

        drain_audio(player);
        sceDisplayWaitVblankStart();
    }

    result_check(AREA, "jump_nothing_before_target", jumpResult >= 0 && pictures > 0 && beforeTarget == 0,
                 "jump 0x%08X, %d pictures after it, %d before %d ms (earliest %lld)", (unsigned int)jumpResult, pictures,
                 beforeTarget, JUMP_TO_MS, (long long)earliest);
    sceAvPlayerClose(player);
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
    app_show_progress("Video", "sceAvPlayer: paced reads, a late caller, jumps");
    play_paced();
    play_late();
    play_jumps();
    play_single_jump();
    sceSysmoduleUnloadModule(SCE_SYSMODULE_AVPLAYER);
}
