#ifndef PAKSCAPE_MODEL_H
#define PAKSCAPE_MODEL_H

#include <stddef.h>

#if defined(_WIN32)
#  if defined(PAKSCAPE_MODEL_BUILD)
#    define PKM_API __declspec(dllexport)
#  else
#    define PKM_API __declspec(dllimport)
#  endif
#else
#  define PKM_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct pkm_model pkm_model;
typedef struct pkm_view pkm_view;

enum pkm_result {
    PKM_OK = 0,
    PKM_ERROR_INVALID_ARGUMENT = -1,
    PKM_ERROR_UNSUPPORTED_FORMAT = -2,
    PKM_ERROR_PARSE = -3,
    PKM_ERROR_OUT_OF_MEMORY = -4
};

enum pkm_format {
    PKM_FORMAT_UNKNOWN = 0,
    PKM_FORMAT_MDL = 1,
    PKM_FORMAT_MD3 = 2,
    PKM_FORMAT_MD5 = 3,
    PKM_FORMAT_SPR = 4,
    PKM_FORMAT_BSP = 5
};

/* Keyboard nudges. Each one moves the camera goal by a fixed step. */
enum pkm_nudge {
    PKM_NUDGE_LEFT = 0,
    PKM_NUDGE_RIGHT = 1,
    PKM_NUDGE_UP = 2,
    PKM_NUDGE_DOWN = 3,
    PKM_NUDGE_IN = 4,
    PKM_NUDGE_OUT = 5
};

/*
 * Counts describe what is on screen at once, so a sprite reports the one quad it
 * draws rather than the sum of its frames; frame_count still counts every frame.
 */
typedef struct pkm_model_stats {
    int format;
    int surface_count;
    int vertex_count;
    int triangle_count;
    int frame_count;
    int skin_count;
    int texture_request_count;
    int textured_surface_count;
} pkm_model_stats;

/*
 * The model formats QSS-M loads: MDL, MD3, MD5, SPR sprites, and the brush models
 * stored as BSP. A .bsp holds either a brush model or a playable level, so the
 * extension alone does not decide: ask pkm_bsp_is_brush_model as well.
 */
PKM_API int pkm_supports_extension(const char *extension);

/*
 * True when BSP data is a brush model — an ammo box or a health kit, with one hull,
 * no visibility data, and nowhere for a player to spawn — rather than a level. A
 * level has its own flat overview preview, so hosts route only brush models here.
 */
PKM_API int pkm_bsp_is_brush_model(const void *bsp_data, size_t bsp_size);

/*
 * Parses model_data into an immutable mesh. The caller can release its buffer as
 * soon as this function returns. error_message is optional.
 */
PKM_API pkm_model *pkm_model_create(
    const void *model_data,
    size_t model_size,
    const char *extension,
    char *error_message,
    size_t error_message_size);

PKM_API void pkm_model_destroy(pkm_model *model);
PKM_API int pkm_model_format(const pkm_model *model);
PKM_API int pkm_model_get_stats(const pkm_model *model, pkm_model_stats *stats);

/*
 * MD3 and MD5 reference their skins by path. The host resolves each request
 * against the open archive and uploads straight RGBA8 pixels; surfaces without a
 * texture fall back to a neutral studio material. MDL skins are embedded, so
 * those models report no requests.
 */
PKM_API int pkm_model_texture_request_count(const pkm_model *model);
PKM_API int pkm_model_texture_request_surface(
    const pkm_model *model,
    int index,
    char *name,
    size_t name_size);
PKM_API int pkm_model_texture_request_name(
    const pkm_model *model,
    int index,
    char *name,
    size_t name_size);
PKM_API int pkm_model_set_texture(
    pkm_model *model,
    int index,
    const void *rgba_pixels,
    int width,
    int height);

/* Selects one of the embedded MDL skins. */
PKM_API int pkm_model_set_skin(pkm_model *model, int skin_index);

/*
 * A view owns the orbit camera and the software renderer. The model must outlive
 * every view created from it.
 */
PKM_API pkm_view *pkm_view_create(pkm_model *model);
PKM_API void pkm_view_destroy(pkm_view *view);

/* Uses a light backdrop when dark is zero. */
PKM_API void pkm_view_set_dark_background(pkm_view *view, int dark);
PKM_API void pkm_view_set_auto_rotate(pkm_view *view, int enabled);

/*
 * Pointer deltas are in rendered device pixels so that the gesture tracks the
 * model under the cursor on any display scale. zoom_steps counts wheel notches;
 * pinches pass the natural logarithm of the scale factor.
 */
PKM_API void pkm_view_begin_interaction(pkm_view *view);
PKM_API void pkm_view_orbit(pkm_view *view, float dx, float dy);
PKM_API void pkm_view_pan(pkm_view *view, float dx, float dy);
PKM_API void pkm_view_zoom(pkm_view *view, float zoom_steps);
PKM_API void pkm_view_end_interaction(pkm_view *view);
PKM_API void pkm_view_nudge(pkm_view *view, int nudge);
PKM_API void pkm_view_reset(pkm_view *view);

/*
 * Advances damping, inertia, idle auto-rotation, and sprite playback. Returns 1
 * when the next frame differs from the one already on screen, so an idle viewer
 * costs nothing. A sprite with more than one frame is never idle: it loops at the
 * intervals stored in the file, or ten frames a second when it stores none.
 */
PKM_API int pkm_view_advance(pkm_view *view, double elapsed_seconds);

/* True until the viewer has been dragged, so hosts can fade an orbit hint. */
PKM_API int pkm_view_show_interaction_prompt(const pkm_view *view);

/*
 * Renders BGRA8 rows, the layout WPF, Avalonia, and Core Graphics all accept
 * without conversion. Supersamples once the camera settles.
 */
PKM_API int pkm_view_render(
    pkm_view *view,
    void *bgra_pixels,
    int width,
    int height,
    int stride);

#ifdef __cplusplus
}
#endif

#endif
