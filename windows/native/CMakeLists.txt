# Builds the vendored llama.cpp (vendor/llama.cpp, same pinned revision as the
# Mac app) as DLLs for Windows x64, plus opentypist_native.dll, a small shim
# for the parts of llama.h that pass structs by value.
#
# ggml's backends are built as separate DLLs that ggml_backend_load_all()
# picks up at runtime: the best CPU variant for the machine (always there, the
# fallback), and Vulkan when the PC has a Vulkan driver (NVIDIA, AMD, Intel).
# CUDA is optional (-DOPENTYPIST_CUDA=ON, needs the CUDA toolkit).
cmake_minimum_required(VERSION 3.21)
project(opentypist_native C CXX)

set(LLAMA_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../vendor/llama.cpp")
if (NOT EXISTS "${LLAMA_DIR}/CMakeLists.txt")
    message(FATAL_ERROR "vendor/llama.cpp is missing: git submodule update --init vendor/llama.cpp")
endif()

option(OPENTYPIST_VULKAN "Build the ggml Vulkan backend" ON)
option(OPENTYPIST_CUDA "Build the ggml CUDA backend" OFF)

set(BUILD_SHARED_LIBS     ON  CACHE BOOL "" FORCE)
set(GGML_BACKEND_DL       ON  CACHE BOOL "" FORCE)
set(GGML_CPU_ALL_VARIANTS ON  CACHE BOOL "" FORCE)
set(GGML_NATIVE           OFF CACHE BOOL "" FORCE)
set(GGML_OPENMP           OFF CACHE BOOL "" FORCE)
set(GGML_VULKAN ${OPENTYPIST_VULKAN} CACHE BOOL "" FORCE)
set(GGML_CUDA   ${OPENTYPIST_CUDA}   CACHE BOOL "" FORCE)
set(LLAMA_BUILD_COMMON   OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_TESTS    OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_TOOLS    OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_SERVER   OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_APP      OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_UI       OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_HTML     OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_MTMD     OFF CACHE BOOL "" FORCE)
set(LLAMA_OPENSSL        OFF CACHE BOOL "" FORCE)

# Everything (llama.dll, ggml*.dll, the shim) lands in <build>/bin.
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

# A one-letter binary directory keeps the Vulkan shader generator's nested
# build under Windows' path length limit.
add_subdirectory("${LLAMA_DIR}" l)

add_library(opentypist_native SHARED opentypist_native.c)
target_link_libraries(opentypist_native PRIVATE llama ggml)
target_compile_definitions(opentypist_native PRIVATE OT_BUILD)
if (MSVC)
    target_compile_options(opentypist_native PRIVATE /O2 /fp:fast)
endif()
