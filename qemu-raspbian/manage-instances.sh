#!/bin/bash

set -euo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
INSTANCES_DIR="${ROOT_DIR}/instances"
CONFIGURATION_DIR="${ROOT_DIR}/configuration"
NBD_DEVICE="/dev/nbd0"
MIN_PREP_KIB=$((24 * 1024 * 1024))
MIN_RUN_KIB=$((9 * 1024 * 1024))

INSTANCE_NAMES=(pi4-1 pi4-2 pi4-3 pi4-4)
INSTANCE_PORTS=(5555 5556 5557 5558)
INSTANCE_UUIDS=(
    8e80dca2-b3b5-4ec3-9f01-000000000001
    8e80dca2-b3b5-4ec3-9f01-000000000002
    8e80dca2-b3b5-4ec3-9f01-000000000003
    8e80dca2-b3b5-4ec3-9f01-000000000004
)
INSTANCE_MACS=(
    52:54:00:04:00:01
    52:54:00:04:00:02
    52:54:00:04:00:03
    52:54:00:04:00:04
)

usage() {
    cat <<'EOF'
Usage: manage-instances.sh COMMAND [INSTANCE]

Commands:
  prep                 Prepare the base image and four standalone disks
  reset                Delete all four disks when CONFIRM_RESET is provided
  update-passwords     Apply USER_PASSWD to the base and all instance disks
  run-all              Start all four instances
  stop-all             Request graceful shutdown for all instances
  force-stop-all       Terminate all running instances
  status               Show disk and process status
  run INSTANCE         Start one instance (pi4-1 through pi4-4)
  connect INSTANCE     Start one instance headlessly, wait, and open SSH
  sync-replay-config INSTANCE
                       Copy configuration/INSTANCE.json into the running guest
  sync-replay-config-all
                       Copy all four configs; every guest must be reachable
  reset-replay-state INSTANCE
                       Reset replay SQLite state; refuses while replay is running
  stop INSTANCE        Request graceful shutdown for one instance
  force-stop INSTANCE  Terminate one instance
EOF
}

die() {
    echo "ERROR: $*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || die "Required command not found: $1"
}

instance_index() {
    local wanted="$1" i
    for i in "${!INSTANCE_NAMES[@]}"; do
        if [[ "${INSTANCE_NAMES[$i]}" == "${wanted}" ]]; then
            echo "${i}"
            return 0
        fi
    done
    die "Unknown instance '${wanted}'. Expected pi4-1, pi4-2, pi4-3, or pi4-4."
}

instance_dir() {
    echo "${INSTANCES_DIR}/${1}"
}

pid_is_running() {
    local pid_file="$1" pid
    [[ -s "${pid_file}" ]] || return 1
    read -r pid < "${pid_file}"
    [[ "${pid}" =~ ^[0-9]+$ ]] || return 1
    kill -0 "${pid}" 2>/dev/null || return 1
    [[ -r "/proc/${pid}/cmdline" ]] || return 1
    grep -azq 'qemu-system-aarch64' "/proc/${pid}/cmdline"
}

cleanup_stale_state() {
    local name="$1" dir pid_file
    dir="$(instance_dir "${name}")"
    pid_file="${dir}/qemu.pid"
    if [[ -e "${pid_file}" ]] && ! pid_is_running "${pid_file}"; then
        rm -f -- "${pid_file}" "${dir}/monitor.sock"
    fi
}

port_is_busy() {
    nc -z -w 1 127.0.0.1 "$1" >/dev/null 2>&1
}

ssh_is_ready() {
    local port="$1" banner fd
    exec {fd}<>"/dev/tcp/127.0.0.1/${port}" 2>/dev/null || return 1
    if ! IFS= read -r -t 2 banner <&"${fd}"; then
        exec {fd}>&-
        return 1
    fi
    exec {fd}>&-
    [[ "${banner}" == SSH-* ]]
}

check_tools() {
    local tool
    for tool in qemu-system-aarch64 qemu-img qemu-nbd nc socat make mountpoint udevadm sudo; do
        require_command "${tool}"
    done
    qemu-system-aarch64 -machine help | grep -q '^raspi4b ' || \
        die "This QEMU installation does not support the raspi4b machine."
}

