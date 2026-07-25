#include "pakscape_model.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <new>
#include <string>
#include <thread>
#include <vector>

namespace {

constexpr float kPi = 3.14159265358979323846f;

/* Caps keep a hostile or corrupt archive entry from allocating the machine. */
constexpr int kMaxSurfaces = 1024;
constexpr int kMaxVertices = 1'000'000;
constexpr int kMaxTriangles = 1'000'000;
constexpr int kMaxFrames = 4096;
constexpr int kMaxSkins = 256;
constexpr int kMaxTextureDimension = 8192;
constexpr int kMaxJoints = 4096;
constexpr int kMaxWeights = 4'000'000;
constexpr int kMaxRenderDimension = 4096;
constexpr int kMaxRenderPixels = 6'000'000;
/* Every sprite frame is decoded up front, so the whole flipbook shares a budget. */
constexpr int kMaxSpritePixels = 32'000'000;

/* Sprites that store no intervals animate at the rate Quake plays them. */
constexpr float kDefaultSpriteInterval = 0.1f;
constexpr float kMinSpriteInterval = 0.02f;
constexpr float kMaxSpriteInterval = 5.0f;

constexpr float kFieldOfView = 32.0f * kPi / 180.0f;
constexpr float kDefaultYaw = 32.0f * kPi / 180.0f;
constexpr float kDefaultPitch = 15.0f * kPi / 180.0f;
constexpr float kMaxPitch = 88.0f * kPi / 180.0f;
constexpr float kAutoRotateDelay = 2.75f;
constexpr float kAutoRotateSpeed = 0.32f;

/* The id1 palette, matching the one the 2D preview decoders already use. */
const unsigned char kQuakePalette[768] = {
    0x00, 0x00, 0x00, 0x0f, 0x0f, 0x0f, 0x1f, 0x1f, 0x1f, 0x2f, 0x2f, 0x2f,
    0x3f, 0x3f, 0x3f, 0x4b, 0x4b, 0x4b, 0x5b, 0x5b, 0x5b, 0x6b, 0x6b, 0x6b,
    0x7b, 0x7b, 0x7b, 0x8b, 0x8b, 0x8b, 0x9b, 0x9b, 0x9b, 0xab, 0xab, 0xab,
    0xbb, 0xbb, 0xbb, 0xcb, 0xcb, 0xcb, 0xdb, 0xdb, 0xdb, 0xeb, 0xeb, 0xeb,
    0x0f, 0x0b, 0x07, 0x17, 0x0f, 0x0b, 0x1f, 0x17, 0x0b, 0x27, 0x1b, 0x0f,
    0x2f, 0x23, 0x13, 0x37, 0x2b, 0x17, 0x3f, 0x2f, 0x17, 0x4b, 0x37, 0x1b,
    0x53, 0x3b, 0x1b, 0x5b, 0x43, 0x1f, 0x63, 0x4b, 0x1f, 0x6b, 0x53, 0x1f,
    0x73, 0x57, 0x1f, 0x7b, 0x5f, 0x23, 0x83, 0x67, 0x23, 0x8f, 0x6f, 0x23,
    0x0b, 0x0b, 0x0f, 0x13, 0x13, 0x1b, 0x1b, 0x1b, 0x27, 0x27, 0x27, 0x33,
    0x2f, 0x2f, 0x3f, 0x37, 0x37, 0x4b, 0x3f, 0x3f, 0x57, 0x47, 0x47, 0x67,
    0x4f, 0x4f, 0x73, 0x5b, 0x5b, 0x7f, 0x63, 0x63, 0x8b, 0x6b, 0x6b, 0x97,
    0x73, 0x73, 0xa3, 0x7b, 0x7b, 0xaf, 0x83, 0x83, 0xbb, 0x8b, 0x8b, 0xcb,
    0x00, 0x00, 0x00, 0x07, 0x07, 0x00, 0x0b, 0x0b, 0x00, 0x13, 0x13, 0x00,
    0x1b, 0x1b, 0x00, 0x23, 0x23, 0x00, 0x2b, 0x2b, 0x07, 0x2f, 0x2f, 0x07,
    0x37, 0x37, 0x07, 0x3f, 0x3f, 0x07, 0x47, 0x47, 0x07, 0x4b, 0x4b, 0x0b,
    0x53, 0x53, 0x0b, 0x5b, 0x5b, 0x0b, 0x63, 0x63, 0x0b, 0x6b, 0x6b, 0x0f,
    0x07, 0x00, 0x00, 0x0f, 0x00, 0x00, 0x17, 0x00, 0x00, 0x1f, 0x00, 0x00,
    0x27, 0x00, 0x00, 0x2f, 0x00, 0x00, 0x37, 0x00, 0x00, 0x3f, 0x00, 0x00,
    0x47, 0x00, 0x00, 0x4f, 0x00, 0x00, 0x57, 0x00, 0x00, 0x5f, 0x00, 0x00,
    0x67, 0x00, 0x00, 0x6f, 0x00, 0x00, 0x77, 0x00, 0x00, 0x7f, 0x00, 0x00,
    0x13, 0x13, 0x00, 0x1b, 0x1b, 0x00, 0x23, 0x23, 0x00, 0x2f, 0x2b, 0x00,
    0x37, 0x2f, 0x00, 0x43, 0x37, 0x00, 0x4b, 0x3b, 0x07, 0x57, 0x43, 0x07,
    0x5f, 0x47, 0x07, 0x6b, 0x4b, 0x0b, 0x77, 0x53, 0x0f, 0x83, 0x57, 0x13,
    0x8b, 0x5b, 0x13, 0x97, 0x5f, 0x1b, 0xa3, 0x63, 0x1f, 0xaf, 0x67, 0x23,
    0x23, 0x13, 0x07, 0x2f, 0x17, 0x0b, 0x3b, 0x1f, 0x0f, 0x4b, 0x23, 0x13,
    0x57, 0x2b, 0x17, 0x63, 0x2f, 0x1f, 0x73, 0x37, 0x23, 0x7f, 0x3b, 0x2b,
    0x8f, 0x43, 0x33, 0x9f, 0x4f, 0x33, 0xaf, 0x63, 0x2f, 0xbf, 0x77, 0x2f,
    0xcf, 0x8f, 0x2b, 0xdf, 0xab, 0x27, 0xef, 0xcb, 0x1f, 0xff, 0xf3, 0x1b,
    0x0b, 0x07, 0x00, 0x1b, 0x13, 0x00, 0x2b, 0x23, 0x0f, 0x37, 0x2b, 0x13,
    0x47, 0x33, 0x1b, 0x53, 0x37, 0x23, 0x63, 0x3f, 0x2b, 0x6f, 0x47, 0x33,
    0x7f, 0x53, 0x3f, 0x8b, 0x5f, 0x47, 0x9b, 0x6b, 0x53, 0xa7, 0x7b, 0x5f,
    0xb7, 0x87, 0x6b, 0xc3, 0x93, 0x7b, 0xd3, 0xa3, 0x8b, 0xe3, 0xb3, 0x97,
    0xab, 0x8b, 0xa3, 0x9f, 0x7f, 0x97, 0x93, 0x73, 0x87, 0x8b, 0x67, 0x7b,
    0x7f, 0x5b, 0x6f, 0x77, 0x53, 0x63, 0x6b, 0x4b, 0x57, 0x5f, 0x3f, 0x4b,
    0x57, 0x37, 0x43, 0x4b, 0x2f, 0x37, 0x43, 0x27, 0x2f, 0x37, 0x1f, 0x23,
    0x2b, 0x17, 0x1b, 0x23, 0x13, 0x13, 0x17, 0x0b, 0x0b, 0x0f, 0x07, 0x07,
    0xbb, 0x73, 0x9f, 0xaf, 0x6b, 0x8f, 0xa3, 0x5f, 0x83, 0x97, 0x57, 0x77,
    0x8b, 0x4f, 0x6b, 0x7f, 0x4b, 0x5f, 0x73, 0x43, 0x53, 0x6b, 0x3b, 0x4b,
    0x5f, 0x33, 0x3f, 0x53, 0x2b, 0x37, 0x47, 0x23, 0x2b, 0x3b, 0x1f, 0x23,
    0x2f, 0x17, 0x1b, 0x23, 0x13, 0x13, 0x17, 0x0b, 0x0b, 0x0f, 0x07, 0x07,
    0xdb, 0xc3, 0xbb, 0xcb, 0xb3, 0xa7, 0xbf, 0xa3, 0x9b, 0xaf, 0x97, 0x8b,
    0xa3, 0x87, 0x7b, 0x97, 0x7b, 0x6f, 0x87, 0x6f, 0x5f, 0x7b, 0x63, 0x53,
    0x6b, 0x57, 0x47, 0x5f, 0x4b, 0x3b, 0x53, 0x3f, 0x33, 0x43, 0x33, 0x27,
    0x37, 0x2b, 0x1f, 0x27, 0x1f, 0x17, 0x1b, 0x13, 0x0f, 0x0f, 0x0b, 0x07,
    0x6f, 0x83, 0x7b, 0x67, 0x7b, 0x6f, 0x5f, 0x73, 0x67, 0x57, 0x6b, 0x5f,
    0x4f, 0x63, 0x57, 0x47, 0x5b, 0x4f, 0x3f, 0x53, 0x47, 0x37, 0x4b, 0x3f,
    0x2f, 0x43, 0x37, 0x2b, 0x3b, 0x2f, 0x23, 0x33, 0x27, 0x1f, 0x2b, 0x1f,
    0x17, 0x23, 0x17, 0x0f, 0x1b, 0x13, 0x0b, 0x13, 0x0b, 0x07, 0x0b, 0x07,
    0xff, 0xf3, 0x1b, 0xef, 0xdf, 0x17, 0xdb, 0xcb, 0x13, 0xcb, 0xb7, 0x0f,
    0xbb, 0xa7, 0x0f, 0xab, 0x97, 0x0b, 0x9b, 0x83, 0x07, 0x8b, 0x73, 0x07,
    0x7b, 0x63, 0x07, 0x6b, 0x53, 0x00, 0x5b, 0x47, 0x00, 0x4b, 0x37, 0x00,
    0x3b, 0x2b, 0x00, 0x2b, 0x1f, 0x00, 0x1b, 0x0f, 0x00, 0x0b, 0x07, 0x00,
    0x00, 0x00, 0xff, 0x0b, 0x0b, 0xef, 0x13, 0x13, 0xdf, 0x1b, 0x1b, 0xcf,
    0x23, 0x23, 0xbf, 0x2b, 0x2b, 0xaf, 0x2f, 0x2f, 0x9f, 0x2f, 0x2f, 0x8f,
    0x2f, 0x2f, 0x7f, 0x2f, 0x2f, 0x6f, 0x2f, 0x2f, 0x5f, 0x2b, 0x2b, 0x4f,
    0x23, 0x23, 0x3f, 0x1b, 0x1b, 0x2f, 0x13, 0x13, 0x1f, 0x0b, 0x0b, 0x0f,
    0x2b, 0x00, 0x00, 0x3b, 0x00, 0x00, 0x4b, 0x07, 0x00, 0x5f, 0x07, 0x00,
    0x6f, 0x0f, 0x00, 0x7f, 0x17, 0x07, 0x93, 0x1f, 0x07, 0xa3, 0x27, 0x0b,
    0xb7, 0x33, 0x0f, 0xc3, 0x4b, 0x1b, 0xcf, 0x63, 0x2b, 0xdb, 0x7f, 0x3b,
    0xe3, 0x97, 0x4f, 0xe7, 0xab, 0x5f, 0xef, 0xbf, 0x77, 0xf7, 0xd3, 0x8b,
    0xa7, 0x7b, 0x3b, 0xb7, 0x9b, 0x37, 0xc7, 0xc3, 0x37, 0xe7, 0xe3, 0x57,
    0x7f, 0xbf, 0xff, 0xab, 0xe7, 0xff, 0xd7, 0xff, 0xff, 0x67, 0x00, 0x00,
    0x8b, 0x00, 0x00, 0xb3, 0x00, 0x00, 0xd7, 0x00, 0x00, 0xff, 0x00, 0x00,
    0xff, 0xf3, 0x93, 0xff, 0xf7, 0xc7, 0xff, 0xff, 0xff, 0x9f, 0x5b, 0x53,
};

struct Vec3 {
    float x = 0.0f;
    float y = 0.0f;
    float z = 0.0f;
};

Vec3 operator+(const Vec3 &a, const Vec3 &b) { return {a.x + b.x, a.y + b.y, a.z + b.z}; }
Vec3 operator-(const Vec3 &a, const Vec3 &b) { return {a.x - b.x, a.y - b.y, a.z - b.z}; }
Vec3 operator*(const Vec3 &a, float s) { return {a.x * s, a.y * s, a.z * s}; }

float dot(const Vec3 &a, const Vec3 &b) { return a.x * b.x + a.y * b.y + a.z * b.z; }

Vec3 cross(const Vec3 &a, const Vec3 &b) {
    return {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x};
}

Vec3 normalize(const Vec3 &v) {
    const float length = std::sqrt(dot(v, v));
    if (length <= 1e-8f) {
        return {0.0f, 0.0f, 1.0f};
    }
    return v * (1.0f / length);
}

float clampf(float value, float low, float high) {
    return value < low ? low : (value > high ? high : value);
}

struct Texture {
    int width = 0;
    int height = 0;
    bool smooth = false;
    std::vector<unsigned char> rgba;

    bool valid() const { return width > 0 && height > 0 && !rgba.empty(); }
};

struct Vertex {
    Vec3 position;
    Vec3 normal;
    float u = 0.0f;
    float v = 0.0f;
};

struct Surface {
    std::string name;
    std::vector<Vertex> vertices;
    std::vector<int> indices;
    int texture = -1;
    /* Sprites are fullbright in Quake, so the studio rig must not touch them. */
    bool unlit = false;
};

/* Bounds-checked little-endian access into the untrusted file buffer. */
class Reader {
public:
    Reader(const unsigned char *data, size_t size) : data_(data), size_(size) {}

