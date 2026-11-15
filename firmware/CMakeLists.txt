cmake_minimum_required(VERSION 3.16)

include($ENV{IDF_PATH}/tools/cmake/project.cmake)

# components/ 为本地组件（Tab5 BSP 等，来自官方 UserDemo）
# dependencies/ 为源码依赖（lvgl v9.2.2，不入库；build.sh 在缺的时候自动从上游取）
set(EXTRA_COMPONENT_DIRS
    "components"
    "dependencies"
)

project(tab5_wdisp)
