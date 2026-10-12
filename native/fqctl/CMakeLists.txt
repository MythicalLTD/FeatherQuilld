cmake_minimum_required(VERSION 3.16)
project(fqctl LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

option(FQCTL_STATIC "Statically link fqctl for portable bins/" ON)

add_executable(fqctl src/main.cpp)

target_compile_options(fqctl PRIVATE
  -Wall
  -Wextra
  -Wpedantic
  -O2
)

if(FQCTL_STATIC)
  target_link_options(fqctl PRIVATE -static)
endif()

install(TARGETS fqctl RUNTIME DESTINATION bin)