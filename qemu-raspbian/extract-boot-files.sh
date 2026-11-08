#!/bin/bash

set -euo pipefail

: "${IMG_QCOW2:?IMG_QCOW2 must be set}"
: "${MNT_DIR:?MNT_DIR must be set}"

NBD_DEVICE=/dev/nbd0
BOOT_MOUNTED=0
NBD_CONNECTED=0
BOOT_FILES=(kernel8.img bcm2710-rpi-3-b.dtb bcm2711-rpi-4-b.dtb)
ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
boot_file=

cleanup() {
    if (( BOOT_MOUNTED )); then
        sudo umount "${MNT_DIR}/boot" || true
    fi
    if (( NBD_CONNECTED )); then
        sudo qemu-nbd --disconnect "${NBD_DEVICE}" || true
    fi
}

trap cleanup EXIT

sudo qemu-nbd -c "${NBD_DEVICE}" "${IMG_QCOW2}"
NBD_CONNECTED=1
sudo udevadm settle
sudo mount -t vfat "${NBD_DEVICE}p1" "${MNT_DIR}/boot"
BOOT_MOUNTED=1
for boot_file in "${BOOT_FILES[@]}"; do
    cp "${MNT_DIR}/boot/${boot_file}" "${ROOT_DIR}/"
done
sync --file-system "${MNT_DIR}/boot"

sudo umount "${MNT_DIR}/boot"
BOOT_MOUNTED=0
sync "${IMG_QCOW2}"
sudo qemu-nbd --disconnect "${NBD_DEVICE}"
NBD_CONNECTED=0
trap - EXIT