    size_t size() const { return size_; }
    const unsigned char *data() const { return data_; }

    bool has(size_t offset, size_t count) const {
        return offset <= size_ && size_ - offset >= count;
    }

    bool int32(size_t offset, int &value) const {
        if (!has(offset, 4)) {
            return false;
        }
        const std::uint32_t bits = static_cast<std::uint32_t>(data_[offset]) |
                                   (static_cast<std::uint32_t>(data_[offset + 1]) << 8) |
                                   (static_cast<std::uint32_t>(data_[offset + 2]) << 16) |
                                   (static_cast<std::uint32_t>(data_[offset + 3]) << 24);
        value = static_cast<int>(static_cast<std::int32_t>(bits));
        return true;
    }

    bool int16(size_t offset, int &value) const {
        if (!has(offset, 2)) {
            return false;
        }
        const std::uint16_t bits = static_cast<std::uint16_t>(
            static_cast<std::uint16_t>(data_[offset]) |
            static_cast<std::uint16_t>(static_cast<std::uint16_t>(data_[offset + 1]) << 8));
        value = static_cast<std::int16_t>(bits);
        return true;
    }

    bool float32(size_t offset, float &value) const {
        int bits = 0;
        if (!int32(offset, bits)) {
            return false;
        }
        const std::uint32_t unsigned_bits = static_cast<std::uint32_t>(bits);
        std::memcpy(&value, &unsigned_bits, sizeof(value));
        return std::isfinite(value);
    }

    bool vec3(size_t offset, Vec3 &value) const {
        return float32(offset, value.x) && float32(offset + 4, value.y) &&
               float32(offset + 8, value.z);
    }

    /* Reads a fixed-width, possibly unterminated name field. */
    bool name(size_t offset, size_t length, std::string &value) const {
        if (!has(offset, length)) {
            return false;
        }
        size_t used = 0;
        while (used < length && data_[offset + used] != 0) {
            used++;
        }
        value.assign(reinterpret_cast<const char *>(data_ + offset), used);
        return true;
    }

private:
    const unsigned char *data_;
    size_t size_;
};

void setError(char *buffer, size_t size, const char *message) {
    if (buffer == nullptr || size == 0) {
        return;
    }
    const size_t length = std::min(std::strlen(message), size - 1);
    std::memcpy(buffer, message, length);
    buffer[length] = '\0';
}

std::string lowerExtension(const char *extension) {
    if (extension == nullptr) {
        return std::string();
    }
    std::string value(extension);
    if (!value.empty() && value.front() == '.') {
        value.erase(value.begin());
    }
    for (char &character : value) {
        if (character >= 'A' && character <= 'Z') {
            character = static_cast<char>(character - 'A' + 'a');
        }
    }
    return value;
}

/* Averages face normals across every vertex that shares a smoothing key. */
void computeSmoothNormals(Surface &surface, const std::vector<int> &smoothingKeys) {
    if (surface.vertices.empty() || smoothingKeys.size() != surface.vertices.size()) {
        return;
    }

    int keyCount = 0;
    for (const int key : smoothingKeys) {
        keyCount = std::max(keyCount, key + 1);
    }
    if (keyCount <= 0) {
        return;
    }

    std::vector<Vec3> accumulated(static_cast<size_t>(keyCount));
    for (size_t index = 0; index + 2 < surface.indices.size(); index += 3) {
        const Vertex &a = surface.vertices[static_cast<size_t>(surface.indices[index])];
        const Vertex &b = surface.vertices[static_cast<size_t>(surface.indices[index + 1])];
        const Vertex &c = surface.vertices[static_cast<size_t>(surface.indices[index + 2])];
        const Vec3 face = cross(b.position - a.position, c.position - a.position);
        for (size_t corner = 0; corner < 3; corner++) {
            const int key = smoothingKeys[static_cast<size_t>(surface.indices[index + corner])];
            if (key >= 0 && key < keyCount) {
                accumulated[static_cast<size_t>(key)] = accumulated[static_cast<size_t>(key)] + face;
            }
        }
    }

    for (size_t index = 0; index < surface.vertices.size(); index++) {
        const int key = smoothingKeys[index];
        if (key >= 0 && key < keyCount) {
            surface.vertices[index].normal = normalize(accumulated[static_cast<size_t>(key)]);
        }
    }
}

/*
 * MDL skins and sprites treat palette index 255 as a cutout. BSP textures do not:
 * there 255 is an ordinary colour, so a cutout would punch holes in the brushwork.
 */
Texture decodePalettedSkin(const Reader &reader, size_t offset, int width, int height,
                           bool cutoutLastIndex = true) {
    Texture texture;
    const size_t pixelCount = static_cast<size_t>(width) * static_cast<size_t>(height);
    if (!reader.has(offset, pixelCount)) {
        return texture;
    }

    texture.width = width;
    texture.height = height;
    texture.smooth = false;
    texture.rgba.resize(pixelCount * 4);
    for (size_t index = 0; index < pixelCount; index++) {
        const unsigned char paletteIndex = reader.data()[offset + index];
        const size_t paletteOffset = static_cast<size_t>(paletteIndex) * 3;
        texture.rgba[index * 4] = kQuakePalette[paletteOffset];
        texture.rgba[index * 4 + 1] = kQuakePalette[paletteOffset + 1];
        texture.rgba[index * 4 + 2] = kQuakePalette[paletteOffset + 2];
        texture.rgba[index * 4 + 3] = (cutoutLastIndex && paletteIndex == 255) ? 0 : 255;
    }
    return texture;
}

/* SPR32 stores straight RGBA rows where a version 1 sprite stores palette indices. */
Texture decodeRgbaFrame(const Reader &reader, size_t offset, int width, int height) {
    Texture texture;
    const size_t byteCount = static_cast<size_t>(width) * static_cast<size_t>(height) * 4;
    if (!reader.has(offset, byteCount)) {
        return texture;
    }

    texture.width = width;
    texture.height = height;
    texture.smooth = false;
    texture.rgba.assign(reader.data() + offset, reader.data() + offset + byteCount);
    return texture;
}

}  // namespace

struct pkm_model {
    int format = PKM_FORMAT_UNKNOWN;
    std::vector<Surface> surfaces;
    std::vector<Texture> textures;
    std::vector<std::string> requestSurfaces;
    std::vector<std::string> requestNames;
    int frameCount = 1;
    int skinCount = 0;
    int activeSkin = 0;
    Vec3 boundsMin;
    Vec3 boundsMax;
    Vec3 center;
    float radius = 1.0f;

    /*
     * A sprite is a flipbook: every frame is its own quad, because frames differ in
     * size and in where their origin sits. surfaces holds whichever one is playing,
     * so the renderer stays the single-mesh path the other formats use.
     */
    std::vector<Surface> spriteFrames;
    std::vector<float> spriteIntervals;
    int activeFrame = 0;
    /* Sprites start square to the camera and never drift into the turntable. */
    bool faceOn = false;

    bool animates() const { return spriteFrames.size() > 1; }

    float frameInterval() const {
        if (spriteIntervals.empty()) {
            return kDefaultSpriteInterval;
        }
        return spriteIntervals[static_cast<size_t>(activeFrame) % spriteIntervals.size()];
    }

    /* Quads are four vertices, so swapping the playing frame is a trivial copy. */
    void setFrame(int index) {
        if (spriteFrames.empty() || surfaces.empty()) {
            return;
        }
        const int count = static_cast<int>(spriteFrames.size());
        activeFrame = ((index % count) + count) % count;
        surfaces[0] = spriteFrames[static_cast<size_t>(activeFrame)];
    }

    const Texture *textureFor(const Surface &surface) const {
        if (surface.texture < 0 || static_cast<size_t>(surface.texture) >= textures.size()) {
            return nullptr;
        }
        const Texture &texture = textures[static_cast<size_t>(surface.texture)];
        return texture.valid() ? &texture : nullptr;
    }
};

