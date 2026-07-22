cmake_minimum_required(VERSION 3.25)

project(PakScapeAudio VERSION 1.0.0 LANGUAGES C CXX)

include(FetchContent)
include(GNUInstallDirs)

set(BUILD_SHARED_LIBS OFF CACHE BOOL "Build dependencies as static libraries" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "Disable dependency tests" FORCE)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# All dependencies are pinned, checksummed, and permissively licensed. They are
# linked into one private native library so the app bundles only one audio ABI.
set(MINIAUDIO_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(MINIAUDIO_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(MINIAUDIO_BUILD_TOOLS OFF CACHE BOOL "" FORCE)
set(MINIAUDIO_NO_EXTRA_NODES ON CACHE BOOL "" FORCE)
set(MINIAUDIO_NO_LIBVORBIS ON CACHE BOOL "" FORCE)
set(MINIAUDIO_NO_LIBOPUS ON CACHE BOOL "" FORCE)
set(MINIAUDIO_INSTALL OFF CACHE BOOL "" FORCE)
FetchContent_Declare(
    miniaudio
    URL https://github.com/mackron/miniaudio/archive/refs/tags/0.11.25.tar.gz
    URL_HASH SHA256=b900edcffe979816e2560a0580b9b1216d674b4f17fbadeca8f777a7f8ab0274
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_MakeAvailable(miniaudio)

set(INSTALL_DOCS OFF CACHE BOOL "" FORCE)
set(INSTALL_PKG_CONFIG_MODULE OFF CACHE BOOL "" FORCE)
set(INSTALL_CMAKE_PACKAGE_MODULE OFF CACHE BOOL "" FORCE)
FetchContent_Declare(
    ogg
    URL https://github.com/xiph/ogg/archive/refs/tags/v1.3.6.tar.gz
    URL_HASH SHA256=95b643da661155d79db9de2fca55daed3a8d491039829def246aacb3d9201c81
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_MakeAvailable(ogg)

set(OPUS_BUILD_SHARED_LIBRARY OFF CACHE BOOL "" FORCE)
set(OPUS_BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(OPUS_BUILD_PROGRAMS OFF CACHE BOOL "" FORCE)
set(OPUS_INSTALL_PKG_CONFIG_MODULE OFF CACHE BOOL "" FORCE)
set(OPUS_INSTALL_CMAKE_CONFIG_MODULE OFF CACHE BOOL "" FORCE)
set(OPUS_DRED OFF CACHE BOOL "" FORCE)
set(OPUS_OSCE OFF CACHE BOOL "" FORCE)
FetchContent_Declare(
    opus
    URL https://github.com/xiph/opus/archive/refs/tags/v1.5.2.tar.gz
    URL_HASH SHA256=9480e329e989f70d69886ded470c7f8cfe6c0667cc4196d4837ac9e668fb7404
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_MakeAvailable(opus)

FetchContent_Declare(
    opusfile_source
    URL https://github.com/xiph/opusfile/archive/refs/tags/v0.12.tar.gz
    URL_HASH SHA256=a20a1dff1cdf0719d1e995112915e9966debf1470ee26bb31b2f510ccf00ef40
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_GetProperties(opusfile_source)
if(NOT opusfile_source_POPULATED)
    FetchContent_Populate(opusfile_source)
endif()
add_library(pakscape_opusfile STATIC
    "${opusfile_source_SOURCE_DIR}/src/info.c"
    "${opusfile_source_SOURCE_DIR}/src/internal.c"
    "${opusfile_source_SOURCE_DIR}/src/opusfile.c"
    "${opusfile_source_SOURCE_DIR}/src/stream.c")
target_include_directories(pakscape_opusfile
    PUBLIC "${opusfile_source_SOURCE_DIR}/include"
    PRIVATE "${opusfile_source_SOURCE_DIR}/src")
target_link_libraries(pakscape_opusfile PUBLIC Opus::opus Ogg::ogg)
if(MSVC)
    target_compile_definitions(pakscape_opusfile PRIVATE _CRT_SECURE_NO_WARNINGS)
endif()

FetchContent_Declare(
    openmpt_source
    URL https://lib.openmpt.org/files/libopenmpt/src/libopenmpt-0.8.7+release.autotools.tar.gz
    URL_HASH SHA256=275c29ef47be9992f62a35fcc96f7ca05c06d2fd05c9298b8dee9f743f75b089
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_GetProperties(openmpt_source)
if(NOT openmpt_source_POPULATED)
    FetchContent_Populate(openmpt_source)
endif()

file(GLOB_RECURSE OPENMPT_SOURCES CONFIGURE_DEPENDS
    "${openmpt_source_SOURCE_DIR}/common/*.cpp"
    "${openmpt_source_SOURCE_DIR}/soundbase/*.cpp"
    "${openmpt_source_SOURCE_DIR}/sounddsp/*.cpp"
    "${openmpt_source_SOURCE_DIR}/soundlib/*.cpp")
list(APPEND OPENMPT_SOURCES
    "${openmpt_source_SOURCE_DIR}/libopenmpt/libopenmpt_c.cpp"
    "${openmpt_source_SOURCE_DIR}/libopenmpt/libopenmpt_cxx.cpp"
    "${openmpt_source_SOURCE_DIR}/libopenmpt/libopenmpt_ext_impl.cpp"
    "${openmpt_source_SOURCE_DIR}/libopenmpt/libopenmpt_impl.cpp")
add_library(pakscape_openmpt STATIC ${OPENMPT_SOURCES})
target_compile_features(pakscape_openmpt PRIVATE cxx_std_17)
target_compile_definitions(pakscape_openmpt PRIVATE
    LIBOPENMPT_BUILD
    MPT_PACKAGE=true
    MPT_BUILD_SILENCE_LIBOPENMPT_CONFIGURATION_WARNINGS)
target_include_directories(pakscape_openmpt
    PUBLIC "${openmpt_source_SOURCE_DIR}"
    PRIVATE
        "${openmpt_source_SOURCE_DIR}/src"
        "${openmpt_source_SOURCE_DIR}/build/svn_version"
        "${openmpt_source_SOURCE_DIR}/libopenmpt"
        "${openmpt_source_SOURCE_DIR}/common"
        "${openmpt_source_SOURCE_DIR}/soundbase"
        "${openmpt_source_SOURCE_DIR}/sounddsp"
        "${openmpt_source_SOURCE_DIR}/soundlib")
if(MSVC)
    target_compile_options(pakscape_openmpt PRIVATE
        /W3 /wd4005 /wd4018 /wd4100 /wd4244 /wd4267 /wd4996)
    target_compile_definitions(pakscape_openmpt PRIVATE
        _CRT_SECURE_NO_WARNINGS
        _CRT_NONSTDC_NO_DEPRECATE
        NOMINMAX)
else()
    target_compile_options(pakscape_openmpt PRIVATE -w)
endif()

add_library(pakscape_stb_vorbis STATIC
    "${miniaudio_SOURCE_DIR}/extras/stb_vorbis.c")
target_include_directories(pakscape_stb_vorbis PUBLIC
    "${miniaudio_SOURCE_DIR}/extras")

add_library(pakscape_audio SHARED src/pakscape_audio.cpp)
target_compile_features(pakscape_audio PRIVATE cxx_std_17)
target_compile_definitions(pakscape_audio PRIVATE PAKSCAPE_AUDIO_BUILD)
target_include_directories(pakscape_audio
    PUBLIC "$<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>"
    PRIVATE "${miniaudio_SOURCE_DIR}/extras")
target_link_libraries(pakscape_audio PRIVATE
    miniaudio
    pakscape_stb_vorbis
    pakscape_opusfile
    pakscape_openmpt)

if(UNIX)
    find_package(Threads REQUIRED)
    target_link_libraries(pakscape_audio PRIVATE Threads::Threads ${CMAKE_DL_LIBS} m)
endif()
if(APPLE)
    find_library(CORE_AUDIO_FRAMEWORK CoreAudio REQUIRED)
    find_library(AUDIO_TOOLBOX_FRAMEWORK AudioToolbox REQUIRED)
    find_library(CORE_FOUNDATION_FRAMEWORK CoreFoundation REQUIRED)
    target_link_libraries(pakscape_audio PRIVATE
        ${CORE_AUDIO_FRAMEWORK}
        ${AUDIO_TOOLBOX_FRAMEWORK}
        ${CORE_FOUNDATION_FRAMEWORK})
    set_target_properties(pakscape_audio PROPERTIES
        BUILD_WITH_INSTALL_RPATH TRUE
        INSTALL_NAME_DIR "@rpath")
    target_link_options(pakscape_audio PRIVATE
        "LINKER:-exported_symbols_list,${CMAKE_CURRENT_SOURCE_DIR}/exports-macos.txt")
elseif(UNIX)
    target_link_options(pakscape_audio PRIVATE
        "LINKER:--version-script=${CMAKE_CURRENT_SOURCE_DIR}/exports-linux.map")
endif()

set_target_properties(pakscape_audio PROPERTIES
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES
    OUTPUT_NAME pakscape_audio)

install(TARGETS pakscape_audio
    RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR}
    LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR})
install(FILES include/pakscape_audio.h DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})

enable_testing()
add_executable(pakscape_audio_api_tests tests/api_tests.cpp)
target_link_libraries(pakscape_audio_api_tests PRIVATE pakscape_audio)
add_test(NAME pakscape_audio_api_tests COMMAND pakscape_audio_api_tests)