check_free_space() {
    local available
    available="$(df -Pk "${ROOT_DIR}" | awk 'NR == 2 {print $4}')"
    [[ "${available}" =~ ^[0-9]+$ ]] || die "Could not determine available disk space."
    (( available >= MIN_PREP_KIB )) || \
        die "At least 24 GiB free is required before preparing four disks."
}

check_memory() {
    local available
    available="$(awk '/^MemAvailable:/ {print $2}' /proc/meminfo)"
    [[ "${available}" =~ ^[0-9]+$ ]] || die "Could not determine available memory."
    (( available >= MIN_RUN_KIB )) || \
        die "At least 9 GiB available memory is required to start all four instances."
}

check_nbd_ready() {
    [[ -b "${NBD_DEVICE}" ]] || \
        die "${NBD_DEVICE} is unavailable. Run: sudo modprobe nbd max_part=8"
    if [[ -r "/sys/class/block/nbd0/pid" ]] && [[ -s "/sys/class/block/nbd0/pid" ]]; then
        die "${NBD_DEVICE} is already connected to another image."
    fi
    if mountpoint -q "${ROOT_DIR}/mnt/root"; then
        die "mnt/root is already mounted."
    fi
    if mountpoint -q "${ROOT_DIR}/mnt/boot"; then
        die "mnt/boot is already mounted."
    fi
    return 0
}

source_image_path() {
    make -s --no-print-directory -C "${ROOT_DIR}" print-img-qcow2
}

customize_disk() (
    local disk="$1" hostname="$2" mounted=0 connected=0

    cleanup_disk_mount() {
        if (( mounted )); then
            sudo umount "${ROOT_DIR}/mnt/root" || true
        fi
        if (( connected )); then
            sudo qemu-nbd --disconnect "${NBD_DEVICE}" || true
        fi
    }
    trap cleanup_disk_mount EXIT

    sudo qemu-nbd -c "${NBD_DEVICE}" "${disk}"
    connected=1
    sudo udevadm settle
    sudo mount -t ext4 "${NBD_DEVICE}p2" "${ROOT_DIR}/mnt/root"
    mounted=1

    printf '%s\n' "${hostname}" | sudo tee "${ROOT_DIR}/mnt/root/etc/hostname" >/dev/null
    if sudo grep -qE '^127\.0\.1\.1[[:space:]]' "${ROOT_DIR}/mnt/root/etc/hosts"; then
        sudo sed -i -E "s/^127\\.0\\.1\\.1[[:space:]].*/127.0.1.1\t${hostname}/" \
            "${ROOT_DIR}/mnt/root/etc/hosts"
    else
        printf '127.0.1.1\t%s\n' "${hostname}" | \
            sudo tee -a "${ROOT_DIR}/mnt/root/etc/hosts" >/dev/null
    fi
    sudo truncate -s 0 "${ROOT_DIR}/mnt/root/etc/machine-id"
    sudo rm -f "${ROOT_DIR}/mnt/root/var/lib/dbus/machine-id"
    sudo rm -f "${ROOT_DIR}/mnt/root/var/lib/systemd/random-seed"
    sudo rm -f "${ROOT_DIR}"/mnt/root/etc/ssh/ssh_host_*
    sudo rm -f "${ROOT_DIR}/mnt/root/home/pi/.ssh/authorized_keys"
    sync --file-system "${ROOT_DIR}/mnt/root"

    sudo umount "${ROOT_DIR}/mnt/root"
    mounted=0
    sudo qemu-nbd --disconnect "${NBD_DEVICE}"
    connected=0
    trap - EXIT
)