namespace {

/* ------------------------------------------------------------------ MDL --- */

bool parseMdl(const Reader &reader, pkm_model &model, std::string &error) {
    int ident = 0;
    int version = 0;
    if (!reader.int32(0, ident) || !reader.int32(4, version)) {
        error = "The model header is truncated.";
        return false;
    }
    if (ident != 0x4F504449) {  // "IDPO"
        error = "The file is not a Quake MDL model.";
        return false;
    }
    if (version != 6) {
        error = "Only version 6 MDL models are supported.";
        return false;
    }

    Vec3 scale;
    Vec3 translate;
    int skinCount = 0;
    int skinWidth = 0;
    int skinHeight = 0;
    int vertexCount = 0;
    int triangleCount = 0;
    int frameCount = 0;
    if (!reader.vec3(8, scale) || !reader.vec3(20, translate) || !reader.int32(48, skinCount) ||
        !reader.int32(52, skinWidth) || !reader.int32(56, skinHeight) ||
        !reader.int32(60, vertexCount) || !reader.int32(64, triangleCount) ||
        !reader.int32(68, frameCount)) {
        error = "The model header is truncated.";
        return false;
    }

    if (skinCount < 0 || skinCount > kMaxSkins || skinWidth <= 0 || skinHeight <= 0 ||
        skinWidth > kMaxTextureDimension || skinHeight > kMaxTextureDimension ||
        vertexCount <= 0 || vertexCount > kMaxVertices || triangleCount <= 0 ||
        triangleCount > kMaxTriangles || frameCount <= 0 || frameCount > kMaxFrames) {
        error = "The model header describes an unsupported amount of geometry.";
        return false;
    }

    const size_t skinPixels = static_cast<size_t>(skinWidth) * static_cast<size_t>(skinHeight);
    size_t cursor = 84;

    for (int skin = 0; skin < skinCount; skin++) {
        int group = 0;
        if (!reader.int32(cursor, group)) {
            error = "The model skins are truncated.";
            return false;
        }
        cursor += 4;

        int count = 1;
        if (group != 0) {
            if (!reader.int32(cursor, count) || count <= 0 || count > kMaxFrames) {
                error = "The model skin group is invalid.";
                return false;
            }
            cursor += 4;
            if (!reader.has(cursor, static_cast<size_t>(count) * 4)) {
                error = "The model skin group is truncated.";
                return false;
            }
            cursor += static_cast<size_t>(count) * 4;
        }

        for (int member = 0; member < count; member++) {
            if (!reader.has(cursor, skinPixels)) {
                error = "The model skins are truncated.";
                return false;
            }
            /* Only the first member of an animated skin group is previewed. */
            if (member == 0) {
                model.textures.push_back(decodePalettedSkin(reader, cursor, skinWidth, skinHeight));
            }
            cursor += skinPixels;
        }
    }

    const size_t textureCoordinateOffset = cursor;
    if (!reader.has(cursor, static_cast<size_t>(vertexCount) * 12)) {
        error = "The model texture coordinates are truncated.";
        return false;
    }
    cursor += static_cast<size_t>(vertexCount) * 12;

    const size_t triangleOffset = cursor;
    if (!reader.has(cursor, static_cast<size_t>(triangleCount) * 16)) {
        error = "The model triangles are truncated.";
        return false;
    }
    cursor += static_cast<size_t>(triangleCount) * 16;

    /* Frame layouts vary, so walk to the first pose rather than assuming one. */
    int frameType = 0;
    if (!reader.int32(cursor, frameType)) {
        error = "The model frames are truncated.";
        return false;
    }
    cursor += 4;
    if (frameType != 0) {
        int groupCount = 0;
        if (!reader.int32(cursor, groupCount) || groupCount <= 0 || groupCount > kMaxFrames) {
            error = "The model frame group is invalid.";
            return false;
        }
        cursor += 4;
        cursor += 8;  // group bounding box
        if (!reader.has(cursor, static_cast<size_t>(groupCount) * 4)) {
            error = "The model frame group is truncated.";
            return false;
        }
        cursor += static_cast<size_t>(groupCount) * 4;
    }
    cursor += 8;   // pose bounding box
    cursor += 16;  // pose name
    const size_t poseOffset = cursor;
    if (!reader.has(poseOffset, static_cast<size_t>(vertexCount) * 4)) {
        error = "The model frames are truncated.";
        return false;
    }

    Surface surface;
    surface.name = "mdl";
    surface.texture = model.textures.empty() ? -1 : 0;
    surface.vertices.reserve(static_cast<size_t>(triangleCount) * 3);
    surface.indices.reserve(static_cast<size_t>(triangleCount) * 3);

    std::vector<int> smoothingKeys;
    smoothingKeys.reserve(static_cast<size_t>(triangleCount) * 3);

    const float halfSkinWidth = static_cast<float>(skinWidth) * 0.5f;
    for (int triangle = 0; triangle < triangleCount; triangle++) {
        const size_t offset = triangleOffset + static_cast<size_t>(triangle) * 16;
        int facesFront = 0;
        reader.int32(offset, facesFront);

        for (int corner = 0; corner < 3; corner++) {
            int vertexIndex = 0;
            reader.int32(offset + 4 + static_cast<size_t>(corner) * 4, vertexIndex);
            if (vertexIndex < 0 || vertexIndex >= vertexCount) {
                error = "The model references a vertex outside the file.";
                return false;
            }

            const size_t coordinateOffset =
                textureCoordinateOffset + static_cast<size_t>(vertexIndex) * 12;
            int onSeam = 0;
            int s = 0;
            int t = 0;
            reader.int32(coordinateOffset, onSeam);
            reader.int32(coordinateOffset + 4, s);
            reader.int32(coordinateOffset + 8, t);

            const size_t vertexOffset = poseOffset + static_cast<size_t>(vertexIndex) * 4;
            const unsigned char *packed = reader.data() + vertexOffset;

            Vertex vertex;
            vertex.position.x = static_cast<float>(packed[0]) * scale.x + translate.x;
            vertex.position.y = static_cast<float>(packed[1]) * scale.y + translate.y;
            vertex.position.z = static_cast<float>(packed[2]) * scale.z + translate.z;

            /* Seam vertices use the right half of the skin on back-facing triangles. */
            float coordinateS = static_cast<float>(s);
            if (facesFront == 0 && onSeam != 0) {
                coordinateS += halfSkinWidth;
            }
            vertex.u = (coordinateS + 0.5f) / static_cast<float>(skinWidth);
            vertex.v = (static_cast<float>(t) + 0.5f) / static_cast<float>(skinHeight);

            surface.indices.push_back(static_cast<int>(surface.vertices.size()));
            surface.vertices.push_back(vertex);
            smoothingKeys.push_back(vertexIndex);
        }
    }

    computeSmoothNormals(surface, smoothingKeys);
    model.surfaces.push_back(std::move(surface));
    model.format = PKM_FORMAT_MDL;
    model.frameCount = frameCount;
    model.skinCount = static_cast<int>(model.textures.size());
    model.activeSkin = 0;
    return true;
}

/* ------------------------------------------------------------------ MD3 --- */

bool parseMd3(const Reader &reader, pkm_model &model, std::string &error) {
    int ident = 0;
    int version = 0;
    if (!reader.int32(0, ident) || !reader.int32(4, version)) {
        error = "The model header is truncated.";
        return false;
    }
    if (ident != 0x33504449) {  // "IDP3"
        error = "The file is not a Quake III MD3 model.";
        return false;
    }
    if (version != 15) {
        error = "Only version 15 MD3 models are supported.";
        return false;
    }

    int frameCount = 0;
    int surfaceCount = 0;
    int surfaceOffset = 0;
    if (!reader.int32(76, frameCount) || !reader.int32(84, surfaceCount) ||
        !reader.int32(100, surfaceOffset)) {
        error = "The model header is truncated.";
        return false;
    }
    if (frameCount <= 0 || frameCount > kMaxFrames || surfaceCount <= 0 ||
        surfaceCount > kMaxSurfaces || surfaceOffset < 0) {
        error = "The model header describes an unsupported amount of geometry.";
        return false;
    }

    size_t cursor = static_cast<size_t>(surfaceOffset);
    int totalTriangles = 0;
    int totalVertices = 0;

    for (int index = 0; index < surfaceCount; index++) {
        int surfaceIdent = 0;
        int surfaceFrames = 0;
        int shaderCount = 0;
        int vertexCount = 0;
        int triangleCount = 0;
        int triangleOffset = 0;
        int shaderOffset = 0;
        int coordinateOffset = 0;
        int vertexOffset = 0;
        int endOffset = 0;
        std::string surfaceName;

        if (!reader.int32(cursor, surfaceIdent) || !reader.name(cursor + 4, 64, surfaceName) ||
            !reader.int32(cursor + 72, surfaceFrames) || !reader.int32(cursor + 76, shaderCount) ||
            !reader.int32(cursor + 80, vertexCount) || !reader.int32(cursor + 84, triangleCount) ||
            !reader.int32(cursor + 88, triangleOffset) || !reader.int32(cursor + 92, shaderOffset) ||
            !reader.int32(cursor + 96, coordinateOffset) ||
            !reader.int32(cursor + 100, vertexOffset) || !reader.int32(cursor + 104, endOffset)) {
            error = "The model surfaces are truncated.";
            return false;
        }

        if (surfaceIdent != 0x33504449) {
            error = "The model contains a corrupt surface.";
            return false;
        }
        if (vertexCount <= 0 || vertexCount > kMaxVertices || triangleCount <= 0 ||
            triangleCount > kMaxTriangles || endOffset <= 0 || triangleOffset < 0 ||
            coordinateOffset < 0 || vertexOffset < 0 || shaderOffset < 0) {
            error = "The model contains an invalid surface.";
            return false;
        }
        totalVertices += vertexCount;
        totalTriangles += triangleCount;
        if (totalVertices > kMaxVertices || totalTriangles > kMaxTriangles) {
            error = "The model describes an unsupported amount of geometry.";
            return false;
        }

        Surface surface;
        surface.name = surfaceName.empty() ? "surface" : surfaceName;
        surface.vertices.resize(static_cast<size_t>(vertexCount));
        surface.indices.reserve(static_cast<size_t>(triangleCount) * 3);

        /* Frame 0 is the pose that is previewed; later frames are skipped. */
        for (int vertex = 0; vertex < vertexCount; vertex++) {
            const size_t offset =
                cursor + static_cast<size_t>(vertexOffset) + static_cast<size_t>(vertex) * 8;
            int x = 0;
            int y = 0;
            int z = 0;
            if (!reader.int16(offset, x) || !reader.int16(offset + 2, y) ||
                !reader.int16(offset + 4, z) || !reader.has(offset + 6, 2)) {
                error = "The model vertices are truncated.";
                return false;
            }

            Vertex &target = surface.vertices[static_cast<size_t>(vertex)];
            target.position = {static_cast<float>(x) / 64.0f, static_cast<float>(y) / 64.0f,
                               static_cast<float>(z) / 64.0f};

            const float latitude =
                static_cast<float>(reader.data()[offset + 6]) * (2.0f * kPi) / 255.0f;
            const float longitude =
                static_cast<float>(reader.data()[offset + 7]) * (2.0f * kPi) / 255.0f;
            target.normal = {std::cos(longitude) * std::sin(latitude),
                             std::sin(longitude) * std::sin(latitude), std::cos(latitude)};

            const size_t stOffset =
                cursor + static_cast<size_t>(coordinateOffset) + static_cast<size_t>(vertex) * 8;
            if (!reader.float32(stOffset, target.u) || !reader.float32(stOffset + 4, target.v)) {
                error = "The model texture coordinates are truncated.";
                return false;
            }
        }

        for (int triangle = 0; triangle < triangleCount; triangle++) {
            const size_t offset =
                cursor + static_cast<size_t>(triangleOffset) + static_cast<size_t>(triangle) * 12;
            for (int corner = 0; corner < 3; corner++) {
                int vertexIndex = 0;
                if (!reader.int32(offset + static_cast<size_t>(corner) * 4, vertexIndex)) {
                    error = "The model triangles are truncated.";
                    return false;
                }
                if (vertexIndex < 0 || vertexIndex >= vertexCount) {
                    error = "The model references a vertex outside the surface.";
                    return false;
                }
                surface.indices.push_back(vertexIndex);
            }
        }

        /* Shaders name a texture the host resolves against the archive. */
        std::string shaderName;
        if (shaderCount > 0) {
            reader.name(cursor + static_cast<size_t>(shaderOffset), 64, shaderName);
        }
        surface.texture = static_cast<int>(model.requestNames.size());
        model.requestSurfaces.push_back(surface.name);
        model.requestNames.push_back(shaderName);
        model.textures.emplace_back();

        model.surfaces.push_back(std::move(surface));

        const size_t nextCursor = cursor + static_cast<size_t>(endOffset);
        if (nextCursor <= cursor || nextCursor > reader.size()) {
            if (index + 1 < surfaceCount) {
                error = "The model surfaces are truncated.";
                return false;
            }
            break;
        }
        cursor = nextCursor;
    }

    model.format = PKM_FORMAT_MD3;
    model.frameCount = frameCount;
    model.skinCount = 0;
    return true;
}

/* ------------------------------------------------------------------ MD5 --- */

class Tokenizer {
public:
    Tokenizer(const char *data, size_t size) : cursor_(data), end_(data + size) {}

    bool next(std::string &token) {
        skipWhitespace();
        if (cursor_ >= end_) {
            return false;
        }

        if (*cursor_ == '"') {
            cursor_++;
            const char *start = cursor_;
            while (cursor_ < end_ && *cursor_ != '"' && *cursor_ != '\n') {
                cursor_++;
            }
            token.assign(start, static_cast<size_t>(cursor_ - start));
            if (cursor_ < end_ && *cursor_ == '"') {
                cursor_++;
            }
            return true;
        }

        if (*cursor_ == '(' || *cursor_ == ')' || *cursor_ == '{' || *cursor_ == '}') {
            token.assign(cursor_, 1);
            cursor_++;
            return true;
        }

        const char *start = cursor_;
        while (cursor_ < end_ && !isDelimiter(*cursor_)) {
            cursor_++;
        }
        token.assign(start, static_cast<size_t>(cursor_ - start));
        return !token.empty();
    }

    bool expect(const char *literal) {
        std::string token;
        return next(token) && token == literal;
    }

    bool nextInt(long &value) {
        std::string token;
        if (!next(token)) {
            return false;
        }
        try {
            size_t used = 0;
            value = std::stol(token, &used);
            return used == token.size();
        } catch (...) {
            return false;
        }
    }

    bool nextFloat(float &value) {
        std::string token;
        if (!next(token)) {
            return false;
        }
        try {
            size_t used = 0;
            value = std::stof(token, &used);
            return used == token.size() && std::isfinite(value);
        } catch (...) {
            return false;
        }
    }

    bool nextVector(Vec3 &value) {
        return expect("(") && nextFloat(value.x) && nextFloat(value.y) && nextFloat(value.z) &&
               expect(")");
    }

private:
    static bool isDelimiter(char character) {
        return character == ' ' || character == '\t' || character == '\r' || character == '\n' ||
               character == '(' || character == ')' || character == '{' || character == '}' ||
               character == '"';
    }

    void skipWhitespace() {
        while (cursor_ < end_) {
            if (*cursor_ == ' ' || *cursor_ == '\t' || *cursor_ == '\r' || *cursor_ == '\n') {
                cursor_++;
            } else if (cursor_ + 1 < end_ && cursor_[0] == '/' && cursor_[1] == '/') {
                while (cursor_ < end_ && *cursor_ != '\n') {
                    cursor_++;
                }
            } else {
                return;
            }
        }
    }

