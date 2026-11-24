#define MINIAUDIO_IMPLEMENTATION
#define MA_NO_ENCODING
#define MA_NO_MP3
#define MA_NO_FLAC
#include "miniaudio.h"
#include <stdio.h>
#include <string.h>

int main(int argc, char **argv)
{
    if (argc != 2) return 2;
    ma_engine engine;
    if (ma_engine_init(NULL, &engine) != MA_SUCCESS) {
        fprintf(stderr, "Cannot open the audio output device.\n");
        return 1;
    }
    ma_sound sound;
    if (ma_sound_init_from_file(&engine, argv[1], MA_SOUND_FLAG_STREAM, NULL, NULL, &sound) != MA_SUCCESS) {
        ma_engine_uninit(&engine);
        return 1;
    }
    puts("READY");
    fflush(stdout);
    char command[96];
    while (fgets(command, sizeof command, stdin)) {
        float value;
        if (!strcmp(command, "PLAY\n")) ma_sound_start(&sound);
        else if (!strcmp(command, "PAUSE\n")) ma_sound_stop(&sound);
        else if (sscanf(command, "SEEK %f", &value) == 1 && value >= 0)
            ma_sound_seek_to_second(&sound, value);
        else if (sscanf(command, "VOLUME %f", &value) == 1)
            ma_sound_set_volume(&sound, value < 0 ? 0 : value > 1 ? 1 : value);
        else if (!strcmp(command, "QUIT\n")) break;
    }
    ma_sound_uninit(&sound);
    ma_engine_uninit(&engine);
    return 0;
}
