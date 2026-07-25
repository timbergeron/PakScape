#include "pakscape_model.h"

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace {

int failures = 0;

/*
 * The release build defines NDEBUG, so these tests report failures themselves
 * instead of relying on assert.
 */
void check(bool condition, const char *description) {
    if (!condition) {
        failures++;
        std::fprintf(stderr, "FAIL: %s\n", description);
    }
}

void appendU32(std::vector<unsigned char> &bytes, std::uint32_t value) {
    bytes.push_back(static_cast<unsigned char>(value));
    bytes.push_back(static_cast<unsigned char>(value >> 8));
    bytes.push_back(static_cast<unsigned char>(value >> 16));
    bytes.push_back(static_cast<unsigned char>(value >> 24));
}

void appendI32(std::vector<unsigned char> &bytes, std::int32_t value) {
    appendU32(bytes, static_cast<std::uint32_t>(value));
}

void appendI16(std::vector<unsigned char> &bytes, std::int16_t value) {
    const std::uint16_t bits = static_cast<std::uint16_t>(value);
    bytes.push_back(static_cast<unsigned char>(bits));
    bytes.push_back(static_cast<unsigned char>(bits >> 8));
}

void appendFloat(std::vector<unsigned char> &bytes, float value) {
    std::uint32_t bits = 0;
    std::memcpy(&bits, &value, sizeof(bits));
    appendU32(bytes, bits);
}

void appendPadded(std::vector<unsigned char> &bytes, const std::string &value, size_t length) {
    for (size_t index = 0; index < length; index++) {
        bytes.push_back(index < value.size() ? static_cast<unsigned char>(value[index]) : 0);
    }
}

std::vector<unsigned char> buildMdl() {
    constexpr int skinWidth = 4;
    constexpr int skinHeight = 4;
    std::vector<unsigned char> bytes;

    appendU32(bytes, 0x4F504449);  // "IDPO"
    appendI32(bytes, 6);
    appendFloat(bytes, 1.0f);  // scale
    appendFloat(bytes, 1.0f);
    appendFloat(bytes, 1.0f);
    appendFloat(bytes, 0.0f);  // translate
    appendFloat(bytes, 0.0f);
    appendFloat(bytes, 0.0f);
    appendFloat(bytes, 128.0f);  // bounding radius
    appendFloat(bytes, 0.0f);    // eye position
    appendFloat(bytes, 0.0f);
    appendFloat(bytes, 0.0f);
    appendI32(bytes, 1);           // skins
    appendI32(bytes, skinWidth);
    appendI32(bytes, skinHeight);
    appendI32(bytes, 3);  // vertices
    appendI32(bytes, 1);  // triangles
    appendI32(bytes, 1);  // frames
    appendI32(bytes, 0);  // sync type
    appendI32(bytes, 0);  // flags
    appendFloat(bytes, 0.0f);

    appendI32(bytes, 0);  // single skin
    for (int index = 0; index < skinWidth * skinHeight; index++) {
        bytes.push_back(15);  // a bright palette entry
    }

    const int coordinates[3][2] = {{0, 0}, {3, 0}, {0, 3}};
    for (const auto &coordinate : coordinates) {
        appendI32(bytes, 0);
        appendI32(bytes, coordinate[0]);
        appendI32(bytes, coordinate[1]);
    }

    appendI32(bytes, 1);  // faces front
    appendI32(bytes, 0);
    appendI32(bytes, 1);
    appendI32(bytes, 2);

    appendI32(bytes, 0);  // single frame
    for (int index = 0; index < 8; index++) {
        bytes.push_back(0);  // bounding box
    }
    appendPadded(bytes, "frame", 16);
    const unsigned char positions[3][3] = {{10, 10, 10}, {200, 20, 10}, {20, 200, 180}};
    for (const auto &position : positions) {
        bytes.push_back(position[0]);
        bytes.push_back(position[1]);
        bytes.push_back(position[2]);
        bytes.push_back(0);  // normal index
    }
    return bytes;
}

std::vector<unsigned char> buildMd3(const std::string &shader) {
    std::vector<unsigned char> bytes;

    appendU32(bytes, 0x33504449);  // "IDP3"
    appendI32(bytes, 15);
    appendPadded(bytes, "test", 64);
    appendI32(bytes, 0);    // flags
    appendI32(bytes, 1);    // frames
    appendI32(bytes, 0);    // tags
    appendI32(bytes, 1);    // surfaces
    appendI32(bytes, 0);    // skins
    appendI32(bytes, 108);  // frame offset
    appendI32(bytes, 164);  // tag offset
    appendI32(bytes, 164);  // surface offset
    appendI32(bytes, 400);  // end offset

    for (int index = 0; index < 3; index++) {  // bounds and origin
        appendFloat(bytes, -64.0f);
    }
    for (int index = 0; index < 3; index++) {
        appendFloat(bytes, 64.0f);
    }
    for (int index = 0; index < 3; index++) {
        appendFloat(bytes, 0.0f);
    }
    appendFloat(bytes, 64.0f);
    appendPadded(bytes, "frame", 16);

    appendU32(bytes, 0x33504449);
    appendPadded(bytes, "body", 64);
    appendI32(bytes, 0);    // flags
    appendI32(bytes, 1);    // frames
    appendI32(bytes, 1);    // shaders
    appendI32(bytes, 3);    // vertices
    appendI32(bytes, 1);    // triangles
    appendI32(bytes, 176);  // triangle offset
    appendI32(bytes, 108);  // shader offset
    appendI32(bytes, 188);  // texture coordinate offset
    appendI32(bytes, 212);  // vertex offset
    appendI32(bytes, 236);  // end offset

    appendPadded(bytes, shader, 64);
    appendI32(bytes, 0);  // shader index

    appendI32(bytes, 0);
    appendI32(bytes, 1);
    appendI32(bytes, 2);

    const float coordinates[3][2] = {{0.0f, 0.0f}, {1.0f, 0.0f}, {0.0f, 1.0f}};
    for (const auto &coordinate : coordinates) {
        appendFloat(bytes, coordinate[0]);
        appendFloat(bytes, coordinate[1]);
    }

    const short positions[3][3] = {{-512, -512, -512}, {512, -512, -512}, {-512, 512, 512}};
    for (const auto &position : positions) {
        appendI16(bytes, position[0]);
        appendI16(bytes, position[1]);
        appendI16(bytes, position[2]);
        bytes.push_back(64);
        bytes.push_back(32);
    }
    return bytes;
}

std::string buildMd5(const std::string &shader) {
    return "MD5Version 10\n"
           "commandline \"\"\n"
           "numJoints 1\n"
           "numMeshes 1\n"
           "\n"
           "joints {\n"
           "\t\"origin\" -1 ( 0 0 0 ) ( 0 0 0 )\n"
           "}\n"
           "\n"
           "mesh {\n"
           "\tshader \"" + shader + "\"\n"
           "\tnumverts 3\n"
           "\tvert 0 ( 0 0 ) 0 1\n"
           "\tvert 1 ( 1 0 ) 1 1\n"
           "\tvert 2 ( 0 1 ) 2 1\n"
           "\tnumtris 1\n"
           "\ttri 0 0 1 2\n"
           "\tnumweights 3\n"
           "\tweight 0 0 1 ( 0 0 0 )\n"
           "\tweight 1 0 1 ( 16 0 0 )\n"
           "\tweight 2 0 1 ( 0 16 12 )\n"
           "}\n";
}

int brightPixelCount(const std::vector<unsigned char> &pixels) {
    int count = 0;
    for (size_t index = 0; index + 3 < pixels.size(); index += 4) {
        const int luminance = (pixels[index] + pixels[index + 1] + pixels[index + 2]) / 3;
        if (luminance > 90) {
            count++;
        }
    }
    return count;
}

std::vector<unsigned char> renderFrame(pkm_view *view, int width, int height) {
    std::vector<unsigned char> pixels(static_cast<size_t>(width) * static_cast<size_t>(height) * 4, 0);
    const int result = pkm_view_render(view, pixels.data(), width, height, width * 4);
    check(result == PKM_OK, "render succeeds");
    return pixels;
}

void settle(pkm_view *view) {
    for (int step = 0; step < 240; step++) {
        pkm_view_advance(view, 1.0 / 60.0);
    }
}

void testExtensions() {
    check(pkm_supports_extension("mdl") == 1, "mdl is supported");
    check(pkm_supports_extension(".MD3") == 1, "md3 is supported regardless of case");
    check(pkm_supports_extension("md5mesh") == 1, "md5mesh is supported");
    check(pkm_supports_extension("bsp") == 0, "bsp is not a model format");
    check(pkm_supports_extension(nullptr) == 0, "a missing extension is rejected");
}

void testRejectsBadInput() {
    char error[256]{};
    check(pkm_model_create(nullptr, 0, "mdl", error, sizeof(error)) == nullptr,
          "an empty buffer is rejected");

    const std::vector<unsigned char> mdl = buildMdl();
    for (size_t size = 1; size < mdl.size(); size += 7) {
        error[0] = '\0';
        pkm_model *model = pkm_model_create(mdl.data(), size, "mdl", error, sizeof(error));
        check(model == nullptr, "a truncated model is rejected");
        check(error[0] != '\0', "a truncated model reports a reason");
        pkm_model_destroy(model);
    }

    /* A header claiming far more geometry than the file holds must not allocate. */
    std::vector<unsigned char> hostile = mdl;
    hostile[60] = 0xFF;
    hostile[61] = 0xFF;
    hostile[62] = 0xFF;
    hostile[63] = 0x7F;
    check(pkm_model_create(hostile.data(), hostile.size(), "mdl", error, sizeof(error)) == nullptr,
          "an oversized vertex count is rejected");

    std::vector<unsigned char> wrongVersion = mdl;
    wrongVersion[4] = 3;
    check(pkm_model_create(wrongVersion.data(), wrongVersion.size(), "mdl", error, sizeof(error)) ==
              nullptr,
          "an unsupported MDL version is rejected");

    const std::string md5 = buildMd5("models/test");
    for (size_t size = 1; size < md5.size(); size += 11) {
        pkm_model *model = pkm_model_create(md5.data(), size, "md5mesh", error, sizeof(error));
        check(model == nullptr, "a truncated MD5 mesh is rejected");
        pkm_model_destroy(model);
    }
}

void testMdl() {
    const std::vector<unsigned char> bytes = buildMdl();
    char error[256]{};
    pkm_model *model = pkm_model_create(bytes.data(), bytes.size(), "mdl", error, sizeof(error));
    check(model != nullptr, "a valid MDL parses");
    if (model == nullptr) {
        return;
    }

    pkm_model_stats stats{};
    check(pkm_model_get_stats(model, &stats) == PKM_OK, "MDL stats are readable");
    check(stats.format == PKM_FORMAT_MDL, "the MDL format is reported");
    check(stats.surface_count == 1, "the MDL has one surface");
    check(stats.triangle_count == 1, "the MDL triangle count matches the header");
    check(stats.vertex_count == 3, "the MDL vertex count matches the triangle");
    check(stats.skin_count == 1, "the embedded MDL skin is decoded");
    check(stats.texture_request_count == 0, "MDL skins need nothing from the archive");
    check(stats.textured_surface_count == 1, "the MDL surface is textured");
    check(pkm_model_set_skin(model, 0) == PKM_OK, "the first skin can be selected");
    check(pkm_model_set_skin(model, 4) == PKM_ERROR_INVALID_ARGUMENT,
          "an out of range skin is rejected");

    pkm_view *view = pkm_view_create(model);
    check(view != nullptr, "a view is created");
    pkm_view_set_auto_rotate(view, 0);

    const std::vector<unsigned char> framed = renderFrame(view, 96, 96);
    check(brightPixelCount(framed) > 80, "the framed model covers a useful part of the view");
    check(pkm_view_show_interaction_prompt(view) == 1, "the orbit hint shows before interaction");

    pkm_view_begin_interaction(view);
    pkm_view_orbit(view, 40.0f, 12.0f);
    pkm_view_end_interaction(view);
    settle(view);
    const std::vector<unsigned char> orbited = renderFrame(view, 96, 96);
    check(orbited != framed, "orbiting changes the rendered frame");
    check(pkm_view_show_interaction_prompt(view) == 0, "the orbit hint clears after a drag");

    pkm_view_reset(view);
    settle(view);
    const std::vector<unsigned char> restored = renderFrame(view, 96, 96);
    check(restored == framed, "resetting returns the camera to the framed pose");
    check(pkm_view_advance(view, 1.0 / 60.0) == 0, "a settled idle view asks for no redraw");

    pkm_view_zoom(view, 40.0f);
    settle(view);
    const std::vector<unsigned char> zoomed = renderFrame(view, 96, 96);
    check(zoomed != restored, "zoom is clamped but still moves the camera");
    check(brightPixelCount(zoomed) > 0, "the model stays in frame at the zoom limit");

    check(pkm_view_render(view, nullptr, 96, 96, 96 * 4) == PKM_ERROR_INVALID_ARGUMENT,
          "rendering without a buffer is rejected");
    std::vector<unsigned char> pixels(96 * 96 * 4, 0);
    check(pkm_view_render(view, pixels.data(), 96, 96, 32) == PKM_ERROR_INVALID_ARGUMENT,
          "a stride shorter than one row is rejected");

    pkm_view_destroy(view);
    pkm_model_destroy(model);
}

void testMd3() {
    const std::vector<unsigned char> bytes = buildMd3("models/test/body.tga");
    char error[256]{};
    pkm_model *model = pkm_model_create(bytes.data(), bytes.size(), "md3", error, sizeof(error));
    check(model != nullptr, "a valid MD3 parses");
    if (model == nullptr) {
        std::fprintf(stderr, "  md3 error: %s\n", error);
        return;
    }

    pkm_model_stats stats{};
    pkm_model_get_stats(model, &stats);
    check(stats.format == PKM_FORMAT_MD3, "the MD3 format is reported");
    check(stats.surface_count == 1, "the MD3 has one surface");
    check(stats.triangle_count == 1, "the MD3 triangle count matches the surface");
    check(stats.texture_request_count == 1, "the MD3 shader becomes a texture request");
    check(stats.textured_surface_count == 0, "an unresolved request leaves the surface untextured");

    char name[128]{};
    check(pkm_model_texture_request_name(model, 0, name, sizeof(name)) == PKM_OK,
          "the request name is readable");
    check(std::string(name) == "models/test/body.tga", "the request keeps the shader path");
    check(pkm_model_texture_request_surface(model, 0, name, sizeof(name)) == PKM_OK,
          "the request surface name is readable");
    check(std::string(name) == "body", "the request names the surface for skin files");
    check(pkm_model_texture_request_name(model, 3, name, sizeof(name)) ==
              PKM_ERROR_INVALID_ARGUMENT,
          "an out of range request is rejected");

    const std::vector<unsigned char> texture(8 * 8 * 4, 220);
    check(pkm_model_set_texture(model, 0, texture.data(), 8, 8) == PKM_OK,
          "a resolved texture is accepted");
    check(pkm_model_set_texture(model, 0, texture.data(), 0, 8) == PKM_ERROR_INVALID_ARGUMENT,
          "an empty texture is rejected");
    pkm_model_get_stats(model, &stats);
    check(stats.textured_surface_count == 1, "the resolved texture is applied to the surface");

    pkm_view *view = pkm_view_create(model);
    pkm_view_set_auto_rotate(view, 0);
    const std::vector<unsigned char> pixels = renderFrame(view, 96, 96);
    check(brightPixelCount(pixels) > 80, "the MD3 surface renders");

    pkm_view_destroy(view);
    pkm_model_destroy(model);
}

void testMd5() {
    const std::string mesh = buildMd5("models/test/body");
    char error[256]{};
    pkm_model *model = pkm_model_create(mesh.data(), mesh.size(), "md5mesh", error, sizeof(error));
    check(model != nullptr, "a valid MD5 mesh parses");
    if (model == nullptr) {
        std::fprintf(stderr, "  md5 error: %s\n", error);
        return;
    }

    pkm_model_stats stats{};
    pkm_model_get_stats(model, &stats);
    check(stats.format == PKM_FORMAT_MD5, "the MD5 format is reported");
    check(stats.surface_count == 1, "the MD5 has one mesh");
    check(stats.vertex_count == 3, "the MD5 vertex count matches the mesh");
    check(stats.triangle_count == 1, "the MD5 triangle count matches the mesh");
    check(stats.texture_request_count == 1, "the MD5 shader becomes a texture request");

    char name[128]{};
    pkm_model_texture_request_name(model, 0, name, sizeof(name));
    check(std::string(name) == "models/test/body", "the MD5 request keeps the shader path");

    pkm_view *view = pkm_view_create(model);
    pkm_view_set_auto_rotate(view, 0);
    const std::vector<unsigned char> dark = renderFrame(view, 96, 96);
    check(brightPixelCount(dark) > 80, "the MD5 bind pose renders");

    pkm_view_set_dark_background(view, 0);
    settle(view);
    const std::vector<unsigned char> light = renderFrame(view, 96, 96);
    check(light != dark, "the backdrop follows the host theme");

    pkm_view_destroy(view);
    pkm_model_destroy(model);
}

void testAutoRotate() {
    const std::vector<unsigned char> bytes = buildMdl();
    pkm_model *model = pkm_model_create(bytes.data(), bytes.size(), "mdl", nullptr, 0);
    check(model != nullptr, "the model for the auto-rotate test parses");
    if (model == nullptr) {
        return;
    }

    pkm_view *view = pkm_view_create(model);
    settle(view);
    const std::vector<unsigned char> before = renderFrame(view, 64, 64);
    for (int step = 0; step < 240; step++) {
        pkm_view_advance(view, 1.0 / 60.0);
    }
    const std::vector<unsigned char> after = renderFrame(view, 64, 64);
    check(after != before, "an idle view drifts into its turntable");

    pkm_view_set_auto_rotate(view, 0);
    settle(view);
    const std::vector<unsigned char> stopped = renderFrame(view, 64, 64);
    for (int step = 0; step < 240; step++) {
        check(pkm_view_advance(view, 1.0 / 60.0) == 0,
              "an idle view without auto-rotate asks for no redraw");
    }
    check(renderFrame(view, 64, 64) == stopped, "auto-rotate can be turned off");

    pkm_view_destroy(view);
    pkm_model_destroy(model);
}

}  // namespace

int main() {
    testExtensions();
    testRejectsBadInput();
    testMdl();
    testMd3();
    testMd5();
    testAutoRotate();

    if (failures > 0) {
        std::fprintf(stderr, "%d model API checks failed\n", failures);
        return 1;
    }
    return 0;
}