    const char *cursor_;
    const char *end_;
};

struct Joint {
    Vec3 position;
    float rotation[4] = {0.0f, 0.0f, 0.0f, 1.0f};
};

Vec3 rotateByJoint(const Joint &joint, const Vec3 &point) {
    const float x = joint.rotation[0];
    const float y = joint.rotation[1];
    const float z = joint.rotation[2];
    const float w = joint.rotation[3];
    const Vec3 axis{x, y, z};
    const Vec3 first = cross(axis, point) + point * w;
    return point + cross(axis, first) * 2.0f;
}

struct Md5Vertex {
    float u = 0.0f;
    float v = 0.0f;
    int firstWeight = 0;
    int weightCount = 0;
};

struct Md5Weight {
    int joint = 0;
    float bias = 0.0f;
    Vec3 position;
};

bool parseMd5(const Reader &reader, pkm_model &model, std::string &error) {
    Tokenizer tokenizer(reinterpret_cast<const char *>(reader.data()), reader.size());
    if (!tokenizer.expect("MD5Version") || !tokenizer.expect("10")) {
        error = "The file is not a version 10 MD5 mesh.";
        return false;
    }

    std::string token;
    long jointCount = 0;
    long meshCount = 0;
    if (!tokenizer.next(token)) {
        error = "The mesh header is truncated.";
        return false;
    }
    if (token == "commandline") {
        if (!tokenizer.next(token) || !tokenizer.next(token)) {
            error = "The mesh header is truncated.";
            return false;
        }
    }
    if (token != "numJoints" || !tokenizer.nextInt(jointCount) ||
        !tokenizer.expect("numMeshes") || !tokenizer.nextInt(meshCount)) {
        error = "The mesh header is truncated.";
        return false;
    }
    if (jointCount <= 0 || jointCount > kMaxJoints || meshCount <= 0 || meshCount > kMaxSurfaces) {
        error = "The mesh describes an unsupported number of joints or meshes.";
        return false;
    }

    std::vector<Joint> joints(static_cast<size_t>(jointCount));
    if (!tokenizer.expect("joints") || !tokenizer.expect("{")) {
        error = "The mesh is missing its joints.";
        return false;
    }
    for (long index = 0; index < jointCount; index++) {
        long parent = 0;
        Vec3 position;
        Vec3 rotation;
        if (!tokenizer.next(token) || !tokenizer.nextInt(parent) ||
            !tokenizer.nextVector(position) || !tokenizer.nextVector(rotation)) {
            error = "The mesh joints are malformed.";
            return false;
        }

        Joint &joint = joints[static_cast<size_t>(index)];
        joint.position = position;
        joint.rotation[0] = rotation.x;
        joint.rotation[1] = rotation.y;
        joint.rotation[2] = rotation.z;
        /* MD5 stores unit quaternions with a negative, implied w. */
        const float remainder =
            1.0f - (rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z);
        joint.rotation[3] = -std::sqrt(remainder > 0.0f ? remainder : 0.0f);
    }
    if (!tokenizer.expect("}")) {
        error = "The mesh joints are malformed.";
        return false;
    }

    int totalTriangles = 0;
    int totalVertices = 0;

    for (long mesh = 0; mesh < meshCount; mesh++) {
        if (!tokenizer.expect("mesh") || !tokenizer.expect("{")) {
            error = "The mesh sections are malformed.";
            return false;
        }

        std::string shaderName;
        std::vector<Md5Vertex> vertices;
        std::vector<Md5Weight> weights;
        Surface surface;
        surface.name = "mesh" + std::to_string(mesh);

        while (tokenizer.next(token) && token != "}") {
            if (token == "shader") {
                if (!tokenizer.next(shaderName)) {
                    error = "The mesh shader is malformed.";
                    return false;
                }
            } else if (token == "numverts") {
                long count = 0;
                if (!tokenizer.nextInt(count) || count < 0 || count > kMaxVertices) {
                    error = "The mesh vertex count is invalid.";
                    return false;
                }
                vertices.assign(static_cast<size_t>(count), Md5Vertex());
                for (long index = 0; index < count; index++) {
                    long vertexIndex = 0;
                    long firstWeight = 0;
                    long weightCount = 0;
                    float u = 0.0f;
                    float v = 0.0f;
                    if (!tokenizer.expect("vert") || !tokenizer.nextInt(vertexIndex) ||
                        !tokenizer.expect("(") || !tokenizer.nextFloat(u) ||
                        !tokenizer.nextFloat(v) || !tokenizer.expect(")") ||
                        !tokenizer.nextInt(firstWeight) || !tokenizer.nextInt(weightCount)) {
                        error = "The mesh vertices are malformed.";
                        return false;
                    }
                    if (vertexIndex < 0 || vertexIndex >= count) {
                        error = "The mesh references a vertex outside its own list.";
                        return false;
                    }
                    Md5Vertex &vertex = vertices[static_cast<size_t>(vertexIndex)];
                    vertex.u = u;
                    vertex.v = v;
                    vertex.firstWeight = static_cast<int>(firstWeight);
                    vertex.weightCount = static_cast<int>(weightCount);
                }
            } else if (token == "numtris") {
                long count = 0;
                if (!tokenizer.nextInt(count) || count < 0 || count > kMaxTriangles) {
                    error = "The mesh triangle count is invalid.";
                    return false;
                }
                surface.indices.assign(static_cast<size_t>(count) * 3, 0);
                for (long index = 0; index < count; index++) {
                    long triangleIndex = 0;
                    long corners[3] = {0, 0, 0};
                    if (!tokenizer.expect("tri") || !tokenizer.nextInt(triangleIndex) ||
                        !tokenizer.nextInt(corners[0]) || !tokenizer.nextInt(corners[1]) ||
                        !tokenizer.nextInt(corners[2])) {
                        error = "The mesh triangles are malformed.";
                        return false;
                    }
                    if (triangleIndex < 0 || triangleIndex >= count) {
                        error = "The mesh references a triangle outside its own list.";
                        return false;
                    }
                    for (int corner = 0; corner < 3; corner++) {
                        surface.indices[static_cast<size_t>(triangleIndex) * 3 +
                                        static_cast<size_t>(corner)] =
                            static_cast<int>(corners[corner]);
                    }
                }
            } else if (token == "numweights") {
                long count = 0;
                if (!tokenizer.nextInt(count) || count < 0 || count > kMaxWeights) {
                    error = "The mesh weight count is invalid.";
                    return false;
                }
                weights.assign(static_cast<size_t>(count), Md5Weight());
                for (long index = 0; index < count; index++) {
                    long weightIndex = 0;
                    long joint = 0;
                    float bias = 0.0f;
                    Vec3 position;
                    if (!tokenizer.expect("weight") || !tokenizer.nextInt(weightIndex) ||
                        !tokenizer.nextInt(joint) || !tokenizer.nextFloat(bias) ||
                        !tokenizer.nextVector(position)) {
                        error = "The mesh weights are malformed.";
                        return false;
                    }
                    if (weightIndex < 0 || weightIndex >= count || joint < 0 ||
                        joint >= jointCount) {
                        error = "The mesh references a joint or weight outside the file.";
                        return false;
                    }
                    Md5Weight &weight = weights[static_cast<size_t>(weightIndex)];
                    weight.joint = static_cast<int>(joint);
                    weight.bias = bias;
                    weight.position = position;
                }
            }
        }

        if (vertices.empty() || surface.indices.empty()) {
            error = "The mesh contains no geometry.";
            return false;
        }

        totalVertices += static_cast<int>(vertices.size());
        totalTriangles += static_cast<int>(surface.indices.size() / 3);
        if (totalVertices > kMaxVertices || totalTriangles > kMaxTriangles) {
            error = "The mesh describes an unsupported amount of geometry.";
            return false;
        }

        /* Bake the bind pose: every vertex is a weighted blend of joint spaces. */
        surface.vertices.resize(vertices.size());
        for (size_t index = 0; index < vertices.size(); index++) {
            const Md5Vertex &source = vertices[index];
            Vec3 position;
            for (int offset = 0; offset < source.weightCount; offset++) {
                const long weightIndex = static_cast<long>(source.firstWeight) + offset;
                if (weightIndex < 0 || static_cast<size_t>(weightIndex) >= weights.size()) {
                    error = "The mesh references a weight outside the file.";
                    return false;
                }
                const Md5Weight &weight = weights[static_cast<size_t>(weightIndex)];
                const Joint &joint = joints[static_cast<size_t>(weight.joint)];
                const Vec3 rotated = rotateByJoint(joint, weight.position) + joint.position;
                position = position + rotated * weight.bias;
            }
            surface.vertices[index].position = position;
            surface.vertices[index].u = source.u;
            surface.vertices[index].v = source.v;
        }

        for (const int index : surface.indices) {
            if (index < 0 || static_cast<size_t>(index) >= surface.vertices.size()) {
                error = "The mesh references a vertex outside its own list.";
                return false;
            }
        }

        std::vector<int> smoothingKeys(surface.vertices.size());
        for (size_t index = 0; index < smoothingKeys.size(); index++) {
            smoothingKeys[index] = static_cast<int>(index);
        }
        computeSmoothNormals(surface, smoothingKeys);

        surface.texture = static_cast<int>(model.requestNames.size());
        model.requestSurfaces.push_back(surface.name);
        model.requestNames.push_back(shaderName);
        model.textures.emplace_back();
        model.surfaces.push_back(std::move(surface));
    }

    model.format = PKM_FORMAT_MD5;
    model.frameCount = 1;
    model.skinCount = 0;
    return true;
}

/* ------------------------------------------------------------------ SPR --- */

/*
 * Hangs one frame off the sprite's origin as a quad in the plane the camera starts
 * square to: y runs to screen right and z runs up, which is the pair of axes Quake
 * itself builds a sprite from once it has faced the viewer.
 */
Surface makeSpriteQuad(int textureIndex, int originX, int originY, int width, int height) {
    const float left = static_cast<float>(originX);
    const float right = static_cast<float>(originX + width);
    const float up = static_cast<float>(originY);
    const float down = static_cast<float>(originY - height);

    struct Corner {
        float y;
        float z;
        float u;
        float v;
    };
    const Corner corners[4] = {
        {left, down, 0.0f, 1.0f},
        {right, down, 1.0f, 1.0f},
        {right, up, 1.0f, 0.0f},
        {left, up, 0.0f, 0.0f},
    };

    Surface surface;
    surface.name = "sprite";
    surface.texture = textureIndex;
    surface.unlit = true;
    surface.vertices.reserve(4);
    for (const Corner &corner : corners) {
        Vertex vertex;
        vertex.position = {0.0f, corner.y, corner.z};
        vertex.normal = {1.0f, 0.0f, 0.0f};  // toward where the camera starts
        vertex.u = corner.u;
        vertex.v = corner.v;
        surface.vertices.push_back(vertex);
    }
    surface.indices = {0, 1, 2, 0, 2, 3};
    return surface;
}

bool parseSpr(const Reader &reader, pkm_model &model, std::string &error) {
    int ident = 0;
    int version = 0;
    if (!reader.int32(0, ident) || !reader.int32(4, version)) {
        error = "The sprite header is truncated.";
        return false;
    }
    if (ident != 0x50534449) {  // "IDSP"
        error = "The file is not a Quake sprite.";
        return false;
    }
    /* Half-Life reuses the magic for a different layout, palette and all. */
    if (version == 2) {
        error = "Half-Life sprites are not supported.";
        return false;
    }
    if (version != 1 && version != 32) {
        error = "Only version 1 and version 32 sprites are supported.";
        return false;
    }
    const bool rgba = version == 32;

    int canvasWidth = 0;
    int canvasHeight = 0;
    int frameCount = 0;
    if (!reader.int32(16, canvasWidth) || !reader.int32(20, canvasHeight) ||
        !reader.int32(24, frameCount)) {
        error = "The sprite header is truncated.";
        return false;
    }
    if (canvasWidth <= 0 || canvasHeight <= 0 || canvasWidth > kMaxTextureDimension ||
        canvasHeight > kMaxTextureDimension || frameCount <= 0 || frameCount > kMaxFrames) {
        error = "The sprite header describes an unsupported amount of image data.";
        return false;
    }

    size_t cursor = 36;
    long long totalPixels = 0;

    /* Reads one frame's header and pixels, whether it stands alone or is in a group. */
    const auto readFrame = [&](float interval) -> bool {
        int originX = 0;
        int originY = 0;
        int width = 0;
        int height = 0;
        if (!reader.int32(cursor, originX) || !reader.int32(cursor + 4, originY) ||
            !reader.int32(cursor + 8, width) || !reader.int32(cursor + 12, height)) {
            error = "The sprite frames are truncated.";
            return false;
        }
        cursor += 16;

        if (width <= 0 || height <= 0 || width > kMaxTextureDimension ||
            height > kMaxTextureDimension) {
            error = "A sprite frame has an unsupported size.";
            return false;
        }
        const size_t pixelCount = static_cast<size_t>(width) * static_cast<size_t>(height);
        totalPixels += static_cast<long long>(pixelCount);
        if (totalPixels > kMaxSpritePixels) {
            error = "The sprite holds more frames than the viewer can decode.";
            return false;
        }

        Texture texture = rgba ? decodeRgbaFrame(reader, cursor, width, height)
                               : decodePalettedSkin(reader, cursor, width, height);
        if (!texture.valid()) {
            error = "The sprite frames are truncated.";
            return false;
        }
        cursor += pixelCount * (rgba ? 4u : 1u);

        model.textures.push_back(std::move(texture));
        model.spriteFrames.push_back(makeSpriteQuad(static_cast<int>(model.textures.size()) - 1,
                                                    originX, originY, width, height));
        model.spriteIntervals.push_back(clampf(interval, kMinSpriteInterval, kMaxSpriteInterval));
        return true;
    };

    for (int frame = 0; frame < frameCount; frame++) {
        int frameType = 0;
        if (!reader.int32(cursor, frameType)) {
            error = "The sprite frames are truncated.";
            return false;
        }
        cursor += 4;

        if (frameType == 0) {  // SPR_SINGLE
            if (!readFrame(kDefaultSpriteInterval)) {
                return false;
            }
            continue;
        }

        /*
         * Groups and the eight-way angled frames share a layout: a count, one
         * interval per member, then the members. Every member becomes a frame of the
         * same flipbook, which is the only way to see all of them in a still viewer.
         */
        int members = 0;
        if (!reader.int32(cursor, members)) {
            error = "The sprite frames are truncated.";
            return false;
        }
        cursor += 4;
        if (members <= 0 || members > kMaxFrames ||
            static_cast<int>(model.spriteFrames.size()) + members > kMaxFrames) {
            error = "A sprite frame group is invalid.";
            return false;
        }

        const size_t intervalOffset = cursor;
        if (!reader.has(cursor, static_cast<size_t>(members) * 4)) {
            error = "A sprite frame group is truncated.";
            return false;
        }
        cursor += static_cast<size_t>(members) * 4;

        for (int member = 0; member < members; member++) {
            float interval = kDefaultSpriteInterval;
            if (!reader.float32(intervalOffset + static_cast<size_t>(member) * 4, interval) ||
                interval <= 0.0f) {
                interval = kDefaultSpriteInterval;
            }
            if (!readFrame(interval)) {
                return false;
            }
        }
    }

    if (model.spriteFrames.empty()) {
        error = "The sprite holds no frames.";
        return false;
    }

    model.surfaces.push_back(model.spriteFrames.front());
    model.format = PKM_FORMAT_SPR;
    model.frameCount = static_cast<int>(model.spriteFrames.size());
    model.skinCount = 0;
    model.faceOn = true;
    return true;
}

/* ------------------------------------------------------------------ BSP --- */

/*
 * Quake stores ammo boxes, health kits, and other props as BSP brush models, so the
 * viewer reads the faces of a BSP's first hull the way the engine draws them: edges
 * walked through the surfedge list, a plane for the normal, and the texture axes in
 * the texinfo for coordinates.
 */
constexpr int kBspVersion = 29;
constexpr int kBspLumpCount = 15;
constexpr int kBspHeaderSize = 4 + kBspLumpCount * 8;
constexpr int kBspLumpEntities = 0;
constexpr int kBspLumpPlanes = 1;
constexpr int kBspLumpTextures = 2;
constexpr int kBspLumpVertexes = 3;
constexpr int kBspLumpVisibility = 4;
constexpr int kBspLumpTexinfo = 6;
constexpr int kBspLumpFaces = 7;
constexpr int kBspLumpEdges = 12;
constexpr int kBspLumpSurfedges = 13;
constexpr int kBspLumpModels = 14;

constexpr int kBspModelSize = 64;
constexpr int kBspFaceSize = 20;
constexpr int kBspPlaneSize = 20;
constexpr int kBspTexinfoSize = 40;
constexpr int kBspMaxFaceEdges = 1024;
constexpr int kMaxBspTextures = 4096;
constexpr int kMaxBspTexturePixels = 32'000'000;
/* Untextured brushwork still needs a coordinate scale; this is Quake's own. */
constexpr float kBspDefaultTextureSize = 64.0f;

struct BspLump {
    size_t offset = 0;
    size_t size = 0;
};

bool readBspLumps(const Reader &reader, BspLump (&lumps)[kBspLumpCount]) {
    int version = 0;
    if (!reader.has(0, static_cast<size_t>(kBspHeaderSize)) || !reader.int32(0, version) ||
        version != kBspVersion) {
        return false;
    }

    for (int index = 0; index < kBspLumpCount; index++) {
        int offset = 0;
        int size = 0;
        if (!reader.int32(4 + static_cast<size_t>(index) * 8, offset) ||
            !reader.int32(4 + static_cast<size_t>(index) * 8 + 4, size) || offset < 0 || size < 0 ||
            !reader.has(static_cast<size_t>(offset), static_cast<size_t>(size))) {
            return false;
        }
        lumps[index].offset = static_cast<size_t>(offset);
        lumps[index].size = static_cast<size_t>(size);
    }
    return true;
}

/* Case-insensitive substring search that never copies the lump it reads. */
bool containsAsciiFolded(const Reader &reader, size_t offset, size_t size, const char *needle) {
    const size_t length = std::strlen(needle);
    if (length == 0 || size < length) {
        return false;
    }

    for (size_t start = 0; start + length <= size; start++) {
        size_t index = 0;
        while (index < length) {
            unsigned char character = reader.data()[offset + start + index];
            if (character >= 'A' && character <= 'Z') {
                character = static_cast<unsigned char>(character - 'A' + 'a');
            }
            if (character != static_cast<unsigned char>(needle[index])) {
                break;
            }
            index++;
        }
        if (index == length) {
            return true;
        }
    }
    return false;
}

/*
 * A brush model is a BSP nobody can play: one hull, no visibility data, and no spawn
 * point of any kind. Levels are told apart by content rather than by their name,
 * because the b_ prefix is only an id1 habit and mods do not keep it.
 */
bool bspIsBrushModel(const Reader &reader) {
    BspLump lumps[kBspLumpCount];
    if (!readBspLumps(reader, lumps)) {
        return false;
    }
    if (lumps[kBspLumpVisibility].size != 0) {
        return false;
    }
    if (lumps[kBspLumpModels].size != static_cast<size_t>(kBspModelSize)) {
        return false;
    }
    if (lumps[kBspLumpFaces].size < static_cast<size_t>(kBspFaceSize)) {
        return false;
    }

    /* info_player_start, _deathmatch, _coop, and _team all share the one prefix. */
    const BspLump &entities = lumps[kBspLumpEntities];
    return !containsAsciiFolded(reader, entities.offset, entities.size, "info_player") &&
           !containsAsciiFolded(reader, entities.offset, entities.size, "testplayerstart");
}

bool parseBsp(const Reader &reader, pkm_model &model, std::string &error) {
    BspLump lumps[kBspLumpCount];
    if (!readBspLumps(reader, lumps)) {
        error = "Only version 29 Quake BSP files can be previewed.";
        return false;
    }

    const size_t vertexCount = lumps[kBspLumpVertexes].size / 12;
    const size_t edgeCount = lumps[kBspLumpEdges].size / 4;
    const size_t surfedgeCount = lumps[kBspLumpSurfedges].size / 4;
    const size_t faceTotal = lumps[kBspLumpFaces].size / kBspFaceSize;
    const size_t planeCount = lumps[kBspLumpPlanes].size / kBspPlaneSize;
    const size_t texinfoCount = lumps[kBspLumpTexinfo].size / kBspTexinfoSize;

    if (vertexCount == 0 || edgeCount == 0 || surfedgeCount == 0 || faceTotal == 0 ||
        planeCount == 0) {
        error = "The brush model holds no geometry.";
        return false;
    }
    if (vertexCount > static_cast<size_t>(kMaxVertices) ||
        faceTotal > static_cast<size_t>(kMaxTriangles)) {
        error = "The brush model describes an unsupported amount of geometry.";
        return false;
    }

    /* Only the first hull is the model itself; the rest are a level's moving parts. */
    int firstFace = 0;
    int faceCount = 0;
    if (lumps[kBspLumpModels].size < static_cast<size_t>(kBspModelSize) ||
        !reader.int32(lumps[kBspLumpModels].offset + 56, firstFace) ||
        !reader.int32(lumps[kBspLumpModels].offset + 60, faceCount) || firstFace < 0 ||
        faceCount <= 0 || static_cast<size_t>(firstFace) + static_cast<size_t>(faceCount) >
                              faceTotal) {
        error = "The brush model does not name any faces.";
        return false;
    }

    /* Textures live in the file, mip level zero first, as palette indices. */
    int miptexCount = 0;
    const BspLump &textureLump = lumps[kBspLumpTextures];
    if (textureLump.size >= 4) {
        reader.int32(textureLump.offset, miptexCount);
    }
    if (miptexCount < 0 || miptexCount > kMaxBspTextures) {
        miptexCount = 0;
    }

    std::vector<int> miptexTexture(static_cast<size_t>(miptexCount), -1);
    std::vector<std::string> miptexName(static_cast<size_t>(miptexCount));
    std::vector<int> miptexWidth(static_cast<size_t>(miptexCount), 0);
    std::vector<int> miptexHeight(static_cast<size_t>(miptexCount), 0);
    long long texturePixels = 0;

    for (int index = 0; index < miptexCount; index++) {
        int relative = 0;
        if (!reader.int32(textureLump.offset + 4 + static_cast<size_t>(index) * 4, relative) ||
            relative < 0) {
            continue;
        }

        const size_t base = textureLump.offset + static_cast<size_t>(relative);
        std::string name;
        int width = 0;
        int height = 0;
        int pixelOffset = 0;
        if (!reader.name(base, 16, name) || !reader.int32(base + 16, width) ||
            !reader.int32(base + 20, height) || !reader.int32(base + 24, pixelOffset) ||
            width <= 0 || height <= 0 || width > kMaxTextureDimension ||
            height > kMaxTextureDimension || pixelOffset <= 0) {
            continue;
        }

        texturePixels += static_cast<long long>(width) * static_cast<long long>(height);
        if (texturePixels > kMaxBspTexturePixels) {
            error = "The brush model holds more texture data than the viewer can decode.";
            return false;
        }

        Texture texture = decodePalettedSkin(reader, base + static_cast<size_t>(pixelOffset), width,
                                             height, /*cutoutLastIndex=*/false);
        if (!texture.valid()) {
            continue;
        }

        model.textures.push_back(std::move(texture));
        miptexTexture[static_cast<size_t>(index)] = static_cast<int>(model.textures.size()) - 1;
        miptexName[static_cast<size_t>(index)] = name;
        miptexWidth[static_cast<size_t>(index)] = width;
        miptexHeight[static_cast<size_t>(index)] = height;
    }

    /* Faces are grouped by texture so the renderer keeps one surface per material. */
    std::vector<int> surfaceForMiptex(static_cast<size_t>(miptexCount), -1);
    int untexturedSurface = -1;
    size_t triangles = 0;

    for (int face = firstFace; face < firstFace + faceCount; face++) {
        const size_t faceOffset =
            lumps[kBspLumpFaces].offset + static_cast<size_t>(face) * kBspFaceSize;
        int planeIndex = 0;
        int side = 0;
        int firstEdge = 0;
        int edges = 0;
        int texinfoIndex = 0;
        if (!reader.int16(faceOffset, planeIndex) || !reader.int16(faceOffset + 2, side) ||
            !reader.int32(faceOffset + 4, firstEdge) || !reader.int16(faceOffset + 8, edges) ||
            !reader.int16(faceOffset + 10, texinfoIndex)) {
            error = "The brush model faces are truncated.";
            return false;
        }

        /* The plane, edge count, and texinfo are unsigned in the file. */
        planeIndex = static_cast<unsigned short>(planeIndex);
        edges = static_cast<unsigned short>(edges);
        texinfoIndex = static_cast<unsigned short>(texinfoIndex);

        if (planeIndex < 0 || static_cast<size_t>(planeIndex) >= planeCount || edges < 3 ||
            edges > kBspMaxFaceEdges || firstEdge < 0 ||
            static_cast<size_t>(firstEdge) + static_cast<size_t>(edges) > surfedgeCount) {
            continue;
        }

        Vec3 normal;
        if (!reader.vec3(lumps[kBspLumpPlanes].offset + static_cast<size_t>(planeIndex) *
                                                            kBspPlaneSize,
                         normal)) {
            continue;
        }
        if (side != 0) {
            normal = normal * -1.0f;
        }
        normal = normalize(normal);

        /* Texture axes are unnormalized, which is what makes BSP textures tile. */
        float axes[2][4] = {{1.0f, 0.0f, 0.0f, 0.0f}, {0.0f, 1.0f, 0.0f, 0.0f}};
        int miptexIndex = -1;
        if (texinfoIndex >= 0 && static_cast<size_t>(texinfoIndex) < texinfoCount) {
            const size_t texinfoOffset =
                lumps[kBspLumpTexinfo].offset + static_cast<size_t>(texinfoIndex) * kBspTexinfoSize;
            bool readAxes = true;
            for (int axis = 0; axis < 2 && readAxes; axis++) {
                for (int component = 0; component < 4 && readAxes; component++) {
                    readAxes = reader.float32(
                        texinfoOffset + static_cast<size_t>(axis) * 16 +
                            static_cast<size_t>(component) * 4,
                        axes[axis][component]);
                }
            }
            int requested = 0;
            if (readAxes && reader.int32(texinfoOffset + 32, requested) && requested >= 0 &&
                static_cast<size_t>(requested) < static_cast<size_t>(miptexCount)) {
                miptexIndex = requested;
            }
        }

        int surfaceIndex = -1;
        if (miptexIndex >= 0) {
            surfaceIndex = surfaceForMiptex[static_cast<size_t>(miptexIndex)];
        } else {
            surfaceIndex = untexturedSurface;
        }

        if (surfaceIndex < 0) {
            if (model.surfaces.size() >= static_cast<size_t>(kMaxSurfaces)) {
                continue;
            }
            Surface surface;
            surface.name = miptexIndex >= 0 ? miptexName[static_cast<size_t>(miptexIndex)] : "brush";
            surface.texture = miptexIndex >= 0 ? miptexTexture[static_cast<size_t>(miptexIndex)] : -1;
            model.surfaces.push_back(std::move(surface));
            surfaceIndex = static_cast<int>(model.surfaces.size()) - 1;
            if (miptexIndex >= 0) {
                surfaceForMiptex[static_cast<size_t>(miptexIndex)] = surfaceIndex;
            } else {
                untexturedSurface = surfaceIndex;
            }
        }

        Surface &surface = model.surfaces[static_cast<size_t>(surfaceIndex)];
        /* A texture the file failed to hand over still needs a coordinate scale. */
        const int namedWidth = miptexIndex >= 0 ? miptexWidth[static_cast<size_t>(miptexIndex)] : 0;
        const int namedHeight = miptexIndex >= 0 ? miptexHeight[static_cast<size_t>(miptexIndex)] : 0;
        const float textureWidth =
            namedWidth > 0 ? static_cast<float>(namedWidth) : kBspDefaultTextureSize;
        const float textureHeight =
            namedHeight > 0 ? static_cast<float>(namedHeight) : kBspDefaultTextureSize;

        const int firstVertex = static_cast<int>(surface.vertices.size());
        bool complete = true;
        for (int edge = 0; edge < edges && complete; edge++) {
            int surfedge = 0;
            if (!reader.int32(lumps[kBspLumpSurfedges].offset +
                                  (static_cast<size_t>(firstEdge) + static_cast<size_t>(edge)) * 4,
                              surfedge)) {
                complete = false;
                break;
            }

            /* A negative surfedge walks its edge backwards, which orders the loop. */
            const size_t edgeIndex = static_cast<size_t>(surfedge < 0 ? -surfedge : surfedge);
            if (edgeIndex >= edgeCount) {
                complete = false;
                break;
            }
            int start = 0;
            int end = 0;
            if (!reader.int16(lumps[kBspLumpEdges].offset + edgeIndex * 4, start) ||
                !reader.int16(lumps[kBspLumpEdges].offset + edgeIndex * 4 + 2, end)) {
                complete = false;
                break;
            }
            /* Edge endpoints are unsigned in the file. */
            const size_t vertexIndex = static_cast<size_t>(
                static_cast<unsigned short>(surfedge < 0 ? end : start));
            if (vertexIndex >= vertexCount) {
                complete = false;
                break;
            }

            Vertex vertex;
            if (!reader.vec3(lumps[kBspLumpVertexes].offset + vertexIndex * 12, vertex.position)) {
                complete = false;
                break;
            }
            vertex.normal = normal;
            vertex.u = (vertex.position.x * axes[0][0] + vertex.position.y * axes[0][1] +
                        vertex.position.z * axes[0][2] + axes[0][3]) /
                       textureWidth;
            vertex.v = (vertex.position.x * axes[1][0] + vertex.position.y * axes[1][1] +
                        vertex.position.z * axes[1][2] + axes[1][3]) /
                       textureHeight;
            surface.vertices.push_back(vertex);
        }

        if (!complete) {
            surface.vertices.resize(static_cast<size_t>(firstVertex));
            continue;
        }

        /* Every BSP face is a convex loop, so a fan triangulates it exactly. */
        for (int corner = 1; corner + 1 < edges; corner++) {
            surface.indices.push_back(firstVertex);
            surface.indices.push_back(firstVertex + corner);
            surface.indices.push_back(firstVertex + corner + 1);
            triangles++;
        }
        if (triangles > static_cast<size_t>(kMaxTriangles)) {
            error = "The brush model describes an unsupported amount of geometry.";
            return false;
        }
    }

    if (model.surfaces.empty() || triangles == 0) {
        error = "The brush model holds no drawable faces.";
        return false;
    }

    model.format = PKM_FORMAT_BSP;
    model.frameCount = 1;
    model.skinCount = 0;
    return true;
}

/* ------------------------------------------------------------ post-load --- */

void computeBounds(pkm_model &model) {
    /* A sprite is framed by every pose it flips through, so playback never rescales. */
    const std::vector<Surface> &geometry =
        model.spriteFrames.empty() ? model.surfaces : model.spriteFrames;

    bool first = true;
    for (const Surface &surface : geometry) {
        for (const Vertex &vertex : surface.vertices) {
            if (first) {
                model.boundsMin = vertex.position;
                model.boundsMax = vertex.position;
                first = false;
                continue;
            }
            model.boundsMin.x = std::min(model.boundsMin.x, vertex.position.x);
            model.boundsMin.y = std::min(model.boundsMin.y, vertex.position.y);
            model.boundsMin.z = std::min(model.boundsMin.z, vertex.position.z);
            model.boundsMax.x = std::max(model.boundsMax.x, vertex.position.x);
            model.boundsMax.y = std::max(model.boundsMax.y, vertex.position.y);
            model.boundsMax.z = std::max(model.boundsMax.z, vertex.position.z);
        }
    }

    model.center = (model.boundsMin + model.boundsMax) * 0.5f;
    float radius = 0.0f;
    for (const Surface &surface : model.surfaces) {
        for (const Vertex &vertex : surface.vertices) {
            radius = std::max(radius, std::sqrt(dot(vertex.position - model.center,
                                                    vertex.position - model.center)));
        }
    }
    model.radius = radius > 1e-4f ? radius : 1.0f;
}

}  // namespace

