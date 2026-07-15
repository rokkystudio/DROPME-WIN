#include "WinFspDriveHost.h"

#include "AndroidFsHttpClient.h"
#include "Log.h"
#include "Utf.h"

#include <windows.h>

#include <algorithm>
#include <cerrno>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fcntl.h>
#include <io.h>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <system_error>
#include <thread>
#include <vector>

#if WIFIDROP_ENABLE_WINFSP
#define FUSE_USE_VERSION 30
#include <fuse3/fuse.h>
#include <sys/stat.h>
#endif

namespace {

#if WIFIDROP_ENABLE_WINFSP

constexpr std::size_t kTransferChunkSize = 256 * 1024;

struct OpenFileHandle {
    std::string remotePath;
    std::wstring tempPath;
    std::FILE *stream{nullptr};
    bool writable{false};
    bool dirty{false};
    std::mutex mutex;
};

struct MountContext {
    explicit MountContext(const AndroidClient &clientValue)
        : client(clientValue.webDavHost, clientValue.webDavPort) {
    }

    AndroidFsHttpClient client;
    std::string clientId;
    std::string mountPoint;
    std::string volumeName;
    std::wstring tempDirectory;
    struct fuse *fuseHandle{nullptr};
    std::thread loopThread;
};

std::mutex g_mountMutex;
std::map<std::string, std::unique_ptr<MountContext>> g_mounts;

std::optional<std::wstring> FindFreeDriveLetter() {
    const DWORD logicalDrives = GetLogicalDrives();
    for (wchar_t letter = L'Z'; letter >= L'D'; --letter) {
        const DWORD bit = 1u << (letter - L'A');
        if ((logicalDrives & bit) == 0) {
            return std::wstring(1, letter) + L":";
        }
    }
    return std::nullopt;
}

std::string TrimColon(const std::wstring &driveLetter) {
    return driveLetter.empty() ? std::string() : std::string(1, static_cast<char>(driveLetter.front()));
}

std::string ParentPath(const std::string &path) {
    if (path.empty() || path == "/") {
        return "";
    }
    const auto separator = path.find_last_of('/');
    if (separator == std::string::npos || separator == 0) {
        return "/";
    }
    return path.substr(0, separator);
}

void FillStatFromMetadata(const AndroidFsHttpClient::NodeMetadata &metadata, struct fuse_stat &stbuf) {
    memset(&stbuf, 0, sizeof(stbuf));
    const mode_t permissions = metadata.directory
        ? static_cast<mode_t>(metadata.writable ? 0755 : 0555)
        : static_cast<mode_t>(metadata.writable ? 0644 : 0444);
    stbuf.st_mode = metadata.directory ? (S_IFDIR | permissions) : (S_IFREG | permissions);
    stbuf.st_nlink = metadata.directory ? 2 : 1;
    stbuf.st_size = static_cast<fuse_off_t>(metadata.size);
    stbuf.st_blksize = 4096;
    stbuf.st_blocks = static_cast<fuse_blkcnt_t>((metadata.size + 511) / 512);
    stbuf.st_mtim.tv_sec = static_cast<decltype(stbuf.st_mtim.tv_sec)>(metadata.lastModified / 1000);
    stbuf.st_mtim.tv_nsec = static_cast<decltype(stbuf.st_mtim.tv_nsec)>((metadata.lastModified % 1000) * 1000000ULL);
    stbuf.st_ctim = stbuf.st_mtim;
    stbuf.st_atim = stbuf.st_mtim;
    stbuf.st_birthtim = stbuf.st_mtim;
}

MountContext *CurrentContext() {
    return static_cast<MountContext *>(fuse_get_context()->private_data);
}

OpenFileHandle *CurrentFileHandle(struct fuse_file_info *fi) {
    return fi != nullptr ? reinterpret_cast<OpenFileHandle *>(fi->fh) : nullptr;
}

std::wstring CreateTempDirectoryPath(const std::string &clientId) {
    wchar_t tempRoot[MAX_PATH]{};
    const DWORD length = GetTempPathW(MAX_PATH, tempRoot);
    if (length == 0 || length >= MAX_PATH) {
        return {};
    }
    std::wstring directory = tempRoot;
    directory += L"DROPME-";
    directory += Utf::Utf8ToWide(clientId);
    return directory;
}

std::optional<std::wstring> CreateTemporaryFilePath(const std::wstring &directory) {
    if (directory.empty()) {
        return std::nullopt;
    }
    wchar_t buffer[MAX_PATH]{};
    if (0 == GetTempFileNameW(directory.c_str(), L"WFD", 0, buffer)) {
        return std::nullopt;
    }
    return std::wstring(buffer);
}

bool DownloadRemoteFile(MountContext &context,
                        const std::string &path,
                        std::FILE *stream,
                        std::uint64_t expectedSize,
                        std::string *errorMessage) {
    if (expectedSize == 0) {
        return true;
    }

    std::vector<char> bytes;
    std::uint64_t offset = 0;
    while (offset < expectedSize) {
        const auto chunkSize = static_cast<std::uint32_t>(
            std::min<std::uint64_t>(kTransferChunkSize, expectedSize - offset));
        if (!context.client.ReadFileRange(path, offset, chunkSize, bytes, errorMessage)) {
            return false;
        }
        if (bytes.empty()) {
            if (errorMessage != nullptr) {
                *errorMessage = "Unexpected end of remote file while downloading";
            }
            return false;
        }
        if (bytes.size() != fwrite(bytes.data(), 1, bytes.size(), stream)) {
            if (errorMessage != nullptr) {
                *errorMessage = "Could not write downloaded bytes into temporary file";
            }
            return false;
        }
        offset += bytes.size();
    }
    fflush(stream);
    _fseeki64(stream, 0, SEEK_SET);
    return true;
}

std::unique_ptr<OpenFileHandle> CreateLocalHandle(const char *path,
                                                  const AndroidFsHttpClient::NodeMetadata &metadata,
                                                  bool writable,
                                                  bool truncateExisting,
                                                  std::string *errorMessage) {
    auto *context = CurrentContext();
    const auto tempPath = CreateTemporaryFilePath(context->tempDirectory);
    if (!tempPath.has_value()) {
        if (errorMessage != nullptr) {
            *errorMessage = "Could not create a temporary file for WinFsp writeback";
        }
        return nullptr;
    }

        std::FILE *stream = _wfopen(tempPath->c_str(), L"w+b");
    if (stream == nullptr) {
        if (errorMessage != nullptr) {
            *errorMessage = "Could not open the temporary file for WinFsp writeback";
        }
        DeleteFileW(tempPath->c_str());
        return nullptr;
    }

    auto handle = std::make_unique<OpenFileHandle>();
    handle->remotePath = path;
    handle->tempPath = *tempPath;
    handle->stream = stream;
    handle->writable = writable;

    if (metadata.exists && !truncateExisting) {
        if (!DownloadRemoteFile(*context, path, handle->stream, metadata.size, errorMessage)) {
            fclose(handle->stream);
            handle->stream = nullptr;
            DeleteFileW(handle->tempPath.c_str());
            return nullptr;
        }
    }
    return handle;
}

bool CommitLocalHandle(OpenFileHandle &handle, std::string *errorMessage) {
    if (!handle.writable || !handle.dirty) {
        return true;
    }
    if (fflush(handle.stream) != 0) {
        if (errorMessage != nullptr) {
            *errorMessage = "Could not flush the temporary file before upload";
        }
        return false;
    }
    return CurrentContext()->client.UploadFile(handle.remotePath, handle.tempPath, errorMessage)
        ? (handle.dirty = false, true)
        : false;
}

void DestroyLocalHandle(OpenFileHandle *handle) {
    if (handle == nullptr) {
        return;
    }
    if (handle->stream != nullptr) {
        fclose(handle->stream);
        handle->stream = nullptr;
    }
    if (!handle->tempPath.empty()) {
        DeleteFileW(handle->tempPath.c_str());
    }
    delete handle;
}

int ResizeLocalHandle(OpenFileHandle &handle, std::uint64_t size) {
    if (fflush(handle.stream) != 0) {
        return -EIO;
    }
    if (_chsize_s(_fileno(handle.stream), size) != 0) {
        return -EIO;
    }
    handle.dirty = true;
    return 0;
}

int WinFspGetAttr(const char *path, struct fuse_stat *stbuf, struct fuse_file_info *) {
    AndroidFsHttpClient::NodeMetadata metadata;
    std::string errorMessage;
    if (!CurrentContext()->client.GetMetadata(path, metadata, &errorMessage)) {
        Log::Warn("WinFsp getattr failed for " + std::string(path) + ": " + errorMessage);
        return -EIO;
    }
    if (!metadata.exists) {
        return -ENOENT;
    }
    FillStatFromMetadata(metadata, *stbuf);
    return 0;
}

int WinFspOpenDir(const char *path, struct fuse_file_info *) {
    AndroidFsHttpClient::NodeMetadata metadata;
    std::string errorMessage;
    if (!CurrentContext()->client.GetMetadata(path, metadata, &errorMessage)) {
        Log::Warn("WinFsp opendir failed for " + std::string(path) + ": " + errorMessage);
        return -EIO;
    }
    if (!metadata.exists) {
        return -ENOENT;
    }
    return metadata.directory ? 0 : -ENOTDIR;
}

int WinFspReadDir(const char *path,
                  void *buf,
                  fuse_fill_dir_t filler,
                  fuse_off_t,
                  struct fuse_file_info *,
                  enum fuse_readdir_flags) {
    std::vector<AndroidFsHttpClient::DirectoryEntry> entries;
    std::string errorMessage;
    if (!CurrentContext()->client.ListDirectory(path, entries, &errorMessage)) {
        Log::Warn("WinFsp readdir failed for " + std::string(path) + ": " + errorMessage);
        return -EIO;
    }