update_base_password() (
    local disk="$1" boot_mounted=0 connected=0

    cleanup_base_mount() {
        if (( boot_mounted )); then
            sudo umount "${ROOT_DIR}/mnt/boot" || true
        fi
        if (( connected )); then
            sudo qemu-nbd --disconnect "${NBD_DEVICE}" || true
        fi
    }
    trap cleanup_base_mount EXIT

    sudo qemu-nbd -c "${NBD_DEVICE}" "${disk}"
    connected=1
    sudo udevadm settle
    sudo mount -t vfat "${NBD_DEVICE}p1" "${ROOT_DIR}/mnt/boot"
    boot_mounted=1
    printf '%s\n' "${USER_PASSWD}" | sudo tee "${ROOT_DIR}/mnt/boot/userconf" >/dev/null
    sync --file-system "${ROOT_DIR}/mnt/boot"

    sudo umount "${ROOT_DIR}/mnt/boot"
    boot_mounted=0
    sudo qemu-nbd --disconnect "${NBD_DEVICE}"
    connected=0
    trap - EXIT
)

update_disk_password() (
    local disk="$1" username="$2" password_hash="$3"
    local root_mounted=0 connected=0 shadow stored_hash

    cleanup_password_mount() {
        if (( root_mounted )); then
            sudo umount "${ROOT_DIR}/mnt/root" || true
        fi
        if (( connected )); then
            sudo qemu-nbd --disconnect "${NBD_DEVICE}" || true
        fi
    }
    trap cleanup_password_mount EXIT

    sudo qemu-nbd -c "${NBD_DEVICE}" "${disk}"
    connected=1
    sudo udevadm settle
    sudo mount -t ext4 "${NBD_DEVICE}p2" "${ROOT_DIR}/mnt/root"
    root_mounted=1
    shadow="${ROOT_DIR}/mnt/root/etc/shadow"
    sudo grep -q "^${username}:" "${shadow}" || {
        echo "ERROR: User ${username} was not found in ${disk}." >&2
        return 1
    }
    sudo sed -i -E "s|^${username}:[^:]*:|${username}:${password_hash}:|" "${shadow}"
    stored_hash="$(sudo awk -F: -v username="${username}" '$1 == username {print $2}' "${shadow}")"
    [[ "${stored_hash}" == "${password_hash}" ]] || {
        echo "ERROR: Password verification failed for ${disk}." >&2
        return 1
    }
    sync --file-system "${ROOT_DIR}/mnt/root"

    sudo umount "${ROOT_DIR}/mnt/root"
    root_mounted=0
    sudo qemu-nbd --disconnect "${NBD_DEVICE}"
    connected=0
    trap - EXIT
)

update_passwords() {
    local username password_hash source name dir disk pid_file

    check_tools
    check_nbd_ready
    [[ -n "${USER_PASSWD:-}" && "${USER_PASSWD}" == *:* ]] || \
        die "USER_PASSWD was not exported. Run this command through Make."
    username="${USER_PASSWD%%:*}"
    password_hash="${USER_PASSWD#*:}"
    [[ "${username}" == "pi" && "${password_hash}" == \$* ]] || \
        die "USER_PASSWD must contain a crypt-compatible hash for user pi."

    for name in "${INSTANCE_NAMES[@]}"; do
        dir="$(instance_dir "${name}")"
        disk="${dir}/disk.qcow2"
        pid_file="${dir}/qemu.pid"
        [[ -f "${disk}" ]] || die "Missing disk for ${name}."
        cleanup_stale_state "${name}"
        ! pid_is_running "${pid_file}" || \
            die "${name} is running; stop every instance before changing passwords."
    done

    source="$(source_image_path)"
    [[ -f "${source}" ]] || die "Prepared source image not found: ${source}"
    echo "Updating the retained base image..."
    update_base_password "${source}"
    for name in "${INSTANCE_NAMES[@]}"; do
        disk="$(instance_dir "${name}")/disk.qcow2"
        echo "Updating ${name}..."
        update_disk_password "${disk}" "${username}" "${password_hash}"
    done
    echo "Updated the pi password in the base image and all four instance disks."
}