/* ----------------------------------------------------------------- view --- */

struct pkm_view {
    pkm_model *model = nullptr;

    float yaw = kDefaultYaw;
    float pitch = kDefaultPitch;
    float distance = 1.0f;
    Vec3 target;

    float goalYaw = kDefaultYaw;
    float goalPitch = kDefaultPitch;
    float goalDistance = 1.0f;
    Vec3 goalTarget;

    /* Where framing and reset put the camera, which sprites move square to the quad. */
    float homeYaw = kDefaultYaw;
    float homePitch = kDefaultPitch;
    /* Time spent on the sprite frame that is playing. */
    double frameClock = 0.0;

    float yawVelocity = 0.0f;
    float pitchVelocity = 0.0f;
    float pendingYaw = 0.0f;
    float pendingPitch = 0.0f;

    float frameDistance = 1.0f;
    float aspect = 0.0f;

    bool interacting = false;
    bool hasInteracted = false;
    bool autoRotate = true;
    bool dark = true;
    bool dirty = true;
    bool settled = false;
    bool supersample = true;
    double idleSeconds = 0.0;

    /* Sensible defaults so input that arrives before the first frame still tracks. */
    int width = 512;
    int height = 512;
    std::vector<unsigned char> color;
    std::vector<float> depth;

    /* The backdrop only changes with the size or the theme, so it is kept. */
    std::vector<unsigned char> backdropCache;
    int backdropWidth = 0;
    int backdropHeight = 0;
    bool backdropDark = true;

