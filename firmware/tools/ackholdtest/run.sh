#!/bin/bash
# 在电脑上编译并跑 TCP 纯 ACK 合并判定逻辑的测试（不用 ESP-IDF）
cd "$(dirname "$0")" || exit 1
src=../../components/esp_hosted/host/drivers/transport
out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT
gcc -std=c11 -Wall -Wextra -Werror -O1 -g -fsanitize=address,undefined -I"$src" \
    -o "$out/ack_hold_test" ack_hold_test.c "$src/tcp_ack_hold_core.c" && "$out/ack_hold_test"
