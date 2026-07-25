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

/*
 * A sprite whose frames differ in size, so the tests cover the flipbook and the
 * union the camera is framed against. groupMembers of zero writes single frames.
 */
std::vector<unsigned char> buildSpr(int frames, int groupMembers, int version = 1) {
    std::vector<unsigned char> bytes;

    appendU32(bytes, 0x50534449);  // "IDSP"
    appendI32(bytes, version);
    appendI32(bytes, 2);         // view parallel
    appendFloat(bytes, 32.0f);   // bounding radius
    appendI32(bytes, 32);        // canvas width
    appendI32(bytes, 32);        // canvas height
    appendI32(bytes, frames);
    appendFloat(bytes, 0.0f);  // beam length
    appendI32(bytes, 0);       // sync type

    const auto appendFrame = [&bytes, version](int size) {
        appendI32(bytes, -size / 2);  // origin x
        appendI32(bytes, size / 2);   // origin y
        appendI32(bytes, size);
        appendI32(bytes, size);
        for (int index = 0; index < size * size; index++) {
            if (version == 32) {
                bytes.push_back(240);
                bytes.push_back(230);
                bytes.push_back(210);
                bytes.push_back(255);
            } else {
                /* A bright palette entry, with a transparent border to test the cutout. */
                const bool border = index < size || index >= size * (size - 1);
                bytes.push_back(border ? 255 : 15);
            }
        }
    };

    for (int frame = 0; frame < frames; frame++) {
        if (groupMembers <= 0) {
            appendI32(bytes, 0);  // SPR_SINGLE
            appendFrame(16 + frame * 8);
            continue;
        }

        appendI32(bytes, 1);  // SPR_GROUP
        appendI32(bytes, groupMembers);
        for (int member = 0; member < groupMembers; member++) {
            appendFloat(bytes, 0.05f);
        }
        for (int member = 0; member < groupMembers; member++) {
            appendFrame(16 + member * 8);
        }
    }
    return bytes;
}

/*
 * A one-brush BSP: a textured cube built the way qbsp writes a brush model, so the
 * tests cover the surfedge walk, the texture axes, and the brush-model check. Pass
 * entities to make it look like a level instead.
 */