    void frame(float viewAspect) {
        const float ratio = frameDistance > 1e-6f ? distance / frameDistance : 1.0f;
        const float goalRatio = frameDistance > 1e-6f ? goalDistance / frameDistance : 1.0f;

        const float vertical = kFieldOfView;
        const float horizontal =
            2.0f * std::atan(std::tan(vertical * 0.5f) * std::max(viewAspect, 0.05f));
        const float limiting = std::min(vertical, horizontal);
        frameDistance = model->radius / std::sin(std::max(limiting * 0.5f, 0.05f)) * 1.12f;
        aspect = viewAspect;

        distance = frameDistance * ratio;
        goalDistance = frameDistance * goalRatio;
    }

    void reset() {
        goalYaw = homeYaw;
        goalPitch = homePitch;
        goalDistance = frameDistance;
        goalTarget = model->center;
        yawVelocity = 0.0f;
        pitchVelocity = 0.0f;
        pendingYaw = 0.0f;
        pendingPitch = 0.0f;
        dirty = true;
        settled = false;
    }

    void snapToGoal() {
        yaw = goalYaw;
        pitch = goalPitch;
        distance = goalDistance;
        target = goalTarget;
    }

    Vec3 direction() const {
        return {std::cos(pitch) * std::cos(yaw), std::cos(pitch) * std::sin(yaw), std::sin(pitch)};
    }

    Vec3 eye() const { return target + direction() * distance; }
};

namespace {

struct Basis {
    Vec3 eye;
    Vec3 right;
    Vec3 up;
    Vec3 forward;
};

Basis makeBasis(const pkm_view &view) {
    Basis basis;
    basis.eye = view.eye();
    basis.forward = normalize(view.target - basis.eye);
    const Vec3 worldUp{0.0f, 0.0f, 1.0f};
    basis.right = normalize(cross(basis.forward, worldUp));
    basis.up = cross(basis.right, basis.forward);
    return basis;
}

struct Lighting {
    Vec3 key;
    Vec3 fill;
    Vec3 rim;
};

/* A three-point rig anchored to the camera keeps every orbit angle readable. */
Lighting makeLighting(const Basis &basis) {
    Lighting lighting;
    lighting.key = normalize(basis.forward * -1.0f + basis.right * -0.55f + basis.up * 0.68f);
    lighting.fill = normalize(basis.forward * -1.0f + basis.right * 0.85f + basis.up * 0.08f);
    lighting.rim = normalize(basis.forward * 0.85f + basis.up * 0.55f);
    return lighting;
}

struct Color {
    float r = 0.0f;
    float g = 0.0f;
    float b = 0.0f;
};

float srgbToLinear(float value) {
    return value <= 0.04045f ? value / 12.92f : std::pow((value + 0.055f) / 1.055f, 2.4f);
}

float linearToSrgb(float value) {
    value = clampf(value, 0.0f, 1.0f);
    return value <= 0.0031308f ? value * 12.92f
                               : 1.055f * std::pow(value, 1.0f / 2.4f) - 0.055f;
}

/*
 * The gamma curves run on every pixel of every frame, so they are tabulated once
 * instead of calling pow in the inner loop.
 */
constexpr int kEncodeSteps = 4096;

struct GammaTables {
    float decode[256];
    unsigned char encode[kEncodeSteps];

    GammaTables() {
        for (int index = 0; index < 256; index++) {
            decode[index] = srgbToLinear(static_cast<float>(index) / 255.0f);
        }
        for (int index = 0; index < kEncodeSteps; index++) {
            const float linear = static_cast<float>(index) / static_cast<float>(kEncodeSteps - 1);
            encode[index] =
                static_cast<unsigned char>(linearToSrgb(linear) * 255.0f + 0.5f);
        }
    }
};

const GammaTables kGamma;

const float *srgbTable() { return kGamma.decode; }

unsigned char encodeLinear(float value) {
    const float clamped = clampf(value, 0.0f, 1.0f);
    return kGamma.encode[static_cast<int>(clamped * static_cast<float>(kEncodeSteps - 1) + 0.5f)];
}

/*
 * An extended Reinhard shoulder: it rolls highlights off smoothly without the
 * cost of an exponential in the per-pixel path.
 */
float shoulder(float value) {
    const float clamped = value > 0.0f ? value : 0.0f;
    return clamped * (1.0f + clamped * 0.22f) / (1.0f + clamped);
}

/* Integer powers, which the shading uses far more than arbitrary exponents. */
float powi(float base, int exponent) {
    float result = 1.0f;
    while (exponent > 0) {
        if ((exponent & 1) != 0) {
            result *= base;
        }
        base *= base;
        exponent >>= 1;
    }
    return result;
}

struct Sample {
    Color color;
    float alpha = 1.0f;
};

Sample sampleTexture(const Texture &texture, float u, float v) {
    const float *table = srgbTable();
    Sample sample;

    auto fetch = [&](int x, int y, Color &color, float &alpha) {
        x = ((x % texture.width) + texture.width) % texture.width;
        y = ((y % texture.height) + texture.height) % texture.height;
        const size_t offset =
            (static_cast<size_t>(y) * static_cast<size_t>(texture.width) + static_cast<size_t>(x)) * 4;
        color.r = table[texture.rgba[offset]];
        color.g = table[texture.rgba[offset + 1]];
        color.b = table[texture.rgba[offset + 2]];
        alpha = static_cast<float>(texture.rgba[offset + 3]) / 255.0f;
    };

    const float x = u * static_cast<float>(texture.width);
    const float y = v * static_cast<float>(texture.height);

    if (!texture.smooth) {
        fetch(static_cast<int>(std::floor(x)), static_cast<int>(std::floor(y)), sample.color,
              sample.alpha);
        return sample;
    }

    const float fx = x - 0.5f;
    const float fy = y - 0.5f;
    const int x0 = static_cast<int>(std::floor(fx));
    const int y0 = static_cast<int>(std::floor(fy));
    const float tx = fx - static_cast<float>(x0);
    const float ty = fy - static_cast<float>(y0);

    Color colors[4];
    float alphas[4];
    fetch(x0, y0, colors[0], alphas[0]);
    fetch(x0 + 1, y0, colors[1], alphas[1]);
    fetch(x0, y0 + 1, colors[2], alphas[2]);
    fetch(x0 + 1, y0 + 1, colors[3], alphas[3]);

    const float w0 = (1.0f - tx) * (1.0f - ty);
    const float w1 = tx * (1.0f - ty);
    const float w2 = (1.0f - tx) * ty;
    const float w3 = tx * ty;
    sample.color.r = colors[0].r * w0 + colors[1].r * w1 + colors[2].r * w2 + colors[3].r * w3;
    sample.color.g = colors[0].g * w0 + colors[1].g * w1 + colors[2].g * w2 + colors[3].g * w3;
    sample.color.b = colors[0].b * w0 + colors[1].b * w1 + colors[2].b * w2 + colors[3].b * w3;
    sample.alpha = alphas[0] * w0 + alphas[1] * w1 + alphas[2] * w2 + alphas[3] * w3;
    return sample;
}

Color backdrop(bool dark, float x, float y, int width, int height) {
    const float verticalRatio = height > 1 ? y / static_cast<float>(height - 1) : 0.0f;
    const float centerX = (x / static_cast<float>(std::max(width, 1))) - 0.5f;
    const float centerY = verticalRatio - 0.42f;
    const float falloff = clampf(1.0f - (centerX * centerX + centerY * centerY) * 1.6f, 0.0f, 1.0f);

    Color top;
    Color bottom;
    if (dark) {
        top = {0.098f, 0.106f, 0.125f};
        bottom = {0.043f, 0.047f, 0.059f};
    } else {
        top = {0.957f, 0.961f, 0.973f};
        bottom = {0.839f, 0.851f, 0.878f};
    }

    const float glow = falloff * falloff * (dark ? 0.055f : 0.045f);
    Color color;
    color.r = top.r + (bottom.r - top.r) * verticalRatio + glow;
    color.g = top.g + (bottom.g - top.g) * verticalRatio + glow;
    color.b = top.b + (bottom.b - top.b) * verticalRatio + glow;
    return color;
}

struct ClipVertex {
    float vx = 0.0f;
    float vy = 0.0f;
    float vz = 0.0f;
    Vec3 world;
    Vec3 normal;
    float u = 0.0f;
    float v = 0.0f;
};

ClipVertex mixVertices(const ClipVertex &a, const ClipVertex &b, float t) {
    ClipVertex result;
    result.vx = a.vx + (b.vx - a.vx) * t;
    result.vy = a.vy + (b.vy - a.vy) * t;
    result.vz = a.vz + (b.vz - a.vz) * t;
    result.world = a.world + (b.world - a.world) * t;
    result.normal = a.normal + (b.normal - a.normal) * t;
    result.u = a.u + (b.u - a.u) * t;
    result.v = a.v + (b.v - a.v) * t;
    return result;
}

struct ScreenVertex {
    float x = 0.0f;
    float y = 0.0f;
    float invDepth = 0.0f;
    Vec3 world;
    Vec3 normal;
    float u = 0.0f;
    float v = 0.0f;
};

/*
 * Rasterizes into one horizontal band of the frame. Bands own disjoint rows, so
 * the frame can be split across threads without any locking and the result stays
 * identical to a single-threaded pass.
 */
class Rasterizer {
public:
    Rasterizer(unsigned char *color, float *depth, int width, int rowBegin, int rowEnd)
        : color_(color), depth_(depth), width_(width), rowBegin_(rowBegin), rowEnd_(rowEnd) {}

