#include "pakscape_audio.h"

#include <array>
#include <cassert>
#include <cstdint>
#include <vector>

namespace {

void append_u16(std::vector<unsigned char> &bytes, std::uint16_t value) {
    bytes.push_back(static_cast<unsigned char>(value));
    bytes.push_back(static_cast<unsigned char>(value >> 8));
}

void append_u32(std::vector<unsigned char> &bytes, std::uint32_t value) {
    bytes.push_back(static_cast<unsigned char>(value));
    bytes.push_back(static_cast<unsigned char>(value >> 8));
    bytes.push_back(static_cast<unsigned char>(value >> 16));
    bytes.push_back(static_cast<unsigned char>(value >> 24));
}

std::vector<unsigned char> silent_wave() {
    constexpr std::uint32_t sample_rate = 8'000;
    constexpr std::uint32_t sample_count = 800;
    constexpr std::uint32_t data_size = sample_count * 2;
    std::vector<unsigned char> bytes;
    bytes.reserve(44 + data_size);
    bytes.insert(bytes.end(), {'R', 'I', 'F', 'F'});
    append_u32(bytes, 36 + data_size);
    bytes.insert(bytes.end(), {'W', 'A', 'V', 'E', 'f', 'm', 't', ' '});
    append_u32(bytes, 16);
    append_u16(bytes, 1);
    append_u16(bytes, 1);
    append_u32(bytes, sample_rate);
    append_u32(bytes, sample_rate * 2);
    append_u16(bytes, 2);
    append_u16(bytes, 16);
    bytes.insert(bytes.end(), {'d', 'a', 't', 'a'});
    append_u32(bytes, data_size);
    bytes.resize(44 + data_size, 0);
    return bytes;
}

} // namespace

int main() {
    constexpr std::array<const char *, 10> supported = {
        "wav", ".mp3", "FLAC", "ogg", "opus", "it", "s3m", "xm", "mod", "umx",
    };
    for (const char *extension : supported) {
        assert(pka_supports_extension(extension) == 1);
    }

    assert(pka_supports_extension("aac") == 0);
    assert(pka_supports_extension("wma") == 0);
    assert(pka_supports_extension(nullptr) == 0);

    const unsigned char invalid[] = {0x00, 0x01, 0x02, 0x03};
    for (const char *extension : supported) {
        char error[128]{};
        pka_player *player = pka_player_create(
            invalid,
            sizeof(invalid),
            extension,
            error,
            sizeof(error));
        assert(player == nullptr);
        assert(error[0] != '\0');
    }

    const std::vector<unsigned char> wave = silent_wave();
    char wave_error[128]{};
    pka_player *wave_player = pka_player_create(
        wave.data(),
        wave.size(),
        "wav",
        wave_error,
        sizeof(wave_error));
    assert(wave_player != nullptr);
    assert(pka_player_duration(wave_player) > 0.09);
    assert(pka_player_duration(wave_player) < 0.11);
    assert(pka_player_seek(wave_player, 0.05) == PKA_OK);
    assert(pka_player_position(wave_player) > 0.04);
    pka_player_destroy(wave_player);
    return 0;
}