    struct fuse_stat dotStat{};
    dotStat.st_mode = S_IFDIR | 0555;
    dotStat.st_nlink = 2;
    if (0 != filler(buf, ".", &dotStat, 0, static_cast<fuse_fill_dir_flags>(0)) ||
        0 != filler(buf, "..", &dotStat, 0, static_cast<fuse_fill_dir_flags>(0))) {
        return -ENOMEM;
    }

    for (const auto &entry : entries) {
        AndroidFsHttpClient::NodeMetadata metadata;
        metadata.exists = true;
        metadata.directory = entry.directory;
        metadata.size = entry.size;
        metadata.lastModified = entry.lastModified;
        metadata.writable = entry.writable;
        metadata.name = entry.name;

        struct fuse_stat entryStat{};
        FillStatFromMetadata(metadata, entryStat);
        if (0 != filler(buf, entry.name.c_str(), &entryStat, 0, FUSE_FILL_DIR_PLUS)) {
            return -ENOMEM;
        }
    }
    return 0;
}

int WinFspOpen(const char *path, struct fuse_file_info *fi) {
    AndroidFsHttpClient::NodeMetadata metadata;
    std::string errorMessage;
    if (!CurrentContext()->client.GetMetadata(path, metadata, &errorMessage)) {
        Log::Warn("WinFsp open failed for " + std::string(path) + ": " + errorMessage);
        return -EIO;
    }
    if (!metadata.exists) {
        return -ENOENT;
    }
    if (metadata.directory) {
        return -EISDIR;
    }

    const int accessMode = fi->flags & O_ACCMODE;
    const bool writable = accessMode != O_RDONLY || (fi->flags & O_TRUNC) != 0;
    if (!writable) {
        return 0;
    }
    if (!metadata.writable) {
        return -EACCES;
    }

    auto handle = CreateLocalHandle(path, metadata, true, (fi->flags & O_TRUNC) != 0, &errorMessage);
    if (!handle) {
        Log::Warn("WinFsp open local cache failed for " + std::string(path) + ": " + errorMessage);
        return -EIO;
    }
    fi->fh = reinterpret_cast<std::uint64_t>(handle.release());
    return 0;
}

int WinFspCreate(const char *path, fuse_mode_t, struct fuse_file_info *fi) {
    const std::string parentPath = ParentPath(path);
    if (parentPath.empty()) {
        return -EACCES;
    }

    AndroidFsHttpClient::NodeMetadata metadata;
    std::string errorMessage;
    if (!CurrentContext()->client.GetMetadata(parentPath, metadata, &errorMessage)) {
        Log::Warn("WinFsp create parent metadata failed for " + parentPath + ": " + errorMessage);
        return -EIO;
    }
    if (!metadata.exists) {
        return -ENOENT;
    }
    if (!metadata.directory) {
        return -ENOTDIR;
    }
    if (!metadata.writable) {
        return -EACCES;
    }

    AndroidFsHttpClient::NodeMetadata existing;
    if (!CurrentContext()->client.GetMetadata(path, existing, &errorMessage)) {
        Log::Warn("WinFsp create existing metadata failed for " + std::string(path) + ": " + errorMessage);
        return -EIO;
    }
    if (existing.exists) {
        return -EEXIST;
    }

    AndroidFsHttpClient::NodeMetadata newMetadata;
    newMetadata.exists = false;
    auto handle = CreateLocalHandle(path, newMetadata, true, true, &errorMessage);
    if (!handle) {
        Log::Warn("WinFsp create local cache failed for " + std::string(path) + ": " + errorMessage);
        return -EIO;
    }
    handle->dirty = true;
    fi->fh = reinterpret_cast<std::uint64_t>(handle.release());
    return 0;
}

int WinFspRead(const char *path,
               char *buf,
               size_t size,
               fuse_off_t off,
               struct fuse_file_info *fi) {
    if (size == 0) {
        return 0;
    }

