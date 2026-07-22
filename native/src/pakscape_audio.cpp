#include "pakscape_audio.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <memory>
#include <new>
#include <string>
#include <vector>

#include <miniaudio.h>
#include <libopenmpt/libopenmpt.h>
#include <opusfile.h>

extern "C" {
#define STB_VORBIS_HEADER_ONLY
#include <stb_vorbis.c>
}

namespace {

constexpr size_t kMaximumInputSize = 128u * 1024u * 1024u;
constexpr std::uint32_t kTrackerSampleRate = 48'000;
constexpr std::uint32_t kOutputChannels = 2;

enum class DecoderKind {
    miniaudio,
    vorbis,
    opus,
    openmpt,
};

std::string normalized_extension(const char *extension) {
    if (extension == nullptr) {
        return {};
    }

    std::string result(extension);
    if (!result.empty() && result.front() == '.') {
        result.erase(result.begin());
    }
    std::transform(result.begin(), result.end(), result.begin(), [](unsigned char value) {
        return static_cast<char>(value >= 'A' && value <= 'Z' ? value + ('a' - 'A') : value);
    });
    return result;
}

bool is_miniaudio_extension(const std::string &extension) {
    return extension == "wav" || extension == "mp3" || extension == "flac";
}

bool is_tracker_extension(const std::string &extension) {
    return extension == "it" || extension == "s3m" || extension == "xm" ||
           extension == "mod" || extension == "umx";
}

void write_error(char *buffer, size_t buffer_size, const char *message) {
    if (buffer == nullptr || buffer_size == 0) {
        return;
    }
    std::snprintf(buffer, buffer_size, "%s", message != nullptr ? message : "Unknown audio error.");
}

} // namespace

struct pka_player {
    DecoderKind decoder_kind = DecoderKind::miniaudio;
    std::vector<std::uint8_t> encoded_data;

    ma_decoder miniaudio_decoder{};
    bool miniaudio_decoder_initialized = false;
    stb_vorbis *vorbis_decoder = nullptr;
    OggOpusFile *opus_decoder = nullptr;
    openmpt_module *openmpt_decoder = nullptr;

    ma_device device{};
    bool device_initialized = false;
    std::uint32_t sample_rate = 0;
    std::uint64_t length_frames = 0;
    std::atomic<std::uint64_t> position_frames{0};
    std::atomic<bool> playing{false};
    std::atomic<bool> finished{false};
};

