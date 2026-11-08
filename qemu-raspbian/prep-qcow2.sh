#!/bin/bash

# bail if any unset variable is referenced
set -eu

NBD_DEVICE=/dev/nbd0
NBD_CONNECTED=0
BOOT_MOUNTED=0
ROOT_MOUNTED=0

cleanup() {
  if (( ROOT_MOUNTED )); then
    sudo umount "${MNT_DIR}/root" || true
  fi
  if (( BOOT_MOUNTED )); then
    sudo umount "${MNT_DIR}/boot" || true
  fi
  if (( NBD_CONNECTED )); then
    sudo qemu-nbd --disconnect "${NBD_DEVICE}" || true
  fi
}

trap cleanup EXIT

# Return the smallest power-of-two GiB size that can contain the image.
# Work in bytes so a 2.57 GiB source rounds up to 4 GiB instead of being
# truncated to 2 GiB and accidentally treated as a shrink operation.
function qcow2Pow2RoundUp() {
  local image_info image_size target_size=1073741824
  image_info="$(qemu-img info "$IMG_QCOW2")"
  image_size="$(sed -n '0,/bytes)/s/.*(\([0-9][0-9]*\) bytes).*/\1/p' <<<"$image_info")"
  if ! [[ "$image_size" =~ ^[0-9]+$ ]]; then
    echo "Could not determine qcow2 virtual size." >&2
    return 1
  fi
  while (( target_size < image_size )); do
    target_size=$((target_size * 2))
  done
  echo "$target_size"
}

qemu-img convert -f raw -O qcow2 -o compression_type=zstd "${IMG}"  "${IMG_QCOW2}"
qemu-img resize "${IMG_QCOW2}" "$(qcow2Pow2RoundUp)"
sudo qemu-nbd -c "${NBD_DEVICE}" "${IMG_QCOW2}"
NBD_CONNECTED=1
sudo udevadm settle
echo  ',+' | sudo sfdisk -N2 "${NBD_DEVICE}"
sudo resize2fs "${NBD_DEVICE}p2"
sudo mount -t vfat "${NBD_DEVICE}p1" "${MNT_DIR}"/boot
BOOT_MOUNTED=1
sudo mount -t ext4 "${NBD_DEVICE}p2" "${MNT_DIR}"/root
ROOT_MOUNTED=1
echo "${USER_PASSWD}" | sudo tee "${MNT_DIR}"/boot/userconf 2>/dev/null 1>&2
echo -e 'otg_mode=0\ndtoverlay=dwc2' | sudo tee -a "${MNT_DIR}"/boot/config.txt 2>/dev/null 1>&2
sync --file-system "${MNT_DIR}"/boot && sleep 0.5
sudo ln -sf /lib/systemd/system/ssh.service "${MNT_DIR}"/root/etc/systemd/system/multi-user.target.wants/ssh.service
if [ -e "$HOME/.ssh/authorized_keys" ]; then
  mkdir -p "${MNT_DIR}"/root/home/pi/.ssh/
  cp "$HOME/.ssh/authorized_keys" "${MNT_DIR}"/root/home/pi/.ssh/
  chown -R 1000:1000 "${MNT_DIR}"/root/home/pi/.ssh/
  chmod 0700 "${MNT_DIR}"/root/home/pi/.ssh/
  chmod 0600 "${MNT_DIR}"/root/home/pi/.ssh/authorized_keys
fi
sync --file-system "${MNT_DIR}"/root && sleep 0.5
sudo umount "${MNT_DIR}"/boot
BOOT_MOUNTED=0
sudo umount "${MNT_DIR}"/root
ROOT_MOUNTED=0
sync "${IMG_QCOW2}" && sleep 0.5
sudo qemu-nbd --disconnect "${NBD_DEVICE}"
NBD_CONNECTED=0
trap - EXIT