prepare_instances() {
    local name dir disk tmp source i

    check_tools
    check_free_space
    check_nbd_ready

    for name in "${INSTANCE_NAMES[@]}"; do
        disk="$(instance_dir "${name}")/disk.qcow2"
        [[ ! -e "${disk}" ]] || \
            die "${disk} already exists; no disks were changed."
    done

    echo "Preparing the repository base image and Pi 4 boot files..."
    make -C "${ROOT_DIR}" prepare-multi-base
    source="$(source_image_path)"
    [[ -f "${source}" ]] || die "Prepared source image not found: ${source}"

    for i in "${!INSTANCE_NAMES[@]}"; do
        name="${INSTANCE_NAMES[$i]}"
        dir="$(instance_dir "${name}")"
        disk="${dir}/disk.qcow2"
        tmp="${disk}.tmp"
        mkdir -p "${dir}"
        rm -f -- "${tmp}"
        echo "Creating standalone disk for ${name}..."
        qemu-img convert -p -f qcow2 -O qcow2 "${source}" "${tmp}"
        customize_disk "${tmp}" "${name}"
        mv -- "${tmp}" "${disk}"
    done

    echo "Prepared four standalone instance disks under ${INSTANCES_DIR}."
}

preflight_instance() {
    local i="$1" name dir disk pid_file port
    name="${INSTANCE_NAMES[$i]}"
    port="${INSTANCE_PORTS[$i]}"
    dir="$(instance_dir "${name}")"
    disk="${dir}/disk.qcow2"
    pid_file="${dir}/qemu.pid"

    [[ -f "${disk}" ]] || die "Missing disk for ${name}. Run: make prep-instances"
    cleanup_stale_state "${name}"
    ! pid_is_running "${pid_file}" || die "${name} is already running."
    ! port_is_busy "${port}" || die "TCP port ${port} is already in use."
}

start_instance_by_index() {
    local i="$1" display_backend="${2:-gtk}" name port dir pid_file display_description
    name="${INSTANCE_NAMES[$i]}"
    port="${INSTANCE_PORTS[$i]}"
    dir="$(instance_dir "${name}")"
    pid_file="${dir}/qemu.pid"

    if [[ "${display_backend}" == "none" ]]; then
        display_description="headless"
    else
        display_description="window title: ${name}"
    fi
    echo "Starting ${name} (${display_description}, SSH: 127.0.0.1:${port})..."
    INSTANCE_NAME="${name}" \
    INSTANCE_UUID="${INSTANCE_UUIDS[$i]}" \
    INSTANCE_MAC="${INSTANCE_MACS[$i]}" \
    IMG_QCOW2="${dir}/disk.qcow2" \
    SSH_PORT="${port}" \
    SERIAL_LOG="${dir}/serial.log" \
    MONITOR_SOCKET="${dir}/monitor.sock" \
    PID_FILE="${pid_file}" \
    QEMU_LOG="${dir}/qemu.log" \
    DISPLAY_BACKEND="${display_backend}" \
    DAEMONIZE=1 \
        "${ROOT_DIR}/run-raspi4.sh"

    if ! pid_is_running "${pid_file}"; then
        echo "ERROR: ${name} did not remain running." >&2
        return 1
    fi
}

reset_instances() {
    local name dir pid_file
    [[ "${CONFIRM_RESET:-}" == "delete-instance-disks" ]] || \
        die "Refusing to delete disks. Re-run with CONFIRM_RESET=delete-instance-disks."
    for name in "${INSTANCE_NAMES[@]}"; do
        dir="$(instance_dir "${name}")"
        pid_file="${dir}/qemu.pid"
        cleanup_stale_state "${name}"
        ! pid_is_running "${pid_file}" || \
            die "${name} is running; stop all instances before resetting."
    done
    for name in "${INSTANCE_NAMES[@]}"; do
        dir="$(instance_dir "${name}")"
        rm -rf -- "${dir}"
    done
    echo "Removed all four instance disks and runtime state. The repository base image was retained."
}

run_one() {
    local i
    check_tools
    [[ -n "${DISPLAY:-}" || -n "${WAYLAND_DISPLAY:-}" ]] || \
        die "No graphical display is available for the QEMU window."
    i="$(instance_index "$1")"
    preflight_instance "${i}"
    start_instance_by_index "${i}" gtk
}

