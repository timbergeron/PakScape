#ifndef PAKSCAPE_AUDIO_H
#define PAKSCAPE_AUDIO_H

#include <stddef.h>

#if defined(_WIN32)
#  if defined(PAKSCAPE_AUDIO_BUILD)
#    define PKA_API __declspec(dllexport)
#  else
#    define PKA_API __declspec(dllimport)
#  endif
#else
#  define PKA_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct pka_player pka_player;

enum pka_result {
    PKA_OK = 0,
    PKA_ERROR_INVALID_ARGUMENT = -1,
    PKA_ERROR_UNSUPPORTED_FORMAT = -2,
    PKA_ERROR_DECODE = -3,
    PKA_ERROR_AUDIO_DEVICE = -4,
    PKA_ERROR_OUT_OF_MEMORY = -5
};

/* The QSS-M music formats supported consistently on every PakScape platform. */
PKA_API int pka_supports_extension(const char *extension);

/*
 * Creates a paused player and copies encoded_data. The caller can release its
 * buffer as soon as this function returns. error_message is optional.
 */
PKA_API pka_player *pka_player_create(
    const void *encoded_data,
    size_t encoded_size,
    const char *extension,
    char *error_message,
    size_t error_message_size);

PKA_API void pka_player_destroy(pka_player *player);
PKA_API int pka_player_play(pka_player *player);
PKA_API int pka_player_pause(pka_player *player);
PKA_API int pka_player_seek(pka_player *player, double seconds);
PKA_API double pka_player_duration(const pka_player *player);
PKA_API double pka_player_position(const pka_player *player);
PKA_API int pka_player_is_playing(const pka_player *player);
PKA_API int pka_player_is_finished(const pka_player *player);

#ifdef __cplusplus
}
#endif

#endif
