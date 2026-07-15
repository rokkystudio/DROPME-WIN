cmake_minimum_required(VERSION 3.25)
project(DROPME LANGUAGES CXX)

set(WIFIDROP_WINFSP_DEFAULT_ROOT "C:/Program Files (x86)/WinFsp")
set(WIFIDROP_WINFSP_ROOT "${WIFIDROP_WINFSP_DEFAULT_ROOT}" CACHE PATH "Path to WinFsp installation")
if (EXISTS "${WIFIDROP_WINFSP_ROOT}/inc/winfsp/winfsp.h")
    set(WIFIDROP_ENABLE_WINFSP_DEFAULT ON)
    set(WIFIDROP_ENABLE_WINFSP ON CACHE BOOL "Enable WinFsp integration" FORCE)
else ()
    set(WIFIDROP_ENABLE_WINFSP_DEFAULT OFF)
    set(WIFIDROP_ENABLE_WINFSP OFF CACHE BOOL "Enable WinFsp integration" FORCE)
endif ()
option(BUILD_SHARED_LIBS "Build shared libraries" OFF)

set(WIFIDROP_SOURCES
        src/main.cpp
        src/App.h
        src/App.cpp
        src/ConnectedDevicesWindow.h
        src/ConnectedDevicesWindow.cpp
        src/TrayIcon.h
        src/TrayIcon.cpp
        src/AutoStart.h
        src/AutoStart.cpp
        src/WiFiDrop.rc
        src/resource.h
        src/server/Protocol.h
        src/server/HttpTypes.h
        src/server/WifiDropServer.h
        src/server/WifiDropServer.cpp
        src/server/UploadController.h
        src/server/UploadController.cpp
        src/server/ControlServer.h
        src/server/ControlServer.cpp
        src/server/DiscoveryResponder.h
        src/server/DiscoveryResponder.cpp
        src/clients/AndroidClient.h
        src/clients/AndroidClient.cpp
        src/clients/ClientManager.h
        src/clients/ClientManager.cpp
        src/utils/DesktopFolders.h
        src/utils/DesktopFolders.cpp
        src/utils/FileName.h
        src/utils/FileName.cpp
        src/utils/Utf.h
        src/utils/Utf.cpp
        src/utils/Log.h
        src/utils/Log.cpp
        src/utils/WebClientService.h
        src/utils/WebClientService.cpp
        src/utils/AndroidFsHttpClient.h
        src/utils/AndroidFsHttpClient.cpp
        src/utils/WebDavDriveMapper.h
        src/utils/WebDavDriveMapper.cpp
        src/utils/WinFspDriveHost.h
        src/utils/WinFspDriveHost.cpp
        src/ConnectedDevicesWindow.cpp
        src/ConnectedDevicesWindow.h
)

add_executable(DROPME ${WIFIDROP_SOURCES})

target_compile_features(DROPME PRIVATE cxx_std_20)

target_include_directories(DROPME PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src)

target_compile_definitions(DROPME PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)

if (MINGW)
    target_compile_options(DROPME PRIVATE -finput-charset=UTF-8 -fexec-charset=UTF-8)
    target_link_options(DROPME PRIVATE -static -static-libgcc -static-libstdc++)
endif ()

if (MSVC)
    set_property(TARGET DROPME PROPERTY
            MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif ()

target_link_libraries(DROPME PRIVATE
        ws2_32
        shell32
        user32
        gdi32
        advapi32
        mpr
        shlwapi
        ole32
        winhttp
        uuid
)

set_target_properties(DROPME PROPERTIES
        WIN32_EXECUTABLE TRUE
)

if (WIFIDROP_ENABLE_WINFSP)
    find_path(WINFSP_INCLUDE_DIR winfsp/winfsp.h
            HINTS
            ${WIFIDROP_WINFSP_ROOT}/inc
            $ENV{WIFIDROP_WINFSP_ROOT}/inc
    )
    find_library(WINFSP_LIBRARY winfsp-x64
            HINTS
            ${WIFIDROP_WINFSP_ROOT}/lib
            $ENV{WIFIDROP_WINFSP_ROOT}/lib
    )
    find_file(WINFSP_DLL winfsp-x64.dll
            HINTS
            ${WIFIDROP_WINFSP_ROOT}/bin
            $ENV{WIFIDROP_WINFSP_ROOT}/bin
    )

    if (NOT WINFSP_INCLUDE_DIR OR NOT WINFSP_LIBRARY OR NOT WINFSP_DLL)
        message(FATAL_ERROR "WinFsp SDK/runtime was not found. Set WIFIDROP_WINFSP_ROOT to a full WinFsp installation.")
    endif ()

    target_include_directories(DROPME PRIVATE ${WINFSP_INCLUDE_DIR})
    target_link_libraries(DROPME PRIVATE ${WINFSP_LIBRARY})
    target_compile_definitions(DROPME PRIVATE WIFIDROP_ENABLE_WINFSP=1)
    add_custom_command(TARGET DROPME POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${WINFSP_DLL}"
            "$<TARGET_FILE_DIR:DROPME>/winfsp-x64.dll")
else ()
    target_compile_definitions(DROPME PRIVATE WIFIDROP_ENABLE_WINFSP=0)
endif ()