    if (auto *handle = CurrentFileHandle(fi); handle != nullptr) {
        std::lock_guard lock(handle->mutex);
        if (_fseeki64(handle->stream, off, SEEK_SET) != 0) {
            return -EIO;
        }
        const auto readCount = fread(buf, 1, size, handle->stream);
        if (readCount < size && ferror(handle->stream) != 0) {
            clearerr(handle->stream);
            return -EIO;
        }
        return static_cast<int>(readCount);
    }

    std::vector<char> bytes;
    std::string errorMessage;
    if (!CurrentContext()->client.ReadFileRange(path,
                                                static_cast<std::uint64_t>(off),
                                                static_cast<std::uint32_t>(size),
                                                bytes,
                                                &errorMessage)) {
        Log::Warn("WinFsp read failed for " + std::string(path) + ": " + errorMessage);
        return -EIO;
    }
    if (bytes.empty()) {
        return 0;
    }
    memcpy(buf, bytes.data(), bytes.size());
    return static_cast<int>(bytes.size());
}

int WinFspWrite(const char *,
                const char *buf,
                size_t size,
                fuse_off_t off,
                struct fuse_file_info *fi) {
    auto *handle = CurrentFileHandle(fi);
    if (handle == nullptr || !handle->writable) {
        return -EBADF;
    }