    /* Shader receives interpolated attributes and returns false to discard. */
    template <typename Shader>
    void triangle(const ScreenVertex &a, const ScreenVertex &b, const ScreenVertex &c,
                  const Shader &shader) {
        const float area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        if (std::fabs(area) < 1e-7f) {
            return;
        }
        const float inverseArea = 1.0f / area;

        /*
         * Two triangles that share an edge evaluate that edge from different corners,
         * so rounding can leave a pixel just outside both and open a hairline seam.
         * Claiming the edge in both triangles closes it; the depth test then keeps the
         * first one that covered the pixel.
         */
        const float bias = std::fabs(area) * 1e-6f;

        const int minX = std::max(0, static_cast<int>(std::floor(std::min({a.x, b.x, c.x}))));
        const int maxX = std::min(width_ - 1, static_cast<int>(std::ceil(std::max({a.x, b.x, c.x}))));
        const int minY = std::max(rowBegin_, static_cast<int>(std::floor(std::min({a.y, b.y, c.y}))));
        const int maxY = std::min(rowEnd_ - 1, static_cast<int>(std::ceil(std::max({a.y, b.y, c.y}))));

        for (int y = minY; y <= maxY; y++) {
            for (int x = minX; x <= maxX; x++) {
                const float px = static_cast<float>(x) + 0.5f;
                const float py = static_cast<float>(y) + 0.5f;

                const float e0 = (c.x - b.x) * (py - b.y) - (c.y - b.y) * (px - b.x);
                const float e1 = (a.x - c.x) * (py - c.y) - (a.y - c.y) * (px - c.x);
                const float e2 = (b.x - a.x) * (py - a.y) - (b.y - a.y) * (px - a.x);
                const bool inside = area > 0.0f
                                        ? (e0 >= -bias && e1 >= -bias && e2 >= -bias)
                                        : (e0 <= bias && e1 <= bias && e2 <= bias);
                if (!inside) {
                    continue;
                }

                const float l0 = e0 * inverseArea;
                const float l1 = e1 * inverseArea;
                const float l2 = e2 * inverseArea;

                const float invDepth = l0 * a.invDepth + l1 * b.invDepth + l2 * c.invDepth;
                if (invDepth <= 0.0f) {
                    continue;
                }
                const size_t index = static_cast<size_t>(y) * static_cast<size_t>(width_) +
                                     static_cast<size_t>(x);
                if (invDepth <= depth_[index]) {
                    continue;
                }

                /* Perspective-correct interpolation of the shading attributes. */
                const float w0 = l0 * a.invDepth / invDepth;
                const float w1 = l1 * b.invDepth / invDepth;
                const float w2 = l2 * c.invDepth / invDepth;

                Vec3 world = a.world * w0 + b.world * w1 + c.world * w2;
                Vec3 normal = a.normal * w0 + b.normal * w1 + c.normal * w2;
                const float u = a.u * w0 + b.u * w1 + c.u * w2;
                const float v = a.v * w0 + b.v * w1 + c.v * w2;

                Color shaded;
                if (!shader(x, y, world, normal, u, v, shaded)) {
                    continue;
                }

                depth_[index] = invDepth;
                unsigned char *pixel = color_ + index * 4;
                pixel[0] = encodeLinear(shaded.b);
                pixel[1] = encodeLinear(shaded.g);
                pixel[2] = encodeLinear(shaded.r);
                pixel[3] = 255;
            }
        }
    }

private:
    unsigned char *color_;
    float *depth_;
    int width_;
    int rowBegin_;
    int rowEnd_;
};

/* Splits the frame into bands and renders them in parallel. */
template <typename Body>
void forEachBand(int rowCount, const Body &body) {
    unsigned int threads = std::thread::hardware_concurrency();
    if (threads == 0) {
        threads = 1;
    }
    threads = std::min(threads, 8u);
    const int bandRows = 64;
    const int bandCount = std::min(static_cast<int>(threads),
                                   std::max(1, (rowCount + bandRows - 1) / bandRows));
    if (bandCount <= 1) {
        body(0, rowCount);
        return;
    }

    const int rowsPerBand = (rowCount + bandCount - 1) / bandCount;
    std::vector<std::thread> workers;
    workers.reserve(static_cast<size_t>(bandCount) - 1);
    for (int band = 1; band < bandCount; band++) {
        const int begin = band * rowsPerBand;
        const int end = std::min(rowCount, begin + rowsPerBand);
        if (begin >= end) {
            break;
        }
        workers.emplace_back([&body, begin, end] { body(begin, end); });
    }
    body(0, std::min(rowCount, rowsPerBand));
    for (std::thread &worker : workers) {
        worker.join();
    }
}

struct Projection {
    Basis basis;
    float focal = 1.0f;
    float aspect = 1.0f;
    float near = 0.01f;
    int width = 0;
    int height = 0;

    ClipVertex toView(const Vec3 &world, const Vec3 &normal, float u, float v) const {
        const Vec3 relative = world - basis.eye;
        ClipVertex vertex;
        vertex.vx = dot(relative, basis.right);
        vertex.vy = dot(relative, basis.up);
        vertex.vz = dot(relative, basis.forward);
        vertex.world = world;
        vertex.normal = normal;
        vertex.u = u;
        vertex.v = v;
        return vertex;
    }

    ScreenVertex toScreen(const ClipVertex &vertex) const {
        ScreenVertex screen;
        const float invDepth = 1.0f / std::max(vertex.vz, 1e-6f);
        screen.x = (vertex.vx * focal / aspect * invDepth * 0.5f + 0.5f) * static_cast<float>(width);
        screen.y = (0.5f - vertex.vy * focal * invDepth * 0.5f) * static_cast<float>(height);
        screen.invDepth = invDepth;
        screen.world = vertex.world;
        screen.normal = vertex.normal;
        screen.u = vertex.u;
        screen.v = vertex.v;
        return screen;
    }
};

/* Clips a triangle against the near plane and emits screen-space triangles. */
template <typename Emit>
void clipAndEmit(const Projection &projection, const ClipVertex &a, const ClipVertex &b,
                 const ClipVertex &c, const Emit &emit) {
    ClipVertex polygon[4];
    int count = 0;
    const ClipVertex input[3] = {a, b, c};

    for (int index = 0; index < 3; index++) {
        const ClipVertex &current = input[index];
        const ClipVertex &next = input[(index + 1) % 3];
        const bool currentInside = current.vz >= projection.near;
        const bool nextInside = next.vz >= projection.near;

        if (currentInside && count < 4) {
            polygon[count++] = current;
        }
        if (currentInside != nextInside && count < 4) {
            const float span = next.vz - current.vz;
            if (std::fabs(span) > 1e-9f) {
                polygon[count++] = mixVertices(current, next, (projection.near - current.vz) / span);
            }
        }
    }

    if (count < 3) {
        return;
    }
    for (int index = 1; index + 1 < count; index++) {
        emit(projection.toScreen(polygon[0]), projection.toScreen(polygon[index]),
             projection.toScreen(polygon[index + 1]));
    }
}

int copyName(const std::string &value, char *name, size_t name_size) {
    if (name == nullptr || name_size == 0) {
        return PKM_ERROR_INVALID_ARGUMENT;
    }
    const size_t length = std::min(value.size(), name_size - 1);
    std::memcpy(name, value.data(), length);
    name[length] = '\0';
    return value.size() < name_size ? PKM_OK : PKM_ERROR_INVALID_ARGUMENT;
}

}  // namespace

/* ------------------------------------------------------------------ ABI --- */

