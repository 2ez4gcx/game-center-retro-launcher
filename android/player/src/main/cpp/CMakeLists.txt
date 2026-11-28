cmake_minimum_required(VERSION 3.22)
project(gcplayer C)
add_library(gcplayer SHARED core_proxy.c)
target_link_libraries(gcplayer dl log GLESv3)