    std::lock_guard lock(handle->mutex);
    if (_fseeki64(handle->stream, off, SEEK_SET) != 0) {
        return -EIO;
    }
    const auto written = fwrite(buf, 1, size, handle->stream);
    if (written != size) {
        return -EIO;
    }
    handle->dirty = true;
    return static_cast<int>(written);
}

int WinFspFlush(const char *path, struct fuse_file_info *fi) {
    auto *handle = CurrentFileHandle(fi);
    if (handle == nullptr || !handle->writable) {
        return 0;
    }

    std::lock_guard lock(handle->mutex);
    std::string errorMessage;
    if (!CommitLocalHandle(*handle, &errorMessage)) {
        Log::Warn("WinFsp flush failed for " + std::string(path) + ": " + errorMessage);
        return -EIO;
    }
    return 0;
}

int WinFspFsync(const char *path, int, struct fuse_file_info *fi) {
    return WinFspFlush(path, fi);
}

int WinFspRelease(const char *path, struct fuse_file_info *fi) {
    auto *handle = CurrentFileHandle(fi);
    fi->fh = 0;
    if (handle == nullptr) {
        return 0;
    }

    int result = 0;
    {
        std::lock_guard lock(handle->mutex);
        if (handle->writable && handle->dirty) {
            std::string errorMessage;
            if (!CommitLocalHandle(*handle, &errorMessage)) {
                Log::Warn("WinFsp release upload failed for " + std::string(path) + ": " + errorMessage);
                result = -EIO;
            }
        }
    }
    DestroyLocalHandle(handle);
    return result;
}

int WinFspTruncate(const char *path, fuse_off_t size, struct fuse_file_info *fi) {
    if (size < 0) {
        return -EINVAL;
    }

    if (auto *handle = CurrentFileHandle(fi); handle != nullptr) {
        std::lock_guard lock(handle->mutex);
        return ResizeLocalHandle(*handle, static_cast<std::uint64_t>(size));
    }

    AndroidFsHttpClient::NodeMetadata metadata;
    std::string errorMessage;
    if (!CurrentContext()->client.GetMetadata(path, metadata, &errorMessage)) {
        Log::Warn("WinFsp truncate metadata failed for " + std::string(path) + ": " + errorMessage);
        return -EIO;
    }
    if (!metadata.exists) {
        return -ENOENT;
    }
    if (metadata.directory) {
        return -EISDIR;
    }
    if (!metadata.writable) {
        return -EACCES;
    }

    auto tempHandle = CreateLocalHandle(path, metadata, true, false, &errorMessage);
    if (!tempHandle) {
        Log::Warn("WinFsp truncate local cache failed for " + std::string(path) + ": " + errorMessage);
        return -EIO;
    }

    const int resizeResult = ResizeLocalHandle(*tempHandle, static_cast<std::uint64_t>(size));
    if (resizeResult != 0) {
        DestroyLocalHandle(tempHandle.release());
        return resizeResult;
    }

    if (!CommitLocalHandle(*tempHandle, &errorMessage)) {
        Log::Warn("WinFsp truncate upload failed for " + std::string(path) + ": " + errorMessage);
        DestroyLocalHandle(tempHandle.release());
        return -EIO;
    }

    DestroyLocalHandle(tempHandle.release());
    return 0;
}

int WinFspMkDir(const char *path, fuse_mode_t) {
    AndroidFsHttpClient::NodeMetadata existing;
    std::string errorMessage;
    if (!CurrentContext()->client.GetMetadata(path, existing, &errorMessage)) {
        Log::Warn("WinFsp mkdir existing metadata failed for " + std::string(path) + ": " + errorMessage);
        return -EIO;
    }
    if (existing.exists) {
        return -EEXIST;
    }

    const std::string parentPath = ParentPath(path);
    if (parentPath.empty()) {
        return -EACCES;
    }
    AndroidFsHttpClient::NodeMetadata parent;
    if (!CurrentContext()->client.GetMetadata(parentPath, parent, &errorMessage)) {
        Log::Warn("WinFsp mkdir parent metadata failed for " + parentPath + ": " + errorMessage);
        return -EIO;
    }
    if (!parent.exists) {
        return -ENOENT;
    }
    if (!parent.directory) {
        return -ENOTDIR;
    }
    if (!parent.writable) {
        return -EACCES;
    }

    if (!CurrentContext()->client.CreateRemoteDirectory(path, &errorMessage)) {
        Log::Warn("WinFsp mkdir failed for " + std::string(path) + ": " + errorMessage);
        return -EIO;
    }
    return 0;
}

int WinFspUnlink(const char *path) {
    AndroidFsHttpClient::NodeMetadata metadata;
    std::string errorMessage;
    if (!CurrentContext()->client.GetMetadata(path, metadata, &errorMessage)) {
        Log::Warn("WinFsp unlink metadata failed for " + std::string(path) + ": " + errorMessage);
        return -EIO;
    }
    if (!metadata.exists) {
        return -ENOENT;
    }
    if (metadata.directory) {
        return -EISDIR;
    }
    if (!metadata.writable) {
        return -EACCES;
    }

    if (!CurrentContext()->client.DeletePath(path, &errorMessage)) {
        Log::Warn("WinFsp unlink failed for " + std::string(path) + ": " + errorMessage);
        return -EIO;
    }
    return 0;
}

int WinFspRmDir(const char *path) {
    AndroidFsHttpClient::NodeMetadata metadata;
    std::string errorMessage;
    if (!CurrentContext()->client.GetMetadata(path, metadata, &errorMessage)) {
        Log::Warn("WinFsp rmdir metadata failed for " + std::string(path) + ": " + errorMessage);
        return -EIO;
    }
    if (!metadata.exists) {
        return -ENOENT;
    }
    if (!metadata.directory) {
        return -ENOTDIR;
    }
    if (!metadata.writable) {
        return -EACCES;
    }

    if (!CurrentContext()->client.DeletePath(path, &errorMessage)) {
        Log::Warn("WinFsp rmdir failed for " + std::string(path) + ": " + errorMessage);
        return -EIO;
    }
    return 0;
}

int WinFspRename(const char *oldPath, const char *newPath, unsigned int flags) {
    if (flags != 0) {
        return -EINVAL;
    }

    AndroidFsHttpClient::NodeMetadata metadata;
    std::string errorMessage;
    if (!CurrentContext()->client.GetMetadata(oldPath, metadata, &errorMessage)) {
        Log::Warn("WinFsp rename source metadata failed for " + std::string(oldPath) + ": " + errorMessage);
        return -EIO;
    }
    if (!metadata.exists) {
        return -ENOENT;
    }
    if (!metadata.writable) {
        return -EACCES;
    }

    if (!CurrentContext()->client.MovePath(oldPath, newPath, true, &errorMessage)) {
        Log::Warn("WinFsp rename failed from " + std::string(oldPath) + " to " + std::string(newPath) + ": " + errorMessage);
        return -EIO;
    }
    return 0;
}

int WinFspChmod(const char *, fuse_mode_t, struct fuse_file_info *) {
    return 0;
}

int WinFspChown(const char *, fuse_uid_t, fuse_gid_t, struct fuse_file_info *) {
    return 0;
}

int WinFspUtimens(const char *, const struct fuse_timespec[2], struct fuse_file_info *) {
    return 0;
}

int WinFspStatFs(const char *, struct fuse_statvfs *stbuf) {
    memset(stbuf, 0, sizeof(*stbuf));
    stbuf->f_bsize = 4096;
    stbuf->f_frsize = 4096;
    stbuf->f_blocks = 1024 * 1024;
    stbuf->f_bfree = 512 * 1024;
    stbuf->f_bavail = 512 * 1024;
    stbuf->f_files = 1024 * 1024;
    stbuf->f_ffree = 512 * 1024;
    stbuf->f_favail = 512 * 1024;
    stbuf->f_namemax = 255;
    return 0;
}

void *WinFspInit(struct fuse_conn_info *, struct fuse_config *config) {
    config->kernel_cache = 0;
    config->auto_cache = 0;
    config->attr_timeout = 0.0;
    config->entry_timeout = 0.0;
    config->negative_timeout = 0.0;
    return CurrentContext();
}

struct fuse_operations g_operations = {
    .getattr = WinFspGetAttr,
    .mkdir = WinFspMkDir,
    .unlink = WinFspUnlink,
    .rmdir = WinFspRmDir,
    .rename = WinFspRename,
    .chmod = WinFspChmod,
    .chown = WinFspChown,
    .truncate = WinFspTruncate,
    .open = WinFspOpen,
    .read = WinFspRead,
    .write = WinFspWrite,
    .statfs = WinFspStatFs,
    .flush = WinFspFlush,
    .release = WinFspRelease,
    .fsync = WinFspFsync,
    .opendir = WinFspOpenDir,
    .readdir = WinFspReadDir,
    .init = WinFspInit,
    .create = WinFspCreate,
    .utimens = WinFspUtimens,
};

#endif

}  // namespace