connect_one() {
    local i name port dir pid_file attempt
    check_tools
    require_command ssh
    i="$(instance_index "$1")"
    name="${INSTANCE_NAMES[$i]}"
    port="${INSTANCE_PORTS[$i]}"
    dir="$(instance_dir "${name}")"
    pid_file="${dir}/qemu.pid"
    cleanup_stale_state "${name}"

    if ! pid_is_running "${pid_file}"; then
        preflight_instance "${i}"
        start_instance_by_index "${i}" none
    else
        echo "${name} is already running; connecting to its existing session."
    fi

    echo "Waiting for SSH on 127.0.0.1:${port}..."
    for attempt in {1..90}; do
        if ssh_is_ready "${port}"; then
            exec ssh -F /dev/null \
                -o UserKnownHostsFile=/dev/null \
                -o StrictHostKeyChecking=no \
                -o LogLevel=ERROR \
                -o PubkeyAuthentication=no \
                -o PreferredAuthentications=password \
                -p "${port}" pi@127.0.0.1
        fi
        if ! pid_is_running "${pid_file}"; then
            die "${name} stopped before SSH became available. Check ${dir}/qemu.log and serial.log."
        fi
        sleep 2
    done
    die "Timed out after 180 seconds waiting for SSH on ${name}."
}

run_all() {
    local i started=()
    check_tools
    check_memory
    [[ -n "${DISPLAY:-}" || -n "${WAYLAND_DISPLAY:-}" ]] || \
        die "No graphical display is available for the four QEMU windows."

    for i in "${!INSTANCE_NAMES[@]}"; do
        preflight_instance "${i}"
    done

    for i in "${!INSTANCE_NAMES[@]}"; do
        if ! start_instance_by_index "${i}" gtk; then
            echo "Startup failed; terminating instances started by this command." >&2
            for i in "${started[@]}"; do
                force_stop_by_index "${i}" || true
            done
            return 1
        fi
        started+=("${i}")
    done
}

stop_by_index() {
    local i="$1" name dir pid_file monitor pid attempt
    name="${INSTANCE_NAMES[$i]}"
    dir="$(instance_dir "${name}")"
    pid_file="${dir}/qemu.pid"
    monitor="${dir}/monitor.sock"

    cleanup_stale_state "${name}"
    if ! pid_is_running "${pid_file}"; then
        echo "${name}: stopped"
        return 0
    fi
    if [[ ! -S "${monitor}" ]]; then
        echo "${name}: running but its monitor socket is missing" >&2
        return 1
    fi
    read -r pid < "${pid_file}"
    printf 'system_powerdown\n' | socat - "UNIX-CONNECT:${monitor}" >/dev/null
    echo "${name}: graceful shutdown requested"
    for attempt in {1..30}; do
        if ! kill -0 "${pid}" 2>/dev/null; then
            rm -f -- "${pid_file}" "${monitor}"
            echo "${name}: stopped"
            return 0
        fi
        sleep 1
    done
    echo "${name}: still running; shut it down inside the guest or use the explicit force-stop target." >&2
    return 1
}

force_stop_by_index() {
    local i="$1" name dir pid_file pid attempt
    name="${INSTANCE_NAMES[$i]}"
    dir="$(instance_dir "${name}")"
    pid_file="${dir}/qemu.pid"
    cleanup_stale_state "${name}"
    if ! pid_is_running "${pid_file}"; then
        echo "${name}: stopped"
        return 0
    fi
    read -r pid < "${pid_file}"
    kill -TERM "${pid}"
    for attempt in {1..10}; do
        kill -0 "${pid}" 2>/dev/null || break
        sleep 1
    done
    if kill -0 "${pid}" 2>/dev/null; then
        kill -KILL "${pid}"
    fi
    rm -f -- "${pid_file}" "${dir}/monitor.sock"
    echo "${name}: force-stopped"
}

status_all() {
    local i name dir disk pid_file port disk_status process_status
    for i in "${!INSTANCE_NAMES[@]}"; do
        name="${INSTANCE_NAMES[$i]}"
        port="${INSTANCE_PORTS[$i]}"
        dir="$(instance_dir "${name}")"
        disk="${dir}/disk.qcow2"
        pid_file="${dir}/qemu.pid"
        cleanup_stale_state "${name}"
        [[ -f "${disk}" ]] && disk_status="ready" || disk_status="missing"
        if pid_is_running "${pid_file}"; then
            process_status="running (PID $(<"${pid_file}"))"
        else
            process_status="stopped"
        fi
        printf '%-6s disk=%-7s process=%-20s ssh=127.0.0.1:%s\n' \
            "${name}" "${disk_status}" "${process_status}" "${port}"
    done
}