extern "C" {

int pkm_supports_extension(const char *extension) {
    const std::string value = lowerExtension(extension);
    return (value == "mdl" || value == "md3" || value == "md5mesh" || value == "md5" ||
            value == "spr" || value == "spr32" || value == "bsp")
               ? 1
               : 0;
}

int pkm_bsp_is_brush_model(const void *bsp_data, size_t bsp_size) {
    if (bsp_data == nullptr || bsp_size == 0) {
        return 0;
    }
    try {
        const Reader reader(static_cast<const unsigned char *>(bsp_data), bsp_size);
        return bspIsBrushModel(reader) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

pkm_model *pkm_model_create(const void *model_data, size_t model_size, const char *extension,
                            char *error_message, size_t error_message_size) {
    setError(error_message, error_message_size, "");
    if (model_data == nullptr || model_size == 0) {
        setError(error_message, error_message_size, "The model file is empty.");
        return nullptr;
    }
    if (!pkm_supports_extension(extension)) {
        setError(error_message, error_message_size, "This model format is not supported.");
        return nullptr;
    }

    try {
        const std::string format = lowerExtension(extension);
        const Reader reader(static_cast<const unsigned char *>(model_data), model_size);
        pkm_model model;
        std::string error;

        bool parsed = false;
        if (format == "mdl") {
            parsed = parseMdl(reader, model, error);
        } else if (format == "md3") {
            parsed = parseMd3(reader, model, error);
        } else if (format == "spr" || format == "spr32") {
            parsed = parseSpr(reader, model, error);
        } else if (format == "bsp") {
            parsed = parseBsp(reader, model, error);
        } else {
            parsed = parseMd5(reader, model, error);
        }

        if (!parsed) {
            setError(error_message, error_message_size,
                     error.empty() ? "The model could not be read." : error.c_str());
            return nullptr;
        }
        if (model.surfaces.empty()) {
            setError(error_message, error_message_size, "The model contains no geometry.");
            return nullptr;
        }

        computeBounds(model);
        return new pkm_model(std::move(model));
    } catch (const std::bad_alloc &) {
        setError(error_message, error_message_size, "The model is too large to preview.");
        return nullptr;
    } catch (...) {
        setError(error_message, error_message_size, "The model could not be read.");
        return nullptr;
    }
}

void pkm_model_destroy(pkm_model *model) { delete model; }

int pkm_model_format(const pkm_model *model) {
    return model == nullptr ? PKM_FORMAT_UNKNOWN : model->format;
}

int pkm_model_get_stats(const pkm_model *model, pkm_model_stats *stats) {
    if (model == nullptr || stats == nullptr) {
        return PKM_ERROR_INVALID_ARGUMENT;
    }

    int vertices = 0;
    int triangles = 0;
    int textured = 0;
    for (const Surface &surface : model->surfaces) {
        vertices += static_cast<int>(surface.vertices.size());
        triangles += static_cast<int>(surface.indices.size() / 3);
        if (model->textureFor(surface) != nullptr) {
            textured++;
        }
    }

    stats->format = model->format;
    stats->surface_count = static_cast<int>(model->surfaces.size());
    stats->vertex_count = vertices;
    stats->triangle_count = triangles;
    stats->frame_count = model->frameCount;
    stats->skin_count = model->skinCount;
    stats->texture_request_count = static_cast<int>(model->requestNames.size());
    stats->textured_surface_count = textured;
    return PKM_OK;
}

int pkm_model_texture_request_count(const pkm_model *model) {
    return model == nullptr ? 0 : static_cast<int>(model->requestNames.size());
}

int pkm_model_texture_request_surface(const pkm_model *model, int index, char *name,
                                      size_t name_size) {
    if (model == nullptr || index < 0 ||
        static_cast<size_t>(index) >= model->requestSurfaces.size()) {
        return PKM_ERROR_INVALID_ARGUMENT;
    }
    return copyName(model->requestSurfaces[static_cast<size_t>(index)], name, name_size);
}

int pkm_model_texture_request_name(const pkm_model *model, int index, char *name,
                                   size_t name_size) {
    if (model == nullptr || index < 0 || static_cast<size_t>(index) >= model->requestNames.size()) {
        return PKM_ERROR_INVALID_ARGUMENT;
    }
    return copyName(model->requestNames[static_cast<size_t>(index)], name, name_size);
}

int pkm_model_set_texture(pkm_model *model, int index, const void *rgba_pixels, int width,
                          int height) {
    if (model == nullptr || rgba_pixels == nullptr || index < 0 ||
        static_cast<size_t>(index) >= model->textures.size() || width <= 0 || height <= 0 ||
        width > kMaxTextureDimension || height > kMaxTextureDimension) {
        return PKM_ERROR_INVALID_ARGUMENT;
    }

    try {
        Texture &texture = model->textures[static_cast<size_t>(index)];
        texture.width = width;
        texture.height = height;
        texture.smooth = true;
        const size_t byteCount =
            static_cast<size_t>(width) * static_cast<size_t>(height) * 4;
        texture.rgba.resize(byteCount);
        std::memcpy(texture.rgba.data(), rgba_pixels, byteCount);
        return PKM_OK;
    } catch (const std::bad_alloc &) {
        return PKM_ERROR_OUT_OF_MEMORY;
    } catch (...) {
        return PKM_ERROR_INVALID_ARGUMENT;
    }
}

int pkm_model_set_skin(pkm_model *model, int skin_index) {
    if (model == nullptr || model->format != PKM_FORMAT_MDL || skin_index < 0 ||
        skin_index >= model->skinCount) {
        return PKM_ERROR_INVALID_ARGUMENT;
    }
    model->activeSkin = skin_index;
    for (Surface &surface : model->surfaces) {
        surface.texture = skin_index;
    }
    return PKM_OK;
}

pkm_view *pkm_view_create(pkm_model *model) {
    if (model == nullptr) {
        return nullptr;
    }
    try {
        pkm_view *view = new pkm_view();
        view->model = model;
        view->target = model->center;
        view->goalTarget = model->center;
        if (model->faceOn) {
            /* A flat card reads as itself head-on, and gains nothing from a turntable. */
            view->homeYaw = 0.0f;
            view->homePitch = 0.0f;
            view->yaw = 0.0f;
            view->pitch = 0.0f;
            view->goalYaw = 0.0f;
            view->goalPitch = 0.0f;
            view->autoRotate = false;
        }
        view->frame(1.0f);
        view->goalDistance = view->frameDistance;
        view->distance = view->frameDistance;
        return view;
    } catch (...) {
        return nullptr;
    }
}

void pkm_view_destroy(pkm_view *view) { delete view; }

void pkm_view_set_dark_background(pkm_view *view, int dark) {
    if (view == nullptr) {
        return;
    }
    const bool value = dark != 0;
    if (view->dark != value) {
        view->dark = value;
        view->dirty = true;
    }
}

void pkm_view_set_auto_rotate(pkm_view *view, int enabled) {
    if (view == nullptr) {
        return;
    }
    view->autoRotate = enabled != 0;
    view->idleSeconds = 0.0;
}

void pkm_view_begin_interaction(pkm_view *view) {
    if (view == nullptr) {
        return;
    }
    view->interacting = true;
    view->hasInteracted = true;
    view->idleSeconds = 0.0;
    view->yawVelocity = 0.0f;
    view->pitchVelocity = 0.0f;
    view->dirty = true;
}

void pkm_view_orbit(pkm_view *view, float dx, float dy) {
    if (view == nullptr || !std::isfinite(dx) || !std::isfinite(dy)) {
        return;
    }

    const float width = static_cast<float>(std::max(view->width, 1));
    const float height = static_cast<float>(std::max(view->height, 1));
    const float yawDelta = -dx / width * 2.0f * kPi * 0.85f;
    const float pitchDelta = dy / height * kPi * 0.85f;

    view->goalYaw += yawDelta;
    view->goalPitch = clampf(view->goalPitch + pitchDelta, -kMaxPitch, kMaxPitch);
    view->pendingYaw += yawDelta;
    view->pendingPitch += pitchDelta;
    view->hasInteracted = true;
    view->idleSeconds = 0.0;
    view->dirty = true;
}

void pkm_view_pan(pkm_view *view, float dx, float dy) {
    if (view == nullptr || !std::isfinite(dx) || !std::isfinite(dy)) {
        return;
    }

    const Basis basis = makeBasis(*view);
    const float height = static_cast<float>(std::max(view->height, 1));
    const float worldPerPixel =
        2.0f * std::tan(kFieldOfView * 0.5f) * view->distance / height;

    Vec3 offset = basis.right * (-dx * worldPerPixel) + basis.up * (dy * worldPerPixel);
    Vec3 next = view->goalTarget + offset;

    /* Keep the model reachable no matter how far the user drags. */
    const Vec3 fromCenter = next - view->model->center;
    const float limit = view->model->radius * 1.5f;
    const float length = std::sqrt(dot(fromCenter, fromCenter));
    if (length > limit) {
        next = view->model->center + fromCenter * (limit / length);
    }

    view->goalTarget = next;
    view->hasInteracted = true;
    view->idleSeconds = 0.0;
    view->dirty = true;
}

void pkm_view_zoom(pkm_view *view, float zoom_steps) {
    if (view == nullptr || !std::isfinite(zoom_steps)) {
        return;
    }
    view->goalDistance = clampf(view->goalDistance * std::exp(-zoom_steps * 0.22f),
                                view->frameDistance * 0.28f, view->frameDistance * 2.8f);
    view->hasInteracted = true;
    view->idleSeconds = 0.0;
    view->dirty = true;
}

void pkm_view_end_interaction(pkm_view *view) {
    if (view == nullptr) {
        return;
    }
    view->interacting = false;
    view->idleSeconds = 0.0;
}

void pkm_view_nudge(pkm_view *view, int nudge) {
    if (view == nullptr) {
        return;
    }
    switch (nudge) {
        case PKM_NUDGE_LEFT:
            view->goalYaw += 0.13f;
            break;
        case PKM_NUDGE_RIGHT:
            view->goalYaw -= 0.13f;
            break;
        case PKM_NUDGE_UP:
            view->goalPitch = clampf(view->goalPitch + 0.1f, -kMaxPitch, kMaxPitch);
            break;
        case PKM_NUDGE_DOWN:
            view->goalPitch = clampf(view->goalPitch - 0.1f, -kMaxPitch, kMaxPitch);
            break;
        case PKM_NUDGE_IN:
            pkm_view_zoom(view, 1.0f);
            break;
        case PKM_NUDGE_OUT:
            pkm_view_zoom(view, -1.0f);
            break;
        default:
            return;
    }
    view->hasInteracted = true;
    view->idleSeconds = 0.0;
    view->dirty = true;
}

void pkm_view_reset(pkm_view *view) {
    if (view == nullptr) {
        return;
    }
    view->reset();
    view->hasInteracted = true;
    view->idleSeconds = 0.0;
}

int pkm_view_advance(pkm_view *view, double elapsed_seconds) {
    if (view == nullptr) {
        return 0;
    }

    const float dt = static_cast<float>(clampf(static_cast<float>(elapsed_seconds), 0.0f, 0.25f));
    if (dt <= 0.0f) {
        return view->dirty ? 1 : 0;
    }

    /* Sprite playback runs on its own clock, so a still camera keeps flipping frames. */
    if (view->model->animates()) {
        view->frameClock += dt;
        for (int step = 0; step < kMaxFrames; step++) {
            const double interval = static_cast<double>(view->model->frameInterval());
            if (view->frameClock < interval) {
                break;
            }
            view->frameClock -= interval;
            view->model->setFrame(view->model->activeFrame + 1);
            view->dirty = true;
        }
    }

    if (view->interacting) {
        /* Remember the drag speed so the model coasts when the button is released. */
        const float blend = 1.0f - std::exp(-dt / 0.06f);
        view->yawVelocity += (view->pendingYaw / dt - view->yawVelocity) * blend;
        view->pitchVelocity += (view->pendingPitch / dt - view->pitchVelocity) * blend;
    } else {
        view->goalYaw += view->yawVelocity * dt;
        view->goalPitch = clampf(view->goalPitch + view->pitchVelocity * dt, -kMaxPitch, kMaxPitch);
        const float decay = std::exp(-dt / 0.16f);
        view->yawVelocity *= decay;
        view->pitchVelocity *= decay;
        if (std::fabs(view->yawVelocity) < 0.002f) {
            view->yawVelocity = 0.0f;
        }
        if (std::fabs(view->pitchVelocity) < 0.002f) {
            view->pitchVelocity = 0.0f;
        }

        view->idleSeconds += dt;
        if (view->autoRotate && view->idleSeconds > kAutoRotateDelay &&
            view->yawVelocity == 0.0f && view->pitchVelocity == 0.0f) {
            view->goalYaw += kAutoRotateSpeed * dt;
        }
    }
    view->pendingYaw = 0.0f;
    view->pendingPitch = 0.0f;

    const float damping = 1.0f - std::exp(-dt / 0.075f);
    const float yawError = view->goalYaw - view->yaw;
    const float pitchError = view->goalPitch - view->pitch;
    const float distanceError = view->goalDistance - view->distance;
    const Vec3 targetError = view->goalTarget - view->target;

    view->yaw += yawError * damping;
    view->pitch += pitchError * damping;
    view->distance += distanceError * damping;
    view->target = view->target + targetError * damping;

    const float motion = std::fabs(yawError) + std::fabs(pitchError) +
                         std::fabs(distanceError) / std::max(view->frameDistance, 1e-4f) +
                         std::sqrt(dot(targetError, targetError)) /
                             std::max(view->model->radius, 1e-4f);

    if (motion > 1e-4f || view->interacting) {
        /* Stay at screen resolution while the camera is in motion. */
        view->supersample = false;
        view->settled = false;
        view->dirty = true;
    } else if (!view->settled) {
        view->snapToGoal();
        view->settled = true;
        view->supersample = true;
        view->dirty = true;
    }

    return view->dirty ? 1 : 0;
}

int pkm_view_show_interaction_prompt(const pkm_view *view) {
    return (view != nullptr && !view->hasInteracted) ? 1 : 0;
}

int pkm_view_render(pkm_view *view, void *bgra_pixels, int width, int height, int stride) {
    if (view == nullptr || bgra_pixels == nullptr || width <= 0 || height <= 0 ||
        width > kMaxRenderDimension || height > kMaxRenderDimension ||
        stride < width * 4) {
        return PKM_ERROR_INVALID_ARGUMENT;
    }

    try {
        const pkm_model &model = *view->model;
        const float aspect = static_cast<float>(width) / static_cast<float>(height);
        if (view->width != width || view->height != height) {
            view->width = width;
            view->height = height;
        }
        if (std::fabs(aspect - view->aspect) > 1e-4f) {
            view->frame(aspect);
            view->dirty = true;
        }

        int scale = view->supersample ? 2 : 1;
        while (scale > 1 && static_cast<long long>(width) * scale * height * scale >
                                static_cast<long long>(kMaxRenderPixels)) {
            scale--;
        }
        const int renderWidth = width * scale;
        const int renderHeight = height * scale;
        const size_t pixelCount =
            static_cast<size_t>(renderWidth) * static_cast<size_t>(renderHeight);

        /* The backdrop covers every pixel, so the colour buffer needs no clear. */
        view->color.resize(pixelCount * 4);
        view->depth.resize(pixelCount);

        if (view->backdropWidth != renderWidth || view->backdropHeight != renderHeight ||
            view->backdropDark != view->dark) {
            view->backdropCache.resize(pixelCount * 4);
            for (int y = 0; y < renderHeight; y++) {
                unsigned char *row = view->backdropCache.data() +
                                     static_cast<size_t>(y) * static_cast<size_t>(renderWidth) * 4;
                for (int x = 0; x < renderWidth; x++) {
                    const Color color = backdrop(view->dark, static_cast<float>(x),
                                                 static_cast<float>(y), renderWidth, renderHeight);
                    unsigned char *pixel = row + static_cast<size_t>(x) * 4;
                    pixel[0] = static_cast<unsigned char>(clampf(color.b, 0.0f, 1.0f) * 255.0f + 0.5f);
                    pixel[1] = static_cast<unsigned char>(clampf(color.g, 0.0f, 1.0f) * 255.0f + 0.5f);
                    pixel[2] = static_cast<unsigned char>(clampf(color.r, 0.0f, 1.0f) * 255.0f + 0.5f);
                    pixel[3] = 255;
                }
            }
            view->backdropWidth = renderWidth;
            view->backdropHeight = renderHeight;
            view->backdropDark = view->dark;
        }

        Projection projection;
        projection.basis = makeBasis(*view);
        projection.focal = 1.0f / std::tan(kFieldOfView * 0.5f);
        projection.aspect = aspect;
        projection.near = std::max(model.radius * 0.005f, 1e-4f);
        projection.width = renderWidth;
        projection.height = renderHeight;

        const Lighting lighting = makeLighting(projection.basis);

        /* Surfaces with no skin fall back to a neutral studio material. */
        const Color neutral{srgbToLinear(0.74f), srgbToLinear(0.74f), srgbToLinear(0.76f)};

        const auto drawBand = [&](int rowBegin, int rowEnd) {
            const size_t bandOffset =
                static_cast<size_t>(rowBegin) * static_cast<size_t>(renderWidth);
            const size_t bandPixels =
                static_cast<size_t>(rowEnd - rowBegin) * static_cast<size_t>(renderWidth);
            std::memcpy(view->color.data() + bandOffset * 4,
                        view->backdropCache.data() + bandOffset * 4, bandPixels * 4);
            std::fill_n(view->depth.data() + bandOffset, bandPixels, 0.0f);

            Rasterizer rasterizer(view->color.data(), view->depth.data(), renderWidth, rowBegin,
                                  rowEnd);

        for (const Surface &surface : model.surfaces) {
            const Texture *texture = model.textureFor(surface);

            auto shadeSurface = [&](int, int, const Vec3 &world, const Vec3 &rawNormal, float u,
                                    float v, Color &out) {
                Color base = neutral;
                float alpha = 1.0f;

                if (texture != nullptr) {
                    const Sample sample = sampleTexture(*texture, u, v);
                    base = sample.color;
                    alpha = sample.alpha;
                }
                if (alpha < 0.5f) {
                    return false;
                }
                /* Sprites are drawn fullbright in the game, so the rig stays off them. */
                if (surface.unlit) {
                    out = base;
                    return true;
                }

                Vec3 normal = normalize(rawNormal);
                const Vec3 toEye = normalize(projection.basis.eye - world);
                if (dot(normal, toEye) < 0.0f) {
                    normal = normal * -1.0f;  // two-sided, so thin geometry still shades
                }

                const float sky = clampf(normal.z * 0.5f + 0.5f, 0.0f, 1.0f);
                Color ambient;
                ambient.r = (0.22f + 0.20f * sky);
                ambient.g = (0.23f + 0.21f * sky);
                ambient.b = (0.26f + 0.22f * sky);

                const float key = std::max(0.0f, dot(normal, lighting.key));
                const float fill = std::max(0.0f, dot(normal, lighting.fill)) * 0.34f;

                /* Keep the rim on grazing angles so flat, dark faces stay dark. */
                const float facing = 1.0f - std::max(0.0f, dot(normal, toEye));
                const float grazing = facing * facing * facing;
                const float rim = grazing * std::max(0.0f, dot(normal, lighting.rim)) * 0.30f;

                const Vec3 half = normalize(lighting.key + toEye);
                const float specular = powi(std::max(0.0f, dot(normal, half)), 42) * 0.10f * key;

                out.r = base.r * (ambient.r + key * 0.92f + fill + rim * 0.85f) + specular;
                out.g = base.g * (ambient.g + key * 0.90f + fill + rim * 0.90f) + specular;
                out.b = base.b * (ambient.b + key * 0.86f + fill + rim * 1.00f) + specular;

                /* A soft shoulder keeps highlights from clipping to flat white. */
                out.r = shoulder(out.r);
                out.g = shoulder(out.g);
                out.b = shoulder(out.b);
                return true;
            };

            const auto emitSurface = [&](const ScreenVertex &a, const ScreenVertex &b,
                                         const ScreenVertex &c) {
                rasterizer.triangle(a, b, c, shadeSurface);
            };

            for (size_t index = 0; index + 2 < surface.indices.size(); index += 3) {
                const Vertex &a = surface.vertices[static_cast<size_t>(surface.indices[index])];
                const Vertex &b = surface.vertices[static_cast<size_t>(surface.indices[index + 1])];
                const Vertex &c = surface.vertices[static_cast<size_t>(surface.indices[index + 2])];
                clipAndEmit(projection, projection.toView(a.position, a.normal, a.u, a.v),
                            projection.toView(b.position, b.normal, b.u, b.v),
                            projection.toView(c.position, c.normal, c.u, c.v), emitSurface);
            }
        }
        };

        forEachBand(renderHeight, drawBand);

        unsigned char *destination = static_cast<unsigned char *>(bgra_pixels);
        forEachBand(height, [&](int rowBegin, int rowEnd) {
            for (int y = rowBegin; y < rowEnd; y++) {
                unsigned char *row =
                    destination + static_cast<size_t>(y) * static_cast<size_t>(stride);
                for (int x = 0; x < width; x++) {
                    int totals[4] = {0, 0, 0, 0};
                    for (int sy = 0; sy < scale; sy++) {
                        for (int sx = 0; sx < scale; sx++) {
                            const size_t offset =
                                (static_cast<size_t>(y * scale + sy) *
                                     static_cast<size_t>(renderWidth) +
                                 static_cast<size_t>(x * scale + sx)) * 4;
                            totals[0] += view->color[offset];
                            totals[1] += view->color[offset + 1];
                            totals[2] += view->color[offset + 2];
                            totals[3] += view->color[offset + 3];
                        }
                    }
                    const int samples = scale * scale;
                    unsigned char *pixel = row + static_cast<size_t>(x) * 4;
                    pixel[0] = static_cast<unsigned char>(totals[0] / samples);
                    pixel[1] = static_cast<unsigned char>(totals[1] / samples);
                    pixel[2] = static_cast<unsigned char>(totals[2] / samples);
                    pixel[3] = static_cast<unsigned char>(totals[3] / samples);
                }
            }
        });

        view->dirty = false;
        return PKM_OK;
    } catch (const std::bad_alloc &) {
        return PKM_ERROR_OUT_OF_MEMORY;
    } catch (...) {
        return PKM_ERROR_INVALID_ARGUMENT;
    }
}

}  // extern "C"
