#!/bin/bash
# 在电脑上编译并跑 kbd_keymap.hpp 的测试（不用 ESP-IDF）
cd "$(dirname "$0")" || exit 1
out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT
g++ -std=c++17 -Wall -Wextra -O1 -o "$out/kbd_keymap_test" kbd_keymap_test.cpp && "$out/kbd_keymap_test"