std::vector<unsigned char> buildBsp(const std::string &entities, bool visibility = false,
                                    int modelCount = 1) {
    constexpr int textureSize = 8;
    constexpr int lumpCount = 15;

    const float corners[8][3] = {
        {-16.0f, -16.0f, 0.0f}, {16.0f, -16.0f, 0.0f}, {16.0f, 16.0f, 0.0f},
        {-16.0f, 16.0f, 0.0f},  {-16.0f, -16.0f, 32.0f}, {16.0f, -16.0f, 32.0f},
        {16.0f, 16.0f, 32.0f},  {-16.0f, 16.0f, 32.0f},
    };
    /* Six quads, each a loop of four edges, in the winding qbsp emits. */
    const int quads[6][4] = {
        {0, 1, 2, 3},  // bottom
        {7, 6, 5, 4},  // top
        {0, 4, 5, 1},  // -y
        {2, 6, 7, 3},  // +y
        {1, 5, 6, 2},  // +x
        {3, 7, 4, 0},  // -x
    };
    const float normals[6][3] = {
        {0.0f, 0.0f, -1.0f}, {0.0f, 0.0f, 1.0f}, {0.0f, -1.0f, 0.0f},
        {0.0f, 1.0f, 0.0f},  {1.0f, 0.0f, 0.0f}, {-1.0f, 0.0f, 0.0f},
    };

    std::vector<unsigned char> planes;
    for (const auto &normal : normals) {
        appendFloat(planes, normal[0]);
        appendFloat(planes, normal[1]);
        appendFloat(planes, normal[2]);
        appendFloat(planes, 16.0f);  // distance
        appendI32(planes, 0);        // plane type
    }

    std::vector<unsigned char> vertexes;
    for (const auto &corner : corners) {
        appendFloat(vertexes, corner[0]);
        appendFloat(vertexes, corner[1]);
        appendFloat(vertexes, corner[2]);
    }

    /* One edge per quad corner, walked forwards through the surfedge list. */
    std::vector<unsigned char> edges;
    std::vector<unsigned char> surfedges;
    appendI16(edges, 0);  // edge zero is unused, as in a real BSP
    appendI16(edges, 0);
    int nextEdge = 1;
    for (const auto &quad : quads) {
        for (int corner = 0; corner < 4; corner++) {
            appendI16(edges, static_cast<std::int16_t>(quad[corner]));
            appendI16(edges, static_cast<std::int16_t>(quad[(corner + 1) % 4]));
            appendI32(surfedges, nextEdge++);
        }
    }

    std::vector<unsigned char> texinfo;
    appendFloat(texinfo, 1.0f);  // s axis
    appendFloat(texinfo, 0.0f);
    appendFloat(texinfo, 0.0f);
    appendFloat(texinfo, 0.0f);
    appendFloat(texinfo, 0.0f);  // t axis
    appendFloat(texinfo, 0.0f);
    appendFloat(texinfo, -1.0f);
    appendFloat(texinfo, 0.0f);
    appendI32(texinfo, 0);  // miptex
    appendI32(texinfo, 0);  // flags

    std::vector<unsigned char> faces;
    for (int face = 0; face < 6; face++) {
        appendI16(faces, static_cast<std::int16_t>(face));      // plane
        appendI16(faces, 0);                                    // side
        appendI32(faces, face * 4);                             // first surfedge
        appendI16(faces, 4);                                    // edges
        appendI16(faces, 0);                                    // texinfo
        for (int style = 0; style < 4; style++) {
            faces.push_back(255);
        }
        appendI32(faces, -1);  // no lightmap
    }

    std::vector<unsigned char> textures;
    appendI32(textures, 1);
    appendI32(textures, 8);  // offset of the one miptex, from the lump
    appendPadded(textures, "crate_top", 16);
    appendI32(textures, textureSize);
    appendI32(textures, textureSize);
    appendI32(textures, 40);  // pixels follow the four mip offsets
    for (int mip = 1; mip < 4; mip++) {
        appendI32(textures, 0);
    }
    for (int index = 0; index < textureSize * textureSize; index++) {
        /* Includes index 255, which a BSP treats as an ordinary colour. */
        textures.push_back(index == 0 ? 255 : 15);
    }

    std::vector<unsigned char> models;
    for (int model = 0; model < modelCount; model++) {
        for (int component = 0; component < 3; component++) {
            appendFloat(models, -16.0f);  // mins
        }
        for (int component = 0; component < 3; component++) {
            appendFloat(models, 32.0f);  // maxs
        }
        for (int component = 0; component < 3; component++) {
            appendFloat(models, 0.0f);  // origin
        }
        for (int hull = 0; hull < 4; hull++) {
            appendI32(models, 0);
        }
        appendI32(models, 1);  // visleafs
        appendI32(models, 0);  // first face
        appendI32(models, 6);  // faces
    }

    std::vector<unsigned char> entityBytes(entities.begin(), entities.end());
    entityBytes.push_back(0);
    const std::vector<unsigned char> visibilityBytes(visibility ? 64 : 0, 0);
    const std::vector<unsigned char> empty;

    /* Lumps in the order the header lists them. */
    const std::vector<unsigned char> *lumps[lumpCount] = {
        &entityBytes, &planes, &textures, &vertexes, &visibilityBytes, &empty, &texinfo,
        &faces,       &empty,  &empty,    &empty,    &empty,           &edges, &surfedges,
        &models,
    };

    std::vector<unsigned char> bytes;
    appendI32(bytes, 29);
    size_t cursor = 4 + lumpCount * 8;
    for (const std::vector<unsigned char> *lump : lumps) {
        appendI32(bytes, static_cast<std::int32_t>(cursor));
        appendI32(bytes, static_cast<std::int32_t>(lump->size()));
        cursor += lump->size();
    }
    for (const std::vector<unsigned char> *lump : lumps) {
        bytes.insert(bytes.end(), lump->begin(), lump->end());
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
    check(pkm_supports_extension("spr") == 1, "spr is supported");
    check(pkm_supports_extension(".SPR32") == 1, "spr32 is supported regardless of case");
    /* BSP covers both brush models and levels, so the data decides which is which. */
    check(pkm_supports_extension("bsp") == 1, "bsp is supported");
    check(pkm_supports_extension("wad") == 0, "wad is not a model format");
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

void testSpr() {
    const std::vector<unsigned char> bytes = buildSpr(3, 0);
    char error[256]{};
    pkm_model *model = pkm_model_create(bytes.data(), bytes.size(), "spr", error, sizeof(error));
    check(model != nullptr, "a valid sprite parses");
    if (model == nullptr) {
        std::fprintf(stderr, "  spr error: %s\n", error);
        return;
    }

    pkm_model_stats stats{};
    pkm_model_get_stats(model, &stats);
    check(stats.format == PKM_FORMAT_SPR, "the sprite format is reported");
    check(stats.frame_count == 3, "every sprite frame is counted");
    check(stats.surface_count == 1, "a sprite draws one quad at a time");
    check(stats.triangle_count == 2, "the sprite quad is two triangles");
    check(stats.skin_count == 0, "sprite frames are not offered as skins");
    check(stats.texture_request_count == 0, "sprite frames need nothing from the archive");
    check(stats.textured_surface_count == 1, "the playing frame is textured");
    check(pkm_model_set_skin(model, 0) == PKM_ERROR_INVALID_ARGUMENT,
          "a sprite has no skins to select");

    pkm_view *view = pkm_view_create(model);
    check(view != nullptr, "a sprite view is created");

    check(brightPixelCount(renderFrame(view, 96, 96)) > 80, "the sprite frame renders");

    /* Frames differ in size, so stepping the flipbook has to change the image. */
    settle(view);
    const std::vector<unsigned char> playing = renderFrame(view, 96, 96);
    int redraws = 0;
    for (int step = 0; step < 12; step++) {
        redraws += pkm_view_advance(view, 1.0 / 60.0);
    }
    check(redraws > 0, "sprite playback asks for a redraw while the camera sits still");
    const std::vector<unsigned char> stepped = renderFrame(view, 96, 96);
    check(stepped != playing, "the next sprite frame is drawn");

    /* Three frames at a tenth of a second each come back around. */
    for (int step = 0; step < 36; step++) {
        pkm_view_advance(view, 1.0 / 120.0);
    }
    check(renderFrame(view, 96, 96) == stepped, "sprite playback loops back around");

    pkm_view_destroy(view);
    pkm_model_destroy(model);

    /* Groups carry their own intervals, and every member is one more frame. */
    const std::vector<unsigned char> grouped = buildSpr(2, 4);
    pkm_model *groupModel =
        pkm_model_create(grouped.data(), grouped.size(), "spr", error, sizeof(error));
    check(groupModel != nullptr, "a grouped sprite parses");
    if (groupModel != nullptr) {
        pkm_model_get_stats(groupModel, &stats);
        check(stats.frame_count == 8, "group members are counted as frames");
        pkm_model_destroy(groupModel);
    }

    /* A sprite with one frame has nothing to play, so it must idle like a mesh. */
    const std::vector<unsigned char> still = buildSpr(1, 0);
    pkm_model *stillModel =
        pkm_model_create(still.data(), still.size(), "spr", error, sizeof(error));
    check(stillModel != nullptr, "a one frame sprite parses");
    if (stillModel != nullptr) {
        pkm_view *stillView = pkm_view_create(stillModel);
        settle(stillView);
        renderFrame(stillView, 64, 64);
        check(pkm_view_advance(stillView, 1.0 / 60.0) == 0,
              "a one frame sprite asks for no redraw");
        pkm_view_destroy(stillView);
        pkm_model_destroy(stillModel);
    }

    const std::vector<unsigned char> spr32 = buildSpr(1, 0, 32);
    pkm_model *rgbaModel =
        pkm_model_create(spr32.data(), spr32.size(), "spr32", error, sizeof(error));
    check(rgbaModel != nullptr, "an SPR32 sprite parses");
    if (rgbaModel != nullptr) {
        pkm_view *rgbaView = pkm_view_create(rgbaModel);
        check(brightPixelCount(renderFrame(rgbaView, 96, 96)) > 80, "the RGBA sprite renders");
        pkm_view_destroy(rgbaView);
        pkm_model_destroy(rgbaModel);
    }

    std::vector<unsigned char> halfLife = bytes;
    halfLife[4] = 2;
    check(pkm_model_create(halfLife.data(), halfLife.size(), "spr", error, sizeof(error)) == nullptr,
          "a Half-Life sprite is rejected");
    check(std::string(error).find("Half-Life") != std::string::npos,
          "the Half-Life sprite error names the format");

    for (size_t size = 1; size < bytes.size(); size += 5) {
        error[0] = '\0';
        pkm_model *truncated =
            pkm_model_create(bytes.data(), size, "spr", error, sizeof(error));
        check(truncated == nullptr, "a truncated sprite is rejected");
        check(error[0] != '\0', "a truncated sprite reports a reason");
        pkm_model_destroy(truncated);
    }

    /* A frame count far past what the file holds must be caught before allocating. */
    std::vector<unsigned char> hostile = bytes;
    hostile[24] = 0xFF;
    hostile[25] = 0xFF;
    hostile[26] = 0xFF;
    hostile[27] = 0x7F;
    check(pkm_model_create(hostile.data(), hostile.size(), "spr", error, sizeof(error)) == nullptr,
          "an oversized sprite frame count is rejected");
}

void testBsp() {
    const std::string ammoBox = "{\n\"wad\" \"gfx/items.wad\"\n\"classname\" \"worldspawn\"\n}\n";
    const std::vector<unsigned char> bytes = buildBsp(ammoBox);

    check(pkm_bsp_is_brush_model(bytes.data(), bytes.size()) == 1,
          "a hull with no spawn point and no vis is a brush model");
    check(pkm_bsp_is_brush_model(nullptr, 0) == 0, "an empty buffer is not a brush model");
    check(pkm_bsp_is_brush_model(bytes.data(), 16) == 0, "a truncated header is not a brush model");

    /* The three things that tell a level apart from a prop. */
    const std::vector<unsigned char> level = buildBsp(
        ammoBox + "{\n\"classname\" \"info_player_start\"\n\"origin\" \"0 0 24\"\n}\n");
    check(pkm_bsp_is_brush_model(level.data(), level.size()) == 0,
          "a BSP with a player start is a level");
    const std::vector<unsigned char> mixedCase =
        buildBsp(ammoBox + "{\n\"classname\" \"Info_Player_Deathmatch\"\n}\n");
    check(pkm_bsp_is_brush_model(mixedCase.data(), mixedCase.size()) == 0,
          "the spawn point check ignores case");
    const std::vector<unsigned char> vised = buildBsp(ammoBox, /*visibility=*/true);
    check(pkm_bsp_is_brush_model(vised.data(), vised.size()) == 0,
          "a BSP carrying visibility data is a level");
    const std::vector<unsigned char> submodels = buildBsp(ammoBox, false, /*modelCount=*/3);
    check(pkm_bsp_is_brush_model(submodels.data(), submodels.size()) == 0,
          "a BSP with submodels is a level");

    char error[256]{};
    pkm_model *model = pkm_model_create(bytes.data(), bytes.size(), "bsp", error, sizeof(error));
    check(model != nullptr, "a brush model parses");
    if (model == nullptr) {
        std::fprintf(stderr, "  bsp error: %s\n", error);
        return;
    }

    pkm_model_stats stats{};
    pkm_model_get_stats(model, &stats);
    check(stats.format == PKM_FORMAT_BSP, "the BSP format is reported");
    check(stats.surface_count == 1, "faces sharing a texture become one surface");
    check(stats.triangle_count == 12, "each of the six quads becomes two triangles");
    check(stats.vertex_count == 24, "each face keeps its own corners");
    check(stats.frame_count == 1, "a brush model has one pose");
    check(stats.skin_count == 0, "brush model textures are not offered as skins");
    check(stats.texture_request_count == 0, "BSP textures come from the file itself");
    check(stats.textured_surface_count == 1, "the embedded texture is applied");

    pkm_view *view = pkm_view_create(model);
    pkm_view_set_auto_rotate(view, 0);
    settle(view);
    const std::vector<unsigned char> framed = renderFrame(view, 96, 96);
    check(brightPixelCount(framed) > 80, "the brush model renders");

    /* Index 255 is a solid colour in a BSP, so no face may be punched through. */
    pkm_view_begin_interaction(view);
    pkm_view_orbit(view, 20.0f, 40.0f);
    pkm_view_end_interaction(view);
    settle(view);
    check(brightPixelCount(renderFrame(view, 96, 96)) > 80, "the brush model renders from above");

    pkm_view_destroy(view);
    pkm_model_destroy(model);

    for (size_t size = 1; size < bytes.size(); size += 37) {
        error[0] = '\0';
        pkm_model *truncated = pkm_model_create(bytes.data(), size, "bsp", error, sizeof(error));
        check(truncated == nullptr, "a truncated BSP is rejected");
        check(error[0] != '\0', "a truncated BSP reports a reason");
        pkm_model_destroy(truncated);
    }

    std::vector<unsigned char> wrongVersion = bytes;
    wrongVersion[0] = 30;
    check(pkm_model_create(wrongVersion.data(), wrongVersion.size(), "bsp", error, sizeof(error)) ==
              nullptr,
          "a GoldSrc BSP is rejected");
    check(pkm_bsp_is_brush_model(wrongVersion.data(), wrongVersion.size()) == 0,
          "a GoldSrc BSP is not offered to the viewer");
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
    testSpr();
    testBsp();
    testAutoRotate();

    if (failures > 0) {
        std::fprintf(stderr, "%d model API checks failed\n", failures);
        return 1;
    }
    return 0;
}
