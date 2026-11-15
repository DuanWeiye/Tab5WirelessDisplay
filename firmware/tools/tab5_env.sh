# 各个脚本共用：定下 Tab5 的串口（TAB5_PORT）和局域网地址（TAB5_IP）。由别的脚本 source，不单独运行。
#
# 两样都可以用环境变量给，也可以写在本目录的 local.env 里（不入库，各人各机一份；环境变量优先）：
#     TAB5_PORT=/dev/serial/by-id/usb-Espressif_USB_JTAG_serial_debug_unit_XX:XX:XX:XX:XX:XX-if00
#     TAB5_IP=192.168.1.50
#     PY=/path/to/python3          （可选：测试脚本用的 Python，要有 numpy 和 PIL；默认 python3）
#
# 串口用 by-id 路径（不随 ttyACM 编号漂移）。没给时在 /dev/serial/by-id 里找 Espressif 的 USB-JTAG 口：
# 正好一个就用它；一个都没有、或者不止一个（分不清哪个是 Tab5）就报错，绝不猜，免得烧到 / 写到别的设备上。
_tab5_env_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
_tab5_env_port=$TAB5_PORT; _tab5_env_ip=$TAB5_IP
# shellcheck disable=SC1091
[ -f "$_tab5_env_dir/local.env" ] && . "$_tab5_env_dir/local.env"
TAB5_PORT=${_tab5_env_port:-$TAB5_PORT}; TAB5_IP=${_tab5_env_ip:-$TAB5_IP}
unset _tab5_env_port _tab5_env_ip

tab5_need_port() {      # 定下 TAB5_PORT；定不下来就报错退出
    if [ -z "$TAB5_PORT" ]; then
        local found=()
        local p
        for p in /dev/serial/by-id/usb-Espressif_USB_JTAG_serial_debug_unit_*-if00; do
            [ -e "$p" ] && found+=("$p")
        done
        case ${#found[@]} in
            1) TAB5_PORT=${found[0]} ;;
            0) echo "找不到 Tab5 的串口（/dev/serial/by-id 里没有 Espressif 的 USB-JTAG 口）：检查 USB 连接，或者用 TAB5_PORT 指定" >&2; exit 1 ;;
            *) echo "有 ${#found[@]} 个 Espressif 的 USB-JTAG 口，分不清哪个是 Tab5：用 TAB5_PORT 指定（或者写进 $_tab5_env_dir/local.env）" >&2
               printf '    %s\n' "${found[@]}" >&2; exit 1 ;;
        esac
    fi
    [ -e "$TAB5_PORT" ] || { echo "找不到 Tab5 的串口 $TAB5_PORT：检查 USB 连接" >&2; exit 1; }
}

tab5_need_ip() {        # 定下 TAB5_IP；没给就报错退出
    [ -n "$TAB5_IP" ] || { echo "不知道 Tab5 的地址：用 TAB5_IP 指定（或者写进 $_tab5_env_dir/local.env）。地址在 Tab5 的设置页「信息」那一行" >&2; exit 1; }
}