namespace {

std::uint64_t read_frames(pka_player *player, float *output, std::uint64_t requested_frames) {
    switch (player->decoder_kind) {
        case DecoderKind::miniaudio: {
            ma_uint64 frames_read = 0;
            const ma_result result = ma_decoder_read_pcm_frames(
                &player->miniaudio_decoder,
                output,
                requested_frames,
                &frames_read);
            return result == MA_SUCCESS || result == MA_AT_END ? frames_read : 0;
        }
        case DecoderKind::vorbis:
            return static_cast<std::uint64_t>(stb_vorbis_get_samples_float_interleaved(
                player->vorbis_decoder,
                static_cast<int>(kOutputChannels),
                output,
                static_cast<int>(requested_frames * kOutputChannels)));
        case DecoderKind::opus: {
            const int frames_read = op_read_float_stereo(
                player->opus_decoder,
                output,
                static_cast<int>(std::min<std::uint64_t>(
                    requested_frames * kOutputChannels,
                    static_cast<std::uint64_t>(std::numeric_limits<int>::max()))));
            return frames_read > 0 ? static_cast<std::uint64_t>(frames_read) : 0;
        }
        case DecoderKind::openmpt:
            return static_cast<std::uint64_t>(openmpt_module_read_interleaved_float_stereo(
                player->openmpt_decoder,
                static_cast<std::int32_t>(player->sample_rate),
                static_cast<size_t>(requested_frames),
                output));
    }
    return 0;
}

bool seek_decoder(pka_player *player, std::uint64_t target_frame) {
    switch (player->decoder_kind) {
        case DecoderKind::miniaudio:
            return ma_decoder_seek_to_pcm_frame(&player->miniaudio_decoder, target_frame) == MA_SUCCESS;
        case DecoderKind::vorbis:
            return target_frame <= std::numeric_limits<unsigned int>::max() &&
                   stb_vorbis_seek(player->vorbis_decoder, static_cast<unsigned int>(target_frame)) != 0;
        case DecoderKind::opus:
            return target_frame <= static_cast<std::uint64_t>(std::numeric_limits<opus_int64>::max()) &&
                   op_pcm_seek(player->opus_decoder, static_cast<opus_int64>(target_frame)) == 0;
        case DecoderKind::openmpt: {
            const double target_seconds = static_cast<double>(target_frame) / player->sample_rate;
            return openmpt_module_set_position_seconds(player->openmpt_decoder, target_seconds) >= 0;
        }
    }
    return false;
}

void audio_callback(ma_device *device, void *output, const void *, ma_uint32 frame_count) {
    auto *player = static_cast<pka_player *>(device->pUserData);
    auto *samples = static_cast<float *>(output);
    if (player == nullptr || !player->playing.load(std::memory_order_acquire)) {
        std::fill_n(samples, static_cast<size_t>(frame_count) * kOutputChannels, 0.0f);
        return;
    }

    const std::uint64_t frames_read = read_frames(player, samples, frame_count);
    if (frames_read < frame_count) {
        std::fill_n(
            samples + (frames_read * kOutputChannels),
            static_cast<size_t>(frame_count - frames_read) * kOutputChannels,
            0.0f);
        player->finished.store(true, std::memory_order_release);
        player->playing.store(false, std::memory_order_release);
    }

    const std::uint64_t previous = player->position_frames.load(std::memory_order_relaxed);
    player->position_frames.store(
        std::min(previous + frames_read, player->length_frames),
        std::memory_order_release);
}

bool initialize_miniaudio_decoder(pka_player *player) {
    ma_decoder_config config = ma_decoder_config_init(ma_format_f32, kOutputChannels, 0);
    if (ma_decoder_init_memory(
            player->encoded_data.data(),
            player->encoded_data.size(),
            &config,
            &player->miniaudio_decoder) != MA_SUCCESS) {
        return false;
    }
    player->miniaudio_decoder_initialized = true;
    player->sample_rate = player->miniaudio_decoder.outputSampleRate;

    ma_uint64 length = 0;
    if (ma_decoder_get_length_in_pcm_frames(&player->miniaudio_decoder, &length) != MA_SUCCESS) {
        return false;
    }
    player->length_frames = length;
    return length > 0 && player->sample_rate > 0;
}

bool initialize_vorbis_decoder(pka_player *player) {
    if (player->encoded_data.size() > static_cast<size_t>(std::numeric_limits<int>::max())) {
        return false;
    }
    int error = VORBIS__no_error;
    player->vorbis_decoder = stb_vorbis_open_memory(
        player->encoded_data.data(),
        static_cast<int>(player->encoded_data.size()),
        &error,
        nullptr);
    if (player->vorbis_decoder == nullptr) {
        return false;
    }

    const stb_vorbis_info info = stb_vorbis_get_info(player->vorbis_decoder);
    player->sample_rate = info.sample_rate;
    player->length_frames = stb_vorbis_stream_length_in_samples(player->vorbis_decoder);
    return player->length_frames > 0 && player->sample_rate > 0;
}

bool initialize_opus_decoder(pka_player *player) {
    if (player->encoded_data.size() > static_cast<size_t>(std::numeric_limits<opus_int32>::max())) {
        return false;
    }
    int error = 0;
    player->opus_decoder = op_open_memory(
        player->encoded_data.data(),
        static_cast<opus_int32>(player->encoded_data.size()),
        &error);
    if (player->opus_decoder == nullptr) {
        return false;
    }

    const opus_int64 length = op_pcm_total(player->opus_decoder, -1);
    if (length <= 0) {
        return false;
    }
    player->sample_rate = kTrackerSampleRate;
    player->length_frames = static_cast<std::uint64_t>(length);
    return true;
}

bool initialize_openmpt_decoder(pka_player *player, std::string &detail) {
    int error = 0;
    const char *error_message = nullptr;
    player->openmpt_decoder = openmpt_module_create_from_memory2(
        player->encoded_data.data(),
        player->encoded_data.size(),
        openmpt_log_func_silent,
        nullptr,
        openmpt_error_func_store,
        &error,
        &error,
        &error_message,
        nullptr);
    if (player->openmpt_decoder == nullptr) {
        if (error_message != nullptr) {
            detail = error_message;
            openmpt_free_string(error_message);
        }
        return false;
    }
    if (error_message != nullptr) {
        openmpt_free_string(error_message);
    }

    openmpt_module_set_repeat_count(player->openmpt_decoder, 0);
    const double duration = openmpt_module_get_duration_seconds(player->openmpt_decoder);
    if (!std::isfinite(duration) || duration <= 0 || duration > 24.0 * 60.0 * 60.0) {
        detail = "The module has an invalid or excessive duration.";
        return false;
    }
    player->sample_rate = kTrackerSampleRate;
    player->length_frames = static_cast<std::uint64_t>(std::ceil(duration * player->sample_rate));
    return player->length_frames > 0;
}

void uninitialize_decoder(pka_player *player) {
    if (player->miniaudio_decoder_initialized) {
        ma_decoder_uninit(&player->miniaudio_decoder);
        player->miniaudio_decoder_initialized = false;
    }
    if (player->vorbis_decoder != nullptr) {
        stb_vorbis_close(player->vorbis_decoder);
        player->vorbis_decoder = nullptr;
    }
    if (player->opus_decoder != nullptr) {
        op_free(player->opus_decoder);
        player->opus_decoder = nullptr;
    }
    if (player->openmpt_decoder != nullptr) {
        openmpt_module_destroy(player->openmpt_decoder);
        player->openmpt_decoder = nullptr;
    }
}

} // namespace

