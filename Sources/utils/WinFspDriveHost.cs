using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using DROPME.Clients;
using Fsp;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace DROPME.Utils
{
    /// <summary>
    /// Монтирует файловый API Android-устройства как локальный диск Windows через WinFsp.
    /// WinFsp используется как основной backend, а WebClient применяется вызывающим кодом только как fallback.
    /// </summary>
    internal static class WinFspDriveHost
    {
        private sealed class MountedDrive
        {
            public MountedDrive(AndroidRemoteFileSystem fileSystem, FileSystemHost host, string driveLetter)
            {
                FileSystem = fileSystem;
                Host = host;
                DriveLetter = driveLetter;
            }

            public AndroidRemoteFileSystem FileSystem { get; }
            public FileSystemHost Host { get; }
            public string DriveLetter { get; }
        }

        private sealed class AndroidRemoteFileSystem : FileSystemBase, IDisposable
        {
            private sealed class RemoteHandle
            {
                public string RemotePath { get; set; } = "/";
                public AndroidFsHttpClient.NodeMetadata Metadata { get; set; } = new AndroidFsHttpClient.NodeMetadata();
                public FileStream? Stream { get; set; }
                public string TempPath { get; set; } = string.Empty;
                public string JournalPath { get; set; } = string.Empty;
                public object SyncRoot { get; } = new object();
                public ulong LogicalSize { get; set; }
                public ulong AllocationSize { get; set; }
                public bool Dirty { get; set; }
                public bool DeletePending { get; set; }
                public bool Deleted { get; set; }
                public List<AndroidFsHttpClient.DirectoryEntry>? DirectoryEntries { get; set; }
            }

            private sealed class WritebackJournal
            {
                public string RemotePath { get; set; } = string.Empty;
                public string ExpectedEtag { get; set; } = string.Empty;
                public ulong LogicalSize { get; set; }
                public bool Stable { get; set; }
            }

            private const ushort AllocationUnit = 4096;
            private const ulong LogicalVolumeSize = 1UL << 50;
            private const uint TransferChunkSize = 1024 * 1024;

            private readonly AndroidFsHttpClient client_;
            private readonly bool readOnly_;
            private readonly string tempDirectory_;
            private readonly byte[] defaultSecurity_;
            private string volumeLabel_;

            public AndroidRemoteFileSystem(AndroidClient client)
            {
                client_ = new AndroidFsHttpClient(client.WebDavHost, client.WebDavPort);
                readOnly_ = client.ReadOnly;
                volumeLabel_ = string.IsNullOrWhiteSpace(client.DriveName) ? "DROPME" : client.DriveName;
                tempDirectory_ = Path.Combine(Path.GetTempPath(), "DROPME", client.ClientId);
                Directory.CreateDirectory(tempDirectory_);
                RecoverPendingWritebacks();

                RawSecurityDescriptor securityDescriptor =
                    new RawSecurityDescriptor("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)");
                defaultSecurity_ = new byte[securityDescriptor.BinaryLength];
                securityDescriptor.GetBinaryForm(defaultSecurity_, 0);
            }

            /// <summary>
            /// Настраивает WinFsp volume для Unicode-имён, сохранения регистра и короткого metadata cache.
            /// </summary>
            public override int Init(object hostObject)
            {
                FileSystemHost host = (FileSystemHost)hostObject;
                host.SectorSize = AllocationUnit;
                host.SectorsPerAllocationUnit = 1;
                host.MaxComponentLength = 255;
                host.FileInfoTimeout = 500;
                host.CaseSensitiveSearch = false;
                host.CasePreservedNames = true;
                host.UnicodeOnDisk = true;
                host.PersistentAcls = false;
                host.PostCleanupWhenModifiedOnly = true;
                host.PassQueryDirectoryPattern = true;
                host.FlushAndPurgeOnCleanup = true;
                host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
                host.VolumeSerialNumber = unchecked((uint)host.VolumeCreationTime);
                return STATUS_SUCCESS;
            }

            /// <summary>
            /// Возвращает логическую ёмкость виртуального агрегированного диска.
            /// Фактические ограничения записи определяются хранилищем Android.
            /// </summary>
            public override int GetVolumeInfo(out VolumeInfo volumeInfo)
            {
                volumeInfo = default;
                volumeInfo.TotalSize = LogicalVolumeSize;
                volumeInfo.FreeSize = LogicalVolumeSize;
                volumeInfo.SetVolumeLabel(volumeLabel_);
                return STATUS_SUCCESS;
            }

            /// <summary>
            /// Задаёт отображаемую метку виртуального диска без изменения Android-хранилища.
            /// </summary>
            public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
            {
                volumeLabel_ = string.IsNullOrWhiteSpace(volumeLabel) ? "DROPME" : volumeLabel;
                return GetVolumeInfo(out volumeInfo);
            }

            /// <summary>
            /// Возвращает атрибуты удалённого узла и общий security descriptor виртуального тома.
            /// </summary>
            public override int GetSecurityByName(
                string fileName,
                out uint fileAttributes,
                ref byte[] securityDescriptor)
            {
                fileAttributes = 0;
                if (!TryGetMetadata(ToRemotePath(fileName), out AndroidFsHttpClient.NodeMetadata metadata))
                {
                    return STATUS_UNEXPECTED_IO_ERROR;
                }
                if (!metadata.Exists)
                {
                    return STATUS_OBJECT_NAME_NOT_FOUND;
                }

                fileAttributes = BuildFileAttributes(metadata);
                if (securityDescriptor != null)
                {
                    securityDescriptor = defaultSecurity_;
                }
                return STATUS_SUCCESS;
            }

            /// <summary>
            /// Создаёт файл или каталог на Android и возвращает WinFsp handle нового узла.
            /// </summary>
            public override int Create(
                string fileName,
                uint createOptions,
                uint grantedAccess,
                uint fileAttributes,
                byte[] securityDescriptor,
                ulong allocationSize,
                out object fileNode,
                out object fileDesc,
                out FileInfo fileInfo,
                out string normalizedName)
            {
                fileNode = null!;
                fileDesc = null!;
                fileInfo = default;
                normalizedName = fileName;

                string remotePath = ToRemotePath(fileName);
                if (readOnly_)
                {
                    return STATUS_ACCESS_DENIED;
                }

                if (!TryGetMetadata(remotePath, out AndroidFsHttpClient.NodeMetadata existing))
                {
                    return STATUS_UNEXPECTED_IO_ERROR;
                }
                if (existing.Exists)
                {
                    return STATUS_OBJECT_NAME_COLLISION;
                }

                string parentPath = GetParentPath(remotePath);
                if (!TryGetMetadata(parentPath, out AndroidFsHttpClient.NodeMetadata parent))
                {
                    return STATUS_UNEXPECTED_IO_ERROR;
                }
                if (!parent.Exists)
                {
                    return STATUS_OBJECT_PATH_NOT_FOUND;
                }
                if (!parent.Directory)
                {
                    return STATUS_NOT_A_DIRECTORY;
                }
                if (!parent.Writable)
                {
                    return STATUS_ACCESS_DENIED;
                }

                bool directory = 0 != (createOptions & FILE_DIRECTORY_FILE);
                if (directory)
                {
                    if (!client_.CreateRemoteDirectory(remotePath, out string errorMessage))
                    {
                        Log.Warn("WinFsp create directory failed for " + remotePath + ": " + errorMessage);
                        return STATUS_UNEXPECTED_IO_ERROR;
                    }
                }
                else
                {
                    string tempPath = CreateTempPath();
                    try
                    {
                        using (FileStream stream = new FileStream(
                            tempPath,
                            FileMode.CreateNew,
                            FileAccess.ReadWrite,
                            FileShare.Read))
                        {
                            stream.SetLength(0);
                        }
                        if (!client_.UploadFile(
                            remotePath,
                            tempPath,
                            string.Empty,
                            true,
                            out string errorMessage))
                        {
                            Log.Warn("WinFsp create file failed for " + remotePath + ": " + errorMessage);
                            return STATUS_UNEXPECTED_IO_ERROR;
                        }
                    }
                    finally
                    {
                        TryDeleteTempFile(tempPath);
                    }
                }

                if (!TryGetMetadata(remotePath, out AndroidFsHttpClient.NodeMetadata metadata) ||
                    !metadata.Exists || metadata.Directory != directory)
                {
                    return STATUS_UNEXPECTED_IO_ERROR;
                }

                RemoteHandle handle = new RemoteHandle
                {
                    RemotePath = remotePath,
                    Metadata = metadata,
                    LogicalSize = metadata.Size,
                    AllocationSize = directory ? 0 : allocationSize
                };
                fileNode = handle;
                fileDesc = handle;
                fileInfo = BuildFileInfo(metadata);
                return STATUS_SUCCESS;
            }

            /// <summary>
            /// Открывает существующий удалённый узел и создаёт WinFsp handle.
            /// </summary>
            public override int Open(
                string fileName,
                uint createOptions,
                uint grantedAccess,
                out object fileNode,
                out object fileDesc,
                out FileInfo fileInfo,
                out string normalizedName)
            {
                fileNode = null!;
                fileDesc = null!;
                fileInfo = default;
                normalizedName = fileName;

                string remotePath = ToRemotePath(fileName);
                if (!TryGetMetadata(remotePath, out AndroidFsHttpClient.NodeMetadata metadata))
                {
                    return STATUS_UNEXPECTED_IO_ERROR;
                }
                if (!metadata.Exists)
                {
                    return STATUS_OBJECT_NAME_NOT_FOUND;
                }

                RemoteHandle handle = new RemoteHandle
                {
                    RemotePath = remotePath,
                    Metadata = metadata,
                    LogicalSize = metadata.Size,
                    AllocationSize = metadata.Directory ? 0 : AlignAllocation(metadata.Size)
                };
                fileNode = handle;
                fileDesc = handle;
                fileInfo = BuildFileInfo(metadata);
                return STATUS_SUCCESS;
            }

            /// <summary>
            /// Подготавливает существующий файл к перезаписи с нулевой длины.
            /// </summary>
            public override int Overwrite(
                object fileNode,
                object fileDesc,
                uint fileAttributes,
                bool replaceFileAttributes,
                ulong allocationSize,
                out FileInfo fileInfo)
            {
                RemoteHandle handle = (RemoteHandle)fileDesc;
                fileInfo = default;
                if (!CanWrite(handle))
                {
                    return STATUS_ACCESS_DENIED;
                }
                if (handle.Metadata.Directory)
                {
                    return STATUS_FILE_IS_A_DIRECTORY;
                }
                lock (handle.SyncRoot)
                {
                    if (!EnsureLocalFile(handle, true, out string errorMessage))
                    {
                        Log.Warn("WinFsp overwrite cache failed for " + handle.RemotePath + ": " + errorMessage);
                        return STATUS_UNEXPECTED_IO_ERROR;
                    }

                    if (!WriteWritebackJournal(handle, false, out string journalError))
                    {
                        Log.Warn("WinFsp overwrite journal failed for " + handle.RemotePath + ": " + journalError);
                        return STATUS_UNEXPECTED_IO_ERROR;
                    }

                    handle.Stream!.SetLength(0);
                    handle.LogicalSize = 0;
                    handle.AllocationSize = allocationSize;
                    handle.Dirty = true;
                    fileInfo = BuildFileInfo(handle, handle.LogicalSize);
                    return STATUS_SUCCESS;
                }
            }

            /// <summary>
            /// Выполняет отложенное удаление, запрошенное Windows при cleanup handle.
            /// </summary>
            public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
            {
                RemoteHandle handle = (RemoteHandle)fileDesc;
                if (0 == (flags & CleanupDelete) || handle.Deleted)
                {
                    return;
                }

                lock (handle.SyncRoot)
                {
                    handle.DeletePending = true;
                    if (!client_.DeletePath(handle.RemotePath, out string errorMessage))
                    {
                        Log.Warn("WinFsp delete failed for " + handle.RemotePath + ": " + errorMessage);
                        return;
                    }
                    handle.Deleted = true;
                    handle.Dirty = false;
                }
            }

            /// <summary>
            /// Завершает handle, отправляет оставшиеся локальные изменения и очищает временный файл.
            /// </summary>
            public override void Close(object fileNode, object fileDesc)
            {
                RemoteHandle handle = (RemoteHandle)fileDesc;
                bool deleteTempFile = true;
                try
                {
                    lock (handle.SyncRoot)
                    {
                        if (handle.DeletePending && !handle.Deleted)
                        {
                            if (client_.DeletePath(handle.RemotePath, out string deleteError))
                            {
                                handle.Deleted = true;
                                handle.Dirty = false;
                            }
                            else
                            {
                                Log.Warn(
                                    "WinFsp close delete retry failed for " + handle.RemotePath + ": " + deleteError);
                            }
                            return;
                        }

                        if (handle.Dirty && !handle.Deleted)
                        {
                            if (!Commit(handle, out string errorMessage))
                            {
                                deleteTempFile = false;
                                Log.Warn(
                                    "WinFsp close upload failed for " + handle.RemotePath + ": " + errorMessage +
                                    ". Preserved writeback cache: " + handle.TempPath);
                            }
                        }
                    }
                }
                finally
                {
                    DisposeLocalFile(handle, deleteTempFile);
                }
            }

            /// <summary>
            /// Читает диапазон файла напрямую с Android либо из локального writeback cache.
            /// </summary>
            public override int Read(
                object fileNode,
                object fileDesc,
                IntPtr buffer,
                ulong offset,
                uint length,
                out uint bytesTransferred)
            {
                RemoteHandle handle = (RemoteHandle)fileDesc;
                bytesTransferred = 0;
                if (handle.Metadata.Directory)
                {
                    return STATUS_FILE_IS_A_DIRECTORY;
                }

                if (handle.Stream != null)
                {
                    lock (handle.SyncRoot)
                    {
                        if (offset >= handle.LogicalSize)
                        {
                            return STATUS_END_OF_FILE;
                        }

                        int count = (int)Math.Min((ulong)length, handle.LogicalSize - offset);
                        byte[] bytes = new byte[count];
                        handle.Stream.Position = (long)offset;
                        int totalRead = 0;
                        while (totalRead < count)
                        {
                            int read = handle.Stream.Read(bytes, totalRead, count - totalRead);
                            if (read <= 0)
                            {
                                break;
                            }
                            totalRead += read;
                        }

                        if (totalRead != count)
                        {
                            Log.Warn(
                                "WinFsp local cache read length mismatch for " + handle.RemotePath +
                                ": expected=" + count + " actual=" + totalRead);
                            return STATUS_UNEXPECTED_IO_ERROR;
                        }

                        Marshal.Copy(bytes, 0, buffer, totalRead);
                        bytesTransferred = (uint)totalRead;
                        return STATUS_SUCCESS;
                    }
                }

                if (offset >= handle.Metadata.Size)
                {
                    return STATUS_END_OF_FILE;
                }
                uint requested = (uint)Math.Min((ulong)length, handle.Metadata.Size - offset);
                if (!client_.ReadFileRange(
                    handle.RemotePath,
                    offset,
                    requested,
                    handle.Metadata.Etag,
                    out byte[] remoteBytes,
                    out string errorMessage))
                {
                    Log.Warn("WinFsp read failed for " + handle.RemotePath + ": " + errorMessage);
                    return STATUS_UNEXPECTED_IO_ERROR;
                }

                int copyLength = Math.Min(remoteBytes.Length, (int)requested);
                if (copyLength > 0)
                {
                    Marshal.Copy(remoteBytes, 0, buffer, copyLength);
                }
                bytesTransferred = (uint)copyLength;
                return STATUS_SUCCESS;
            }

            /// <summary>
            /// Записывает данные в локальный writeback cache; синхронизация с Android выполняется при flush/close.
            /// </summary>
            public override int Write(
                object fileNode,
                object fileDesc,
                IntPtr buffer,
                ulong offset,
                uint length,
                bool writeToEndOfFile,
                bool constrainedIo,
                out uint bytesTransferred,
                out FileInfo fileInfo)
            {
                RemoteHandle handle = (RemoteHandle)fileDesc;
                bytesTransferred = 0;
                fileInfo = default;
                if (!CanWrite(handle))
                {
                    return STATUS_ACCESS_DENIED;
                }
                if (handle.Metadata.Directory)
                {
                    return STATUS_FILE_IS_A_DIRECTORY;
                }
                lock (handle.SyncRoot)
                {
                    if (!EnsureLocalFile(handle, false, out string errorMessage))
                    {
                        Log.Warn("WinFsp write cache failed for " + handle.RemotePath + ": " + errorMessage);
                        return STATUS_UNEXPECTED_IO_ERROR;
                    }

                    FileStream stream = handle.Stream!;
                    ulong writeOffset = writeToEndOfFile ? handle.LogicalSize : offset;
                    uint writeLength = length;
                    if (constrainedIo)
                    {
                        ulong writeLimit = Math.Max(handle.LogicalSize, handle.AllocationSize);
                        if (writeLimit > 0)
                        {
                            if (writeOffset >= writeLimit)
                            {
                                fileInfo = BuildFileInfo(handle, handle.LogicalSize);
                                return STATUS_SUCCESS;
                            }
                            writeLength = (uint)Math.Min((ulong)writeLength, writeLimit - writeOffset);
                        }
                    }

                    byte[] bytes = new byte[writeLength];
                    if (writeLength > 0)
                    {
                        if (!WriteWritebackJournal(handle, false, out string journalError))
                        {
                            Log.Warn("WinFsp write journal failed for " + handle.RemotePath + ": " + journalError);
                            return STATUS_UNEXPECTED_IO_ERROR;
                        }

                        Marshal.Copy(buffer, bytes, 0, (int)writeLength);
                        stream.Position = (long)writeOffset;
                        stream.Write(bytes, 0, bytes.Length);
                        ulong writtenEnd = writeOffset + writeLength;
                        if (writtenEnd > handle.LogicalSize)
                        {
                            handle.LogicalSize = writtenEnd;
                        }
                        if (handle.LogicalSize > handle.AllocationSize)
                        {
                            handle.AllocationSize = AlignAllocation(handle.LogicalSize);
                        }
                        handle.Dirty = true;
                    }

                    bytesTransferred = writeLength;
                    fileInfo = BuildFileInfo(handle, handle.LogicalSize);
                    return STATUS_SUCCESS;
                }
            }

            /// <summary>
            /// Синхронизирует изменённый локальный writeback cache с Android.
            /// </summary>
            public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
            {
                fileInfo = default;
                if (fileDesc == null)
                {
                    return STATUS_SUCCESS;
                }

                RemoteHandle handle = (RemoteHandle)fileDesc;
                if (handle.Dirty && !Commit(handle, out string errorMessage))
                {
                    Log.Warn("WinFsp flush failed for " + handle.RemotePath + ": " + errorMessage);
                    return STATUS_UNEXPECTED_IO_ERROR;
                }

                fileInfo = handle.Stream != null
                    ? BuildFileInfo(handle, handle.LogicalSize)
                    : BuildFileInfo(handle.Metadata);
                return STATUS_SUCCESS;
            }

            /// <summary>
            /// Возвращает актуальные метаданные открытого удалённого узла.
            /// </summary>
            public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
            {
                RemoteHandle handle = (RemoteHandle)fileDesc;
                if (handle.Stream != null && handle.Dirty)
                {
                    fileInfo = BuildFileInfo(handle, handle.LogicalSize);
                    return STATUS_SUCCESS;
                }

                if (!TryGetMetadata(handle.RemotePath, out AndroidFsHttpClient.NodeMetadata metadata) || !metadata.Exists)
                {
                    fileInfo = default;
                    return STATUS_OBJECT_NAME_NOT_FOUND;
                }
                handle.Metadata = metadata;
                fileInfo = BuildFileInfo(metadata);
                return STATUS_SUCCESS;
            }

            /// <summary>
            /// Возвращает текущие метаданные; Android API не хранит Windows ACL и отдельные timestamps.
            /// </summary>
            public override int SetBasicInfo(
                object fileNode,
                object fileDesc,
                uint fileAttributes,
                ulong creationTime,
                ulong lastAccessTime,
                ulong lastWriteTime,
                ulong changeTime,
                out FileInfo fileInfo)
            {
                return GetFileInfo(fileNode, fileDesc, out fileInfo);
            }

            /// <summary>
            /// Изменяет размер файла в локальном writeback cache.
            /// </summary>
            public override int SetFileSize(
                object fileNode,
                object fileDesc,
                ulong newSize,
                bool setAllocationSize,
                out FileInfo fileInfo)
            {
                RemoteHandle handle = (RemoteHandle)fileDesc;
                fileInfo = default;
                if (!CanWrite(handle))
                {
                    return STATUS_ACCESS_DENIED;
                }
                if (handle.Metadata.Directory)
                {
                    return STATUS_FILE_IS_A_DIRECTORY;
                }
                lock (handle.SyncRoot)
                {
                    if (!EnsureLocalFile(handle, false, out string errorMessage))
                    {
                        Log.Warn("WinFsp resize cache failed for " + handle.RemotePath + ": " + errorMessage);
                        return STATUS_UNEXPECTED_IO_ERROR;
                    }

                    FileStream stream = handle.Stream!;
                    if (setAllocationSize)
                    {
                        if (newSize < handle.LogicalSize)
                        {
                            if (!WriteWritebackJournal(handle, false, out string journalError))
                            {
                                Log.Warn("WinFsp resize journal failed for " + handle.RemotePath + ": " + journalError);
                                return STATUS_UNEXPECTED_IO_ERROR;
                            }
                            handle.LogicalSize = newSize;
                            stream.SetLength((long)Math.Min(newSize, (ulong)long.MaxValue));
                            handle.Dirty = true;
                        }
                        handle.AllocationSize = newSize;
                    }
                    else
                    {
                        if (!WriteWritebackJournal(handle, false, out string journalError))
                        {
                            Log.Warn("WinFsp resize journal failed for " + handle.RemotePath + ": " + journalError);
                            return STATUS_UNEXPECTED_IO_ERROR;
                        }
                        handle.LogicalSize = newSize;
                        if (newSize > handle.AllocationSize)
                        {
                            handle.AllocationSize = AlignAllocation(newSize);
                        }
                        stream.SetLength((long)Math.Min(newSize, (ulong)long.MaxValue));
                        handle.Dirty = true;
                    }
                    fileInfo = BuildFileInfo(handle, handle.LogicalSize);
                    return STATUS_SUCCESS;
                }
            }

            /// <summary>
            /// Проверяет возможность удаления узла и непустого каталога.
            /// </summary>
            public override int CanDelete(object fileNode, object fileDesc, string fileName)
            {
                RemoteHandle handle = (RemoteHandle)fileDesc;
                if (!CanWrite(handle))
                {
                    return STATUS_ACCESS_DENIED;
                }
                if (!handle.Metadata.Directory)
                {
                    return STATUS_SUCCESS;
                }

                if (!client_.ListDirectory(handle.RemotePath, out List<AndroidFsHttpClient.DirectoryEntry> entries, out string errorMessage))
                {
                    Log.Warn("WinFsp delete directory check failed for " + handle.RemotePath + ": " + errorMessage);
                    return STATUS_UNEXPECTED_IO_ERROR;
                }
                return entries.Count == 0 ? STATUS_SUCCESS : STATUS_DIRECTORY_NOT_EMPTY;
            }

            /// <summary>
            /// Переименовывает или перемещает удалённый путь через Android MOVE endpoint.
            /// </summary>
            public override int Rename(
                object fileNode,
                object fileDesc,
                string fileName,
                string newFileName,
                bool replaceIfExists)
            {
                RemoteHandle handle = (RemoteHandle)fileDesc;
                if (!CanWrite(handle))
                {
                    return STATUS_ACCESS_DENIED;
                }

                lock (handle.SyncRoot)
                {
                    if (handle.Dirty && !Commit(handle, out string commitError))
                    {
                        Log.Warn(
                            "WinFsp rename commit failed for " + handle.RemotePath + ": " + commitError);
                        return STATUS_UNEXPECTED_IO_ERROR;
                    }

                    string destination = ToRemotePath(newFileName);
                    if (!client_.MovePath(handle.RemotePath, destination, replaceIfExists, out string errorMessage))
                    {
                        Log.Warn("WinFsp rename failed from " + handle.RemotePath + " to " + destination + ": " + errorMessage);
                        return STATUS_UNEXPECTED_IO_ERROR;
                    }

                    handle.RemotePath = destination;
                    handle.DirectoryEntries = null;
                    if (TryGetMetadata(destination, out AndroidFsHttpClient.NodeMetadata metadata) && metadata.Exists)
                    {
                        handle.Metadata = metadata;
                    }
                    return STATUS_SUCCESS;
                }
            }

            /// <summary>
            /// Возвращает общий security descriptor виртуального тома.
            /// </summary>
            public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
            {
                securityDescriptor = defaultSecurity_;
                return STATUS_SUCCESS;
            }

            /// <summary>
            /// Принимает Windows security update без переноса ACL в Android-хранилище.
            /// </summary>
            public override int SetSecurity(
                object fileNode,
                object fileDesc,
                AccessControlSections sections,
                byte[] securityDescriptor)
            {
                return readOnly_ ? STATUS_ACCESS_DENIED : STATUS_SUCCESS;
            }

            /// <summary>
            /// Перечисляет содержимое удалённого каталога для Проводника Windows.
            /// </summary>
            public override bool ReadDirectoryEntry(
                object fileNode,
                object fileDesc,
                string pattern,
                string marker,
                ref object context,
                out string fileName,
                out FileInfo fileInfo)
            {
                RemoteHandle handle = (RemoteHandle)fileDesc;
                fileName = null!;
                fileInfo = default;

                if (!handle.Metadata.Directory)
                {
                    return false;
                }

                if (handle.DirectoryEntries == null)
                {
                    if (!client_.ListDirectory(handle.RemotePath, out List<AndroidFsHttpClient.DirectoryEntry> entries, out string errorMessage))
                    {
                        Log.Warn("WinFsp list directory failed for " + handle.RemotePath + ": " + errorMessage);
                        handle.DirectoryEntries = new List<AndroidFsHttpClient.DirectoryEntry>();
                    }
                    else
                    {
                        entries.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
                        handle.DirectoryEntries = entries;
                    }
                }

                string normalizedPattern = string.IsNullOrEmpty(pattern)
                    ? "*"
                    : pattern.Replace('<', '*').Replace('>', '?').Replace('"', '.');
                int index = context is int storedIndex ? storedIndex : 0;
                if (context == null && !string.IsNullOrEmpty(marker))
                {
                    while (index < handle.DirectoryEntries.Count &&
                           StringComparer.OrdinalIgnoreCase.Compare(handle.DirectoryEntries[index].Name, marker) <= 0)
                    {
                        index++;
                    }
                }

                while (index < handle.DirectoryEntries.Count)
                {
                    AndroidFsHttpClient.DirectoryEntry entry = handle.DirectoryEntries[index++];
                    context = index;
                    if (!FileSystemName.MatchesSimpleExpression(normalizedPattern, entry.Name, true))
                    {
                        continue;
                    }

                    fileName = entry.Name;
                    fileInfo = BuildFileInfo(entry);
                    return true;
                }

                return false;
            }

            /// <summary>
            /// Удаляет только безопасные временные файлы, сохраняя journaled writeback cache
            /// для восстановления после сетевого сбоя или перезапуска.
            /// </summary>
            public void Dispose()
            {
                try
                {
                    if (!Directory.Exists(tempDirectory_))
                    {
                        return;
                    }

                    foreach (string pendingPath in Directory.EnumerateFiles(tempDirectory_, "*.journal.new"))
                    {
                        TryDeleteTempFile(pendingPath);
                    }
                    foreach (string tempPath in Directory.EnumerateFiles(tempDirectory_, "*.tmp"))
                    {
                        if (!File.Exists(tempPath + ".journal"))
                        {
                            TryDeleteTempFile(tempPath);
                        }
                    }
                    foreach (string journalPath in Directory.EnumerateFiles(tempDirectory_, "*.tmp.journal"))
                    {
                        string tempPath = journalPath.Substring(0, journalPath.Length - ".journal".Length);
                        if (!File.Exists(tempPath))
                        {
                            TryDeleteTempFile(journalPath);
                        }
                    }

                    if (Directory.GetFileSystemEntries(tempDirectory_).Length == 0)
                    {
                        Directory.Delete(tempDirectory_);
                    }
                }
                catch (Exception exception)
                {
                    Log.Warn("WinFsp temp cleanup failed: " + exception.Message);
                }
            }

            private bool TryGetMetadata(string remotePath, out AndroidFsHttpClient.NodeMetadata metadata)
            {
                if (client_.GetMetadata(remotePath, out metadata, out string errorMessage))
                {
                    return true;
                }

                Log.Warn("WinFsp metadata failed for " + remotePath + ": " + errorMessage);
                return false;
            }

            private bool CanWrite(RemoteHandle handle)
            {
                return !readOnly_ && handle.Metadata.Writable;
            }

            private bool EnsureLocalFile(RemoteHandle handle, bool truncate, out string errorMessage)
            {
                errorMessage = string.Empty;
                if (handle.Stream != null)
                {
                    return true;
                }

                if (handle.Metadata.Directory)
                {
                    errorMessage = "Directory cannot be cached as a file";
                    return false;
                }

                string tempPath = CreateTempPath();
                FileStream stream;
                try
                {
                    stream = new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.Read,
                        64 * 1024,
                        FileOptions.RandomAccess);
                }
                catch (Exception exception)
                {
                    errorMessage = exception.Message;
                    return false;
                }

                try
                {
                    if (!truncate && handle.Metadata.Exists && handle.Metadata.Size > 0)
                    {
                        ulong offset = 0;
                        while (offset < handle.Metadata.Size)
                        {
                            uint size = (uint)Math.Min((ulong)TransferChunkSize, handle.Metadata.Size - offset);
                            if (!client_.ReadFileRange(
                                handle.RemotePath,
                                offset,
                                size,
                                handle.Metadata.Etag,
                                out byte[] bytes,
                                out errorMessage))
                            {
                                stream.Dispose();
                                TryDeleteTempFile(tempPath);
                                return false;
                            }
                            if (bytes.Length == 0)
                            {
                                stream.Dispose();
                                TryDeleteTempFile(tempPath);
                                errorMessage = "Unexpected end of remote file";
                                return false;
                            }

                            stream.Write(bytes, 0, bytes.Length);
                            offset += (ulong)bytes.Length;
                        }
                        stream.Position = 0;
                    }

                    handle.Stream = stream;
                    handle.TempPath = tempPath;
                    return true;
                }
                catch (Exception exception)
                {
                    stream.Dispose();
                    TryDeleteTempFile(tempPath);
                    errorMessage = exception.Message;
                    return false;
                }
            }

            private bool Commit(RemoteHandle handle, out string errorMessage)
            {
                lock (handle.SyncRoot)
                {
                    errorMessage = string.Empty;
                    if (!handle.Dirty || handle.Stream == null || handle.TempPath.Length == 0)
                    {
                        return true;
                    }

                    try
                    {
                        handle.Stream.SetLength((long)Math.Min(handle.LogicalSize, (ulong)long.MaxValue));
                        handle.Stream.Flush(true);
                    }
                    catch (Exception exception)
                    {
                        errorMessage = exception.Message;
                        return false;
                    }

                    if (!WriteWritebackJournal(handle, true, out string journalError))
                    {
                        errorMessage = "Could not persist stable writeback journal: " + journalError;
                        return false;
                    }

                    if (!client_.UploadFile(
                        handle.RemotePath,
                        handle.TempPath,
                        handle.Metadata.Etag,
                        false,
                        out errorMessage))
                    {
                        return false;
                    }

                    if (!TryGetMetadata(handle.RemotePath, out AndroidFsHttpClient.NodeMetadata metadata) || !metadata.Exists)
                    {
                        errorMessage = "Uploaded file metadata could not be confirmed";
                        return false;
                    }
                    if (metadata.Size != handle.LogicalSize)
                    {
                        errorMessage =
                            "Uploaded file size mismatch: expected=" + handle.LogicalSize +
                            " actual=" + metadata.Size;
                        return false;
                    }

                    handle.Metadata = metadata;
                    handle.Dirty = false;
                    DeleteWritebackJournal(handle);
                    return true;
                }
            }

            private void DisposeLocalFile(RemoteHandle handle, bool deleteTempFile = true)
            {
                try
                {
                    handle.Stream?.Dispose();
                }
                catch
                {
                }
                handle.Stream = null;

                if (deleteTempFile)
                {
                    DeleteWritebackJournal(handle);
                    if (handle.TempPath.Length > 0)
                    {
                        TryDeleteTempFile(handle.TempPath);
                        handle.TempPath = string.Empty;
                    }
                }
            }

            private string CreateTempPath()
            {
                return Path.Combine(tempDirectory_, Guid.NewGuid().ToString("N") + ".tmp");
            }

            private void DeleteWritebackJournal(RemoteHandle handle)
            {
                string journalPath = handle.JournalPath;
                if (journalPath.Length == 0 && handle.TempPath.Length > 0)
                {
                    journalPath = handle.TempPath + ".journal";
                }

                if (journalPath.Length > 0)
                {
                    TryDeleteTempFile(journalPath + ".new");
                    TryDeleteTempFile(journalPath);
                }
                handle.JournalPath = string.Empty;
            }

            private bool WriteWritebackJournal(RemoteHandle handle, bool stable, out string errorMessage)
            {
                errorMessage = string.Empty;
                if (handle.TempPath.Length == 0)
                {
                    errorMessage = "Writeback cache path is not available";
                    return false;
                }
                if (handle.Metadata.Etag.Length == 0)
                {
                    errorMessage = "Writeback journal requires a known remote ETag";
                    return false;
                }

                string journalPath = handle.TempPath + ".journal";
                string pendingPath = journalPath + ".new";
                string content = string.Join(
                    "\n",
                    "stable=" + (stable ? "1" : "0"),
                    "size=" + handle.LogicalSize,
                    "path=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(handle.RemotePath)),
                    "etag=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(handle.Metadata.Etag)),
                    string.Empty);

                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(content);
                    using (FileStream stream = new FileStream(
                        pendingPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    File.Move(pendingPath, journalPath, true);
                    handle.JournalPath = journalPath;
                    return true;
                }
                catch (Exception exception)
                {
                    TryDeleteTempFile(pendingPath);
                    errorMessage = exception.Message;
                    return false;
                }
            }

            private static bool TryReadWritebackJournal(
                string journalPath,
                out WritebackJournal journal,
                out string errorMessage)
            {
                journal = new WritebackJournal();
                errorMessage = string.Empty;
                try
                {
                    bool hasStable = false;
                    bool hasSize = false;
                    bool hasPath = false;
                    bool hasEtag = false;
                    foreach (string line in File.ReadLines(journalPath, Encoding.UTF8))
                    {
                        int separator = line.IndexOf('=');
                        if (separator <= 0)
                        {
                            continue;
                        }

                        string key = line.Substring(0, separator);
                        string value = line.Substring(separator + 1);
                        switch (key)
                        {
                            case "stable":
                                if (value != "0" && value != "1")
                                {
                                    errorMessage = "Invalid writeback journal stable flag";
                                    return false;
                                }
                                journal.Stable = value == "1";
                                hasStable = true;
                                break;
                            case "size":
                                hasSize = ulong.TryParse(value, out ulong size);
                                if (!hasSize)
                                {
                                    errorMessage = "Invalid writeback journal size";
                                    return false;
                                }
                                journal.LogicalSize = size;
                                break;
                            case "path":
                                journal.RemotePath = Encoding.UTF8.GetString(Convert.FromBase64String(value));
                                hasPath = journal.RemotePath.Length > 0;
                                break;
                            case "etag":
                                journal.ExpectedEtag = Encoding.UTF8.GetString(Convert.FromBase64String(value));
                                hasEtag = journal.ExpectedEtag.Length > 0;
                                break;
                        }
                    }

                    if (!hasStable || !hasSize || !hasPath || !hasEtag)
                    {
                        errorMessage = "Writeback journal is incomplete";
                        return false;
                    }
                    return true;
                }
                catch (Exception exception)
                {
                    errorMessage = exception.Message;
                    return false;
                }
            }

            private void RecoverPendingWritebacks()
            {
                if (!Directory.Exists(tempDirectory_))
                {
                    return;
                }

                foreach (string journalPath in Directory.EnumerateFiles(tempDirectory_, "*.tmp.journal"))
                {
                    string tempPath = journalPath.Substring(0, journalPath.Length - ".journal".Length);
                    if (!File.Exists(tempPath))
                    {
                        Log.Warn("Removing orphan writeback journal: " + journalPath);
                        TryDeleteTempFile(journalPath);
                        continue;
                    }

                    if (!TryReadWritebackJournal(journalPath, out WritebackJournal journal, out string parseError))
                    {
                        Log.Warn("Preserving invalid writeback journal " + journalPath + ": " + parseError);
                        continue;
                    }
                    if (!journal.Stable)
                    {
                        Log.Warn("Preserving incomplete writeback cache without replay: " + tempPath);
                        continue;
                    }
                    if (journal.LogicalSize > long.MaxValue)
                    {
                        Log.Warn("Preserving oversized writeback cache without replay: " + tempPath);
                        continue;
                    }

                    System.IO.FileInfo cacheInfo = new System.IO.FileInfo(tempPath);
                    if (!cacheInfo.Exists || (ulong)cacheInfo.Length != journal.LogicalSize)
                    {
                        Log.Warn(
                            "Preserving writeback cache with unexpected size: " + tempPath +
                            " expected=" + journal.LogicalSize +
                            " actual=" + (cacheInfo.Exists ? cacheInfo.Length : -1));
                        continue;
                    }
                    if (readOnly_)
                    {
                        Log.Warn("Preserving pending writeback cache on read-only mount: " + tempPath);
                        continue;
                    }

                    if (client_.UploadFile(
                        journal.RemotePath,
                        tempPath,
                        journal.ExpectedEtag,
                        false,
                        out string uploadError))
                    {
                        Log.Info("Recovered pending WinFsp writeback: " + journal.RemotePath);
                        TryDeleteTempFile(tempPath);
                        TryDeleteTempFile(journalPath);
                    }
                    else
                    {
                        Log.Warn(
                            "Pending WinFsp writeback was not replayed for " + journal.RemotePath +
                            ": " + uploadError + ". Preserved cache: " + tempPath);
                    }
                }

                foreach (string pendingPath in Directory.EnumerateFiles(tempDirectory_, "*.journal.new"))
                {
                    TryDeleteTempFile(pendingPath);
                }
                foreach (string tempPath in Directory.EnumerateFiles(tempDirectory_, "*.tmp"))
                {
                    if (!File.Exists(tempPath + ".journal"))
                    {
                        TryDeleteTempFile(tempPath);
                    }
                }
            }

            private static void TryDeleteTempFile(string path)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                }
            }

            private static string ToRemotePath(string fileName)
            {
                if (string.IsNullOrEmpty(fileName) || fileName == "\\")
                {
                    return "/";
                }
                string normalized = fileName.Replace('\\', '/').Trim('/');
                return normalized.Length == 0 ? "/" : "/" + normalized;
            }

            private static string GetParentPath(string remotePath)
            {
                if (string.IsNullOrEmpty(remotePath) || remotePath == "/")
                {
                    return "/";
                }
                int separator = remotePath.LastIndexOf('/');
                return separator <= 0 ? "/" : remotePath.Substring(0, separator);
            }

            private static uint BuildFileAttributes(AndroidFsHttpClient.NodeMetadata metadata)
            {
                uint attributes = metadata.Directory
                    ? (uint)System.IO.FileAttributes.Directory
                    : (uint)System.IO.FileAttributes.Archive;
                if (!metadata.Writable)
                {
                    attributes |= (uint)System.IO.FileAttributes.ReadOnly;
                }
                return attributes;
            }

            private static FileInfo BuildFileInfo(AndroidFsHttpClient.NodeMetadata metadata)
            {
                FileInfo fileInfo = default;
                fileInfo.FileAttributes = BuildFileAttributes(metadata);
                fileInfo.FileSize = metadata.Directory ? 0 : metadata.Size;
                fileInfo.AllocationSize = AlignAllocation(fileInfo.FileSize);
                ulong fileTime = UnixMillisecondsToFileTime(metadata.LastModified);
                fileInfo.CreationTime = fileTime;
                fileInfo.LastAccessTime = fileTime;
                fileInfo.LastWriteTime = fileTime;
                fileInfo.ChangeTime = fileTime;
                return fileInfo;
            }

            private static FileInfo BuildFileInfo(AndroidFsHttpClient.DirectoryEntry entry)
            {
                AndroidFsHttpClient.NodeMetadata metadata = new AndroidFsHttpClient.NodeMetadata
                {
                    Exists = true,
                    Directory = entry.Directory,
                    Size = entry.Size,
                    LastModified = entry.LastModified,
                    Writable = entry.Writable,
                    Name = entry.Name
                };
                return BuildFileInfo(metadata);
            }

            private static FileInfo BuildFileInfo(RemoteHandle handle, ulong size)
            {
                AndroidFsHttpClient.NodeMetadata metadata = new AndroidFsHttpClient.NodeMetadata
                {
                    Exists = true,
                    Directory = false,
                    Size = size,
                    LastModified = handle.Metadata.LastModified,
                    Writable = handle.Metadata.Writable,
                    Name = handle.Metadata.Name,
                    Etag = handle.Metadata.Etag
                };
                return BuildFileInfo(metadata);
            }

            private static ulong AlignAllocation(ulong size)
            {
                return (size + AllocationUnit - 1) / AllocationUnit * AllocationUnit;
            }

            private static ulong UnixMillisecondsToFileTime(ulong milliseconds)
            {
                if (milliseconds == 0 || milliseconds > (ulong)long.MaxValue)
                {
                    return (ulong)DateTime.UtcNow.ToFileTimeUtc();
                }

                try
                {
                    return (ulong)DateTimeOffset
                        .FromUnixTimeMilliseconds((long)milliseconds)
                        .UtcDateTime
                        .ToFileTimeUtc();
                }
                catch (ArgumentOutOfRangeException)
                {
                    return (ulong)DateTime.UtcNow.ToFileTimeUtc();
                }
            }
        }

        private static readonly object mutex_ = new object();
        private static readonly Dictionary<string, MountedDrive> mountedDrives_ =
            new Dictionary<string, MountedDrive>(StringComparer.Ordinal);

        /// <summary>
        /// Читает полную файловую версию установленного x64 runtime WinFsp из каталога,
        /// зарегистрированного официальным MSI.
        /// </summary>
        private static Version? GetInstalledRuntimeVersion()
        {
            string? installDirectory = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\WinFsp",
                "InstallDir",
                null) as string;
            if (string.IsNullOrWhiteSpace(installDirectory))
            {
                return null;
            }

            string dllPath = Path.Combine(installDirectory, "bin", "winfsp-x64.dll");
            if (!File.Exists(dllPath))
            {
                return null;
            }

            System.Diagnostics.FileVersionInfo versionInfo =
                System.Diagnostics.FileVersionInfo.GetVersionInfo(dllPath);
            return new Version(
                versionInfo.FileMajorPart,
                versionInfo.FileMinorPart,
                versionInfo.FileBuildPart);
        }

        /// <summary>
        /// Пытается смонтировать Android endpoint через WinFsp в первую свободную букву от Z до D.
        /// Возвращает букву диска или null с диагностикой ошибки.
        /// </summary>
        public static string? Mount(AndroidClient client, out string errorMessage)
        {
            errorMessage = string.Empty;
            if (string.IsNullOrEmpty(client.WebDavHost) || client.WebDavPort <= 0)
            {
                errorMessage = "Android file endpoint is incomplete";
                return null;
            }

            lock (mutex_)
            {
                if (mountedDrives_.TryGetValue(client.ClientId, out MountedDrive? existing))
                {
                    return existing.DriveLetter;
                }

                string? mountPoint = FindFreeDriveLetter();
                if (mountPoint == null)
                {
                    errorMessage = "No free drive letters are available";
                    return null;
                }

                AndroidRemoteFileSystem? fileSystem = null;
                FileSystemHost? host = null;
                try
                {
                    Version minimumVersion = new Version(2, 2, 26215);
                    Version? installedVersion = GetInstalledRuntimeVersion();
                    if (installedVersion == null)
                    {
                        errorMessage = "WinFsp 2.2.26215 runtime is not installed";
                        return null;
                    }
                    if (installedVersion.CompareTo(minimumVersion) < 0)
                    {
                        errorMessage = "WinFsp 2.2.26215 or newer is required. Installed version: " + installedVersion;
                        return null;
                    }

                    Version apiVersion = FileSystemHost.Version();
                    Log.Info("WinFsp runtime detected: file=" + installedVersion + ", API=" + apiVersion);

                    fileSystem = new AndroidRemoteFileSystem(client);
                    host = new FileSystemHost(fileSystem);
                    int result = host.Mount(mountPoint, null, true, 0);
                    if (result < 0)
                    {
                        errorMessage = "WinFsp mount failed with NTSTATUS 0x" +
                            unchecked((uint)result).ToString("X8");
                        host.Dispose();
                        fileSystem.Dispose();
                        return null;
                    }

                    string actualMountPoint = host.MountPoint() ?? mountPoint;
                    string driveLetter = actualMountPoint.TrimEnd(':', '\\');
                    if (driveLetter.Length != 1)
                    {
                        errorMessage = "WinFsp returned an unexpected mount point: " + actualMountPoint;
                        host.Dispose();
                        fileSystem.Dispose();
                        return null;
                    }

                    mountedDrives_[client.ClientId] = new MountedDrive(fileSystem, host, driveLetter);
                    Log.Info("WinFsp drive mounted: " + driveLetter + ": for client " + client.ClientId);
                    return driveLetter;
                }
                catch (DllNotFoundException exception)
                {
                    host?.Dispose();
                    fileSystem?.Dispose();
                    errorMessage = "WinFsp runtime is not installed: " + exception.Message;
                    return null;
                }
                catch (BadImageFormatException exception)
                {
                    host?.Dispose();
                    fileSystem?.Dispose();
                    errorMessage = "WinFsp runtime architecture is incompatible: " + exception.Message;
                    return null;
                }
                catch (Exception exception)
                {
                    host?.Dispose();
                    fileSystem?.Dispose();
                    Log.Error("WinFsp mount failed: " + exception);
                    errorMessage = "WinFsp mount failed: " + exception.GetBaseException().Message;
                    return null;
                }
            }
        }

        /// <summary>
        /// Размонтирует WinFsp-диск клиента и освобождает его временный writeback cache.
        /// </summary>
        public static void Unmount(AndroidClient client)
        {
            MountedDrive? mountedDrive;
            lock (mutex_)
            {
                if (!mountedDrives_.TryGetValue(client.ClientId, out mountedDrive))
                {
                    return;
                }
                mountedDrives_.Remove(client.ClientId);
            }

            try
            {
                mountedDrive.Host.Unmount();
            }
            catch (Exception exception)
            {
                Log.Warn("WinFsp unmount failed for client " + client.ClientId + ": " + exception.Message);
            }
            finally
            {
                mountedDrive.Host.Dispose();
                mountedDrive.FileSystem.Dispose();
            }
        }

        private static string? FindFreeDriveLetter()
        {
            uint logicalDrives = GetLogicalDrives();
            for (char letter = 'Z'; letter >= 'D'; --letter)
            {
                uint bit = 1u << (letter - 'A');
                if ((logicalDrives & bit) == 0)
                {
                    return letter + ":";
                }
            }
            return null;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetLogicalDrives();
    }
}