std::optional<std::string> WinFspDriveHost::Mount(const AndroidClient &client, std::string *errorMessage) {
#if !WIFIDROP_ENABLE_WINFSP
    if (errorMessage != nullptr) {
        *errorMessage = "WinFsp support is not enabled in this build";
    }
    return std::nullopt;
#else
    const auto mountPoint = FindFreeDriveLetter();
    if (!mountPoint.has_value()) {
        if (errorMessage != nullptr) {
            *errorMessage = "No free drive letters are available";
        }
        return std::nullopt;
    }

    auto context = std::make_unique<MountContext>(client);
    context->clientId = client.clientId;
    context->mountPoint = TrimColon(*mountPoint) + ":";
    context->volumeName = client.deviceNameUtf8.empty() ? "DROPME" : client.deviceNameUtf8;
    context->tempDirectory = CreateTempDirectoryPath(client.clientId);

    if (context->tempDirectory.empty()) {
        if (errorMessage != nullptr) {
            *errorMessage = "Could not resolve a temporary directory for WinFsp";
        }
        return std::nullopt;
    }
    if (!CreateDirectoryW(context->tempDirectory.c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS) {
        if (errorMessage != nullptr) {
            *errorMessage = "Could not create a temporary directory for WinFsp";
        }
        return std::nullopt;
    }

    std::vector<char *> argv;
    std::string arg0 = "dropmefs";
    std::string mountArg = context->mountPoint;
    std::string optionArg = "-ofsname=DROPME,volname=DROPME,uid=-1,gid=-1";
    argv.push_back(arg0.data());
    argv.push_back(mountArg.data());
    argv.push_back(optionArg.data());
    struct fuse_args args = FUSE_ARGS_INIT(static_cast<int>(argv.size()), argv.data());

    context->fuseHandle = fuse_new(&args, &g_operations, sizeof(g_operations), context.get());
    if (context->fuseHandle == nullptr) {
        if (errorMessage != nullptr) {
            *errorMessage = "fuse_new failed";
        }
        return std::nullopt;
    }

    if (0 != fuse_mount(context->fuseHandle, mountArg.c_str())) {
        if (errorMessage != nullptr) {
            *errorMessage = "fuse_mount failed for " + mountArg;
        }
        fuse_destroy(context->fuseHandle);
        context->fuseHandle = nullptr;
        return std::nullopt;
    }

    context->loopThread = std::thread([rawContext = context.get()] {
        const int result = fuse_loop(rawContext->fuseHandle);
        Log::Info("WinFsp fuse loop exited for client " + rawContext->clientId + " with code " + std::to_string(result));
    });

    const std::string driveLetter = TrimColon(*mountPoint);
    {
        std::lock_guard lock(g_mountMutex);
        g_mounts[client.clientId] = std::move(context);
    }
    Log::Info("WinFsp drive mounted: " + driveLetter + " for client " + client.clientId);
    return driveLetter;
#endif
}

void WinFspDriveHost::Unmount(const AndroidClient &client) {
#if WIFIDROP_ENABLE_WINFSP
    std::unique_ptr<MountContext> context;
    {
        std::lock_guard lock(g_mountMutex);
        const auto iterator = g_mounts.find(client.clientId);
        if (iterator == g_mounts.end()) {
            return;
        }
        context = std::move(iterator->second);
        g_mounts.erase(iterator);
    }

    if (context->fuseHandle != nullptr) {
        fuse_exit(context->fuseHandle);
        fuse_unmount(context->fuseHandle);
    }
    if (context->loopThread.joinable()) {
        context->loopThread.join();
    }
    if (context->fuseHandle != nullptr) {
        fuse_destroy(context->fuseHandle);
        context->fuseHandle = nullptr;
    }
    if (!context->tempDirectory.empty()) {
        RemoveDirectoryW(context->tempDirectory.c_str());
    }
    Log::Info("WinFsp drive unmounted for client " + client.clientId);
#else
    (void)client;
#endif
}