require_guest_ssh() {
    local port="$1" name="$2"
    ssh_is_ready "${port}" || \
        die "${name} SSH is not reachable on 127.0.0.1:${port}. Start the instance first."
}

sync_replay_config_by_index() {
    local i="$1" name port config
    name="${INSTANCE_NAMES[$i]}"
    port="${INSTANCE_PORTS[$i]}"
    config="${CONFIGURATION_DIR}/${name}.json"
    require_command scp
    [[ -f "${config}" ]] || die "Replay configuration not found: ${config}"
    require_guest_ssh "${port}" "${name}"
    echo "Copying ${config} to ${name} (password prompt expected)..."
    if ! scp -F /dev/null \
            -o UserKnownHostsFile=/dev/null \
            -o StrictHostKeyChecking=no \
            -o LogLevel=ERROR \
            -o PubkeyAuthentication=no \
            -o PreferredAuthentications=password \
            -P "${port}" \
            "${config}" \
            "pi@127.0.0.1:/home/pi/telemetry-replay/replay_config.json"; then
        echo "ERROR: Failed to synchronize replay configuration to ${name}." >&2
        return 1
    fi
    echo "${name}: replay configuration synchronized"
}

reset_replay_state_by_index() {
    local i="$1" name port
    name="${INSTANCE_NAMES[$i]}"
    port="${INSTANCE_PORTS[$i]}"
    require_command ssh
    require_guest_ssh "${port}" "${name}"
    echo "Resetting replay state on ${name} (password prompt expected)..."
    ssh -F /dev/null \
        -o UserKnownHostsFile=/dev/null \
        -o StrictHostKeyChecking=no \
        -o LogLevel=ERROR \
        -o PubkeyAuthentication=no \
        -o PreferredAuthentications=password \
        -p "${port}" \
        pi@127.0.0.1 \
        'cd /home/pi/telemetry-replay && python3 emit_environment_data.py --reset-state'
}

main() {
    local command="${1:-}" name="${2:-}" i result=0
    case "${command}" in
        prep) prepare_instances ;;
        reset) reset_instances ;;
        update-passwords) update_passwords ;;
        run-all) run_all ;;
        status) status_all ;;
        run)
            [[ -n "${name}" ]] || die "An instance name is required."
            run_one "${name}"
            ;;
        connect)
            [[ -n "${name}" ]] || die "An instance name is required."
            connect_one "${name}"
            ;;
        sync-replay-config)
            [[ -n "${name}" ]] || die "An instance name is required."
            i="$(instance_index "${name}")"
            sync_replay_config_by_index "${i}"
            ;;
        sync-replay-config-all)
            for i in "${!INSTANCE_NAMES[@]}"; do
                sync_replay_config_by_index "${i}" || result=1
            done
            return "${result}"
            ;;
        reset-replay-state)
            [[ -n "${name}" ]] || die "An instance name is required."
            i="$(instance_index "${name}")"
            reset_replay_state_by_index "${i}"
            ;;
        stop)
            [[ -n "${name}" ]] || die "An instance name is required."
            i="$(instance_index "${name}")"
            stop_by_index "${i}"
            ;;
        force-stop)
            [[ -n "${name}" ]] || die "An instance name is required."
            i="$(instance_index "${name}")"
            force_stop_by_index "${i}"
            ;;
        stop-all)
            for i in "${!INSTANCE_NAMES[@]}"; do
                stop_by_index "${i}" || result=1
            done
            return "${result}"
            ;;
        force-stop-all)
            for i in "${!INSTANCE_NAMES[@]}"; do
                force_stop_by_index "${i}" || result=1
            done
            return "${result}"
            ;;
        -h|--help|help) usage ;;
        *) usage >&2; return 2 ;;
    esac
}

main "$@"
