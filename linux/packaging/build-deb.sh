#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "${script_directory}/../.." && pwd)"
version="${1:-1.0.1}"
runtime_identifier="${2:-linux-x64}"
dotnet_command="${DOTNET_COMMAND:-dotnet}"

if [[ ! "${version}" =~ ^[0-9]+([.][0-9]+){1,3}([+-][0-9A-Za-z.-]+)?$ ]]; then
    echo "Invalid Debian version: ${version}" >&2
    exit 2
fi

case "${runtime_identifier}" in
    linux-x64)
        debian_architecture="amd64"
        ;;
    linux-arm64)
        debian_architecture="arm64"
        ;;
    *)
        echo "Unsupported runtime identifier: ${runtime_identifier}" >&2
        exit 2
        ;;
esac

if ! command -v "${dotnet_command}" >/dev/null 2>&1; then
    echo "Required .NET command not found: ${dotnet_command}" >&2
    exit 1
fi
for command_name in dpkg-deb find install sed tar; do
    if ! command -v "${command_name}" >/dev/null 2>&1; then
        echo "Required command not found: ${command_name}" >&2
        exit 1
    fi
done

artifacts_directory="${repository_root}/linux/artifacts"
publish_directory="${artifacts_directory}/publish/${runtime_identifier}"
package_root="${artifacts_directory}/package/${runtime_identifier}"
package_name="pakscape_${version}_${debian_architecture}"

rm -rf "${publish_directory}" "${package_root}"
mkdir -p "${publish_directory}" "${package_root}/DEBIAN"

"${dotnet_command}" publish "${repository_root}/linux/PakScape.Linux/PakScape.Linux.csproj" \
    --configuration Release \
    --runtime "${runtime_identifier}" \
    --self-contained true \
    --output "${publish_directory}" \
    -p:DebugType=None \
    -p:Version="${version}" \
    -p:PublishReadyToRun=false \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false

find "${publish_directory}" -type d -exec chmod 0755 {} +
find "${publish_directory}" -type f -exec chmod 0644 {} +
chmod 0755 \
    "${publish_directory}/PakScape" \
    "${publish_directory}/createdump"

install -d \
    "${package_root}/opt/pakscape" \
    "${package_root}/usr/bin" \
    "${package_root}/usr/share/applications" \
    "${package_root}/usr/share/icons/hicolor/16x16/apps" \
    "${package_root}/usr/share/icons/hicolor/32x32/apps" \
    "${package_root}/usr/share/icons/hicolor/64x64/apps" \
    "${package_root}/usr/share/icons/hicolor/128x128/apps" \
    "${package_root}/usr/share/icons/hicolor/256x256/apps" \
    "${package_root}/usr/share/icons/hicolor/512x512/apps" \
    "${package_root}/usr/share/mime/packages" \
    "${package_root}/usr/share/doc/pakscape"
cp -a "${publish_directory}/." "${package_root}/opt/pakscape/"
chmod 0755 "${package_root}/opt/pakscape/PakScape"
ln -s /opt/pakscape/PakScape "${package_root}/usr/bin/pakscape"

install -m 0644 \
    "${script_directory}/io.github.timbergeron.PakScape.desktop" \
    "${package_root}/usr/share/applications/io.github.timbergeron.PakScape.desktop"
install -m 0644 \
    "${script_directory}/io.github.timbergeron.PakScape.xml" \
    "${package_root}/usr/share/mime/packages/io.github.timbergeron.PakScape.xml"
for icon_size in 16 32 64 128 256 512; do
    case "${icon_size}" in
        16) icon_source="AppIcon_16x16@1x.png" ;;
        32) icon_source="AppIcon_16x16@2x.png" ;;
        64) icon_source="AppIcon_32x32@2x.png" ;;
        128) icon_source="AppIcon_128x128@1x.png" ;;
        256) icon_source="AppIcon_256x256@1x.png" ;;
        512) icon_source="AppIcon_256x256@2x.png" ;;
    esac
    install -m 0644 \
        "${repository_root}/macos/PakScape/Assets.xcassets/AppIcon.appiconset/${icon_source}" \
        "${package_root}/usr/share/icons/hicolor/${icon_size}x${icon_size}/apps/io.github.timbergeron.PakScape.png"
done
install -m 0644 \
    "${script_directory}/copyright" \
    "${package_root}/usr/share/doc/pakscape/copyright"
install -m 0755 "${script_directory}/postinst" "${package_root}/DEBIAN/postinst"
install -m 0755 "${script_directory}/postrm" "${package_root}/DEBIAN/postrm"

find "${package_root}" -type d -exec chmod 0755 {} +
find "${package_root}" -type f -exec chmod 0644 {} +
chmod 0755 \
    "${package_root}/opt/pakscape/PakScape" \
    "${package_root}/opt/pakscape/createdump" \
    "${package_root}/DEBIAN/postinst" \
    "${package_root}/DEBIAN/postrm"

installed_size="$(du -sk "${package_root}" | cut -f1)"
sed \
    -e "s/@VERSION@/${version}/g" \
    -e "s/@ARCHITECTURE@/${debian_architecture}/g" \
    -e "s/@INSTALLED_SIZE@/${installed_size}/g" \
    "${script_directory}/debian-control.in" > "${package_root}/DEBIAN/control"

dpkg-deb --root-owner-group --build \
    "${package_root}" \
    "${artifacts_directory}/${package_name}.deb"
tar -C "${publish_directory}" -czf \
    "${artifacts_directory}/${package_name}.tar.gz" \
    .

echo "Built ${artifacts_directory}/${package_name}.deb"
echo "Built ${artifacts_directory}/${package_name}.tar.gz"