extern "C" {

int pka_supports_extension(const char *extension) {
    try {
        const std::string value = normalized_extension(extension);
        return is_miniaudio_extension(value) || value == "ogg" || value == "opus" ||
               is_tracker_extension(value);
    } catch (...) {
        return 0;
    }
}

pka_player *pka_player_create(
    const void *encoded_data,
    size_t encoded_size,
    const char *extension,
    char *error_message,
    size_t error_message_size) {
    std::unique_ptr<pka_player> player;
    try {
        if (encoded_data == nullptr || encoded_size == 0 || encoded_size > kMaximumInputSize ||
            !pka_supports_extension(extension)) {
            write_error(error_message, error_message_size, "Invalid or unsupported audio data.");
            return nullptr;
        }

        player = std::make_unique<pka_player>();
        const auto *bytes = static_cast<const std::uint8_t *>(encoded_data);
        player->encoded_data.assign(bytes, bytes + encoded_size);

        const std::string value = normalized_extension(extension);
        std::string decode_detail;
        bool initialized = false;
        if (is_miniaudio_extension(value)) {
            player->decoder_kind = DecoderKind::miniaudio;
            initialized = initialize_miniaudio_decoder(player.get());
        } else if (value == "ogg") {
            player->decoder_kind = DecoderKind::vorbis;
            initialized = initialize_vorbis_decoder(player.get());
        } else if (value == "opus") {
            player->decoder_kind = DecoderKind::opus;
            initialized = initialize_opus_decoder(player.get());
        } else {
            player->decoder_kind = DecoderKind::openmpt;
            initialized = initialize_openmpt_decoder(player.get(), decode_detail);
        }

        if (!initialized) {
            uninitialize_decoder(player.get());
            write_error(
                error_message,
                error_message_size,
                decode_detail.empty() ? "The audio decoder could not read this file." : decode_detail.c_str());
            return nullptr;
        }

        ma_device_config device_config = ma_device_config_init(ma_device_type_playback);
        device_config.playback.format = ma_format_f32;
        device_config.playback.channels = kOutputChannels;
        device_config.sampleRate = player->sample_rate;
        device_config.dataCallback = audio_callback;
        device_config.pUserData = player.get();
        if (ma_device_init(nullptr, &device_config, &player->device) != MA_SUCCESS) {
            uninitialize_decoder(player.get());
            write_error(error_message, error_message_size, "The system audio output could not be opened.");
            return nullptr;
        }
        player->device_initialized = true;
        ma_device_set_master_volume(&player->device, 0.8f);
        write_error(error_message, error_message_size, "");
        return player.release();
    } catch (const std::bad_alloc &) {
        if (player != nullptr) {
            if (player->device_initialized) {
                ma_device_uninit(&player->device);
                player->device_initialized = false;
            }
            uninitialize_decoder(player.get());
        }
        write_error(error_message, error_message_size, "Not enough memory to prepare this audio file.");
        return nullptr;
    } catch (...) {
        if (player != nullptr) {
            if (player->device_initialized) {
                ma_device_uninit(&player->device);
                player->device_initialized = false;
            }
            uninitialize_decoder(player.get());
        }
        write_error(error_message, error_message_size, "The audio player could not be initialized.");
        return nullptr;
    }
}

void pka_player_destroy(pka_player *player) {
    if (player == nullptr) {
        return;
    }
    player->playing.store(false, std::memory_order_release);
    if (player->device_initialized) {
        ma_device_uninit(&player->device);
        player->device_initialized = false;
    }
    uninitialize_decoder(player);
    delete player;
}

int pka_player_play(pka_player *player) {
    if (player == nullptr || !player->device_initialized) {
        return PKA_ERROR_INVALID_ARGUMENT;
    }
    if (player->finished.load(std::memory_order_acquire) && pka_player_seek(player, 0) != PKA_OK) {
        return PKA_ERROR_DECODE;
    }
    player->playing.store(true, std::memory_order_release);
    if (ma_device_start(&player->device) != MA_SUCCESS) {
        player->playing.store(false, std::memory_order_release);
        return PKA_ERROR_AUDIO_DEVICE;
    }
    return PKA_OK;
}

int pka_player_pause(pka_player *player) {
    if (player == nullptr || !player->device_initialized) {
        return PKA_ERROR_INVALID_ARGUMENT;
    }
    player->playing.store(false, std::memory_order_release);
    return ma_device_stop(&player->device) == MA_SUCCESS ? PKA_OK : PKA_ERROR_AUDIO_DEVICE;
}

int pka_player_seek(pka_player *player, double seconds) {
    if (player == nullptr || !std::isfinite(seconds) || player->sample_rate == 0) {
        return PKA_ERROR_INVALID_ARGUMENT;
    }

    const bool resume = player->playing.exchange(false, std::memory_order_acq_rel);
    if (player->device_initialized && ma_device_stop(&player->device) != MA_SUCCESS) {
        return PKA_ERROR_AUDIO_DEVICE;
    }

    const double duration = pka_player_duration(player);
    const double clamped = std::clamp(seconds, 0.0, duration);
    const std::uint64_t target = std::min(
        static_cast<std::uint64_t>(std::llround(clamped * player->sample_rate)),
        player->length_frames);
    if (!seek_decoder(player, target)) {
        return PKA_ERROR_DECODE;
    }

    player->position_frames.store(target, std::memory_order_release);
    player->finished.store(target >= player->length_frames, std::memory_order_release);
    if (resume && target < player->length_frames) {
        player->playing.store(true, std::memory_order_release);
        if (ma_device_start(&player->device) != MA_SUCCESS) {
            player->playing.store(false, std::memory_order_release);
            return PKA_ERROR_AUDIO_DEVICE;
        }
    }
    return PKA_OK;
}

double pka_player_duration(const pka_player *player) {
    return player != nullptr && player->sample_rate > 0
        ? static_cast<double>(player->length_frames) / player->sample_rate
        : 0;
}

double pka_player_position(const pka_player *player) {
    return player != nullptr && player->sample_rate > 0
        ? static_cast<double>(player->position_frames.load(std::memory_order_acquire)) / player->sample_rate
        : 0;
}

int pka_player_is_playing(const pka_player *player) {
    return player != nullptr && player->playing.load(std::memory_order_acquire) ? 1 : 0;
}

int pka_player_is_finished(const pka_player *player) {
    return player != nullptr && player->finished.load(std::memory_order_acquire) ? 1 : 0;
}

} // extern "C"
